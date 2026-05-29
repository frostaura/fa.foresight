using FluentAssertions;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TradingNotifier"/>.
///
/// Covers:
///   - Each lifecycle method emits the expected NotificationKind via IChannelAdapter.
///   - A channel adapter that throws does NOT propagate the exception to the caller.
/// </summary>
public class TradingNotifierTests
{
    private static readonly Guid TenantId  = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid BetId     = Guid.NewGuid();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static (TradingNotifier Notifier, CapturingChannelAdapter Channel) Build()
    {
        var channel  = new CapturingChannelAdapter();
        var notifier = new TradingNotifier(channel, NullLogger<TradingNotifier>.Instance);
        return (notifier, channel);
    }

    private static (TradingNotifier Notifier, ThrowingChannelAdapter Channel) BuildThrowing()
    {
        var channel  = new ThrowingChannelAdapter();
        var notifier = new TradingNotifier(channel, NullLogger<TradingNotifier>.Instance);
        return (notifier, channel);
    }

    // ── Trade placed ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyTradePlaced_sends_AutotradeExecution_notification()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyTradePlacedAsync(
            TenantId, SessionId, orderId: "shadow-abc",
            side: "UP", size: 2.5m, entryPrice: 0.55m, marketId: "btc-up-1",
            CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Kind.Should().Be(NotificationKind.AutotradeExecution);
    }

    [Fact]
    public async Task NotifyTradePlaced_title_contains_placed()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyTradePlacedAsync(
            TenantId, SessionId, "ord-1", "UP", 1m, 0.5m, null, CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Title.Should().ContainEquivalentOf("placed");
    }

    // ── Bet resolved ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyBetResolved_win_sends_PositionResolution_with_WIN_in_title()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyBetResolvedAsync(
            TenantId, SessionId, BetId,
            side: "UP", size: 2m, payout: 3.6m, won: true, balanceAfter: 103.6m,
            CancellationToken.None);

        var notification = channel.Received.Should().ContainSingle().Subject;
        notification.Kind.Should().Be(NotificationKind.PositionResolution);
        notification.Title.Should().ContainEquivalentOf("WIN");
    }

    [Fact]
    public async Task NotifyBetResolved_loss_sends_PositionResolution_with_LOSS_in_title()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyBetResolvedAsync(
            TenantId, SessionId, BetId,
            side: "UP", size: 2m, payout: 0m, won: false, balanceAfter: 98m,
            CancellationToken.None);

        var notification = channel.Received.Should().ContainSingle().Subject;
        notification.Kind.Should().Be(NotificationKind.PositionResolution);
        notification.Title.Should().ContainEquivalentOf("LOSS");
    }

    // ── Session bust ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifySessionBust_sends_CircuitBreakerTripped_notification()
    {
        var (notifier, channel) = Build();

        await notifier.NotifySessionBustAsync(TenantId, SessionId, "paper", 0m, CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Kind.Should().Be(NotificationKind.CircuitBreakerTripped);
    }

    [Fact]
    public async Task NotifySessionBust_title_contains_bust()
    {
        var (notifier, channel) = Build();

        await notifier.NotifySessionBustAsync(TenantId, SessionId, "live", -0.01m, CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Title.Should().ContainEquivalentOf("bust");
    }

    // ── Circuit breaker ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyCircuitBreaker_sends_CircuitBreakerTripped_notification()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyCircuitBreakerAsync(TenantId, SessionId, 0.35m, 0.30m, CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Kind.Should().Be(NotificationKind.CircuitBreakerTripped);
    }

    [Fact]
    public async Task NotifyCircuitBreaker_title_contains_circuit()
    {
        var (notifier, channel) = Build();

        await notifier.NotifyCircuitBreakerAsync(TenantId, SessionId, 0.35m, 0.30m, CancellationToken.None);

        channel.Received.Should().ContainSingle()
            .Which.Title.Should().ContainEquivalentOf("circuit");
    }

    // ── Resilience: channel failure must not propagate ────────────────────────────

    [Fact]
    public async Task NotifyTradePlaced_channel_throws_does_not_throw_to_caller()
    {
        var (notifier, _) = BuildThrowing();

        var act = async () => await notifier.NotifyTradePlacedAsync(
            TenantId, SessionId, "ord", "UP", 1m, 0.5m, null, CancellationToken.None);

        await act.Should().NotThrowAsync("channel errors must be swallowed");
    }

    [Fact]
    public async Task NotifyBetResolved_channel_throws_does_not_throw_to_caller()
    {
        var (notifier, _) = BuildThrowing();

        var act = async () => await notifier.NotifyBetResolvedAsync(
            TenantId, SessionId, BetId, "UP", 1m, 0m, false, 99m, CancellationToken.None);

        await act.Should().NotThrowAsync("channel errors must be swallowed");
    }

    [Fact]
    public async Task NotifySessionBust_channel_throws_does_not_throw_to_caller()
    {
        var (notifier, _) = BuildThrowing();

        var act = async () => await notifier.NotifySessionBustAsync(
            TenantId, SessionId, "paper", 0m, CancellationToken.None);

        await act.Should().NotThrowAsync("channel errors must be swallowed");
    }

    [Fact]
    public async Task NotifyCircuitBreaker_channel_throws_does_not_throw_to_caller()
    {
        var (notifier, _) = BuildThrowing();

        var act = async () => await notifier.NotifyCircuitBreakerAsync(
            TenantId, SessionId, 0.35m, 0.30m, CancellationToken.None);

        await act.Should().NotThrowAsync("channel errors must be swallowed");
    }
}

// ── Fakes ─────────────────────────────────────────────────────────────────────

/// <summary>Records every SendAsync call for assertion.</summary>
internal sealed class CapturingChannelAdapter : IChannelAdapter
{
    public string ChannelId => "capturing";
    public bool SupportsRichContent => false;

    public List<OutboundNotification> Received { get; } = new();

    public Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
    {
        Received.Add(notification);
        return Task.CompletedTask;
    }

    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Always throws on SendAsync — verifies the notifier swallows channel errors.</summary>
internal sealed class ThrowingChannelAdapter : IChannelAdapter
{
    public string ChannelId => "throwing";
    public bool SupportsRichContent => false;

    public Task SendAsync(Guid tenantId, OutboundNotification notification, CancellationToken ct)
        => throw new InvalidOperationException("Simulated channel failure");

    public Task RegisterCommandHandlerAsync(string command, Func<InboundCommand, CancellationToken, Task<CommandResponse>> handler) => Task.CompletedTask;
    public Task StartListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
    public Task StopListeningAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}
