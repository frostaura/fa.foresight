using FluentAssertions;
using FrostAura.Foresight.Domain.Platform;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using FrostAura.Foresight.Infrastructure.Platform;
using FrostAura.Foresight.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Tests for the per-tenant platform-connection stack: <see cref="SecretProtector"/> (encrypt at rest),
/// <see cref="PlatformConnectionResolver"/> (read + decrypt), and <see cref="PlatformConnectorFactory"/>
/// (build a live Polymarket connector vs the shadow null connector based on key + LiveTrading).
///
/// Safety invariants asserted:
///   - The factory returns the shadow NullExecutionProvider when no key is present OR LiveTrading=false.
///   - A live PolymarketExecutionProvider is built ONLY when both a usable key and LiveTrading=true exist.
///   - LiveTrading is honored from the connection (not a global flag).
/// </summary>
public class PlatformConnectionTests
{
    // A deterministic, well-formed secp256k1 test private key (NOT a funded wallet — test-only).
    private const string TestPrivateKey = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    private static readonly Guid TenantId = Guid.NewGuid();

    private static ISecretProtector NewProtector()
        => new SecretProtector(new EphemeralDataProtectionProvider());

    private static ForesightDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<ForesightDbContext>()
            .UseInMemoryDatabase($"platform-connection-test-{Guid.NewGuid()}")
            .Options;
        return new ForesightDbContext(opts);
    }

    // ── SecretProtector round-trip ────────────────────────────────────────────────

    [Fact]
    public void SecretProtector_round_trips_plaintext()
    {
        var protector = NewProtector();

        var cipher = protector.Protect(TestPrivateKey);
        var plain = protector.Unprotect(cipher);

        cipher.Should().NotBeNullOrEmpty();
        cipher.Should().NotBe(TestPrivateKey, "the stored value must be ciphertext, not the raw key");
        plain.Should().Be(TestPrivateKey);
    }

    [Fact]
    public void SecretProtector_treats_null_and_empty_as_no_secret()
    {
        var protector = NewProtector();

        protector.Protect(null).Should().BeNull();
        protector.Protect("").Should().BeNull();
        protector.Unprotect(null).Should().BeNull();
        protector.Unprotect("").Should().BeNull();
    }

    // ── Resolver reads + decrypts the default-tenant connection ────────────────────

    [Fact]
    public async Task Resolver_returns_and_decrypts_the_default_tenant_connection()
    {
        var protector = NewProtector();
        await using var db = NewDb();

        db.PlatformConnections.Add(new PlatformConnection
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ConnectorId = "polymarket",
            IsDefault = true,
            PrivateKeyEncrypted = protector.Protect(TestPrivateKey),
            WalletAddress = "0xAbC",
            SignatureType = 1,
            Funder = "0xFunder",
            ClobBaseUrl = "https://clob.example",
            GammaBaseUrl = "https://gamma.example",
            ChainId = 137,
            LiveTrading = true,
            MaxTradeUsd = 5m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var resolver = new PlatformConnectionResolver(db, protector, NullLogger<PlatformConnectionResolver>.Instance);
        var resolved = await resolver.GetAsync(TenantId, default);

        resolved.Should().NotBeNull();
        resolved!.PrivateKey.Should().Be(TestPrivateKey, "the resolver must decrypt the stored key");
        resolved.HasUsableKey.Should().BeTrue();
        resolved.SignatureType.Should().Be(1);
        resolved.Funder.Should().Be("0xFunder");
        resolved.ClobBaseUrl.Should().Be("https://clob.example");
        resolved.LiveTrading.Should().BeTrue();
        resolved.MaxTradeUsd.Should().Be(5m);
    }

    [Fact]
    public async Task Resolver_returns_null_when_no_default_connection()
    {
        var protector = NewProtector();
        await using var db = NewDb();

        var resolver = new PlatformConnectionResolver(db, protector, NullLogger<PlatformConnectionResolver>.Instance);
        var resolved = await resolver.GetAsync(TenantId, default);

        resolved.Should().BeNull();
    }

    // ── Factory: live connector vs shadow connector ───────────────────────────────

    private static (ServiceProvider sp, ISecretProtector protector) BuildFactoryHost(PlatformConnection? seed)
    {
        var dbName = $"platform-factory-test-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("polymarket-clob");
        services.AddSingleton<ISecretProtector>(new SecretProtector(new EphemeralDataProtectionProvider()));
        services.AddDbContext<ForesightDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IPlatformConnectionResolver, PlatformConnectionResolver>();
        services.AddSingleton<IPlatformConnectorFactory, PlatformConnectorFactory>();

        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ISecretProtector>();

        if (seed is not null)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.PlatformConnections.Add(seed);
            db.SaveChanges();
        }
        return (sp, protector);
    }

    private static PlatformConnection Conn(ISecretProtector protector, bool withKey, bool liveTrading) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantId,
        ConnectorId = "polymarket",
        IsDefault = true,
        PrivateKeyEncrypted = withKey ? protector.Protect(TestPrivateKey) : null,
        WalletAddress = withKey ? "0xAbC" : null,
        SignatureType = 0,
        ClobBaseUrl = "https://clob.polymarket.com",
        GammaBaseUrl = "https://gamma-api.polymarket.com",
        ChainId = 137,
        LiveTrading = liveTrading,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Factory_builds_live_polymarket_connector_when_key_and_live_trading_present()
    {
        ServiceProvider sp;
        ISecretProtector protector;
        // Seed needs the protector; build host first without seed, then seed via the same protector.
        (sp, protector) = BuildFactoryHost(seed: null);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.PlatformConnections.Add(Conn(protector, withKey: true, liveTrading: true));
            await db.SaveChangesAsync();
        }

        var factory = sp.GetRequiredService<IPlatformConnectorFactory>();
        var connector = await factory.GetForTenantAsync(TenantId, default);

        connector.Should().BeOfType<PolymarketExecutionProvider>();
        connector.ConnectorId.Should().Be("polymarket-clob");
        sp.Dispose();
    }

    [Fact]
    public async Task Factory_returns_shadow_connector_when_key_missing()
    {
        var (sp, protector) = BuildFactoryHost(seed: null);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.PlatformConnections.Add(Conn(protector, withKey: false, liveTrading: true));
            await db.SaveChangesAsync();
        }

        var factory = sp.GetRequiredService<IPlatformConnectorFactory>();
        var connector = await factory.GetForTenantAsync(TenantId, default);

        connector.Should().BeOfType<NullExecutionProvider>();
        connector.ConnectorId.Should().Be("null-execution");
        sp.Dispose();
    }

    [Fact]
    public async Task Factory_returns_shadow_connector_when_live_trading_false()
    {
        var (sp, protector) = BuildFactoryHost(seed: null);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.PlatformConnections.Add(Conn(protector, withKey: true, liveTrading: false));
            await db.SaveChangesAsync();
        }

        var factory = sp.GetRequiredService<IPlatformConnectorFactory>();
        var connector = await factory.GetForTenantAsync(TenantId, default);

        connector.Should().BeOfType<NullExecutionProvider>("LiveTrading=false must keep the tenant in shadow even with a key");
        sp.Dispose();
    }

    [Fact]
    public async Task Factory_returns_shadow_connector_when_no_connection_at_all()
    {
        var (sp, _) = BuildFactoryHost(seed: null);

        var factory = sp.GetRequiredService<IPlatformConnectorFactory>();
        var connector = await factory.GetForTenantAsync(TenantId, default);

        connector.Should().BeOfType<NullExecutionProvider>();
        sp.Dispose();
    }

    [Fact]
    public async Task Factory_rebuilds_after_invalidate_when_live_trading_toggled()
    {
        var (sp, protector) = BuildFactoryHost(seed: null);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.PlatformConnections.Add(Conn(protector, withKey: true, liveTrading: false));
            await db.SaveChangesAsync();
        }

        var factory = sp.GetRequiredService<IPlatformConnectorFactory>();
        (await factory.GetForTenantAsync(TenantId, default)).Should().BeOfType<NullExecutionProvider>();

        // Flip LiveTrading on, invalidate the cache, and confirm the connector is rebuilt live.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            var row = await db.PlatformConnections.FirstAsync(c => c.TenantId == TenantId);
            row.LiveTrading = true;
            await db.SaveChangesAsync();
        }
        factory.Invalidate(TenantId);

        (await factory.GetForTenantAsync(TenantId, default)).Should().BeOfType<PolymarketExecutionProvider>();
        sp.Dispose();
    }
}
