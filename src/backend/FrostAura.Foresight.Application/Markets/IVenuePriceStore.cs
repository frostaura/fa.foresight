using FrostAura.Foresight.Domain.Markets;

namespace FrostAura.Foresight.Application.Markets;

/// <summary>
/// Port for fetching the anti-look-ahead entry-price quote used at bet placement time. The entry is
/// modelled as a fixed conservative fee/price (per-tenant connection config) for these near-50/50
/// BTC up/down markets — not a fetched live-odds book.
/// </summary>
public interface IVenuePriceStore
{
    /// <summary>
    /// Returns the entry quote for a given (venue, symbol, interval, targetOpenTime). The quote
    /// is the row with the latest ObservedAt that is strictly ≤ targetOpenTime (anti-look-ahead).
    /// Returns null when no row exists yet; prefer <see cref="EnsureEntryAsync"/> to always obtain one.
    /// </summary>
    Task<EntryQuote?> GetEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, CancellationToken ct);

    /// <summary>
    /// Returns the entry quote, creating a fixed-fee row when none exists. Both YES and NO are set to
    /// the tenant's conservative <c>EffectivePrice</c> (default 0.55) — the SAME price for either side,
    /// because the fee is symmetric. Rows are persisted with <c>Synthetic=false, Source="fixed-fee"</c>:
    /// the fixed conservative price is the canonical entry, so <c>edge = sideProb − price</c> is honest.
    /// <paramref name="pUp"/> is accepted for signature compatibility but is NOT used for pricing.
    /// </summary>
    Task<EntryQuote> EnsureEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, decimal pUp, CancellationToken ct);
}
