using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;

namespace FrostAura.Foresight.Application.Backtesting;

/// <summary>
/// Anti-lookahead microstructure provider for a single backtest/training iteration. Holds the bars
/// pre-fetched once for the whole run and, per iteration, returns only those whose CLOSE-time is at
/// or before the decision boundary (open of the forming candle). Same close-time discipline the
/// candle slice providers use, so order-flow features can never see a bar that hadn't closed when
/// the bet was decided.
/// </summary>
internal sealed class MicrostructureSlice : IHistoricalMicrostructureProvider
{
    private readonly IReadOnlyList<MicrostructureBar> _pool;
    private readonly long _intervalMs;
    private readonly long _boundaryMs;

    public MicrostructureSlice(IReadOnlyList<MicrostructureBar> pool, long intervalMs, long boundaryMs)
    {
        _pool = pool;
        _intervalMs = intervalMs;
        _boundaryMs = boundaryMs;
    }

    public Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        var clampedEnd = Math.Min(endMs, _boundaryMs);
        var res = _pool
            .Where(b => b.Symbol == symbol && b.Interval == interval
                        && b.OpenTime >= startMs && b.OpenTime + _intervalMs <= clampedEnd)
            .ToList();
        return Task.FromResult<IReadOnlyList<MicrostructureBar>>(res);
    }
}
