using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure.Adapters;

public sealed class TelegramBotOptions
{
    /// <summary>Bot token from @BotFather. When empty the platform falls back to the log channel.</summary>
    public string Token { get; set; } = "";

    /// <summary>Chat ids permitted to issue commands. A command from any other chat is ignored.</summary>
    public List<long> AllowedChatIds { get; set; } = new();

    /// <summary>Tenant slug a Telegram principal acts as. v1 single-user; promote to a per-chat map later.</summary>
    public string DefaultTenantSlug { get; set; } = "dean";

    /// <summary>Chat that receives outbound notifications. Defaults to the first allowed chat when unset.</summary>
    public long? NotifyChatId { get; set; }

    /// <summary>UTC hour (0–23) the daily briefing notification fires. Default 08:00 UTC.</summary>
    public int DailyBriefingHourUtc { get; set; } = 8;

    public bool Enabled => !string.IsNullOrWhiteSpace(Token);

    public long? ResolvedNotifyChatId => NotifyChatId ?? (AllowedChatIds.Count > 0 ? AllowedChatIds[0] : null);
}

/// <summary>
/// Telegram transport for the channel port. Outbound notifications + a raw send used by the
/// command listener to reply. This type is the transport only — the inbound poll loop lives in
/// <see cref="Live.TelegramCommandListenerService"/> and dispatch in <see cref="Live.CommandRouter"/>,
/// because command handlers need a per-command DI scope (DbContext is scoped) and this adapter is a
/// singleton. The <see cref="ICommandChannel"/> members are therefore satisfied as faithful no-ops:
/// listening is driven by the hosted service, not by an in-adapter registry.
/// </summary>
public sealed class TelegramChannelAdapter : IChannelAdapter
{
    private readonly HttpClient _http;
    private readonly TelegramBotOptions _opts;
    private readonly ILogger<TelegramChannelAdapter> _logger;

    public string ChannelId => "telegram";
    public bool SupportsRichContent => true;

    public TelegramChannelAdapter(HttpClient http, IOptions<TelegramBotOptions> opts, ILogger<TelegramChannelAdapter> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
        // Token is guaranteed present: DI only registers this adapter when TelegramBotOptions.Enabled.
        _http.BaseAddress = new Uri($"https://api.telegram.org/bot{_opts.Token}/");
    }

    public async Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
    {
        var chatId = _opts.ResolvedNotifyChatId;
        if (chatId is null)
        {
            _logger.LogWarning("Telegram notification dropped — no NotifyChatId/AllowedChatIds configured.");
            return;
        }
        var text = string.IsNullOrWhiteSpace(notification.Title)
            ? notification.Body
            : $"{notification.Title}\n\n{notification.Body}";
        await SendMessageAsync(chatId.Value, text, ct);
    }

    public Task SendRichAsync(Guid tenantId, NotificationKind kind, string title, RichContent content, CancellationToken ct)
    {
        var chatId = _opts.ResolvedNotifyChatId;
        if (chatId is null) { _logger.LogWarning("Telegram rich notification dropped — no chat configured."); return Task.CompletedTask; }
        return SendRichToChatAsync(chatId.Value, title, content, ct);
    }

    /// <summary>
    /// Native rich rendering: text + tables/charts inside an HTML &lt;pre&gt; block (monospace, so columns
    /// and sparklines align), and any button rows as a tappable inline keyboard whose callback_data
    /// re-enters the command pipeline. Used for /menu, comparisons, and rich notifications.
    /// </summary>
    public async Task SendRichToChatAsync(long chatId, string? title, RichContent content, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("<b>").Append(Html(title)).Append("</b>\n");
        if (!string.IsNullOrWhiteSpace(content.Text)) sb.Append(Html(content.Text)).Append('\n');
        var mono = new StringBuilder();
        if (content.Table is not null) mono.AppendLine(StripFences(content.Table.RenderMonospace()));
        if (content.Chart is not null && content.ImagePng is null) mono.AppendLine(content.Chart.RenderAscii());
        if (mono.Length > 0) sb.Append("<pre>").Append(Html(mono.ToString().TrimEnd())).Append("</pre>");

        // Image takes the photo path; the assembled HTML rides as the caption (Telegram captions are
        // capped at 1024 chars, so we trim to fit and put the rest in a follow-up text message).
        if (content.ImagePng is { Length: > 0 } png)
        {
            var caption = sb.ToString();
            string? overflow = null;
            if (caption.Length > 1024)
            {
                // Try to break at the last newline before the cap so we don't split a sentence/table row.
                var cut = caption[..1024].LastIndexOf('\n');
                if (cut < 600) cut = 1024;
                overflow = caption[cut..];
                caption = caption[..cut];
            }
            await SendPhotoAsync(chatId, png, caption, content.Buttons, ct);
            if (!string.IsNullOrWhiteSpace(overflow))
                await PostSendMessageAsync(new { chat_id = chatId, text = "<pre>" + Html(overflow.TrimStart('\n')) + "</pre>", parse_mode = "HTML" }, ct);
            return;
        }

        object? replyMarkup = content.Buttons is { Count: > 0 }
            ? new { inline_keyboard = content.Buttons.Select(r => r.Buttons.Select(b => new { text = b.Label, callback_data = Truncate64(b.Arg is null ? b.Command : $"{b.Command} {b.Arg}") }).ToArray()).ToArray() }
            : null;

        await PostSendMessageAsync(new { chat_id = chatId, text = sb.ToString(), parse_mode = "HTML", reply_markup = replyMarkup }, ct);
    }

