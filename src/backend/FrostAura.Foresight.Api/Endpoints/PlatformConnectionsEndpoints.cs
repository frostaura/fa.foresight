using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Platform;
using FrostAura.Foresight.Infrastructure.Persistence;
using FrostAura.Foresight.Infrastructure.Platform;
using FrostAura.Foresight.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// In-app editor for the current tenant's default ("polymarket") platform connection.
///
/// GET  /api/platform-connections/default → connection with secrets MASKED (walletAddress +
///      hasPrivateKey only; never the raw key) plus all non-secret fields.
/// PUT  /api/platform-connections/default → upsert. Omitting privateKey keeps the existing one;
///      a supplied key is encrypted-on-write and its wallet address re-derived. Saving invalidates
///      the connector-factory cache for the tenant so the next tick picks up the new config.
///
/// LiveTrading here is the per-tenant config gate beneath the /golive runtime arm — turning it on
/// does NOT by itself place real orders (the arm + a funded wallet are still required).
/// </summary>
public static class PlatformConnectionsEndpoints
{
    private const string DefaultConnectorId = "polymarket";

    public static void MapPlatformConnectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform-connections").WithTags("platform-connections");

        // GET — masked view of the tenant's default connection.
        g.MapGet("/default", async (
            ITenantContext tc,
            ForesightDbContext db,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenantId = tc.TenantId!.Value;

            var conn = await db.PlatformConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ConnectorId == DefaultConnectorId, ct);

            if (conn is null)
            {
                // No row yet — return the unconfigured shape so the editor can render defaults.
                return Results.Ok(new PlatformConnectionView(
                    ConnectorId: DefaultConnectorId,
                    IsDefault: true,
                    HasPrivateKey: false,
                    WalletAddress: null,
                    SignatureType: 0,
                    Funder: null,
                    ClobBaseUrl: "https://clob.polymarket.com",
                    GammaBaseUrl: "https://gamma-api.polymarket.com",
                    ChainId: 137,
                    LiveTrading: false,
                    MaxTradeUsd: 0m,
                    EffectivePrice: 0.55m,
                    RpcUrl: "https://polygon-rpc.com"));
            }

            return Results.Ok(new PlatformConnectionView(
                ConnectorId: conn.ConnectorId,
                IsDefault: conn.IsDefault,
                HasPrivateKey: !string.IsNullOrEmpty(conn.PrivateKeyEncrypted),
                WalletAddress: conn.WalletAddress,
                SignatureType: conn.SignatureType,
                Funder: conn.Funder,
                ClobBaseUrl: conn.ClobBaseUrl,
                GammaBaseUrl: conn.GammaBaseUrl,
                ChainId: conn.ChainId,
                LiveTrading: conn.LiveTrading,
                MaxTradeUsd: conn.MaxTradeUsd,
                EffectivePrice: conn.EffectivePrice,
                RpcUrl: conn.RpcUrl));
        });

        // PUT — upsert the tenant's default connection.
        g.MapPut("/default", async (
            UpdatePlatformConnectionRequest req,
            ITenantContext tc,
            ForesightDbContext db,
            ISecretProtector protector,
            IPlatformConnectorFactory factory,
            CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenantId = tc.TenantId!.Value;
            var now = DateTimeOffset.UtcNow;

            var conn = await db.PlatformConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ConnectorId == DefaultConnectorId, ct);
            var isNew = conn is null;
            if (conn is null)
            {
                conn = new PlatformConnection
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ConnectorId = DefaultConnectorId,
                    IsDefault = true,
                    CreatedAt = now
                };
            }

            // Private key: omitted/blank ⇒ keep existing; supplied ⇒ validate, encrypt, re-derive address.
            if (!string.IsNullOrWhiteSpace(req.PrivateKey))
            {
                var raw = req.PrivateKey.Trim();
                string walletAddress;
                try
                {
                    var ethKey = new EthECKey(raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw : "0x" + raw);
                    walletAddress = ethKey.GetPublicAddress();
                }
                catch
                {
                    return Results.BadRequest(new { error = "privateKey is not a valid wallet private key." });
                }
                conn.PrivateKeyEncrypted = protector.Protect(raw);
                conn.WalletAddress = walletAddress;
            }

            if (req.SignatureType.HasValue) conn.SignatureType = req.SignatureType.Value;
            if (req.Funder is not null) conn.Funder = string.IsNullOrWhiteSpace(req.Funder) ? null : req.Funder.Trim();
            if (!string.IsNullOrWhiteSpace(req.ClobBaseUrl)) conn.ClobBaseUrl = req.ClobBaseUrl.Trim();
            if (!string.IsNullOrWhiteSpace(req.GammaBaseUrl)) conn.GammaBaseUrl = req.GammaBaseUrl.Trim();
            if (req.ChainId.HasValue) conn.ChainId = req.ChainId.Value;
            if (req.LiveTrading.HasValue) conn.LiveTrading = req.LiveTrading.Value;
            if (req.MaxTradeUsd.HasValue) conn.MaxTradeUsd = req.MaxTradeUsd.Value;
            if (req.EffectivePrice.HasValue)
            {
                // Guard the fee/price into a sane band: must be a probability strictly in (0,1), and
                // realistically a slight premium over a coin flip for these near-50/50 markets.
                var p = req.EffectivePrice.Value;
                if (p < 0.50m || p > 0.95m)
                    return Results.BadRequest(new { error = "effectivePrice must be between 0.50 and 0.95 (the conservative fee/price for a near-50/50 contract)." });
                conn.EffectivePrice = p;
            }
            if (req.RpcUrl is not null) conn.RpcUrl = string.IsNullOrWhiteSpace(req.RpcUrl) ? null : req.RpcUrl.Trim();
            conn.UpdatedAt = now;

            if (isNew) db.PlatformConnections.Add(conn);
            await db.SaveChangesAsync(ct);

            // Invalidate the cached connector so the next tick rebuilds from the new config.
            factory.Invalidate(tenantId);

            return Results.Ok(new PlatformConnectionView(
                ConnectorId: conn.ConnectorId,
                IsDefault: conn.IsDefault,
                HasPrivateKey: !string.IsNullOrEmpty(conn.PrivateKeyEncrypted),
                WalletAddress: conn.WalletAddress,
                SignatureType: conn.SignatureType,
                Funder: conn.Funder,
                ClobBaseUrl: conn.ClobBaseUrl,
                GammaBaseUrl: conn.GammaBaseUrl,
                ChainId: conn.ChainId,
                LiveTrading: conn.LiveTrading,
                MaxTradeUsd: conn.MaxTradeUsd,
                EffectivePrice: conn.EffectivePrice,
                RpcUrl: conn.RpcUrl));
        });
    }

    /// <summary>Masked view — never carries the raw private key or any secret material.</summary>
    private sealed record PlatformConnectionView(
        string ConnectorId,
        bool IsDefault,
        bool HasPrivateKey,
        string? WalletAddress,
        int SignatureType,
        string? Funder,
        string ClobBaseUrl,
        string GammaBaseUrl,
        int ChainId,
        bool LiveTrading,
        decimal MaxTradeUsd,
        decimal EffectivePrice,
        string? RpcUrl);

    /// <summary>Upsert body. All fields optional; omitted = unchanged. privateKey is write-only.</summary>
    private sealed record UpdatePlatformConnectionRequest(
        string? PrivateKey,
        int? SignatureType,
        string? Funder,
        string? ClobBaseUrl,
        string? GammaBaseUrl,
        int? ChainId,
        bool? LiveTrading,
        decimal? MaxTradeUsd,
        decimal? EffectivePrice,
        string? RpcUrl);
}
