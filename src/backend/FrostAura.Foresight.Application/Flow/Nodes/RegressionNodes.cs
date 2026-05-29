using System.Collections.Concurrent;
using System.Text.Json;
using FrostAura.Foresight.Application.Models;

namespace FrostAura.Foresight.Application.Flow.Nodes;

/// <summary>
/// Inference-time linear regression. Coefficients come from <see cref="FlowContext.TrainedState"/>
/// keyed by node id; if no trained state is present this node returns null (the flow can't predict
/// until trained). Used to predict the next-candle close — direction = sign(predicted - anchor),
/// confidence proxied by magnitude scaled to [0,1].
/// </summary>
public sealed class LinearRegressionNode : IFlowNode
{
    public string TypeId => "model.linear_regression";

    public NodePortSpec Spec { get; } = new(
        Category: "model",
        Inputs: new[]
        {
            new PortDef("matrix", "Matrix"),
            new PortDef("ready",  "bool", Required: false),
            new PortDef("anchor", "decimal", Required: false,
                Description: "Reference close to compare predicted close against. Defaults to last candle's close."),
        },
        Outputs: new[]
        {
            new PortDef("predicted", "decimal?"),
            new PortDef("pUp",       "decimal?"),
            new PortDef("confidence","decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["l2"] = new("decimal", false, 0.0m, "Ridge regularization strength."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var ready = inputs.GetValueOrDefault("ready") as bool? ?? true;
        var matrix = inputs.GetValueOrDefault("matrix") as FeatureMatrix;
        if (!ready || matrix is null || matrix.RowCount == 0)
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        // Coefficients are written into TrainedState by ModelTrainer.TrainLinearRegression with the
        // shape { "intercept": double, "weights": [double], "featureNames": [string] }.
        var coeffs = ResolveCoefficients(ctx, matrix);
        if (coeffs is null) return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        var (intercept, weights) = coeffs.Value;
        double y = intercept;
        for (var i = 0; i < weights.Length && i < matrix.ColumnCount; i++)
            y += weights[i] * matrix.Rows[0, i];

        var predicted = (decimal)y;
        var anchorRaw = inputs.GetValueOrDefault("anchor");
        decimal? anchor = anchorRaw switch { decimal d => d, double dd => (decimal)dd, _ => null };
        decimal? pUp = anchor is null ? null : predicted >= anchor ? 1m : 0m;
        // Confidence as |predicted - anchor| / anchor, clamped to [0, 1]. Cheap proxy in absence of
        // calibrated probability distributions — replaced by LogReg's native pUp where wanted.
        decimal confidence = anchor is null || anchor.Value == 0
            ? 0.5m
            : Math.Min(1m, Math.Abs((predicted - anchor.Value) / anchor.Value) * 10m);

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["predicted"]  = predicted,
            ["pUp"]        = pUp,
            ["confidence"] = confidence,
        });
    }

    private static IReadOnlyDictionary<string, object?> NullOutputs() => new Dictionary<string, object?>
    {
        ["predicted"] = (decimal?)null,
        ["pUp"] = (decimal?)null,
        ["confidence"] = 0.5m,
    };

    /// <summary>Reads (intercept, weights) for THIS node's id out of the model's trained-state blob.</summary>
    internal static (double Intercept, double[] Weights)? ResolveCoefficients(FlowContext ctx, FeatureMatrix _matrix)
    {
        if (ctx.TrainedState is not JsonElement state || state.ValueKind != JsonValueKind.Object) return null;
        // Currently keyed by node-typeid since one node per regression is the common case. The
        // FlowExecutor will lift this to per-node-id keying once flows with multiple regressions become a thing.
        if (!state.TryGetProperty("model.linear_regression", out var blob)) return null;
        if (!blob.TryGetProperty("weights", out var wArr) || wArr.ValueKind != JsonValueKind.Array) return null;
        var intercept = blob.TryGetProperty("intercept", out var iEl) ? iEl.GetDouble() : 0.0;
        var weights = new double[wArr.GetArrayLength()];
        var idx = 0;
        foreach (var v in wArr.EnumerateArray()) weights[idx++] = v.GetDouble();
        return (intercept, weights);
    }
}

/// <summary>
/// Inference-time logistic regression — sigmoid over a linear combination of features producing
/// <c>pUp ∈ [0,1]</c> directly. The native Up/Down classifier; preferred over LinearRegression
/// when the target is direction rather than magnitude.
/// </summary>
public sealed class LogisticRegressionNode : IFlowNode
{
    public string TypeId => "model.logistic_regression";

