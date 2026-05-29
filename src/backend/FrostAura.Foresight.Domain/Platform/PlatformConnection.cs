using FrostAura.Foresight.Domain.Tenancy;

namespace FrostAura.Foresight.Domain.Platform;

/// <summary>
/// Per-tenant trading-platform connection configuration. One row per (tenant, connector); the
/// <see cref="IsDefault"/> row is the tenant's active connector. Replaces the global env-bound
/// <c>KeyVaultOptions</c>/<c>PolymarketExecutionOptions</c> as the authoritative source of the
/// wallet key, signing parameters, endpoints, and the live-trading flag.
///
/// Secrets are stored ENCRYPTED at rest (ASP.NET Core Data Protection). The raw private key and any
/// API secret never live in plaintext in the DB and are decrypted only at signing/derivation time by
/// the connector factory. Env vars seed this row once for the <c>default</c> tenant; the row is
/// authoritative and in-app editable thereafter.
/// </summary>
public sealed class PlatformConnection : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Stable connector identifier. Currently always "polymarket".</summary>
    public string ConnectorId { get; set; } = "polymarket";

    /// <summary>True when this is the tenant's active connector for live execution.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Data-Protection-encrypted wallet private key (hex). Empty/null ⇒ no usable key.</summary>
    public string? PrivateKeyEncrypted { get; set; }

    /// <summary>Optional Data-Protection-encrypted CLOB API secret override. Creds are normally derived
    /// at runtime from the wallet; this is kept for manual override scenarios.</summary>
    public string? ApiSecretEncrypted { get; set; }

    /// <summary>Public wallet address (derived from the key on write). Non-secret.</summary>
    public string? WalletAddress { get; set; }

    /// <summary>Polymarket EIP-712 signatureType: 0 = EOA, 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE.</summary>
    public int SignatureType { get; set; }

    /// <summary>Optional funder/maker address override (used with proxy/safe signature types).</summary>
    public string? Funder { get; set; }

    public string ClobBaseUrl { get; set; } = "https://clob.polymarket.com";
    public string GammaBaseUrl { get; set; } = "https://gamma-api.polymarket.com";
    public int ChainId { get; set; } = 137;

    /// <summary>Master live-trading switch for this tenant. Even when true, real orders also require
    /// the in-memory /golive arm — this flag is the per-tenant config gate beneath the runtime arm.</summary>
    public bool LiveTrading { get; set; }

    /// <summary>Optional per-trade cap (USD) surfaced from the connection editor. 0 ⇒ unset.</summary>
    public decimal MaxTradeUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
