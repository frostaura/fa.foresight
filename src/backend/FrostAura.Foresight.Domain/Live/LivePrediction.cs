using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Live;

/// <summary>
/// LLM-generated forecast of the *next* candle's close for a (symbol, interval) on a live market.
/// Recorded at the moment of inference so accuracy can be measured retroactively once the target
/// candle closes — this is the accuracy-overlay substrate behind the live charts.
///
/// Unique per (tenant, symbol, interval, target_open_time) so a candle is only predicted once.
/// </summary>
public sealed class LivePrediction : ITenantScoped
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required long TargetOpenTime { get; init; }
    /// <summary>
    /// The model that produced this row. Defaults to the Flat Baseline id so rows attribute
    /// coherently when no per-(tenant,symbol,interval) override exists.
    /// </summary>
    public Guid ModelId { get; init; } = ModelIds.ForesightFlatBaseline;
    public required decimal AnchorClose { get; init; }
    public required decimal PredictedClose { get; init; }
    public required decimal PredictedChangePct { get; init; }
    /// <summary>
    /// Calibrated probability that close at target time > anchor close. This is the *primary*
    /// signal for Polymarket-style binary trading; PredictedClose/PredictedChangePct are derived
    /// for chart placement and historical compatibility.
    /// </summary>
    public decimal DirectionUpProbability { get; init; }
    /// <summary>
    /// Legacy field — superseded by ClosePercentile05/50/95 quantile triple in iter-3. Retained
    /// for back-compat reads against rows persisted before the schema change. Defaults to 0.5.
    /// </summary>
    public decimal TargetHitProbability { get; set; } = 0.5m;
    /// <summary>5th-percentile of the close distribution. Lower bound of the model's uncertainty band.</summary>
    public decimal ClosePercentile05 { get; set; }
    /// <summary>Median of the close distribution. Central estimate; should equal PredictedClose for back-compat reads.</summary>
    public decimal ClosePercentile50 { get; set; }
    /// <summary>95th-percentile of the close distribution. Upper bound of the model's uncertainty band.</summary>
    public decimal ClosePercentile95 { get; set; }
    public required decimal Confidence { get; init; }
    public string? Reasoning { get; init; }
    public required string Model { get; init; }
    public string? SupportingDataJson { get; init; }
    /// <summary>
    /// Exact LLM round-trip that produced this prediction — model, temperature, max-tokens, both
    /// chat messages (system + user) verbatim, and the raw response content. Persisted as JSON so
    /// any historic prediction can be inspected, diffed, or replayed against another model without
    /// having to reconstruct the prompt from current source. Nullable for rows older than this
    /// column.
    /// </summary>
    public string? PromptTraceJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public decimal? ActualClose { get; set; }
    public decimal? AbsoluteErrorPct { get; set; }
    public bool? DirectionHit { get; set; }
}
