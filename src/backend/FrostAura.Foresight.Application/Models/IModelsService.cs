using FrostAura.Foresight.Domain.Models;

namespace FrostAura.Foresight.Application.Models;

/// <summary>
/// Tenant-scoped CRUD for prediction models. List returns both the tenant's own models and the
/// global built-ins (TenantId NULL) in one stream so the frontend can render them uniformly. Built-in
/// rows are read-only — mutating endpoints reject writes against an IsBuiltIn row with 403.
/// </summary>
public interface IModelsService
{
    Task<IReadOnlyList<Model>> ListAsync(CancellationToken ct);
    Task<Model?> GetAsync(Guid id, CancellationToken ct);
    Task<Model> CreateAsync(string name, string? description, string kind, bool supportsBacktesting, string definition, CancellationToken ct);
    Task<Model?> UpdateAsync(Guid id, string? name, string? description, string? definition, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<Model?> DuplicateAsync(Guid id, string newName, CancellationToken ct);
    Task<Model?> SetDefaultAsync(Guid id, CancellationToken ct);
}
