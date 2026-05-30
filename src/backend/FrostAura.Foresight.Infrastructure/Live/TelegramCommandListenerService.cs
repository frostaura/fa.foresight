using System.Globalization;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Inbound Telegram command listener (long-polling getUpdates). Deliberately MINIMAL — the bot only
/// understands two commands, set as the entire menu (clean slate):
///   /start   — if this chat is already connected to a tenant, welcome back + P&amp;L stats; otherwise
///              show the chat id + how to connect (same as /connect).
///   /connect — always show the chat id + connect instructions.
/// Every other message is ignored. Registered only when a Telegram bot token is configured.
/// </summary>
public sealed class TelegramCommandListenerService : BackgroundService
{
    private readonly TelegramChannelAdapter _telegram;
    private readonly TelegramChartComposer _charts;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TelegramCommandListenerService> _logger;

    public TelegramCommandListenerService(
        TelegramChannelAdapter telegram,
        TelegramChartComposer charts,
        IServiceScopeFactory scopes,
        ILogger<TelegramCommandListenerService> logger)
    {
        _telegram = telegram;
        _charts = charts;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _telegram.DeleteWebhookAsync(ct);   // ensure long-polling isn't blocked by a stale webhook
        await _telegram.SetMyCommandsAsync(ct);   // clean slate: only /start and /connect in the menu
        _logger.LogInformation("Telegram command listener started (commands: /start, /connect)");

        long offset = 0;
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _telegram.GetUpdatesAsync(offset, ct);
                if (updates is null)
                {
                    // Error / 409 conflict — back off (exponential, capped) so we don't spam the API.
                    try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
                    continue;
                }
                backoff = TimeSpan.FromSeconds(1); // healthy response → reset
                foreach (var u in updates)
                {
                    offset = u.UpdateId + 1;
                    var text = u.Message?.Text?.Trim();
                    var chatId = u.Message?.Chat?.Id;
                    if (string.IsNullOrEmpty(text) || chatId is null) continue;
                    await DispatchAsync(text!, chatId.Value, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram listener loop error — backing off");
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
            }
        }
        _logger.LogInformation("Telegram command listener stopped");
    }

    private async Task DispatchAsync(string text, long chatId, CancellationToken ct)
    {
        // First token, slash + @botname stripped: "/start", "/start@FooBot", "/connect now" → start/connect.
        var token = text.Split(new[] { ' ', '\n', '\t' }, 2)[0].TrimStart('/');
        var at = token.IndexOf('@');
        if (at >= 0) token = token[..at];

        switch (token.ToLowerInvariant())
        {
            case "start":   await HandleStartAsync(chatId, ct); break;
            case "connect": await SendConnectAsync(chatId, ct); break;
            // Anything else is ignored — the bot only speaks /start and /connect.
        }
    }

    private async Task HandleStartAsync(long chatId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();

        // TelegramChatId lives inside the value-converted jsonb settings, so filter in memory
        // (single-user MVP — a handful of tenants at most).
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(ct);
        var tenant = tenants.FirstOrDefault(t => t.Settings.TelegramChatId == chatId);
        if (tenant is null) { await SendConnectAsync(chatId, ct); return; }

        // Connected → welcome back + overall P&L across the tenant's paper + live sessions.
        var paper = await db.PaperSessions.AsNoTracking().Where(s => s.TenantId == tenant.Id).ToListAsync(ct);
        var live  = await db.LiveSessions.AsNoTracking().Where(s => s.TenantId == tenant.Id).ToListAsync(ct);

        var net     = paper.Sum(s => s.CurrentBalance - s.InitialBalance) + live.Sum(s => s.CurrentBalance - s.InitialBalance);
        var initSum = paper.Sum(s => s.InitialBalance) + live.Sum(s => s.InitialBalance);
        var pct     = initSum > 0m ? net / initSum * 100m : 0m;
        var active  = paper.Count(s => s.StoppedAt == null) + live.Count(s => s.StoppedAt == null);

        var pIds = paper.Select(s => s.Id).ToList();
        var lIds = live.Select(s => s.Id).ToList();
        var pBets = await db.PaperBets.AsNoTracking().Where(b => pIds.Contains(b.SessionId) && b.Resolved).ToListAsync(ct);
        var lBets = await db.LiveBets.AsNoTracking().Where(b => lIds.Contains(b.SessionId) && b.Resolved).ToListAsync(ct);
        var placed = pBets.Count + lBets.Count;
        var won    = pBets.Count(b => b.Outcome == "win") + lBets.Count(b => b.Outcome == "win");
        var hr     = placed > 0 ? (decimal)won / placed * 100m : 0m;

        var sign = net >= 0m ? "🟢" : "🔴";
        // Hit-rate icon: green once it clears the ~55% break-even for the conservative fee, amber if
        // above a coin-flip but not yet profitable, red below 50%.
        var hrIcon = hr >= 55m ? "🟢" : hr >= 50m ? "🟡" : "🔴";
        var html =
            "👋 <b>Welcome back to Foresight</b>\n\n" +
            $"{sign} <b>Net P&amp;L: {Signed(net)} ({SignedPct(pct)})</b>\n" +
            $"{hrIcon} Hit rate: {hr.ToString("0", CultureInfo.InvariantCulture)}%  ({won}/{placed} bets)\n\n" +
            $"Active sessions: {active}\n\n" +
            "You're connected — win/loss and session alerts arrive here automatically.";
        await _telegram.SendHtmlAsync(chatId, html, ct);

        // Live dashboard: a chart of the most-recent active session that then edits itself in place on
        // every resolved bet. No active session → the welcome text above is the whole reply.
        await _charts.SendDashboardAsync(chatId, tenant.Id, ct);
    }

    private Task SendConnectAsync(long chatId, CancellationToken ct)
    {
        // <code> makes the id tap-to-copy in Telegram.
        var html =
            "🔗 <b>Connect Telegram to Foresight</b>\n\n" +
            "Your chat id is:\n" +
            $"<code>{chatId}</code>\n\n" +
            "Tap it to copy, then in Foresight open:\n" +
            "<b>Live → Notifications → Your Telegram chat id</b>\n" +
            "Paste it there and hit <b>Save</b>.\n\n" +
            "After that, your win/loss and session P&amp;L alerts show up right here.";
        return _telegram.SendHtmlAsync(chatId, html, ct);
    }

    private static string Signed(decimal v) => (v >= 0 ? "+$" : "−$") + Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);
    private static string SignedPct(decimal v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
