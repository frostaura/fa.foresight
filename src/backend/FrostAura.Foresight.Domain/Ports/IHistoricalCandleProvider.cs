using FrostAura.Foresight.Domain.MarketData;

namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Source of historical candles for backtesting. The live adapter wraps Binance with a Postgres
/// cache (historical_candles table). A backtest run can also wrap the provider in an anti-lookahead
/// slice so nodes only see candles up to the current target — see HistoricalSliceProvider.
/// </summary>
public interface IHistoricalCandleProvider
{
    /// <summary>
    /// Returns all candles with <c>OpenTime ∈ [startMs, endMs]</c>, contiguous and ordered. The
    /// adapter is responsible for filling cache misses against the upstream API; callers see a
    /// fully-populated range or an exception.
    /// </summary>
    Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval,
        long startMs, long endMs, CancellationToken ct = default);
}
