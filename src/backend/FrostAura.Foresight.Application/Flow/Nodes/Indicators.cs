using System.Text.Json;
using FrostAura.Foresight.Domain.MarketData;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Pure in-process indicator math used by the indicator nodes. Inputs are series of candle closes
/// (decimal). All functions return <c>null</c> when the input is too short for the requested
/// period — callers fan that null forward as a sentinel that the indicator isn't warmed up yet.
/// </summary>
internal static class Indicators
{
    public static decimal? Sma(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count < period) return null;
        decimal s = 0m;
        for (var i = values.Count - period; i < values.Count; i++) s += values[i];
        return s / period;
    }

    public static decimal? Ema(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count < period) return null;
        var k = 2m / (period + 1);
        // Seed EMA with the SMA of the first `period` values, then walk forward.
        decimal ema = 0m;
        for (var i = 0; i < period; i++) ema += values[i];
        ema /= period;
        for (var i = period; i < values.Count; i++) ema = values[i] * k + ema * (1 - k);
        return ema;
    }

    public static decimal? Rsi(IReadOnlyList<decimal> closes, int period = 14)
    {
        if (closes.Count <= period) return null;
        decimal gainSum = 0m, lossSum = 0m;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta >= 0) gainSum += delta; else lossSum -= delta;
        }
        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        for (var i = period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var gain = delta >= 0 ? delta : 0m;
            var loss = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }
        if (avgLoss == 0m) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    public static decimal? Atr(IReadOnlyList<HistoricalCandle> candles, int period = 14)
    {
        if (candles.Count <= period) return null;
        var trs = new List<decimal>(candles.Count);
        for (var i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            var tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
            trs.Add(tr);
        }
        return Sma(trs, period);
    }

    public static (decimal Macd, decimal Signal, decimal Histogram)? Macd(IReadOnlyList<decimal> closes, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = Ema(closes, fast);
        var emaSlow = Ema(closes, slow);
        if (emaFast is null || emaSlow is null) return null;
        var macd = emaFast.Value - emaSlow.Value;
        // Approximation: signal-line EMA needs a history of MACD values; we synthesise it by walking
        // the series with progressively-larger slices. Cheap enough for warmup-bounded sizes.
        var macdSeries = new List<decimal>(closes.Count);
        for (var i = slow; i <= closes.Count; i++)
        {
            var slice = closes.Take(i).ToList();
            var f = Ema(slice, fast); var s = Ema(slice, slow);
            if (f is null || s is null) continue;
            macdSeries.Add(f.Value - s.Value);
        }
        var sig = Ema(macdSeries, signal);
        if (sig is null) return null;
        return (macd, sig.Value, macd - sig.Value);
    }

    public static (decimal Upper, decimal Lower)? BollingerBands(IReadOnlyList<decimal> closes, int period = 20, decimal k = 2m)
    {
        if (closes.Count < period) return null;
        var sma = Sma(closes, period)!.Value;
        decimal sq = 0m;
        for (var i = closes.Count - period; i < closes.Count; i++)
        {
            var d = closes[i] - sma;
            sq += d * d;
        }
        var std = (decimal)Math.Sqrt((double)(sq / period));
        return (sma + k * std, sma - k * std);
    }

    public static decimal LogReturn(decimal prev, decimal current) =>
        prev <= 0m ? 0m : (decimal)Math.Log((double)(current / prev));

    public static decimal? ZScore(IReadOnlyList<decimal> values, int period = 20)
    {
        if (values.Count < period) return null;
        var sma = Sma(values, period)!.Value;
        decimal sq = 0m;
        for (var i = values.Count - period; i < values.Count; i++)
        {
            var d = values[i] - sma;
            sq += d * d;
        }
        var std = (decimal)Math.Sqrt((double)(sq / period));
        return std == 0m ? 0m : (values[^1] - sma) / std;
    }

    /// <summary>
    /// Running on-balance volume. OBV[i] = OBV[i-1] + sign(close[i] - close[i-1]) * volume[i].
    /// Raw OBV grows monotonically — only the change/z-score is informative; use <see cref="ZScore"/>
    /// on the returned series for a stationary feature.
    /// </summary>
    public static IReadOnlyList<decimal> Obv(IReadOnlyList<HistoricalCandle> candles)
    {
        if (candles.Count == 0) return Array.Empty<decimal>();
        var series = new decimal[candles.Count];
        series[0] = 0m;
        for (var i = 1; i < candles.Count; i++)
        {
            var sign = Math.Sign(candles[i].Close - candles[i - 1].Close);
            series[i] = series[i - 1] + sign * candles[i].Volume;
        }
        return series;
    }
}

