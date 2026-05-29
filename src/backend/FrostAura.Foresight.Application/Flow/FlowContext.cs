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
    IHistoricalMicrostructureProvider? Microstructure = null);
