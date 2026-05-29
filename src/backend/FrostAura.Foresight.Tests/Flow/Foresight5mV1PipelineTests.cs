using System.Text.Json;
using FluentAssertions;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// End-to-end Phase-1 pipeline coverage for "Foresight | 5m | v1": the seeded flow validates, the
/// existing trainer fits it on synthetic multi-timeframe data, and a single-pass backtest produces
/// bets through the full node DAG (klines → 5m/15m/1m feature packs → matrix → logistic regression
/// → output). Uses a fully in-memory candle provider so no Postgres/Binance is required.
///
/// The synthetic series embeds a learnable hour-of-day direction bias, so a correctly-wired model
/// must clear 50% in-sample — a wiring smoke test, NOT a claim about real-market accuracy.
/// </summary>
public sealed class Foresight5mV1PipelineTests
{
    private const string Symbol = "BTCUSDT";

    [Fact]
    public void V1_flow_passes_structural_validation()
    {
        var (_, validator, flow) = BuildHarness();
        var result = validator.Validate(flow);
        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task V1_flow_trains_and_backtests_end_to_end()
    {
        var (executor, _, flow) = BuildHarness();
        var provider = SyntheticProvider(seed: 7);

        var startMs = provider.MinOpen(Symbol, "5m") + 200L * Ms("5m"); // leave warmup headroom on the left
        var endMs = provider.MaxOpen(Symbol, "5m") - 5L * Ms("5m");

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", startMs, endMs, default);

        // Trainer produced a usable model: accuracy is a real probability and coefficients exist.
        trained.ValidationAccuracy.Should().BeInRange(0m, 1m);
        trained.TrainedStateJson.Should().Contain("modelLogisticRegression");
        using (var doc = JsonDocument.Parse(trained.TrainedStateJson))
        {
            var weights = doc.RootElement.GetProperty("modelLogisticRegression").GetProperty("weights");
            weights.GetArrayLength().Should().BeGreaterThan(20, "the v1 matrix wires >20 feature columns");
        }

        // Embedded hour-of-day signal must be learnable end-to-end.
        trained.ValidationAccuracy.Should().BeGreaterThan(0.5m, "the synthetic series has a real directional signal");

        // Single-pass backtest runs the whole inference DAG and actually places bets (no all-abstain,
        // matrix readies across all three timeframes).
        var runner = new BacktestRunner(executor, provider, NullLogger<BacktestRunner>.Instance);
        var outcome = await runner.RunAsync(
            flow, trained.TrainedStateJson, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            startMs, endMs, initialBalance: 1000m, initialBetSize: 10m, allowBorrow: true,
            strategy: new FlatStakingStrategy(), progress: null, ct: default);

        outcome.BetsPlaced.Should().BeGreaterThan(100, "the model bets every candle (no in-node gate)");
        outcome.HitRate.Should().NotBeNull();
        outcome.HitRate!.Value.Should().BeGreaterThan(0.5m, "the embedded signal should be exploitable in-sample");
    }

    /// <summary>
    /// Walk-forward harness: the embedded signal must hold OUT-OF-SAMPLE across multiple
    /// temporally-disjoint folds (each fold retrains, then scores on a later, embargoed block).
    /// This is the real generalisation test — distinct from in-sample fit. Also asserts the
    /// overfit gap stays modest, proving the harness measures genuine edge rather than memorisation.
    /// </summary>
    [Fact]
    public async Task WalkForward_shows_out_of_sample_edge_across_folds()
    {
        var (executor, _, flow) = BuildHarness();
        var provider = SyntheticProvider(seed: 3, fiveMCount: 1_400);

        var rangeStart = provider.MinOpen(Symbol, "5m");
        var rangeEnd = provider.MaxOpen(Symbol, "5m");

        var wf = new WalkForwardEvaluator(executor, provider);
        var result = await wf.EvaluateAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", rangeStart, rangeEnd, folds: 3, ct: default);

        result.Folds.Count.Should().BeGreaterThanOrEqualTo(2, "the range supports multiple folds");
        result.TotalOosBets.Should().BeGreaterThan(100, "each OOS block bets every candle");
        result.OosHitRate.Should().NotBeNull();
        result.OosHitRate!.Value.Should().BeGreaterThan(0.5m, "the embedded signal must generalise out-of-sample");
        // Majority of folds beat coinflip — regime robustness, not one lucky window.
        (result.FoldsAboveHalf * 2).Should().BeGreaterThan(result.Folds.Count);
        // The simple model on a clean signal should not show a large in-sample vs OOS gap.
        result.OverfitGap.Should().NotBeNull();
        result.OverfitGap!.Value.Should().BeLessThan(0.2m, "no severe overfitting on a clean linear signal");
    }

    /// <summary>
    /// Pins the 2-step canon end-to-end. The 5m series is a sawtooth where close(i+2) > close(i) for
    /// EVERY i (net +5 over two candles), while close(i+1) vs close(i) alternates up/down. So:
    ///   • if the system trains/grades target i+2 vs close(i) (the canon) → label is always UP, the
    ///     model bets UP and wins ~everything (hit-rate → 1.0);
    ///   • if it regressed to next-candle (i+1), or graded against the moving/forming candle
    ///     close(i+1), the alternating step would drag the hit-rate to ~50%.
    /// A hit-rate well above 50% therefore proves target = i+2 AND reference = close(i).
    /// </summary>
    [Fact]
    public async Task TwoStep_canon_targets_i_plus_2_graded_vs_previous_close()
    {
        var (executor, _, flow) = BuildHarness();
        var provider = SawtoothProvider();
        var start = provider.MinOpen(Symbol, "5m");
        var end = provider.MaxOpen(Symbol, "5m");

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);

        var runner = new BacktestRunner(executor, provider, NullLogger<BacktestRunner>.Instance);
        var outcome = await runner.RunAsync(
            flow, trained.TrainedStateJson, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            start, end, 1000m, 10m, true, new FlatStakingStrategy(), null, default);

        outcome.BetsPlaced.Should().BeGreaterThan(50);
        outcome.HitRate.Should().NotBeNull();
        outcome.HitRate!.Value.Should().BeGreaterThan(0.85m,
            "close(i+2) > close(i) always holds in the sawtooth — only the correct 2-step canon scores this high");
    }

