using FrostAura.Foresight.Domain.Markets;

namespace FrostAura.Foresight.Application.Markets;

/// <summary>
/// Port for fetching the anti-look-ahead entry-price quote used at bet placement time.
/// Implementations resolve the most-recently-observed real price for the given candle, or
/// fall back to a synthetic-flat row when no real price is available.
/// </summary>
public interface IVenuePriceStore
{
    /// <summary>
    /// Returns the entry quote for a given (venue, symbol, interval, targetOpenTime). The quote
    /// is the row with the latest ObservedAt that is strictly ≤ targetOpenTime (anti-look-ahead).
    /// Returns null when no row exists and no pUp is available to synthesise one; prefer
    /// <see cref="EnsureEntryAsync"/> to always obtain an entry.
    /// </summary>
    Task<EntryQuote?> GetEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, CancellationToken ct);

    /// <summary>
    /// Returns the entry quote, creating a synthetic-flat row when no real row exists. The
    /// synthetic YES price is clamped and calibrated from <paramref name="pUp"/>:
    /// <c>yes = clamp(0.5 + (pUp - 0.5) * 0.8, 0.02, 0.98)</c>, no = 1 - yes.
    /// The row is persisted with Synthetic=true, Source="synthetic-flat" so downstream consumers
    /// can always distinguish real from synthetic data.
    /// </summary>
    Task<EntryQuote> EnsureEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, decimal pUp, CancellationToken ct);
}
