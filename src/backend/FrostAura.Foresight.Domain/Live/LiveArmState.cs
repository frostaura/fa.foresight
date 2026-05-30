namespace FrostAura.Foresight.Domain.Live;

/// <summary>
/// Durable mirror of a tenant's live-trading arm switch. The runtime source of truth is the
/// in-memory <c>LiveTradingArm</c> singleton; this row persists the armed flag so the arm survives a
/// process restart (the operator's chosen behaviour for an always-on automated trader). Confirm/kill
/// write here; startup hydrates the in-memory map from here. <see cref="ArmedAt"/>/<see cref="ArmedBy"/>
/// are an audit trail.
/// </summary>
public sealed class LiveArmState
{
    /// <summary>Owning tenant — primary key (one arm state per tenant).</summary>
    public Guid TenantId { get; set; }
    /// <summary>True when live execution is armed for this tenant.</summary>
    public bool Armed { get; set; }
    /// <summary>When the tenant was last armed (null if never armed).</summary>
    public DateTimeOffset? ArmedAt { get; set; }
    /// <summary>Optional audit label for who armed it (null in the single-user MVP).</summary>
    public string? ArmedBy { get; set; }
    /// <summary>Last write time (arm or disarm).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
