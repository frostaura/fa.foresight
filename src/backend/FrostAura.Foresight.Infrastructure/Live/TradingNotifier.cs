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
    /// Notify that a bet was resolved (win or loss, paper or live).
    /// </summary>
    public async Task NotifyBetResolvedAsync(
        Guid tenantId, Guid sessionId, Guid betId,
        string side, decimal size, decimal payout, bool won, decimal balanceAfter,
        CancellationToken ct)
    {
        var outcome = won ? "WIN" : "LOSS";
        var body = $"Session {sessionId:N} | {outcome} | {side} size={size:F4} payout={payout:F4} balance-after={balanceAfter:F4}";
        _logger.LogInformation(
            "Bet resolved — session={SessionId} betId={BetId} outcome={Outcome} side={Side} size={Size} payout={Payout} balanceAfter={BalanceAfter}",
            sessionId, betId, outcome, side, size, payout, balanceAfter);
        await SendSafeAsync(tenantId, NotificationKind.PositionResolution, $"Bet resolved: {outcome}", body, ct);
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
}
