using System.Globalization;
using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Models;
using Xunit;
using Xunit.Abstractions;

namespace FrostAura.Foresight.Tests.Flow;

/// <summary>
/// RESEARCH EXPERIMENT (not a CI assertion). The "v3-intrabar" hypothesis: the ~53% 5m ceiling held
/// because every prior feature was a single per-5m aggregate. This reframes the task to the one Dean
/// asked for — use the forming (active) candle's live price. Concretely:
///
///   At decision sub-step s inside the still-forming 5m bar B (s = number of 1m sub-candles already
///   closed, s ∈ {1,2,3,4}), price_now = close of the last closed 1m sub-candle. Predict whether the
///   5m bar's CLOSE will be ABOVE price_now. Entry = price_now, settle = close(B). The bet is the
///   REMAINING intra-bar move — exactly what a live-refreshing chart dot represents.
///
/// Honesty: real Binance 1m klines, walk-forward (expanding window + embargo, split by BAR so the
/// overlapping sub-step rows of one bar never straddle the train/OOS boundary), real GBT engine.
/// Reports OOS hit-rate, Wilson CI, sample size, overfit gap, base-rate-adjusted edge, and a
/// per-sub-step breakdown. Compares against the always-predict-majority baseline so a trivial drift
/// bias can't masquerade as edge. Result is written to intrabar-experiment-report.txt.
/// </summary>
public sealed class IntrabarExperiment
{
    private readonly ITestOutputHelper _out;
    public IntrabarExperiment(ITestOutputHelper o) => _out = o;

    private const string Symbol = "BTCUSDT";
    private const int Days = 50;
    private const int Warmup5m = 30;        // 5m context-indicator warmup
    private const int Folds = 4;
    private const int EmbargoBars = 3;

    // Research experiment — hits Binance + trains GBT, so it's a no-op in normal runs. Run on demand:
    //   FORESIGHT_RUN_EXPERIMENTS=1 dotnet test --filter FullyQualifiedName~IntrabarExperiment
    [Fact]
    public async Task Intrabar_walkforward_edge_report()
    {
        if (Environment.GetEnvironmentVariable("FORESIGHT_RUN_EXPERIMENTS") != "1")
        {
            _out.WriteLine("skipped — set FORESIGHT_RUN_EXPERIMENTS=1 to run the intra-bar experiment");
            return;
        }
        await Execute();
    }

