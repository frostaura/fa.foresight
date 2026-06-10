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
    // research pipeline. 4h/1d were dropped because they served no real use case here and
    // doubled the cache warmer's work for no upside. 1h was re-added intentionally (2026-06-10)
    // as the htf_regime_pack source for the 15m v3-bag model — it must be in this list so the
    // backtest/trainer/chaos off-tf hydration loops pool 1h candles; warm cost is negligible
    // (24 candles/day).
    public static readonly IReadOnlyList<string> Intervals = new[] { "1m", "5m", "15m", "1h" };

    public static bool IsSupportedSymbol(string symbol) => All.Contains(symbol);
    public static bool IsSupportedInterval(string interval) => Intervals.Contains(interval);
}
