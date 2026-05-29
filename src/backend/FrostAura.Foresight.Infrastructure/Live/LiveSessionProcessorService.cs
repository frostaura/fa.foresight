using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Background loop that drives all active live sessions, modelled on <see cref="Paper.PaperTradingProcessorService"/>.
/// Each tick scans every non-stopped, non-bust live session and calls ILiveSessionEngine.ProcessAsync.
/// Cadence is 3 s so candle boundaries are caught quickly; ProcessAsync is idempotent (partial-unique
/// index on (session_id, target_open_time) prevents double-bets; settlement is idempotent by design).
/// </summary>
public sealed class LiveSessionProcessorService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LiveSessionProcessorService> _logger;

    public LiveSessionProcessorService(IServiceScopeFactory scopeFactory, ILogger<LiveSessionProcessorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Live session processor tick crashed; continuing"); }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var snapshotScope = _scopeFactory.CreateScope();
        var snapshotDb = snapshotScope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var active = await snapshotDb.LiveSessions
            .AsNoTracking()
            .Where(s => s.StoppedAt == null && !s.Bust)
            .Select(s => new { s.Id, s.TenantId })
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
                var engine  = scope.ServiceProvider.GetRequiredService<ILiveSessionEngine>();
                var session = await engine.GetByIdAsync(summary.Id, ct);
                if (session is null) continue;
                await engine.ProcessAsync(session, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live session processor tick failed for session {SessionId}", summary.Id);
            }
        }
    }
}
