using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

public static class ModelsEndpoints
{
    public static void MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/models").WithTags("models");

        g.MapGet("/", async (ITenantContext tc, IModelsService svc, ForesightDbContext db,
            bool includeArchived = false, CancellationToken ct = default) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var rows = await svc.ListAsync(ct, includeArchived);

            // Enrich each model row with the most-recent completed backtest hit-rate per interval.
            // Source of truth = the Backtest table; latest = highest StartedAt for each
            // (ModelId, Interval) at Symbol=BTCUSDT, Status=complete, HitRate not null. averageScore
            // averages whichever intervals have data (null when none) so a freshly-created model
            // doesn't read as a zero-score disaster on the leaderboard.
            // EF Core 10 can't translate the nested GroupBy → OrderByDescending → First pattern
            // (EmptyProjectionMember error). Pull all matching rows, group in memory — for a
            // single tenant's models with at most ~hundreds of backtest rows, this is well under
            // a millisecond.
            var modelIds = rows.Select(r => r.Id).ToList();
            var allCompleted = await db.Backtests.AsNoTracking()
                .Where(b => modelIds.Contains(b.ModelId)
                            && b.Symbol == "BTCUSDT"
                            && b.Status == "complete"
                            && b.HitRate != null)
                .Select(b => new { b.ModelId, b.Interval, b.StartedAt, HitRatePct = b.HitRate!.Value * 100m })
                .ToListAsync(ct);

            var byModel = allCompleted
                .GroupBy(x => new { x.ModelId, x.Interval })
                .Select(g => g.OrderByDescending(x => x.StartedAt).First())
                .GroupBy(x => x.ModelId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Interval, x => x.HitRatePct));

            var enriched = rows.Select(m =>
            {
                byModel.TryGetValue(m.Id, out var scores);
                decimal? avg = scores is { Count: > 0 } ? scores.Values.Average() : null;
                return new
                {
                    m.Id,
                    m.TenantId,
                    m.Name,
                    m.Description,
                    m.Kind,
                    m.SupportsBacktesting,
                    m.IsBuiltIn,
                    m.IsDefault,
                    m.IsArchived,
                    m.Definition,
                    m.TrainedState,
                    m.TrainingValidationAccuracy,
                    m.BacktestAccuracy,
                    m.LastTrainedAt,
                    m.TrainStartMs,
                    m.TrainEndMs,
                    m.TrainSymbol,
                    m.TrainInterval,
                    m.TrainingStatus,
                    m.TrainingStartedAt,
                    m.TrainingError,
                    m.SimpleDescription,
                    m.TechnicalDescription,
                    m.CreatedAt,
                    m.UpdatedAt,
                    ScoresByInterval = scores ?? new Dictionary<string, decimal>(),
                    AverageScore = avg,
                };
            }).ToList();

