using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Tenancy;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").WithTags("tenants");

        g.MapGet("/me", async (ITenantContext ctx, ForesightDbContext db) =>
        {
            if (!ctx.IsResolved) return Results.NotFound();
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == ctx.TenantId);
            return Results.Ok(t);
        });

        g.MapGet("/", async (ForesightDbContext db) =>
        {
            var rows = await db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync();
            return Results.Ok(rows);
        });

        g.MapPost("/", async (CreateTenantRequest req, ForesightDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest("name and slug are required.");
            if (await db.Tenants.AnyAsync(t => t.Slug == req.Slug)) return Results.Conflict($"Slug '{req.Slug}' is taken.");

            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                Slug = req.Slug,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tenants/{tenant.Id}", tenant);
        });

        g.MapPut("/me/settings", async (TenantSettings settings, ITenantContext ctx, ForesightDbContext db) =>
        {
            if (!ctx.IsResolved) return Results.NotFound();
            var t = await db.Tenants.FirstAsync(x => x.Id == ctx.TenantId);
            t.Settings = settings;
            await db.SaveChangesAsync();
            return Results.Ok(t);
        });
    }

    public sealed record CreateTenantRequest(string Name, string Slug);
}