/// <summary>
/// Computes the iter-3 "tech_pack" snapshot for a candle series: SMA10, SMA20, RSI14, ATR14, the
/// last-N OHLCs, and a simple trend percentage. Used by the LLM-flow port-over for the same
/// supporting-data shape the legacy pipeline emitted.
/// </summary>
public sealed class TechPackNode : IFlowNode
{
    public string TypeId => "indicator.tech_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("sma10",     "decimal?"),
            new PortDef("sma20",     "decimal?"),
            new PortDef("rsi14",     "decimal?"),
            new PortDef("atr14",     "decimal?"),
            new PortDef("trendPct",  "decimal?"),
            new PortDef("last10",    "Candle[]"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        var closes = candles.Select(c => c.Close).ToList();
        var sma10 = Indicators.Sma(closes, 10);
        var sma20 = Indicators.Sma(closes, 20);
        var rsi14 = Indicators.Rsi(closes, 14);
        var atr14 = Indicators.Atr(candles, 14);
        decimal? trend = (closes.Count >= 20 && closes[^20] > 0)
            ? (closes[^1] - closes[^20]) / closes[^20] * 100m
            : null;
        var last10 = candles.Count > 10 ? candles.Skip(candles.Count - 10).ToList() : candles.ToList();
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["sma10"] = sma10,
            ["sma20"] = sma20,
            ["rsi14"] = rsi14,
            ["atr14"] = atr14,
            ["trendPct"] = trend,
            ["last10"] = (IReadOnlyList<HistoricalCandle>)last10,
        });
    }
}

/// <summary>
/// Wider feature pack used by the statistical models: EMA12 / EMA26 / MACD triple / Bollinger
/// upper+lower / log-return / 20-bar z-score on close. Returns nulls for warmup misses.
/// </summary>
public sealed class FeaturePackNode : IFlowNode
{
    public string TypeId => "indicator.feature_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("ema12",  "decimal?"),
            new PortDef("ema26",  "decimal?"),
            new PortDef("macd",   "decimal?"),
            new PortDef("signal", "decimal?"),
            new PortDef("hist",   "decimal?"),
            new PortDef("bbU",    "decimal?"),
            new PortDef("bbL",    "decimal?"),
            new PortDef("logret", "decimal"),
            new PortDef("z20",    "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        var closes = candles.Select(c => c.Close).ToList();
        var ema12 = Indicators.Ema(closes, 12);
        var ema26 = Indicators.Ema(closes, 26);
        var macd = Indicators.Macd(closes);
        var boll = Indicators.BollingerBands(closes);
        var logret = closes.Count >= 2 ? Indicators.LogReturn(closes[^2], closes[^1]) : 0m;
        var z20 = Indicators.ZScore(closes, 20);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["ema12"] = ema12,
            ["ema26"] = ema26,
            ["macd"] = macd?.Macd,
            ["signal"] = macd?.Signal,
            ["hist"] = macd?.Histogram,
            ["bbU"] = boll?.Upper,
            ["bbL"] = boll?.Lower,
            ["logret"] = logret,
            ["z20"] = z20,
        });
    }
}

