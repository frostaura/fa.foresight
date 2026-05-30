using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrostAura.Foresight.Infrastructure.Live;

public enum LiveEventKind
{
    /// <summary>The tenant's live-trading arm state flipped (armed ↔ disarmed).</summary>
    ArmChanged,
    /// <summary>A live session was created, advanced (bet placed/resolved), busted, or stopped.</summary>
    SessionChanged,
}

/// <summary>
/// One live-trading control-plane event. <c>Armed</c> carries the new arm state for
/// <see cref="LiveEventKind.ArmChanged"/>; <c>SessionId</c> identifies the affected session for
/// <see cref="LiveEventKind.SessionChanged"/>. The Live page subscribes to the SSE stream and
/// refetches arm status / the session list on the matching event — replacing the old 4–5s polls of
/// <c>/api/live/status</c> and <c>/api/sessions</c>.
/// </summary>
public sealed record LiveEvent(Guid TenantId, LiveEventKind Kind, bool? Armed, Guid? SessionId);

/// <summary>
/// In-process fan-out hub for live-trading arm + session events. <see cref="LiveTradingArm"/> and
/// <see cref="LiveSessionEngine"/> publish here; the SSE endpoint pushes the deltas to the browser.
/// Mirrors <see cref="LivePredictionEventHub"/> — bounded per-subscriber channel, DropOldest on
/// overflow. Singleton.
/// </summary>
public interface ILiveEventHub
{
    void Publish(LiveEvent evt);
    IAsyncEnumerable<LiveEvent> Subscribe(CancellationToken ct);
}

public sealed class LiveEventHub : ILiveEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<LiveEvent>> _subs = new();

    public void Publish(LiveEvent evt)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(evt);
    }

    public async IAsyncEnumerable<LiveEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subs[id] = ch;
        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            _subs.TryRemove(id, out _);
        }
    }
}
