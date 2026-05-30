using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Indicator pack nodes turn a candle window into the feature dictionary the models consume. They are
/// pure transforms of the candle list — given a deterministic synthetic window they must produce a
/// non-empty feature set without throwing, across the full warmed-up window.
/// </summary>
public class IndicatorPackNodeTests
{
    // 120 deterministic candles on a gentle uptrend with intrabar range — enough to clear every
    // pack's warmup (longest is the 20-bar lookbacks).
    private static IReadOnlyList<HistoricalCandle> Window(int n = 120)
    {
        var list = new List<HistoricalCandle>(n);
        for (var i = 0; i < n; i++)
        {
            var baseP = 100m + i * 0.5m + (i % 5) * 0.2m; // drift + small oscillation
            list.Add(new HistoricalCandle
            {
                Symbol = "BTCUSDT",
                Interval = "5m",
                OpenTime = 1_700_000_000_000L + i * 300_000L,
                Open = baseP,
                High = baseP + 1.5m,
                Low = baseP - 1.2m,
                Close = baseP + 0.3m,
                Volume = 10m + i % 7,
            });
        }
        return list;
    }

    private static FlowContext Ctx() => new(
        Guid.Empty, Guid.Empty, "BTCUSDT", "5m", 0, 1, FlowMode.Backtest, new StubCandles(), null);

    public static IEnumerable<object[]> Packs() => new[]
    {
        new object[] { new TechPackNode() },
        new object[] { new FeaturePackNode() },
        new object[] { new CrossPackNode() },
        new object[] { new MomentumPackNode() },
        new object[] { new NormPackNode() },
        new object[] { new VolumePackNode() },
        new object[] { new HtfRegimePackNode() },
        new object[] { new SubBarPackNode() },
    };

    [Theory]
    [MemberData(nameof(Packs))]
    public async Task Pack_node_emits_features_for_a_warmed_window(IFlowNode node)
    {
        var inputs = new Dictionary<string, object?> { ["candles"] = Window() };
        var outputs = await node.ExecuteAsync(inputs, JsonDocument.Parse("{}").RootElement, Ctx(), default);

        outputs.Should().NotBeNull();
        outputs.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public async Task Pack_node_tolerates_an_empty_window(IFlowNode node)
    {
        // No candles bound → warmup misses everywhere; the node must return (typically nulls) and
        // never throw, because the matrix builder downstream marks itself not-ready off that.
        var outputs = await node.ExecuteAsync(
            new Dictionary<string, object?>(), JsonDocument.Parse("{}").RootElement, Ctx(), default);

        outputs.Should().NotBeNull();
    }

    private sealed class StubCandles : IHistoricalCandleProvider
    {
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
            long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
    }
}
