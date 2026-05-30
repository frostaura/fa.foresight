using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Inference-time model nodes are pure functions of (matrix, trained-state, params). These cover the
/// prediction engines the trader actually runs — linear/logistic regression, GBT, and the majority
/// vote aggregator — including the trained, untrained, and not-ready paths.
/// </summary>
public class RegressionNodeTests
{
    private static FeatureMatrix Matrix(params double[] row)
        => new(Enumerable.Range(0, row.Length).Select(i => $"f{i}").ToArray(),
               To2D(row));

    private static double[,] To2D(double[] row)
    {
        var m = new double[1, row.Length];
        for (var i = 0; i < row.Length; i++) m[0, i] = row[i];
        return m;
    }

    private static FlowContext Ctx(string? trainedJson = null)
        => new(Guid.Empty, Guid.Empty, "BTCUSDT", "5m", 0, 1, FlowMode.Live, new StubCandles(),
               trainedJson is null ? null : JsonDocument.Parse(trainedJson).RootElement);

    private static async Task<IReadOnlyDictionary<string, object?>> Run(
        IFlowNode node, Dictionary<string, object?> inputs, FlowContext ctx)
        => await node.ExecuteAsync(inputs, JsonDocument.Parse("{}").RootElement, ctx, default);

    // ---- LinearRegressionNode --------------------------------------------------------------

