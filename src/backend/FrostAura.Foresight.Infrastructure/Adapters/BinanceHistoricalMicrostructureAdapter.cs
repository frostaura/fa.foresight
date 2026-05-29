using System.Globalization;
using System.IO.Compression;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// <see cref="IHistoricalMicrostructureProvider"/> backed by a Postgres cache +
/// <c>data.binance.vision</c> daily aggregated-trades dumps. Reconstructs per-(symbol, interval)
/// order-flow bars from raw trades and caches them in <c>historical_microstructure</c> (one row per
/// candle — same cardinality as the candle cache; we never store raw trades).
///
/// Ingest is day-granular: each missing UTC day in the requested range is downloaded once
/// (<c>{SYMBOL}-aggTrades-YYYY-MM-DD.zip</c>), streamed, bucketed into interval bars, and inserted.
///
/// KNOWN LIMITATION: the daily dumps lag real time by ~1 day (today's file is published next day),
/// so the most recent ~24-48h of microstructure is simply absent — order-flow features for those
/// bars come back null and the model abstains on them. Backtests that need the very recent edge
/// should either end ~2 days before "now" or a REST-based recent-trades top-up should be added
/// (follow-up). Taker convention: <c>isBuyerMaker = true</c> ⇒ aggressive SELL, else aggressive BUY.
/// </summary>
public sealed class BinanceHistoricalMicrostructureAdapter : IHistoricalMicrostructureProvider
{
    /// <summary>Trades with base-qty ≥ this count as "large" (whale aggressor flow). BTC-scaled.</summary>
    private const decimal LargeTradeQty = 1.0m;

    /// <summary>Max uncached days an order-flow backtest will download on-demand in one call. Each
    /// day is a large dump; beyond this we fail fast (see <see cref="GetRangeAsync"/>) rather than
    /// grind/hang. Comfortably covers the 90-day 5m experiment windows the iteration line uses.</summary>
    private const int MaxOnDemandDays = 95;

    /// <summary>Per-day download+parse budget. A stalled dump fails that single day (→ abstain on its
    /// bars) instead of hanging the whole ingest forever — HttpClient's header timeout doesn't bound
    /// the streamed body read, so we bound it explicitly.</summary>
    private static readonly TimeSpan PerDayTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly ForesightDbContext _db;
    private readonly ILogger<BinanceHistoricalMicrostructureAdapter> _logger;

    public BinanceHistoricalMicrostructureAdapter(
        HttpClient http, ForesightDbContext db, ILogger<BinanceHistoricalMicrostructureAdapter> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        var intervalMs = IntervalMs(interval);
        var snapStart = startMs / intervalMs * intervalMs;
        // Never return the currently-forming bar (its trades are still arriving).
        var snapEnd = Math.Min(endMs / intervalMs * intervalMs, LastClosedOpen(intervalMs));
        if (snapEnd < snapStart) return Array.Empty<MicrostructureBar>();

        // Days already cached (any bar present ⇒ that day was ingested).
        var cachedOpens = await _db.HistoricalMicrostructure.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= snapStart && b.OpenTime <= snapEnd)
            .Select(b => b.OpenTime)
            .ToListAsync(ct);
        var presentDays = cachedOpens.Select(DayOf).ToHashSet();

        var firstDay = DayOf(snapStart);
        var lastDay = DayOf(snapEnd);
        var missing = new List<DateOnly>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            if (!presentDays.Contains(day)) missing.Add(day);

        // On-demand ingest cap. Each missing day downloads + parses a large Binance aggTrades dump
        // (millions of trades). A 180-day cold window would grind for many minutes or hang — so we
        // fail fast with an actionable message rather than letting an order-flow backtest stall.
        // Already-cached ranges (missing.Count small) are unaffected; pre-ingesting a range warms it.
        if (missing.Count > MaxOnDemandDays)
            throw new InvalidOperationException(
                $"Order-flow data needs {missing.Count} uncached days of Binance trade dumps for this window — over the {MaxOnDemandDays}-day on-demand cap. " +
                $"Order-flow models (v1+ofx / v1+ofx2) download a large trade dump per day, so use a shorter lookback (≤ {MaxOnDemandDays} days) for them, or pre-ingest the range first.");

