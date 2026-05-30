using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Paper;

namespace FrostAura.Foresight.Api.Endpoints;

public static class PaperEndpoints
{
    public static void MapPaperEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/paper").WithTags("paper");

        // List all active sessions for the tenant.
        g.MapGet("/sessions", async (ITenantContext tc, IPaperTradingService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var list = await svc.ListAsync(ct);
            return Results.Ok(list);
        });

        // Get a single active session for (symbol, interval). Includes the full bet ledger.
        g.MapGet("/sessions/{symbol}/{interval}", async (
            string symbol, string interval, ITenantContext tc, IPaperTradingService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            // "No active session for this (symbol, interval)" is a normal empty result on every page
            // load, not an error — return 200 with a null body so the client falls back gracefully
            // without the browser logging a 404.
            var s = await svc.GetAsync(symbol, interval, ct);
            return Results.Ok(s);
        });

        // Start a new session. Fails 409 if one is already active for that (symbol, interval).
        g.MapPost("/sessions", async (
            StartSessionRequest req, ITenantContext tc, IPaperTradingService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Symbol) || string.IsNullOrWhiteSpace(req.Interval))
                return Results.BadRequest(new { error = "symbol and interval are required" });
            try
            {
                var s = await svc.StartAsync(req.Symbol, req.Interval, req.InitialBalance, req.InitialBetSize, req.StrategyId, req.Gated, ct);
                return Results.Created($"/api/paper/sessions/{req.Symbol}/{req.Interval}", s);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Stop (or dismiss-after-bust) an active session.
        g.MapDelete("/sessions/{symbol}/{interval}", async (
            string symbol, string interval, ITenantContext tc, IPaperTradingService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var stopped = await svc.StopAsync(symbol, interval, ct);
            return stopped is null ? Results.NotFound() : Results.Ok(stopped);
        });

        // SSE delta stream of every paper-trading state change for the tenant. Mirrors the
        // /api/live/predictions/stream pattern: one shared socket per tab; client filters by
        // (symbol, interval).
        g.MapGet("/sessions/stream", async (
            HttpContext ctx,
            IPaperTradingEventHub hub,
            CancellationToken ct) =>
        {
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
                catch { }
            }, linked.Token);

            try
            {
                await foreach (var evt in hub.Subscribe(linked.Token))
                {
                    var name = evt.Kind switch
                    {
                        PaperTradingEventKind.SessionStarted => "session-started",
                        PaperTradingEventKind.SessionStopped => "session-stopped",
                        PaperTradingEventKind.SessionBust => "session-bust",
                        PaperTradingEventKind.BetPlaced => "bet-placed",
                        PaperTradingEventKind.BetResolved => "bet-resolved",
                        _ => "unknown"
                    };
                    var payload = JsonSerializer.Serialize(new { evt.Session, evt.Bet }, jsonOpts);
                    var sb = new StringBuilder()
                        .Append("event: ").Append(name).Append('\n')
                        .Append("data: ").Append(payload).Append("\n\n");
                    await ctx.Response.WriteAsync(sb.ToString(), linked.Token);
                    await ctx.Response.Body.FlushAsync(linked.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                heartbeat.Cancel();
                try { await pulse; } catch { }
            }
        });
    }
}

public sealed record StartSessionRequest(string Symbol, string Interval, decimal InitialBalance, decimal InitialBetSize, string? StrategyId, bool Gated = false);
