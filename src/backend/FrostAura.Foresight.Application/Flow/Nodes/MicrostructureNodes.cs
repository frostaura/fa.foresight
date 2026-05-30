using System.Text.Json;
using FrostAura.Foresight.Domain.MarketData;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Source node: fetches the last <c>limit</c> order-flow microstructure bars for the runtime
/// interval ending before <c>ctx.TargetOpenTime</c>, via <see cref="FlowContext.Microstructure"/>.
/// Mirrors <c>source.binance.klines</c> exactly, including the <c>endMs = TargetOpenTime - 1</c>
/// window so it can never include the target's own (not-yet-closed) bar. In backtest the provider is
/// a boundary-clamped slice, so a still-forming bar can't leak. Emits an empty series when no
/// microstructure provider is wired (the downstream pack then yields nulls and the matrix isn't
/// ready) — that keeps candle-only flows and backtests-without-microstructure working unchanged.
/// </summary>
public sealed class MicrostructureSourceNode : IFlowNode
{
    public string TypeId => "source.microstructure.orderflow";

    public NodePortSpec Spec { get; } = new(
        Category: "data",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[] { new PortDef("bars", "MicroBar[]") },
        Params: new Dictionary<string, ParamDef>
        {
            ["limit"] = new("int", false, 60, "Number of microstructure bars ending at the target."),
        });

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        if (ctx.Microstructure is null)
            return new Dictionary<string, object?> { ["bars"] = Array.Empty<MicrostructureBar>() };

        var limit = NodeParams.GetInt(nodeParams, "limit", 60);
        var intervalMs = ToIntervalMs(ctx.Interval);
        var endMs = ctx.TargetOpenTime - 1;
        var startMs = endMs - (long)limit * intervalMs;
        var bars = await ctx.Microstructure.GetRangeAsync(ctx.Symbol, ctx.Interval, startMs, endMs, ct);
        return new Dictionary<string, object?> { ["bars"] = bars };
    }

    private static long ToIntervalMs(string interval) => interval switch
    {
        "1m" => 60_000L,
        "5m" => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };
}

/// <summary>
/// Order-flow feature pack. From the trailing microstructure-bar series it derives the signals that
/// actually carry short-horizon directional information — taker-volume imbalance, CVD momentum,
/// large-order skew, and trade intensity — all stationary (ratios or z-scores), so they generalise
/// across price regimes. Nulls until the window has enough bars (≥ 20 for the z-scores).
///
/// <list type="bullet">
///   <item><c>of_imbalance</c> — (buyVol − sellVol)/(buyVol + sellVol) on the last bar ∈ [-1, 1].</item>
///   <item><c>of_count_imbalance</c> — (buyTrades − sellTrades)/tradeCount on the last bar.</item>
///   <item><c>of_large_skew</c> — (largeBuy − largeSell)/(largeBuy + largeSell): whale aggressor side.</item>
///   <item><c>of_cvd_z</c> — z-score (20-bar) of per-bar signed volume (buyVol − sellVol): flow momentum.</item>
///   <item><c>of_intensity_z</c> — z-score (20-bar) of trade count: bursts of activity precede moves.</item>
/// </list>
/// </summary>
public sealed class OrderFlowPackNode : IFlowNode
{
    public string TypeId => "indicator.orderflow_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("bars", "MicroBar[]") },
        Outputs: new[]
        {
            new PortDef("of_imbalance",       "decimal?"),
            new PortDef("of_count_imbalance", "decimal?"),
            new PortDef("of_large_skew",      "decimal?"),
            new PortDef("of_cvd_z",           "decimal?"),
            new PortDef("of_intensity_z",     "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var bars = (inputs.GetValueOrDefault("bars") as IReadOnlyList<MicrostructureBar>) ?? Array.Empty<MicrostructureBar>();
        decimal? imbalance = null, countImbalance = null, largeSkew = null, cvdZ = null, intensityZ = null;

        if (bars.Count >= 20)
        {
            var last = bars[^1];
            var totalVol = last.BuyVolume + last.SellVolume;
            if (totalVol > 0m) imbalance = (last.BuyVolume - last.SellVolume) / totalVol;
            if (last.TradeCount > 0)
            {
                var sellTrades = last.TradeCount - last.BuyTradeCount;
                countImbalance = (decimal)(last.BuyTradeCount - sellTrades) / last.TradeCount;
            }
            var largeTotal = last.LargeBuyVolume + last.LargeSellVolume;
            if (largeTotal > 0m) largeSkew = (last.LargeBuyVolume - last.LargeSellVolume) / largeTotal;

            var signed = bars.Select(b => b.BuyVolume - b.SellVolume).ToList();
            cvdZ = Indicators.ZScore(signed, 20);
            var counts = bars.Select(b => (decimal)b.TradeCount).ToList();
            intensityZ = Indicators.ZScore(counts, 20);
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["of_imbalance"] = imbalance,
            ["of_count_imbalance"] = countImbalance,
            ["of_large_skew"] = largeSkew,
            ["of_cvd_z"] = cvdZ,
            ["of_intensity_z"] = intensityZ,
        });
    }
}

