using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>Logs orders, never executes. Default for Phase 0–4 (shadow mode).</summary>
public sealed class NullExecutionProvider : IExecutionProvider
{
    private readonly ILogger<NullExecutionProvider> _logger;
    public NullExecutionProvider(ILogger<NullExecutionProvider> logger) { _logger = logger; }

    public string ProviderId => "null-execution";

    public Task<OrderReceipt> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        _logger.LogInformation("[SHADOW] would place {Side} {Qty}@{Price} on {Mkt} (tenant {Tenant})",
            request.Side, request.QuantityShares, request.LimitPrice, request.MarketExternalId, request.TenantId);
        var orderId = $"shadow-{Guid.NewGuid():N}";
        var state = new OrderState(orderId, request.Side, request.QuantityShares, 0m, 0m, OrderStatus.Pending, DateTimeOffset.UtcNow);
        return Task.FromResult(new OrderReceipt(orderId, state));
    }

    public Task<OrderReceipt?> SellAsync(SellRequest request, CancellationToken ct)
    {
        _logger.LogInformation("[SHADOW] would SELL {Side} {Qty}@{Price} on {Mkt} (tenant {Tenant})",
            request.Side, request.QuantityShares, request.LimitPrice, request.MarketExternalId, request.TenantId);
        return Task.FromResult<OrderReceipt?>(null); // disarmed — shadow logs only
    }

    public Task<OrderState> GetOrderStateAsync(string orderId, CancellationToken ct)
        => Task.FromResult(new OrderState(orderId, OrderSide.Yes, 0m, 0m, 0m, OrderStatus.Pending, DateTimeOffset.UtcNow));

    public Task CancelOrderAsync(string orderId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<PositionState>> GetOpenPositionsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PositionState>>(Array.Empty<PositionState>());

    public Task<MarketResolution?> GetMarketResolutionAsync(string conditionId, CancellationToken ct)
        => Task.FromResult<MarketResolution?>(null); // shadow — always "not resolved yet"
}

public sealed class KeyVaultOptions
{
    public string LocalAddress { get; set; } = "0x0000000000000000000000000000000000000000";

    /// <summary>Wallet private key for live signing (hex, with or without 0x). Empty ⇒ the platform
    /// uses the LocalKeyVault stub and cannot sign. Supplied only via secret/env, never committed.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>
    /// Polymarket EIP-712 signatureType field:
    ///   0 = EOA (default), 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE.
    /// Configurable so a proxy wallet can be switched in without a code rewrite.
    /// </summary>
    public int SignatureType { get; set; } = 0;

    /// <summary>
    /// Optional funder/maker address override. When null the signing EOA is used as maker.
    /// Set this when using POLY_PROXY or POLY_GNOSIS_SAFE where the funder differs from the signer.
    /// </summary>
    public string? Funder { get; set; } = null;

    public bool HasKey => !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>Phase 0 stub: never returns real key material. Real signing arrives at Phase 4.</summary>
public sealed class LocalKeyVault : IKeyVault
{
    private readonly KeyVaultOptions _opts;
    public LocalKeyVault(IOptions<KeyVaultOptions> opts) { _opts = opts.Value; }

    public string AdapterId => "local-stub";

    public Task<string> SignTypedDataAsync(string typedDataPayload, CancellationToken ct)
        => throw new InvalidOperationException("LocalKeyVault is a Phase 0 stub. Live signing requires the Phase 4 adapter.");

    public Task<string> GetPublicAddressAsync(CancellationToken ct) => Task.FromResult(_opts.LocalAddress);
}

/// <summary>Logs notifications to the application logger; useful for development before Discord/Telegram are wired.</summary>
public sealed class LogChannelAdapter : IChannelAdapter
{
    private readonly ILogger<LogChannelAdapter> _logger;
    public LogChannelAdapter(ILogger<LogChannelAdapter> logger) { _logger = logger; }

    public string ChannelId => "log";
    public bool SupportsRichContent => false;

    public Task SendAsync(Guid tenantId, OutboundNotification n, CancellationToken ct)
    {
        _logger.LogInformation("[NOTIFICATION:{Kind}] tenant={Tenant} {Title} | {Body}", n.Kind, tenantId, n.Title, n.Body);
        return Task.CompletedTask;
    }

    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Fans an outbound notification out to every configured channel adapter (Telegram, Discord, …).
/// Registered as the IChannelAdapter when more than one channel is live; a single channel resolves
/// directly to its own adapter, and none resolves to the log channel. Inbound (commands) is owned by
/// each channel's own listener, so the command-channel members here are no-ops.
/// </summary>
public sealed class CompositeChannelAdapter : IChannelAdapter
{
    private readonly IReadOnlyList<IChannelAdapter> _children;
    public CompositeChannelAdapter(IReadOnlyList<IChannelAdapter> children) { _children = children; }

    public string ChannelId => "composite";
    public bool SupportsRichContent => _children.Any(c => c.SupportsRichContent);

    public async Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
    {
        foreach (var c in _children) await c.SendAsync(tenantId, notification, ct);
    }

    public async Task SendRichAsync(Guid tenantId, NotificationKind kind, string title, RichContent content, CancellationToken ct)
    {
        foreach (var c in _children) await c.SendRichAsync(tenantId, kind, title, content, ct);
    }

    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}