    /// <summary>
    /// No-future-bias guard: the feature matrix computed for a candle must be identical whether the
    /// provider holds the full series or only candles up to (and including) the anchor. If any node
    /// peeked at future candles, truncating the future would change the matrix.
    /// </summary>
    [Fact]
    public async Task V1_features_are_causal_truncating_the_future_changes_nothing()
    {
        var (executor, _, flow) = BuildHarness();
        var full = SyntheticProvider(seed: 11);

        // Pick a target 5m candle well inside the series so warmup is satisfied either way.
        var fiveM = full.Series(Symbol, "5m");
        var anchorIndex = fiveM.Count - 50;
        var targetOpen = fiveM[anchorIndex + 1].OpenTime; // predicting the candle after the anchor

        var matrixFull = await ExecuteMatrix(executor, flow, full, targetOpen);

        // Truncated provider: drop every candle at or after the target open across ALL timeframes.
        var truncated = full.TruncatedBefore(targetOpen);
        var matrixTrunc = await ExecuteMatrix(executor, flow, truncated, targetOpen);

        matrixTrunc.Should().NotBeNull();
        matrixFull.Should().NotBeNull();
        matrixFull!.Columns.Should().Equal(matrixTrunc!.Columns);
        for (var c = 0; c < matrixFull.ColumnCount; c++)
            matrixFull.Rows[0, c].Should().BeApproximately(matrixTrunc.Rows[0, c], 1e-9,
                $"column '{matrixFull.Columns[c]}' must not depend on data at/after the target open");
    }

    [Fact]
    public void Gbt_fitter_learns_a_nonlinear_signal()
    {
        var rng = new Random(1);
        var x = new double[800][];
        var y = new int[800];
        for (var i = 0; i < x.Length; i++)
        {
            var f0 = rng.NextDouble() * 2 - 1;
            var f1 = rng.NextDouble() * 2 - 1;
            x[i] = new[] { f0, f1, rng.NextDouble() };           // 3rd feature is pure noise
            // Non-linear (XOR-ish) target a linear model can't separate but trees can.
            y[i] = (f0 * f1 > 0) ? 1 : 0;
        }
        var model = GradientBoostedTrees.Fit(x, y,
            new GbtParams(NEstimators: 120, MaxDepth: 3, MinSamplesLeaf: 10, Subsample: 1.0, ColSample: 1.0, Lambda: 1.0, Seed: 1));

        var correct = 0;
        for (var i = 0; i < x.Length; i++)
            if ((GradientBoostedTrees.PredictProba(model, x[i]) >= 0.5 ? 1 : 0) == y[i]) correct++;
        ((double)correct / x.Length).Should().BeGreaterThan(0.8, "boosted trees should capture the XOR interaction");
    }

