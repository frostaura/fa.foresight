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
/// ensuring no future price leaks into the bet decision.
///
/// PRICING MODEL — FIXED CONSERVATIVE FEE (replaces the old model-tracking synthetic odds). These
/// BTC up/down markets sit near 50/50, so rather than fetch live odds we model the entry as a single
/// conservative <c>EffectivePrice</c> (per-tenant connection config, default 0.55). The SAME price is
/// used for BOTH sides — buying UP (YES) or DOWN (NO) each costs EffectivePrice, because the fee is
/// symmetric (this is NOT a coherent YES+NO=1 book). The old formula <c>yes = 0.5 + (pUp−0.5)·0.8</c>
/// made the price track the model, so edge was positive by construction; a fixed price makes
/// <c>edge = sideProb − EffectivePrice</c> honest. Rows are written non-synthetic (<c>Source =
/// "fixed-fee"</c>): the fixed conservative price IS the canonical, trustworthy entry — surviving a
/// chaos/bust test at 0.55 means surviving live, where real fills are usually cheaper.
/// </summary>
public sealed class VenuePriceStore : IVenuePriceStore
{
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;

    /// <summary>Per-scope cache of the tenant's conservative effective price (one DB read per scope).</summary>
    private decimal? _effectivePrice;

    public VenuePriceStore(ForesightDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>Conservative default when no connection row exists yet — matches PlatformConnection's default.</summary>
    private const decimal DefaultEffectivePrice = 0.55m;

    /// <summary>
    /// Resolve the tenant's conservative effective price from its active platform connection, cached
    /// for the lifetime of this (scoped) store. Reads only the non-secret column — no secret
    /// decryption — and falls back to the conservative default when unset/out-of-band.
    /// </summary>
    private async Task<decimal> GetEffectivePriceAsync(Guid tenantId, CancellationToken ct)
    {
        if (_effectivePrice is { } cached) return cached;
        var p = await _db.PlatformConnections
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsDefault)
            .Select(c => (decimal?)c.EffectivePrice)
            .FirstOrDefaultAsync(ct);
        var price = p is { } v && v > 0m && v < 1m ? v : DefaultEffectivePrice;
        _effectivePrice = price;
        return price;
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

        var tenantId = _tenant.IsResolved ? _tenant.TenantId!.Value : Guid.Empty;

        // Fixed conservative fee/price for this near-50/50 contract — the SAME for both sides (UP=YES
        // and DOWN=NO each cost EffectivePrice). pUp is intentionally NOT used for pricing: a fixed
        // price keeps the edge (sideProb − price) honest rather than positive-by-construction.
        var price = await GetEffectivePriceAsync(tenantId, ct);
        var yes = price;
        var no = price;

        var row = new VenueMarketPrice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Venue = venue,
            Symbol = symbol,
            Interval = interval,
            TargetOpenTime = targetOpenTime,
            // Stamp at the placement instant (candle open), NOT wall-clock now:
            // (1) GetEntryAsync filters ObservedAt <= targetOpenTime, so a now-stamped row (now >
            // targetOpenTime for historical backtests) could never be re-found — every pass would
            // re-insert a duplicate; (2) wall-clock would break chaos determinism.
            // At targetOpenTime the unique index makes re-inserts collide (race-safe) and the row is reused.
            ObservedAt = targetOpenTime,
            MarketExternalId = "",
            YesPrice = yes,
            NoPrice = no,
            // Not synthetic: the fixed conservative fee is the canonical, trustworthy pricing model.
            Synthetic = false,
            Source = "fixed-fee",
            ResolutionWindowStart = targetOpenTime,
            ResolutionWindowEnd = targetOpenTime,
            ReferenceSource = null,
            ResolvedOutcomeUp = null
        };

        try
        {
            _db.VenueMarketPrices.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race: another scope already inserted the row. Detach and fall back to a query.
            _db.Entry(row).State = EntityState.Detached;
            var refetched = await GetEntryAsync(venue, symbol, interval, targetOpenTime, ct);
            if (refetched is not null) return refetched.Value;
        }

        return new EntryQuote(yes, no, false, null);
    }
}
