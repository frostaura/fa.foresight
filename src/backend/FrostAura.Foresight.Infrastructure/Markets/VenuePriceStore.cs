using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Infrastructure.Markets;

/// <summary>
/// EF-backed implementation of <see cref="IVenuePriceStore"/>. Resolves anti-look-ahead entry
/// prices from the <c>venue_market_prices</c> table: for a given (Venue, Symbol, Interval,
/// TargetOpenTime) it returns the row with the latest ObservedAt that is ≤ targetOpenTime,
/// ensuring no future price leaks into the bet decision. When no real row exists it writes a
/// synthetic-flat row calibrated from pUp and returns that.
/// </summary>
public sealed class VenuePriceStore : IVenuePriceStore
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;

    public VenuePriceStore(ForesightDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<EntryQuote?> GetEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, CancellationToken ct)
    {
        var tenantId = _tenant.IsResolved ? _tenant.TenantId!.Value : Guid.Empty;
        var row = await _db.VenueMarketPrices
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId
                        && v.Venue == venue
                        && v.Symbol == symbol
                        && v.Interval == interval
                        && v.TargetOpenTime == targetOpenTime
                        && v.ObservedAt <= targetOpenTime)
            .OrderByDescending(v => v.ObservedAt)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        return new EntryQuote(row.YesPrice, row.NoPrice, row.Synthetic, row.MarketExternalId);
    }

    public async Task<EntryQuote> EnsureEntryAsync(
        string venue, string symbol, string interval, long targetOpenTime, decimal pUp, CancellationToken ct)
    {
        var existing = await GetEntryAsync(venue, symbol, interval, targetOpenTime, ct);
        if (existing is not null) return existing.Value;

        // No real row — synthesise a calibrated-flat price from pUp.
        // yes = clamp(0.5 + (pUp - 0.5) * 0.8, 0.02, 0.98)
        var rawYes = 0.5m + (pUp - 0.5m) * 0.8m;
        var yes = Math.Max(0.02m, Math.Min(0.98m, rawYes));
        var no = 1m - yes;

        var tenantId = _tenant.IsResolved ? _tenant.TenantId!.Value : Guid.Empty;

        var synth = new VenueMarketPrice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = venue,
            Symbol = symbol,
            Interval = interval,
            TargetOpenTime = targetOpenTime,
            // Stamp the synthetic quote at the placement instant (candle open), NOT wall-clock now:
            // (1) GetEntryAsync filters ObservedAt <= targetOpenTime, so a now-stamped row (now >
            // targetOpenTime for historical backtests) could never be re-found — every pass would
            // re-synthesise and insert a duplicate; (2) wall-clock would break chaos determinism.
            // At targetOpenTime the unique index makes re-inserts collide (race-safe) and the row is reused.
            ObservedAt = targetOpenTime,
            MarketExternalId = "",
            YesPrice = yes,
            NoPrice = no,
            Synthetic = true,
            Source = "synthetic-flat",
            ResolutionWindowStart = targetOpenTime,
            ResolutionWindowEnd = targetOpenTime,
            ReferenceSource = null,
            ResolvedOutcomeUp = null
        };

        try
        {
            _db.VenueMarketPrices.Add(synth);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race: another scope already inserted the row. Detach and fall back to a query.
            _db.Entry(synth).State = EntityState.Detached;
            var refetched = await GetEntryAsync(venue, symbol, interval, targetOpenTime, ct);
            if (refetched is not null) return refetched.Value;
        }

        return new EntryQuote(yes, no, true, null);
    }
}
