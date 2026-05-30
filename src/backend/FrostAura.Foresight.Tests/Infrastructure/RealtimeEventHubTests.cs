using FluentAssertions;
using FrostAura.Foresight.Infrastructure.Live;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Unit tests for the SSE fan-out hubs that replaced the frontend's fixed-interval polling
/// (<see cref="ModelEventHub"/>, <see cref="LiveEventHub"/>) and the arm-state event that
/// <see cref="LiveTradingArm"/> now publishes.
///
/// The subscribe-then-publish ordering is deterministic, not timing-based: calling
/// <c>MoveNextAsync()</c> once runs the async iterator synchronously up to its first await, which is
/// where the per-subscriber channel registers itself in the hub. Publishing AFTER that call is
/// therefore guaranteed to be observed — no sleeps, no flakiness.
/// </summary>
public class RealtimeEventHubTests
{
    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ModelEventHub_delivers_published_event_to_subscriber()
    {
        var hub = new ModelEventHub();
        var tenantId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        using var cts = Timeout();

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync(); // registers the subscriber channel synchronously

        hub.Publish(new ModelEvent(tenantId, modelId, ModelEventKind.Trained, null));

        (await move).Should().BeTrue();
        sub.Current.TenantId.Should().Be(tenantId);
        sub.Current.ModelId.Should().Be(modelId);
        sub.Current.Kind.Should().Be(ModelEventKind.Trained);
    }

    [Fact]
    public async Task ModelEventHub_fans_out_to_every_subscriber()
    {
        var hub = new ModelEventHub();
        using var cts = Timeout();

        await using var a = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        await using var b = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var moveA = a.MoveNextAsync();
        var moveB = b.MoveNextAsync();

        hub.Publish(new ModelEvent(Guid.NewGuid(), Guid.NewGuid(), ModelEventKind.Failed, "boom"));

        (await moveA).Should().BeTrue();
        (await moveB).Should().BeTrue();
        a.Current.Error.Should().Be("boom");
        b.Current.Error.Should().Be("boom");
    }

    [Fact]
    public async Task LiveEventHub_delivers_session_changed_event()
    {
        var hub = new LiveEventHub();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using var cts = Timeout();

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync();

        hub.Publish(new LiveEvent(tenantId, LiveEventKind.SessionChanged, Armed: null, SessionId: sessionId));

        (await move).Should().BeTrue();
        sub.Current.Kind.Should().Be(LiveEventKind.SessionChanged);
        sub.Current.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task LiveTradingArm_Confirm_publishes_armed_true_event()
    {
        var hub = new LiveEventHub();
        var arm = new LiveTradingArm(scopes: null, logger: null, events: hub);
        var tenantId = Guid.NewGuid();
        using var cts = Timeout();

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync();

        var code = arm.RequestCode(tenantId);
        arm.Confirm(tenantId, code).Should().BeTrue();

        (await move).Should().BeTrue();
        sub.Current.TenantId.Should().Be(tenantId);
        sub.Current.Kind.Should().Be(LiveEventKind.ArmChanged);
        sub.Current.Armed.Should().BeTrue();
    }

    [Fact]
    public async Task LiveTradingArm_Disarm_publishes_armed_false_event()
    {
        var hub = new LiveEventHub();
        var arm = new LiveTradingArm(scopes: null, logger: null, events: hub);
        var tenantId = Guid.NewGuid();
        using var cts = Timeout();

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync();

        arm.Disarm(tenantId);

        (await move).Should().BeTrue();
        sub.Current.Kind.Should().Be(LiveEventKind.ArmChanged);
        sub.Current.Armed.Should().BeFalse();
    }

    [Fact]
    public void LiveTradingArm_without_event_hub_still_arms()
    {
        // The hub is optional (null in pure-state-machine unit contexts); arming must not require it.
        var arm = new LiveTradingArm();
        var tenantId = Guid.NewGuid();

        var code = arm.RequestCode(tenantId);

        arm.Confirm(tenantId, code).Should().BeTrue();
        arm.IsArmed(tenantId).Should().BeTrue();
    }
}
