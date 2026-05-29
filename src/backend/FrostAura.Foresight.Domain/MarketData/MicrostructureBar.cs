namespace FrostAura.Foresight.Domain.MarketData;

/// <summary>
/// Per-bar aggregated order-flow microstructure for one (symbol, interval, openTime), reconstructed
/// from Binance historical aggregated-trades. Stored at the SAME interval as the candles it aligns
/// to (one microstructure bar per candle) so a deterministic flow can join them by open time.
///
/// We persist only the cheap aggregates — never raw trades — so the table stays small (one row per
/// candle, same cardinality as historical_candles). The flow nodes derive the actual features
/// (imbalance, CVD, large-order skew, trade intensity) and any trailing normalisation from these.
///
/// Taker convention: a trade where the BUYER is the maker (<c>isBuyerMaker = true</c>) is a taker
/// SELL (an incoming market sell hit a resting bid); otherwise it's a taker BUY. So
/// <see cref="BuyVolume"/>/<see cref="SellVolume"/> are taker-aggressor volumes — the signed order
/// flow that actually moves price, which is the whole point of this signal.
/// </summary>
public sealed class MicrostructureBar
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    /// <summary>Open time of the aligned candle (ms). Matches HistoricalCandle.OpenTime exactly.</summary>
    public required long OpenTime { get; init; }

    /// <summary>Number of aggregated trades in the bar.</summary>
    public required long TradeCount { get; init; }
    /// <summary>Taker-buy base-asset volume (aggressive buys).</summary>
    public required decimal BuyVolume { get; init; }
    /// <summary>Taker-sell base-asset volume (aggressive sells).</summary>
    public required decimal SellVolume { get; init; }
    /// <summary>Count of taker-buy aggregated trades (count-side imbalance, robust to size outliers).</summary>
    public required long BuyTradeCount { get; init; }
    /// <summary>Taker-buy base volume from "large" trades (qty ≥ the adapter's large-trade threshold).</summary>
    public required decimal LargeBuyVolume { get; init; }
    /// <summary>Taker-sell base volume from "large" trades.</summary>
    public required decimal LargeSellVolume { get; init; }

    // --- Intra-bar order-flow (the high-frequency structure the whole-bar aggregates throw away) ---
    // Computed from the SAME ticks, split by each trade's position within the bar. "Late" = final 20%
    // of the bar (freshest flow at the decision boundary), "Early" = first 20% (for acceleration).
    // Strictly causal: every tick is inside the closed anchor bar, so these leak nothing about the
    // future. Nullable — rows aggregated before these existed read null and the microflow pack
    // abstains, exactly like the derivatives metrics do.
    /// <summary>Taker-buy base volume in the final 20% of the bar.</summary>
    public decimal? LateBuyVolume { get; init; }
    /// <summary>Taker-sell base volume in the final 20% of the bar.</summary>
    public decimal? LateSellVolume { get; init; }
    /// <summary>Taker-buy base volume in the first 20% of the bar.</summary>
    public decimal? EarlyBuyVolume { get; init; }
    /// <summary>Taker-sell base volume in the first 20% of the bar.</summary>
    public decimal? EarlySellVolume { get; init; }
    /// <summary>Number of aggregated trades in the final 20% of the bar (late-burst intensity).</summary>
    public long? LateTradeCount { get; init; }

    // --- Futures derivatives metrics (Binance UM `metrics` dump, 5m cadence) ---------------------
    // Orthogonal to spot candles + spot order-flow: these describe perp positioning + aggressive
    // futures flow, which lead short-horizon BTC direction. Nullable — a bar may have order-flow
    // (spot aggTrades) but no metrics row (or vice-versa); the derivatives pack abstains on nulls.
    /// <summary>Perp open interest (contracts) at the bar's metric timestamp.</summary>
    public decimal? OpenInterest { get; init; }
    /// <summary>Perp open interest notional (USD).</summary>
    public decimal? OpenInterestValue { get; init; }
    /// <summary>Top-trader long/short ACCOUNT ratio (smart-money positioning).</summary>
    public decimal? TopTraderLongShortRatio { get; init; }
    /// <summary>Global long/short account ratio.</summary>
    public decimal? LongShortRatio { get; init; }
    /// <summary>Taker buy/sell VOLUME ratio (aggressive futures flow direction).</summary>
    public decimal? TakerLongShortVolRatio { get; init; }
}
