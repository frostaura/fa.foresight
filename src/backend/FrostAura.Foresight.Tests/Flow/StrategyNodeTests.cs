using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Strategy-as-DAG: the strategy primitive nodes wrap the same Domain.Trading math as the built-in
/// strategies, so a DAG strategy and the built-in agree. Plus the canonical strategy chain
/// edge_aware_kelly → clamp_round → gate → output.stake.
/// </summary>
public class StrategyNodeTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly JsonElement Empty = P("{}");

    private static FlowContext Ctx() => new(
        Guid.Empty, Guid.Empty, "BTCUSDT", "5m", 0, 1, FlowMode.Live, new StubCandles(), null);

    private static async Task<decimal> StakeAsync(IFlowNode node, Dictionary<string, object?> inputs, JsonElement p)
    {
        var outputs = await node.ExecuteAsync(inputs, p, Ctx(), default);
        return (decimal)outputs["stake"]!;
    }

    [Fact]
    public async Task EdgeAwareKelly_node_matches_the_builtin_strategy()
    {
        var inputs = new Dictionary<string, object?>
        {
            ["pUp"] = 0.60m, ["yesPrice"] = 0.55m, ["noPrice"] = 0.45m, ["balance"] = 1000m, ["initialBet"] = 2m,
        };
        var nodeStake = await StakeAsync(new EdgeAwareKellyNode(), inputs, Empty);

        var builtin = new EdgeAwareKellyStakingStrategy().NextBetSize(
            new StrategyStep(2m, true, 2m, 1000m, new StakingInputs(0.60m, 0.55m, 0.45m)));

        nodeStake.Should().Be(builtin);
        nodeStake.Should().BeApproximately(27.78m, 0.01m);
    }

    [Fact]
    public async Task Canonical_strategy_chain_clear_edge_yields_whole_dollar_stake()
    {
        var stake = await RunChainAsync(pUp: 0.60m, yes: 0.55m, no: 0.45m, balance: 1000m);
        stake.Should().Be(28m); // round(27.78) = 28, gate passes (|0.6-0.5|*2 = 0.20 ≥ 0.04)
    }

    [Fact]
    public async Task Canonical_strategy_chain_no_edge_yields_zero()
    {
        // Model below the market price ⇒ negative edge ⇒ 0 all the way through.
        var stake = await RunChainAsync(pUp: 0.52m, yes: 0.55m, no: 0.45m, balance: 1000m);
        stake.Should().Be(0m);
    }

    [Fact]
    public async Task Canonical_strategy_chain_gate_skip_yields_zero()
    {
        // Tiny edge but pUp inside the ±2pp gate band ⇒ gate zeroes the stake.
        var stake = await RunChainAsync(pUp: 0.505m, yes: 0.50m, no: 0.50m, balance: 1000m);
        stake.Should().Be(0m);
    }

    private static async Task<decimal> RunChainAsync(decimal pUp, decimal yes, decimal no, decimal balance)
    {
        var edge = await StakeAsync(new EdgeAwareKellyNode(), new Dictionary<string, object?>
        {
            ["pUp"] = pUp, ["yesPrice"] = yes, ["noPrice"] = no, ["balance"] = balance, ["initialBet"] = 2m,
        }, Empty);
        var clamped = await StakeAsync(new ClampRoundNode(),
            new Dictionary<string, object?> { ["stake"] = edge }, Empty);
        var gated = await StakeAsync(new GateNode(),
            new Dictionary<string, object?> { ["stake"] = clamped, ["pUp"] = pUp }, Empty);
        // output.stake terminal just surfaces the value.
        var terminal = await new OutputStakeNode().ExecuteAsync(
            new Dictionary<string, object?> { ["stake"] = gated }, Empty, Ctx(), default);
        return (decimal)terminal["stake"]!;
    }

    [Fact]
    public async Task Flat_node_emits_initial_bet()
        => (await StakeAsync(new FlatStrategyNode(), new() { ["initialBet"] = 5m }, Empty)).Should().Be(5m);

    [Fact]
    public async Task Martingale_step_doubles_on_loss_resets_on_win()
    {
        (await StakeAsync(new MartingaleStepNode(),
            new() { ["currentBet"] = 4m, ["lastOutcome"] = false, ["initialBet"] = 2m }, Empty)).Should().Be(8m);
        (await StakeAsync(new MartingaleStepNode(),
            new() { ["currentBet"] = 16m, ["lastOutcome"] = true, ["initialBet"] = 2m }, Empty)).Should().Be(2m);
    }

    [Fact]
    public async Task Kelly_node_is_two_and_a_half_percent()
        => (await StakeAsync(new KellyStrategyNode(), new() { ["balance"] = 1000m, ["initialBet"] = 2m }, Empty))
            .Should().Be(25.00m);

    [Theory]
    [InlineData(2.6, 3)]
    [InlineData(2.4, 2)]
    [InlineData(0.4, 0)] // below $1 ⇒ skip
    public async Task ClampRound_rounds_to_whole_dollars_and_skips_sub_minimum(double raw, double expected)
        => (await StakeAsync(new ClampRoundNode(), new() { ["stake"] = (decimal)raw }, Empty))
            .Should().Be((decimal)expected);

    [Fact]
    public async Task Gate_passes_confident_signals_and_zeroes_coinflips()
    {
        (await StakeAsync(new GateNode(), new() { ["stake"] = 10m, ["pUp"] = 0.60m }, Empty)).Should().Be(10m);
        (await StakeAsync(new GateNode(), new() { ["stake"] = 10m, ["pUp"] = 0.51m }, Empty)).Should().Be(0m);
    }

    private sealed class StubCandles : IHistoricalCandleProvider
    {
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
            long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
    }
}
