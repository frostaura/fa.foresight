using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Covers the additive GBT serving contract: seed-bag mean prediction (modelGbtBag), isotonic
/// calibration (calibration), the confidence gate (confidenceGate), and the OOD guard (oodGuard).
/// All four fields are optional — absent fields must reproduce exact legacy behavior — and every
/// abstention surfaces through the single canonical signal pUp = 0.5.
/// </summary>
public class GbtBaggingCalibrationGatingTests
{
    private static FeatureMatrix Matrix(params double[] row)
    {
        var m = new double[1, row.Length];
        for (var i = 0; i < row.Length; i++) m[0, i] = row[i];
        return new FeatureMatrix(Enumerable.Range(0, row.Length).Select(i => $"f{i}").ToArray(), m);
    }

    private static FlowContext Ctx(string? trainedJson = null)
        => new(Guid.Empty, Guid.Empty, "BTCUSDT", "5m", 0, 1, FlowMode.Live, new StubCandles(),
               trainedJson is null ? null : JsonDocument.Parse(trainedJson).RootElement);

    private static async Task<decimal?> PUp(string trained, params double[] row)
    {
        var outputs = await new GradientBoostedTreesNode().ExecuteAsync(
            new Dictionary<string, object?> { ["matrix"] = Matrix(row) },
            JsonDocument.Parse("{}").RootElement, Ctx(trained), default);
        return (decimal?)outputs["pUp"];
    }

    // One tree splitting on f0 < 0.5 with leaf logits ±leafLogit (baseScore 0, lr 1).
    private static string Tree(double leafLogit)
        => """{"baseScore":0.0,"learningRate":1.0,"featureCount":1,"trees":[{"f":0,"t":0.5,"l":{"v":@LEAF@},"r":{"v":@NEG@}}]}"""
            .Replace("@LEAF@", leafLogit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@NEG@", (-leafLogit).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));

    // ---- Isotonic calibration (PAVA) — pure function ----------------------------------------

    [Fact]
    public void Pava_perfectly_ordered_labels_produce_increasing_breakpoints()
    {
        var preds = new[] { 0.1, 0.2, 0.3, 0.7, 0.8, 0.9 };
        var labels = new[] { 0, 0, 0, 1, 1, 1 };
        var (x, y) = IsotonicCalibration.Fit(preds, labels);

        y.Should().BeInAscendingOrder();
        IsotonicCalibration.Apply(x, y, 0.1).Should().Be(0.0);
        IsotonicCalibration.Apply(x, y, 0.9).Should().Be(1.0);
    }

    [Fact]
    public void Pava_pools_violators_into_block_means()
    {
        // Labels 1,0 against ascending preds violate monotonicity → pooled to mean 0.5.
        var preds = new[] { 0.4, 0.6 };
        var labels = new[] { 1, 0 };
        var (x, y) = IsotonicCalibration.Fit(preds, labels);

        y.Should().OnlyContain(v => v == 0.5);
        IsotonicCalibration.Apply(x, y, 0.5).Should().Be(0.5);
    }

    [Fact]
    public void Pava_apply_clamps_outside_fitted_range_and_interpolates_inside()
    {
        var x = new[] { 0.2, 0.8 };
        var y = new[] { 0.3, 0.7 };

        IsotonicCalibration.Apply(x, y, 0.0).Should().Be(0.3);   // clamp low
        IsotonicCalibration.Apply(x, y, 1.0).Should().Be(0.7);   // clamp high
        IsotonicCalibration.Apply(x, y, 0.5).Should().BeApproximately(0.5, 1e-9); // midpoint
    }

    [Fact]
    public void Pava_empty_input_passes_probability_through()
    {
        IsotonicCalibration.Apply(Array.Empty<double>(), Array.Empty<double>(), 0.42).Should().Be(0.42);
    }

    [Fact]
    public void Pava_fit_on_noisy_data_is_monotone_bounded_and_apply_preserves_order()
    {
        // 500 noisy-but-correlated points: PAVA must pool violators into a non-decreasing map with
        // strictly increasing breakpoints and probabilities in [0,1]; the interpolated Apply must
        // itself be monotone across the whole [0,1] domain.
        var rng = new Random(42);
        var preds = Enumerable.Range(0, 500).Select(_ => rng.NextDouble()).ToArray();
        var labels = preds.Select(p => rng.NextDouble() < p ? 1 : 0).ToArray();

        var (x, y) = IsotonicCalibration.Fit(preds, labels);

        x.Length.Should().Be(y.Length);
        x.Should().BeInAscendingOrder();
        x.Should().OnlyHaveUniqueItems("breakpoint x values must be strictly increasing");
        y.Should().BeInAscendingOrder();
        y.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);

        var applied = Enumerable.Range(0, 101).Select(i => IsotonicCalibration.Apply(x, y, i / 100.0)).ToArray();
        applied.Should().BeInAscendingOrder("a monotone fit interpolated linearly stays monotone");
    }

