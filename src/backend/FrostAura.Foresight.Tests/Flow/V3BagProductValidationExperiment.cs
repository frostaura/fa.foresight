using System.Globalization;
using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// RESEARCH EXPERIMENT (not a CI assertion). In-product sanity check for the NEW
/// "Foresight | 15m | v3-bag" built-in (the productionised 2026-06-10 campaign recipe): train +
/// walk-forward the EXACT shipped flow definition through the EXACT product machinery
/// (ModelTrainer → WalkForwardEvaluator → BacktestRunner) on real Binance candles — no research
/// shortcuts, the same node DAG / trained-state / abstention path live serving uses.
///
/// Two walk-forward passes over the same windows isolate the coverage gate:
///   • UNGATED — the shipped flow with coverage forced to 0 (bag + calibration + OOD guard still
///     active). Its OOS hit-rate is the bet-every-candle "WF mean accuracy".
///   • GATED   — the shipped flow as-is (coverage = 0.05). Its OOS hit-rate is the hit-rate on the
///     non-abstained (high-confidence) bets; 1 − gatedBets/ungatedBets is the gate's abstention rate.
///
/// EXPECTATION-SETTING: this is the product feature subset (klines-only — no OI/funding/Coinbase/
/// DVOL/breadth from the research stack) on a short (~20 day) window, so the gated hit-rate is
/// expected at 52–57%, NOT the research 58.3%. Run on demand:
///   FORESIGHT_RUN_EXPERIMENTS=1 dotnet test --filter FullyQualifiedName~V3BagProductValidationExperiment
/// </summary>
public sealed class V3BagProductValidationExperiment
{
    private readonly ITestOutputHelper _out;
    public V3BagProductValidationExperiment(ITestOutputHelper o) => _out = o;

    private const string Symbol = "BTCUSDT";
    // ≈ 2000 15m candles + warmup headroom by default; override via FORESIGHT_V3BAG_DAYS to probe
    // how the (data-hungry: min_samples_leaf=150, depth 6) recipe behaves with more history.
    private static readonly int Days =
        int.TryParse(Environment.GetEnvironmentVariable("FORESIGHT_V3BAG_DAYS"), out var d) && d > 0 ? d : 22;
    private const int Folds = 4;

    [Fact]
    public async Task V3Bag_15m_product_walkforward_report()
    {
        if (Environment.GetEnvironmentVariable("FORESIGHT_RUN_EXPERIMENTS") != "1")
        {
            _out.WriteLine("skipped — set FORESIGHT_RUN_EXPERIMENTS=1 to run the v3-bag product validation");
            return;
        }

        var report = new StringBuilder();
        void Log(string s) { _out.WriteLine(s); report.AppendLine(s); }

        Log("=== v3-bag PRODUCT validation — 'Foresight | 15m | v3-bag' through the product trainer/backtester ===");

        // Real Binance candles for every timeframe the flow wires: 15m target, 1h regime, 5m sub-bar.
        var data = new Dictionary<string, List<HistoricalCandle>>
        {
            ["15m"] = await LoadKlines("15m", Days),
            ["1h"] = await LoadKlines("1h", Days),
            ["5m"] = await LoadKlines("5m", Days),
        };
        var provider = new InMemoryCandleProvider(data);
        var fifteen = data["15m"];
        Log($"candles: 15m={fifteen.Count}  1h={data["1h"].Count}  5m={data["5m"].Count}  ({Days}d)");

        var (executor, validator) = BuildHarness();
        var gatedJson = BuiltInModels.BuildForesight15mV3BagFlow();
        // Ungated A/B twin: identical flow with the coverage gate disabled (bags/seed unchanged).
        var ungatedJson = gatedJson.Replace("\"coverage\": 0.05", "\"coverage\": 0");
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var gatedFlow = JsonSerializer.Deserialize<FlowDefinition>(gatedJson, jsonOpts)!;
        var ungatedFlow = JsonSerializer.Deserialize<FlowDefinition>(ungatedJson, jsonOpts)!;
        Assert.True(validator.Validate(gatedFlow).IsValid);

        // Leave warmup headroom on the left (trainer/backtester fetch warmup BEFORE rangeStart) and
        // the horizon on the right.
        const long intervalMs = 900_000L;
        var rangeStart = fifteen[0].OpenTime + 70L * intervalMs;
        var rangeEnd = fifteen[^1].OpenTime - 5L * intervalMs;

        var wf = new WalkForwardEvaluator(executor, provider);
        var ungated = await wf.EvaluateAsync(ungatedFlow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "15m", rangeStart, rangeEnd, Folds, default);
        var gated = await wf.EvaluateAsync(gatedFlow, Guid.NewGuid(), Guid.NewGuid(), Symbol, "15m", rangeStart, rangeEnd, Folds, default);

        Log("");
        Log($"UNGATED (coverage=0; bag+calibration+OOD active) — the bet-every-candle WF accuracy:");
        Log($"  pooled OOS {ungated.OosHitRate:P2}  CI [{ungated.OosHitRateCiLow:P2},{ungated.OosHitRateCiHigh:P2}]  on {ungated.TotalOosBets} bets" +
            $" | in-sample {ungated.InSampleHitRate:P2}  gap {ungated.OverfitGap:P2} | folds>50% {ungated.FoldsAboveHalf}/{ungated.Folds.Count} | Brier {ungated.MeanBrier:F4}");
        foreach (var f in ungated.Folds)
            Log($"  fold {f.Index}: OOS {f.OosHitRate:P2} ({f.OosBets} bets)  in-sample {f.InSampleHitRate:P2}  trainer-val {f.ValidationAccuracy:P2}");

        Log("");
        Log($"GATED (coverage=0.05 as shipped) — abstention canon pUp=0.5, no bet placed:");
        Log($"  pooled gated hit {gated.OosHitRate:P2}  CI [{gated.OosHitRateCiLow:P2},{gated.OosHitRateCiHigh:P2}]  on {gated.TotalOosBets} bets" +
            $" | folds>50% {gated.FoldsAboveHalf}/{gated.Folds.Count}");
        foreach (var f in gated.Folds)
        {
            var twin = ungated.Folds.FirstOrDefault(u => u.Index == f.Index);
            var abst = twin is { OosBets: > 0 } ? 1.0 - (double)f.OosBets / twin.OosBets : double.NaN;
            Log($"  fold {f.Index}: gated hit {f.OosHitRate:P2} ({f.OosBets} bets)  abstention {abst:P1}");
        }
        var pooledAbst = ungated.TotalOosBets == 0 ? double.NaN : 1.0 - (double)gated.TotalOosBets / ungated.TotalOosBets;
        Log($"  pooled abstention from the coverage gate: {pooledAbst:P1} (kept {gated.TotalOosBets}/{ungated.TotalOosBets} decisions)");
        Log("");
        Log($"Caveats: klines-only feature set (no OI/funding/Coinbase/DVOL/breadth), {Days}-day window,");
        Log("gated sample is small — read the CI, not the point estimate.");

        await File.WriteAllTextAsync(Path.Combine(FindRepoRoot(), "v3bag-product-validation-report.txt"), report.ToString());
    }

