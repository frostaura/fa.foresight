using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Per-tenant notification settings. The Telegram BOT is global (one Foresight bot, token in app
/// config); only the destination CHAT is per-tenant and editable here — never a per-user env var.
///
/// GET  /api/notifications/settings → { telegramChatId, botConfigured }.
/// PUT  /api/notifications/settings → set/clear the tenant's telegramChatId (null ⇒ fall back to the
///      global default chat seeded from env for the admin/dev tenant).
/// POST /api/notifications/test     → send a test message to the tenant's resolved chat.
/// </summary>
public static class NotificationsEndpoints
{
    public static void MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").WithTags("notifications");

        g.MapGet("/settings", async (ITenantContext tc, ForesightDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tc.TenantId!.Value, ct);
            var botConfigured = !string.IsNullOrWhiteSpace(config.GetSection("TelegramBot")["Token"]);
            return Results.Ok(new NotificationSettingsView(
                TelegramChatId: tenant?.Settings.TelegramChatId,
                BotConfigured: botConfigured));
        });

        g.MapPut("/settings", async (UpdateNotificationSettingsRequest req, ITenantContext tc, ForesightDbContext db, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tc.TenantId!.Value, ct);
            if (tenant is null) return Results.NotFound();

            // null/0 clears the per-tenant chat (falls back to the global default).
            tenant.Settings.TelegramChatId = req.TelegramChatId is > 0 ? req.TelegramChatId : null;
            // jsonb value-converter compares by reference, so flag the in-place mutation explicitly.
            db.Entry(tenant).Property(t => t.Settings).IsModified = true;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new NotificationSettingsView(tenant.Settings.TelegramChatId, BotConfigured: true));
        });

        g.MapPost("/test", async (ITenantContext tc, IChannelAdapter channel, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            await channel.SendAsync(tc.TenantId!.Value, new OutboundNotification(
                NotificationKind.DailyBriefing,
                "Foresight test notification",
                "If you can read this, your notifications are wired up correctly. ✅"), ct);
            return Results.Ok(new { sent = true });
        });
    }

    private sealed record NotificationSettingsView(long? TelegramChatId, bool BotConfigured);
    private sealed record UpdateNotificationSettingsRequest(long? TelegramChatId);
}
