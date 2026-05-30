using FrostAura.Foresight.Domain.MarketData;

namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Source of historical order-flow microstructure bars for backtesting — the parallel of
/// <see cref="IHistoricalCandleProvider"/> for the data that actually predicts short-horizon
/// direction (signed taker volume / CVD / large-order skew / trade intensity). The live adapter
/// reconstructs these from Binance historical aggregated-trades and caches them in
/// <c>historical_microstructure</c>; a backtest run wraps the provider in an anti-lookahead slice so
/// nodes only ever see bars whose CLOSE-time is at or before the bet-decision boundary.
///
/// Making this signal historical (not live-only) is what lets it be backtested at all — the whole
/// reason it was previously unreachable from a deterministic, backtestable flow.
/// </summary>
public interface IHistoricalMicrostructureProvider
{
    /// <summary>
    /// Returns microstructure bars with <c>OpenTime ∈ [startMs, endMs]</c> for the (symbol,
    /// interval), ordered by OpenTime. The adapter fills cache misses against the upstream source;
    /// callers see a populated range or an empty list when the data isn't available for that span.
    /// </summary>
    Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(string symbol, string interval,
        long startMs, long endMs, CancellationToken ct = default);
}
