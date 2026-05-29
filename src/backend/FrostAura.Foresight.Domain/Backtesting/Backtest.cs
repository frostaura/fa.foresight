using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Backtesting;

/// <summary>
/// One backtest run: a deterministic model evaluated against a historical candle range, with the
/// strategy's Martingale staking replayed *with no bankruptcy* — balance can go negative, recorded
/// as borrowed shortfall. The summary fields are derived from the per-candle <see cref="BacktestBet"/>
/// rows but cached here for fast list rendering.
/// </summary>
public sealed class Backtest : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid ModelId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required long StartTime { get; init; }
    public required long EndTime { get; init; }
    public required decimal InitialBalance { get; init; }
    public required decimal InitialBetSize { get; init; }
    /// <summary>"running" | "complete" | "cancelled" | "failed".</summary>
    public string Status { get; set; } = "running";
    /// <summary>
    /// Was the run executed with no-bankruptcy semantics (true; default) or with strict bust-check
    /// (false)? When false the engine halts placing once a doubled bet would exceed the bankroll,
    /// matching the live paper-trading contract.
    /// </summary>
    public bool AllowBorrow { get; init; } = true;
    /// <summary>
    /// Whether the run applied the confidence gate (skip the ±2pp no-bet band) rather than betting
    /// every candle. Persisted so the recent-runs UI can label gated vs always-bet runs and so an
    /// A/B of the two stays interpretable after restart. Default false = always-bet baseline.
    /// </summary>
    public bool ApplyGate { get; init; } = false;
    /// <summary>
    /// Optional grouping id linking this row to a set of sibling runs from the same A/B comparison
    /// batch. Frontend uses it to visually pair-highlight runs that were launched together.
    /// </summary>
    public Guid? BatchId { get; init; }
    /// <summary>
    /// Staking strategy id used for the run (e.g. <c>martingale</c>, <c>flat</c>). Persisted so the
    /// recent-runs UI can label each row and so an A/B comparison across strategies stays
    /// interpretable after restart. Defaults to <c>martingale</c> for backwards-compat on legacy rows.
    /// </summary>
    public string StrategyId { get; init; } = "martingale";
    /// <summary>
    /// Optional batch classification. <c>null</c> = an ordinary standalone/A-B run; <c>"bust-test"</c>
    /// = one rung of a bust-test sweep (N runs at increasing lookback). Lets the recent-runs UI
    /// collapse a sweep into a single clickable batch row.
    /// </summary>
    public string? BatchKind { get; init; }
    /// <summary>
    /// For a bust-test rung, the lookback in days (1, 2, 3, … up to the sweep's max). Null for
    /// non-bust-test runs. The window is <c>[now − LookbackDay days, now]</c>.
    /// </summary>
    public int? LookbackDay { get; init; }
    public int BetsPlaced { get; set; }
    public int BetsWon { get; set; }
    public decimal? HitRate { get; set; }
    /// <summary>Fraction of bets placed against synthetic-flat odds (no real venue price was available).
    /// 1.0 = every bet used synthetic odds; 0.0 = all bets had real venue prices.</summary>
    public decimal? SyntheticBetFraction { get; set; }
    public decimal? FinalBalance { get; set; }
    public decimal? PeakBalance { get; set; }
    public decimal? TroughBalance { get; set; }
    public decimal? MaxDrawdown { get; set; }
    /// <summary>The maximum |negative balance| reached during the run (auto-borrow watermark).</summary>
    public decimal? PeakBorrowed { get; set; }
    /// <summary>How many times the bankroll crossed zero in either direction (sign change).</summary>
    public int ZeroCrossingsCount { get; set; }
    /// <summary>
    /// Deepest Martingale doubling chain reached during the run (<c>log2(maxBetSize / initialBet)</c>).
    /// 0 means no chain was ever doubled (every bet won immediately); 7 means the strategy went
    /// 7 losses deep before recovering (size = initial × 2^7 = 128×). Surfaced as the user-visible
    /// "Max ×N" risk signal.
    /// </summary>
    public int MaxMartingaleStep { get; set; }
    /// <summary>Compact JSON for chart overlay: [{t, hit, side}, …]. Pulled into a recharts ReferenceDot per candle.</summary>
    public string? MarkersJson { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }

    public List<BacktestBet> Bets { get; init; } = new();
}

/// <summary>
/// One bet inside a backtest. Owns its own pre/post balance so the ledger can be walked
/// independently of the parent's running totals.
/// </summary>
public sealed class BacktestBet
{
    public Guid Id { get; init; }
    public Guid BacktestId { get; init; }
    public required long TargetOpenTime { get; init; }
    public required string Side { get; init; }
    public required decimal PUpRaw { get; init; }
    public decimal? PUpCalibrated { get; init; }
    public required decimal Size { get; init; }
    public required decimal BalanceBefore { get; init; }
    public required decimal BalanceAfter { get; init; }
    public required bool Won { get; init; }
    public decimal BorrowedShortfall { get; init; }
    /// <summary>The price paid for the chosen side's outcome token (YES price if UP, NO price if DOWN).</summary>
    public decimal EntryPrice { get; init; }
    /// <summary>Number of outcome shares purchased at EntryPrice (= Size / EntryPrice).</summary>
    public decimal Shares { get; init; }
    /// <summary>Gross payout on a win (= Shares × $1); 0 on a loss.</summary>
    public decimal Payout { get; init; }
    /// <summary>True when the entry price was synthesised (no real venue price was available).</summary>
    public bool Synthetic { get; init; }
    /// <summary>Venue-side market id the price came from; null for synthetic rows.</summary>
    public string? MarketExternalId { get; init; }
}
