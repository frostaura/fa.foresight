using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Paper;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Strategies;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Paper;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Migrate live trading state between Foresight instances. The motivating use case: copy a local
/// machine's running paper-trading sessions into a freshly-deployed prod instance so they resume
/// trading there without losing their bet ledger, balance, or Martingale escalation level.
///
/// GET  /api/sessions/export   — snapshot the tenant's active sessions (paper + live) plus the
///                               dependency closure needed to keep trading: any CUSTOM staking
///                               strategies they reference, any CUSTOM models behind the cards they
///                               trade, and the per-card active-model mappings. Built-in strategies
///                               and models are NOT exported — they're seeded identically in every
///                               instance, so the importing side already has them.
/// POST /api/sessions/import   — graft a bundle into the resolved tenant. Everything is re-stamped
///                               to the importing tenant, dependency rows are upserted first, then
///                               sessions are recreated with their full bet history. An imported
///                               active paper session resumes the moment it lands: the gap-filler
///                               keys prediction demand off active paper sessions, and the paper
///                               processor settles/places on the next tick.
///
/// Why this is "complete": a resumed paper session needs (1) its own row + bets (balance + the last
/// settled bet drives the next stake), (2) predictions flowing for its (symbol, interval) — which
/// only happens while an active session exists, so importing the session is itself the trigger, and
/// (3) the SAME model producing those predictions — carried via the custom-model + active-model rows.
///
/// Safety: import is idempotent (a session whose id already exists is skipped), transactional (a
/// failure rolls the whole bundle back), and conflict-aware (an active session colliding on the
/// active-uniqueness key or config hash is skipped unless <c>overwriteActive=true</c> stops the
/// incumbent first). Importing live sessions re-writes their reservation-ledger audit row so the
/// pUSD invariant stays consistent; live placement remains gated by the arm + LiveTrading flag, so
/// a copied live session never auto-fires real orders on an unarmed prod box.
/// </summary>
public static class SessionsTransferEndpoints
{
    public static void MapSessionsTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("sessions");

        // ── Export ─────────────────────────────────────────────────────────────────
        // kind=paper|live|all (default all); includeStopped=true to snapshot history too.
        g.MapGet("/export", async (
            string? kind,
            bool? includeStopped,
            ITenantContext tc,
            ForesightDbContext db,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tid = tc.TenantId!.Value;
            var wantPaper = kind is null or "all" or "paper";
            var wantLive = kind is null or "all" or "live";
            var withStopped = includeStopped == true;

            var paperSessions = new List<PaperSession>();
            if (wantPaper)
            {
                var q = db.PaperSessions.AsNoTracking().Include(s => s.Bets).Where(s => s.TenantId == tid);
                if (!withStopped) q = q.Where(s => s.StoppedAt == null);
                paperSessions = await q.ToListAsync(ct);
            }

            var liveSessions = new List<LiveSession>();
            if (wantLive)
            {
                var q = db.LiveSessions.AsNoTracking().Include(s => s.Bets).Where(s => s.TenantId == tid);
                if (!withStopped) q = q.Where(s => s.StoppedAt == null);
                liveSessions = await q.ToListAsync(ct);
            }

            // Dependency closure ────────────────────────────────────────────────────
            // Custom (DAG) strategies referenced by a Guid strategy id that isn't a built-in.
            var strategyGuids = paperSessions.Select(s => s.StrategyId)
                .Concat(liveSessions.Select(s => s.StrategyId))
                .Where(id => Guid.TryParse(id, out _) && !StakingStrategies.IsKnown(id))
                .Select(Guid.Parse)
                .Distinct()
                .ToList();
            var strategies = strategyGuids.Count == 0
                ? new List<Strategy>()
                : await db.Strategies.AsNoTracking().Where(s => strategyGuids.Contains(s.Id)).ToListAsync(ct);

            // Active-model card mappings for every (symbol, interval) we're exporting, plus the
            // CUSTOM models behind them (tenant-owned; built-in/global models are seeded in prod).
            var pairs = paperSessions.Select(s => (s.Symbol, s.Interval))
                .Concat(liveSessions.Select(s => (s.Symbol, s.Interval)))
                .ToHashSet();
            var allActive = await db.ActiveModels.AsNoTracking().Where(a => a.TenantId == tid).ToListAsync(ct);
            var activeModels = allActive.Where(a => pairs.Contains((a.Symbol, a.Interval))).ToList();
            var modelIds = activeModels.Select(a => a.ModelId).Distinct().ToList();
            var models = modelIds.Count == 0
                ? new List<Model>()
                // Only carry tenant-owned models; null-tenant built-ins already exist on the target.
                : await db.Models.AsNoTracking().Where(m => modelIds.Contains(m.Id) && m.TenantId != null).ToListAsync(ct);

            var bundle = new SessionTransferBundle
            {
                FormatVersion = 1,
                ExportedAt = DateTimeOffset.UtcNow,
                SourceTenantSlug = tc.TenantSlug,
                PaperSessions = paperSessions,
                LiveSessions = liveSessions,
                Strategies = strategies,
                Models = models,
                ActiveModels = activeModels
            };
            return Results.Ok(bundle);
        });

