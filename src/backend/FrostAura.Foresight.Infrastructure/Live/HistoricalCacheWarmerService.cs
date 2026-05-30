using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Proactively hydrates <c>historical_candles</c> with the last 730 days of every supported
/// (symbol, interval) on app startup, then refreshes daily so newly-closed candles slot into the
/// cache without a backtest having to page Binance live.
///
/// Boot runs in two phases so live predictions become usable almost instantly:
/// <list type="number">
///   <item><b>Fast warm</b> — fires immediately on startup, fans out across every
///     (symbol, interval) pair in parallel, fetches only the last few days. This guarantees the
///     ~60 candles of warmup the live flow needs before the first user-triggered predict hits
///     the API. Typical cold-cache fast warm: 1–3 seconds total.</item>
///   <item><b>Full warm</b> — long-window 730-day fill for backtesting. Runs after the fast warm
///     so backtests don't have to page Binance live either. Takes 30–60s on a cold DB; 1s on a
///     warm one. Refreshed every 24h thereafter to slot newly-closed candles in.</item>
/// </list>
///
/// The on-demand gap-fill inside <c>BinanceHistoricalCandleAdapter</c> still backstops anything
/// the warmer missed (a candle that closed mid-call, a fresh tenant favoriting a TF the warmer
/// hadn't reached). The warmer just makes that path the exception, not the rule.
/// </summary>
public sealed class HistoricalCacheWarmerService : BackgroundService
{
    // Matches the backtest cap (730d). Big enough for the 15m × 24-month default to hit Postgres
    // directly on first run with no live Binance pagination.
    private static readonly TimeSpan FullWindow = TimeSpan.FromDays(730);
    /// <summary>Small window the live prediction flow actually needs (60 candles + headroom).</summary>
    private static readonly TimeSpan FastWindow = TimeSpan.FromDays(3);
    /// <summary>Cadence of the incremental top-up after the first full warm completes.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HistoricalCacheWarmerService> _logger;

    public HistoricalCacheWarmerService(
        IServiceScopeFactory scopes,
        ILogger<HistoricalCacheWarmerService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No startup delay on the fast warm — fan it out the moment the host comes up so the
        // live prediction window is already populated by the time the first chart card mounts.
        try
        {
            await WarmAsync(FastWindow, parallel: true, stoppingToken);
            _logger.LogInformation("Fast cache warm complete ({Days}d window); kicking off full backfill", FastWindow.TotalDays);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast cache warm failed; on-demand gap-fill will handle live predictions, full backfill continuing");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Full warm runs sequentially per (symbol, interval): the 1m × 730d window is
                // ~1M rows and pages Binance several hundred times — fanning it out further would
                // exceed Binance's 1200 weight/min budget. Sequential is the deliberate floor.
                await WarmAsync(FullWindow, parallel: false, stoppingToken);
                _logger.LogInformation("Full historical cache warm complete; next refresh in {Hours}h", RefreshInterval.TotalHours);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full historical cache warm failed; will retry next interval");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task WarmAsync(TimeSpan window, bool parallel, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startMs = nowMs - (long)window.TotalMilliseconds;

        var targets = SupportedSymbols.All
            .SelectMany(s => SupportedSymbols.Intervals.Select(i => (Symbol: s, Interval: i)))
            .ToArray();

        // Each warm fetches its own DbContext-scoped provider — provider is registered Scoped so
        // we can't share one across parallel awaits without tripping EF's single-threaded
        // DbContext contract.
        async Task WarmOne((string Symbol, string Interval) target)
        {
            if (ct.IsCancellationRequested) return;
            await using var inner = _scopes.CreateAsyncScope();
            var provider = inner.ServiceProvider.GetRequiredService<IHistoricalCandleProvider>();
            _logger.LogInformation("Warming historical cache {Symbol}/{Interval} for last {Days}d", target.Symbol, target.Interval, window.TotalDays);
            try
            {
                var rows = await provider.GetRangeAsync(target.Symbol, target.Interval, startMs, nowMs, ct);
                _logger.LogInformation("Cache ready {Symbol}/{Interval}: {Count} candles", target.Symbol, target.Interval, rows.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One bad timeframe shouldn't take the warmer down — log and continue. The next
                // refresh interval will try again.
                _logger.LogWarning(ex, "Warm failed for {Symbol}/{Interval}; continuing", target.Symbol, target.Interval);
            }
        }

        if (parallel)
        {
            await Task.WhenAll(targets.Select(WarmOne));
        }
        else
        {
            foreach (var target in targets) await WarmOne(target);
        }
    }
}
