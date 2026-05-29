using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Middleware;

/// <summary>
/// Resolves the tenant from the X-Tenant-Slug header (default: "default").
/// Public endpoints — health, root, openapi — bypass and don't require a tenant.
/// </summary>
public sealed class TenantResolutionMiddleware
{
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

        // Header is the canonical channel; the `?tenant=` query-string fallback exists for
        // EventSource subscribers, which can't set custom headers.
        var slug = ctx.Request.Headers["X-Tenant-Slug"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(slug)) slug = ctx.Request.Query["tenant"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(slug)) slug = "default";

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new { error = $"Tenant '{slug}' not found." });
            return;
        }
        tenantContext.Set(tenant.Id, tenant.Slug);
        await _next(ctx);
    }
}
