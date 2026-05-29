namespace FrostAura.Foresight.Domain.Strategies;

/// <summary>
/// A user-defined staking strategy, either a built-in catalogue entry or a custom DAG authored in
/// the designer and persisted as a strategy-kind <see cref="FrostAura.Foresight.Application.Flow.FlowDefinition"/>.
///
/// Built-in rows carry <see cref="IsBuiltIn"/> = true and <see cref="TenantId"/> = null (globally
/// visible, read-only). Custom DAG strategies belong to a tenant and carry a valid
/// <see cref="Definition"/> JSON (DefinitionKind = "strategy").
/// </summary>
public sealed class Strategy
{
    public Guid Id { get; init; }
    /// <summary>Null for global built-in rows; otherwise the owning tenant.</summary>
    public Guid? TenantId { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// Flow DAG JSON (DefinitionKind = "strategy"). Null for built-in catalogue entries that are
    /// implemented in Domain.Trading code rather than a DAG flow.
    /// </summary>
    public string? Definition { get; set; }
    /// <summary>
    /// Reserved for future per-strategy parameter overrides (JSON). Not used by the current
    /// executor — node-level params live in the DAG nodes themselves.
    /// </summary>
    public string? Params { get; set; }
    /// <summary>True for rows that mirror the static <see cref="FrostAura.Foresight.Domain.Trading.StakingStrategies"/> catalogue.</summary>
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
