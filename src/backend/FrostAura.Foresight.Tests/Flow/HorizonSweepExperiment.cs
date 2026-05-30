using System.Globalization;
using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Models;
using Xunit;
using Xunit.Abstractions;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// RESEARCH EXPERIMENT (not a CI assertion). Answers "what timeframe gives the best HONEST hit-rate?"
/// The 5m every-candle symmetric direction bet is exhaustively pinned at ~53% (see
/// docs/foresight-5m-v1-iterations.md). This sweeps LONGER horizons — 15m / 1h / 4h / 1d — on the
/// standard next-bar direction task (close(i+1) vs close(i)) with the same honest machinery: real
/// Binance klines, causal TA features, real GBT engine, walk-forward (expanding + embargo).
///
/// Reports per interval: OOS hit-rate + Wilson CI, the LABEL BASE RATE and base-rate-ADJUSTED edge
/// (longer horizons drift up in a bull regime — a higher raw hit-rate that's just drift is NOT edge,
/// so we separate the two), overfit gap, and a conviction-gate readout. Run on demand:
///   FORESIGHT_RUN_EXPERIMENTS=1 dotnet test --filter FullyQualifiedName~HorizonSweepExperiment
/// </summary>
public sealed class HorizonSweepExperiment
{
    private readonly ITestOutputHelper _out;
    public HorizonSweepExperiment(ITestOutputHelper o) => _out = o;

    private const string Symbol = "BTCUSDT";
    private const int Folds = 4;
    private const int EmbargoBars = 3;
    private const int Warmup = 60;

    // interval → calendar days to pull (longer bars need more history for a ≥1000-bet OOS sample).
    private static readonly (string Interval, int Days)[] Sweep =
    {
        ("15m", 365),
        ("1h", 720),
        ("4h", 1460),
        ("1d", 2920),
    };

    [Fact]
    public async Task Horizon_sweep_best_hit_rate()
    {
        if (Environment.GetEnvironmentVariable("FORESIGHT_RUN_EXPERIMENTS") != "1")
        {
            _out.WriteLine("skipped — set FORESIGHT_RUN_EXPERIMENTS=1 to run the horizon sweep");
            return;
        }

        var report = new StringBuilder();
        void Log(string s) { _out.WriteLine(s); report.AppendLine(s); }

        Log("=== HORIZON SWEEP — best honest next-bar direction hit-rate by timeframe ===");
        Log("(5m baseline from prior work: ~52-53% OOS, no exploitable edge over coin-flip)");
        Log("");

        foreach (var (interval, days) in Sweep)
        {
            try { await OneInterval(interval, days, Log); }
            catch (Exception ex) { Log($"[{interval}] FAILED: {ex.Message}"); }
            Log("");
        }

        await File.WriteAllTextAsync(Path.Combine(FindRepoRoot(), "horizon-sweep-report.txt"), report.ToString());
    }

    private async Task OneInterval(string interval, int days, Action<string> Log)
    {
        var candles = await LoadKlines(interval, days);
        Log($"[{interval}] {candles.Count} candles ({days}d)");
        if (candles.Count < Warmup + 500) { Log($"[{interval}] too few candles, skipping"); return; }

        var close = candles.Select(c => c.Close).ToArray();
        var X = new List<double[]>();
        var Y = new List<int>();
        for (var i = Warmup; i < candles.Count - 1; i++)
        {
            var c = candles[i];
            var ret1 = Ret(close, i, 1);
            var ret3 = Ret(close, i, 3);
            var ret6 = Ret(close, i, 6);
            var ret12 = Ret(close, i, 12);
            var ret24 = Ret(close, i, 24);
            var (rsi, atrn) = RsiAtr(candles, i, 14);
            var sma20 = Sma(close, i, 20);
            var sma50 = Sma(close, i, 50);
            var smaDist20 = sma20 == 0 ? 0 : close[i] / sma20 - 1.0;
            var smaDist50 = sma50 == 0 ? 0 : close[i] / sma50 - 1.0;
            var rv = Rv(close, i, 12);
            var rangePct = close[i] == 0 ? 0 : (candles[i].High - candles[i].Low) / close[i];
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime).UtcDateTime;
            var hour = dt.Hour + dt.Minute / 60.0;
            var hourSin = Math.Sin(2 * Math.PI * hour / 24.0);
            var hourCos = Math.Cos(2 * Math.PI * hour / 24.0);
            var dowSin = Math.Sin(2 * Math.PI * (int)dt.DayOfWeek / 7.0);
            var dowCos = Math.Cos(2 * Math.PI * (int)dt.DayOfWeek / 7.0);

            if (close[i + 1] == close[i]) continue;
            X.Add(new[]
            {
                ret1, ret3, ret6, ret12, ret24,
                rsi / 100.0, atrn, smaDist20, smaDist50, rv, rangePct,
                hourSin, hourCos, dowSin, dowCos,
            });
            Y.Add(close[i + 1] > close[i] ? 1 : 0);
        }

        var baseRate = Y.Average();
        var majority = Math.Max(baseRate, 1 - baseRate);
        Log($"[{interval}] rows {X.Count}  base rate P(up)={baseRate:P2}  majority-baseline={majority:P2}");

        var n = X.Count;
        var bucket = n / (Folds + 1);
        var p = new GbtParams(NEstimators: 120, MaxDepth: 3, LearningRate: 0.05,
            MinSamplesLeaf: Math.Max(50, n / 200), Subsample: 0.7, ColSample: 0.8, Lambda: 1.0, Seed: 1);

