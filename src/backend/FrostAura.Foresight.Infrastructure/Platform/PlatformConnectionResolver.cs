using FrostAura.Foresight.Infrastructure.Persistence;
using FrostAura.Foresight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Platform;

/// <summary>
/// A tenant's active platform connection with secrets DECRYPTED, ready to construct a connector.
/// Held only transiently in memory — never logged, never persisted.
/// </summary>
public sealed record ResolvedConnection(
    Guid Id,
    Guid TenantId,
    string ConnectorId,
    string? PrivateKey,
    string? ApiSecret,
    string? WalletAddress,
    int SignatureType,
    string? Funder,
    string ClobBaseUrl,
    string GammaBaseUrl,
    int ChainId,
    bool LiveTrading,
    decimal MaxTradeUsd,
    decimal EffectivePrice,
    string? RpcUrl)
{
    public bool HasUsableKey => !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>
/// Reads a tenant's active (IsDefault) platform connection and decrypts its secrets.
/// Scoped — owns a request DbContext and the scoped <see cref="ISecretProtector"/>.
/// </summary>
public interface IPlatformConnectionResolver
{
    Task<ResolvedConnection?> GetAsync(Guid tenantId, CancellationToken ct);
}

public sealed class PlatformConnectionResolver : IPlatformConnectionResolver
{
    private readonly ForesightDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<PlatformConnectionResolver> _logger;

    public PlatformConnectionResolver(
        ForesightDbContext db,
        ISecretProtector protector,
        ILogger<PlatformConnectionResolver> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task<ResolvedConnection?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await _db.PlatformConnections
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsDefault)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            _logger.LogDebug("No default platform connection for tenant {Tenant}", tenantId);
            return null;
        }

        string? privateKey = null, apiSecret = null;
        try
        {
            privateKey = _protector.Unprotect(row.PrivateKeyEncrypted);
            apiSecret = _protector.Unprotect(row.ApiSecretEncrypted);
        }
        catch (Exception ex)
        {
            // A decrypt failure means the key ring is gone/rotated — degrade to "no usable key"
            // (shadow), never throw on the trading path.
            _logger.LogError(ex, "Failed to decrypt platform-connection secrets for tenant {Tenant} — treating as no key", tenantId);
        }

        return new ResolvedConnection(
            row.Id, row.TenantId, row.ConnectorId,
            privateKey, apiSecret, row.WalletAddress,
            row.SignatureType, row.Funder,
            row.ClobBaseUrl, row.GammaBaseUrl, row.ChainId,
            row.LiveTrading, row.MaxTradeUsd,
            row.EffectivePrice <= 0m || row.EffectivePrice >= 1m ? 0.55m : row.EffectivePrice,
            row.RpcUrl);
    }
}
