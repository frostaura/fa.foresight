using FrostAura.Foresight.Domain.Backtesting;

namespace FrostAura.Foresight.Application.Backtesting;

public interface IBacktestsService
{
    Task<Backtest> RunAsync(BacktestRequest req, CancellationToken ct);
    /// <summary>
    /// Launches a bust-test sweep: N backtests sharing one BatchId, identical params except the
    /// lookback, which steps 1, 2, 3 … up to <paramref name="maxLookbackDays"/>. Each rung's window
    /// is <c>[now − k days, now]</c>, answering "would the strategy have survived the last k days?".
    /// Returns the created (still-running) rungs. 5m-only, flat-staking is the intended use but the
    /// request carries the chosen interval/strategy verbatim.
    /// </summary>
    Task<IReadOnlyList<Backtest>> RunBustTestAsync(BustTestRequest req, CancellationToken ct);
    Task<IReadOnlyList<Backtest>> ListAsync(Guid? modelId, CancellationToken ct);
    /// <summary>All rungs of a bust-test batch (ordered by lookback day).</summary>
    Task<IReadOnlyList<Backtest>> GetBatchAsync(Guid batchId, CancellationToken ct);
    Task<Backtest?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<BacktestBet>> GetBetsAsync(Guid id, int take, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    /// <summary>
    /// Bulk-delete backtests for the current tenant. When <paramref name="modelId"/> is non-null,
    /// only that model's runs are removed; otherwise every backtest for the tenant is wiped.
    /// Implementation uses raw SQL <c>TRUNCATE … CASCADE</c> when clearing everything so the
    /// per-bet child rows go in one statement instead of EF's cascading per-row delete.
    /// </summary>
    Task<int> ClearAsync(Guid? modelId, CancellationToken ct);
}

public sealed record BacktestRequest(
    Guid ModelId,
    string Symbol,
    string Interval,
    long StartTime,
    long EndTime,
    decimal InitialBalance,
    decimal InitialBetSize,
    /// <summary>When true, Martingale steps that exceed the bankroll are allowed (balance dips
    /// negative, recorded as borrowed shortfall). When false, the run halts with status='complete'
    /// once a step would exceed the current balance — matches live paper-trading bankruptcy.</summary>
    bool AllowBorrow = true,
    /// <summary>Optional grouping id so an A/B multi-model run can be displayed together in the
    /// recent-runs table. Null for ad-hoc single-model runs.</summary>
    Guid? BatchId = null,
    /// <summary>Staking strategy id (see <see cref="FrostAura.Foresight.Domain.Trading.StakingStrategies"/>).
    /// Defaults to <c>martingale</c>; pass <c>flat</c> or any other registered id to switch.</summary>
    string StrategyId = "martingale",
    /// <summary>Batch classification — <c>null</c> for ordinary runs, <c>"bust-test"</c> for a sweep rung.</summary>
    string? BatchKind = null,
    /// <summary>For a bust-test rung, the lookback in days. Null otherwise.</summary>
    int? LookbackDay = null,
    /// <summary>When true, the run applies the confidence gate: candles in the ±2pp no-bet band are
    /// skipped (no bet placed) — the same equation the chart GATE + live paper gate use. Default
    /// false = bet every candle (the always-bet baseline being compared against).</summary>
    bool ApplyGate = false,
    /// <summary>Explicit confidence-gate band width (total, e.g. 0.06 = skip pUp within ±3pp of 0.50).
    /// When set, it overrides <see cref="ApplyGate"/>'s fixed ±2pp band — the lever for sweeping the
    /// gate to find its profitability/risk sweet spot. Null = use ApplyGate's default band (or none).
    /// Not persisted; the per-run metrics (final balance, drawdown, bets, hit-rate) capture the result.</summary>
    decimal? GateBand = null);

/// <summary>
/// A bust-test sweep request. Identical to a single backtest except the lookback is a MAX rather
/// than an absolute window — the service fans it out to one run per day from 1..MaxLookbackDays.
/// </summary>
public sealed record BustTestRequest(
    Guid ModelId,
    string Symbol,
    string Interval,
    decimal InitialBalance,
    decimal InitialBetSize,
    int MaxLookbackDays,
    bool AllowBorrow = false,
    string StrategyId = "flat");

public interface IModelTrainingService
{
    /// <summary>
    /// Trains the model across every supported interval (currently 1m/5m/15m). The result holds
    /// one entry per interval — each interval gets its own coefficients calibrated to that
    /// timeframe's data distribution. Inference picks the variant matching the run's interval.
    ///
    /// <paramref name="holdoutDays"/> optionally pushes the training window back so a downstream
    /// backtest can score the model on candles strictly newer than anything it was trained on.
    /// E.g. with holdoutDays=90 and the 15m variant's 365d lookback, the 15m window becomes
    /// [now-455d, now-90d] — leaving the last 90 days free for an honest out-of-sample backtest.
    /// Defaults to 0 (legacy behaviour: train right up to now).
    ///
    /// <paramref name="interval"/> optionally restricts training to a SINGLE interval (e.g. "5m"),
    /// merging the result into the model's existing variants instead of retraining all of them. This
    /// is the fast path for iterating a 5m-specialised model — training just its 5m variant takes
    /// seconds instead of the minutes the full 1m/5m/15m sweep needs (the 15m variant alone pulls
    /// ~525k 1m off-tf candles). Null trains every supported interval (legacy behaviour).
    /// </summary>
    Task<ModelTrainResult> TrainAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct);

    /// <summary>
    /// Kicks off a training run on a background task and returns immediately. The fit itself is NOT
    /// tied to <paramref name="ct"/> — it survives the HTTP request (and the browser) that started
    /// it, so closing the tab no longer cancels training. Progress is observable via the model's
    /// <c>TrainingStatus</c> ("training" → null on success, "failed" on error), which the UI reads on
    /// load and polls until it clears. Cheap validation (tenant, symbol, trainable flow, not already
    /// training) runs synchronously so the caller gets an immediate error for bad input; the heavy
    /// fit runs in the background via <see cref="TrainAsync"/>.
    /// </summary>
    Task StartTrainingAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct);
}

public sealed record TrainedVariantSummary(
    string Interval,
    long TrainStartMs,
    long TrainEndMs,
    decimal ValidationAccuracy);

public sealed record ModelTrainResult(
    IReadOnlyList<TrainedVariantSummary> Variants,
    DateTimeOffset TrainedAt);

/// <summary>
/// Honest out-of-sample evaluation: rolling-origin walk-forward (retrain per fold + embargo gap)
/// over a range, returning the aggregate result plus the accept/reject verdict from the plan's
/// guards. This is the iteration loop's source of truth — the number that says whether an edge is
/// real, not the in-sample / overlapping-window numbers a single backtest can produce.
/// </summary>
public interface IWalkForwardService
{
    Task<WalkForwardReport> EvaluateAsync(
        Guid modelId, string symbol, string interval, long startMs, long endMs, int folds, CancellationToken ct, int horizonSteps = 2);
}

/// <summary>Walk-forward result plus the guard verdict (≥60% floor, CI lower bound &gt; 50%, fold majority, small overfit gap).</summary>
public sealed record WalkForwardReport(WalkForwardResult Result, bool Pass, string Reason);