    private async Task Execute()
    {
        var report = new StringBuilder();
        void Log(string s) { _out.WriteLine(s); report.AppendLine(s); }

        var oneM = await LoadOneMinute(Days);
        Log($"v3-intrabar experiment — {Symbol}, {Days}d, {oneM.Count} 1m candles");

        // Group 1m into complete 5m bars.
        var bars = BuildFiveMinuteBars(oneM);
        Log($"complete 5m bars: {bars.Count}");

        // 5m context indicator series (indexed by bar position).
        var close5 = bars.Select(b => b.Close).ToArray();
        var (rsi14, atr14n, rv12) = ContextSeries(bars);

        // Build (features, label, barIndex, subStep, priceNow, close5) rows.
        var X = new List<double[]>();
        var Y = new List<int>();
        var bIdx = new List<int>();
        var sStep = new List<int>();
        for (var b = Warmup5m; b < bars.Count; b++)
        {
            var bar = bars[b];
            var open5 = bar.Open;
            var ctxClosePrev = close5[b - 1];

            // 5m context (strictly < bar b).
            var ret1 = Ret(close5, b - 1, 1);
            var ret3 = Ret(close5, b - 1, 3);
            var ret6 = Ret(close5, b - 1, 6);
            var ret12 = Ret(close5, b - 1, 12);
            var sma20 = Sma(close5, b - 1, 20);
            var smaDist = sma20 == 0 ? 0 : ctxClosePrev / sma20 - 1.0;
            var hour = DateTimeOffset.FromUnixTimeMilliseconds(bar.OpenTime).UtcDateTime.Hour
                       + DateTimeOffset.FromUnixTimeMilliseconds(bar.OpenTime).UtcDateTime.Minute / 60.0;
            var hourSin = Math.Sin(2 * Math.PI * hour / 24.0);
            var hourCos = Math.Cos(2 * Math.PI * hour / 24.0);

            for (var s = 1; s <= 4; s++)
            {
                // Partial bar from sub-candles [0..s-1]; price_now = close of sub-candle s-1.
                var subs = bar.Subs;
                var priceNow = subs[s - 1].Close;
                double barHi = open5, barLo = open5;
                double cvd = 0;
                for (var j = 0; j < s; j++)
                {
                    barHi = Math.Max(barHi, subs[j].High);
                    barLo = Math.Min(barLo, subs[j].Low);
                    var o = subs[j].Open; var c = subs[j].Close;
                    cvd += (c >= o ? 1 : -1) * (o == 0 ? 0 : Math.Abs(c / o - 1.0));
                }
                var partialRet = open5 == 0 ? 0 : priceNow / open5 - 1.0;
                var elapsed = s / 5.0;
                var partialRange = open5 == 0 ? 0 : (barHi - barLo) / open5;
                var posInRange = (barHi - barLo) <= 0 ? 0.5 : (priceNow - barLo) / (barHi - barLo);
                var last1mRet = s == 1
                    ? (open5 == 0 ? 0 : subs[0].Close / open5 - 1.0)
                    : (subs[s - 2].Close == 0 ? 0 : subs[s - 1].Close / subs[s - 2].Close - 1.0);
                var prev1mRet = s <= 1 ? 0
                    : (s == 2 ? (open5 == 0 ? 0 : subs[0].Close / open5 - 1.0)
                              : (subs[s - 3].Close == 0 ? 0 : subs[s - 2].Close / subs[s - 3].Close - 1.0));
                var accel = last1mRet - prev1mRet;

                var label = bar.Close > priceNow ? 1 : 0;
                if (bar.Close == priceNow) continue; // drop exact ties

                X.Add(new[]
                {
                    ret1, ret3, ret6, ret12,
                    rsi14[b - 1] / 100.0, atr14n[b - 1], smaDist, rv12[b - 1],
                    partialRet, elapsed, partialRange, posInRange,
                    last1mRet, accel, cvd, hourSin, hourCos,
                });
                Y.Add(label);
                bIdx.Add(b);
                sStep.Add(s);
            }
        }
        Log($"rows: {X.Count}  (cols={X[0].Length})");
        var baseRate = Y.Average();
        Log($"label base rate P(close>price_now) = {baseRate:P2}  → majority-baseline acc = {Math.Max(baseRate, 1 - baseRate):P2}");

        // Walk-forward by BAR index.
        var minBar = bIdx[0];
        var maxBar = bIdx[^1];
        var span = maxBar - minBar + 1;
        var bucket = span / (Folds + 1);
        var p = new GbtParams(NEstimators: 120, MaxDepth: 3, LearningRate: 0.05,
            MinSamplesLeaf: 400, Subsample: 0.7, ColSample: 0.8, Lambda: 1.0, Seed: 1);

        int totBets = 0, totWins = 0, foldsAbove = 0;
        double inSampleAccSum = 0; int inSampleFolds = 0;
        var perStepWins = new int[5]; var perStepBets = new int[5];
        var oosProbaLabel = new List<(double Proba, int Label)>(); // for conviction-gate readout

        for (var k = 1; k <= Folds; k++)
        {
            var trainEndBar = minBar + k * bucket;
            var oosStartBar = trainEndBar + EmbargoBars;
            var oosEndBar = minBar + (k + 1) * bucket;
            if (oosEndBar <= oosStartBar) continue;

            var xtr = new List<double[]>(); var ytr = new List<int>();
            var xoo = new List<double[]>(); var yoo = new List<int>(); var soo = new List<int>();
            for (var i = 0; i < X.Count; i++)
            {
                if (bIdx[i] <= trainEndBar) { xtr.Add(X[i]); ytr.Add(Y[i]); }
                else if (bIdx[i] > oosStartBar && bIdx[i] <= oosEndBar) { xoo.Add(X[i]); yoo.Add(Y[i]); soo.Add(sStep[i]); }
            }
            if (xtr.Count < 500 || xoo.Count < 100) continue;

            var model = GradientBoostedTrees.Fit(xtr.ToArray(), ytr.ToArray(), p);

            int oosWins = 0;
            for (var i = 0; i < xoo.Count; i++)
            {
                var proba = GradientBoostedTrees.PredictProba(model, xoo[i]);
                var pred = proba >= 0.5 ? 1 : 0;
                var hit = pred == yoo[i] ? 1 : 0;
                oosWins += hit;
                perStepBets[soo[i]]++; perStepWins[soo[i]] += hit;
                oosProbaLabel.Add((proba, yoo[i]));
            }
            int inWins = 0;
            for (var i = 0; i < xtr.Count; i++)
                if ((GradientBoostedTrees.PredictProba(model, xtr[i]) >= 0.5 ? 1 : 0) == ytr[i]) inWins++;

            var oosAcc = (double)oosWins / xoo.Count;
            var inAcc = (double)inWins / xtr.Count;
            totBets += xoo.Count; totWins += oosWins;
            inSampleAccSum += inAcc; inSampleFolds++;
            if (oosAcc > 0.5) foldsAbove++;
            Log($"  fold {k}: train {xtr.Count}, OOS {xoo.Count} → OOS {oosAcc:P2} | in-sample {inAcc:P2} | gap {inAcc - oosAcc:P2}");
        }

        var oosHit = (double)totWins / Math.Max(1, totBets);
        var (lo, hi) = Wilson(totWins, totBets);
        var inMean = inSampleFolds == 0 ? 0 : inSampleAccSum / inSampleFolds;
        Log("");
        Log($"=== AGGREGATE (OOS, all folds) ===");
        Log($"OOS hit-rate : {oosHit:P2}   on {totBets} bets");
        Log($"Wilson 95% CI: [{lo:P2}, {hi:P2}]   (edge real only if CI-low > 50%)");
        Log($"in-sample    : {inMean:P2}   → overfit gap {inMean - oosHit:P2}");
        Log($"folds > 50%  : {foldsAbove}/{Folds}");
        Log($"vs majority baseline {Math.Max(baseRate, 1 - baseRate):P2} → edge over baseline {oosHit - Math.Max(baseRate, 1 - baseRate):P2}");
        Log("per-sub-step OOS hit (minutes remaining):");
        for (var s = 1; s <= 4; s++)
            if (perStepBets[s] > 0)
                Log($"   s={s} ({5 - s}m left): {(double)perStepWins[s] / perStepBets[s]:P2}  ({perStepBets[s]} bets)");

        // Conviction-gate readout: does the high-|p-0.5| subset beat the always-bet average? Sort OOS
        // predictions by distance from 0.5 and report hit-rate of the most-confident top fractions.
        Log("conviction-gated OOS (most-confident subset, |p-0.5| ranked):");
        var ranked = oosProbaLabel.OrderByDescending(x => Math.Abs(x.Proba - 0.5)).ToList();
        decimal bestGatedHit = 0; int bestGatedN = 0; double bestGatedFrac = 0;
        foreach (var frac in new[] { 0.05, 0.10, 0.20, 0.30, 0.50 })
        {
            var take = (int)(ranked.Count * frac);
            if (take < 200) continue;
            var subset = ranked.Take(take).ToList();
            var wins = subset.Count(x => (x.Proba >= 0.5 ? 1 : 0) == x.Label);
            var ghit = (double)wins / take;
            var (glo, ghi) = Wilson(wins, take);
            Log($"   top {frac:P0} ({take} bets): {ghit:P2}  CI [{glo:P2},{ghi:P2}]");
            if (take >= 1000 && glo > 0.5 && (decimal)ghit > bestGatedHit) { bestGatedHit = (decimal)ghit; bestGatedN = take; bestGatedFrac = frac; }
        }
        if (bestGatedN > 0) Log($"best honest gated edge: {bestGatedHit:P2} on top {bestGatedFrac:P0} ({bestGatedN} bets, CI-low>50%)");

        var verdict = oosHit > 0.55 && lo > 0.5 && totBets >= 1000 && foldsAbove * 2 > Folds && (inMean - oosHit) <= 0.05
            ? "PROMISING — beats 53 ceiling out-of-sample under guards"
            : "NOT a clear win — does not beat the ceiling under honest guards";
        Log($"VERDICT: {verdict}");

        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "intrabar-experiment-report.txt"), report.ToString());
        var stamped = Path.Combine(FindRepoRoot(), "intrabar-experiment-report.txt");
        await File.WriteAllTextAsync(stamped, report.ToString());
    }

    // ---- data + indicators -------------------------------------------------------------------

    private sealed record Sub(double Open, double High, double Low, double Close);
    private sealed record Bar(long OpenTime, double Open, double High, double Low, double Close, Sub[] Subs);

    private static List<Bar> BuildFiveMinuteBars(List<OneM> oneM)
    {
        var bars = new List<Bar>();
        var byBucket = new Dictionary<long, List<OneM>>();
        foreach (var c in oneM)
        {
            var bkt = c.OpenTime - c.OpenTime % 300_000L;
            if (!byBucket.TryGetValue(bkt, out var l)) { l = new List<OneM>(); byBucket[bkt] = l; }
            l.Add(c);
        }
        foreach (var bkt in byBucket.Keys.OrderBy(x => x))
        {
            var subsRaw = byBucket[bkt].OrderBy(c => c.OpenTime).ToList();
            if (subsRaw.Count != 5) continue; // only complete bars
            // contiguity check
            var ok = true;
            for (var j = 0; j < 5; j++) if (subsRaw[j].OpenTime != bkt + j * 60_000L) { ok = false; break; }
            if (!ok) continue;
            var subs = subsRaw.Select(c => new Sub(c.Open, c.High, c.Low, c.Close)).ToArray();
            bars.Add(new Bar(bkt, subs[0].Open, subs.Max(s => s.High), subs.Min(s => s.Low), subs[4].Close, subs));
        }
        return bars;
    }

    private static (double[] rsi, double[] atrNorm, double[] rv) ContextSeries(List<Bar> bars)
    {
        var n = bars.Count;
        var rsi = new double[n]; var atr = new double[n]; var rv = new double[n];
        for (var i = 0; i < n; i++)
        {
            rsi[i] = 50; atr[i] = 0; rv[i] = 0;
            if (i >= 14)
            {
                double gain = 0, loss = 0, tr = 0;
                for (var j = i - 13; j <= i; j++)
                {
                    var d = bars[j].Close - bars[j - 1].Close;
                    if (d >= 0) gain += d; else loss -= d;
                    var t = Math.Max(bars[j].High - bars[j].Low,
                        Math.Max(Math.Abs(bars[j].High - bars[j - 1].Close), Math.Abs(bars[j].Low - bars[j - 1].Close)));
                    tr += t;
                }
                rsi[i] = (gain + loss) == 0 ? 50 : 100.0 - 100.0 / (1.0 + (loss == 0 ? 100 : gain / loss));
                atr[i] = bars[i].Close == 0 ? 0 : (tr / 14) / bars[i].Close;
            }
            if (i >= 12)
            {
                var rets = new double[12];
                for (var j = 0; j < 12; j++) rets[j] = bars[i - j].Close / bars[i - j - 1].Close - 1.0;
                var m = rets.Average();
                rv[i] = Math.Sqrt(rets.Select(x => (x - m) * (x - m)).Sum() / 12);
            }
        }
        return (rsi, atr, rv);
    }

    private static double Ret(double[] c, int idx, int back) =>
        idx - back < 0 || c[idx - back] == 0 ? 0 : c[idx] / c[idx - back] - 1.0;
    private static double Sma(double[] c, int idx, int n)
    {
        if (idx - n + 1 < 0) return 0;
        double s = 0; for (var j = idx - n + 1; j <= idx; j++) s += c[j]; return s / n;
    }

    private static (double, double) Wilson(int wins, int n, double z = 1.96)
    {
        if (n == 0) return (0, 0);
        var p = (double)wins / n; var d = 1 + z * z / n;
        var c = (p + z * z / (2.0 * n)) / d;
        var m = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return (c - m, c + m);
    }

    private sealed record OneM(long OpenTime, double Open, double High, double Low, double Close, double Volume);

    private async Task<List<OneM>> LoadOneMinute(int days)
    {
        var cache = Path.Combine(FindRepoRoot(), $".cache-btc-1m-{days}d.json");
        if (File.Exists(cache))
        {
            var cached = JsonSerializer.Deserialize<List<OneM>>(await File.ReadAllTextAsync(cache));
            if (cached is { Count: > 1000 }) { _out.WriteLine($"loaded {cached.Count} 1m candles from cache"); return cached; }
        }
        var http = new HttpClient();
        var endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startMs = endMs - (long)days * 24 * 60 * 60 * 1000;
        var all = new List<OneM>();
        var cursor = startMs;
        while (cursor < endMs)
        {
            var url = $"https://api.binance.com/api/v3/klines?symbol={Symbol}&interval=1m&startTime={cursor}&limit=1000";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() == 0) break;
            long lastOpen = cursor;
            foreach (var e in arr.EnumerateArray())
            {
                lastOpen = e[0].GetInt64();
                all.Add(new OneM(lastOpen,
                    D(e[1]), D(e[2]), D(e[3]), D(e[4]), D(e[5])));
            }
            if (arr.GetArrayLength() < 1000) break;
            cursor = lastOpen + 60_000L;
        }
        // de-dup + sort
        var dedup = all.GroupBy(c => c.OpenTime).Select(g => g.First()).OrderBy(c => c.OpenTime).ToList();
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(dedup));
        _out.WriteLine($"fetched {dedup.Count} 1m candles from Binance");
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
