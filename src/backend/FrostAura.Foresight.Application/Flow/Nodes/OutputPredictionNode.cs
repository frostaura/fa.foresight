using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Terminal node — exactly one per flow. Surfaces the upstream model's <c>pUp</c> and
/// <c>confidence</c> as the headline result the executor returns to its caller. Optional ports
/// <c>p05</c>/<c>p50</c>/<c>p95</c> carry quantile-aware LLM output when present (legacy passthrough
/// path), otherwise they remain null and the persisted row uses anchor-derived defaults.
/// </summary>
public sealed class OutputPredictionNode : IFlowNode
{
    public string TypeId => "output.prediction";

    public NodePortSpec Spec { get; } = new(
        Category: "output",
        Inputs: new[]
        {
            new PortDef("pUp",        "decimal?"),
            new PortDef("confidence", "decimal", Required: false),
            new PortDef("predicted",  "decimal?", Required: false),
            new PortDef("p05",        "decimal?", Required: false),
            new PortDef("p50",        "decimal?", Required: false),
            new PortDef("p95",        "decimal?", Required: false),
            new PortDef("reasoning",  "string",   Required: false),
        },
        // No declared output ports — this is a terminal node. The executor still surfaces the
        // node's internal "outputs" dict as the FlowResult.OutputPrediction, so the persistence
        // layer reads what it needs without rendering dangling port handles on the canvas.
        Outputs: Array.Empty<PortDef>(),
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        // Pass-through: the executor's caller will read these off FlowResult.OutputPrediction and
        // map them into the persisted LivePrediction row.
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["pUp"]        = inputs.GetValueOrDefault("pUp"),
            ["confidence"] = inputs.GetValueOrDefault("confidence") ?? 0.5m,
            ["predicted"]  = inputs.GetValueOrDefault("predicted"),
            ["p05"]        = inputs.GetValueOrDefault("p05"),
            ["p50"]        = inputs.GetValueOrDefault("p50"),
            ["p95"]        = inputs.GetValueOrDefault("p95"),
            ["reasoning"]  = inputs.GetValueOrDefault("reasoning"),
        });
    }
}
