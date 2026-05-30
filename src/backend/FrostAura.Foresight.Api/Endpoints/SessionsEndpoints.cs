using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Paper;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Unified sessions API — merges paper and live sessions into one normalised shape.
///
/// GET  /api/sessions              — list all sessions (paper + live), newest first
/// GET  /api/sessions?kind=paper   — filter to paper only
/// GET  /api/sessions?kind=live    — filter to live only
/// GET  /api/sessions?active=true  — only sessions without a stoppedAt
/// POST /api/sessions              — create a session (delegates to paper or live engine)
/// DELETE /api/sessions/{id}       — stop a session (routes by id to paper or live)
/// </summary>
public static class SessionsEndpoints
{
    public static void MapSessionsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("sessions");

        // GET /api/sessions — unified list of paper + live sessions.
        g.MapGet("/", async (
            string? kind,
            bool? active,
            ITenantContext tc,
            IPaperTradingService paperSvc,
            ILiveSessionEngine liveSvc,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();

            var results = new List<SessionDto>();

            if (kind is null || kind == "paper")
            {
                var paperSessions = await paperSvc.ListAsync(ct);
                foreach (var s in paperSessions)
                {
                    if (active == true && s.StoppedAt is not null) continue;
                    var betsPlaced = s.Bets.Count;
                    var betsWon = s.Bets.Count(b => b.Outcome == "win");
                    results.Add(new SessionDto(
                        Id: s.Id.ToString(),
                        Mode: "paper",
                        Symbol: s.Symbol,
                        Interval: s.Interval,
                        ModelId: null,
                        StrategyId: s.StrategyId,
                        StartedAt: s.StartedAt,
                        StoppedAt: s.StoppedAt,
                        InitialBalance: s.InitialBalance,
                        CurrentBalance: s.CurrentBalance,
                        CurrentBetSize: s.CurrentBetSize,
                        BetsPlaced: betsPlaced,
                        BetsWon: betsWon,
                        Bust: s.Bust,
                        ReservedAmount: null
                    ));
                }
            }

            if (kind is null || kind == "live")
            {
                var liveSessions = await liveSvc.ListAsync(ct);
                foreach (var s in liveSessions)
                {
                    if (active == true && s.StoppedAt is not null) continue;
                    var betsPlaced = s.Bets.Count;
                    var betsWon = s.Bets.Count(b => b.Outcome == "win");
                    results.Add(new SessionDto(
                        Id: s.Id.ToString(),
                        Mode: s.Mode, // "live" or "paper" (live engine may hold both)
                        Symbol: s.Symbol,
                        Interval: s.Interval,
                        ModelId: null,
                        StrategyId: s.StrategyId,
                        StartedAt: s.StartedAt,
                        StoppedAt: s.StoppedAt,
                        InitialBalance: s.InitialBalance,
                        CurrentBalance: s.CurrentBalance,
                        CurrentBetSize: s.CurrentBetSize,
                        BetsPlaced: betsPlaced,
                        BetsWon: betsWon,
                        Bust: s.Bust,
                        ReservedAmount: s.ReservedAmount
                    ));
                }
            }

            // Newest first.
            results.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            return Results.Ok(results);
        });

        // POST /api/sessions — create a session.
        g.MapPost("/", async (
            CreateSessionRequest req,
            ITenantContext tc,
            IPaperTradingService paperSvc,
            ILiveSessionEngine liveSvc,
            ILiveTradingArm arm,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Symbol) || string.IsNullOrWhiteSpace(req.Interval))
                return Results.BadRequest(new { error = "symbol and interval are required" });

