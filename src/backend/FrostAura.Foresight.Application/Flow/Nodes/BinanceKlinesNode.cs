using System.Text.Json;
using FrostAura.Foresight.Domain.MarketData;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Source node: fetches the last <c>limit</c> candles for the configured timeframe ending at
/// <c>ctx.TargetOpenTime</c>. The <c>tf</c> param can be a literal interval ("1m", "5m", "15m") or
/// the special sentinel <c>"target"</c> which resolves to the runtime <c>ctx.Interval</c> — that's
/// what lets a single flow definition operate across cards on different intervals.
///
/// Both live and backtest mode route through <c>FlowContext.HistoricalCandles</c>. In live, the
/// adapter happens to be the same Postgres-backed Binance fetcher; in backtest, it's the slice
/// provider that withholds future candles to enforce anti-lookahead.
/// </summary>
public sealed class BinanceKlinesNode : IFlowNode
{
    public string TypeId => "source.binance.klines";

    public NodePortSpec Spec { get; } = new(
        Category: "data",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[] { new PortDef("candles", "Candle[]") },
        Params: new Dictionary<string, ParamDef>
        {
            ["tf"] = new("string", false, "target",
                "Interval to fetch. \"target\" resolves to the runtime card interval."),
            ["limit"] = new("int", false, 60,
                "Number of candles ending at the target. Capped at 1000 by Binance."),
        });

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        JsonElement nodeParams,
        FlowContext ctx,
        CancellationToken ct)
    {
        var tf = NodeParams.GetString(nodeParams, "tf", "target");
        var limit = NodeParams.GetInt(nodeParams, "limit", 60);
        if (string.Equals(tf, "target", StringComparison.OrdinalIgnoreCase)) tf = ctx.Interval;

        // Compute the window: `limit` candles ending at (and including) the candle BEFORE the
        // target. The anchor close is the target candle's open — i.e. the previous candle's close.
        var intervalMs = ToIntervalMs(tf);
        var endMs = ctx.TargetOpenTime - 1;        // exclusive of the target candle
        var startMs = endMs - (long)limit * intervalMs;

        var candles = await ctx.HistoricalCandles.GetRangeAsync(ctx.Symbol, tf, startMs, endMs, ct);
        return new Dictionary<string, object?> { ["candles"] = candles };
    }

    /// <summary>Mirror of BinanceMarketDataClient.IntervalMs so the Application project doesn't pull Infrastructure.</summary>
    private static long ToIntervalMs(string interval) => interval switch
    {
        "1m" => 60_000L,
        "5m" => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };
}
