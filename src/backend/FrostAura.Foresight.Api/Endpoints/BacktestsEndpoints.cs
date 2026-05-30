using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Live;

namespace FrostAura.Foresight.Api.Endpoints;

public static class BacktestsEndpoints
{
    public static void MapBacktestsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backtests").WithTags("backtests");

        g.MapPost("/", async (BacktestRequest req, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var bt = await svc.RunAsync(req, ct);
                return Results.Ok(bt);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapGet("/", async (Guid? modelId, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.ListAsync(modelId, ct);
            return Results.Ok(rows);
        });

        // Bust-test sweep: fans out into N runs (lookback 1..maxLookbackDays), all sharing a BatchId.
        g.MapPost("/bust-test", async (BustTestRequest req, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var rungs = await svc.RunBustTestAsync(req, ct);
                return Results.Ok(rungs);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // All rungs of a bust-test batch, ordered by lookback day.
        g.MapGet("/batches/{batchId:guid}", async (Guid batchId, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.GetBatchAsync(batchId, ct);
            return Results.Ok(rows);
        });

        g.MapGet("/{id:guid}", async (Guid id, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var bt = await svc.GetAsync(id, ct);
            return bt is null ? Results.NotFound() : Results.Ok(bt);
        });

        g.MapGet("/{id:guid}/bets", async (Guid id, int? take, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.GetBetsAsync(id, take ?? 500, ct);
            return Results.Ok(rows);
        });

        g.MapDelete("/{id:guid}", async (Guid id, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var removed = await svc.DeleteAsync(id, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Bulk clear. With no modelId → wipes every backtest for the tenant; with modelId → clears
        // just that model's runs. Returns the number of rows deleted so the UI can show a confirmation.
        g.MapDelete("/", async (Guid? modelId, ITenantContext tc, IBacktestsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var deleted = await svc.ClearAsync(modelId, ct);
            return Results.Ok(new { deleted });
        });

        // SSE progress stream for a specific backtest. The frontend subscribes after POST /api/backtests
        // returns the "running" row id; we forward Progress / Completed / Failed events as they fire.
        g.MapGet("/{id:guid}/stream", async (Guid id, HttpContext ctx, IBacktestEventHub hub, CancellationToken ct) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await foreach (var evt in hub.Subscribe(ct))
            {
                if (evt.BacktestId != id) continue;
                var json = JsonSerializer.Serialize(new
                {
                    kind = evt.Kind.ToString().ToLowerInvariant(),
                    candlesProcessed = evt.CandlesProcessed,
                    totalCandles = evt.TotalCandles,
                    betsPlaced = evt.BetsPlaced,
                    betsWon = evt.BetsWon,
                    currentBalance = evt.CurrentBalance,
                    finalBalance = evt.FinalBalance,
                    error = evt.Error,
                }, opts);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                if (evt.Kind == BacktestEventKind.Completed || evt.Kind == BacktestEventKind.Failed) break;
            }
        });

        // Catalogue of staking strategies the UI can pick from. The first entry is treated as the
        // default by the form. Adding a new strategy is one new class in Domain.Paper + a line
        // in StakingStrategies.All — the API and UI surface it automatically.
        app.MapGet("/api/staking-strategies", () => Results.Ok(new
        {
            @default = StakingStrategies.DefaultId,
            strategies = StakingStrategies.All.Select(s => new { id = s.Id, name = s.Name, description = s.Description }),
        })).WithTags("backtests");

        // The train endpoint sits alongside model CRUD but belongs here logically — it's the
        // training half of the backtest pair (train on first half, backtest on second).
        var m = app.MapGroup("/api/models").WithTags("models");
        m.MapPost("/{id:guid}/train", async (Guid id, TrainRequest req, ITenantContext tc, IModelTrainingService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                // Trains one variant per supported interval (server-side). The frontend no longer
                // picks an interval/window — interval is part of the model's variant catalogue
                // and the per-interval window is server-controlled (LookbackDaysFor).
                //
                // `HoldoutDays` (default 0) optionally pushes the training window into the past so
                // a downstream backtest can score the model on candles it has never seen. The
                // v6 scoring harness passes 90 so the last 90 days are reserved for backtesting.
                //
                // Training is kicked off on a background task and we return 202 immediately — the
                // fit survives the browser closing, and the UI tracks progress via the model's
                // TrainingStatus (polled on /api/models). Callers that need the result wait for
                // TrainingStatus to clear, then read the model's variants.
                await svc.StartTrainingAsync(id, req.Symbol, req.HoldoutDays ?? 0, req.Interval, ct);
                return Results.Accepted($"/api/models/{id}", new { status = "training" });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Honest out-of-sample walk-forward: rolling-origin retrain + embargo, multi-fold OOS. Returns
        // the aggregate metrics + the guard verdict (the iteration loop's accept/reject). Synchronous
        // and potentially slow (each fold retrains) — keep folds modest and the window focused.
        m.MapPost("/{id:guid}/walk-forward", async (Guid id, WalkForwardApiRequest req, ITenantContext tc, IWalkForwardService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var report = await svc.EvaluateAsync(id, req.Symbol, req.Interval, req.StartTime, req.EndTime, req.Folds ?? 4, ct, req.HorizonSteps ?? 2);
                return Results.Ok(report);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    public sealed record TrainRequest(string Symbol, int? HoldoutDays = null, string? Interval = null);

    public sealed record WalkForwardApiRequest(string Symbol, string Interval, long StartTime, long EndTime, int? Folds = null, int? HorizonSteps = null);
}
