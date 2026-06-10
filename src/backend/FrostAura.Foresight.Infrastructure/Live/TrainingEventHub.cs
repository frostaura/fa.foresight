using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrostAura.Foresight.Infrastructure.Live;

public enum TrainingEventKind
{
    Started,
    Progress,
    Completed,
    Failed,
}

/// <summary>One progress event emitted during a model training run. A training run fits one variant
/// per supported interval IN PARALLEL, so <c>Interval</c> identifies which variant this event
/// belongs to and the UI aggregates across them. <c>Phase</c> is the work stage
/// (<c>hydrating</c>/<c>building-features</c>/<c>validating</c>/<c>fitting</c>);
/// <c>Processed/Total</c> is the within-phase fraction; <c>Error</c> is set only on Failed.</summary>
public sealed record TrainingEvent(
    Guid ModelId,
    TrainingEventKind Kind,
    string Interval,
    string Phase,
    int Processed,
    int Total,
    string? Error);

/// <summary>
/// In-process pub/sub for training progress. <see cref="FrostAura.Foresight.Application.Models.ModelTrainer"/>
/// reports phase ticks through an <c>IProgress</c> that the training service adapts into Started + N
/// Progress + Completed/Failed events here; the SSE endpoint subscribes per browser and forwards the
/// events for the specific model id the UI is watching. Mirrors <see cref="BacktestEventHub"/>'s
/// pattern — bounded channel per subscriber so a slow client never stalls the trainer.
/// </summary>
public interface ITrainingEventHub
{
    void Publish(TrainingEvent evt);
    IAsyncEnumerable<TrainingEvent> Subscribe(CancellationToken ct);
}

public sealed class TrainingEventHub : ITrainingEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<TrainingEvent>> _subs = new();

    public void Publish(TrainingEvent evt)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(evt);
    }

    public async IAsyncEnumerable<TrainingEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<TrainingEvent>(new BoundedChannelOptions(256)
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
