using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// <see cref="IHistoricalCandleProvider"/> backed by a Postgres cache + Binance public REST.
///
/// On each request:
/// 1. Snap the requested <c>[startMs, endMs]</c> to candle-open boundaries and cap the end at the
///    most recent <i>fully-closed</i> candle — the currently-forming candle is never cached or
///    returned (its OHLCV is still mutating, so persisting it would freeze a partial candle in the
///    cache forever; see <see cref="LastClosedOpen"/>).
/// 2. Read whatever's already cached in <c>historical_candles</c> for that range.
/// 3. Identify gaps (missing OpenTimes) — collapse contiguous misses into windows.
/// 4. Always treat the trailing <see cref="FreshnessWindow"/> as a forced re-fetch regardless of
///    what's cached, so the active edge is guaranteed live and any stale partial candles written by
///    older code (or a candle that closed since it was first cached) self-heal.
/// 5. Page Binance's <c>/api/v3/klines?startTime=&amp;endTime=&amp;limit=1000</c> per window, gated
///    at 4 concurrent calls to stay under the 1200 weight/min rate limit (each klines call costs 1).
/// 6. Overwrite the forced window then bulk-insert the fetched rows.
/// 7. Re-read the full range and return it ordered.
///
/// A year of 1m candles (~525k rows) takes ~525 paginated REST calls on first fill (~2 min wall
/// time at 4 concurrent), then a few trailing pages per request thereafter (the forced freshness
/// window) — sub-second once the bulk of the range is cached.
/// </summary>
public sealed class BinanceHistoricalCandleAdapter : IHistoricalCandleProvider
{
    private const int BinanceMaxLimit = 1000;
    private const int ConcurrencyLimit = 4;

    /// <summary>
    /// Per-(symbol, interval) gate serialising the live-edge freshness rewrite (the delete + re-fetch
    /// + re-insert of the trailing window). Without it, two callers hitting the same recent window
    /// concurrently — e.g. an A/B run fanning several models out over an identical "last 15 days"
    /// window — race the <c>ExecuteDeleteAsync</c>: one deletes the trailing rows the other is mid-read
    /// on, so the second sees phantom gaps, the off-tf coverage develops holes, and the model abstains
    /// on those candles (the 43%/bust artifact). Historical windows never enter this path (their data
    /// is immutable, gap-fill-only), so this only serialises genuine edge rewrites and leaves
    /// non-overlapping (symbol, interval) pairs fully parallel.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FreshnessGates = new();

