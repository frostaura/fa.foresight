namespace FrostAura.Foresight.Domain.MarketData;

/// <summary>
/// Cached Binance kline for backtesting. Tenant-agnostic — candles are global market data so any
/// tenant's backtest can hit the same row. PK is the natural Binance key (symbol, interval, openTime).
/// </summary>
public sealed class HistoricalCandle
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required long OpenTime { get; init; }
    public required decimal Open { get; init; }
    public required decimal High { get; init; }
    public required decimal Low { get; init; }
    public required decimal Close { get; init; }
    public required decimal Volume { get; init; }
}
