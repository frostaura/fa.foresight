using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Descriptions;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Infrastructure.Live;

public sealed class ModelsService : IModelsService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly FlowValidator _validator;

    public ModelsService(ForesightDbContext db, ITenantContext tenant, FlowValidator validator)
    {
        _db = db;
        _tenant = tenant;
        _validator = validator;
    }

    public async Task<IReadOnlyList<Model>> ListAsync(CancellationToken ct, bool includeArchived = false)
    {
        EnsureTenant();
        // Both the tenant's own models and the global built-ins (TenantId NULL) — frontend renders
        // them in one list, built-ins flagged read-only. Archived models are excluded by default.
        var query = _db.Models.AsNoTracking()
            .Where(m => m.TenantId == _tenant.TenantId || m.TenantId == null);

        if (!includeArchived)
            query = query.Where(m => !m.IsArchived);

        return await query
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.IsBuiltIn)
            .ThenBy(m => m.Name)
            .ToListAsync(ct);
    }

    public async Task<bool> ArchiveAsync(Guid id, bool archive, CancellationToken ct)
    {
        EnsureTenant();
        var model = await _db.Models
            .FirstOrDefaultAsync(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id, ct);
        if (model is null) return false;

        model.IsArchived = archive;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Model?> GetAsync(Guid id, CancellationToken ct)
    {
        EnsureTenant();
        return await _db.Models.AsNoTracking()
            .Where(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Model> CreateAsync(string name, string? description, string kind, bool supportsBacktesting, string definition, CancellationToken ct)
    {
        EnsureTenant();
        ValidateDefinitionOrThrow(definition);

        var now = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId,
            Name = name,
            Description = description,
            Kind = kind,
            SupportsBacktesting = supportsBacktesting,
            Definition = definition,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var (simple, technical) = DescriptionTemplater.ForModel(model.Name, model.Kind, model.SupportsBacktesting, model.Definition);
        model.SimpleDescription = simple;
        model.TechnicalDescription = technical;
        _db.Models.Add(model);
        await _db.SaveChangesAsync(ct);

        return model;
    }

    public async Task<Model?> UpdateAsync(Guid id, string? name, string? description, string? definition, CancellationToken ct)
    {
        EnsureTenant();
        // Tenant scope includes global (TenantId == null) so unlocked global seeds can be edited.
        var model = await _db.Models.FirstOrDefaultAsync(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id, ct);
        if (model is null) return null;
        if (model.IsBuiltIn) throw new InvalidOperationException("Built-in models are read-only.");

        var nameChanged = name is not null && name != model.Name;
        var definitionChanged = definition is not null && definition != model.Definition;

        if (name is not null) model.Name = name;
        if (description is not null) model.Description = description;
        if (definition is not null)
        {
            ValidateDefinitionOrThrow(definition);
            model.Definition = definition;
        }
        // Regenerate descriptions when the name or definition changed, since they're derived from
        // those fields. Deterministic + inline — no background task, persisted in the same write.
        if (nameChanged || definitionChanged)
        {
            var (simple, technical) = DescriptionTemplater.ForModel(model.Name, model.Kind, model.SupportsBacktesting, model.Definition);
            model.SimpleDescription = simple;
            model.TechnicalDescription = technical;
        }
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return model;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        EnsureTenant();
        // Tenant scope includes global (TenantId == null) so unlocked global seeds can be deleted.
        var model = await _db.Models.FirstOrDefaultAsync(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id, ct);
        if (model is null) return false;
        if (model.IsBuiltIn) throw new InvalidOperationException("Built-in models are read-only.");

        // FK constraints on models.Id from active_models, backtests, and live_predictions are
        // NOT CASCADE in the schema — Postgres refuses the DELETE if any reference exists. Wipe
        // dependents explicitly in dependency order (backtest_bets cascades from backtests; no
        // backstop needed there). This is intentional cascade semantics expressed at the app
        // layer rather than the schema layer; safer than a destructive ALTER on live DB.
        await _db.ActiveModels.Where(a => a.ModelId == id).ExecuteDeleteAsync(ct);
        await _db.Backtests.Where(b => b.ModelId == id).ExecuteDeleteAsync(ct);
        await _db.LivePredictions.Where(p => p.ModelId == id).ExecuteDeleteAsync(ct);

        _db.Models.Remove(model);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Model?> DuplicateAsync(Guid id, string newName, CancellationToken ct)
    {
        EnsureTenant();
        var source = await _db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id, ct);
        if (source is null) return null;
        var now = DateTimeOffset.UtcNow;
        var clone = new Model
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId,
            Name = newName,
            Description = source.Description,
            Kind = source.Kind,
            SupportsBacktesting = source.SupportsBacktesting,
            Definition = source.Definition,
            // TrainedState NOT copied — a clone retrains from scratch since the user typically
            // edits the flow before retraining.
            CreatedAt = now,
            UpdatedAt = now,
        };
        var (simple, technical) = DescriptionTemplater.ForModel(clone.Name, clone.Kind, clone.SupportsBacktesting, clone.Definition);
        clone.SimpleDescription = simple;
        clone.TechnicalDescription = technical;
        _db.Models.Add(clone);
        await _db.SaveChangesAsync(ct);
        return clone;
    }

    public async Task<Model?> SetDefaultAsync(Guid id, CancellationToken ct)
    {
        EnsureTenant();
        var model = await _db.Models.FirstOrDefaultAsync(m => (m.TenantId == _tenant.TenantId || m.TenantId == null) && m.Id == id, ct);
        if (model is null) return null;

        // Clear any other tenant-level default to keep the "single default per tenant" invariant.
        var others = await _db.Models.Where(m => m.TenantId == _tenant.TenantId && m.IsDefault && m.Id != id).ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;

        model.IsDefault = true;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return model;
    }

    private void EnsureTenant()
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
    }

    private void ValidateDefinitionOrThrow(string definition)
    {
        FlowDefinition? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FlowDefinition>(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Flow definition is not valid JSON: {ex.Message}");
        }
        if (parsed is null) throw new InvalidOperationException("Flow definition is empty.");
        var result = _validator.Validate(parsed);
        if (!result.IsValid) throw new InvalidOperationException($"Flow validation failed: {result.Error}");
    }
}