    [Fact]
    public async Task LinearRegression_predicts_intercept_plus_weighted_features()
    {
        // predicted = 10 + 2*3 + (-1)*4 = 12. anchor 11 < 12 → up.
        var trained = """{"model.linear_regression":{"intercept":10.0,"weights":[2.0,-1.0]}}""";
        var outputs = await Run(new LinearRegressionNode(),
            new() { ["matrix"] = Matrix(3.0, 4.0), ["anchor"] = 11m }, Ctx(trained));

        ((decimal?)outputs["predicted"]).Should().Be(12m);
        ((decimal?)outputs["pUp"]).Should().Be(1m);
        ((decimal)outputs["confidence"]!).Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task LinearRegression_emits_down_when_anchor_above_prediction()
    {
        var trained = """{"model.linear_regression":{"intercept":0.0,"weights":[1.0]}}""";
        var outputs = await Run(new LinearRegressionNode(),
            new() { ["matrix"] = Matrix(5.0), ["anchor"] = 100m }, Ctx(trained));

        ((decimal?)outputs["predicted"]).Should().Be(5m);
        ((decimal?)outputs["pUp"]).Should().Be(0m);
    }

    [Fact]
    public async Task LinearRegression_without_anchor_emits_no_direction()
    {
        var trained = """{"model.linear_regression":{"intercept":0.0,"weights":[1.0]}}""";
        var outputs = await Run(new LinearRegressionNode(),
            new() { ["matrix"] = Matrix(5.0) }, Ctx(trained));

        ((decimal?)outputs["pUp"]).Should().BeNull();
        ((decimal)outputs["confidence"]!).Should().Be(0.5m);
    }

    [Fact]
    public async Task LinearRegression_without_trained_state_returns_null_outputs()
    {
        var outputs = await Run(new LinearRegressionNode(),
            new() { ["matrix"] = Matrix(1.0), ["anchor"] = 1m }, Ctx());

        ((decimal?)outputs["predicted"]).Should().BeNull();
        ((decimal?)outputs["pUp"]).Should().BeNull();
    }

    [Fact]
    public async Task LinearRegression_when_not_ready_returns_null_outputs()
    {
        var trained = """{"model.linear_regression":{"intercept":0.0,"weights":[1.0]}}""";
        var outputs = await Run(new LinearRegressionNode(),
            new() { ["matrix"] = Matrix(1.0), ["ready"] = false }, Ctx(trained));

        ((decimal?)outputs["predicted"]).Should().BeNull();
    }

    // ---- LogisticRegressionNode ------------------------------------------------------------

    [Fact]
    public async Task Logistic_applies_sigmoid_over_linear_combination()
    {
        // z = 0 → sigmoid = 0.5 → confidence 0.
        var trained = """{"model.logistic_regression":{"intercept":0.0,"weights":[0.0]}}""";
        var outputs = await Run(new LogisticRegressionNode(),
            new() { ["matrix"] = Matrix(1.0) }, Ctx(trained));

        ((decimal?)outputs["pUp"]).Should().BeApproximately(0.5m, 0.0001m);
        ((decimal)outputs["confidence"]!).Should().BeApproximately(0m, 0.0001m);
    }

    [Fact]
    public async Task Logistic_strong_positive_logit_yields_high_pUp()
    {
        // z = 4 → sigmoid ≈ 0.982.
        var trained = """{"model.logistic_regression":{"intercept":4.0,"weights":[0.0]}}""";
        var outputs = await Run(new LogisticRegressionNode(),
            new() { ["matrix"] = Matrix(1.0) }, Ctx(trained));

        ((decimal?)outputs["pUp"]).Should().BeGreaterThan(0.9m);
        ((decimal)outputs["confidence"]!).Should().BeGreaterThan(0.8m);
    }

    [Fact]
    public async Task Logistic_without_trained_state_returns_null()
    {
        var outputs = await Run(new LogisticRegressionNode(),
            new() { ["matrix"] = Matrix(1.0) }, Ctx());

        ((decimal?)outputs["pUp"]).Should().BeNull();
        ((decimal)outputs["confidence"]!).Should().Be(0.5m);
    }

    // ---- GradientBoostedTreesNode ----------------------------------------------------------

    [Fact]
    public async Task Gbt_routes_row_through_single_split_tree()
    {
        // One tree splitting on f0 < 0.5: left leaf +4, right leaf -4. baseScore 0, lr 1.
        // row f0 = 0.0 → left (+4) → logit 4 → sigmoid ≈ 0.982.
        var trained = """
        {"model.gbt":{"baseScore":0.0,"learningRate":1.0,"featureCount":1,
          "trees":[{"f":0,"t":0.5,"l":{"v":4.0},"r":{"v":-4.0}}]}}
        """;
        var outputs = await Run(new GradientBoostedTreesNode(),
            new() { ["matrix"] = Matrix(0.0) }, Ctx(trained));

        ((decimal?)outputs["pUp"]).Should().BeGreaterThan(0.9m);
    }

    [Fact]
    public async Task Gbt_without_trained_state_returns_null()
    {
        var outputs = await Run(new GradientBoostedTreesNode(),
            new() { ["matrix"] = Matrix(0.0) }, Ctx());

        ((decimal?)outputs["pUp"]).Should().BeNull();
    }

    // ---- MajorityVoteNode ------------------------------------------------------------------

    [Fact]
    public async Task MajorityVote_sides_with_the_up_majority()
    {
        var node = new MajorityVoteNode();
        var p = JsonDocument.Parse("""{"signalPorts":["a","b","c"]}""").RootElement;
        var inputs = new Dictionary<string, object?>
        {
            ["a"] = 0.7m,
            ["a_confidence"] = 0.6m,
            ["b"] = 0.6m,
            ["b_confidence"] = 0.8m,
            ["c"] = 0.3m,
            ["c_confidence"] = 0.5m,
        };
        var outputs = await node.ExecuteAsync(inputs, p, Ctx(), default);

        ((decimal?)outputs["pUp"]).Should().BeGreaterThan(0.5m);
        ((decimal)outputs["confidence"]!).Should().BeApproximately(0.7m, 0.0001m); // mean of up-side confs
    }

    [Fact]
    public async Task MajorityVote_sides_with_the_down_majority()
    {
        var node = new MajorityVoteNode();
        var p = JsonDocument.Parse("""{"signalPorts":["a","b"]}""").RootElement;
        var inputs = new Dictionary<string, object?> { ["a"] = 0.2m, ["b"] = 0.4m };
        var outputs = await node.ExecuteAsync(inputs, p, Ctx(), default);

        ((decimal?)outputs["pUp"]).Should().BeLessThan(0.5m);
    }

    [Fact]
    public async Task MajorityVote_with_no_signals_emits_null()
    {
        var node = new MajorityVoteNode();
        var p = JsonDocument.Parse("""{"signalPorts":["a"]}""").RootElement;
        var outputs = await node.ExecuteAsync(new Dictionary<string, object?>(), p, Ctx(), default);

        ((decimal?)outputs["pUp"]).Should().BeNull();
        ((decimal)outputs["confidence"]!).Should().Be(0.5m);
    }

    private sealed class StubCandles : IHistoricalCandleProvider
    {
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
            long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
    }
}
