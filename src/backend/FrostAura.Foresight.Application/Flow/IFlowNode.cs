using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// The contract for every node type in the flow system. One concrete <see cref="IFlowNode"/>
/// implementation per <see cref="TypeId"/>; the <see cref="NodeRegistry"/> collects them via DI
/// and resolves a node-definition row to its implementation by string id.
///
/// Nodes are stateless and re-entrant — the executor calls <see cref="ExecuteAsync"/> potentially
/// many times per second across concurrent flows. Any per-instance state belongs in the trained
/// coefficients blob carried on <see cref="FlowContext.TrainedState"/>, not on the node.
/// </summary>
public interface IFlowNode
{
    /// <summary>Stable string id used in flow JSON (e.g. "indicator.tech_pack").</summary>
    string TypeId { get; }

    /// <summary>Static port + param description.</summary>
    NodePortSpec Spec { get; }

    /// <summary>
    /// Compute the node's outputs from its inputs and params. Returns a dictionary keyed by
    /// output port name. Soft-fail (e.g. an external HTTP source being down) is internal — return
    /// <c>null</c> on the port and downstream nodes are expected to tolerate that.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        JsonElement nodeParams,
        FlowContext ctx,
        CancellationToken ct);
}
