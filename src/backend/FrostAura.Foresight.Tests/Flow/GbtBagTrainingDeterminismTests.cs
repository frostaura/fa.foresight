using System.Text;
using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// Trainer-side coverage for the seed-bagged GBT contract (2026-06-10 campaign): training with the
/// same seed set must be fully deterministic (identical TrainedState minus the timestamp), and a
/// bagged + gated flow must emit all four additive contract fields — modelGbtBag, calibration,
/// confidenceGate, oodGuard — in the exact shapes the serving node consumes.
/// </summary>
public sealed class GbtBagTrainingDeterminismTests
{
    private const string Symbol = "BTCUSDT";

    [Fact]
    public async Task Same_seed_set_produces_identical_trained_state_minus_timestamp()
    {
        var (executor, flow) = BuildHarness(bags: 3, seed: 101, coverage: 0.05);
        var provider = SyntheticProvider(seed: 21, fiveMCount: 800);
        var (start, end) = Window(provider);

        var trainer = new ModelTrainer(executor, provider);
        var first = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);
        var second = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);

        // Everything except the wall-clock trainedAt stamp must match byte-for-byte: the bag fit is
        // seeded (seed..seed+B-1), PAVA/quantile/OOD stats are pure, and the replayed rows are
        // deterministic. Any drift here would break backtest == chaos == live reproducibility.
        Canonical(first.TrainedStateJson).Should().Be(Canonical(second.TrainedStateJson));
        first.ValidationAccuracy.Should().Be(second.ValidationAccuracy);
    }

    [Fact]
    public async Task Bagged_gated_training_emits_all_four_contract_fields()
    {
        var (executor, flow) = BuildHarness(bags: 3, seed: 101, coverage: 0.05);
        var provider = SyntheticProvider(seed: 22, fiveMCount: 800);
        var (start, end) = Window(provider);

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);

        using var doc = JsonDocument.Parse(trained.TrainedStateJson);
        var root = doc.RootElement;
        root.GetProperty("engine").GetString().Should().Be("gbt");
        var featureCount = root.GetProperty("featureNames").GetArrayLength();

        // modelGbtBag: B models with consecutive seeds starting at the node's seed param.
        var bag = root.GetProperty("modelGbtBag");
        bag.GetProperty("seeds").EnumerateArray().Select(s => s.GetInt32())
            .Should().Equal(101, 102, 103);
        bag.GetProperty("models").GetArrayLength().Should().Be(3);
        // Legacy single-model field stays the FIRST bag member for backward compatibility.
        root.GetProperty("modelGbt").GetRawText()
            .Should().Be(bag.GetProperty("models")[0].GetRawText());

        // calibration: PAVA breakpoints — strictly increasing x, non-decreasing y in [0,1].
        var cal = root.GetProperty("calibration");
        cal.GetProperty("type").GetString().Should().Be("isotonic");
        var x = cal.GetProperty("x").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var y = cal.GetProperty("y").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        x.Length.Should().Be(y.Length);
        x.Length.Should().BeGreaterThan(0, "the walk-forward OOF set is non-empty so calibration must fit");
        x.Should().BeInAscendingOrder();
        x.Should().OnlyHaveUniqueItems("breakpoint x values must be strictly increasing");
        y.Should().BeInAscendingOrder();
        y.Should().OnlyContain(v => v >= 0.0 && v <= 1.0);

        // confidenceGate: coverage echoed from the node param, threshold a |pCal-0.5| quantile.
        var gate = root.GetProperty("confidenceGate");
        gate.GetProperty("coverage").GetDouble().Should().Be(0.05);
        gate.GetProperty("threshold").GetDouble().Should().BeInRange(0.0, 0.5);

        // oodGuard: per-feature stats sized to the matrix, with the contract constants.
        var ood = root.GetProperty("oodGuard");
        ood.GetProperty("means").GetArrayLength().Should().Be(featureCount);
        ood.GetProperty("stds").GetArrayLength().Should().Be(featureCount);
        ood.GetProperty("zMax").GetDouble().Should().Be(8.0);
        ood.GetProperty("minHits").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Legacy_flow_without_bagging_params_leaves_bag_and_gate_absent()
    {
        // bags omitted (default 1) and coverage omitted (default 0 = gate disabled): the ensemble
        // and gate fields must be null so the serving node takes the legacy single-model path.
        var (executor, flow) = BuildHarness(bags: null, seed: null, coverage: null);
        var provider = SyntheticProvider(seed: 23, fiveMCount: 800);
        var (start, end) = Window(provider);

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);

        using var doc = JsonDocument.Parse(trained.TrainedStateJson);
        doc.RootElement.GetProperty("modelGbtBag").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("confidenceGate").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("modelGbt").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Confidence_gate_in_trainer_emitted_state_reaches_the_node_through_the_backtest_remap()
    {
        // End-to-end choke-point proof: the trainer's TrainedState (camelCase contract fields) must
        // survive BacktestRunner.RemapTrainedState and drive abstentions inside the serving node.
        // Same trained state, two backtests — with the confidenceGate and with it stripped. The gated
        // run must place strictly fewer bets (gate abstentions emit the canonical pUp = 0.5, which
        // the backtest loop skips); the ungated run bets nearly every candle.
        var (executor, flow) = BuildHarness(bags: 2, seed: 101, coverage: 0.05);
        var provider = SyntheticProvider(seed: 24, fiveMCount: 800);
        var (start, end) = Window(provider);

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);

        var stripped = System.Text.Json.Nodes.JsonNode.Parse(trained.TrainedStateJson)!;
        stripped["confidenceGate"] = null;

        var runner = new BacktestRunner(executor, provider, NullLogger<BacktestRunner>.Instance);
        var gated = await runner.RunAsync(
            flow, trained.TrainedStateJson, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            start, end, 1000m, 10m, true, new FlatStakingStrategy(), null, default);
        var ungated = await runner.RunAsync(
            flow, stripped.ToJsonString(), Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            start, end, 1000m, 10m, true, new FlatStakingStrategy(), null, default);

        ungated.BetsPlaced.Should().BeGreaterThan(100, "without the gate the GBT node emits an opinion on every ready candle");
        gated.BetsPlaced.Should().BeLessThan(ungated.BetsPlaced,
            "the coverage gate must abstain on low-|pCal-0.5| candles — proving the field passed through the remap to the node");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Top-level TrainedState properties minus the wall-clock trainedAt stamp.</summary>
    private static string Canonical(string trainedStateJson)
    {
        using var doc = JsonDocument.Parse(trainedStateJson);
        var sb = new StringBuilder();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "trainedAt") continue;
            sb.Append(prop.Name).Append('=').Append(prop.Value.GetRawText()).Append(';');
        }
        return sb.ToString();
    }

    private static (long Start, long End) Window(InMemoryCandleProvider provider)
        => (provider.MinOpen() + 70L * FiveMinMs, provider.MaxOpen() - 5L * FiveMinMs);

    private const long FiveMinMs = 300_000L;

    private static (IFlowExecutor Executor, FlowDefinition Flow) BuildHarness(int? bags, int? seed, double? coverage)
    {
        var registry = new NodeRegistry(new IFlowNode[]
        {
            new BinanceKlinesNode(), new MomentumPackNode(), new TemporalPackNode(),
            new MatrixBuilderNode(), new LogisticRegressionNode(), new GradientBoostedTreesNode(),
            new OutputPredictionNode(),
        });
        var validator = new FlowValidator(registry);
        var executor = new FlowExecutor(registry, validator, NullLogger<FlowExecutor>.Instance);

        var bagParams = bags is null ? "" :
            $""","bags": {bags}, "seed": {seed}, "coverage": {coverage?.ToString(System.Globalization.CultureInfo.InvariantCulture)}""";
        var flowJson = $$"""
        {
          "schemaVersion": 1, "modelKind": "deterministic", "supportsBacktesting": true, "warmupCandles": 60,
          "nodes": [
            { "id": "c5",   "type": "source.binance.klines",   "params": { "tf": "target", "limit": 60 } },
            { "id": "mom",  "type": "indicator.momentum_pack", "params": {} },
            { "id": "time", "type": "indicator.temporal_pack", "params": {} },
            { "id": "matrix", "type": "feature.matrix_builder",
              "params": { "columns": ["ret_1","ret_3","ret_5","hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend"] } },
            { "id": "model", "type": "model.gbt",
              "params": { "n_estimators": 40, "max_depth": 2, "learning_rate": 0.05, "min_samples_leaf": 20, "subsample": 0.8, "colsample": 0.9, "l2": 1.0{{bagParams}} } },
            { "id": "out", "type": "output.prediction", "params": {} }
          ],
          "edges": [
            { "from": "c5.candles", "to": "mom.candles" },
            { "from": "mom.ret_1", "to": "matrix.ret_1" },
            { "from": "mom.ret_3", "to": "matrix.ret_3" },
            { "from": "mom.ret_5", "to": "matrix.ret_5" },
            { "from": "time.hour_sin", "to": "matrix.hour_sin" },
            { "from": "time.hour_cos", "to": "matrix.hour_cos" },
            { "from": "time.dow_sin", "to": "matrix.dow_sin" },
            { "from": "time.dow_cos", "to": "matrix.dow_cos" },
            { "from": "time.is_us_session", "to": "matrix.is_us_session" },
            { "from": "time.is_eu_session", "to": "matrix.is_eu_session" },
            { "from": "time.is_weekend", "to": "matrix.is_weekend" },
            { "from": "matrix.matrix", "to": "model.matrix" },
            { "from": "matrix.ready", "to": "model.ready" },
            { "from": "model.pUp", "to": "out.pUp" },
            { "from": "model.confidence", "to": "out.confidence" }
          ]
        }
        """;
        var flow = JsonSerializer.Deserialize<FlowDefinition>(flowJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        validator.Validate(flow).IsValid.Should().BeTrue();
        return (executor, flow);
    }

    /// <summary>Single-timeframe 5m random walk with an hour-of-day direction bias (learnable signal).</summary>
    private static InMemoryCandleProvider SyntheticProvider(int seed, int fiveMCount)
    {
        var baseMs = (1_700_000_000_000L / 900_000L) * 900_000L;
        var rng = new Random(seed);
        var candles = new List<HistoricalCandle>(fiveMCount);
        var close = 50_000m;
        for (var k = 0; k < fiveMCount; k++)
        {
            var open = close;
            var openTime = baseMs + (long)k * FiveMinMs;
            var hour = DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime.Hour
                       + DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime.Minute / 60.0;
            var pUp = 0.5 + 0.25 * Math.Sin(2 * Math.PI * hour / 24.0);
            var up = rng.NextDouble() < pUp;
            var mag = 0.0004m + (decimal)(rng.NextDouble() * 0.0006);
            close = up ? open * (1 + mag) : open * (1 - mag);
            candles.Add(new HistoricalCandle
            {
                Symbol = Symbol,
                Interval = "5m",
                OpenTime = openTime,
                Open = open,
                High = Math.Max(open, close) * 1.0002m,
                Low = Math.Min(open, close) * 0.9998m,
                Close = close,
                Volume = 5m + (decimal)(rng.NextDouble() * 10),
            });
        }
        return new InMemoryCandleProvider(candles);
    }

    private sealed class InMemoryCandleProvider : IHistoricalCandleProvider
    {
        private readonly List<HistoricalCandle> _fiveM;
        public InMemoryCandleProvider(List<HistoricalCandle> fiveM) => _fiveM = fiveM;
        public long MinOpen() => _fiveM[0].OpenTime;
        public long MaxOpen() => _fiveM[^1].OpenTime;

        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            if (interval != "5m")
                return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(
                _fiveM.Where(c => c.Symbol == symbol && c.OpenTime >= startMs && c.OpenTime <= endMs).ToList());
        }
    }
}