/// <summary>
/// Intra-bar (high-frequency) order-flow pack — the structure the whole-bar order-flow aggregates
/// discard. The whole point of the v6→v1 ceiling work: ~53% held across horizon and feature class
/// because every signal was a single per-5m aggregate. These features read the LATE (final 20%) vs
/// EARLY (first 20%) flow within the anchor bar — the freshest aggressor pressure at the decision
/// boundary, and whether it's building or fading into the close. Deliberately just THREE features
/// (overfit defence — the same parsimony the order-flow + derivatives packs use):
/// <list type="bullet">
///   <item><c>mf_late_imbalance</c> — taker (buy−sell)/(buy+sell) over the final 20% of the bar.</item>
///   <item><c>mf_imbalance_accel</c> — late imbalance minus early imbalance: is aggressive flow accelerating into the close.</item>
///   <item><c>mf_late_intensity</c> — late trade-count relative to a uniform split (lateCount/(0.2·total) − 1): a late activity burst.</item>
/// </list>
/// Strictly causal: every tick is inside the closed anchor bar. Nulls (and an unready matrix) when
/// the bar lacks intra-bar fields (rows aggregated before this existed) — abstains cleanly.
/// </summary>
public sealed class MicroFlowPackNode : IFlowNode
{
    public string TypeId => "indicator.microflow_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("bars", "MicroBar[]") },
        Outputs: new[]
        {
            new PortDef("mf_late_imbalance",  "decimal?"),
            new PortDef("mf_imbalance_accel", "decimal?"),
            new PortDef("mf_late_intensity",  "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var bars = (inputs.GetValueOrDefault("bars") as IReadOnlyList<MicrostructureBar>) ?? Array.Empty<MicrostructureBar>();
        decimal? lateImb = null, imbAccel = null, lateIntensity = null;

        if (bars.Count >= 1)
        {
            var last = bars[^1];
            // All intra-bar fields are populated together at aggregation time, so a single null check
            // gates the whole pack (old rows aggregated before these existed → abstain).
            if (last.LateBuyVolume is { } lb && last.LateSellVolume is { } ls &&
                last.EarlyBuyVolume is { } eb && last.EarlySellVolume is { } es &&
                last.LateTradeCount is { } ltc)
            {
                var lateTot = lb + ls;
                if (lateTot > 0m) lateImb = (lb - ls) / lateTot;
                var earlyTot = eb + es;
                if (lateTot > 0m && earlyTot > 0m)
                    imbAccel = (lb - ls) / lateTot - (eb - es) / earlyTot;
                if (last.TradeCount > 0)
                    lateIntensity = (decimal)ltc / (0.2m * last.TradeCount) - 1m;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["mf_late_imbalance"] = lateImb,
            ["mf_imbalance_accel"] = imbAccel,
            ["mf_late_intensity"] = lateIntensity,
        });
    }
}

/// <summary>
/// Futures derivatives pack — the orthogonal positioning/flow signals from the perp `metrics` dump
/// (open interest + long/short ratios), carried on the microstructure bars. These lead short-horizon
/// BTC direction in ways spot candles + spot order-flow can't see: OI momentum (conviction behind a
/// move), OI surprise, smart-money (top-trader) positioning, aggressive taker flow, and crowd
/// positioning. Deliberately parsimonious (5 features) and stationary (log-ratios / z-scores) to
/// resist overfitting. Nulls until ≥ 20 bars with metrics; abstains cleanly when metrics are absent.
///
/// <list type="bullet">
///   <item><c>d_oi_mom</c> — log(OI[t] / OI[t-3]): 3-bar open-interest momentum.</item>
///   <item><c>d_oi_z</c> — 20-bar z-score of per-bar OI log-change: position-building surprise.</item>
///   <item><c>d_toptrader_ls</c> — ln(top-trader long/short position ratio): smart-money lean.</item>
///   <item><c>d_taker_ls</c> — ln(taker buy/sell volume ratio): aggressive futures flow direction.</item>
///   <item><c>d_ls_ratio</c> — ln(global long/short account ratio): crowd positioning (often contrarian).</item>
/// </list>
/// </summary>
public sealed class DerivativesPackNode : IFlowNode
{
    public string TypeId => "indicator.derivatives_pack";

    public NodePortSpec Spec { get; } = new(
        Category: "indicator",
        Inputs: new[] { new PortDef("bars", "MicroBar[]") },
        Outputs: new[]
        {
            new PortDef("d_oi_mom",       "decimal?"),
            new PortDef("d_oi_z",         "decimal?"),
            new PortDef("d_toptrader_ls", "decimal?"),
            new PortDef("d_taker_ls",     "decimal?"),
            new PortDef("d_ls_ratio",     "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var bars = (inputs.GetValueOrDefault("bars") as IReadOnlyList<MicrostructureBar>) ?? Array.Empty<MicrostructureBar>();
        decimal? oiMom = null, oiZ = null, topLs = null, takerLs = null, lsRatio = null;

        if (bars.Count >= 20)
        {
            var last = bars[^1];
            static decimal? Ln(decimal? r) => r is > 0m ? (decimal)Math.Log((double)r.Value) : null;
            topLs = Ln(last.TopTraderLongShortRatio);
            takerLs = Ln(last.TakerLongShortVolRatio);
            lsRatio = Ln(last.LongShortRatio);

            if (bars.Count >= 4 && bars[^4].OpenInterest is > 0m && last.OpenInterest is > 0m)
                oiMom = (decimal)Math.Log((double)(last.OpenInterest!.Value / bars[^4].OpenInterest!.Value));

            // Per-bar OI log-change series (only across consecutive bars that both carry OI), z-scored.
            var deltas = new List<decimal>();
            for (var i = 1; i < bars.Count; i++)
                if (bars[i - 1].OpenInterest is > 0m && bars[i].OpenInterest is > 0m)
                    deltas.Add((decimal)Math.Log((double)(bars[i].OpenInterest!.Value / bars[i - 1].OpenInterest!.Value)));
            if (deltas.Count >= 20) oiZ = Indicators.ZScore(deltas, 20);
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["d_oi_mom"] = oiMom,
            ["d_oi_z"] = oiZ,
            ["d_toptrader_ls"] = topLs,
            ["d_taker_ls"] = takerLs,
            ["d_ls_ratio"] = lsRatio,
        });
    }
}
