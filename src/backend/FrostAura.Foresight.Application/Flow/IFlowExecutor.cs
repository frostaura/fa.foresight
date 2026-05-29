namespace FrostAura.Foresight.Application.Flow;

public interface IFlowExecutor
{
    Task<FlowResult> ExecuteAsync(FlowDefinition flow, FlowContext ctx, CancellationToken ct);
}

/// <summary>
/// The outputs of the terminal <c>output.prediction</c> node, plus a structured trace of every
/// node's outputs keyed by <c>(nodeId, port)</c>. The trace is used for audit logs and the
/// per-prediction debug view in the UI.
/// </summary>
public sealed record FlowResult(
    IReadOnlyDictionary<string, object?> OutputPrediction,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> NodeOutputs);

public sealed class FlowExecutionException : Exception
{
    public string NodeId { get; }
    public FlowExecutionException(string nodeId, Exception inner)
        : base($"Flow node '{nodeId}' failed: {inner.Message}", inner) => NodeId = nodeId;
}
