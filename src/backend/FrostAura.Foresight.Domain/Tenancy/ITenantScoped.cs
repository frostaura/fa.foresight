namespace FrostAura.Foresight.Domain.Tenancy;

/// <summary>
/// Marker contract for any persisted entity that belongs to a tenant.
/// Repositories enforce tenant isolation by filtering on this property at the boundary.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
