namespace FrostAura.Foresight.Domain.Tenancy;

/// <summary>
/// Top-level tenant aggregate. Every other entity (except this one) implements ITenantScoped
/// and references this tenant by Id.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public TenantSettings Settings { get; set; } = new();
}

/// <summary>
/// Per-tenant runtime defaults. Each value is overridable per-flow.
/// </summary>
public sealed class TenantSettings
{
    public bool AutotradeEnabled { get; set; } = false;
    public string DefaultJurisdiction { get; set; } = "global-ex-us";
    public string DefaultLlmProviderId { get; set; } = "openrouter";

    /// <summary>
    /// Telegram chat id that receives THIS tenant's outbound notifications. The bot itself is global
    /// (one Foresight bot, token in app config); only the destination chat is per-tenant. Null ⇒ fall
    /// back to the global default chat (seeded from env for the admin/dev tenant). Stored in the
    /// tenant's jsonb settings, editable in-app — never a per-user env var.
    /// </summary>
    public long? TelegramChatId { get; set; }

    /// <summary>
    /// Message id of the single live-chart widget in the tenant's Telegram chat. The chart is sent
    /// once then updated in place (editMessageMedia) on each candle, so it stays one message instead
    /// of spamming a new image every time. Null until the first chart is sent (or after it's deleted).
    /// </summary>
    public long? TelegramChartMessageId { get; set; }
}