/// <summary>
/// Interaction (product) features. A logistic regression sees features additively — it can't
/// learn "high volume AND uptrend predict continuation; high volume in a downtrend predicts
/// reversal" from the volume and trend columns separately. Multiplying them turns the interaction
/// into a feature the linear model CAN see. Standard trick for squeezing non-linear capacity out
/// of a linear classifier without going to trees.
///
/// Outputs five products of base features (all already stationary, so the products are too):
/// <list type="bullet">
///   <item><c>mom_x_vol</c> = ret_3 * vol_z20  — directional volume.</item>
///   <item><c>pos_x_mom</c> = (bb_pos - 0.5) * ret_3  — momentum relative to Bollinger position.</item>
///   <item><c>regime_x_mom</c> = atr_pct * ret_3  — momentum scaled by vol regime.</item>
///   <item><c>trend_x_mom</c> = ema_spread_atr * ret_3  — short-term momentum vs longer-term trend.</item>
///   <item><c>vol_x_range</c> = vol_z20 * (bb_pos - 0.5)  — extreme position confirmed by volume.</item>
/// </list>
/// All nulls during warmup (mirror momentum_pack + norm_pack + volume_pack warmup logic, ≥ 26).
/// </summary>
public sealed class CrossPackNode : IFlowNode
{
    public string TypeId => "indicator.cross_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("mom_x_vol",    "decimal?"),
            new PortDef("pos_x_mom",    "decimal?"),
            new PortDef("regime_x_mom", "decimal?"),
            new PortDef("trend_x_mom",  "decimal?"),
            new PortDef("vol_x_range",  "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        decimal? momXVol = null, posXMom = null, regimeXMom = null, trendXMom = null, volXRange = null;

        if (candles.Count >= 26)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var volumes = candles.Select(c => c.Volume).ToList();
            decimal? ret3 = closes.Count > 3 && closes[^4] > 0m
                ? Indicators.LogReturn(closes[^4], closes[^1]) : null;
            var volZ = Indicators.ZScore(volumes, 20);
            var atr = Indicators.Atr(candles, 14);
            var ema12 = Indicators.Ema(closes, 12);
            var ema26 = Indicators.Ema(closes, 26);
            var boll = Indicators.BollingerBands(closes);
            decimal? bbPos = null;
            if (boll is not null && boll.Value.Upper > boll.Value.Lower)
            {
                var pos = (closes[^1] - boll.Value.Lower) / (boll.Value.Upper - boll.Value.Lower);
                bbPos = Math.Max(0m, Math.Min(1m, pos));
            }

            if (ret3 is not null && volZ is not null) momXVol = ret3.Value * volZ.Value;
            if (ret3 is not null && bbPos is not null) posXMom = (bbPos.Value - 0.5m) * ret3.Value;
            if (ret3 is not null && atr is not null && atr.Value > 0m && closes[^1] > 0m)
                regimeXMom = (atr.Value / closes[^1]) * ret3.Value;
            if (ret3 is not null && ema12 is not null && ema26 is not null && atr is not null && atr.Value > 0m)
                trendXMom = ((ema12.Value - ema26.Value) / atr.Value) * ret3.Value;
            if (volZ is not null && bbPos is not null) volXRange = volZ.Value * (bbPos.Value - 0.5m);
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["mom_x_vol"] = momXVol,
            ["pos_x_mom"] = posXMom,
            ["regime_x_mom"] = regimeXMom,
            ["trend_x_mom"] = trendXMom,
            ["vol_x_range"] = volXRange,
        });
    }
}

/// <summary>
/// Multi-horizon momentum. Log returns at lags 1, 3, 5, 10, 20. A single `logret` field captures
/// only the most recent candle's move — momentum at multiple horizons lets the model see whether
/// short-term and medium-term moves agree (continuation) or disagree (reversal-setup). These are
/// already stationary (returns, not levels), so no further normalisation needed.
///
/// Warmup: 20 candles. Returns null for any lag with insufficient history.
/// </summary>
public sealed class MomentumPackNode : IFlowNode
{
    public string TypeId => "indicator.momentum_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("ret_1",  "decimal?"),
            new PortDef("ret_3",  "decimal?"),
            new PortDef("ret_5",  "decimal?"),
            new PortDef("ret_10", "decimal?"),
            new PortDef("ret_20", "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        var closes = candles.Select(c => c.Close).ToList();
        decimal? RetAtLag(int lag)
            => closes.Count > lag && closes[^(lag + 1)] > 0m
                ? Indicators.LogReturn(closes[^(lag + 1)], closes[^1])
                : null;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["ret_1"] = RetAtLag(1),
            ["ret_3"] = RetAtLag(3),
            ["ret_5"] = RetAtLag(5),
            ["ret_10"] = RetAtLag(10),
            ["ret_20"] = RetAtLag(20),
        });
    }
}

