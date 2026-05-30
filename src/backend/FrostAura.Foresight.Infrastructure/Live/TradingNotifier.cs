using System.Globalization;
using System.Linq;
using FrostAura.Foresight.Domain.Ports;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Thin facade that fires best-effort outbound notifications at key trading lifecycle moments via
/// IChannelAdapter. All calls are best-effort: any channel failure is caught, logged, and swallowed
/// so a notification error can never break the trading loop.
///
/// Lifecycle hooks exposed:
///   - Trade placed (live order placed on-chain)
///   - Bet resolved (win or loss + payout, paper or live)
///   - Session bust (balance ≤ 0 or can't cover next bet, paper or live)
///   - Circuit-breaker trip (per-session drawdown guardrail, live only)
/// </summary>
public sealed class TradingNotifier
{
    private readonly IChannelAdapter _channel;
    private readonly ILogger<TradingNotifier> _logger;

    public TradingNotifier(IChannelAdapter channel, ILogger<TradingNotifier> logger)
    {
        _channel = channel;
        _logger  = logger;
    }

    /// <summary>
    /// Notify that a live order was successfully placed on-chain.
    /// </summary>
    public async Task NotifyTradePlacedAsync(
        Guid tenantId, Guid sessionId, string orderId,
        string side, decimal size, decimal entryPrice, string? marketId,
        CancellationToken ct)
    {
        var body = $"Session {sessionId:N} | {side} {size:F4} @ {entryPrice:F4} | market={marketId ?? "n/a"} | orderId={orderId}";
        _logger.LogInformation(
            "Trade placed — session={SessionId} side={Side} size={Size} entryPrice={EntryPrice} orderId={OrderId}",
            sessionId, side, size, entryPrice, orderId);
        await SendSafeAsync(tenantId, NotificationKind.AutotradeExecution, "Trade placed", body, ct);
    }

    /// <summary>
    /// Notify that a bet was resolved (win or loss, paper or live), formatted as a compact card:
    /// a bold header (🟢 WIN / 🔴 LOSS · SYM int · time) over a monospace block showing this bet's
    /// P&amp;L + stake→payout, the session's overall P&amp;L and hit rate, and the new balance with a
    /// direction arrow. Colour is conveyed by emoji + ▲/▼ (Telegram text has no per-run colour).
    /// </summary>
    public async Task NotifyBetResolvedAsync(
        Guid tenantId, Guid sessionId, Guid betId,
        string symbol, string interval, long targetOpenTimeMs,
        decimal stake, decimal payout, bool won,
        decimal balanceAfter, decimal initialBalance,
        int betsWon, int betsPlaced,
        CancellationToken ct)
    {
        var (title, body) = BetCardFormatter.Format(
            symbol, interval, targetOpenTimeMs, stake, payout, won, balanceAfter, initialBalance, betsWon, betsPlaced);

        _logger.LogInformation(
            "Bet resolved — session={SessionId} betId={BetId} outcome={Outcome} stake={Stake} payout={Payout} balanceAfter={BalanceAfter}",
            sessionId, betId, won ? "WIN" : "LOSS", stake, payout, balanceAfter);

        await SendRichSafeAsync(tenantId, NotificationKind.PositionResolution, title,
            new RichContent(Monospace: body), ct);
    }

    /// <summary>
    /// Notify that a session busted (balance ≤ 0 or next bet exceeds bankroll), paper or live.
    /// </summary>
    public async Task NotifySessionBustAsync(
        Guid tenantId, Guid sessionId, string mode, decimal finalBalance,
        CancellationToken ct)
    {
        var body = $"Session {sessionId:N} [{mode}] BUSTED — final balance={finalBalance:F4}";
        _logger.LogWarning(
            "Session bust — session={SessionId} mode={Mode} finalBalance={FinalBalance}",
            sessionId, mode, finalBalance);
        await SendSafeAsync(tenantId, NotificationKind.CircuitBreakerTripped, "Session busted", body, ct);
    }

    /// <summary>
    /// Notify that a session was STOPPED by an error (e.g. a broken custom strategy that failed to
    /// evaluate) rather than a bust. Surfaced so a broken strategy never silently no-bets forever.
    /// </summary>
    public async Task NotifySessionErrorAsync(
        Guid tenantId, Guid sessionId, string mode, string reason,
        CancellationToken ct)
    {
        var body = $"Session {sessionId:N} [{mode}] STOPPED on error — {reason}";
        _logger.LogError("Session error — session={SessionId} mode={Mode} reason={Reason}", sessionId, mode, reason);
        await SendSafeAsync(tenantId, NotificationKind.CircuitBreakerTripped, "Session stopped (error)", body, ct);
    }

    /// <summary>
    /// Notify that the per-session drawdown circuit breaker tripped (live only).
    /// </summary>
    public async Task NotifyCircuitBreakerAsync(
        Guid tenantId, Guid sessionId, decimal drawdownPct, decimal threshold,
        CancellationToken ct)
    {
        var body = $"Session {sessionId:N} drawdown={drawdownPct:P1} ≥ threshold={threshold:P1} — session stopped.";
        _logger.LogWarning(
            "Circuit breaker tripped — session={SessionId} drawdown={DrawdownPct:P1} threshold={Threshold:P1}",
            sessionId, drawdownPct, threshold);
        await SendSafeAsync(tenantId, NotificationKind.CircuitBreakerTripped, "Circuit breaker tripped", body, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort send: catches any channel failure, logs it at Warning, and returns normally.
    /// A channel error must never propagate to the caller — the trading loop must remain healthy.
    /// </summary>
    private async Task SendSafeAsync(
        Guid tenantId, NotificationKind kind, string title, string body, CancellationToken ct)
    {
        try
        {
            await _channel.SendAsync(tenantId, new OutboundNotification(kind, title, body), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel notification failed (kind={Kind}, title={Title}) — swallowed; trading loop unaffected", kind, title);
        }
    }

    /// <summary>Best-effort rich send — same swallow-and-log contract as <see cref="SendSafeAsync"/>.</summary>
    private async Task SendRichSafeAsync(
        Guid tenantId, NotificationKind kind, string title, RichContent content, CancellationToken ct)
    {
        try
        {
            await _channel.SendRichAsync(tenantId, kind, title, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel rich notification failed (kind={Kind}, title={Title}) — swallowed; trading loop unaffected", kind, title);
        }
    }
}
