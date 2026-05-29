using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrostAura.Foresight.Application.Flow;

/// <summary>
/// Wire-format flow DAG. Persisted as jsonb on <c>models.Definition</c> and round-tripped between
/// frontend designer and backend executor. <see cref="SchemaVersion"/> guards forward-compat —
/// bump it whenever the shape of nodes / edges changes incompatibly.
/// </summary>
public sealed record FlowDefinition
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("modelKind")]     public string ModelKind { get; init; } = "deterministic";
    [JsonPropertyName("supportsBacktesting")] public bool SupportsBacktesting { get; init; }
    /// <summary>The maximum lookback any node in the flow needs. The backtester extends its fetch
    /// window left by this many candles so indicator warmup is satisfied before the first
    /// predicted candle.</summary>
    [JsonPropertyName("warmupCandles")] public int WarmupCandles { get; init; } = 60;
    [JsonPropertyName("nodes")]         public required IReadOnlyList<NodeDefinition> Nodes { get; init; }
    [JsonPropertyName("edges")]         public required IReadOnlyList<EdgeDefinition> Edges { get; init; }
}

public sealed record NodeDefinition
{
    [JsonPropertyName("id")]       public required string Id { get; init; }
    [JsonPropertyName("type")]     public required string Type { get; init; }
    [JsonPropertyName("params")]   public JsonElement Params { get; init; }
    [JsonPropertyName("position")] public NodePosition? Position { get; init; }
}

public sealed record NodePosition([property: JsonPropertyName("x")] double X,
                                  [property: JsonPropertyName("y")] double Y);

/// <summary>
/// One directed edge in the DAG. <see cref="From"/> and <see cref="To"/> are <c>"nodeId.portName"</c>
/// strings — the validator splits on the dot and confirms both ports exist with compatible
/// type tags.
/// </summary>
public sealed record EdgeDefinition
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")]   public required string To   { get; init; }
}

public static class EdgeEndpointExtensions
{
    public static (string NodeId, string Port) SplitEndpoint(this string endpoint)
    {
        var dot = endpoint.IndexOf('.');
        if (dot <= 0 || dot == endpoint.Length - 1)
            throw new ArgumentException($"Invalid endpoint '{endpoint}'. Expected 'nodeId.port'.");
        return (endpoint[..dot], endpoint[(dot + 1)..]);
    }
}