/// <summary>
/// Stationary, vol-normalised features. The plain feature_pack outputs (ema12, ema26, bbU, bbL,
/// macd, signal) are *raw price levels* — they drift with BTC's price and force a logistic
/// regression to memorise price ranges rather than learn signal. norm_pack expresses the same
/// underlying information in stationary form: distances normalised by ATR (vol units) or
/// expressed as ratios. Outputs:
///
/// <list type="bullet">
///   <item><c>px_vs_ema12_atr</c> — (close - EMA12) / ATR14. How far above/below short-term trend, in vol units.</item>
///   <item><c>px_vs_ema26_atr</c> — (close - EMA26) / ATR14. Distance from medium-term trend, vol units.</item>
///   <item><c>ema_spread_atr</c> — (EMA12 - EMA26) / ATR14. Trend strength + direction, vol units.</item>
///   <item><c>bb_pos</c> — (close - bbL) / (bbU - bbL), clamped to [0, 1]. 0 = at lower band, 1 = at upper band.</item>
///   <item><c>atr_pct</c> — ATR14 / close. Volatility as fraction of price (regime indicator).</item>
/// </list>
///
/// All nulls during warmup (≥ 26 candles for the slow EMA + ATR).
/// </summary>
public sealed class NormPackNode : IFlowNode
{
    public string TypeId => "indicator.norm_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("px_vs_ema12_atr", "decimal?"),
            new PortDef("px_vs_ema26_atr", "decimal?"),
            new PortDef("ema_spread_atr",  "decimal?"),
            new PortDef("bb_pos",          "decimal?"),
            new PortDef("atr_pct",         "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        decimal? pxVsE12 = null, pxVsE26 = null, emaSpread = null, bbPos = null, atrPct = null;

        if (candles.Count >= 26)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var ema12 = Indicators.Ema(closes, 12);
            var ema26 = Indicators.Ema(closes, 26);
            var atr = Indicators.Atr(candles, 14);
            var boll = Indicators.BollingerBands(closes);
            var last = closes[^1];

            if (atr is not null && atr.Value > 0m)
            {
                if (ema12 is not null) pxVsE12 = (last - ema12.Value) / atr.Value;
                if (ema26 is not null) pxVsE26 = (last - ema26.Value) / atr.Value;
                if (ema12 is not null && ema26 is not null) emaSpread = (ema12.Value - ema26.Value) / atr.Value;
                atrPct = atr.Value / last;
            }
            if (boll is not null)
            {
                var range = boll.Value.Upper - boll.Value.Lower;
                if (range > 0m)
                {
                    var pos = (last - boll.Value.Lower) / range;
                    bbPos = Math.Max(0m, Math.Min(1m, pos));
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["px_vs_ema12_atr"] = pxVsE12,
            ["px_vs_ema26_atr"] = pxVsE26,
            ["ema_spread_atr"] = emaSpread,
            ["bb_pos"] = bbPos,
            ["atr_pct"] = atrPct,
        });
    }
}

/// <summary>
/// Volume-derived features. Per the iteration log, volume z-score is the strongest single
/// non-price feature on short-horizon BTC direction prediction. Four outputs:
///
/// <list type="bullet">
///   <item><c>vol_z20</c> — 20-bar z-score of the latest candle's volume. Captures volume surges
///     (z &gt; 1.5) that often accompany trend continuation, and dead zones (z &lt; -1) that
///     correlate with mean-reversion.</item>
///   <item><c>obv_z20</c> — z-score of On-Balance Volume over the last 20 OBV samples. Raw OBV
///     drifts monotonically; the z-score is the stationary form. Positive = accumulation,
///     negative = distribution.</item>
///   <item><c>vol_vs_range</c> — volume / (high - low). High value = lots of volume traded in a
///     narrow range (absorption, supply/demand at price), low value = volume spreads price
///     (impulsive move).</item>
///   <item><c>up_vol_ratio</c> — sum of volume on green candles divided by total volume over last
///     20 bars. 0.5 = balanced, &gt; 0.5 = bull-volume dominance, &lt; 0.5 = bear-volume dominance.</item>
/// </list>
///
/// All four return <c>null</c> until the warmup window (≥ 20 candles) is filled.
/// </summary>
public sealed class VolumePackNode : IFlowNode
{
    public string TypeId => "indicator.volume_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("vol_z20",      "decimal?"),
            new PortDef("obv_z20",      "decimal?"),
            new PortDef("vol_vs_range", "decimal?"),
            new PortDef("up_vol_ratio", "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        decimal? volZ = null, obvZ = null, volVsRange = null, upVolRatio = null;

        if (candles.Count >= 20)
        {
            var volumes = candles.Select(c => c.Volume).ToList();
            volZ = Indicators.ZScore(volumes, 20);

            var obv = Indicators.Obv(candles);
            if (obv.Count >= 20) obvZ = Indicators.ZScore(obv, 20);

            var last = candles[^1];
            var range = last.High - last.Low;
            volVsRange = range > 0m ? last.Volume / range : 0m;

            decimal upVol = 0m, totalVol = 0m;
            for (var i = candles.Count - 20; i < candles.Count; i++)
            {
                totalVol += candles[i].Volume;
                if (candles[i].Close >= candles[i].Open) upVol += candles[i].Volume;
            }
            upVolRatio = totalVol > 0m ? upVol / totalVol : 0.5m;
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["vol_z20"] = volZ,
            ["obv_z20"] = obvZ,
            ["vol_vs_range"] = volVsRange,
            ["up_vol_ratio"] = upVolRatio,
        });
    }
}

