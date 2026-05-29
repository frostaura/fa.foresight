using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FrostAura.Foresight.Infrastructure.Live;

public enum BacktestEventKind
{
    Started,
    Progress,
    Completed,
    Failed,
}

/// <summary>One progress event emitted during a backtest run. <c>CandlesProcessed/TotalCandles</c>
/// form the progress fraction (candles, not bets — a gated model bets rarely so a bets-based
/// fraction never fills); <c>BetsPlaced</c> is informational; <c>FinalBalance</c> is set only on
/// Completed; <c>Error</c> on Failed.</summary>
public sealed record BacktestEvent(
    Guid BacktestId,
    BacktestEventKind Kind,
    int CandlesProcessed,
    int TotalCandles,
    int BetsPlaced,
    int BetsWon,
    decimal? CurrentBalance,
    decimal? FinalBalance,
    string? Error);

/// <summary>
/// In-process pub/sub for backtest progress. <see cref="BacktestRunner"/> publishes Started + N
/// Progress + Completed/Failed; the SSE endpoint subscribes per browser and forwards events for
/// the specific backtest id the UI is watching. Mirrors <see cref="LivePredictionEventHub"/>'s
/// pattern — bounded channel per subscriber so a slow client never stalls the runner.
/// </summary>
public interface IBacktestEventHub
{
    void Publish(BacktestEvent evt);
    IAsyncEnumerable<BacktestEvent> Subscribe(CancellationToken ct);
}

public sealed class BacktestEventHub : IBacktestEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<BacktestEvent>> _subs = new();

    public void Publish(BacktestEvent evt)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(evt);
    }

    public async IAsyncEnumerable<BacktestEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<BacktestEvent>(new BoundedChannelOptions(256)
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
