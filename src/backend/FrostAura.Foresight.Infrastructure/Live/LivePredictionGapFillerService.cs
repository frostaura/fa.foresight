using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Background loop that guarantees a horizon=1 prediction exists for every tenant × symbol ×
/// interval, regardless of whether any client UI is open. Without this the chart's auto-predict
/// was the only trigger, so closing the page left holes in the calibration dataset (observed: 84
/// missing 1m candles across two browser-closed windows). LLM calls are skipped when an existing
/// prediction is already persisted for the next-candle target — `LivePredictionService.PredictAsync`
/// guards on `(tenant, symbol, interval, targetOpenTime)`, so ticking aggressively here costs
/// nothing in the happy path.
///
/// Tick cadence is sub-candle for the shortest interval (1m → 15 s) so a transient LLM/network
/// failure has multiple retries before the boundary advances. Targets are hard-coded for v1 (Dean
/// only); promote to a `(tenant, symbol, interval)` registry the day a second tenant joins.
/// </summary>
public sealed class LivePredictionGapFillerService : BackgroundService
{
    // Derived from SupportedSymbols so adding a new symbol or interval is a one-file edit. The
    // Cartesian product of all supported (symbol, interval) pairs forms the gap-filler's tick targets.
    private static readonly (string Symbol, string Interval)[] Targets =
        SupportedSymbols.All
            .SelectMany(s => SupportedSymbols.Intervals.Select(i => (Symbol: s, Interval: i)))
            .ToArray();

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LivePredictionGapFillerService> _logger;

    public LivePredictionGapFillerService(
        IServiceScopeFactory scopeFactory,
        ILogger<LivePredictionGapFillerService> logger)
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
                _logger.LogError(ex, "Gap-filler tick crashed; continuing");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // One DI scope per tick — required because ITenantContext, ForesightDbContext, and
        // ILivePredictionService are all scoped (Per-request lifetimes). The HTTP request scope
        // doesn't apply in a background service, so we mint our own.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var tenants = await db.Tenants.AsNoTracking().Select(t => new { t.Id, t.Slug }).ToListAsync(ct);
        if (tenants.Count == 0)
        {
            _logger.LogDebug("Gap-filler: no tenants in DB yet, skipping tick");
            return;
        }

        var predictionService = scope.ServiceProvider.GetRequiredService<ILivePredictionService>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        foreach (var tenant in tenants)
        {
            tenantContext.Set(tenant.Id, tenant.Slug);

            // Demand gate: build from active paper sessions for this tenant.
            var sessionDemand = await db.PaperSessions.AsNoTracking()
                .Where(s => s.TenantId == tenant.Id && s.StoppedAt == null)
                .Select(s => new { s.Symbol, s.Interval })
                .ToListAsync(ct);
            var demand = new HashSet<(string Sym, string Iv)>(
                sessionDemand.Select(s => (s.Symbol, s.Interval)));

            if (demand.Count == 0)
            {
                _logger.LogDebug("Gap-filler: tenant {Slug} has no active sessions — skipping all targets", tenant.Slug);
                continue;
            }

            foreach (var (sym, iv) in Targets)
            {
                if (!demand.Contains((sym, iv)))
                {
                    _logger.LogDebug("Gap-filler: skipping {Sym}/{Iv} for tenant {Slug} — no demand", sym, iv, tenant.Slug);
                    continue;
                }
                try
                {
                    await predictionService.PredictAsync(sym, iv, horizon: 1, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Swallow per-target failures so one bad symbol can't crater the loop.
                    _logger.LogWarning(ex, "Gap-fill predict failed for tenant={Tenant} {Sym} {Iv}", tenant.Slug, sym, iv);
                }
            }
        }
    }
}
