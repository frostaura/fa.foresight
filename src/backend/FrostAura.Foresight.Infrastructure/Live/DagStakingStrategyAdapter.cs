using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Adapts a custom DAG strategy (evaluated via <see cref="IStrategyEvaluator"/>) behind the
/// <see cref="IStakingStrategy"/> interface so it can be passed into existing sync-aware callers
/// (e.g. <see cref="FrostAura.Foresight.Application.Backtesting.BacktestRunner"/> which calls
/// <c>strategy.NextBetSize</c> once per candle inside an already-async method).
///
/// DESIGN NOTE: The <see cref="NextBetSize"/> call blocks on the evaluator's <c>ValueTask</c>.
/// This is intentional and safe here because:
/// 1. DAG strategy nodes are pure CPU-bound math (no network, no I/O) so the task completes
///    synchronously in almost all cases.
/// 2. The caller (<see cref="FrostAura.Foresight.Application.Backtesting.BacktestRunner.RunAsync"/>
///    and <see cref="PaperTradingService.ProcessAsync"/>) runs on a thread-pool thread (inside
///    <c>Task.Run</c> or an async chain without a SynchronizationContext) so there is no deadlock risk.
///
/// If a future DAG strategy node ever introduces real async I/O, callers should be updated to use
/// <see cref="IStrategyEvaluator.NextStakeAsync"/> directly rather than this adapter.
/// </summary>
public sealed class DagStakingStrategyAdapter : IStakingStrategy
{
    private readonly string _strategyId;
    private readonly IStrategyEvaluator _evaluator;
    private readonly FlowContext _baseCtx;

    public string Id => _strategyId;
    public string Name => $"DAG({_strategyId})";
    public string Description => "Custom DAG strategy evaluated via the flow executor.";
    public bool RequiresEdgeInputs => true;

    /// <summary>
    /// Constructs the adapter. The <paramref name="baseCtx"/> is used as the execution context
    /// for the strategy DAG; it is shared across all calls within one backtest/paper-tick scope.
    /// </summary>
    public DagStakingStrategyAdapter(string strategyId, IStrategyEvaluator evaluator, FlowContext baseCtx)
    {
        _strategyId = strategyId;
        _evaluator = evaluator;
        _baseCtx = baseCtx;
    }

    public decimal NextBetSize(StrategyStep step)
    {
        // Synchronously wait on the async evaluator. Safe because DAG strategy nodes are pure.
        return _evaluator.NextStakeAsync(_strategyId, step, _baseCtx, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Creates a minimal no-op FlowContext suitable for strategy DAG evaluation. Strategy DAG
    /// flows do not use historical candles or trained state — only the step inputs fed by the
    /// DAG edges matter.
    /// </summary>
    public static FlowContext MakeStrategyFlowContext(Guid tenantId, Guid modelId, string symbol, string interval)
        => new FlowContext(
            TenantId: tenantId,
            ModelId: modelId,
            Symbol: symbol,
            Interval: interval,
            TargetOpenTime: 0L,
            Horizon: 1,
            Mode: FlowMode.Backtest,
            HistoricalCandles: NullCandleProvider.Instance,
            TrainedState: null,
            Microstructure: null);
}

/// <summary>Minimal IHistoricalCandleProvider that always returns an empty list.</summary>
internal sealed class NullCandleProvider : IHistoricalCandleProvider
{
    public static readonly NullCandleProvider Instance = new();
    public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
}
