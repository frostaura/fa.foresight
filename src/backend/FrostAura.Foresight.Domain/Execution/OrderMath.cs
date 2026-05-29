namespace FrostAura.Foresight.Domain.Execution;

/// <summary>
/// Pure, stateless math for Polymarket CLOB V2 order sizing and tick rounding.
/// No I/O. All amounts are in the CLOB's 6-decimal integer space (multiply by 1e6).
/// </summary>
public static class OrderMath
{
    private const decimal Scale = 1_000_000m; // 6 dp — USDC / pUSD / CTF tokens

    /// <summary>
    /// Round a price DOWN to the nearest minimum tick size (mts).
    /// Polymarket requires prices to land exactly on a tick boundary.
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal mts)
    {
        if (mts <= 0m) return price;
        return Math.Floor(price / mts) * mts;
    }

    /// <summary>
    /// Compute the scaled integer makerAmount (USDC/pUSD in) for a BUY order.
    /// BUY: pay makerAmount collateral to receive takerAmount outcome tokens.
    /// makerAmount = floor(tickPrice × size × 1e6).
    /// </summary>
    public static long BuyMakerAmount(decimal tickPrice, decimal sizeShares)
        => (long)Math.Floor(tickPrice * sizeShares * Scale);

    /// <summary>
    /// Compute the scaled integer takerAmount (tokens out) for a BUY order.
    /// takerAmount = floor(size × 1e6).
    /// </summary>
    public static long BuyTakerAmount(decimal sizeShares)
        => (long)Math.Floor(sizeShares * Scale);

    /// <summary>
    /// Compute the scaled integer makerAmount (tokens in) for a SELL order.
    /// SELL: deliver makerAmount tokens to receive takerAmount collateral.
    /// makerAmount = floor(size × 1e6).
    /// </summary>
    public static long SellMakerAmount(decimal sizeShares)
        => (long)Math.Floor(sizeShares * Scale);

    /// <summary>
    /// Compute the scaled integer takerAmount (USDC/pUSD out) for a SELL order.
    /// takerAmount = floor(tickPrice × size × 1e6).
    /// </summary>
    public static long SellTakerAmount(decimal tickPrice, decimal sizeShares)
        => (long)Math.Floor(tickPrice * sizeShares * Scale);

    /// <summary>
    /// Returns true when sizeShares is below the minimum order size (mos).
    /// Orders below mos must be skipped — the CLOB will reject them.
    /// </summary>
    public static bool IsBelowMinOrderSize(decimal sizeShares, decimal mos)
        => mos > 0m && sizeShares < mos;

    /// <summary>
    /// Full BUY sizing: tick-round price, compute (makerAmount, takerAmount), check mos.
    /// Returns null when the order should be skipped (sub-mos after rounding).
    /// </summary>
    public static ClobAmounts? SizeBuy(decimal rawPrice, decimal sizeShares, decimal mts, decimal mos)
    {
        var tickPrice = RoundToTick(rawPrice, mts);
        if (tickPrice <= 0m) return null;
        if (IsBelowMinOrderSize(sizeShares, mos)) return null;
        return new ClobAmounts(BuyMakerAmount(tickPrice, sizeShares), BuyTakerAmount(sizeShares), tickPrice);
    }

    /// <summary>
    /// Full SELL sizing: tick-round price, compute (makerAmount, takerAmount), check mos.
    /// Returns null when the order should be skipped (sub-mos after rounding).
    /// </summary>
    public static ClobAmounts? SizeSell(decimal rawPrice, decimal sizeShares, decimal mts, decimal mos)
    {
        var tickPrice = RoundToTick(rawPrice, mts);
        if (tickPrice <= 0m) return null;
        if (IsBelowMinOrderSize(sizeShares, mos)) return null;
        return new ClobAmounts(SellMakerAmount(sizeShares), SellTakerAmount(tickPrice, sizeShares), tickPrice);
    }
}

/// <summary>Scaled integer amounts for a CLOB order together with the tick-rounded price used.</summary>
public sealed record ClobAmounts(long MakerAmount, long TakerAmount, decimal TickPrice);