    /// <summary>
    /// Send a PNG photo with an optional caption + inline keyboard. Used by Reports to deliver the
    /// rendered chart natively (Telegram's sendPhoto accepts the bytes as multipart/form-data).
    /// </summary>
    public async Task SendPhotoAsync(long chatId, byte[] png, string? caption, IReadOnlyList<RichButtonRow>? buttons, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
            if (!string.IsNullOrWhiteSpace(caption))
            {
                form.Add(new StringContent(caption), "caption");
                form.Add(new StringContent("HTML"), "parse_mode");
            }
            if (buttons is { Count: > 0 })
            {
                var keyboard = new
                {
                    inline_keyboard = buttons.Select(r => r.Buttons.Select(b => new
                    {
                        text = b.Label,
                        callback_data = (b.Arg is null ? b.Command : $"{b.Command} {b.Arg}")
                    }).ToArray()).ToArray()
                };
                form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(keyboard)), "reply_markup");
            }
            var photoContent = new ByteArrayContent(png);
            photoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(photoContent, "photo", "chart.png");

            using var resp = await _http.PostAsync("sendPhoto", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Telegram sendPhoto failed: {Status} {Body}", resp.StatusCode, body);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Telegram sendPhoto error to chat {ChatId}", chatId); }
    }

    /// <summary>Acknowledge a tapped inline-keyboard button so Telegram clears the loading state.</summary>
    public async Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)
    {
        try { await _http.PostAsJsonAsync("answerCallbackQuery", new { callback_query_id = callbackQueryId }, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "answerCallbackQuery failed"); }
    }

    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string StripFences(string s) => s.Replace("```", "").Trim('\n');
    private static string Truncate64(string s) => System.Text.Encoding.UTF8.GetByteCount(s) <= 64 ? s : s[..Math.Min(s.Length, 60)];

    /// <summary>
    /// Long-poll for inbound updates. <paramref name="offset"/> is one past the last update_id seen,
    /// so Telegram only returns new updates and marks the prior batch consumed. Used by the listener.
    /// </summary>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"getUpdates?offset={offset}&timeout={timeoutSeconds}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram getUpdates failed: {Status} {Body}", resp.StatusCode, body);
            return Array.Empty<TelegramUpdate>();
        }
        var payload = await resp.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken: ct);
        return payload?.Result ?? new List<TelegramUpdate>();
    }

    /// <summary>Live connectivity check: calls getMe and reports the bot identity or the failure reason.</summary>
    public async Task<ChannelHealth> TestConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("getMe", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return new ChannelHealth(false, $"{(int)resp.StatusCode}: {body}");
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var username = doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("username", out var u) ? u.GetString() : null;
            return new ChannelHealth(true, $"Connected as @{username ?? "?"}; notify chat {_opts.ResolvedNotifyChatId?.ToString() ?? "(none)"}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ChannelHealth(false, ex.Message); }
    }

    /// <summary>Raw send used by the command listener to reply to the originating chat.</summary>
    public Task SendMessageAsync(long chatId, string text, CancellationToken ct)
        => PostSendMessageAsync(new { chat_id = chatId, text }, ct);

    private async Task PostSendMessageAsync(object payload, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("sendMessage", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Telegram sendMessage failed: {Status} {Body}", resp.StatusCode, body);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage error");
        }
    }

    // Listening is handled by TelegramCommandListenerService (scope-safe). These satisfy the port.
    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Telegram getUpdates response shapes — only the fields the listener consumes.</summary>
public sealed class TelegramUpdatesResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public List<TelegramUpdate> Result { get; set; } = new();
}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TelegramMessage? Message { get; set; }
    [JsonPropertyName("callback_query")] public TelegramCallbackQuery? CallbackQuery { get; set; }
}

public sealed class TelegramCallbackQuery
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("message")] public TelegramMessage? Message { get; set; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("chat")] public TelegramChat? Chat { get; set; }
}

public sealed class TelegramChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}