    private static SemaphoreSlim FreshnessGate(string symbol, string interval)
        => FreshnessGates.GetOrAdd($"{symbol}|{interval}", _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Trailing slice always re-fetched from Binance regardless of cache contents. Guarantees the
    /// active edge is live and overwrites any partial/stale candle that older write paths cached at
    /// a past <c>endMs</c>. Sized past the warmer's 24h refresh cadence so a day-boundary partial
    /// can't survive between refreshes. Cheap: ≤ 48h is ≤ 3 Binance pages per (symbol, interval).
    /// </summary>
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromHours(48);

    private readonly BinanceMarketDataClient _binance;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BinanceHistoricalCandleAdapter> _logger;

    public BinanceHistoricalCandleAdapter(
        BinanceMarketDataClient binance,
        IServiceScopeFactory scopes,
        ILogger<BinanceHistoricalCandleAdapter> logger)
    {
        _binance = binance;
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        if (endMs <= startMs)
            throw new ArgumentException($"endMs ({endMs}) must be > startMs ({startMs}).");

        // A fresh DbContext per call (own scope), NOT the request-scoped one. The flow executor runs
        // sibling feature nodes concurrently (e.g. htf_regime_pack + subbar_pack both read off-tf
        // candles in the same layer); sharing one DbContext across them trips EF's "second operation
        // on this context" race. Per-call contexts make concurrent reads safe; Npgsql pools the
        // connection so the cost is negligible.
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        var intervalMs = BinanceMarketDataClient.IntervalMs(interval);
        // Snap to candle-open boundaries so equality checks against Binance OpenTime values line up.
        var snapStart = (startMs / intervalMs) * intervalMs;
        // Cap the end at the most recent fully-closed candle. The currently-forming candle has
        // mutating OHLCV, so it must never enter the cache (it would freeze as a stale partial).
        var lastClosed = LastClosedOpen(intervalMs);
        var snapEnd = Math.Min((endMs / intervalMs) * intervalMs, lastClosed);

        // Whole requested window is in the future / has no closed candle yet — nothing to serve.
        if (snapEnd < snapStart) return Array.Empty<HistoricalCandle>();

        // Force-refresh the trailing slice ONLY when the requested range reaches the live edge.
        // Historical ranges (ending well before now — every training/backtest window) are immutable,
        // so we serve them from cache + gap-fill only, never delete/re-insert. This is what stops the
        // duplicate-key storms that raced the warmer and throttled training: a past window does zero
        // redundant writes. The freshness re-fetch still heals partial candles at the live edge.
        var freshnessMs = (long)FreshnessWindow.TotalMilliseconds;
        var nearLiveEdge = snapEnd >= lastClosed - freshnessMs;
        var freshFrom = nearLiveEdge
            ? Math.Max(snapStart, ((snapEnd - freshnessMs) / intervalMs) * intervalMs)
            : snapEnd + intervalMs; // sentinel past snapEnd → no forced window; whole range is gap-fill-only

        // Serialise the read-modify-write per (symbol, interval) ONLY when we're at the live edge,
        // i.e. when the freshness delete below will actually run. Concurrent callers on the same
        // recent window would otherwise race that delete (see FreshnessGates). Historical windows
        // skip the gate entirely and stay fully parallel — their cache is append-only gap-fill.
        var gate = nearLiveEdge ? FreshnessGate(symbol, interval) : null;
        if (gate is not null) await gate.WaitAsync(ct);
        try
        {

            // 1+2. Read whatever is cached.
            var existing = await db.HistoricalCandles
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Interval == interval &&
                            c.OpenTime >= snapStart && c.OpenTime <= snapEnd)
                .OrderBy(c => c.OpenTime)
                .ToListAsync(ct);

            // 3. Identify gaps below the freshness window. Walk the expected sequence and group misses
            // into contiguous windows. Candles at/after freshFrom are handled by the forced re-fetch.
            var present = new HashSet<long>(existing.Where(c => c.OpenTime < freshFrom).Select(c => c.OpenTime));
            var gaps = new List<(long Start, long End)>();
            long? gapStart = null;
            for (var t = snapStart; t < freshFrom; t += intervalMs)
            {
                if (!present.Contains(t))
                {
                    gapStart ??= t;
                }
                else if (gapStart is not null)
                {
                    gaps.Add((gapStart.Value, t - intervalMs));
                    gapStart = null;
                }
            }
            if (gapStart is not null) gaps.Add((gapStart.Value, freshFrom - intervalMs));

            var backfillGapCount = gaps.Count;

            // 4. At the live edge only: force-refetch the trailing window. Drop any cached rows in it
            // first so the fresh fetch overwrites them rather than colliding on the primary key. For
            // historical ranges this block is skipped entirely — no delete, no re-fetch, no race.
            if (nearLiveEdge)
            {
                gaps.Add((freshFrom, snapEnd));
                await db.HistoricalCandles
                    .Where(c => c.Symbol == symbol && c.Interval == interval &&
                                c.OpenTime >= freshFrom && c.OpenTime <= snapEnd)
                    .ExecuteDeleteAsync(ct);
            }

            // Cold-fill backfill is worth an Information line; the routine trailing freshness re-fetch is
            // every-call background noise, so it logs at Debug (only when we actually did one).
            if (backfillGapCount > 0)
                _logger.LogInformation("Historical candle backfill {Symbol}/{Interval}: {GapCount} gap(s) covering {Total} candles",
                    symbol, interval, backfillGapCount, gaps.Take(backfillGapCount).Sum(g => (g.End - g.Start) / intervalMs + 1));
            else if (nearLiveEdge)
                _logger.LogDebug("Historical candle freshness refresh {Symbol}/{Interval}: {Candles} trailing candle(s)",
                    symbol, interval, (snapEnd - freshFrom) / intervalMs + 1);

            // 5. Split gaps into Binance-sized pages (max 1000 candles each) and fetch with bounded parallelism.
            var pages = new List<(long Start, long End)>();
            foreach (var (gs, ge) in gaps)
            {
                for (var p = gs; p <= ge; p += intervalMs * BinanceMaxLimit)
                {
                    var pageEnd = Math.Min(p + intervalMs * (BinanceMaxLimit - 1), ge);
                    pages.Add((p, pageEnd));
                }
            }

            using var sem = new SemaphoreSlim(ConcurrencyLimit, ConcurrencyLimit);
            var fetched = new List<HistoricalCandle>();
            var fetchedLock = new object();

            await Task.WhenAll(pages.Select(async page =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var raw = await _binance.GetKlinesRangeAsync(symbol, interval, page.Start, page.End, BinanceMaxLimit, ct);
                    lock (fetchedLock)
                    {
                        foreach (var c in raw)
                            fetched.Add(new HistoricalCandle
                            {
                                Symbol = symbol,
                                Interval = interval,
                                OpenTime = c.OpenTime,
                                Open = c.Open,
                                High = c.High,
                                Low = c.Low,
                                Close = c.Close,
                                Volume = c.Volume,
                            });
                    }
                }
                finally { sem.Release(); }
            }));