        int totBets = 0, totWins = 0, foldsAbove = 0; double inSum = 0; int inN = 0;
        var oosPL = new List<(double Proba, int Label)>();
        for (var k = 1; k <= Folds; k++)
        {
            var trEnd = k * bucket;
            var ooStart = trEnd + EmbargoBars;
            var ooEnd = Math.Min((k + 1) * bucket, n);
            if (ooEnd <= ooStart) continue;
            var xtr = X.Take(trEnd).ToArray(); var ytr = Y.Take(trEnd).ToArray();
            var xoo = X.Skip(ooStart).Take(ooEnd - ooStart).ToArray(); var yoo = Y.Skip(ooStart).Take(ooEnd - ooStart).ToArray();
            if (xtr.Length < 300 || xoo.Length < 80) continue;
            var model = GradientBoostedTrees.Fit(xtr, ytr, p);
            int ow = 0;
            for (var i = 0; i < xoo.Length; i++)
            {
                var pr = GradientBoostedTrees.PredictProba(model, xoo[i]);
                var hit = (pr >= 0.5 ? 1 : 0) == yoo[i] ? 1 : 0;
                ow += hit; oosPL.Add((pr, yoo[i]));
            }
            int iw = 0; for (var i = 0; i < xtr.Length; i++) if ((GradientBoostedTrees.PredictProba(model, xtr[i]) >= 0.5 ? 1 : 0) == ytr[i]) iw++;
            var oacc = (double)ow / xoo.Length; var iacc = (double)iw / xtr.Length;
            totBets += xoo.Length; totWins += ow; inSum += iacc; inN++;
            if (oacc > 0.5) foldsAbove++;
        }
        if (totBets == 0) { Log($"[{interval}] no OOS bets"); return; }
        var oosHit = (double)totWins / totBets;
        var (lo, hi) = Wilson(totWins, totBets);
        var inMean = inN == 0 ? 0 : inSum / inN;
        Log($"[{interval}] OOS {oosHit:P2}  CI [{lo:P2},{hi:P2}]  on {totBets} bets | in-sample {inMean:P2} gap {inMean - oosHit:P2} | folds>50% {foldsAbove}/{Folds}");
        Log($"[{interval}] base-rate-ADJUSTED edge = {oosHit - majority:P2}  (raw hit minus always-bet-majority — the part that's real, not drift)");

        // Conviction gate.
        var ranked = oosPL.OrderByDescending(x => Math.Abs(x.Proba - 0.5)).ToList();
        var gates = new StringBuilder($"[{interval}] gated:");
        foreach (var frac in new[] { 0.10, 0.20, 0.50 })
        {
            var take = (int)(ranked.Count * frac);
            if (take < 100) continue;
            var sub = ranked.Take(take).ToList();
            var w = sub.Count(x => (x.Proba >= 0.5 ? 1 : 0) == x.Label);
            gates.Append($"  top{frac:P0}={(double)w / take:P1}({take})");
        }
        Log(gates.ToString());
    }

    // ---- indicators --------------------------------------------------------------------------

    private static double Ret(double[] c, int i, int b) => i - b < 0 || c[i - b] == 0 ? 0 : c[i] / c[i - b] - 1.0;
    private static double Sma(double[] c, int i, int n) { if (i - n + 1 < 0) return 0; double s = 0; for (var j = i - n + 1; j <= i; j++) s += c[j]; return s / n; }
    private static double Rv(double[] c, int i, int n)
    {
        if (i - n < 0) return 0;
        var r = new double[n]; for (var j = 0; j < n; j++) r[j] = c[i - j] / c[i - j - 1] - 1.0;
        var m = r.Average(); return Math.Sqrt(r.Select(x => (x - m) * (x - m)).Sum() / n);
    }
    private static (double rsi, double atrNorm) RsiAtr(List<Kline> c, int i, int n)
    {
        if (i < n) return (50, 0);
        double gain = 0, loss = 0, tr = 0;
        for (var j = i - n + 1; j <= i; j++)
        {
            var d = c[j].Close - c[j - 1].Close;
            if (d >= 0) gain += d; else loss -= d;
            tr += Math.Max(c[j].High - c[j].Low, Math.Max(Math.Abs(c[j].High - c[j - 1].Close), Math.Abs(c[j].Low - c[j - 1].Close)));
        }
        var rsi = (gain + loss) == 0 ? 50 : 100.0 - 100.0 / (1.0 + (loss == 0 ? 100 : gain / loss));
        return (rsi, c[i].Close == 0 ? 0 : (tr / n) / c[i].Close);
    }
    private static (double, double) Wilson(int wins, int n, double z = 1.96)
    {
        if (n == 0) return (0, 0);
        var p = (double)wins / n; var d = 1 + z * z / n;
        var ctr = (p + z * z / (2.0 * n)) / d;
        var m = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return (ctr - m, ctr + m);
    }

    // ---- data --------------------------------------------------------------------------------

    private sealed record Kline(long OpenTime, double Open, double High, double Low, double Close, double Volume);

    private async Task<List<Kline>> LoadKlines(string interval, int days)
    {
        var cache = Path.Combine(FindRepoRoot(), $".cache-btc-{interval}-{days}d.json");
        if (File.Exists(cache))
        {
            var cached = JsonSerializer.Deserialize<List<Kline>>(await File.ReadAllTextAsync(cache));
            if (cached is { Count: > 100 }) { _out.WriteLine($"[{interval}] loaded {cached.Count} from cache"); return cached; }
        }
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
        var dedup = all.GroupBy(c => c.OpenTime).Select(g => g.First()).OrderBy(c => c.OpenTime).ToList();
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(dedup));
        _out.WriteLine($"[{interval}] fetched {dedup.Count} from Binance");
        return dedup;

        static double D(JsonElement e) => double.Parse(e.GetString()!, CultureInfo.InvariantCulture);
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