    // ---- Bagged serving ----------------------------------------------------------------------

    [Fact]
    public async Task Bag_prediction_is_the_mean_over_members()
    {
        // Two members with logits +4 and +2 on the same row → mean(sigmoid(4), sigmoid(2)).
        var trained = """{"modelGbtBag":{"seeds":[101,102],"models":[""" + Tree(4.0) + "," + Tree(2.0) + "]}}";
        var expected = (Sigmoid(4.0) + Sigmoid(2.0)) / 2.0;

        (await PUp(trained, 0.0)).Should().BeApproximately((decimal)expected, 0.0001m);
    }

    [Fact]
    public async Task Bag_takes_precedence_over_legacy_single_model()
    {
        // Legacy model says logit -4 (down); the bag says +4 (up). Bag wins.
        var trained = """{"model.gbt":""" + Tree(-4.0)
            + ""","modelGbtBag":{"seeds":[1],"models":[""" + Tree(4.0) + "]}}";

        (await PUp(trained, 0.0)).Should().BeGreaterThan(0.9m);
    }

    [Fact]
    public async Task Legacy_single_model_without_optional_fields_is_unchanged()
    {
        var trained = """{"model.gbt":""" + Tree(4.0) + "}";
        var expected = Sigmoid(4.0);

        (await PUp(trained, 0.0)).Should().BeApproximately((decimal)expected, 0.0001m);
    }

    // ---- Calibration at serving time -----------------------------------------------------------

    [Fact]
    public async Task Calibration_remaps_raw_probability_via_interpolation()
    {
        // Raw p = sigmoid(4) ≈ 0.982 → calibration maps everything ≥ 0.9 to 0.6.
        var trained = """{"model.gbt":""" + Tree(4.0)
            + ""","calibration":{"type":"isotonic","x":[0.1,0.9],"y":[0.45,0.6]}}""";

        (await PUp(trained, 0.0)).Should().BeApproximately(0.6m, 0.0001m);
    }

    // ---- Confidence gate ------------------------------------------------------------------------

    [Fact]
    public async Task Confidence_gate_abstains_with_pUp_half_when_below_threshold()
    {
        // Raw p = sigmoid(0.2) ≈ 0.55 → |p - 0.5| ≈ 0.05 < 0.2 → abstain at exactly 0.5.
        var trained = """{"model.gbt":""" + Tree(0.2)
            + ""","confidenceGate":{"coverage":0.05,"threshold":0.2}}""";

        (await PUp(trained, 0.0)).Should().Be(0.5m);
    }

    [Fact]
    public async Task Confidence_gate_passes_predictions_above_threshold()
    {
        var trained = """{"model.gbt":""" + Tree(4.0)
            + ""","confidenceGate":{"coverage":0.05,"threshold":0.2}}""";

        (await PUp(trained, 0.0)).Should().BeGreaterThan(0.9m);
    }

    // ---- OOD guard ------------------------------------------------------------------------------

    [Fact]
    public async Task Ood_guard_vetoes_with_pUp_half_when_enough_features_exceed_zMax()
    {
        // 3 features all 100 sigmas out (mean 0, std 1, zMax 8, minHits 3) → veto.
        var trained = """
        {"model.gbt":{"baseScore":4.0,"learningRate":1.0,"featureCount":3,"trees":[{"v":0.0}]},
         "oodGuard":{"means":[0.0,0.0,0.0],"stds":[1.0,1.0,1.0],"zMax":8.0,"minHits":3}}
        """;

        (await PUp(trained, 100.0, 100.0, 100.0)).Should().Be(0.5m);
    }

    [Fact]
    public async Task Ood_guard_passes_when_hits_below_minHits()
    {
        // Only 2 of 3 features beyond zMax → below minHits → prediction flows through.
        var trained = """
        {"model.gbt":{"baseScore":4.0,"learningRate":1.0,"featureCount":3,"trees":[{"v":0.0}]},
         "oodGuard":{"means":[0.0,0.0,0.0],"stds":[1.0,1.0,1.0],"zMax":8.0,"minHits":3}}
        """;

        (await PUp(trained, 100.0, 100.0, 0.0)).Should().BeGreaterThan(0.9m);
    }

    private sealed class StubCandles : IHistoricalCandleProvider
    {
        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
            long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
    }
}