        // ── Import ─────────────────────────────────────────────────────────────────
        // overwriteActive=true stops an incumbent active session that collides; activate=false
        // imports sessions as stopped history (no resume).
        g.MapPost("/import", async (
            SessionTransferBundle bundle,
            bool? overwriteActive,
            bool? activate,
            ITenantContext tc,
            ForesightDbContext db,
            IAccountLedger ledger,
            IPaperTradingEventHub paperEvents,
            ILogger<SessionTransferBundle> logger,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (bundle is null) return Results.BadRequest(new { error = "missing bundle body" });
            if (bundle.FormatVersion != 1)
                return Results.BadRequest(new { error = $"unsupported formatVersion {bundle.FormatVersion}; expected 1" });

            var tid = tc.TenantId!.Value;
            var overwrite = overwriteActive == true;
            var doActivate = activate ?? true;
            var now = DateTimeOffset.UtcNow;

            var results = new List<SessionImportResult>();
            var warnings = new List<string>();
            int stratImported = 0, modelImported = 0, activeModelImported = 0;
            int paperImported = 0, paperSkipped = 0, liveImported = 0, liveSkipped = 0;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // 1. Strategies — upsert, building an old-id → effective-id remap.
                var strategyRemap = new Dictionary<Guid, Guid>();
                foreach (var s in bundle.Strategies ?? new())
                {
                    var byId = await db.Strategies.FirstOrDefaultAsync(x => x.Id == s.Id, ct);
                    if (byId is not null) { strategyRemap[s.Id] = byId.Id; continue; }
                    var targetTenant = s.TenantId.HasValue ? tid : (Guid?)null;
                    var byName = await db.Strategies.FirstOrDefaultAsync(x => x.TenantId == targetTenant && x.Name == s.Name, ct);
                    if (byName is not null) { strategyRemap[s.Id] = byName.Id; continue; }
                    db.Strategies.Add(new Strategy
                    {
                        Id = s.Id,
                        TenantId = targetTenant,
                        Name = s.Name,
                        Description = s.Description,
                        Definition = s.Definition,
                        Params = s.Params,
                        IsBuiltIn = s.IsBuiltIn,
                        SimpleDescription = s.SimpleDescription,
                        TechnicalDescription = s.TechnicalDescription,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt
                    });
                    strategyRemap[s.Id] = s.Id;
                    stratImported++;
                }

                // 2. Models — upsert, remapping old-id → effective-id. Trained state is preserved so
                //    predictions reproduce; in-flight training status is cleared (the job didn't come
                //    across) and IsDefault is forced off so we never clobber the target's default.
                var modelRemap = new Dictionary<Guid, Guid>();
                foreach (var m in bundle.Models ?? new())
                {
                    var byId = await db.Models.FirstOrDefaultAsync(x => x.Id == m.Id, ct);
                    if (byId is not null) { modelRemap[m.Id] = byId.Id; continue; }
                    var targetTenant = m.TenantId.HasValue ? tid : (Guid?)null;
                    var byName = await db.Models.FirstOrDefaultAsync(x => x.TenantId == targetTenant && x.Name == m.Name, ct);
                    if (byName is not null) { modelRemap[m.Id] = byName.Id; continue; }
                    db.Models.Add(new Model
                    {
                        Id = m.Id,
                        TenantId = targetTenant,
                        Name = m.Name,
                        Description = m.Description,
                        Kind = m.Kind,
                        SupportsBacktesting = m.SupportsBacktesting,
                        IsBuiltIn = m.IsBuiltIn,
                        IsDefault = false,
                        Definition = m.Definition,
                        TrainedState = m.TrainedState,
                        TrainingValidationAccuracy = m.TrainingValidationAccuracy,
                        BacktestAccuracy = m.BacktestAccuracy,
                        LastTrainedAt = m.LastTrainedAt,
                        TrainStartMs = m.TrainStartMs,
                        TrainEndMs = m.TrainEndMs,
                        TrainSymbol = m.TrainSymbol,
                        TrainInterval = m.TrainInterval,
                        TrainingStatus = null,
                        TrainingStartedAt = null,
                        TrainingError = null,
                        IsArchived = m.IsArchived,
                        SimpleDescription = m.SimpleDescription,
                        TechnicalDescription = m.TechnicalDescription,
                        CreatedAt = m.CreatedAt,
                        UpdatedAt = m.UpdatedAt
                    });
                    modelRemap[m.Id] = m.Id;
                    modelImported++;
                }

                await db.SaveChangesAsync(ct);

                // 3. Active-model card mappings — re-stamp tenant, remap model id, upsert by card.
                foreach (var a in bundle.ActiveModels ?? new())
                {
                    var modelId = modelRemap.TryGetValue(a.ModelId, out var rm) ? rm : a.ModelId;
                    if (!await db.Models.AnyAsync(x => x.Id == modelId, ct))
                    {
                        warnings.Add($"active-model {a.Symbol}/{a.Interval}: model {a.ModelId} not found on target — left on existing/default model.");
                        continue;
                    }
                    var existing = await db.ActiveModels.FirstOrDefaultAsync(
                        x => x.TenantId == tid && x.Symbol == a.Symbol && x.Interval == a.Interval, ct);
                    if (existing is not null) { existing.ModelId = modelId; existing.UpdatedAt = a.UpdatedAt; }
                    else db.ActiveModels.Add(new ActiveModel { TenantId = tid, Symbol = a.Symbol, Interval = a.Interval, ModelId = modelId, UpdatedAt = a.UpdatedAt });
                    activeModelImported++;
                }
                await db.SaveChangesAsync(ct);

                // 4. Paper sessions.
                foreach (var s in bundle.PaperSessions ?? new())
                {
                    if (await db.PaperSessions.AnyAsync(x => x.Id == s.Id, ct))
                    {
                        results.Add(new SessionImportResult("paper", s.Id.ToString(), s.Symbol, s.Interval, "skipped", "already exists"));
                        paperSkipped++;
                        continue;
                    }

                    var effStrategy = ResolveStrategy(s.StrategyId, strategyRemap);
                    var importActive = doActivate && s.StoppedAt is null;
                    var effHash = LiveSessionEngine.ComputeConfigHash("polymarket", s.Symbol, s.Interval, effStrategy, s.InitialBalance, s.InitialBetSize);

                    if (importActive)
                    {
                        // Hard DB constraint: one active paper session per (tenant, symbol, interval, label).
                        var labelClash = await db.PaperSessions.FirstOrDefaultAsync(
                            x => x.TenantId == tid && x.Symbol == s.Symbol && x.Interval == s.Interval && x.Label == s.Label && x.StoppedAt == null, ct);
                        // Code-level cross-mode dedup: same config hash active on either table.
                        var paperHashClash = await db.PaperSessions.FirstOrDefaultAsync(
                            x => x.TenantId == tid && x.ConfigHash == effHash && x.StoppedAt == null, ct);
                        var liveHashClash = await db.LiveSessions.FirstOrDefaultAsync(
                            x => x.TenantId == tid && x.ConfigHash == effHash && x.StoppedAt == null, ct);

                        var blockers = new List<object?> { labelClash, paperHashClash, liveHashClash }.Where(b => b is not null).ToList();
                        if (blockers.Count > 0)
                        {
                            if (!overwrite)
                            {
                                results.Add(new SessionImportResult("paper", s.Id.ToString(), s.Symbol, s.Interval, "conflict",
                                    "an active session collides (label or config hash); pass overwriteActive=true to replace it"));
                                paperSkipped++;
                                continue;
                            }
                            if (labelClash is not null) labelClash.StoppedAt = now;
                            if (paperHashClash is not null) paperHashClash.StoppedAt = now;
                            if (liveHashClash is not null) liveHashClash.StoppedAt = now;
                            await db.SaveChangesAsync(ct);
                            if (liveHashClash is not null) await TryReleaseAsync(ledger, tid, liveHashClash.Id, warnings, ct);
                        }
                    }

                    db.PaperSessions.Add(new PaperSession
                    {
                        Id = s.Id,
                        TenantId = tid,
                        Symbol = s.Symbol,
                        Interval = s.Interval,
                        Label = s.Label,
                        ConfigHash = effHash,
                        StartedAt = s.StartedAt,
                        StoppedAt = importActive ? null : (s.StoppedAt ?? now),
                        InitialBalance = s.InitialBalance,
                        InitialBetSize = s.InitialBetSize,
                        StrategyId = effStrategy,
                        Gated = s.Gated,
                        CurrentBalance = s.CurrentBalance,
                        CurrentBetSize = s.CurrentBetSize,
                        Bust = s.Bust,
                        ZeroCrossingsCount = s.ZeroCrossingsCount,
                        PeakBorrowed = s.PeakBorrowed,
                        LastProcessedAt = null
                    });
                    foreach (var bet in s.Bets)
                    {
                        db.PaperBets.Add(new PaperBet
                        {
                            Id = bet.Id,
                            TenantId = tid,
                            SessionId = s.Id,
                            TargetOpenTime = bet.TargetOpenTime,
                            Side = bet.Side,
                            PredictedProbUp = bet.PredictedProbUp,
                            AnchorClose = bet.AnchorClose,
                            Size = bet.Size,
                            BalanceBefore = bet.BalanceBefore,
                            PlacedAt = bet.PlacedAt,
                            Resolved = bet.Resolved,
                            Outcome = bet.Outcome,
                            Payout = bet.Payout,
                            BalanceAfter = bet.BalanceAfter,
                            ResolvedAt = bet.ResolvedAt,
                            ActualClose = bet.ActualClose,
                            NotesJson = bet.NotesJson,
                            EntryPrice = bet.EntryPrice,
                            Shares = bet.Shares,
                            Synthetic = bet.Synthetic,
                            MarketExternalId = bet.MarketExternalId
                        });
                    }
                    await db.SaveChangesAsync(ct);

                    if (importActive)
                    {
                        var saved = await db.PaperSessions.AsNoTracking().Include(x => x.Bets).FirstAsync(x => x.Id == s.Id, ct);
                        paperEvents.Publish(new PaperTradingEvent(PaperTradingEventKind.SessionStarted, saved, null));
                    }
                    results.Add(new SessionImportResult("paper", s.Id.ToString(), s.Symbol, s.Interval,
                        importActive ? "imported-active" : "imported-stopped", $"{s.Bets.Count} bets"));
                    paperImported++;
                }

                // 5. Live sessions.
                foreach (var s in bundle.LiveSessions ?? new())
                {
                    if (await db.LiveSessions.AnyAsync(x => x.Id == s.Id, ct))
                    {
                        results.Add(new SessionImportResult("live", s.Id.ToString(), s.Symbol, s.Interval, "skipped", "already exists"));
                        liveSkipped++;
                        continue;
                    }

                    var effStrategy = ResolveStrategy(s.StrategyId, strategyRemap);
                    var importActive = doActivate && s.StoppedAt is null;
                    var effHash = LiveSessionEngine.ComputeConfigHash(s.Venue, s.Symbol, s.Interval, effStrategy, s.InitialBalance, s.InitialBetSize);

                    if (importActive)
                    {
                        // live_sessions has a GLOBAL partial-unique index on config_hash (active rows),
                        // so the collision check is not tenant-scoped here.
                        var hashClash = await db.LiveSessions.FirstOrDefaultAsync(x => x.ConfigHash == effHash && x.StoppedAt == null, ct);
                        if (hashClash is not null)
                        {
                            if (!overwrite)
                            {
                                results.Add(new SessionImportResult("live", s.Id.ToString(), s.Symbol, s.Interval, "conflict",
                                    "an active live session collides on config hash; pass overwriteActive=true to replace it"));
                                liveSkipped++;
                                continue;
                            }
                            hashClash.StoppedAt = now;
                            await db.SaveChangesAsync(ct);
                            await TryReleaseAsync(ledger, hashClash.TenantId, hashClash.Id, warnings, ct);
                        }
                    }

                    db.LiveSessions.Add(new LiveSession
                    {
                        Id = s.Id,
                        TenantId = tid,
                        Symbol = s.Symbol,
                        Interval = s.Interval,
                        Venue = s.Venue,
                        Mode = s.Mode,
                        ConfigHash = effHash,
                        StartedAt = s.StartedAt,
                        StoppedAt = importActive ? null : (s.StoppedAt ?? now),
                        InitialBalance = s.InitialBalance,
                        InitialBetSize = s.InitialBetSize,
                        StrategyId = effStrategy,
                        Gated = s.Gated,
                        CurrentBalance = s.CurrentBalance,
                        CurrentBetSize = s.CurrentBetSize,
                        Bust = s.Bust,
                        ZeroCrossingsCount = s.ZeroCrossingsCount,
                        PeakBorrowed = s.PeakBorrowed,
                        ReservedAmount = s.ReservedAmount,
                        LastProcessedAt = null
                    });
                    foreach (var bet in s.Bets)
                    {
                        db.LiveBets.Add(new LiveBet
                        {
                            Id = bet.Id,
                            TenantId = tid,
                            SessionId = s.Id,
                            TargetOpenTime = bet.TargetOpenTime,
                            Side = bet.Side,
                            PredictedProbUp = bet.PredictedProbUp,
                            AnchorClose = bet.AnchorClose,
                            Size = bet.Size,
                            BalanceBefore = bet.BalanceBefore,
                            PlacedAt = bet.PlacedAt,
                            ExternalOrderId = bet.ExternalOrderId,
                            Resolved = bet.Resolved,
                            Outcome = bet.Outcome,
                            Payout = bet.Payout,
                            BalanceAfter = bet.BalanceAfter,
                            ResolvedAt = bet.ResolvedAt,
                            MarketOutcomeUp = bet.MarketOutcomeUp,
                            EntryPrice = bet.EntryPrice,
                            Shares = bet.Shares,
                            DivergenceNote = bet.DivergenceNote,
                            NotesJson = bet.NotesJson,
                            MarketExternalId = bet.MarketExternalId
                        });
                    }
                    await db.SaveChangesAsync(ct);

                    // Keep the pUSD reservation ledger consistent for an active real-money session so
                    // GetFreeAsync reflects the copied session's reserved balance. Paper-mode rows held
                    // in the live table don't reserve. WriteReserveAuditAsync only writes the audit row
                    // (no double-counting) — the saved session's balance is already in Σactive.
                    if (importActive && s.Mode == "live")
                    {
                        try { await ledger.WriteReserveAuditAsync(tid, s.Id, s.CurrentBalance, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // A wallet-RPC hiccup must not roll the whole bundle back — the session row is
                            // saved; the ledger self-heals on the next reconcile tick.
                            warnings.Add($"live session {s.Symbol}/{s.Interval}: reservation-ledger audit deferred ({ex.Message}); reconciler will reconcile.");
                        }
                        warnings.Add($"live session {s.Symbol}/{s.Interval} imported active — it will only place real orders once live trading is armed on this instance.");
                    }

                    results.Add(new SessionImportResult("live", s.Id.ToString(), s.Symbol, s.Interval,
                        importActive ? "imported-active" : "imported-stopped", $"{s.Bets.Count} bets"));
                    liveImported++;
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "Session import failed — rolled back");
                return Results.Problem(title: "Session import failed", detail: ex.Message, statusCode: 500);
            }

            return Results.Ok(new SessionImportSummary(
                PaperImported: paperImported,
                PaperSkipped: paperSkipped,
                LiveImported: liveImported,
                LiveSkipped: liveSkipped,
                StrategiesImported: stratImported,
                ModelsImported: modelImported,
                ActiveModelsImported: activeModelImported,
                Sessions: results,
                Warnings: warnings));
        });
    }

    // A built-in strategy id (e.g. "martingale") passes through untouched. A custom DAG strategy is a
    // Guid that may have been remapped to an existing row on the target.
    private static string ResolveStrategy(string strategyId, IReadOnlyDictionary<Guid, Guid> remap)
        => Guid.TryParse(strategyId, out var g) && remap.TryGetValue(g, out var rm)
            ? rm.ToString()
            : strategyId;

    // Releasing a stopped incumbent's reservation is best-effort — a wallet-RPC failure shouldn't
    // abort the import. The reconciler corrects any residual reservation drift on its next tick.
    private static async Task TryReleaseAsync(IAccountLedger ledger, Guid tenantId, Guid sessionId, List<string> warnings, CancellationToken ct)
    {
        try { await ledger.ReleaseAsync(tenantId, sessionId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"could not release reservation for replaced session {sessionId} ({ex.Message}); reconciler will reconcile.");
        }
    }
}

/// <summary>Self-contained, portable snapshot of a tenant's sessions plus what they need to keep trading.</summary>
public sealed class SessionTransferBundle
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; }
    public string? SourceTenantSlug { get; set; }
    public List<PaperSession> PaperSessions { get; set; } = new();
    public List<LiveSession> LiveSessions { get; set; } = new();
    public List<Strategy> Strategies { get; set; } = new();
    public List<Model> Models { get; set; } = new();
    public List<ActiveModel> ActiveModels { get; set; } = new();
}

public sealed record SessionImportResult(string Kind, string Id, string Symbol, string Interval, string Status, string? Detail);

public sealed record SessionImportSummary(
    int PaperImported,
    int PaperSkipped,
    int LiveImported,
    int LiveSkipped,
    int StrategiesImported,
    int ModelsImported,
    int ActiveModelsImported,
    List<SessionImportResult> Sessions,
    List<string> Warnings);
