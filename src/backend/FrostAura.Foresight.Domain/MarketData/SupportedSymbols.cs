namespace FrostAura.Foresight.Domain.MarketData;

/// <summary>
/// Single source of truth for which symbols + intervals the platform supports for live predictions
/// and backtesting. Every surface that accepts a (symbol, interval) — the per-card model picker, the
/// backtest form, the gap-filler scheduler, and the historical-cache warmer — consults this list so
/// the system has true referential integrity instead of relying on freetext typed by the user.
///
/// Promotion path: this becomes a `(tenant, symbol, interval)` registry the day a second tenant
/// joins or the day we ship more than one symbol.
/// </summary>
public static class SupportedSymbols
{
    public static readonly IReadOnlyList<string> All = new[] { "BTCUSDT" };
    // Scoped to the short-horizon candle bands that actually feed paper trading + the agentic
    // research pipeline. 1h/4h/1d were dropped because they served no real use case here and
    // doubled the cache warmer's work for no upside — multi-tf models that consume higher
    // timeframes can be re-added intentionally if the need surfaces.
    public static readonly IReadOnlyList<string> Intervals = new[] { "1m", "5m", "15m" };

    public static bool IsSupportedSymbol(string symbol) => All.Contains(symbol);
    public static bool IsSupportedInterval(string interval) => Intervals.Contains(interval);
}
