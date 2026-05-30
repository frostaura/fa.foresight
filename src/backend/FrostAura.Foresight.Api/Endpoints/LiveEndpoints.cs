using System.Text;
using System.Text.Json;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Live;

namespace FrostAura.Foresight.Api.Endpoints;

public static class LiveEndpoints
{
    public static void MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/live").WithTags("live");

        g.MapPost("/predict", async (PredictRequest req, ITenantContext tc, ILivePredictionService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Symbol) || string.IsNullOrWhiteSpace(req.Interval))
                return Results.BadRequest(new { error = "symbol and interval are required" });
            try
            {
                var horizon = req.Horizon ?? 1;
                var p = await svc.PredictAsync(req.Symbol, req.Interval, horizon, ct);
                return Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        // Backfill resolved predictions for the active model across the most recent N closed candles
        // by replaying it over history (deterministic, leakage-free). Fills the chart's hit/miss dots
        // for the stretch before live recording began. Idempotent — skips candles already predicted.
        g.MapPost("/predictions/backfill", async (
            string symbol, string interval, int? candles, ITenantContext tc, ILivePredictionService svc, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(interval))
                return Results.BadRequest(new { error = "symbol and interval are required" });
            try
            {
                var added = await svc.BackfillHistoryAsync(symbol, interval, candles ?? 200, ct);
                return Results.Ok(new { added });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        g.MapGet("/predictions", async (string symbol, string interval, int? take, ITenantContext tc, ILivePredictionService svc, ICalibrationRescaler calibration, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            // Resolve any matured-but-unresolved rows first so the list comes back consistent.
            try { await svc.ResolveMaturedAsync(symbol, interval, ct); } catch { /* best-effort */ }
            var rows = await svc.ListAsync(symbol, interval, Math.Clamp(take ?? 50, 1, 500), ct);

            // Iter-3: project each row to a payload that includes directionUpProbabilityCalibrated
            // alongside the raw value. UI shows calibrated as primary and the raw as a tooltip; the
            // paper-trading abstain filter uses calibrated too. Identity-fallback on cold start.
            var enriched = new List<object>(rows.Count);
            foreach (var p in rows)
            {
                var cal = await calibration.RescaleAsync(tc.TenantId!.Value, interval, p.DirectionUpProbability, ct);
                enriched.Add(new
                {
                    p.Id,
                    p.TenantId,
                    p.Symbol,
                    p.Interval,
                    p.TargetOpenTime,
                    p.AnchorClose,
                    p.PredictedClose,
                    p.PredictedChangePct,
                    p.DirectionUpProbability,
                    directionUpProbabilityCalibrated = cal,
                    p.TargetHitProbability,
                    closeP05 = p.ClosePercentile05,
                    closeP50 = p.ClosePercentile50,
                    closeP95 = p.ClosePercentile95,
                    p.Confidence,
                    p.Reasoning,
                    p.Model,
                    p.SupportingDataJson,
                    p.PromptTraceJson,
                    p.CreatedAt,
                    p.ResolvedAt,
                    p.ActualClose,
                    p.AbsoluteErrorPct,
                    p.DirectionHit
                });
            }
            return Results.Ok(enriched);
        });

        // SSE delta stream — one connection per browser tab. Pushes every `created`/`resolved`
        // event the LivePredictionEventHub fan-outs for the tenant. Multiplexed so all cards on a
        // page share a single socket — keeps us under the browser's HTTP/1.1 six-connections-per-
        // origin limit. The frontend filters by (symbol, interval) client-side. Heartbeat comment
        // every 15s so intermediaries don't idle-close.
        g.MapGet("/predictions/stream", async (
            HttpContext ctx,
            ILivePredictionEventHub hub,
            ICalibrationRescaler calibration,
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
                catch { /* expected on shutdown */ }
            }, linked.Token);

            try
            {
                await foreach (var evt in hub.Subscribe(linked.Token))
                {
                    var name = evt.Kind == LivePredictionEventKind.Created ? "created" : "resolved";
                    // Mirror the /predictions REST shape so the frontend gets calibrated alongside
                    // raw on every SSE frame — without this, predictions arriving over the live
                    // stream never carry directionUpProbabilityCalibrated and the chart marker
                    // logic falls back to raw, which is exactly the inversion bug calibration was
                    // introduced to suppress.
                    var p = evt.Prediction;
                    var cal = await calibration.RescaleAsync(p.TenantId, p.Interval, p.DirectionUpProbability, linked.Token);
                    var enriched = new
                    {
                        p.Id,
                        p.TenantId,
                        p.Symbol,
                        p.Interval,
                        p.TargetOpenTime,
                        p.AnchorClose,
                        p.PredictedClose,
                        p.PredictedChangePct,
                        p.DirectionUpProbability,
                        directionUpProbabilityCalibrated = cal,
                        p.TargetHitProbability,
                        closeP05 = p.ClosePercentile05,
                        closeP50 = p.ClosePercentile50,
                        closeP95 = p.ClosePercentile95,
                        p.Confidence,
                        p.Reasoning,
                        p.Model,
                        p.SupportingDataJson,
                        p.PromptTraceJson,
                        p.CreatedAt,
                        p.ResolvedAt,
                        p.ActualClose,
                        p.AbsoluteErrorPct,
                        p.DirectionHit
                    };
                    var payload = JsonSerializer.Serialize(enriched, jsonOpts);
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
        });

        // Best-effort match against an on-Polymarket "BTC up or down" binary whose window aligns
        // with the prediction's target candle. Returns null when nothing's currently live (which
        // is most of the time — Polymarket runs these in bursts). The frontend falls back to a
        // synthetic 0.5 reference when null.
        g.MapGet("/polymarket-reference", async (
            string symbol,
            long targetOpenTimeMs,
            long intervalMs,
            IEnumerable<IPredictionMarketProvider> providers,
            CancellationToken ct) =>
        {
            var poly = providers.FirstOrDefault(p => p.ProviderId == "polymarket");
            if (poly is null) return Results.Ok<object?>(null);

            // Polymarket up/down markets are BTC-only for now; bail early on anything else.
            if (!symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
                return Results.Ok<object?>(null);

            try
            {
                var results = await poly.DiscoverMarketsRichAsync(new MarketDiscoveryQuery(
                    SearchTerm: "Bitcoin Up or Down",
                    Take: 50,
                    IncludeClosed: false), ct);

                var targetStart = DateTimeOffset.FromUnixTimeMilliseconds(targetOpenTimeMs);
                var targetEnd = DateTimeOffset.FromUnixTimeMilliseconds(targetOpenTimeMs + intervalMs);

                // Pick the market whose resolves-at falls inside [targetStart, targetEnd]. If none
                // matches, fall back to the soonest-resolving live BTC up/down market within ±1
                // interval of target — better than nothing for direction-edge purposes.
                MarketDiscoveryResult? exact = null, near = null;
                foreach (var r in results)
                {
                    var resolvesAt = r.Market.ResolvesAt;
                    if (resolvesAt is null) continue;
                    if (resolvesAt >= targetStart && resolvesAt <= targetEnd) { exact = r; break; }
                    if (near is null && Math.Abs((resolvesAt.Value - targetEnd).TotalMilliseconds) <= intervalMs) near = r;
                }
                var pick = exact ?? near;
                if (pick is null) return Results.Ok<object?>(null);
                return Results.Ok(new
                {
                    providerId = "polymarket",
                    externalId = pick.Market.ExternalId,
                    question = pick.Market.Question,
                    yesPrice = pick.YesPrice,
                    noPrice = pick.NoPrice,
                    resolvesAt = pick.Market.ResolvesAt,
                    exactMatch = exact != null
                });
            }
            catch (Exception ex)
            {
                // Discovery is best-effort; surface as null rather than blowing up the page.
                return Results.Ok<object?>(new { error = ex.Message });
            }
        });
    }

    public sealed record PredictRequest(string Symbol, string Interval, int? Horizon);
}
