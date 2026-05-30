using FluentAssertions;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Platform;
using FrostAura.Foresight.Domain.Tenancy;
using FrostAura.Foresight.Infrastructure.Markets;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FrostAura.Foresight.Tests.Markets;

/// <summary>
/// Verifies the fixed conservative-fee pricing model that replaced the model-tracking synthetic odds.
/// The entry quote must be the tenant's connection <c>EffectivePrice</c> for BOTH sides (so the edge
/// = sideProb − price is honest, not positive-by-construction), and rows are non-synthetic.
/// </summary>
public class VenuePriceStoreFixedFeeTests
{
    private static ForesightDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ForesightDbContext>()
            .UseInMemoryDatabase($"venue-price-test-{Guid.NewGuid()}")
            .Options);

    private static (ForesightDbContext db, TenantContext tenant, Guid tenantId) Seed(decimal? effectivePrice)
    {
        var db = MakeDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = "default", CreatedAt = DateTimeOffset.UtcNow, Settings = new TenantSettings() });
        if (effectivePrice is { } ep)
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ConnectorId = "polymarket", IsDefault = true,
                ClobBaseUrl = "https://clob.polymarket.com", GammaBaseUrl = "https://gamma-api.polymarket.com",
                EffectivePrice = ep, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        db.SaveChanges();
        var tenant = new TenantContext();
        tenant.Set(tenantId, "default");
        return (db, tenant, tenantId);
    }

    [Fact]
    public async Task EnsureEntry_uses_connection_effective_price_for_both_sides_and_is_not_synthetic()
    {
        var (db, tenant, _) = Seed(effectivePrice: 0.58m);
        var store = new VenuePriceStore(db, tenant);

        // pUp is deliberately far from 0.5 — the price must NOT track it (that was the old bug).
        var quote = await store.EnsureEntryAsync("polymarket", "BTCUSDT", "5m", 1_700_000_000_000L, pUp: 0.85m, default);

        quote.YesPrice.Should().Be(0.58m);
        quote.NoPrice.Should().Be(0.58m);
        quote.Synthetic.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureEntry_falls_back_to_conservative_default_when_no_connection()
    {
        var (db, tenant, _) = Seed(effectivePrice: null); // tenant but no connection row
        var store = new VenuePriceStore(db, tenant);

        var quote = await store.EnsureEntryAsync("polymarket", "BTCUSDT", "15m", 1_700_000_000_000L, pUp: 0.20m, default);

        quote.YesPrice.Should().Be(0.55m);
        quote.NoPrice.Should().Be(0.55m);
    }
}