    private static (IFlowExecutor Executor, FlowValidator Validator) BuildHarness()
    {
        var registry = new NodeRegistry(new IFlowNode[]
        {
            new BinanceKlinesNode(),
            new TechPackNode(), new FeaturePackNode(),
            new MomentumPackNode(), new NormPackNode(), new VolumePackNode(),
            new TemporalPackNode(), new HtfRegimePackNode(), new SubBarPackNode(),
            new MatrixBuilderNode(), new LogisticRegressionNode(), new GradientBoostedTreesNode(),
            new OutputPredictionNode(),
        });
        var validator = new FlowValidator(registry);
        return (new FlowExecutor(registry, validator, NullLogger<FlowExecutor>.Instance), validator);
    }

    // ---- data (same fetch+cache pattern as HorizonSweepExperiment) -----------------------------

    private sealed record Kline(long OpenTime, double Open, double High, double Low, double Close, double Volume);

    private async Task<List<HistoricalCandle>> LoadKlines(string interval, int days)
    {
        var cache = Path.Combine(FindRepoRoot(), $".cache-btc-{interval}-{days}d.json");
        List<Kline>? klines = null;
        if (File.Exists(cache))
        {
            klines = JsonSerializer.Deserialize<List<Kline>>(await File.ReadAllTextAsync(cache));
            if (klines is { Count: > 100 }) _out.WriteLine($"[{interval}] loaded {klines.Count} from cache");
            else klines = null;
        }
        if (klines is null)
        {
            var http = new HttpClient();
            var endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startMs = endMs - (long)days * 86_400_000L;
            var all = new List<Kline>();
            var cursor = startMs;
            while (cursor < endMs)
            {
                var url = $"https://api.binance.com/api/v3/klines?symbol={Symbol}&interval={interval}&startTime={cursor}&limit=1000";
                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0) break;
                long last = cursor;
                foreach (var e in arr.EnumerateArray())
                {
                    last = e[0].GetInt64();
                    all.Add(new Kline(last, D(e[1]), D(e[2]), D(e[3]), D(e[4]), D(e[5])));
                }
                if (arr.GetArrayLength() < 1000) break;
                cursor = last + 1;
            }
            klines = all.GroupBy(c => c.OpenTime).Select(g => g.First()).OrderBy(c => c.OpenTime).ToList();
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(klines));
            _out.WriteLine($"[{interval}] fetched {klines.Count} from Binance");
        }
        return klines.Select(k => new HistoricalCandle
        {
            Symbol = Symbol,
            Interval = interval,
            OpenTime = k.OpenTime,
            Open = (decimal)k.Open,
            High = (decimal)k.High,
            Low = (decimal)k.Low,
            Close = (decimal)k.Close,
            Volume = (decimal)k.Volume,
        }).ToList();

        static double D(JsonElement e) => double.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    }

    private sealed class InMemoryCandleProvider : IHistoricalCandleProvider
    {
        private readonly Dictionary<string, List<HistoricalCandle>> _data;
        public InMemoryCandleProvider(Dictionary<string, List<HistoricalCandle>> data) => _data = data;

        public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
            string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        {
            if (!_data.TryGetValue(interval, out var series))
                return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(
                series.Where(c => c.Symbol == symbol && c.OpenTime >= startMs && c.OpenTime <= endMs).ToList());
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "backend"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
