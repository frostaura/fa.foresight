namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Static structural validation of a <see cref="FlowDefinition"/>. Runs on save (block invalid
/// flows from persisting) and on execute (defence in depth — a flow may have been migrated from
/// an earlier schema or assembled by the AI assistant). Returns the first encountered error rather
/// than an aggregate so the failing message is precise enough for the user / LLM to act on.
/// </summary>
public sealed class FlowValidator
{
    private readonly NodeRegistry _registry;

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

        // 2. Exactly one terminal output node
        var outputs = flow.Nodes.Where(n => n.Type == "output.prediction").ToList();
        if (outputs.Count == 0) return Err("Flow must contain exactly one 'output.prediction' node.");
        if (outputs.Count > 1)  return Err($"Flow must contain exactly one 'output.prediction' node; found {outputs.Count}.");

        // 3. Edges resolve to known ports with compatible type tags
        var nodesByType = new Dictionary<string, NodePortSpec>(StringComparer.Ordinal);
        foreach (var n in flow.Nodes) nodesByType[n.Id] = _registry.Get(n.Type).Spec;

        foreach (var e in flow.Edges)
        {
            var (fromNode, fromPort) = e.From.SplitEndpoint();
            var (toNode,   toPort)   = e.To.SplitEndpoint();
            if (!nodesByType.TryGetValue(fromNode, out var fromSpec)) return Err($"Edge '{e.From}->{e.To}' references missing node '{fromNode}'.");
            if (!nodesByType.TryGetValue(toNode,   out var toSpec))   return Err($"Edge '{e.From}->{e.To}' references missing node '{toNode}'.");

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
        var incomingByNodePort = flow.Edges
            .Select(e => e.To.SplitEndpoint())
            .ToHashSet();
        foreach (var n in flow.Nodes)
        {
            var spec = nodesByType[n.Id];
            foreach (var p in spec.Inputs.Where(p => p.Required))
            {
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
            var toId   = e.To.SplitEndpoint().NodeId;
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
