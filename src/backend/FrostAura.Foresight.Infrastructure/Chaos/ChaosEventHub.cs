using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrostAura.Foresight.Infrastructure.Chaos;

public enum ChaosEventKind
{
    Started,
    Progress,
    Completed,
    Failed,
}

/// <summary>
/// One SSE progress event emitted by <see cref="ChaosService"/> during a chaos run.
/// <c>BatchId</c> lets a single SSE stream fan out to a batch of many combo rows.
/// </summary>
public sealed record ChaosEvent(
    Guid BatchId,
    ChaosEventKind Kind,
    int ComboIndex,
    int TotalCombos,
    int SampleIndex,
    int TotalSamples,
    string? Error);

/// <summary>
/// In-process pub/sub for chaos run progress. Mirrors <see cref="FrostAura.Foresight.Infrastructure.Live.BacktestEventHub"/>
/// — bounded channel per subscriber so a slow client never stalls the engine.
/// </summary>
public interface IChaosEventHub
{
    void Publish(ChaosEvent evt);
    IAsyncEnumerable<ChaosEvent> Subscribe(CancellationToken ct);
}

public sealed class ChaosEventHub : IChaosEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<ChaosEvent>> _subs = new();

    public void Publish(ChaosEvent evt)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(evt);
    }

    public async IAsyncEnumerable<ChaosEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<ChaosEvent>(new BoundedChannelOptions(256)
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