    [Fact]
    public async Task Gbt_flow_trains_and_backtests_end_to_end()
    {
        var (executor, validator, _) = BuildHarness();
        var flow = JsonSerializer.Deserialize<FlowDefinition>(BuildGbtTestFlow(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        validator.Validate(flow).IsValid.Should().BeTrue();

        var provider = SyntheticProvider(seed: 9, fiveMCount: 1_600);
        var start = provider.MinOpen(Symbol, "5m") + 100L * Ms("5m");
        var end = provider.MaxOpen(Symbol, "5m") - 5L * Ms("5m");

        var trainer = new ModelTrainer(executor, provider);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);
        trained.TrainedStateJson.Should().Contain("modelGbt", "the GBT ensemble must be serialized into trained state");
        trained.TrainedStateJson.Should().Contain("\"engine\":\"gbt\"");

        var runner = new BacktestRunner(executor, provider, NullLogger<BacktestRunner>.Instance);
        var outcome = await runner.RunAsync(
            flow, trained.TrainedStateJson, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            start, end, 1000m, 10m, true, new FlatStakingStrategy(), null, default);

        outcome.BetsPlaced.Should().BeGreaterThan(100, "the GBT node emits pUp for every candle");
        outcome.HitRate.Should().NotBeNull();
        outcome.HitRate!.Value.Should().BeGreaterThan(0.5m, "GBT should learn the embedded hour-of-day signal");
    }

    // A minimal GBT flow: 5m momentum + temporal features → matrix → model.gbt. Test-friendly
    // hyper-params (small min_samples_leaf) so it can learn on a few hundred rows.
    private static string BuildGbtTestFlow() => /*lang=json,strict*/ """
    {
      "schemaVersion": 1, "modelKind": "deterministic", "supportsBacktesting": true, "warmupCandles": 60,
      "nodes": [
        { "id": "c5",   "type": "source.binance.klines",   "params": { "tf": "target", "limit": 60 } },
        { "id": "mom",  "type": "indicator.momentum_pack", "params": {} },
        { "id": "time", "type": "indicator.temporal_pack", "params": {} },
        { "id": "matrix", "type": "feature.matrix_builder",
          "params": { "columns": ["ret_1","ret_3","ret_5","hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend"] } },
        { "id": "model", "type": "model.gbt",
          "params": { "n_estimators": 80, "max_depth": 3, "learning_rate": 0.05, "min_samples_leaf": 20, "subsample": 0.8, "colsample": 0.9, "l2": 1.0 } },
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

    [Fact]
    public async Task Ofx_flow_trains_and_backtests_with_microstructure()
    {
        var (executor, validator, _) = BuildHarness();
        var flow = JsonSerializer.Deserialize<FlowDefinition>(
            BuiltInModels.BuildForesight5mV1OfxFlow(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        validator.Validate(flow).IsValid.Should().BeTrue(validator.Validate(flow).Error);

        var provider = SyntheticProvider(seed: 13, fiveMCount: 1_600);
        var micro = SyntheticMicro(provider, seed: 13);
        var start = provider.MinOpen(Symbol, "5m") + 100L * Ms("5m");
        var end = provider.MaxOpen(Symbol, "5m") - 5L * Ms("5m");

        // Trainer + backtester receive the microstructure provider; they pre-fetch + boundary-clamp it
        // exactly like candles, so the order-flow features are anti-lookahead by construction.
        var trainer = new ModelTrainer(executor, provider, micro);
        var trained = await trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);
        trained.TrainedStateJson.Should().Contain("of_imbalance", "order-flow columns must be in the feature set");

        var runner = new BacktestRunner(executor, provider, NullLogger<BacktestRunner>.Instance, micro);
        var outcome = await runner.RunAsync(
            flow, trained.TrainedStateJson, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m",
            start, end, 1000m, 10m, true, new FlatStakingStrategy(), null, default);

        // The whole microstructure path executed: source → orderflow pack → matrix (ready) → model.
        outcome.BetsPlaced.Should().BeGreaterThan(100, "order-flow features are non-null so the matrix readies every candle");
        outcome.HitRate.Should().NotBeNull();
    }

    [Fact]
    public async Task Ofx_flow_without_microstructure_provider_abstains_gracefully()
    {
        // No micro provider wired → source emits empty → orderflow pack nulls → matrix never ready →
        // model abstains. Must NOT crash; just places no bets. Proves candle-only runs are unaffected
        // and the microstructure dependency fails safe.
        var (executor, _, _) = BuildHarness();
        var flow = JsonSerializer.Deserialize<FlowDefinition>(
            BuiltInModels.BuildForesight5mV1OfxFlow(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var provider = SyntheticProvider(seed: 14, fiveMCount: 600);
        var start = provider.MinOpen(Symbol, "5m") + 100L * Ms("5m");
        var end = provider.MaxOpen(Symbol, "5m") - 5L * Ms("5m");

        var trainer = new ModelTrainer(executor, provider); // micro = null
        Func<Task> train = () => trainer.TrainAsync(flow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", start, end, default);
        // Not enough ready rows (matrix never readies) → trainer throws a clear "not enough rows", not a crash.
        await train.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<FeatureMatrix?> ExecuteMatrix(
        IFlowExecutor executor, FlowDefinition flow, IHistoricalCandleProvider provider, long targetOpen)
    {
        var ctx = new FlowContext(Guid.NewGuid(), Guid.NewGuid(), Symbol, "5m", targetOpen, 1,
            FlowMode.Backtest, provider, TrainedState: null);
        var result = await executor.ExecuteAsync(flow, ctx, default);
        var entry = result.NodeOutputs.FirstOrDefault(kv => kv.Value.ContainsKey("matrix"));
        return entry.Value?["matrix"] as FeatureMatrix;
    }

    private static (IFlowExecutor Executor, FlowValidator Validator, FlowDefinition Flow) BuildHarness()
    {
        var nodes = new IFlowNode[]
        {
            new BinanceKlinesNode(),
            new MomentumPackNode(), new NormPackNode(), new VolumePackNode(),
            new TemporalPackNode(), new HtfRegimePackNode(), new SubBarPackNode(),
            new MatrixBuilderNode(), new LogisticRegressionNode(), new GradientBoostedTreesNode(), new OutputPredictionNode(),
            new MicrostructureSourceNode(), new OrderFlowPackNode(), new DerivativesPackNode(),
        };
        var registry = new NodeRegistry(nodes);
        var validator = new FlowValidator(registry);
        var executor = new FlowExecutor(registry, validator, NullLogger<FlowExecutor>.Instance);
        var flow = JsonSerializer.Deserialize<FlowDefinition>(
            BuiltInModels.BuildForesight5mV1Flow(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return (executor, validator, flow);
    }

    private static long Ms(string tf) => tf switch { "1m" => 60_000L, "5m" => 300_000L, "15m" => 900_000L, _ => throw new ArgumentException(tf) };

    /// <summary>Builds aligned 1m/5m/15m random-walk series with an hour-of-day direction bias.</summary>
    private static InMemoryCandleProvider SyntheticProvider(int seed, int fiveMCount = 3_000)
    {
        var baseMs = (1_700_000_000_000L / 900_000L) * 900_000L; // aligned to a 15m boundary
        var rng = new Random(seed);
        // Scale off-tf counts to cover the same wall-clock span (+ buffer for warmup/embargo).
        var data = new Dictionary<string, List<HistoricalCandle>>
        {
            ["1m"]  = Walk("1m",  fiveMCount * 5 + 400, baseMs, rng),
            ["5m"]  = Walk("5m",  fiveMCount,           baseMs, rng),
            ["15m"] = Walk("15m", fiveMCount / 3 + 100, baseMs, rng),
        };
        return new InMemoryCandleProvider(data);
    }

    /// <summary>5m sawtooth (close(i+2) > close(i) always; close(i+1) alternates) + random off-tf for readiness.</summary>
    private static InMemoryCandleProvider SawtoothProvider()
    {
        var baseMs = (1_700_000_000_000L / 900_000L) * 900_000L;
        var rng = new Random(5);
        var data = new Dictionary<string, List<HistoricalCandle>>
        {
            ["1m"]  = Walk("1m", 3_200, baseMs, rng),
            ["5m"]  = Sawtooth("5m", 600, baseMs),
            ["15m"] = Walk("15m", 260, baseMs, rng),
        };
        return new InMemoryCandleProvider(data);
    }

    private static List<HistoricalCandle> Sawtooth(string tf, int count, long baseMs)
    {
        var ms = Ms(tf);
        var candles = new List<HistoricalCandle>(count);
        var close = 100m;
        for (var k = 0; k < count; k++)
        {
            var open = close;
            // +10 on even→odd, -5 on odd→even: net +5 every two candles, so close(k+2) > close(k)
            // always, while close(k+1) vs close(k) alternates up/down.
            if (k > 0) close = open + (k % 2 == 1 ? 10m : -5m);
            candles.Add(new HistoricalCandle
            {
                Symbol = Symbol, Interval = tf, OpenTime = baseMs + (long)k * ms,
                Open = open, High = Math.Max(open, close) + 1m, Low = Math.Min(open, close) - 1m,
                Close = close, Volume = 10m,
            });
        }
        return candles;
    }

    private static List<HistoricalCandle> Walk(string tf, int count, long baseMs, Random rng)
    {
        var ms = Ms(tf);
        var candles = new List<HistoricalCandle>(count);
        var close = 50_000m;
        for (var k = 0; k < count; k++)
        {
            var open = close;
            var openTime = baseMs + (long)k * ms;
            var hour = DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime.Hour
                       + DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime.Minute / 60.0;
            // Hour-of-day up-bias — the learnable signal.
            var pUp = 0.5 + 0.25 * Math.Sin(2 * Math.PI * hour / 24.0);
            var up = rng.NextDouble() < pUp;
            var mag = 0.0004m + (decimal)(rng.NextDouble() * 0.0006);
            close = up ? open * (1 + mag) : open * (1 - mag);
            var high = Math.Max(open, close) * 1.0002m;
            var low = Math.Min(open, close) * 0.9998m;
            var vol = 5m + (decimal)(rng.NextDouble() * 10);
            candles.Add(new HistoricalCandle
            {
                Symbol = Symbol, Interval = tf, OpenTime = openTime,
                Open = open, High = high, Low = low, Close = close, Volume = vol,
            });
        }
        return candles;
    }

    /// <summary>One microstructure bar per 5m candle, with varied (non-degenerate) order-flow aggregates.</summary>
    private static FakeMicroProvider SyntheticMicro(InMemoryCandleProvider candles, int seed)
    {
        var rng = new Random(seed);
        var bars = new List<MicrostructureBar>();
        foreach (var c in candles.Series(Symbol, "5m"))
        {
            var buyVol = 10m + (decimal)(rng.NextDouble() * 90);
            var sellVol = 10m + (decimal)(rng.NextDouble() * 90);
            var tradeCount = 50 + rng.Next(250);
            bars.Add(new MicrostructureBar
            {
                Symbol = Symbol, Interval = "5m", OpenTime = c.OpenTime,
                TradeCount = tradeCount,
                BuyVolume = buyVol, SellVolume = sellVol,
                BuyTradeCount = rng.Next(tradeCount + 1),
                LargeBuyVolume = buyVol * (decimal)rng.NextDouble(),
                LargeSellVolume = sellVol * (decimal)rng.NextDouble(),
                // Derivatives metrics (5m cadence) so derivatives_pack readies.
                OpenInterest = 90000m + (decimal)(rng.NextDouble() * 20000),
                OpenInterestValue = 7_000_000_000m + (decimal)(rng.NextDouble() * 1_000_000_000),
                TopTraderLongShortRatio = 0.8m + (decimal)(rng.NextDouble() * 0.8),
                LongShortRatio = 1.0m + (decimal)(rng.NextDouble() * 1.5),
                TakerLongShortVolRatio = 0.7m + (decimal)(rng.NextDouble() * 0.8),
            });
        }
        return new FakeMicroProvider(bars);
    }

    private sealed class FakeMicroProvider : IHistoricalMicrostructureProvider
    {
        private readonly List<MicrostructureBar> _bars;
        public FakeMicroProvider(List<MicrostructureBar> bars) => _bars = bars;
        public Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MicrostructureBar>>(
                _bars.Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= startMs && b.OpenTime <= endMs).ToList());
    }

    /// <summary>In-memory IHistoricalCandleProvider over pre-built per-timeframe series.</summary>
    private sealed class InMemoryCandleProvider : IHistoricalCandleProvider
    {
        private readonly IReadOnlyDictionary<string, List<HistoricalCandle>> _byTf;
        public InMemoryCandleProvider(IReadOnlyDictionary<string, List<HistoricalCandle>> byTf) => _byTf = byTf;

        public IReadOnlyList<HistoricalCandle> Series(string symbol, string tf) => _byTf[tf];
        public long MinOpen(string symbol, string tf) => _byTf[tf][0].OpenTime;
        public long MaxOpen(string symbol, string tf) => _byTf[tf][^1].OpenTime;

        public InMemoryCandleProvider TruncatedBefore(long openTimeExclusive)
        {
            var clone = _byTf.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Where(c => c.OpenTime < openTimeExclusive).ToList());
            return new InMemoryCandleProvider(clone);
        }

        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            if (!_byTf.TryGetValue(interval, out var series))
                return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
            var window = series.Where(c => c.Symbol == symbol && c.OpenTime >= startMs && c.OpenTime <= endMs).ToList();
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(window);
        }
    }
}
