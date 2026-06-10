using System.Text.Json;
using FrostAura.Foresight.Application.Chaos;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Chaos;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// API surface for the chaos/bust test engine.
///
/// POST /api/chaos                        — start a run (returns batchId)
/// GET  /api/chaos/batches/{batchId}      — all combo rows for a batch, ranked
/// GET  /api/chaos/{id}                   — single combo row
/// GET  /api/chaos/{id}/samples           — per-window sample rows
/// DELETE /api/chaos                       — bulk clear runs (optional ?modelId)
/// GET  /api/chaos/stream                 — SSE progress stream for a batch
/// </summary>
public static class ChaosEndpoints
{
    public static void MapChaosEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chaos").WithTags("chaos");

        // Start a chaos run. Body = ChaosRequest. Returns { batchId }.
        g.MapPost("/", async (ChaosRequest req, ITenantContext tc, IChaosService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var batchId = await svc.RunAsync(req, ct);
                return Results.Ok(new { batchId });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // All combo rows for a batch, ranked (Pass first, then ProfitP50 desc).
        g.MapGet("/batches/{batchId:guid}", async (Guid batchId, ITenantContext tc, IChaosService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.GetBatchAsync(batchId, ct);
            return Results.Ok(rows);
        });

        // Single chaos run row.
        g.MapGet("/{id:guid}", async (Guid id, ITenantContext tc, IChaosService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var run = await svc.GetAsync(id, ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        // Per-window sample rows for a chaos run.
        g.MapGet("/{id:guid}/samples", async (Guid id, int? take, ITenantContext tc, IChaosService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.GetSamplesAsync(id, take ?? 500, ct);
            return Results.Ok(rows);
        });

        // List recent chaos runs (newest first, capped at 100). Optionally filter by batchId.
        g.MapGet("/", async (Guid? batchId, ITenantContext tc, IChaosService svc, ForesightDbContext db, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenantId = tc.TenantId!.Value;

            IQueryable<Domain.Chaos.ChaosRun> query = db.ChaosRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId);

            if (batchId.HasValue)
                query = query.Where(r => r.BatchId == batchId.Value);

            var rows = await query
                .OrderByDescending(r => r.StartedAt)
                .Take(100)
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        // Bulk clear. With no modelId → wipes every chaos run for the tenant; with modelId → clears
        // just that model's runs. Per-window samples cascade. Returns the rows deleted for a toast.
        g.MapDelete("/", async (Guid? modelId, ITenantContext tc, IChaosService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var deleted = await svc.ClearAsync(modelId, ct);
            return Results.Ok(new { deleted });
        });

        // SSE progress stream. The browser subscribes after POST /api/chaos returns a batchId;
        // we forward Progress / Completed / Failed events for that batch.
        g.MapGet("/stream", async (Guid batchId, HttpContext ctx, IChaosEventHub hub, CancellationToken ct) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await foreach (var evt in hub.Subscribe(ct))
            {
                if (evt.BatchId != batchId) continue;
                var json = JsonSerializer.Serialize(new
                {
                    kind = evt.Kind.ToString().ToLowerInvariant(),
                    comboIndex = evt.ComboIndex,
                    totalCombos = evt.TotalCombos,
                    sampleIndex = evt.SampleIndex,
                    totalSamples = evt.TotalSamples,
                    error = evt.Error,
                }, opts);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                if (evt.Kind == ChaosEventKind.Completed || evt.Kind == ChaosEventKind.Failed) break;
            }
        });
    }
}