            return Results.Ok(enriched);
        });

        // SSE delta stream of model-lifecycle events (training started/completed/failed) for the
        // tenant. One shared socket per browser tab; the client invalidates its model cache on each
        // event. Replaces the old fixed-interval poll of GET /api/models that ran while any model was
        // training. Heartbeat comment every 15s so intermediaries don't idle-close the connection.
        g.MapGet("/stream", async (HttpContext ctx, ITenantContext tc, IModelEventHub hub, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenantId = tc.TenantId!.Value;

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);

            var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            using var heartbeat = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, heartbeat.Token);

            var pulse = Task.Run(async () =>
            {
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), linked.Token);
                        await ctx.Response.WriteAsync(": ping\n\n", linked.Token);
                        await ctx.Response.Body.FlushAsync(linked.Token);
                    }
                }
                catch { /* expected on shutdown */ }
            }, linked.Token);

            try
            {
                await foreach (var evt in hub.Subscribe(linked.Token))
                {
                    if (evt.TenantId != tenantId) continue;
                    var name = evt.Kind switch
                    {
                        ModelEventKind.Training => "training",
                        ModelEventKind.Trained => "trained",
                        ModelEventKind.Failed => "failed",
                        _ => "unknown"
                    };
                    var payload = JsonSerializer.Serialize(new { modelId = evt.ModelId, error = evt.Error }, jsonOpts);
                    var sb = new StringBuilder()
                        .Append("event: ").Append(name).Append('\n')
                        .Append("data: ").Append(payload).Append("\n\n");
                    await ctx.Response.WriteAsync(sb.ToString(), linked.Token);
                    await ctx.Response.Body.FlushAsync(linked.Token);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                heartbeat.Cancel();
                try { await pulse; } catch { }
            }
            return Results.Empty;
        });

        g.MapGet("/catalogue", (FrostAura.Foresight.Application.Flow.NodeRegistry registry) =>
            Results.Text(registry.GetCatalogueDescriptor(), "application/json"));

        // Whitelist of symbols + intervals the platform actually supports. Frontend dropdowns
        // populate from this; backend services validate against the same constants so a freetext
        // POST can't slip an unsupported pair through.
        app.MapGet("/api/symbols", () => Results.Ok(new
        {
            symbols = SupportedSymbols.All,
            intervals = SupportedSymbols.Intervals,
        })).WithTags("symbols");

        // Cached historical candles for a window — used by the backtest popover's Visual tab to
        // render a price line under the hit/miss markers. Tenant-agnostic since candles are global
        // market data shared by every tenant's cache.
        app.MapGet("/api/historical-candles", async (
            string symbol, string interval, long start, long end,
            FrostAura.Foresight.Domain.Ports.IHistoricalCandleProvider provider,
            CancellationToken ct) =>
        {
            if (!SupportedSymbols.IsSupportedSymbol(symbol)) return Results.BadRequest(new { error = $"Unsupported symbol '{symbol}'." });
            if (!SupportedSymbols.IsSupportedInterval(interval)) return Results.BadRequest(new { error = $"Unsupported interval '{interval}'." });
            if (end <= start) return Results.BadRequest(new { error = "end must be > start" });
            var rows = await provider.GetRangeAsync(symbol, interval, start, end, ct);
            // Project to a compact (t, c) shape — the popover only needs close prices, not OHLC.
            return Results.Ok(rows.Select(r => new { t = r.OpenTime, c = r.Close }));
        }).WithTags("historical-candles");

        g.MapGet("/{id:guid}", async (Guid id, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var m = await svc.GetAsync(id, ct);
            return m is null ? Results.NotFound() : Results.Ok(m);
        });

        g.MapPost("/", async (CreateModelRequest req, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var m = await svc.CreateAsync(req.Name, req.Description, req.Kind, req.SupportsBacktesting, req.Definition, ct);
                return Results.Ok(m);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateModelRequest req, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var m = await svc.UpdateAsync(id, req.Name, req.Description, req.Definition, ct);
                return m is null ? Results.NotFound() : Results.Ok(m);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapDelete("/{id:guid}", async (Guid id, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var removed = await svc.DeleteAsync(id, ct);
                return removed ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException) { return Results.Forbid(); }
        });

        g.MapPost("/{id:guid}/duplicate", async (Guid id, DuplicateRequest req, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var m = await svc.DuplicateAsync(id, req.Name, ct);
            return m is null ? Results.NotFound() : Results.Ok(m);
        });

        g.MapPost("/{id:guid}/set-default", async (Guid id, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var m = await svc.SetDefaultAsync(id, ct);
            return m is null ? Results.NotFound() : Results.Ok(m);
        });

        // Archive / unarchive a model. Archived models are excluded from the default GET /api/models
        // listing but preserved for history. Allowed on any model including built-ins.
        g.MapPost("/{id:guid}/archive", async (Guid id, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var found = await svc.ArchiveAsync(id, archive: true, ct);
            return found ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/unarchive", async (Guid id, ITenantContext tc, IModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var found = await svc.ArchiveAsync(id, archive: false, ct);
            return found ? Results.NoContent() : Results.NotFound();
        });

        // Leakage-aware backtest. Auto-picks a backtest window strictly outside the model's
        // training range so the resulting hit rate is genuinely out-of-sample. Compare the
        // returned run's HitRate to model.TrainingValidationAccuracy — the gap is the overfit
        // penalty. Uses Flat staking + small bet so a single lucky path can't masquerade as a
        // strategy validation (the metric being tested is accuracy, not P&L).
        g.MapPost("/{id:guid}/honest-backtest", async (
            Guid id,
            ITenantContext tc,
            IModelsService modelsSvc,
            IBacktestsService backtestSvc,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var model = await modelsSvc.GetAsync(id, ct);
            if (model is null) return Results.NotFound();
            if (!model.SupportsBacktesting)
                return Results.BadRequest(new { error = "Model does not support backtesting (LLM-only models can't be honest-backtested)." });
            if (model.TrainStartMs is null || model.TrainEndMs is null ||
                model.TrainSymbol is null || model.TrainInterval is null)
                return Results.BadRequest(new { error = "Model has no recorded training range. Retrain the model to enable an honest backtest." });

            // Out-of-sample window selection. Two candidate ranges, in preference order:
            //   (A) AFTER training — [TrainEndMs+1, now]. Cleanest: data the model never saw, and
            //       newest, so it reflects current regime. Often empty when training ran on the
            //       most recent N days (default Train button = "last 14 days").
            //   (B) BEFORE training — [max(now - 365d, 0), TrainStartMs - 1]. Falls within the
            //       365-day cache horizon, predates anything the trainer saw. Used when (A) is
            //       too short to be statistically meaningful.
            // Both candidates are strictly outside [TrainStartMs, TrainEndMs] — no overlap is
            // possible regardless of which one we pick.
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const long maxLookbackMs = 365L * 86_400_000L;
            const long minRangeMs = 30L * 86_400_000L;

            var afterStart = model.TrainEndMs.Value + 1;
            var afterEnd = nowMs;
            var beforeEnd = model.TrainStartMs.Value - 1;
            var beforeStart = Math.Max(nowMs - maxLookbackMs, 0);

            long oosStart, oosEnd;
            if (afterEnd - afterStart >= minRangeMs)
            {
                oosStart = afterStart;
                oosEnd = afterEnd;
            }
            else if (beforeEnd - beforeStart >= minRangeMs)
            {
                oosStart = beforeStart;
                oosEnd = beforeEnd;
            }
            else
            {
                return Results.BadRequest(new
                {
                    error = "Not enough out-of-sample data outside the training range to run an honest backtest (need at least 30 days on one side). Retrain on a smaller window or wait for more candles to accrue.",
                    trainStartMs = model.TrainStartMs.Value,
                    trainEndMs = model.TrainEndMs.Value,
                    afterRangeDays = (afterEnd - afterStart) / 86_400_000.0,
                    beforeRangeDays = (beforeEnd - beforeStart) / 86_400_000.0,
                });
            }

            try
            {
                // Flat strategy + $10 bet + $1000 bankroll + allowBorrow=true → the run can't halt
                // on bust, so accuracy is computed over the full window. The hit rate that comes
                // back is the honest one to compare against TrainingValidationAccuracy.
                var bt = await backtestSvc.RunAsync(new BacktestRequest(
                    ModelId: id,
                    Symbol: model.TrainSymbol,
                    Interval: model.TrainInterval,
                    StartTime: oosStart,
                    EndTime: oosEnd,
                    InitialBalance: 1000m,
                    InitialBetSize: 10m,
                    AllowBorrow: true,
                    BatchId: null,
                    StrategyId: "flat"), ct);
                return Results.Ok(new
                {
                    backtestId = bt.Id,
                    trainStartMs = model.TrainStartMs.Value,
                    trainEndMs = model.TrainEndMs.Value,
                    outOfSampleStartMs = oosStart,
                    outOfSampleEndMs = oosEnd,
                    trainingValidationAccuracy = model.TrainingValidationAccuracy,
                });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Active per-(symbol, interval) selection.
        var a = app.MapGroup("/api/active-models").WithTags("active-models");

        a.MapGet("/", async (ITenantContext tc, ActiveModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            return Results.Ok(await svc.ListAsync(ct));
        });

        a.MapPut("/", async (SetActiveRequest req, ITenantContext tc, ActiveModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            try
            {
                var row = await svc.SetAsync(req.Symbol, req.Interval, req.ModelId, ct);
                return Results.Ok(row);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        a.MapDelete("/{symbol}/{interval}", async (string symbol, string interval, ITenantContext tc, ActiveModelsService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var removed = await svc.ClearAsync(symbol, interval, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });
    }

    public sealed record CreateModelRequest(string Name, string? Description, string Kind, bool SupportsBacktesting, string Definition);
    public sealed record UpdateModelRequest(string? Name, string? Description, string? Definition);
    public sealed record DuplicateRequest(string Name);
    public sealed record SetActiveRequest(string Symbol, string Interval, Guid ModelId);
}
