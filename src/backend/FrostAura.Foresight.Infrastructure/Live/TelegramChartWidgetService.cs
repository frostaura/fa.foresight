using FrostAura.Foresight.Infrastructure.Paper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Drives the Telegram chart off the paper event hub: on every RESOLVED bet it sends a per-bet chart
/// photo (captioned with that bet's P&amp;L) and refreshes the connected user's /start dashboard in
/// place. All the work lives in <see cref="TelegramChartComposer"/>; this is just the subscription +
/// resilient loop. Registered only when a Telegram bot is configured.
/// </summary>
public sealed class TelegramChartWidgetService : BackgroundService
{
    private readonly IPaperTradingEventHub _events;
    private readonly TelegramChartComposer _composer;
    private readonly ILogger<TelegramChartWidgetService> _logger;

    public TelegramChartWidgetService(
        IPaperTradingEventHub events,
        TelegramChartComposer composer,
        ILogger<TelegramChartWidgetService> logger)
    {
        _events = events;
        _composer = composer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Telegram chart service started");
        try
        {
            await foreach (var evt in _events.Subscribe(ct))
            {
                if (evt.Kind != PaperTradingEventKind.BetResolved || evt.Bet is null) continue;
                try { await _composer.OnBetResolvedAsync(evt.Session, evt.Bet, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Telegram chart update failed (non-fatal)"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        _logger.LogInformation("Telegram chart service stopped");
    }
}