public sealed class ActiveModelsService
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IActiveModelResolver _resolver;

    public ActiveModelsService(ForesightDbContext db, ITenantContext tenant, IActiveModelResolver resolver)
    {
        _db = db;
        _tenant = tenant;
        _resolver = resolver;
    }

    public async Task<IReadOnlyList<ActiveModel>> ListAsync(CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        return await _db.ActiveModels.AsNoTracking()
            .Where(a => a.TenantId == _tenant.TenantId)
            .ToListAsync(ct);
    }

    public async Task<ActiveModel> SetAsync(string symbol, string interval, Guid modelId, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;

        // Validate the model exists and is visible to this tenant (own or built-in).
        var modelExists = await _db.Models.AsNoTracking()
            .AnyAsync(m => m.Id == modelId && (m.TenantId == tenantId || m.TenantId == null), ct);
        if (!modelExists) throw new InvalidOperationException($"Model {modelId} not found for this tenant.");

        var existing = await _db.ActiveModels
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Symbol == symbol && a.Interval == interval, ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new ActiveModel
            {
                TenantId = tenantId,
                Symbol = symbol,
                Interval = interval,
                ModelId = modelId,
                UpdatedAt = now,
            };
            _db.ActiveModels.Add(existing);
        }
        else
        {
            existing.ModelId = modelId;
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        _resolver.Invalidate(tenantId, symbol, interval);
        return existing;
    }

    public async Task<bool> ClearAsync(string symbol, string interval, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant context not resolved.");
        var tenantId = _tenant.TenantId!.Value;
        var existing = await _db.ActiveModels.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Symbol == symbol && a.Interval == interval, ct);
        if (existing is null) return false;
        _db.ActiveModels.Remove(existing);
        await _db.SaveChangesAsync(ct);
        _resolver.Invalidate(tenantId, symbol, interval);
        return true;
    }
}
