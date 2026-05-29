using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Paper;

/// <summary>
/// Server-side paper-trading session. One active session per (tenant, symbol, interval) at a time
/// — a background processor settles open bets the moment their candle closes and opens a new bet
/// at every candle boundary while the session is live, so trading continues even when no client UI
/// is open. The session ends only when the user explicitly stops it OR when Martingale escalation
/// breaches the bankroll (`Bust = true`).
/// </summary>
public sealed class PaperSession : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    /// <summary>
    /// Distinguishes multiple ACTIVE sessions on the same (tenant, symbol, interval). Empty string =
    /// the "primary" session the chart UI drives via its Start button. Bot-created comparison sessions
    /// carry a non-empty label (e.g. the strategy id) so several can run in parallel on one market.
    /// </summary>
    public string Label { get; init; } = "";
    /// <summary>
    /// Stable SHA-256 config hash over {venue,symbol,interval,strategyId,initialBalance,initialBetSize}
    /// EXCLUDING mode. Mirrors the same hash computed by LiveSessionEngine so the cross-mode dedup check
    /// can compare paper and live sessions in a single query. Nullable for sessions created before this
    /// column existed (pre-Phase-E2 rows).
    /// </summary>
    public string? ConfigHash { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>Set when the user clicks Stop or when the session goes bust. While null, the
    /// background processor will continue ticking this session.</summary>
    public DateTimeOffset? StoppedAt { get; set; }
    public required decimal InitialBalance { get; init; }
    public required decimal InitialBetSize { get; init; }
    /// <summary>
    /// Stable kebab-case id of the staking strategy driving this session's bet-size dynamic — see
    /// <see cref="StakingStrategies"/>. Picked at start time and immutable thereafter. Defaults to
    /// <c>flat</c> at the API boundary; the DB column has a default of <c>martingale</c> for
    /// backward compatibility with pre-selector rows whose engine was implicitly Martingale.
    /// </summary>
    public required string StrategyId { get; init; }
    /// <summary>
    /// When true, the session applies the confidence gate: the processor SKIPS placing a bet on
    /// candles whose calibrated pUp sits in the ±2pp no-bet band (the same equation the backtest
    /// gate + chart GATE use). Off by default = bet every candle (the always-bet baseline). Picked
    /// at start time and immutable thereafter.
    /// </summary>
    public bool Gated { get; init; }
    public decimal CurrentBalance { get; set; }
    public decimal CurrentBetSize { get; set; }
    public bool Bust { get; set; }
    /// <summary>
    /// How many times the bankroll crossed zero in either direction during this session. With the
    /// live bust rule active this is usually 0 — surfaced for parity with backtests where bankruptcy
    /// is disabled and the count is meaningful.
    /// </summary>
    public int ZeroCrossingsCount { get; set; }
    /// <summary>Maximum |negative balance| reached during the session.</summary>
    public decimal PeakBorrowed { get; set; }
    /// <summary>Last processor tick that touched this session — used for telemetry / debugging
    /// only. Real progress lives on the bets themselves.</summary>
    public DateTimeOffset? LastProcessedAt { get; set; }

    public List<PaperBet> Bets { get; init; } = new();
}

/// <summary>
/// A single candle-aligned binary bet within a paper-trading session. `TargetOpenTime` is the
/// Binance candle openTime in milliseconds; `PlacedAt` is set to that exact moment so the ledger
/// reads as round-minute boundaries regardless of when the processor tick actually wrote the row.
/// </summary>
public sealed class PaperBet : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SessionId { get; init; }
    public required long TargetOpenTime { get; init; }
    public required string Side { get; init; } // "UP" or "DOWN"
    public required decimal PredictedProbUp { get; init; }
    public required decimal AnchorClose { get; init; }
    public required decimal Size { get; init; }
    public required decimal BalanceBefore { get; init; }
    public DateTimeOffset PlacedAt { get; init; }
    public bool Resolved { get; set; }
    public string? Outcome { get; set; } // "win" or "loss"
    public decimal? Payout { get; set; }
    public decimal? BalanceAfter { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public decimal? ActualClose { get; set; }
    /// <summary>
    /// Structured iter-3 instrumentation — decision context per bet: raw_p_up, calibrated_p_up,
    /// p50, action ("placed" | "skipped_low_conviction" | "skipped_disabled"), and a free-form
    /// reason. Nullable for rows older than this column.
    /// </summary>
    public string? NotesJson { get; set; }
    /// <summary>The entry price paid for the chosen side's outcome token at placement time.</summary>
    public decimal? EntryPrice { get; set; }
    /// <summary>Number of outcome shares purchased (= Size / EntryPrice). Null for legacy rows.</summary>
    public decimal? Shares { get; set; }
    /// <summary>True when the entry price was synthesised (no real venue price was available).</summary>
    public bool Synthetic { get; set; }
    /// <summary>Venue-side market id the price came from; null for synthetic rows.</summary>
    public string? MarketExternalId { get; set; }
}
