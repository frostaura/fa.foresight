namespace FrostAura.Foresight.Application.Models;

/// <summary>
/// Resolves "which prediction model should drive (tenant, symbol, interval) right now?". Wraps a
/// 30s in-memory cache so the gap-filler doesn't hammer Postgres on every tick — invalidated
/// proactively by the SSE-broadcasting active-models endpoint whenever the user changes the
/// selection.
///
/// Fallback chain when no per-card row exists:
///   1. The tenant's own default model (<c>models.IsDefault=true AND TenantId=ctx.tenant</c>)
///   2. The global built-in default LLM (<c>ModelIds.ForesightDefaultLlm</c>)
/// </summary>
public interface IActiveModelResolver
{
    Task<Guid> ResolveAsync(Guid tenantId, string symbol, string interval, CancellationToken ct);
    void Invalidate(Guid tenantId, string symbol, string interval);
    void InvalidateAll();
}
