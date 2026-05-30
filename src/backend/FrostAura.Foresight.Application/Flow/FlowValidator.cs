using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Static structural validation of a <see cref="FlowDefinition"/>. Runs on save (block invalid
/// flows from persisting) and on execute (defence in depth — a flow may have been migrated from
/// an earlier schema or assembled by the AI assistant). Returns the first encountered error rather
/// than an aggregate so the failing message is precise enough for the user / LLM to act on.
///
/// Terminal-node requirement is parameterised by <see cref="FlowDefinition.DefinitionKind"/>:
///   "model"    → exactly one <c>output.prediction</c>
///   "strategy" → exactly one <c>output.stake</c>
/// Model validation is unchanged — the default DefinitionKind of "model" keeps every existing flow
/// valid.
///
/// Nodes implementing <see cref="IDynamicSpecNode"/> have their <see cref="NodePortSpec"/> derived
/// from per-instance params at validation time rather than using the static Spec property. All
/// existing static nodes are unaffected.
/// </summary>
public sealed class FlowValidator
{
    private readonly NodeRegistry _registry;

    /// <summary>
    /// The set of input-port names that are runtime-injected by the strategy evaluator as ambient
    /// context (from <see cref="FrostAura.Foresight.Domain.Trading.StrategyStep"/> and the current
    /// market prices). For strategy flows (<c>definitionKind == "strategy"</c>), ports in this set
    /// are treated as satisfied by the required-input check even without an incoming edge, because
    /// they are fulfilled at runtime via <see cref="FlowContext.AmbientInputs"/>. This set is the
    /// single source of truth shared with <c>StrategyEvaluator.BuildAmbientInputs</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> StrategyContextPorts = new HashSet<string>(StringComparer.Ordinal)
    {
        "pUp", "yesPrice", "noPrice", "balance", "currentBet", "initialBet", "lastOutcome",
    };

    public FlowValidator(NodeRegistry registry) => _registry = registry;

    public ValidationResult Validate(FlowDefinition flow)
    {
        // 1. Node ids unique, types known
        var byId = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes)
        {
            if (!byId.TryAdd(n.Id, n)) return Err($"Duplicate node id '{n.Id}'.");
            if (!_registry.TryGet(n.Type, out _)) return Err($"Unknown node type '{n.Type}' on node '{n.Id}'.");
        }

        // 2. Exactly one terminal output node — kind determined by DefinitionKind.
        var terminalType = ResolveTerminalType(flow.DefinitionKind);
        var outputs = flow.Nodes.Where(n => n.Type == terminalType).ToList();
        if (outputs.Count == 0) return Err($"Flow must contain exactly one '{terminalType}' node.");
        if (outputs.Count > 1) return Err($"Flow must contain exactly one '{terminalType}' node; found {outputs.Count}.");

        // 3. Edges resolve to known ports with compatible type tags.
        // For nodes implementing IDynamicSpecNode, derive spec from the instance params.
        var nodesByType = new Dictionary<string, NodePortSpec>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes)
        {
            var node = _registry.Get(n.Type);
            nodesByType[n.Id] = ResolveNodeSpec(node, n);
        }

        foreach (var e in flow.Edges)
        {
            var (fromNode, fromPort) = e.From.SplitEndpoint();
            var (toNode, toPort) = e.To.SplitEndpoint();
            if (!nodesByType.TryGetValue(fromNode, out var fromSpec)) return Err($"Edge '{e.From}->{e.To}' references missing node '{fromNode}'.");
            if (!nodesByType.TryGetValue(toNode, out var toSpec)) return Err($"Edge '{e.From}->{e.To}' references missing node '{toNode}'.");

            var output = fromSpec.Outputs.FirstOrDefault(p => p.Name == fromPort);
            if (output is null) return Err($"Edge '{e.From}->{e.To}' references missing output port '{fromPort}' on '{fromNode}'.");

            var input = toSpec.Inputs.FirstOrDefault(p => p.Name == toPort);
            if (input is null && !toSpec.AcceptsAdditionalInputs)
                return Err($"Edge '{e.From}->{e.To}' references missing input port '{toPort}' on '{toNode}'.");

            // Type-tag compat: identity, or upstream is a more-specific subtype encoded by suffix.
            var requiredTag = input?.TypeTag ?? output.TypeTag; // varargs match upstream tag.
            if (!TypeTagsCompatible(output.TypeTag, requiredTag))
                return Err($"Edge '{e.From}->{e.To}' type mismatch: '{output.TypeTag}' -> '{requiredTag}'.");
        }

        // 4. Required input ports satisfied
        // For strategy flows, ports in the StrategyContextPorts set are fulfilled at runtime via
        // FlowContext.AmbientInputs — they do not need an incoming edge and are skipped here.
        var isStrategyFlow = string.Equals(flow.DefinitionKind, "strategy", StringComparison.Ordinal);
        var incomingByNodePort = flow.Edges
            .Select(e => e.To.SplitEndpoint())
            .ToHashSet();
        foreach (var n in flow.Nodes)
        {
            var spec = nodesByType[n.Id];
            foreach (var p in spec.Inputs.Where(p => p.Required))
            {
                // Strategy context ports are runtime-injected — skip the edge check for them.
                if (isStrategyFlow && StrategyContextPorts.Contains(p.Name)) continue;

                if (!incomingByNodePort.Contains((n.Id, p.Name)))
                    return Err($"Required input port '{p.Name}' on node '{n.Id}' is not connected.");
            }
        }

        // 5. DAG check via Kahn topological sort
        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes) indegree[n.Id] = 0;
        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes) outgoing[n.Id] = new();
        foreach (var e in flow.Edges)
        {
            var fromId = e.From.SplitEndpoint().NodeId;
            var toId = e.To.SplitEndpoint().NodeId;
            outgoing[fromId].Add(toId);
            indegree[toId]++;
        }
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            visited++;
            foreach (var next in outgoing[id])
            {
                if (--indegree[next] == 0) queue.Enqueue(next);
            }
        }
        if (visited != flow.Nodes.Count) return Err("Flow contains a cycle.");

        // 6. Backtest-source restriction
        if (flow.SupportsBacktesting)
        {
            foreach (var n in flow.Nodes)
            {
                if (nodesByType[n.Id].RequiresLiveData)
                    return Err($"Node type '{n.Type}' requires live data and cannot appear in a backtestable flow.");
            }
        }

        return ValidationResult.Ok();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string ResolveTerminalType(string definitionKind) => definitionKind switch
    {
        "strategy" => "output.stake",
        _ => "output.prediction",  // default: "model" and legacy null/empty
    };

    /// <summary>
    /// Returns the effective <see cref="NodePortSpec"/> for a node-definition. For nodes that
    /// implement <see cref="IDynamicSpecNode"/>, the spec is derived from the per-instance params
    /// by materialising the JsonElement into a plain string→object? dict; otherwise the static
    /// <see cref="IFlowNode.Spec"/> is returned unchanged.
    /// </summary>
    private static NodePortSpec ResolveNodeSpec(IFlowNode node, NodeDefinition def)
    {
        if (node is not IDynamicSpecNode dynamic) return node.Spec;

        // Materialise the JsonElement params dict into a plain Dictionary<string,object?> so
        // the IDynamicSpecNode implementation can read values without depending on System.Text.Json.
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (def.Params.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in def.Params.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    // Objects/arrays fall back to the raw JSON string — dynamic nodes parse them if needed.
                    _ => prop.Value.GetRawText(),
                };
            }
        }
        return dynamic.ResolveSpec(dict);
    }

    private static bool TypeTagsCompatible(string upstream, string downstream)
    {
        if (string.Equals(upstream, downstream, StringComparison.Ordinal)) return true;
        // "any" target accepts everything — used by feature.matrix_builder's column ports.
        if (downstream == "any") return true;
        return false;
    }

    private static ValidationResult Err(string msg) => new(false, msg);
}

public sealed record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);
}
