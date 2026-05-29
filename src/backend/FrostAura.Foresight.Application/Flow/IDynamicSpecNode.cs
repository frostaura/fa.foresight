namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Optional extension for <see cref="IFlowNode"/> implementations whose port spec is determined at
/// design-time by per-instance params (e.g. <c>code.python</c> where inputs/outputs are declared in
/// the node's params JSON rather than being statically known).
///
/// When a node implements this interface, <see cref="NodeRegistry"/> and <see cref="FlowValidator"/>
/// call <see cref="ResolveSpec"/> with the current node-definition params instead of using the
/// static <see cref="IFlowNode.Spec"/>. All existing static nodes are unaffected (they don't
/// implement this interface).
/// </summary>
public interface IDynamicSpecNode
{
    /// <summary>
    /// Derive the effective <see cref="NodePortSpec"/> for this specific node instance given its
    /// design-time <paramref name="nodeParams"/>. Called once per validator pass — not hot-path.
    /// </summary>
    NodePortSpec ResolveSpec(IReadOnlyDictionary<string, object?> nodeParams);
}
