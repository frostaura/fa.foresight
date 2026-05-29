using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Scheduled reconciliation loop that periodically calls <see cref="IAccountLedger.ReconcileAsync"/>
/// for every active tenant that has at least one live session.
///
/// Mirrors the scoped-loop pattern of <see cref="Paper.PaperTradingProcessorService"/>:
///   - Opens a fresh DI scope per tick so the DbContext and TenantContext are lifecycle-clean.
///   - Default interval: 60 s (configurable via FORESIGHT_RECONCILE_INTERVAL_SECONDS).
///   - Inert in practice while walletPUSD returns 0 (Phase E stub) — the loop is safe and will
///     compute drift=0 on every tick until the on-chain balance read is wired.
///   - Any reconciliation error is caught, logged, and swallowed; the loop continues.
/// </summary>
public sealed class AccountReconciliationService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("FORESIGHT_RECONCILE_INTERVAL_SECONDS"), out var parsed) && parsed > 0
            ? parsed
            : 60);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountReconciliationService> _logger;

    public AccountReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<AccountReconciliationService> logger)
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
            catch (Exception ex) { _logger.LogError(ex, "Account reconciliation tick crashed; continuing"); }

            try { await Task.Delay(DefaultInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Snapshot the set of tenants that currently have at least one active live session.
        // Uses a snapshot scope so we don't hold a long-lived EF context across the per-tenant loops.
        using var snapshotScope = _scopeFactory.CreateScope();
        var snapshotDb = snapshotScope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        var activeTenants = await snapshotDb.LiveSessions
            .AsNoTracking()
            .Where(s => s.StoppedAt == null && s.Mode == "live")
            .Select(s => s.TenantId)
            .Distinct()
            .ToListAsync(ct);

        if (activeTenants.Count == 0)
        {
            _logger.LogDebug("Reconciliation tick: no active live sessions — skipping");
            return;
        }

        var tenantSlugs = await snapshotDb.Tenants.AsNoTracking()
            .Where(t => activeTenants.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Slug, ct);

        _logger.LogInformation("Reconciliation tick: reconciling {Count} tenant(s)", activeTenants.Count);

        foreach (var tenantId in activeTenants)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                if (!tenantSlugs.TryGetValue(tenantId, out var slug)) continue;
                tenantContext.Set(tenantId, slug);

                var ledger = scope.ServiceProvider.GetRequiredService<IAccountLedger>();
                await ledger.ReconcileAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciliation failed for tenant {TenantId} — continuing with others", tenantId);
            }
        }
    }
}
