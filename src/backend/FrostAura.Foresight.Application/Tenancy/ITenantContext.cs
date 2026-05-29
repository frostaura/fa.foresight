namespace FrostAura.Foresight.Application.Tenancy;

/// <summary>
/// Per-request resolved tenant. Population happens in API middleware before the request body executes.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    string? TenantSlug { get; }
    bool IsResolved { get; }
    void Set(Guid id, string slug);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public bool IsResolved => TenantId.HasValue;

    public void Set(Guid id, string slug)
    {
        TenantId = id;
        TenantSlug = slug;
    }
}