        if (missing.Count > 0)
        {
            // Download + aggregate the missing days in PARALLEL (bounded) — the cost is network +
            // CSV parsing, so concurrency collapses a ~105-day serial ingest from ~20 min to a few.
            // The DB insert is done serially afterwards because the scoped DbContext isn't thread-safe.
            using var sem = new SemaphoreSlim(6, 6);
            var perDay = await Task.WhenAll(missing.Select(async day =>
            {
                await sem.WaitAsync(ct);
                try { return (day, bars: await DownloadAndAggregateDayAsync(symbol, interval, intervalMs, day, ct)); }
                catch (Exception ex) { _logger.LogWarning(ex, "Microstructure ingest failed for {Symbol} {Day:yyyy-MM-dd}", symbol, day); return (day, bars: new List<MicrostructureBar>()); }
                finally { sem.Release(); }
            }));

            var written = 0;
            foreach (var (day, bars) in perDay)
            {
                if (ct.IsCancellationRequested) break;
                if (bars.Count == 0) continue;
                var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
                var dayEnd = dayStart + 86_400_000L;
                await _db.HistoricalMicrostructure
                    .Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= dayStart && b.OpenTime < dayEnd)
                    .ExecuteDeleteAsync(ct);
                _db.HistoricalMicrostructure.AddRange(bars);
                await _db.SaveChangesAsync(ct);
                written += bars.Count;
            }
            _logger.LogInformation("Microstructure ingest {Symbol}/{Interval}: {Days} day(s), {Bars} bars", symbol, interval, missing.Count, written);
        }

        return await _db.HistoricalMicrostructure.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= snapStart && b.OpenTime <= snapEnd)
            .OrderBy(b => b.OpenTime)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Downloads + aggregates ONE day's order-flow (+ merges 5m metrics). No DB access, so it's safe
    /// to run many of these concurrently; the caller inserts the returned bars serially.
    /// </summary>
    private async Task<List<MicrostructureBar>> DownloadAndAggregateDayAsync(string symbol, string interval, long intervalMs, DateOnly day, CancellationToken outerCt)
    {
        // Bound each day so a single stalled download/parse can't hang the whole ingest.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(PerDayTimeout);
        var ct = timeoutCts.Token;
        var url = $"/data/spot/daily/aggTrades/{symbol}/{symbol}-aggTrades-{day:yyyy-MM-dd}.zip";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("aggTrades dump {Url} → {Status}; skipping day", url, resp.StatusCode);
            return new List<MicrostructureBar>();
        }

        List<MicrostructureBar> bars;
        await using (var zipStream = await resp.Content.ReadAsStreamAsync(ct))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return new List<MicrostructureBar>();
            await using var es = entry.Open();
            using var reader = new StreamReader(es);
            // Stream lines lazily into the pure aggregator — O(buckets) memory, never materialises
            // the day's millions of trades.
            bars = AggregateTrades(ReadLines(reader), symbol, interval, intervalMs, LargeTradeQty);
        }

        if (bars.Count == 0) return bars;

        // Merge futures derivatives metrics (OI / long-short ratios), which publish at 5m cadence,
        // into the 5m bars by snapshot timestamp. Orthogonal to spot order-flow; soft-fails to null.
        if (interval == "5m")
            bars = await MergeMetricsAsync(symbol, day, bars, ct);

        return bars;
    }

    /// <summary>
    /// Pure aggregation of Binance aggTrades CSV lines into per-bucket microstructure bars. Public +
    /// static so it's unit-testable in isolation (the riskiest part of the ingest). Tolerates a
    /// header row and malformed lines (skipped), and guards against microsecond timestamps. Taker
    /// convention: <c>isBuyerMaker = true</c> ⇒ aggressive SELL, else aggressive BUY.
    /// CSV columns: aggId, price, qty, firstId, lastId, timestamp(ms), isBuyerMaker, isBestMatch.
    /// </summary>
    public static List<MicrostructureBar> AggregateTrades(
        IEnumerable<string> csvLines, string symbol, string interval, long intervalMs, decimal largeTradeQty)
        => AggregateParsed(ParseCsv(csvLines), symbol, interval, intervalMs, largeTradeQty);

    /// <summary>
    /// Core aggregation over already-parsed trades — shared by the CSV (daily dumps) and the REST
    /// (live recent-trades) paths so both produce identical bars from the same rules.
    /// </summary>
    public static List<MicrostructureBar> AggregateParsed(
        IEnumerable<(long Ts, decimal Qty, bool IsBuyerMaker)> trades,
        string symbol, string interval, long intervalMs, decimal largeTradeQty)
    {
        var acc = new Dictionary<long, Agg>();
        foreach (var (ts, qty, isBuyerMaker) in trades)
        {
            var bucket = ts / intervalMs * intervalMs;
            if (!acc.TryGetValue(bucket, out var a)) { a = new Agg(); acc[bucket] = a; }
            a.TradeCount++;
            // Fractional position of this trade within its bar [0,1): used to split flow into the
            // early (first 20%) and late (final 20%) windows. Causal — all inside the closed bar.
            var pos = (decimal)(ts - bucket) / intervalMs;
            var isLate = pos >= 0.8m;
            var isEarly = pos < 0.2m;
            if (isBuyerMaker) // aggressive SELL
            {
                a.SellVolume += qty;
                if (qty >= largeTradeQty) a.LargeSellVolume += qty;
                if (isLate) a.LateSellVolume += qty;
                if (isEarly) a.EarlySellVolume += qty;
            }
            else // aggressive BUY
            {
                a.BuyVolume += qty;
                a.BuyTradeCount++;
                if (qty >= largeTradeQty) a.LargeBuyVolume += qty;
                if (isLate) a.LateBuyVolume += qty;
                if (isEarly) a.EarlyBuyVolume += qty;
            }
            if (isLate) a.LateTradeCount++;
        }

        return acc.OrderBy(kv => kv.Key).Select(kv => new MicrostructureBar
        {
            Symbol = symbol,
            Interval = interval,
            OpenTime = kv.Key,
            TradeCount = kv.Value.TradeCount,
            BuyVolume = kv.Value.BuyVolume,
            SellVolume = kv.Value.SellVolume,
            BuyTradeCount = kv.Value.BuyTradeCount,
            LargeBuyVolume = kv.Value.LargeBuyVolume,
            LargeSellVolume = kv.Value.LargeSellVolume,
            LateBuyVolume = kv.Value.LateBuyVolume,
            LateSellVolume = kv.Value.LateSellVolume,
            EarlyBuyVolume = kv.Value.EarlyBuyVolume,
            EarlySellVolume = kv.Value.EarlySellVolume,
            LateTradeCount = kv.Value.LateTradeCount,
        }).ToList();
    }

    /// <summary>
    /// Downloads the Binance UM futures <c>metrics</c> daily dump (OI + long/short ratios, 5m
    /// cadence) and merges it into the order-flow bars by snapshot timestamp. Causal by construction:
    /// each metric snapshot at time T is attached to the bar opening at T (known by that bar's close,
    /// which is the decision boundary). Soft-fails to null metrics if the dump is missing.
    /// </summary>
    private async Task<List<MicrostructureBar>> MergeMetricsAsync(string symbol, DateOnly day, List<MicrostructureBar> bars, CancellationToken ct)
    {
        var url = $"/data/futures/um/daily/metrics/{symbol}/{symbol}-metrics-{day:yyyy-MM-dd}.zip";
        Dictionary<long, MetricRow> metrics;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { _logger.LogDebug("metrics dump {Url} → {Status}; bars keep null metrics", url, resp.StatusCode); return bars; }
            await using var zs = await resp.Content.ReadAsStreamAsync(ct);
            using var archive = new ZipArchive(zs, ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return bars;
            await using var es = entry.Open();
            using var reader = new StreamReader(es);
            metrics = ParseMetricsCsv(ReadLines(reader));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Metrics merge failed for {Symbol} {Day:yyyy-MM-dd}; bars keep null metrics", symbol, day); return bars; }

        if (metrics.Count == 0) return bars;
        return bars.Select(b => metrics.TryGetValue(b.OpenTime, out var m)
            ? new MicrostructureBar
            {
                Symbol = b.Symbol, Interval = b.Interval, OpenTime = b.OpenTime,
                TradeCount = b.TradeCount, BuyVolume = b.BuyVolume, SellVolume = b.SellVolume,
                BuyTradeCount = b.BuyTradeCount, LargeBuyVolume = b.LargeBuyVolume, LargeSellVolume = b.LargeSellVolume,
                LateBuyVolume = b.LateBuyVolume, LateSellVolume = b.LateSellVolume,
                EarlyBuyVolume = b.EarlyBuyVolume, EarlySellVolume = b.EarlySellVolume, LateTradeCount = b.LateTradeCount,
                OpenInterest = m.Oi, OpenInterestValue = m.OiVal,
                TopTraderLongShortRatio = m.TopLS, LongShortRatio = m.GlobalLS, TakerLongShortVolRatio = m.TakerLS,
            }
            : b).ToList();
    }

    /// <summary>
    /// Parses the UM metrics CSV. Columns: create_time, symbol, sum_open_interest,
    /// sum_open_interest_value, count_toptrader_long_short_ratio, sum_toptrader_long_short_ratio,
    /// count_long_short_ratio, sum_taker_long_short_vol_ratio. Keyed by the 5m snapshot timestamp (ms).
    /// </summary>
    private static Dictionary<long, MetricRow> ParseMetricsCsv(IEnumerable<string> lines)
    {
        var d = new Dictionary<long, MetricRow>();
        foreach (var line in lines)
        {
            var p = line.Split(',');
            if (p.Length < 8) continue;
            if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)) continue; // header / bad row
            var bucket = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds();
            decimal? Num(int i) => decimal.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            d[bucket] = new MetricRow(Num(2), Num(3), Num(5), Num(6), Num(7));
        }
        return d;
    }

    private readonly record struct MetricRow(decimal? Oi, decimal? OiVal, decimal? TopLS, decimal? GlobalLS, decimal? TakerLS);

    /// <summary>Parses Binance aggTrades CSV lines into (ts, qty, isBuyerMaker), skipping header/bad rows.</summary>
    private static IEnumerable<(long Ts, decimal Qty, bool IsBuyerMaker)> ParseCsv(IEnumerable<string> csvLines)
    {
        foreach (var line in csvLines)
        {
            var parts = line.Split(',');
            if (parts.Length < 7) continue;
            if (!long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) continue; // header / bad row
            if (!decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var qty)) continue;
            if (ts > 100_000_000_000_000L) ts /= 1000; // some dumps use microseconds
            yield return (ts, qty, parts[6].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Ingests recent bars for the [fromOpenMs, toOpenMs] range via the REST aggTrades feed (for the
    /// dump-lag gap and the live edge), aggregates, and upserts into the cache. Only fully-closed
    /// buckets are written. Returns the number of bars written.
    /// </summary>
    public async Task<int> IngestRecentRangeAsync(
        BinanceMarketDataClient binance, string symbol, string interval, long fromOpenMs, long toOpenMs, CancellationToken ct)
    {
        var intervalMs = IntervalMs(interval);
        var lastClosed = LastClosedOpen(intervalMs);
        var end = Math.Min(toOpenMs / intervalMs * intervalMs, lastClosed);
        var start = fromOpenMs / intervalMs * intervalMs;
        if (end < start) return 0;

        var trades = await binance.GetAggTradesRangeAsync(symbol, start, end + intervalMs, ct);
        var bars = AggregateParsed(trades.Select(t => (t.Timestamp, t.Quantity, t.IsBuyerMaker)),
            symbol, interval, intervalMs, LargeTradeQty)
            .Where(b => b.OpenTime >= start && b.OpenTime <= end)
            .ToList();
        if (bars.Count == 0) return 0;

        await _db.HistoricalMicrostructure
            .Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= start && b.OpenTime <= end)
            .ExecuteDeleteAsync(ct);
        _db.HistoricalMicrostructure.AddRange(bars);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Live-ingested {Count} {Interval} microstructure bars for {Symbol} via REST", bars.Count, interval, symbol);
        return bars.Count;
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private sealed class Agg
    {
        public long TradeCount;
        public decimal BuyVolume;
        public decimal SellVolume;
        public long BuyTradeCount;
        public decimal LargeBuyVolume;
        public decimal LargeSellVolume;
        // Intra-bar windows (by each trade's fractional position within the bar).
        public decimal LateBuyVolume;
        public decimal LateSellVolume;
        public decimal EarlyBuyVolume;
        public decimal EarlySellVolume;
        public long LateTradeCount;
    }

    private static DateOnly DayOf(long ms) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);

    private static long LastClosedOpen(long intervalMs)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return now / intervalMs * intervalMs - intervalMs;
    }

    private static long IntervalMs(string interval) => interval switch
    {
        "1m" => 60_000L,
        "5m" => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };
}