/// <summary>
/// Intraday/seasonality features for the candle being predicted. Reads <c>ctx.TargetOpenTime</c>
/// (the open time of the target candle) directly — that timestamp is known the instant the bet is
/// placed, so these features are perfectly causal and carry zero look-ahead risk (no candle data
/// is consulted at all). BTC has well-documented session structure (Asia / EU / US handoffs,
/// weekend thinness) that a 5m model can exploit but the general v6 ignores.
///
/// Hour-of-day and day-of-week are encoded as sin/cos pairs so the model sees them as cyclical
/// (23:00 is adjacent to 00:00) rather than as a discontinuous integer. Session flags are coarse
/// UTC windows; the weekend flag captures the lower-liquidity regime.
/// </summary>
public sealed class TemporalPackNode : IFlowNode
{
    public string TypeId => "indicator.temporal_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[]
        {
            new PortDef("hour_sin",      "decimal"),
            new PortDef("hour_cos",      "decimal"),
            new PortDef("dow_sin",       "decimal"),
            new PortDef("dow_cos",       "decimal"),
            new PortDef("is_us_session", "decimal"),
            new PortDef("is_eu_session", "decimal"),
            new PortDef("is_weekend",    "decimal"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ctx.TargetOpenTime).UtcDateTime;
        // Fractional hour so a 5m candle at 13:35 sits between the 13:00 and 14:00 phase points
        // rather than snapping to a coarse integer hour.
        var hourFrac = t.Hour + t.Minute / 60.0;
        var dow = (int)t.DayOfWeek; // 0 = Sunday .. 6 = Saturday
        var hourSin = (decimal)Math.Sin(2 * Math.PI * hourFrac / 24.0);
        var hourCos = (decimal)Math.Cos(2 * Math.PI * hourFrac / 24.0);
        var dowSin = (decimal)Math.Sin(2 * Math.PI * dow / 7.0);
        var dowCos = (decimal)Math.Cos(2 * Math.PI * dow / 7.0);
        // Coarse UTC session windows: EU ~07:00-16:00, US ~13:00-21:00 (overlap is the high-vol band).
        var isEu = t.Hour is >= 7 and < 16 ? 1m : 0m;
        var isUs = t.Hour is >= 13 and < 21 ? 1m : 0m;
        var isWeekend = dow is 0 or 6 ? 1m : 0m;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["hour_sin"] = hourSin,
            ["hour_cos"] = hourCos,
            ["dow_sin"] = dowSin,
            ["dow_cos"] = dowCos,
            ["is_us_session"] = isUs,
            ["is_eu_session"] = isEu,
            ["is_weekend"] = isWeekend,
        });
    }
}

