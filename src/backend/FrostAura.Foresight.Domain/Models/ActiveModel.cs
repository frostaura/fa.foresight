using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Models;

/// <summary>
/// Per-card model selection. Each (tenant, symbol, interval) maps to exactly one active model id
/// that drives live predictions and paper trading on that card. Absence of a row means "use the
/// tenant's / global default model".
/// </summary>
public sealed class ActiveModel : ITenantScoped
{
    public Guid TenantId { get; init; }
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public Guid ModelId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