    public NodePortSpec Spec { get; } = new(
        Category: "model",
        Inputs: new[]
        {
            new PortDef("matrix", "Matrix"),
            new PortDef("ready",  "bool", Required: false),
        },
        Outputs: new[]
        {
            new PortDef("pUp",        "decimal?"),
            new PortDef("confidence", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["l2"] = new("decimal", false, 0.01m, "Ridge regularization strength."),
            ["min_confidence"] = new("decimal", false, 0m,
                "Confidence threshold for the high-conviction REPORTING subset (confidence = |pUp - 0.5| * 2, range [0,1]). The node no longer abstains on it — it always emits pUp so live and backtest measure the SAME strategy. BacktestRunner reads this param only to compute a secondary gated hit-rate alongside the headline ungated (live-equivalent) one."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var ready = inputs.GetValueOrDefault("ready") as bool? ?? true;
        var matrix = inputs.GetValueOrDefault("matrix") as FeatureMatrix;
        if (!ready || matrix is null || matrix.RowCount == 0)
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        var coeffs = ResolveCoefficients(ctx);
        if (coeffs is null) return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        var (intercept, weights) = coeffs.Value;
        double z = intercept;
        for (var i = 0; i < weights.Length && i < matrix.ColumnCount; i++)
            z += weights[i] * matrix.Rows[0, i];
        var p = 1.0 / (1.0 + Math.Exp(-z));
        var pUp = (decimal)p;
        // Confidence as |pUp - 0.5| * 2 — 0 at 0.5, 1 at 0/1. Calibrated probability already lives in pUp.
        var confidence = Math.Abs(pUp - 0.5m) * 2m;

        // No in-node confidence gating. The node ALWAYS emits pUp so live and backtest measure the
        // same "bet every candle" strategy — the old backtest-only abstention is exactly what made
        // v6's backtest headline (53%, on the confident subset) diverge from its live number (~51%,
        // all candles). Any high-conviction reporting split now lives in BacktestRunner, which reads
        // `min_confidence` to compute a secondary gated hit-rate next to the headline ungated one.
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["pUp"]        = pUp,
            ["confidence"] = confidence,
        });
    }

    private static IReadOnlyDictionary<string, object?> NullOutputs() => new Dictionary<string, object?>
    {
        ["pUp"] = (decimal?)null,
        ["confidence"] = 0.5m,
    };

    internal static (double Intercept, double[] Weights)? ResolveCoefficients(FlowContext ctx)
    {
        if (ctx.TrainedState is not JsonElement state || state.ValueKind != JsonValueKind.Object) return null;
        if (!state.TryGetProperty("model.logistic_regression", out var blob)) return null;
        if (!blob.TryGetProperty("weights", out var wArr) || wArr.ValueKind != JsonValueKind.Array) return null;
        var intercept = blob.TryGetProperty("intercept", out var iEl) ? iEl.GetDouble() : 0.0;
        var weights = new double[wArr.GetArrayLength()];
        var idx = 0;
        foreach (var v in wArr.EnumerateArray()) weights[idx++] = v.GetDouble();
        return (intercept, weights);
    }
}

/// <summary>
/// Inference-time gradient-boosted trees — the non-linear estimator option. Same port contract as
/// <see cref="LogisticRegressionNode"/> (matrix + ready → pUp + confidence), so it drops into any
/// flow behind <c>feature.matrix_builder</c>/<c>output.prediction</c> with no other change. The
/// trained ensemble lives on <see cref="FlowContext.TrainedState"/> under the <c>model.gbt</c> key
/// (written by <see cref="Models.ModelTrainer"/>). Always emits pUp (no in-node gating — gating is a
/// reporting concern, see <see cref="LogisticRegressionNode"/>).
/// </summary>
public sealed class GradientBoostedTreesNode : IFlowNode
{
    public string TypeId => "model.gbt";

    // Parsing a 150-tree ensemble per candle would dominate a backtest; cache by the trained-state's
    // raw JSON (identical across every call of a single run). Bounded so it can't grow unboundedly.
    private static readonly ConcurrentDictionary<string, GbtModel> _cache = new();

