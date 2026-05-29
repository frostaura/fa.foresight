using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Keeps the order-flow microstructure cache current at the live edge. The daily aggTrades dumps lag
/// real time by ~1 day, so on its own the cache is stale for recent bars and v1+ofx would abstain
/// live. This follower fills the gap from the REST aggTrades feed: each tick it ingests from the
/// last-cached bar up to the most-recent fully-closed bar (bounded so a cold start can't try to
/// backfill years). Once caught up, ticks are near no-ops (one new bar per 5 minutes).
///
/// OPT-IN: gated behind <c>FORESIGHT_MICRO_FOLLOWER=true</c>. It makes continuous external REST
/// calls whose pagination hasn't yet had a live-API smoke test, so it stays OFF until deliberately
/// enabled — enabling it is what makes v1+ofx tradeable live.
/// </summary>
public sealed class MicrostructureFollowerService : BackgroundService
{
    private const string Symbol = "BTCUSDT";
    private const string Interval = "5m";
    private const long IntervalMs = 300_000L;
    private const int MaxBackfillBars = 576;                       // cold-start cap ≈ 2 days of 5m bars
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MicrostructureFollowerService> _logger;
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FORESIGHT_MICRO_FOLLOWER"), "true", StringComparison.OrdinalIgnoreCase);

    public MicrostructureFollowerService(IServiceScopeFactory scopes, ILogger<MicrostructureFollowerService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Microstructure follower disabled (set FORESIGHT_MICRO_FOLLOWER=true to keep the live order-flow cache current).");
            return;
        }
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Microstructure follower tick failed; will retry"); }
            try { await Task.Delay(RefreshInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IHistoricalMicrostructureProvider>() is not BinanceHistoricalMicrostructureAdapter adapter)
            return;
        var binance = scope.ServiceProvider.GetRequiredService<BinanceMarketDataClient>();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastClosed = nowMs / IntervalMs * IntervalMs - IntervalMs;

        var maxCached = await db.HistoricalMicrostructure
            .Where(b => b.Symbol == Symbol && b.Interval == Interval)
            .MaxAsync(b => (long?)b.OpenTime, ct);

        // Ingest only the gap [from, lastClosed]; cap a cold start to the recent window so we never
        // try to REST-backfill the whole history (the daily dumps own the deep history).
        var from = Math.Max((maxCached ?? 0L) + IntervalMs, lastClosed - MaxBackfillBars * IntervalMs);
        if (from > lastClosed) return; // already current

        await adapter.IngestRecentRangeAsync(binance, Symbol, Interval, from, lastClosed, ct);
    }
}
