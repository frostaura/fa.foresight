using System.Collections.Concurrent;
using System.Threading.Channels;
using FrostAura.Foresight.Domain.Live;

namespace FrostAura.Foresight.Infrastructure.Live;

public enum LivePredictionEventKind
{
    Created,
    Resolved
}

public sealed record LivePredictionEvent(LivePredictionEventKind Kind, LivePrediction Prediction);

/// <summary>
/// In-process fan-out hub for live-prediction events. PredictionService publishes here whenever it
/// inserts or resolves a row; the SSE endpoint subscribes once per browser tab and receives every
/// event for the tenant — the page then filters client-side per (symbol, interval). One stream per
/// tab keeps us under the HTTP/1.1 six-connections-per-origin limit no matter how many cards mount.
/// Singleton — outlives request scope. Bounded channels per subscriber drop on overflow rather
/// than block the publisher, so a slow client never stalls the prediction pipeline.
/// </summary>
public interface ILivePredictionEventHub
{
    void Publish(LivePredictionEvent evt);
    IAsyncEnumerable<LivePredictionEvent> Subscribe(CancellationToken ct);
}

public sealed class LivePredictionEventHub : ILivePredictionEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<LivePredictionEvent>> _subs = new();

    public void Publish(LivePredictionEvent evt)
    {
        foreach (var ch in _subs.Values)
        {
            // BoundedChannelFullMode.DropOldest — guarantees TryWrite never blocks and the freshest
            // event always lands. Reads after a drop are still consistent (server-of-record is the
            // DB; SSE is a delta stream, not authoritative state).
            ch.Writer.TryWrite(evt);
        }
    }

    public async IAsyncEnumerable<LivePredictionEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<LivePredictionEvent>(new BoundedChannelOptions(128)
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
