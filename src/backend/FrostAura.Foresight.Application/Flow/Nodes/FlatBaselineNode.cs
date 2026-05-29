using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Null-hypothesis baseline. Emits pUp = 0.50 and confidence = 0.50 every call regardless of inputs.
/// Exists so real models have a "no edge" control to be measured against — any model that does not
/// beat this on calibration / hit-rate / backtest P&L has no demonstrable signal.
/// </summary>
public sealed class FlatBaselineNode : IFlowNode
{
    public string TypeId => "model.flat_baseline";

    public NodePortSpec Spec { get; } = new(
        Category: "model",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[]
        {
            new PortDef("pUp",        "decimal?"),
            new PortDef("confidence", "decimal"),
            new PortDef("predicted",  "decimal?"),
        },
        Params: new Dictionary<string, ParamDef>());

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["pUp"]        = 0.50m,
            ["confidence"] = 0.50m,
            ["predicted"]  = (decimal?)null,
        });
    }
}
