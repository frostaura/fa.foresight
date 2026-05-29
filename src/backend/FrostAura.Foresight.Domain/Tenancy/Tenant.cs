namespace FrostAura.Foresight.Domain.Tenancy;

/// <summary>
/// Top-level tenant aggregate. Every other entity (except this one) implements ITenantScoped
/// and references this tenant by Id.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public TenantSettings Settings { get; set; } = new();
}

/// <summary>
/// Per-tenant runtime defaults. Each value is overridable per-flow.
/// </summary>
public sealed class TenantSettings
{
    public bool AutotradeEnabled { get; set; } = false;
    public string DefaultJurisdiction { get; set; } = "global-ex-us";
    public string DefaultLlmProviderId { get; set; } = "openrouter";
}
