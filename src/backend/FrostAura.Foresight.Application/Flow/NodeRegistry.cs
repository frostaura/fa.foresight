using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Resolves a node TypeId string to the concrete <see cref="IFlowNode"/> implementation. Singleton:
/// DI hands an <c>IEnumerable&lt;IFlowNode&gt;</c> in, the registry collapses it by TypeId.
///
/// Also exposes <see cref="GetCatalogueDescriptor"/> — the single source-of-truth catalogue used by
/// the flow validator, the frontend designer (auto-generated params form), and the AI assistant
/// (system-prompt JSON). Any new node type only has to register in DI; all three surfaces pick it up.
/// </summary>
public sealed class NodeRegistry
{
    private readonly IReadOnlyDictionary<string, IFlowNode> _byType;

    public NodeRegistry(IEnumerable<IFlowNode> nodes)
    {
        var map = new Dictionary<string, IFlowNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (!map.TryAdd(n.TypeId, n))
                throw new InvalidOperationException($"Duplicate flow-node TypeId registered: {n.TypeId}");
        }
        _byType = map;
    }

    public IFlowNode Get(string typeId) =>
        _byType.TryGetValue(typeId, out var node)
            ? node
            : throw new InvalidOperationException($"Unknown flow-node TypeId: {typeId}");

    public bool TryGet(string typeId, out IFlowNode? node)
    {
        if (_byType.TryGetValue(typeId, out var n)) { node = n; return true; }
        node = null;
        return false;
    }

    public IReadOnlyCollection<string> TypeIds => (IReadOnlyCollection<string>)_byType.Keys;

    /// <summary>
    /// Render the entire node catalogue as a JSON descriptor suitable for embedding in the AI
    /// assistant's system prompt or returning to the frontend designer to drive the node palette
    /// and params form. Shape:
    /// <code>
    /// {
    ///   "n.tech_pack": {
    ///     "category": "indicator",
    ///     "inputs":  [{"name":"candles","typeTag":"Candle[]","required":true}],
    ///     "outputs": [{"name":"sma10","typeTag":"decimal"}, …],
    ///     "params":  {"periods":{"typeTag":"int[]","required":false,"default":[10,14,20]}},
    ///     "acceptsAdditionalInputs": false,
    ///     "requiresLiveData": false
    ///   }, …
    /// }
    /// </code>
    /// </summary>
    public string GetCatalogueDescriptor()
    {
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (typeId, node) in _byType)
        {
            var spec = node.Spec;
            doc[typeId] = new
            {
                category = spec.Category,
                inputs   = spec.Inputs,
                outputs  = spec.Outputs,
                @params  = spec.Params,
                acceptsAdditionalInputs = spec.AcceptsAdditionalInputs,
                requiresLiveData = spec.RequiresLiveData,
            };
        }
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        });
    }
}
