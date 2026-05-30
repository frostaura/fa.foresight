using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Middleware;

/// <summary>
/// SINGLE-TENANT MODE. Every visitor — any device, browser, or incoming X-Tenant-Slug
/// header / ?tenant= query — is mapped to the one shared tenant so everyone who opens the
/// site sees the same data. The multi-tenant data model (tenant-scoped entities) is kept
/// intact for future B2B use; only resolution is pinned here. To re-enable per-tenant
/// routing later, honour the header/query again instead of forcing <see cref="SharedTenantSlug"/>.
/// Public endpoints — health, root, openapi — bypass and don't require a tenant.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    /// <summary>Slug of the single tenant every request is pinned to (seeded by DatabaseInitializer).</summary>
    public const string SharedTenantSlug = "default";

    private readonly RequestDelegate _next;
    public TenantResolutionMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext ctx, ITenantContext tenantContext, ForesightDbContext db)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path == "/" || path.StartsWith("/health") || path.StartsWith("/openapi"))
        {
            await _next(ctx);
            return;
        }

        // Resolution is pinned to the shared tenant regardless of any header/query the client sends.
        // Prefer the canonical "default" slug; fall back to the earliest-created tenant so the app
        // still resolves if the shared tenant was ever renamed.
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == SharedTenantSlug)
                     ?? await db.Tenants.AsNoTracking().OrderBy(t => t.CreatedAt).FirstOrDefaultAsync();
        if (tenant is null)
        {
            // The shared tenant is seeded on first boot; a miss here means the DB isn't initialised yet.
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { error = "Shared tenant not provisioned yet." });
            return;
        }
        tenantContext.Set(tenant.Id, tenant.Slug);
        await _next(ctx);
    }
}
