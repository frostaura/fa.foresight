using System.Text.Json;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Code node: executes user-supplied Python via the sandbox sidecar. Ports are DYNAMIC — declared
/// in the node's <c>params.ports</c> JSON:
/// <code>
/// { "ports": { "inputs": { "candles": "Candle[]", "pUp": "decimal" },
///              "outputs": { "signal": "decimal" } },
///   "code": "def run(inputs, params):\n    return {'signal': inputs['pUp']}",
///   "seed": 42 }
/// </code>
/// The <c>code</c>, the port schema, and <c>seed</c> all live in params so the .NET node wrapper
/// holds no user state. Implements <see cref="IDynamicSpecNode"/> so the validator derives ports
/// from the instance params rather than the static Spec placeholder.
///
/// Marshalling:
///   Candle[]     → tag="candles", columnar arrays (openTime/open/high/low/close/volume).
///   FeatureMatrix → tag="matrix", {columns, rows[][]} (jagged double[][]).
///   decimal/double/float/int → tag="scalar".
///   double[]     → tag="series".
///   null         → omitted from inputs (port not connected/warmup skip).
/// Float quantisation: all double outputs are rounded to 1e-9 so backtest and live agree.
/// </summary>
public sealed class CodePythonNode : IFlowNode, IDynamicSpecNode
{
    public string TypeId => "code.python";

    private readonly ISandboxExecutor _sandbox;

    public CodePythonNode(ISandboxExecutor sandbox)
    {
        _sandbox = sandbox;
    }

    /// <summary>
    /// Static placeholder spec — the real spec is dynamic (see <see cref="ResolveSpec"/>). This
    /// baseline satisfies the <see cref="IFlowNode.Spec"/> contract; the validator and registry
    /// call <see cref="ResolveSpec"/> for instances of this node type.
    /// </summary>
    public NodePortSpec Spec { get; } = new(
        Category: "code",
        Inputs: Array.Empty<PortDef>(),
        Outputs: Array.Empty<PortDef>(),
        Params: new Dictionary<string, ParamDef>
        {
            ["code"]  = new("string",  true,  null, "Python source. Must define run(inputs, params) → dict."),
            ["ports"] = new("object",  true,  null, "Port schema: {inputs:{name:tag}, outputs:{name:tag}}"),
            ["seed"]  = new("long",    false, 0L,   "RNG seed for reproducibility."),
        },
        AcceptsAdditionalInputs: false,
        RequiresLiveData: false);

    public NodePortSpec ResolveSpec(IReadOnlyDictionary<string, object?> nodeParams)
    {
        // ports param is stored as a raw JSON string by the validator's materialise helper.
        if (!nodeParams.TryGetValue("ports", out var portsRaw) || portsRaw is null)
            return Spec;

        PortSchema? schema = null;
        try
        {
            var json = portsRaw as string ?? portsRaw.ToString()!;
            schema = JsonSerializer.Deserialize<PortSchema>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch { return Spec; }
        if (schema is null) return Spec;

        var inputs  = (schema.Inputs  ?? new Dictionary<string, string>())
            .Select(kv => new PortDef(kv.Key, kv.Value, Required: false))
            .ToArray();
        var outputs = (schema.Outputs ?? new Dictionary<string, string>())
            .Select(kv => new PortDef(kv.Key, kv.Value))
            .ToArray();

        return new NodePortSpec(
            Category: "code",
            Inputs: inputs,
            Outputs: outputs,
            Params: Spec.Params,
            AcceptsAdditionalInputs: false,
            RequiresLiveData: false);
    }

    // Deserialization helper for the ports param.
    private sealed class PortSchema
    {
        public Dictionary<string, string>? Inputs  { get; init; }
        public Dictionary<string, string>? Outputs { get; init; }
    }

    // ---------------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------------

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        JsonElement nodeParams,
        FlowContext ctx,
        CancellationToken ct)
    {
        var code   = NodeParams.GetString(nodeParams, "code", string.Empty);
        var seed   = GetLong(nodeParams, "seed", 0L);
        var nodeId = GetNodeId(ctx);

        // Read the output schema from params so we know what tags to send.
        var outputSchema = BuildOutputSchema(nodeParams);

        // Marshal CLR inputs → sandbox wire format.
        var sandboxInputs = MarshalInputs(inputs);

        // Determine mode and series length.
        string mode;
        int seriesLength;
        if (ctx.Mode == FlowMode.Backtest)
        {
            mode = "batch";
            // seriesLength = number of series entries in the first series/candles input, else 1.
            seriesLength = EstimateSeriesLength(inputs);
        }
        else
        {
            mode = "step";
            seriesLength = 1;
        }

        // Forward node-level params as a plain dict (exclude code/ports/seed — runtime only).
        var forwardedParams = BuildForwardedParams(nodeParams);

        var req = new SandboxRequest
        {
            Mode         = mode,
            NodeId       = nodeId,
            Code         = code,
            Seed         = seed,
            Params       = forwardedParams,
            SeriesLength = seriesLength,
            Inputs       = sandboxInputs,
            OutputSchema = outputSchema,
        };

        var result = await _sandbox.ExecuteAsync(req, ct);
        if (!result.Ok)
        {
            var errMsg = result.Error is not null
                ? $"{result.Error.Kind}: {result.Error.Message}"
                : result.Stderr ?? "Sandbox returned ok=false";
            throw new FlowExecutionException(nodeId, new InvalidOperationException(errMsg));
        }

        return MarshalOutputs(result.Outputs);
    }

