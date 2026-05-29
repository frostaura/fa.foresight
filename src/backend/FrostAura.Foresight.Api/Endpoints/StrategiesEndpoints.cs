using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Strategies;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// CRUD endpoints for staking strategies (both built-in and custom DAG).
///
/// GET  /api/strategies          — list built-in + tenant custom rows, normalised.
/// GET  /api/strategies/{id}     — single row by Guid.
/// POST /api/strategies          — create a custom DAG strategy; validates via FlowValidator.
/// PUT  /api/strategies/{id}     — update name / description / definition (403 on built-ins).
/// DELETE /api/strategies/{id}   — delete (403 on built-ins).
///
/// The existing /api/staking-strategies endpoint is kept intact (it delegates to this listing)
/// so the UI's backtest strategy picker keeps working without changes.
/// </summary>
public static class StrategiesEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapStrategiesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/strategies").WithTags("strategies");

        // ── LIST ─────────────────────────────────────────────────────────────────────────────
        g.MapGet("/", async (ITenantContext tc, ForesightDbContext db, CancellationToken ct) =>
        {
            // Always include global built-ins (TenantId = null).
            // If a tenant is resolved, also include their custom rows.
            IQueryable<Strategy> query = db.Strategies.AsNoTracking()
                .Where(s => s.TenantId == null);

            if (tc.IsResolved && tc.TenantId.HasValue)
                query = db.Strategies.AsNoTracking()
                    .Where(s => s.TenantId == null || s.TenantId == tc.TenantId);

            var rows = await query
                .OrderBy(s => s.IsBuiltIn ? 0 : 1)
                .ThenBy(s => s.Name)
                .ToListAsync(ct);

            return Results.Ok(rows.Select(ToDto));
        });

        // ── GET BY ID ────────────────────────────────────────────────────────────────────────
        g.MapGet("/{id:guid}", async (Guid id, ITenantContext tc, ForesightDbContext db, CancellationToken ct) =>
        {
            var row = await db.Strategies.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id &&
                    (s.TenantId == null || (tc.IsResolved && s.TenantId == tc.TenantId)), ct);
            return row is null ? Results.NotFound() : Results.Ok(ToDto(row));
        });

        // ── CREATE ────────────────────────────────────────────────────────────────────────────
        g.MapPost("/", async (CreateStrategyRequest req, ITenantContext tc, ForesightDbContext db,
            FlowValidator validator, CancellationToken ct) =>
        {
            if (!tc.IsResolved || !tc.TenantId.HasValue)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            // Validate the DAG definition if provided.
            if (!string.IsNullOrWhiteSpace(req.Definition))
            {
                FlowDefinition? parsed;
                try { parsed = JsonSerializer.Deserialize<FlowDefinition>(req.Definition, JsonOpts); }
                catch (JsonException ex) { return Results.BadRequest(new { error = $"Definition is not valid JSON: {ex.Message}" }); }
                if (parsed is null) return Results.BadRequest(new { error = "Definition is empty." });
                if (parsed.DefinitionKind != "strategy")
                    return Results.BadRequest(new { error = "Definition.definitionKind must be \"strategy\"." });
                var validation = validator.Validate(parsed);
                if (!validation.IsValid) return Results.BadRequest(new { error = $"Flow validation failed: {validation.Error}" });
            }

            var now = DateTimeOffset.UtcNow;
            var strategy = new Strategy
            {
                Id = Guid.NewGuid(),
                TenantId = tc.TenantId.Value,
                Name = req.Name,
                Description = req.Description,
                Definition = req.Definition,
                Params = req.Params,
                IsBuiltIn = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Strategies.Add(strategy);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Results.Conflict(new { error = $"A strategy named '{req.Name}' already exists." });
            }

            return Results.Created($"/api/strategies/{strategy.Id}", ToDto(strategy));
        });

        // ── UPDATE ────────────────────────────────────────────────────────────────────────────
        g.MapPut("/{id:guid}", async (Guid id, UpdateStrategyRequest req, ITenantContext tc,
            ForesightDbContext db, FlowValidator validator, CancellationToken ct) =>
        {
            if (!tc.IsResolved || !tc.TenantId.HasValue)
                return Results.Unauthorized();

            var row = await db.Strategies
                .FirstOrDefaultAsync(s => s.Id == id &&
                    (s.TenantId == null || s.TenantId == tc.TenantId), ct);
            if (row is null) return Results.NotFound();
            if (row.IsBuiltIn)
                return Results.Problem("Built-in strategies are read-only.", statusCode: StatusCodes.Status403Forbidden);

            if (req.Name is not null) row.Name = req.Name;
            if (req.Description is not null) row.Description = req.Description;
            if (req.Params is not null) row.Params = req.Params;

            if (req.Definition is not null)
            {
                FlowDefinition? parsed;
                try { parsed = JsonSerializer.Deserialize<FlowDefinition>(req.Definition, JsonOpts); }
                catch (JsonException ex) { return Results.BadRequest(new { error = $"Definition is not valid JSON: {ex.Message}" }); }
                if (parsed is null) return Results.BadRequest(new { error = "Definition is empty." });
                if (parsed.DefinitionKind != "strategy")
                    return Results.BadRequest(new { error = "Definition.definitionKind must be \"strategy\"." });
                var validation = validator.Validate(parsed);
                if (!validation.IsValid) return Results.BadRequest(new { error = $"Flow validation failed: {validation.Error}" });
                row.Definition = req.Definition;
            }

            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(row));
        });

        // ── DELETE ────────────────────────────────────────────────────────────────────────────
        g.MapDelete("/{id:guid}", async (Guid id, ITenantContext tc, ForesightDbContext db, CancellationToken ct) =>
        {
            if (!tc.IsResolved || !tc.TenantId.HasValue)
                return Results.Unauthorized();

            var row = await db.Strategies
                .FirstOrDefaultAsync(s => s.Id == id &&
                    (s.TenantId == null || s.TenantId == tc.TenantId), ct);
            if (row is null) return Results.NotFound();
            if (row.IsBuiltIn)
                return Results.Problem("Built-in strategies are read-only.", statusCode: StatusCodes.Status403Forbidden);

            db.Strategies.Remove(row);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ── DTO helper ────────────────────────────────────────────────────────────────────────────

    private static StrategyDto ToDto(Strategy s) => new(
        s.Id,
        s.Name,
        s.Description,
        s.IsBuiltIn,
        s.Definition is not null ? "dag" : "code",
        s.Definition,
        s.Params,
        s.TenantId,
        s.CreatedAt,
        s.UpdatedAt);

    public sealed record StrategyDto(
        Guid Id,
        string Name,
        string? Description,
        bool IsBuiltIn,
        /// <summary>"code" for built-in code strategies; "dag" for custom DAG flows.</summary>
        string Kind,
        string? Definition,
        string? Params,
        Guid? TenantId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record CreateStrategyRequest(
        string Name,
        string? Description = null,
        /// <summary>Flow DAG JSON with definitionKind="strategy". Required for DAG strategies.</summary>
        string? Definition = null,
        string? Params = null);

    public sealed record UpdateStrategyRequest(
        string? Name = null,
        string? Description = null,
        string? Definition = null,
        string? Params = null);
}
