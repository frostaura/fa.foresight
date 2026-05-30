using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Paper;

/// <summary>
/// Background loop that drives all live paper-trading sessions. Each tick scans every active
/// session in the DB and calls `IPaperTradingService.ProcessAsync` to settle and place. This is
/// what lets a session keep trading while the user's browser is closed — the source of truth has
/// moved off the client.
///
/// Tick cadence is short (3 s) so a candle boundary is caught within one cycle. The processor is
/// idempotent: settling a resolved bet is a no-op, placing the same target twice is collision-
/// guarded by the partial unique index on `paper_bets (SessionId, TargetOpenTime)`.
/// </summary>
public sealed class PaperTradingProcessorService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaperTradingProcessorService> _logger;

    public PaperTradingProcessorService(IServiceScopeFactory scopeFactory, ILogger<PaperTradingProcessorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Paper-trading processor tick crashed; continuing");
            }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Snapshot every active session up-front so the per-session scope below loads the right
        // tenant context before touching DB state. Each session gets its own scope so the per-
        // request services (DbContext, TenantContext) are lifecycle-clean.
        using var snapshotScope = _scopeFactory.CreateScope();
        var snapshotDb = snapshotScope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var active = await snapshotDb.PaperSessions
            .AsNoTracking()
            .Where(s => s.StoppedAt == null && !s.Bust)
            .Select(s => new { s.Id, s.TenantId, s.Symbol, s.Interval })
            .ToListAsync(ct);
        if (active.Count == 0) return;

        var tenantSlugs = await snapshotDb.Tenants.AsNoTracking()
            .Where(t => active.Select(a => a.TenantId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Slug, ct);

        foreach (var summary in active)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                if (!tenantSlugs.TryGetValue(summary.TenantId, out var slug)) continue;
                tenantContext.Set(summary.TenantId, slug);
                var paper = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();
                // Resolve by id (not by symbol/interval) so EVERY active session — the primary plus any
                // labelled same-market comparison sessions — is driven independently each tick.
                var session = await paper.GetByIdAsync(summary.Id, ct);
                if (session is null) continue;
                await paper.ProcessAsync(session, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Process tick failed for session {SessionId} ({Sym} {Iv})", summary.Id, summary.Symbol, summary.Interval);
            }
        }
    }
}
