using System.Collections.Concurrent;
using System.Threading.Channels;
using FrostAura.Foresight.Domain.Paper;

namespace FrostAura.Foresight.Infrastructure.Paper;

public enum PaperTradingEventKind
{
    SessionStarted,
    SessionStopped,
    SessionBust,
    BetPlaced,
    BetResolved
}

/// <summary>
/// Combined session + bet view pushed on every state change. Keeps the wire format consistent and
/// lets the client patch its in-memory copy from a single record. `Bet` is null when only session
/// state changed (e.g. SessionStarted, SessionStopped).
/// </summary>
public sealed record PaperTradingEvent(
    PaperTradingEventKind Kind,
    PaperSession Session,
    PaperBet? Bet);

/// <summary>
/// In-process fan-out hub for paper-trading state changes. Mirrors LivePredictionEventHub's
/// bounded-drop-oldest behavior so a slow client never stalls the processor loop. Subscribers
/// receive every event for the tenant; the client filters per (symbol, interval) on read.
/// </summary>
public interface IPaperTradingEventHub
{
    void Publish(PaperTradingEvent evt);
    IAsyncEnumerable<PaperTradingEvent> Subscribe(CancellationToken ct);
}

public sealed class PaperTradingEventHub : IPaperTradingEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<PaperTradingEvent>> _subs = new();

    public void Publish(PaperTradingEvent evt)
    {
        foreach (var ch in _subs.Values)
        {
            ch.Writer.TryWrite(evt);
        }
    }

    public async IAsyncEnumerable<PaperTradingEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<PaperTradingEvent>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subs[id] = ch;
        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }
        finally
        {
            _subs.TryRemove(id, out _);
        }
    }
}