    // ---------------------------------------------------------------------------
    // Marshal helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, SandboxPortValue> MarshalInputs(
        IReadOnlyDictionary<string, object?> inputs)
    {
        var dict = new Dictionary<string, SandboxPortValue>(StringComparer.Ordinal);
        foreach (var (name, value) in inputs)
        {
            if (value is null) continue;
            var pv = MarshalValue(value);
            if (pv is not null) dict[name] = pv;
        }
        return dict;
    }

    internal static SandboxPortValue? MarshalValue(object? value)
    {
        if (value is null) return null;

        // Candle array → columnar "candles" tag.
        if (value is IReadOnlyList<HistoricalCandle> candles && candles.Count > 0)
        {
            var cv = new SandboxCandlesValue(
                OpenTime: candles.Select(c => c.OpenTime).ToArray(),
                Open:     candles.Select(c => (double)c.Open).ToArray(),
                High:     candles.Select(c => (double)c.High).ToArray(),
                Low:      candles.Select(c => (double)c.Low).ToArray(),
                Close:    candles.Select(c => (double)c.Close).ToArray(),
                Volume:   candles.Select(c => (double)c.Volume).ToArray());
            return new SandboxPortValue("candles", cv);
        }

        // FeatureMatrix → "matrix" tag with jagged rows[][].
        if (value is FeatureMatrix fm)
        {
            var rows = new double[fm.RowCount][];
            for (var r = 0; r < fm.RowCount; r++)
            {
                rows[r] = new double[fm.ColumnCount];
                for (var c = 0; c < fm.ColumnCount; c++)
                    rows[r][c] = fm.Rows[r, c];
            }
            return new SandboxPortValue("matrix", new SandboxMatrixValue(
                Columns: fm.Columns.ToArray(),
                Rows: rows));
        }

        // double[] / float[] → "series".
        if (value is double[] dArr)
            return new SandboxPortValue("series", dArr);
        if (value is float[] fArr)
            return new SandboxPortValue("series", fArr.Select(x => (double)x).ToArray());

        // Scalars.
        if (value is decimal d)
            return new SandboxPortValue("scalar", (double)d);
        if (value is double dd)
            return new SandboxPortValue("scalar", dd);
        if (value is float ff)
            return new SandboxPortValue("scalar", (double)ff);
        if (value is int i)
            return new SandboxPortValue("scalar", (double)i);
        if (value is long l)
            return new SandboxPortValue("scalar", (double)l);
        if (value is bool b)
            return new SandboxPortValue("scalar", b);
        if (value is string s)
            return new SandboxPortValue("scalar", s);

        // Fallback: skip unknown types rather than crash.
        return null;
    }

    private static IReadOnlyDictionary<string, object?> MarshalOutputs(
        IReadOnlyDictionary<string, SandboxPortValue> outputs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, pv) in outputs)
            dict[name] = UnmarshalValue(pv);
        return dict;
    }

    private static object? UnmarshalValue(SandboxPortValue pv)
    {
        return pv.Tag switch
        {
            "scalar" => pv.Value switch
            {
                JsonElement je => je.ValueKind == JsonValueKind.Number
                    ? Quantize(je.GetDouble())
                    : je.ValueKind == JsonValueKind.True  ? (object?)true
                    : je.ValueKind == JsonValueKind.False ? false
                    : je.GetString(),
                double d  => Quantize(d),
                float  f  => Quantize(f),
                bool   b  => b,
                string st => st,
                _         => pv.Value,
            },
            "series" => pv.Value switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.Array =>
                    je.EnumerateArray().Select(e => Quantize(e.GetDouble())).ToArray(),
                double[] da => da.Select(Quantize).ToArray(),
                _           => pv.Value,
            },
            "candles" => UnmarshalCandles(pv.Value),
            "matrix"  => UnmarshalMatrix(pv.Value),
            _         => pv.Value,
        };
    }

    private static HistoricalCandle[]? UnmarshalCandles(object? raw)
    {
        SandboxCandlesValue? cv = raw switch
        {
            SandboxCandlesValue s => s,
            JsonElement je => JsonSerializer.Deserialize<SandboxCandlesValue>(je.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            _ => null,
        };
        if (cv is null) return null;
        var n = cv.OpenTime.Length;
        var arr = new HistoricalCandle[n];
        for (var i = 0; i < n; i++)
            arr[i] = new HistoricalCandle
            {
                Symbol   = "sandbox",
                Interval = "sandbox",
                OpenTime = cv.OpenTime[i],
                Open     = (decimal)cv.Open[i],
                High     = (decimal)cv.High[i],
                Low      = (decimal)cv.Low[i],
                Close    = (decimal)cv.Close[i],
                Volume   = (decimal)cv.Volume[i],
            };
        return arr;
    }

    private static FeatureMatrix? UnmarshalMatrix(object? raw)
    {
        SandboxMatrixValue? mv = raw switch
        {
            SandboxMatrixValue s => s,
            JsonElement je => JsonSerializer.Deserialize<SandboxMatrixValue>(je.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            _ => null,
        };
        if (mv is null) return null;
        var rowCount = mv.Rows.Length;
        var colCount = mv.Columns.Length;
        var mat = new double[rowCount, colCount];
        for (var r = 0; r < rowCount; r++)
        for (var c = 0; c < Math.Min(colCount, mv.Rows[r].Length); c++)
            mat[r, c] = mv.Rows[r][c];
        return new FeatureMatrix(mv.Columns, mat);
    }

    // ---------------------------------------------------------------------------
    // Minor helpers
    // ---------------------------------------------------------------------------

    /// <summary>Round to 1e-9 to ensure backtest/live float agreement.</summary>
    private static double Quantize(double v) => Math.Round(v, 9, MidpointRounding.AwayFromZero);

    private static IReadOnlyDictionary<string, string> BuildOutputSchema(JsonElement nodeParams)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (nodeParams.ValueKind != JsonValueKind.Object) return dict;
        if (!nodeParams.TryGetProperty("ports", out var portsEl)) return dict;
        try
        {
            string portsJson = portsEl.GetRawText();
            using var doc = JsonDocument.Parse(portsJson);
            if (doc.RootElement.TryGetProperty("outputs", out var outputs) &&
                outputs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in outputs.EnumerateObject())
                    dict[prop.Name] = prop.Value.GetString() ?? "scalar";
            }
        }
        catch { /* malformed ports — no schema */ }
        return dict;
    }

    private static IReadOnlyDictionary<string, object?> BuildForwardedParams(JsonElement nodeParams)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (nodeParams.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in nodeParams.EnumerateObject())
        {
            // Skip the runtime-only keys the sandbox doesn't need.
            if (prop.Name is "code" or "ports" or "seed") continue;
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Null   => null,
                _                    => prop.Value.GetRawText(),
            };
        }
        return dict;
    }

    private static int EstimateSeriesLength(IReadOnlyDictionary<string, object?> inputs)
    {
        foreach (var val in inputs.Values)
        {
            if (val is IReadOnlyList<HistoricalCandle> c && c.Count > 0) return c.Count;
            if (val is double[] da) return da.Length;
        }
        return 1;
    }

    /// <summary>
    /// Derive a stable node id to pass to the sandbox for error attribution. Uses the flow
    /// context's ModelId as a namespace so different models with the same code node id don't collide.
    /// </summary>
    private static string GetNodeId(FlowContext ctx)
        => $"code-{ctx.ModelId:N}";

    private static long GetLong(JsonElement p, string name, long fallback)
    {
        if (p.ValueKind != JsonValueKind.Object) return fallback;
        if (!p.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d)) return (long)d;
        }
        return fallback;
    }
}
