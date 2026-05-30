using System.Collections.Concurrent;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Walk-forward calibration map per interval. Buckets the last N resolved predictions by
/// `DirectionUpProbability` (5 bins from 0..1) and records empirical hit rate per bucket. The
/// rescaler then maps raw probability through linear interpolation between bucket centroids,
/// so a model that's well-calibrated on extremes but noisy in the middle gets pulled toward
/// 0.5 in the middle and stretched at the tails. Identity-fallback when a bucket has too few
/// samples — paper trading naturally degrades to raw probability cold-start.
///
/// Cached per (tenant, interval); invalidated lazily on a short TTL (resolved predictions land
/// often enough that aggressive caching wins over freshness here).
/// </summary>
public interface ICalibrationRescaler
{
    /// <summary>
    /// Returns a calibrated probability in [0, 1] for the given (tenant, interval, raw). If
    /// insufficient data exists for the bucket, returns the raw value unchanged.
    /// </summary>
    Task<decimal> RescaleAsync(Guid tenantId, string interval, decimal rawP, CancellationToken ct);

    /// <summary>Flush the cached map for a tenant — call after writing many resolutions at once
    /// (e.g. a backfill).</summary>
    void Invalidate(Guid tenantId);
}

public sealed class CalibrationRescaler : ICalibrationRescaler
{
    private const int BinCount = 5;
    private const int MinSamplesPerBin = 20;
    private const int LookbackSize = 200;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private sealed record IntervalMap(DateTimeOffset BuiltAt, decimal[] BinCentroids, decimal?[] BinHitRates);

    private readonly ConcurrentDictionary<(Guid, string), IntervalMap> _cache = new();
    private readonly ForesightDbContext _db;
    private readonly ILogger<CalibrationRescaler> _logger;

    public CalibrationRescaler(ForesightDbContext db, ILogger<CalibrationRescaler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<decimal> RescaleAsync(Guid tenantId, string interval, decimal rawP, CancellationToken ct)
    {
        var clamped = Math.Clamp(rawP, 0m, 1m);
        var map = await GetOrBuildAsync(tenantId, interval, ct);
        if (map is null) return clamped;

        // Linear interpolation between bucket centroids. Skip bins that didn't collect enough
        // samples — they're treated as "no information," so the rescaler falls back to the
        // nearest neighbours with data.
        var withData = new List<(decimal centroid, decimal hit)>();
        for (var i = 0; i < BinCount; i++)
        {
            if (map.BinHitRates[i] is { } hit) withData.Add((map.BinCentroids[i], hit));
        }
        // Need ≥ 2 populated bins to interpolate meaningfully. With only one bin's worth of data,
        // returning that bin's empirical hit rate collapses every raw probability to the same
        // constant — destroying the model's signal (every prediction reads DOWN or every reads UP
        // depending on which side the lone bin lands on). Identity fallback preserves the underlying
        // direction until calibration accumulates enough samples across multiple bins to be useful.
        if (withData.Count < 2) return clamped;

        // Walk the sorted centroids, find the segment containing rawP, lerp.
        withData.Sort((a, b) => a.centroid.CompareTo(b.centroid));
        if (clamped <= withData[0].centroid) return withData[0].hit;
        if (clamped >= withData[^1].centroid) return withData[^1].hit;
        for (var i = 0; i < withData.Count - 1; i++)
        {
            var (c0, h0) = withData[i];
            var (c1, h1) = withData[i + 1];
            if (clamped >= c0 && clamped <= c1)
            {
                if (c1 == c0) return h0;
                var t = (clamped - c0) / (c1 - c0);
                return Math.Clamp(h0 + t * (h1 - h0), 0m, 1m);
            }
        }
        return clamped;
    }

    public void Invalidate(Guid tenantId)
    {
        foreach (var key in _cache.Keys)
            if (key.Item1 == tenantId) _cache.TryRemove(key, out _);
    }

    private async Task<IntervalMap?> GetOrBuildAsync(Guid tenantId, string interval, CancellationToken ct)
    {
        var key = (tenantId, interval);
        if (_cache.TryGetValue(key, out var existing) && DateTimeOffset.UtcNow - existing.BuiltAt < CacheTtl)
            return existing;

        // Pull the most recent N resolutions and bucket. Keep `DirectionHit IS NOT NULL` so
        // explicit non-bets (p=0.5) don't pollute the empirical hit rate.
        var rows = await _db.LivePredictions.AsNoTracking()
            .Where(p => p.TenantId == tenantId
                     && p.Interval == interval
                     && p.ResolvedAt != null
                     && p.DirectionHit != null)
            .OrderByDescending(p => p.ResolvedAt)
            .Take(LookbackSize)
            .Select(p => new { p.DirectionUpProbability, p.DirectionHit })
            .ToListAsync(ct);

        if (rows.Count < MinSamplesPerBin)
        {
            _logger.LogDebug("Calibration map for {Tenant}/{Interval}: only {N} resolved rows — identity fallback", tenantId, interval, rows.Count);
            return null;
        }

        var centroids = new decimal[BinCount];
        var hitRates = new decimal?[BinCount];
        var binWidth = 1m / BinCount;
        for (var i = 0; i < BinCount; i++) centroids[i] = (i + 0.5m) * binWidth;

        for (var b = 0; b < BinCount; b++)
        {
            var lo = b * binWidth;
            var hi = (b + 1) * binWidth;
            // Use the directional truth: a hit on a DOWN call (raw < 0.5) translates to the
            // *down* probability being right. We're calibrating p(up) directly, so a DirectionHit
            // on raw < 0.5 means "down call was right," which is an outcome that should pull the
            // empirical "p(up)" for that bucket *lower*. So: empirical hit rate on the up-direction
            // = avg of (hit if predicted up, else !hit).
            var inBucket = rows.Where(r => r.DirectionUpProbability >= lo && r.DirectionUpProbability < hi).ToList();
            if (b == BinCount - 1)
                inBucket = rows.Where(r => r.DirectionUpProbability >= lo && r.DirectionUpProbability <= hi).ToList();
            if (inBucket.Count < MinSamplesPerBin)
            {
                hitRates[b] = null;
                continue;
            }
            decimal upRight = 0;
            foreach (var r in inBucket)
            {
                var predictedUp = r.DirectionUpProbability >= 0.5m;
                var hit = r.DirectionHit == true;
                // P(actual up) = if predicted up & hit, actual was up. If predicted down & hit, actual was down (so contributes 0). Etc.
                var actualWasUp = (predictedUp && hit) || (!predictedUp && !hit);
                if (actualWasUp) upRight += 1m;
            }
            hitRates[b] = upRight / inBucket.Count;
        }

        var map = new IntervalMap(DateTimeOffset.UtcNow, centroids, hitRates);
        _cache[key] = map;
        return map;
    }
}
