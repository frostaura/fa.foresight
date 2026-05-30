using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrostAura.Foresight.Infrastructure.Live;

public enum ModelEventKind
{
    /// <summary>A background fit has just started — the model's TrainingStatus flipped to "training".</summary>
    Training,
    /// <summary>The fit finished successfully — TrainingStatus cleared and TrainedState is populated.</summary>
    Trained,
    /// <summary>The fit failed — TrainingStatus is "failed" with an error message.</summary>
    Failed,
}

/// <summary>
/// One model-lifecycle event emitted as a model's training status transitions. The UI subscribes to
/// the SSE stream and invalidates its model cache on each event — replacing the old fixed-interval
/// poll of <c>/api/models</c> that ran while any model was training.
/// </summary>
public sealed record ModelEvent(Guid TenantId, Guid ModelId, ModelEventKind Kind, string? Error);

/// <summary>
/// In-process fan-out hub for model-lifecycle events. <see cref="ModelTrainingService"/> publishes
/// here when a fit starts, completes, or fails; the SSE endpoint subscribes once per browser tab and
/// pushes the deltas so training-status changes reach the UI without polling. Mirrors
/// <see cref="LivePredictionEventHub"/> — bounded channel per subscriber, DropOldest on overflow so a
/// slow client never stalls the trainer. Singleton — outlives request scope.
/// </summary>
public interface IModelEventHub
{
    void Publish(ModelEvent evt);
    IAsyncEnumerable<ModelEvent> Subscribe(CancellationToken ct);
}

public sealed class ModelEventHub : IModelEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<ModelEvent>> _subs = new();

    public void Publish(ModelEvent evt)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(evt);
    }

    public async IAsyncEnumerable<ModelEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<ModelEvent>(new BoundedChannelOptions(128)
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
