using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Walks the DAG layer-by-layer (Kahn-style), executing nodes inside a layer in parallel via
/// <c>Task.WhenAll</c>. The <see cref="FlowValidator"/> runs first so this never sees a cycle,
/// disconnected required port, or unknown TypeId.
/// </summary>
public sealed class FlowExecutor : IFlowExecutor
{
    private readonly NodeRegistry _registry;
    private readonly FlowValidator _validator;
    private readonly ILogger<FlowExecutor> _logger;

    public FlowExecutor(NodeRegistry registry, FlowValidator validator, ILogger<FlowExecutor> logger)
    {
        _registry = registry;
        _validator = validator;
        _logger = logger;
    }

    public async Task<FlowResult> ExecuteAsync(FlowDefinition flow, FlowContext ctx, CancellationToken ct)
    {
        var validation = _validator.Validate(flow);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Flow failed validation before execute: {validation.Error}");

        // Adjacency + indegree for Kahn.
        var byId = flow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var indegree = flow.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var outgoing = flow.Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        // Edges grouped by destination node so we can build that node's input bag in one pass.
        var incoming = flow.Nodes.ToDictionary(n => n.Id, _ => new List<EdgeDefinition>(), StringComparer.Ordinal);
        foreach (var e in flow.Edges)
        {
            var fromId = e.From.SplitEndpoint().NodeId;
            var toId   = e.To.SplitEndpoint().NodeId;
            outgoing[fromId].Add(toId);
            incoming[toId].Add(e);
            indegree[toId]++;
        }

        // Per-(nodeId, port) output bag built up as we execute layers.
        var outputs = new Dictionary<(string nodeId, string port), object?>();

        // Initial layer: every node with indegree 0.
        var currentLayer = indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        var executed = new HashSet<string>(StringComparer.Ordinal);

        while (currentLayer.Count > 0)
        {
            var batch = await Task.WhenAll(currentLayer.Select(async nodeId =>
            {
                var def = byId[nodeId];
                var node = _registry.Get(def.Type);
                var inputs = BuildInputBag(incoming[nodeId], outputs);
                try
                {
                    var portOutputs = await node.ExecuteAsync(inputs, def.Params, ctx, ct);
                    return (nodeId, portOutputs);
                }
                catch (Exception ex) when (ex is not FlowExecutionException && ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Flow node {NodeId} ({TypeId}) threw", nodeId, def.Type);
                    throw new FlowExecutionException(nodeId, ex);
                }
            }));

            foreach (var (nodeId, portOutputs) in batch)
            {
                foreach (var kv in portOutputs)
                    outputs[(nodeId, kv.Key)] = kv.Value;
                executed.Add(nodeId);
            }

            // Compute next layer.
            var nextLayer = new List<string>();
            foreach (var nodeId in currentLayer)
            {
                foreach (var next in outgoing[nodeId])
                {
                    if (--indegree[next] == 0) nextLayer.Add(next);
                }
            }
            currentLayer = nextLayer;
        }

        if (executed.Count != flow.Nodes.Count)
            throw new InvalidOperationException($"Executor walked {executed.Count} of {flow.Nodes.Count} nodes — DAG topology mismatch (should have been caught by validator).");

        // Pull the terminal output node's outputs out as the headline result.
        // Support both "output.prediction" (model flows) and "output.stake" (strategy flows).
        var outputNode = flow.Nodes.Single(n => n.Type is "output.prediction" or "output.stake");
        var outputPrediction = outputs
            .Where(kv => kv.Key.nodeId == outputNode.Id)
            .ToDictionary(kv => kv.Key.port, kv => kv.Value, StringComparer.Ordinal);

        // Flatten outputs into a nodeId -> { port -> value } map for the trace return.
        var nodeOutputs = outputs
            .GroupBy(kv => kv.Key.nodeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, object?>)g.ToDictionary(kv => kv.Key.port, kv => kv.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new FlowResult(outputPrediction, nodeOutputs);
    }

    private static IReadOnlyDictionary<string, object?> BuildInputBag(
        List<EdgeDefinition> edges,
        Dictionary<(string nodeId, string port), object?> outputs)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            var (fromNode, fromPort) = e.From.SplitEndpoint();
            var (_,        toPort)   = e.To.SplitEndpoint();
            bag[toPort] = outputs.TryGetValue((fromNode, fromPort), out var val) ? val : null;
        }
        return bag;
    }
}