    public NodePortSpec Spec { get; } = new(
        Category: "model",
        Inputs: new[]
        {
            new PortDef("matrix", "Matrix"),
            new PortDef("ready",  "bool", Required: false),
        },
        Outputs: new[]
        {
            new PortDef("pUp",        "decimal?"),
            new PortDef("confidence", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["n_estimators"]     = new("int", false, 150, "Number of boosting rounds (trees)."),
            ["max_depth"]        = new("int", false, 3, "Max tree depth — keep shallow to resist overfitting."),
            ["learning_rate"]    = new("decimal", false, 0.04m, "Shrinkage applied to each tree."),
            ["min_samples_leaf"] = new("int", false, 200, "Minimum rows per leaf — large values regularise on thin-edge data."),
            ["subsample"]        = new("decimal", false, 0.7m, "Row subsample fraction per tree."),
            ["colsample"]        = new("decimal", false, 0.7m, "Feature subsample fraction per split."),
            ["l2"]               = new("decimal", false, 1.0m, "L2 leaf-weight penalty (lambda)."),
            ["min_confidence"]   = new("decimal", false, 0m,
                "Confidence threshold for the high-conviction REPORTING subset (confidence = |pUp - 0.5| * 2). The node never abstains on it (always emits pUp, so live == backtest); BacktestRunner reads it to compute a secondary gated hit-rate alongside the headline ungated one."),
        });

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var ready = inputs.GetValueOrDefault("ready") as bool? ?? true;
        var matrix = inputs.GetValueOrDefault("matrix") as FeatureMatrix;
        if (!ready || matrix is null || matrix.RowCount == 0)
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        var model = ResolveModel(ctx);
        if (model is null || model.Trees.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(NullOutputs());

        var row = new double[matrix.ColumnCount];
        for (var c = 0; c < matrix.ColumnCount; c++) row[c] = matrix.Rows[0, c];

        var p = GradientBoostedTrees.PredictProba(model, row);
        var pUp = (decimal)p;
        var confidence = Math.Abs(pUp - 0.5m) * 2m;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["pUp"]        = pUp,
            ["confidence"] = confidence,
        });
    }

    private static IReadOnlyDictionary<string, object?> NullOutputs() => new Dictionary<string, object?>
    {
        ["pUp"] = (decimal?)null,
        ["confidence"] = 0.5m,
    };

    private static GbtModel? ResolveModel(FlowContext ctx)
    {
        if (ctx.TrainedState is not JsonElement state || state.ValueKind != JsonValueKind.Object) return null;
        if (!state.TryGetProperty("model.gbt", out var blob) || blob.ValueKind != JsonValueKind.Object) return null;
        var raw = blob.GetRawText();
        return _cache.GetOrAdd(raw, r =>
        {
            if (_cache.Count > 32) _cache.Clear(); // crude bound; models are few
            return JsonSerializer.Deserialize<GbtModel>(r) ?? new GbtModel();
        });
    }
}

/// <summary>
/// Combines N model signals into one via simple (optionally weighted) majority vote on the sign of
/// (pUp - 0.5). Confidence = mean confidence of the winning side.
/// </summary>
public sealed class MajorityVoteNode : IFlowNode
{
    public string TypeId => "aggregator.majority_vote";

    public NodePortSpec Spec { get; } = new(
        Category: "aggregator",
        Inputs: Array.Empty<PortDef>(),
        Outputs: new[]
        {
            new PortDef("pUp",        "decimal?"),
            new PortDef("confidence", "decimal"),
        },
        Params: new Dictionary<string, ParamDef>
        {
            ["signalPorts"] = new("string[]", true, null,
                "Input port names carrying upstream pUp values to vote across."),
        },
        AcceptsAdditionalInputs: true);

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, JsonElement nodeParams, FlowContext ctx, CancellationToken ct)
    {
        var ports = NodeParams.Get<List<string>>(nodeParams, "signalPorts") ?? new List<string>();
        var ups = 0; var downs = 0; decimal upConf = 0m; decimal downConf = 0m;
        foreach (var port in ports)
        {
            if (inputs.GetValueOrDefault(port) is not decimal p) continue;
            // Confidence for this signal is read from a parallel port "<port>_confidence" if bound.
            decimal c = inputs.GetValueOrDefault(port + "_confidence") is decimal cc ? cc : 0.5m;
            if (p >= 0.5m) { ups++; upConf += c; } else { downs++; downConf += c; }
        }
        decimal? pUp = null;
        decimal confidence = 0.5m;
        if (ups + downs > 0)
        {
            pUp = ups >= downs ? 0.5m + 0.5m * (decimal)ups / (ups + downs) : 0.5m - 0.5m * (decimal)downs / (ups + downs);
            confidence = ups >= downs ? upConf / Math.Max(1, ups) : downConf / Math.Max(1, downs);
        }
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["pUp"]        = pUp,
            ["confidence"] = confidence,
        });
    }
}
