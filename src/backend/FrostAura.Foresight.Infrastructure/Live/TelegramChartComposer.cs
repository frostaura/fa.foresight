using System.Globalization;
using FrostAura.Foresight.Domain.Paper;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Charts;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Builds + sends the Telegram chart in two modes:
///   • Per-bet — on every resolved paper bet, a NEW photo (the chart at that moment) captioned with
///     that bet's win/loss P&amp;L card. This is the notification stream.
///   • Dashboard — a single chart sent on /start for a connected user and then EDITED in place on every
///     bet, so the welcome screen is a live balance + P&amp;L dashboard (stored as TelegramChartMessageId).
/// Singleton: owns no per-request state and creates its own DI scope per call.
/// </summary>
public sealed class TelegramChartComposer
{
    private const int VisibleCandles = 31;

    private readonly IServiceScopeFactory _scopes;
    private readonly TelegramChannelAdapter _telegram;
    private readonly IConfiguration _config;
    private readonly ILogger<TelegramChartComposer> _logger;

    public TelegramChartComposer(
        IServiceScopeFactory scopes,
        TelegramChannelAdapter telegram,
        IConfiguration config,
        ILogger<TelegramChartComposer> logger)
    {
        _scopes = scopes;
        _telegram = telegram;
        _config = config;
        _logger = logger;
    }

    /// <summary>On a resolved bet: send a per-bet chart photo, then refresh the /start dashboard if present.</summary>
    public async Task OnBetResolvedAsync(PaperSession session, PaperBet bet, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == session.TenantId, ct);
        if (tenant?.Settings.TelegramChatId is not long chatId) return;

        var binance = scope.ServiceProvider.GetRequiredService<BinanceMarketDataClient>();
        var png = await RenderAsync(session, binance, ct);

        var (btnText, btnUrl) = ButtonFor("/trading/paper");

        // 1) Per-bet notification: a fresh photo captioned with this bet's card. If the chart can't
        //    render (e.g. Binance feed hiccup) fall back to the text card so the alert is never lost.
        var placed = session.Bets.Count(b => b.Resolved);
        var won = session.Bets.Count(b => b.Resolved && b.Outcome == "win");
        var caption = BetCardFormatter.Html(
            session.Symbol, session.Interval, bet.TargetOpenTime,
            bet.Size, bet.Payout ?? 0m, bet.Outcome == "win",
            bet.BalanceAfter ?? session.CurrentBalance, session.InitialBalance, won, placed);
        if (png is null)
        {
            await _telegram.SendHtmlAsync(chatId, caption, ct);
            return;
        }
        await _telegram.SendPhotoReturningIdAsync(chatId, png, caption, btnText, btnUrl, ct);

        // 2) Dashboard: edit the /start chart in place (silent live update).
        if (tenant.Settings.TelegramChartMessageId is long mid)
        {
            var ok = await _telegram.EditMessagePhotoAsync(chatId, mid, png, DashboardCaption(session), btnText, btnUrl, ct);
            if (!ok)
            {
                tenant.Settings.TelegramChartMessageId = null;   // dashboard message gone — forget it
                db.Entry(tenant).Property(t => t.Settings).IsModified = true;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>/start: send a fresh dashboard chart + stats and remember its id (edited live thereafter).
    /// Returns false when there's no active session to chart, so the caller can fall back to text.</summary>
    public async Task<bool> SendDashboardAsync(long chatId, Guid tenantId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return false;

        var session = await db.PaperSessions.AsNoTracking()
            .Include(s => s.Bets)
            .Where(s => s.TenantId == tenantId && s.StoppedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (session is null) return false;

        var binance = scope.ServiceProvider.GetRequiredService<BinanceMarketDataClient>();
        var png = await RenderAsync(session, binance, ct);
        if (png is null) return false;

        var (btnText, btnUrl) = ButtonFor("/trading/paper");
        var id = await _telegram.SendPhotoReturningIdAsync(chatId, png, DashboardCaption(session), btnText, btnUrl, ct);
        if (id is long newId)
        {
            tenant.Settings.TelegramChartMessageId = newId;
            db.Entry(tenant).Property(t => t.Settings).IsModified = true;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>Render the chart PNG for a session: last 31 candles + hit/miss dots + balance line +
    /// the all-time peak dotted line. Returns null if candles can't be fetched.</summary>
    private async Task<byte[]?> RenderAsync(PaperSession session, BinanceMarketDataClient binance, CancellationToken ct)
    {
        var raw = await binance.GetKlinesAsync(session.Symbol, session.Interval, VisibleCandles + 1, ct);
        if (raw.Count == 0) return null;
        var window = raw.OrderBy(c => c.OpenTime).TakeLast(VisibleCandles).ToList();
        long minOpen = window[0].OpenTime, maxOpen = window[^1].OpenTime;

        var betByTime = new Dictionary<long, PaperBet>();
        foreach (var b in session.Bets) betByTime[b.TargetOpenTime] = b;

        var candles = window.Select(c =>
        {
            var dot = ChartDotState.None;
            if (betByTime.TryGetValue(c.OpenTime, out var bet))
                dot = !bet.Resolved ? ChartDotState.Open : bet.Outcome == "win" ? ChartDotState.Hit : ChartDotState.Miss;
            return new ChartCandle(c.OpenTime, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, dot);
        }).ToList();

        var balance = session.Bets
            .Where(b => b.Resolved && b.BalanceAfter is not null && b.TargetOpenTime >= minOpen && b.TargetOpenTime <= maxOpen)
            .OrderBy(b => b.TargetOpenTime)
            .Select(b => new ChartBalance(b.TargetOpenTime, (double)b.BalanceAfter!.Value))
            .ToList();

        // All-time peak across the WHOLE session (not just in-view) → dotted line.
        var peak = (double)session.InitialBalance;
        foreach (var b in session.Bets)
            if (b.Resolved && b.BalanceAfter is { } ba) peak = Math.Max(peak, (double)ba);

        var intervalMs = maxOpen > minOpen && window.Count > 1 ? (maxOpen - minOpen) / (window.Count - 1) : 5 * 60_000L;
        return CandlestickChartRenderer.Render(new ChartModel(candles, balance, peak, intervalMs));
    }

    private static string DashboardCaption(PaperSession s)
    {
        var sym = s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? s.Symbol[..^4] : s.Symbol;
        var net = s.CurrentBalance - s.InitialBalance;
        var pct = s.InitialBalance > 0m ? net / s.InitialBalance * 100m : 0m;
        var placed = s.Bets.Count(b => b.Resolved);
        var won = s.Bets.Count(b => b.Resolved && b.Outcome == "win");
        var hr = placed > 0 ? (decimal)won / placed * 100m : 0m;
        var sign = net >= 0m ? "🟢" : "🔴";
        return
            $"<b>{sym} {s.Interval}</b> · ${s.CurrentBalance.ToString("0.00", CultureInfo.InvariantCulture)}  " +
            $"{sign} <b>{(net >= 0m ? "+" : "−")}${Math.Abs(net).ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"({(pct >= 0m ? "+" : "−")}{Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture)}%)</b>\n" +
            $"Hit {hr.ToString("0", CultureInfo.InvariantCulture)}%  ({won}/{placed})";
    }

    private (string? text, string? url) ButtonFor(string path)
    {
        var baseUrl = _config["Foresight:PublicUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return (null, null);
        return ("Open in Foresight ↗", baseUrl.TrimEnd('/') + path);
    }
}
