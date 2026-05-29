using System.Text.Json;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Collapses a bag of named scalar inputs into a one-row feature matrix. The <c>columns</c> param
/// is an ordered list of input port names; the matrix carries that order so downstream models can
/// surface coefficient names that match.
///
/// Null scalars (warmup misses) collapse the row to nulls — the regression node skips fits or
/// inferences against rows containing any null, treating them as "model not ready for this candle".
/// </summary>
public sealed class MatrixBuilderNode : IFlowNode
{
    public string TypeId => "feature.matrix_builder";

    public NodePortSpec Spec { get; } = new(
        Category: "feature",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[]
        {
            new PortDef("matrix", "Matrix"),
            new PortDef("ready", "bool", Description: "False when any required column was null this candle — downstream models fast-skip the warmup window."),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["columns"] = new("string[]", true, null,
                "Ordered list of input port names to assemble into the feature row."),
        },
        AcceptsAdditionalInputs: true);

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var columns = NodeParams.Get<List<string>>(nodeParams, "columns") ?? new List<string>();
        var row = new double[columns.Count];
        var hasAllValues = true;
        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i];
            var val = inputs.GetValueOrDefault(name);
            if (val is null) { hasAllValues = false; row[i] = 0; continue; }
            row[i] = val switch
            {
                decimal d => (double)d,
                double dd => dd,
                int n     => n,
                long l    => l,
                _ => double.NaN,
            };
        }
        var mat = new double[1, columns.Count];
        for (var i = 0; i < columns.Count; i++) mat[0, i] = row[i];
        var matrix = new FeatureMatrix(columns, mat);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["matrix"] = matrix,
            // Sidecar boolean so downstream models can fast-skip warmup misses without sniffing for NaNs.
            ["ready"] = hasAllValues,
        });
    }
}
