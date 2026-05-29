using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Markets;

/// <summary>
/// A price observation for a recurring prediction-market contract aligned to a specific candle
/// window. Rows are written when real venue prices are fetched and when synthetic-flat fallbacks
/// are generated. The (Venue, Symbol, Interval, TargetOpenTime, ObservedAt, MarketExternalId)
/// tuple is unique — the store picks the latest ObservedAt ≤ targetOpenTime for anti-look-ahead
/// entry-price resolution.
/// </summary>
public sealed class VenueMarketPrice : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Venue { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    /// <summary>Candle open time in UTC milliseconds — the candle this price row is aligned to.</summary>
    public required long TargetOpenTime { get; init; }
    /// <summary>Wall-clock time (UTC ms) at which the price was observed or synthesised.</summary>
    public required long ObservedAt { get; init; }
    /// <summary>Venue-side market identifier (e.g. Polymarket condition id).</summary>
    public required string MarketExternalId { get; init; }
    /// <summary>YES-outcome price ∈ (0,1) at ObservedAt. Equals 0.5 for synthetic-flat rows.</summary>
    public required decimal YesPrice { get; init; }
    /// <summary>NO-outcome price ∈ (0,1) at ObservedAt. Equals 0.5 for synthetic-flat rows.</summary>
    public required decimal NoPrice { get; init; }
    /// <summary>True when the price was not fetched from a live venue feed and must not be
    /// presented as a real market price.</summary>
    public required bool Synthetic { get; init; }
    /// <summary>Human-readable source tag, e.g. "polymarket-clob" or "synthetic-flat".</summary>
    public required string Source { get; init; }
    /// <summary>Market resolution window start in UTC ms.</summary>
    public long ResolutionWindowStart { get; init; }
    /// <summary>Market resolution window end in UTC ms.</summary>
    public long ResolutionWindowEnd { get; init; }
    /// <summary>The price-reference source the market resolves against (e.g. "Binance:BTCUSDT").</summary>
    public string? ReferenceSource { get; init; }
    /// <summary>Resolved outcome when known; null while unresolved.</summary>
    public bool? ResolvedOutcomeUp { get; set; }
}

/// <summary>
/// Alignment verdict between a backtest candle window and a prediction-market resolution window.
/// </summary>
public enum AlignmentVerdict
{
    /// <summary>Windows match exactly and the reference source matches the expected source.</summary>
    Exact,
    /// <summary>Windows match exactly but the reference source differs from the expected source
    /// (e.g. Binance vs Coinbase). Still usable but flagged for audit.</summary>
    Tolerated,
    /// <summary>Windows differ by any amount, or the reference source is null/empty/unknown.
    /// The market cannot safely be used for this candle.</summary>
    Mismatch
}

/// <summary>
/// Result of aligning a backtest candle to a prediction-market resolution window.
/// </summary>
public sealed record MarketAlignment(
    string Symbol,
    string Interval,
    long PredictedCandleOpenMs,
    long PredictedCandleCloseMs,
    string MarketExternalId,
    long MarketWindowStartMs,
    long MarketWindowEndMs,
    string? ReferenceSource,
    AlignmentVerdict Verdict);

/// <summary>
/// A snapshot of a recurring market's resolution window together with a set of price quotes.
/// </summary>
public sealed record VenueMarketWindow(
    string MarketExternalId,
    string YesTokenId,
    string NoTokenId,
    long ResolutionWindowStartMs,
    long ResolutionWindowEndMs,
    string? ReferenceSource,
    IReadOnlyList<VenuePriceQuote> Quotes);

/// <summary>One price observation at a specific wall-clock moment.</summary>
public sealed record VenuePriceQuote(
    long ObservedAtMs,
    decimal YesPrice,
    decimal NoPrice,
    bool Synthetic,
    string Source);

/// <summary>
/// The entry-price quote used at bet placement time — the minimum information required by
/// StakingEngine.Settle and Step to compute odds-based payoffs.
/// </summary>
public readonly record struct EntryQuote(
    decimal YesPrice,
    decimal NoPrice,
    bool Synthetic,
    string? MarketExternalId);
