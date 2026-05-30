using System.Collections.Concurrent;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Foresight.Infrastructure.Live;

public sealed class ActiveModelResolver : IActiveModelResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();

    public ActiveModelResolver(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<Guid> ResolveAsync(Guid tenantId, string symbol, string interval, CancellationToken ct)
    {
        var key = new CacheKey(tenantId, symbol, interval);
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.ModelId;

        // Open a fresh DI scope per cache miss — the resolver is singleton, so it can't hold a
        // scoped DbContext for its lifetime (captive-dependency anti-pattern).
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        // 1. Per-(tenant, symbol, interval) override.
        var active = await db.ActiveModels.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Symbol == symbol && a.Interval == interval)
            .Select(a => (Guid?)a.ModelId)
            .FirstOrDefaultAsync(ct);

        // 2. Default model — tenant-owned wins; otherwise the global (built-in) default. Mirrors
        //    the frontend ModelPicker fallback (tenantDefault ?? globalDefault ?? first), so a
        //    user marking the global Foresight v6 as default actually drives predictions instead
        //    of silently falling back to the FlatBaseline guid that may not exist any more.
        Guid? defaultId = null;
        if (active is null)
        {
            var defaults = await db.Models.AsNoTracking()
                .Where(m => (m.TenantId == tenantId || m.TenantId == null) && m.IsDefault)
                .Select(m => new { m.Id, m.TenantId })
                .ToListAsync(ct);
            defaultId = defaults.FirstOrDefault(d => d.TenantId == tenantId)?.Id
                     ?? defaults.FirstOrDefault(d => d.TenantId == null)?.Id;
        }

        // Validate the chosen id exists — the active_models override can refer to a row a user has
        // since deleted. If we hand back a phantom id, every PredictAsync 500s with "Active model X
        // not found". Walk the priority list and pick the first id that exists; the last-chance
        // "any visible model" fallback below covers the case where neither exists. (The old
        // hardcoded FlatBaseline fallback was removed — Flat is a staking strategy, not a model.)
        var candidates = new[] { active, defaultId };
        Guid? resolved = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            var exists = await db.Models.AsNoTracking().AnyAsync(m => m.Id == candidate, ct);
            if (exists) { resolved = candidate; break; }
        }
        // Last-chance fallback: any visible model for this tenant. Better to predict via *something*
        // than fail every card on the page.
        resolved ??= await db.Models.AsNoTracking()
            .Where(m => m.TenantId == tenantId || m.TenantId == null)
            .OrderBy(m => m.TenantId == null ? 0 : 1)
            .ThenBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
        if (resolved is null)
            throw new InvalidOperationException("No models are configured for this tenant; cannot resolve an active model.");

        _cache[key] = new CacheEntry(resolved.Value, DateTimeOffset.UtcNow + Ttl);
        return resolved.Value;
    }

    public void Invalidate(Guid tenantId, string symbol, string interval) =>
        _cache.TryRemove(new CacheKey(tenantId, symbol, interval), out _);

    public void InvalidateAll() => _cache.Clear();

    private readonly record struct CacheKey(Guid TenantId, string Symbol, string Interval);
    private readonly record struct CacheEntry(Guid ModelId, DateTimeOffset ExpiresAt);
}
