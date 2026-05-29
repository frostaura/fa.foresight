using System.Net.Http.Headers;
using System.Net.Http.Json;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Adapters;

public sealed class DiscordBotOptions
{
    /// <summary>Bot token from the Discord developer portal. Empty ⇒ Discord disabled.</summary>
    public string Token { get; set; } = "";

    /// <summary>Channel ids (snowflakes, as strings) permitted to issue commands.</summary>
    public List<string> AllowedChannelIds { get; set; } = new();

    /// <summary>User ids permitted to issue commands. Empty ⇒ allow any user in an allowed channel.</summary>
    public List<string> AllowedUserIds { get; set; } = new();

    /// <summary>Tenant slug a Discord principal acts as. v1 single-user.</summary>
    public string DefaultTenantSlug { get; set; } = "dean";

    /// <summary>Channel that receives outbound notifications. Defaults to the first allowed channel.</summary>
    public string? NotifyChannelId { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Token);

    public string? ResolvedNotifyChannelId => NotifyChannelId ?? (AllowedChannelIds.Count > 0 ? AllowedChannelIds[0] : null);
}

/// <summary>
/// Discord transport for the channel port. Outbound goes over the REST API; inbound (commands) is
/// pumped by <see cref="Live.DiscordGatewayListenerService"/> over the gateway WebSocket, dispatched
/// through the shared <see cref="Live.CommandRouter"/>. Mirrors the Telegram adapter's split: this
/// type is the transport, the hosted service is the inbound pump (scope-per-command).
/// </summary>
public sealed class DiscordChannelAdapter : IChannelAdapter
{
    private readonly HttpClient _http;
    private readonly DiscordBotOptions _opts;
    private readonly ILogger<DiscordChannelAdapter> _logger;

    public string ChannelId => "discord";
    public bool SupportsRichContent => true;

    public DiscordChannelAdapter(HttpClient http, IOptions<DiscordBotOptions> opts, ILogger<DiscordChannelAdapter> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _opts.Token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/frostaura/fa.foresight, 1.0)");
    }

    public async Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
    {
        var channelId = _opts.ResolvedNotifyChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            _logger.LogWarning("Discord notification dropped — no NotifyChannelId/AllowedChannelIds configured.");
            return;
        }
        var text = string.IsNullOrWhiteSpace(notification.Title) ? notification.Body : $"**{notification.Title}**\n{notification.Body}";
        await SendMessageAsync(channelId, text, ct);
    }

    /// <summary>Post a message to a channel. Discord caps content at 2000 chars — long bodies are wrapped in a code block and truncated.</summary>
    public async Task SendMessageAsync(string channelId, string text, CancellationToken ct)
    {
        if (text.Length > 1990) text = text[..1990] + "…";
        try
        {
            using var resp = await _http.PostAsJsonAsync($"channels/{channelId}/messages", new { content = text }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Discord sendMessage failed: {Status} {Body}", resp.StatusCode, body);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord sendMessage error to channel {ChannelId}", channelId);
        }
    }

    /// <summary>Live connectivity check: calls users/@me and reports the bot identity or the failure reason.</summary>
    public async Task<ChannelHealth> TestConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("users/@me", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return new ChannelHealth(false, $"{(int)resp.StatusCode}: {body}");
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
            return new ChannelHealth(true, $"Connected as {username ?? "?"}; notify channel {_opts.ResolvedNotifyChannelId ?? "(none)"}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ChannelHealth(false, ex.Message); }
    }

    // Listening handled by DiscordGatewayListenerService (scope-safe). These satisfy the port.
    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}