            // 6. Bulk-insert. Dedup defensively in case Binance overlapped a page boundary, and clamp to
            // [snapStart, snapEnd] so a forming candle Binance may tack on never reaches the cache.
            var deduped = fetched
                .Where(c => c.OpenTime >= snapStart && c.OpenTime <= snapEnd)
                .GroupBy(c => c.OpenTime)
                .Select(g => g.First())
                .ToList();
            await db.HistoricalCandles.AddRangeAsync(deduped, ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // PK collision with concurrent fillers — fall back to a per-row upsert via raw SQL.
                // Acceptable cost since the racing fill is rare and recovery preserves the cache.
                _logger.LogWarning(ex, "Bulk historical-candle insert collided; falling back to row-by-row");
                foreach (var entry in db.ChangeTracker.Entries<HistoricalCandle>().ToList())
                    entry.State = EntityState.Detached;
                foreach (var c in deduped)
                {
                    db.HistoricalCandles.Attach(c);
                    db.Entry(c).State = EntityState.Added;
                    try { await db.SaveChangesAsync(ct); }
                    catch (DbUpdateException) { db.Entry(c).State = EntityState.Detached; }
                }
            }

            // 7. Re-read so the caller sees the merged (cache + freshly-fetched) view in order.
            return await db.HistoricalCandles
                .AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Interval == interval &&
                            c.OpenTime >= snapStart && c.OpenTime <= snapEnd)
                .OrderBy(c => c.OpenTime)
                .ToListAsync(ct);

        }
        finally { gate?.Release(); }
    }

    /// <summary>
    /// Open time of the most recent fully-closed candle. A candle opening at <c>t</c> closes at
    /// <c>t + intervalMs</c>, so the candle currently forming opens at <c>floor(now/intervalMs)*intervalMs</c>
    /// and the last closed one is exactly one interval before that.
    /// </summary>
    private static long LastClosedOpen(long intervalMs)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (nowMs / intervalMs) * intervalMs - intervalMs;
    }
}

/// <summary>
/// Anti-lookahead wrapper used by <c>BacktestRunner</c>. Replays a fixed candle slice so source
/// nodes only ever see candles up to (and including) the candle being predicted — never the future.
/// Each runner iteration constructs a fresh slice with one more candle appended.
/// </summary>
public sealed class HistoricalSliceProvider : IHistoricalCandleProvider
{
    private readonly IReadOnlyList<HistoricalCandle> _slice;

    public HistoricalSliceProvider(IReadOnlyList<HistoricalCandle> slice) => _slice = slice;

    public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        var window = _slice
            .Where(c => c.Symbol == symbol && c.Interval == interval &&
                        c.OpenTime >= startMs && c.OpenTime <= endMs)
            .ToList();
        return Task.FromResult<IReadOnlyList<HistoricalCandle>>(window);
    }
}