/// <summary>
/// Higher-timeframe regime context for a 5m model, computed from the <b>15m</b> candle series fed
/// in via a second <c>source.binance.klines</c> node (<c>tf:"15m"</c>). In backtest/training the
/// slice provider serves those 15m candles filtered by close-time, so a still-forming 15m bar can
/// never leak — the model only ever sees fully-closed higher-tf context.
///
/// Direction at 5m is strongly conditioned by the prevailing 15m regime (trend vs chop, vol level,
/// overbought/oversold). All outputs are stationary (vol-normalised or bounded). Nulls until the
/// 15m series has ≥ 26 candles.
/// </summary>
public sealed class HtfRegimePackNode : IFlowNode
{
    public string TypeId => "indicator.htf_regime_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("htf_ema_spread_atr",  "decimal?"),
            new PortDef("htf_px_vs_ema26_atr", "decimal?"),
            new PortDef("htf_atr_pct",         "decimal?"),
            new PortDef("htf_rsi",             "decimal?"),
            new PortDef("htf_ret_4",           "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        decimal? emaSpread = null, pxVsE26 = null, atrPct = null, rsi = null, ret4 = null;

        if (candles.Count >= 26)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var ema12 = Indicators.Ema(closes, 12);
            var ema26 = Indicators.Ema(closes, 26);
            var atr = Indicators.Atr(candles, 14);
            var last = closes[^1];
            if (atr is not null && atr.Value > 0m)
            {
                if (ema12 is not null && ema26 is not null) emaSpread = (ema12.Value - ema26.Value) / atr.Value;
                if (ema26 is not null) pxVsE26 = (last - ema26.Value) / atr.Value;
                atrPct = atr.Value / last;
            }
            rsi = Indicators.Rsi(closes, 14);
            if (closes.Count > 4 && closes[^5] > 0m) ret4 = Indicators.LogReturn(closes[^5], closes[^1]);
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["htf_ema_spread_atr"] = emaSpread,
            ["htf_px_vs_ema26_atr"] = pxVsE26,
            ["htf_atr_pct"] = atrPct,
            ["htf_rsi"] = rsi,
            ["htf_ret_4"] = ret4,
        });
    }
}

/// <summary>
/// Sub-bar pressure for a 5m model, computed from the <b>1m</b> candle series fed in via a second
/// <c>source.binance.klines</c> node (<c>tf:"1m"</c>). The 1m candles leading into the target give
/// a finer-grained read on the run-up than the 5m bar alone: realised vol of 1m returns, the
/// fraction of up-closing minutes, and signed-volume skew (a cheap order-flow proxy without needing
/// trade-level data). All from candles strictly before the target open, so fully causal. Nulls
/// until ≥ <c>window</c> 1m candles are present.
/// </summary>
public sealed class SubBarPackNode : IFlowNode
{
    public string TypeId => "indicator.subbar_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("candles", "Candle[]") },
        Outputs: new[]
        {
            new PortDef("subbar_rvol",     "decimal?"),
            new PortDef("subbar_up_ratio", "decimal?"),
            new PortDef("subbar_vol_skew", "decimal?"),
            new PortDef("subbar_ret_5",    "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["window"] = new("int", false, 15, "Number of trailing 1m candles to summarise."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var candles = (inputs.GetValueOrDefault("candles") as IReadOnlyList<HistoricalCandle>) ?? Array.Empty<HistoricalCandle>();
        var window = Math.Max(5, NodeParams.GetInt(nodeParams, "window", 15));
        decimal? rvol = null, upRatio = null, volSkew = null, ret5 = null;

        if (candles.Count >= window)
        {
            var tail = candles.Skip(candles.Count - window).ToList();
            // Realised vol = sample std-dev of 1m log returns across the window.
            var rets = new List<decimal>(window);
            for (var i = 1; i < tail.Count; i++)
                if (tail[i - 1].Close > 0m) rets.Add(Indicators.LogReturn(tail[i - 1].Close, tail[i].Close));
            if (rets.Count >= 2)
            {
                var mean = rets.Average();
                decimal sq = 0m;
                foreach (var r in rets) sq += (r - mean) * (r - mean);
                rvol = (decimal)Math.Sqrt((double)(sq / rets.Count));
            }

            int upCount = 0;
            decimal upVol = 0m, downVol = 0m;
            foreach (var c in tail)
            {
                if (c.Close >= c.Open) { upCount++; upVol += c.Volume; }
                else downVol += c.Volume;
            }
            upRatio = (decimal)upCount / tail.Count;
            var totalVol = upVol + downVol;
            volSkew = totalVol > 0m ? (upVol - downVol) / totalVol : 0m;

            var last5 = candles.Skip(Math.Max(0, candles.Count - 6)).ToList();
            if (last5.Count >= 2 && last5[0].Close > 0m) ret5 = Indicators.LogReturn(last5[0].Close, last5[^1].Close);
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["subbar_rvol"] = rvol,
            ["subbar_up_ratio"] = upRatio,
            ["subbar_vol_skew"] = volSkew,
            ["subbar_ret_5"] = ret5,
        });
    }
}
