using System.Collections.Concurrent;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Platform;

/// <summary>
/// Builds (and caches) the <see cref="IPlatformConnector"/> for a tenant from its stored, decrypted
/// connection. Returns a live <see cref="PolymarketExecutionProvider"/> when the connection has a
/// usable key AND LiveTrading=true; otherwise returns the <see cref="NullExecutionProvider"/> (shadow).
///
/// Singleton with a small per-tenant cache keyed by tenantId + a config fingerprint, so derived CLOB
/// API creds are not re-fetched on every order. The connection-config API calls
/// <see cref="Invalidate"/> on save so the next resolve rebuilds the connector.
/// </summary>
public interface IPlatformConnectorFactory
{
    Task<IPlatformConnector> GetForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Drop the cached connector for a tenant (called when its connection is updated).</summary>
    void Invalidate(Guid tenantId);
}

public sealed class PlatformConnectorFactory : IPlatformConnectorFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PlatformConnectorFactory> _logger;

    // tenantId → (fingerprint, connector). Fingerprint guards against serving a stale connector after
    // a config change that didn't route through Invalidate (belt-and-suspenders).
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public PlatformConnectorFactory(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        ILogger<PlatformConnectorFactory> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IPlatformConnector> GetForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        // Resolve the (decrypted) connection in its own scope — the resolver + DbContext are scoped.
        ResolvedConnection? conn;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IPlatformConnectionResolver>();
            conn = await resolver.GetAsync(tenantId, ct);
        }

        var fingerprint = Fingerprint(conn);
        if (_cache.TryGetValue(tenantId, out var hit) && hit.Fingerprint == fingerprint)
            return hit.Connector;

        var connector = Build(conn);
        _cache[tenantId] = new CacheEntry(fingerprint, connector);
        return connector;
    }

    public void Invalidate(Guid tenantId) => _cache.TryRemove(tenantId, out _);

    private IPlatformConnector Build(ResolvedConnection? conn)
    {
        // No connection, no usable key, or live trading off ⇒ shadow connector.
        if (conn is null || !conn.HasUsableKey || !conn.LiveTrading)
        {
            _logger.LogInformation(
                "Platform connector → shadow (NullExecutionProvider): {Reason}",
                conn is null ? "no connection" : !conn.HasUsableKey ? "no usable key" : "LiveTrading=false");
            return new NullExecutionProvider(_loggerFactory.CreateLogger<NullExecutionProvider>());
        }

        // Per-tenant signer from the decrypted private key.
        var keyOpts = new KeyVaultOptions
        {
            PrivateKey = conn.PrivateKey!,
            SignatureType = conn.SignatureType,
            Funder = conn.Funder,
            LocalAddress = conn.WalletAddress ?? "0x0000000000000000000000000000000000000000"
        };
        var keyVault = new NethereumKeyVault(
            Options.Create(keyOpts),
            _loggerFactory.CreateLogger<NethereumKeyVault>());

        var execOpts = new PolymarketExecutionOptions
        {
            ClobBaseUrl = conn.ClobBaseUrl,
            GammaBaseUrl = conn.GammaBaseUrl,
            LiveTrading = conn.LiveTrading,
            ChainId = conn.ChainId
        };

        _logger.LogInformation("Platform connector → live Polymarket CLOB for wallet {Address}", conn.WalletAddress);
        return new PolymarketExecutionProvider(
            _httpFactory.CreateClient("polymarket-clob"),
            keyVault,
            Options.Create(execOpts),
            Options.Create(keyOpts),
            _loggerFactory.CreateLogger<PolymarketExecutionProvider>());
    }

    /// <summary>Config fingerprint — changes whenever any connector-affecting field changes, so the
    /// cached connector is rebuilt even if Invalidate was somehow missed. Excludes secrets' plaintext
    /// (uses a presence flag + endpoints/flags only).</summary>
    private static string Fingerprint(ResolvedConnection? c)
    {
        if (c is null) return "none";
        var hasKey = c.HasUsableKey ? "k1" : "k0";
        return string.Join('|', c.ConnectorId, hasKey, c.WalletAddress, c.SignatureType,
            c.Funder, c.ClobBaseUrl, c.GammaBaseUrl, c.ChainId, c.LiveTrading);
    }

    private readonly record struct CacheEntry(string Fingerprint, IPlatformConnector Connector);
}
