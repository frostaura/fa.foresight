namespace FrostAura.Foresight.Application.Chaos;

// ─────────────────────────────────────────────────────────────────────────────
// BET CANDIDATE
// A precomputed, per-candle bet opportunity reused across all windows of a
// chaos run.  Precomputing it once per model eliminates redundant flow re-
// execution for every (window, strategy) combo in the matrix sweep.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One precomputed bet opportunity for a single candle. All fields are read-only
/// after construction — the chaos engine never mutates candidates.
/// </summary>
/// <param name="TargetOpenTime">The bettable candle's open time (ms since epoch).</param>
/// <param name="PUp">Calibrated up-probability emitted by the model for this candle.</param>
/// <param name="YesPrice">Entry price for the YES outcome (anti-look-ahead).</param>
/// <param name="NoPrice">Entry price for the NO outcome (1 − YesPrice for synthetic rows).</param>
/// <param name="Synthetic">True when the entry price was synthesised (no real venue price).</param>
/// <param name="OutcomeUp">Whether close(target) &gt; close(anchor) — the realised direction.</param>
public sealed record BetCandidate(
    long TargetOpenTime,
    decimal PUp,
    decimal YesPrice,
    decimal NoPrice,
    bool Synthetic,
    bool OutcomeUp);

// ─────────────────────────────────────────────────────────────────────────────
// CHAOS REQUEST
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parameters for a chaos/bust test run.  The engine creates one row in
/// <c>chaos_runs</c> per (ModelId × StrategyId × WindowLength) combo.
/// </summary>
/// <param name="ModelIds">One or more model ids to sweep.</param>
/// <param name="StrategyIds">One or more strategy ids to sweep (e.g. "flat", "kelly-edge").</param>
/// <param name="Symbol">Market symbol (e.g. "BTCUSDT").</param>
/// <param name="Interval">Candle interval (e.g. "5m").</param>
/// <param name="WindowLengthCandles">Primary window length in candles.</param>
/// <param name="LengthSweep">Optional additional window lengths to sweep alongside the primary.</param>
/// <param name="SampleCount">Number of random start offsets to draw.</param>
/// <param name="InitialBalance">Starting bankroll for each window replay.</param>
/// <param name="InitialBetSize">First bet size (flat) / unit for compounding strategies.</param>
/// <param name="AllowBorrow">When false, a window halts the moment the next stake exceeds the balance.</param>
/// <param name="Seed">
/// Reproducibility seed.  When null the service picks a fixed-default seed (0) so the run
/// is always reproducible — seed is persisted on the row regardless.
/// </param>
public sealed record ChaosRequest(
    Guid[] ModelIds,
    string[] StrategyIds,
    string Symbol,
    string Interval,
    int WindowLengthCandles,
    int[]? LengthSweep,
    int SampleCount,
    decimal InitialBalance,
    decimal InitialBetSize,
    bool AllowBorrow,
    long? Seed);

// ─────────────────────────────────────────────────────────────────────────────
// PURE RESULT TYPES (returned by ChaosRunner, never persisted directly)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The outcome of replaying one random window for one (strategy, balance) configuration.</summary>
/// <param name="StartMs">OpenTime of the first candidate in the window.</param>
/// <param name="Survived">True if the window never busted (balance &gt; 0 throughout).</param>
/// <param name="FinalBalance">Balance at the end of the window.</param>
/// <param name="MaxDrawdown">Largest peak→trough drawdown observed in the window.</param>
/// <param name="ZeroCrossings">How many times the balance crossed zero in either direction.</param>
public sealed record ChaosSampleResult(
    long StartMs,
    bool Survived,
    decimal FinalBalance,
    decimal MaxDrawdown,
    int ZeroCrossings);

/// <summary>
/// Aggregated statistics for one (model × strategy × windowLen) combo across all sampled windows.
/// </summary>
/// <param name="ModelId">Model whose candidates were used.</param>
/// <param name="StrategyId">Strategy that sized the bets.</param>
/// <param name="WindowLength">Number of candles in each window.</param>
/// <param name="BustRate">Fraction of windows that did not survive.</param>
/// <param name="ProfitP5">5th-percentile profit (FinalBalance − InitialBalance).</param>
/// <param name="ProfitP50">Median profit.</param>
/// <param name="ProfitP95">95th-percentile profit.</param>
/// <param name="ProfitMean">Mean (average) profit across all windows.</param>
/// <param name="WorstDrawdown">Maximum drawdown across all windows.</param>
/// <param name="MeanZeroCrossings">Mean zero-crossings across all windows.</param>
/// <param name="SyntheticBetFraction">Fraction of candidates that used synthetic odds.</param>
/// <param name="Pass">True when BustRate == 0 AND ProfitP50 &gt; 0.</param>
public sealed record ChaosComboAggregate(
    Guid ModelId,
    string StrategyId,
    int WindowLength,
    double BustRate,
    decimal ProfitP5,
    decimal ProfitP50,
    decimal ProfitP95,
    decimal ProfitMean,
    decimal WorstDrawdown,
    double MeanZeroCrossings,
    decimal SyntheticBetFraction,
    bool Pass);

// ─────────────────────────────────────────────────────────────────────────────
// SERVICE INTERFACE
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates a full chaos/bust test matrix run: candidate precompute, random-window sweep,
/// aggregation, persistence, and SSE progress broadcast.
/// </summary>
public interface IChaosService
{
    /// <summary>Starts a chaos run in the background. Returns the shared BatchId immediately.</summary>
    Task<Guid> RunAsync(ChaosRequest req, CancellationToken ct);

    /// <summary>All combo rows for a batch, ranked: Pass rows first, then by ProfitP50 desc.</summary>
    Task<IReadOnlyList<Domain.Chaos.ChaosRun>> GetBatchAsync(Guid batchId, CancellationToken ct);

    /// <summary>Single chaos run row (with no samples — call GetSamplesAsync for those).</summary>
    Task<Domain.Chaos.ChaosRun?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Per-window sample rows for a chaos run.</summary>
    Task<IReadOnlyList<Domain.Chaos.ChaosSample>> GetSamplesAsync(Guid id, int take, CancellationToken ct);

    /// <summary>
    /// Bulk-deletes chaos runs for the current tenant (samples cascade). With no modelId every run
    /// is removed; with a modelId only that model's runs. Returns the number of run rows deleted.
    /// </summary>
    Task<int> ClearAsync(Guid? modelId, CancellationToken ct);
}