            try
            {
                if (req.Mode == "paper")
                {
                    var s = await paperSvc.StartAsync(
                        req.Symbol, req.Interval, req.InitialBalance, req.InitialBetSize,
                        req.StrategyId, req.Gated, ct, startedAt: req.StartTime);
                    var betsPlaced = s.Bets.Count;
                    var betsWon = s.Bets.Count(b => b.Outcome == "win");
                    var dto = new SessionDto(
                        Id: s.Id.ToString(),
                        Mode: "paper",
                        Symbol: s.Symbol,
                        Interval: s.Interval,
                        ModelId: null,
                        StrategyId: s.StrategyId,
                        StartedAt: s.StartedAt,
                        StoppedAt: s.StoppedAt,
                        InitialBalance: s.InitialBalance,
                        CurrentBalance: s.CurrentBalance,
                        CurrentBetSize: s.CurrentBetSize,
                        BetsPlaced: betsPlaced,
                        BetsWon: betsWon,
                        Bust: s.Bust,
                        ReservedAmount: null
                    );
                    return Results.Created($"/api/sessions/{s.Id}", dto);
                }

                if (req.Mode == "live")
                {
                    // Guard: live trading must be armed before any live session can start.
                    if (!arm.IsArmed(tc.TenantId!.Value))
                    {
                        return Results.UnprocessableEntity(new
                        {
                            error = "Live trading is disarmed — arm via /api/golive first."
                        });
                    }

                    var startReq = new LiveSessionStartRequest(
                        Venue: "polymarket",
                        Symbol: req.Symbol,
                        Interval: req.Interval,
                        InitialBalance: req.InitialBalance,
                        InitialBetSize: req.InitialBetSize,
                        StrategyId: req.StrategyId,
                        Gated: req.Gated
                    );
                    var s = await liveSvc.StartAsync(startReq, ct);
                    var betsPlaced = s.Bets.Count;
                    var betsWon = s.Bets.Count(b => b.Outcome == "win");
                    var dto = new SessionDto(
                        Id: s.Id.ToString(),
                        Mode: s.Mode,
                        Symbol: s.Symbol,
                        Interval: s.Interval,
                        ModelId: null,
                        StrategyId: s.StrategyId,
                        StartedAt: s.StartedAt,
                        StoppedAt: s.StoppedAt,
                        InitialBalance: s.InitialBalance,
                        CurrentBalance: s.CurrentBalance,
                        CurrentBetSize: s.CurrentBetSize,
                        BetsPlaced: betsPlaced,
                        BetsWon: betsWon,
                        Bust: s.Bust,
                        ReservedAmount: s.ReservedAmount
                    );
                    return Results.Created($"/api/sessions/{s.Id}", dto);
                }

                return Results.BadRequest(new { error = "mode must be 'paper' or 'live'" });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("[409]") || ex.Message.Contains("already exists"))
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DELETE /api/sessions/{id} — stop a session by id (routes to paper or live by probing both).
        g.MapDelete("/{id:guid}", async (
            Guid id,
            ITenantContext tc,
            IPaperTradingService paperSvc,
            ILiveSessionEngine liveSvc,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();

            // Try live first (has a direct GetByIdAsync), then paper.
            var liveSession = await liveSvc.GetByIdAsync(id, ct);
            if (liveSession is not null)
            {
                var stopped = await liveSvc.StopAsync(id, ct);
                return stopped is null ? Results.NotFound() : Results.Ok(ToDto(stopped));
            }

            // Check paper sessions by id.
            var paperSession = await paperSvc.GetByIdAsync(id, ct);
            if (paperSession is not null)
            {
                var stopped = await paperSvc.StopAsync(paperSession.Symbol, paperSession.Interval, ct, paperSession.Label);
                return stopped is null ? Results.NotFound() : Results.Ok(ToPaperDto(stopped));
            }

            return Results.NotFound();
        });
    }

    private static SessionDto ToDto(Domain.Live.LiveSession s)
    {
        var betsPlaced = s.Bets.Count;
        var betsWon = s.Bets.Count(b => b.Outcome == "win");
        return new SessionDto(
            Id: s.Id.ToString(),
            Mode: s.Mode,
            Symbol: s.Symbol,
            Interval: s.Interval,
            ModelId: null,
            StrategyId: s.StrategyId,
            StartedAt: s.StartedAt,
            StoppedAt: s.StoppedAt,
            InitialBalance: s.InitialBalance,
            CurrentBalance: s.CurrentBalance,
            CurrentBetSize: s.CurrentBetSize,
            BetsPlaced: betsPlaced,
            BetsWon: betsWon,
            Bust: s.Bust,
            ReservedAmount: s.ReservedAmount
        );
    }

    private static SessionDto ToPaperDto(Domain.Paper.PaperSession s)
    {
        var betsPlaced = s.Bets.Count;
        var betsWon = s.Bets.Count(b => b.Outcome == "win");
        return new SessionDto(
            Id: s.Id.ToString(),
            Mode: "paper",
            Symbol: s.Symbol,
            Interval: s.Interval,
            ModelId: null,
            StrategyId: s.StrategyId,
            StartedAt: s.StartedAt,
            StoppedAt: s.StoppedAt,
            InitialBalance: s.InitialBalance,
            CurrentBalance: s.CurrentBalance,
            CurrentBetSize: s.CurrentBetSize,
            BetsPlaced: betsPlaced,
            BetsWon: betsWon,
            Bust: s.Bust,
            ReservedAmount: null
        );
    }
}

/// <summary>
/// Normalised session shape returned by all /api/sessions endpoints.
/// </summary>
public sealed record SessionDto(
    string Id,
    string Mode,           // "paper" | "live"
    string Symbol,
    string Interval,
    string? ModelId,
    string StrategyId,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    decimal InitialBalance,
    decimal CurrentBalance,
    decimal CurrentBetSize,
    int BetsPlaced,
    int BetsWon,
    bool Bust,
    decimal? ReservedAmount
);

public sealed record CreateSessionRequest(
    string Mode,           // "paper" | "live"
    string Symbol,
    string Interval,
    decimal InitialBalance,
    decimal InitialBetSize,
    string? StrategyId,
    bool Gated = false,
    // Optional back-dated start for PAPER sessions only (ignored for live — live trades a real venue
    // in real time). Past instant → the session back-bets from there up to now; null = start now.
    DateTimeOffset? StartTime = null
);
