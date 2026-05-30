using System.Text.Json;
using FrostAura.Foresight.Domain.Ports;

namespace FrostAura.Foresight.Application.Flow;

public enum FlowMode { Live, Backtest }

/// <summary>
/// Runtime context passed to every node during a single flow execution. Carries the (symbol,
/// interval, target candle) the flow is predicting against, the mode (live vs backtest), the
/// historical-candle provider for source nodes, and the trained-coefficients blob for stateful
/// model nodes.
/// </summary>
public sealed record FlowContext(
    Guid TenantId,
    Guid ModelId,
    string Symbol,
    string Interval,
    long TargetOpenTime,
    int Horizon,
    FlowMode Mode,
    IHistoricalCandleProvider HistoricalCandles,
    JsonElement? TrainedState,
    // Optional order-flow microstructure source. Null for flows / runs that don't use it (the
    // microstructure source node then emits nulls and the matrix marks itself not-ready). In
    // backtest/training this is a boundary-clamped slice so bars can't leak across the decision edge;
    // in live it's the Postgres-backed adapter.
    IHistoricalMicrostructureProvider? Microstructure = null,
    // Optional ambient inputs for strategy DAGs. When present, the executor falls back to these
    // values for any declared input port that is NOT already satisfied by an incoming edge.
    // Model flows leave this null; StrategyEvaluator populates it from the StrategyStep before
    // calling ExecuteAsync so strategy primitive nodes can read pUp / balance / etc. by port name
    // without requiring upstream edges. Edge-wired values always take precedence.
    IReadOnlyDictionary<string, object?>? AmbientInputs = null);
