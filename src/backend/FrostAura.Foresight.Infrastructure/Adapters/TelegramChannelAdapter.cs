using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TelegramChannelAdapter> _logger;

    public string ChannelId => "telegram";
    public bool SupportsRichContent => true;

    public TelegramChannelAdapter(HttpClient http, IOptions<TelegramBotOptions> opts, IServiceScopeFactory scopes, ILogger<TelegramChannelAdapter> logger)
    {
        _http = http;
        _opts = opts.Value;
        _scopes = scopes;
        _logger = logger;
        // Token is guaranteed present: DI only registers this adapter when TelegramBotOptions.Enabled.
        _http.BaseAddress = new Uri($"https://api.telegram.org/bot{_opts.Token}/");
    }

    /// <summary>
    /// Resolve the destination chat for a tenant: the bot is global, but the chat id is per-tenant
    /// (TenantSettings.TelegramChatId, editable in-app). Falls back to the global env-seeded default
    /// when the tenant hasn't set one, so the admin/dev tenant keeps working out of the box.
    /// </summary>
    private async Task<long?> ResolveChatIdAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant?.Settings.TelegramChatId is { } perTenant) return perTenant;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: could not read per-tenant chat id for {Tenant}; using global default", tenantId);
        }
        return _opts.ResolvedNotifyChatId;
    }

    public async Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(tenantId, ct);
        if (chatId is null)
        {
            _logger.LogWarning("Telegram notification dropped — no chat id configured for tenant {Tenant} and no global default.", tenantId);
            return;
        }
        var text = string.IsNullOrWhiteSpace(notification.Title)
            ? notification.Body
            : $"{notification.Title}\n\n{notification.Body}";
        await SendMessageAsync(chatId.Value, text, ct);
    }

    public async Task SendRichAsync(Guid tenantId, NotificationKind kind, string title, RichContent content, CancellationToken ct)
    {
        var chatId = await ResolveChatIdAsync(tenantId, ct);
        if (chatId is null) { _logger.LogWarning("Telegram rich notification dropped — no chat configured for tenant {Tenant}.", tenantId); return; }
        await SendRichToChatAsync(chatId.Value, title, content, ct);
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
        if (!string.IsNullOrWhiteSpace(content.Monospace)) mono.AppendLine(content.Monospace);
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

        // Only attach reply_markup when there are actually buttons — Telegram rejects a null/empty
        // reply_markup with "object expected as reply markup" (the JSON serialiser emits null otherwise).
        if (replyMarkup is null)
            await PostSendMessageAsync(new { chat_id = chatId, text = sb.ToString(), parse_mode = "HTML" }, ct);
        else
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

    // ── Single chart widget (sendPhoto returning the message id + editMessageMedia in place) ──────

    /// <summary>Send a PNG with an optional caption + single URL button; returns the new message id.</summary>
    public async Task<long?> SendPhotoReturningIdAsync(long chatId, byte[] png, string? caption, string? buttonText, string? buttonUrl, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
            };
            if (!string.IsNullOrWhiteSpace(caption)) { form.Add(new StringContent(caption), "caption"); form.Add(new StringContent("HTML"), "parse_mode"); }
            var kb = UrlKeyboardJson(buttonText, buttonUrl);
            if (kb is not null) form.Add(new StringContent(kb), "reply_markup");
            var photo = new ByteArrayContent(png);
            photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(photo, "photo", "chart.png");

            using var resp = await _http.PostAsync("sendPhoto", form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { _logger.LogWarning("Telegram sendPhoto failed: {Status} {Body}", resp.StatusCode, body); return null; }
            return ParseMessageId(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Telegram sendPhoto (chart) error"); return null; }
    }

    /// <summary>Replace an existing message's photo + caption + button in place (editMessageMedia).
    /// Returns false when the message can't be edited (e.g. deleted) so the caller can re-send.</summary>
    public async Task<bool> EditMessagePhotoAsync(long chatId, long messageId, byte[] png, string? caption, string? buttonText, string? buttonUrl, CancellationToken ct)
    {
        try
        {
            var media = new { type = "photo", media = "attach://photo", caption = caption ?? "", parse_mode = "HTML" };
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
                { new StringContent(messageId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "message_id" },
                { new StringContent(System.Text.Json.JsonSerializer.Serialize(media)), "media" },
            };
            var kb = UrlKeyboardJson(buttonText, buttonUrl);
            if (kb is not null) form.Add(new StringContent(kb), "reply_markup");
            var photo = new ByteArrayContent(png);
            photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(photo, "photo", "chart.png");

            using var resp = await _http.PostAsync("editMessageMedia", form, ct);
            if (resp.IsSuccessStatusCode) return true;
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Telegram editMessageMedia failed: {Status} {Body}", resp.StatusCode, body);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Telegram editMessageMedia error"); return false; }
    }

    private static string? UrlKeyboardJson(string? text, string? url)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(url)) return null;
        var kb = new { inline_keyboard = new[] { new[] { new { text, url } } } };
        return System.Text.Json.JsonSerializer.Serialize(kb);
    }

    private static long? ParseMessageId(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("message_id", out var mid))
                return mid.GetInt64();
        }
        catch { /* unparseable — no id */ }
        return null;
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

    /// <summary>Send a pre-built HTML message (parse_mode=HTML) — used by the command listener for the
    /// /start and /connect replies (so the chat id can be a tap-to-copy &lt;code&gt; span).</summary>
    public Task SendHtmlAsync(long chatId, string html, CancellationToken ct)
        => PostSendMessageAsync(new { chat_id = chatId, text = html, parse_mode = "HTML" }, ct);

    /// <summary>
    /// Long-poll getUpdates for inbound messages. Returns the (possibly empty) update list on a clean
    /// 200, or NULL on any error/non-success (e.g. 409 Conflict when another instance is polling the
    /// same bot) so the caller can back off instead of hammering the API.
    /// </summary>
    public async Task<IReadOnlyList<TelegramUpdate>?> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        try
        {
            // allowed_updates=["message"] — we only care about typed commands, not callbacks/edits.
            var url = $"getUpdates?offset={offset}&timeout=25&allowed_updates=%5B%22message%22%5D";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 409)
                    _logger.LogWarning("Telegram getUpdates 409 Conflict — another instance is polling this bot. Backing off. Only one instance should run the command listener (set Telegram__EnableCommandListener=false here, or stop the other).");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken: ct);
            return parsed?.Result ?? (IReadOnlyList<TelegramUpdate>)Array.Empty<TelegramUpdate>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Telegram getUpdates failed"); return null; }
    }

    /// <summary>Replace the bot's command menu with a clean slate — only /start and /connect.</summary>
    public async Task SetMyCommandsAsync(CancellationToken ct)
    {
        var payload = new
        {
            commands = new[]
            {
                new { command = "start",   description = "Connect, or see your P&L stats" },
                new { command = "connect", description = "Show your chat id to connect" },
            }
        };
        try
        {
            using var resp = await _http.PostAsJsonAsync("setMyCommands", payload, ct);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Telegram setMyCommands failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Telegram setMyCommands error"); }
    }

    /// <summary>Ensure no webhook is set so long-polling getUpdates works (a stale webhook 409s it).</summary>
    public async Task DeleteWebhookAsync(CancellationToken ct)
    {
        try { using var _ = await _http.PostAsync("deleteWebhook", content: null, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Telegram deleteWebhook failed (non-fatal)"); }
    }

    // Inbound command listening is driven by the TelegramCommandListenerService hosted service (it
    // owns the getUpdates poll loop + per-command DI scope). These port members are intentional no-ops.
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
