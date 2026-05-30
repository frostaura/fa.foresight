using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Models;

/// <summary>
/// A user-defined prediction model. The behaviour lives in <see cref="Definition"/> — a flow-DAG
/// JSON that the flow executor runs per candle to produce (direction, confidence). Two flavours:
/// LLM-based (non-deterministic, not backtestable) and deterministic (statistical, backtestable).
///
/// Built-in seeds (e.g. "Foresight Default LLM") live with <see cref="TenantId"/> = <c>null</c>
/// so every tenant sees them via a UNION at read time. Tenant-authored models carry the tenant id.
/// </summary>
public sealed class Model
{
    public Guid Id { get; init; }
    /// <summary>Null for global built-ins; otherwise the owning tenant.</summary>
    public Guid? TenantId { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>"llm" or "deterministic".</summary>
    public required string Kind { get; init; }
    public required bool SupportsBacktesting { get; init; }
    public bool IsBuiltIn { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>Flow DAG JSON — schema in /docs/flow-schema.md (planned).</summary>
    public required string Definition { get; set; }
    /// <summary>Trained coefficients keyed by node id. Null for never-trained or LLM-only models.</summary>
    public string? TrainedState { get; set; }
    public decimal? TrainingValidationAccuracy { get; set; }
    public decimal? BacktestAccuracy { get; set; }
    public DateTimeOffset? LastTrainedAt { get; set; }
    /// <summary>
    /// Epoch-ms of the first candle in the training window. Persisted so the leakage-audit can
    /// detect overlap between training data and a subsequent backtest range — every overlapping
    /// candle is in-sample and inflates the reported hit rate. Null for never-trained models.
    /// </summary>
    public long? TrainStartMs { get; set; }
    /// <summary>Epoch-ms of the last candle in the training window. See <see cref="TrainStartMs"/>.</summary>
    public long? TrainEndMs { get; set; }
    /// <summary>Symbol the model was trained on (e.g. "BTCUSDT"). Null for never-trained models.</summary>
    public string? TrainSymbol { get; set; }
    /// <summary>Interval the model was trained on (e.g. "15m"). Null for never-trained models.</summary>
    public string? TrainInterval { get; set; }
    /// <summary>
    /// Server-side training job state so a run survives the browser closing. "training" while a
    /// background fit is in flight, "failed" if it threw, null when idle/complete. The UI reads
    /// this on load and polls until it clears — training is persistent + auto-synced, not tied to
    /// the HTTP request that kicked it off (which would cancel on tab close).
    /// </summary>
    public string? TrainingStatus { get; set; }
    /// <summary>When the in-flight (or last) training run started. Null if never trained.</summary>
    public DateTimeOffset? TrainingStartedAt { get; set; }
    /// <summary>Error message from the last failed training run; null on success/idle.</summary>
    public string? TrainingError { get; set; }
    /// <summary>
    /// When true the model is soft-deleted from the default listing but preserved for history.
    /// Archived models are excluded from GET /api/models unless ?includeArchived=true is passed.
    /// </summary>
    public bool IsArchived { get; set; } = false;
    /// <summary>
    /// Plain-English description (1–2 sentences) for non-experts. Generated deterministically by
    /// <c>DescriptionTemplater.ForModel</c> at create/update — no external LLM or API key.
    /// </summary>
    public string? SimpleDescription { get; set; }
    /// <summary>
    /// Technical description (2–3 sentences) for a data scientist — node types, estimator framing,
    /// honest accuracy regime. Generated alongside <see cref="SimpleDescription"/>.
    /// </summary>
    public string? TechnicalDescription { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
