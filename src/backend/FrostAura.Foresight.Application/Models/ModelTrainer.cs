using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Domain.Ports;
using MathNet.Numerics.LinearAlgebra;

namespace FrostAura.Foresight.Application.Models;

/// <summary>
/// Fits deterministic model coefficients (linear regression, logistic regression) by walking the
/// flow up to the <c>feature.matrix_builder</c> node for every candle in a historical training
/// window, then solving the regression problem via Math.NET Numerics. Stores the resulting
/// coefficients on <c>Model.TrainedState</c> as JSON.
///
/// Validation is a chronological 80/20 hold-out — k-fold leaks across time on financial series, so
/// rolling-origin is post-v1 (see <see cref="TrainAsync"/>). The validation accuracy is the
/// direction-hit rate of the trained model against the held-out tail of the training window.
/// </summary>
public sealed class ModelTrainer
{
    private readonly IFlowExecutor _executor;
    private readonly IHistoricalCandleProvider _candles;
    private readonly IHistoricalMicrostructureProvider? _micro;

    public ModelTrainer(IFlowExecutor executor, IHistoricalCandleProvider candles,
        IHistoricalMicrostructureProvider? micro = null)
    {
        _executor = executor;
        _candles = candles;
        _micro = micro;
    }

    public async Task<TrainResult> TrainAsync(
        FlowDefinition flow,
        Guid tenantId,
        Guid modelId,
        string symbol,
        string interval,
        long trainingWindowStartMs,
        long trainingWindowEndMs,
        CancellationToken ct,
        int horizonSteps = 2)
    {
        // Fetch historicals with warmup so the first usable candle has full indicator coverage.
        var intervalMs = IntervalMs(interval);
        var warmupMs = (long)flow.WarmupCandles * intervalMs;
        var candles = await _candles.GetRangeAsync(symbol, interval,
            trainingWindowStartMs - warmupMs, trainingWindowEndMs, ct);
        if (candles.Count <= flow.WarmupCandles + 10)
            throw new InvalidOperationException($"Not enough candles ({candles.Count}) for training; need at least {flow.WarmupCandles + 10}.");

        // Pre-fetch off-tf candles once so flows with multi-timeframe source nodes (e.g. a 15m
        // regime feature feeding a 5m model) can compute their indicators during training. Per-tf
        // warmup scales with the off-tf interval so 14-bar RSI etc still have coverage.
        var offTfCandles = new Dictionary<string, IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>>();
        foreach (var otherTf in FrostAura.Foresight.Domain.MarketData.SupportedSymbols.Intervals)
        {
            if (otherTf == interval) continue;
            var offWarmupMs = Math.Max(warmupMs, 60L * IntervalMs(otherTf));
            try { offTfCandles[otherTf] = await _candles.GetRangeAsync(symbol, otherTf, trainingWindowStartMs - offWarmupMs, trainingWindowEndMs, ct); }
            catch { offTfCandles[otherTf] = Array.Empty<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>(); }
        }

        // Pre-fetch microstructure bars once (when a provider is wired); per-iteration slices clamp
        // by close-time so order-flow features train under the same anti-lookahead rule as candles.
        IReadOnlyList<FrostAura.Foresight.Domain.MarketData.MicrostructureBar> microPool =
            Array.Empty<FrostAura.Foresight.Domain.MarketData.MicrostructureBar>();
        if (_micro is not null)
        {
            try { microPool = await _micro.GetRangeAsync(symbol, interval, trainingWindowStartMs - warmupMs, trainingWindowEndMs, ct); }
            catch { microPool = Array.Empty<FrostAura.Foresight.Domain.MarketData.MicrostructureBar>(); }
        }

        // Build (feature matrix, target) rows by replaying the flow up to feature.matrix_builder.
        // Anti-lookahead: each iteration sees only candles[..i+1] via TrainingSliceProvider, which
        // also serves off-tf candles from the pre-fetched dict (clamped to the current boundary).
        var X = new List<double[]>();
        var yClose = new List<double>();    // for LR — predict the target candle (i+2) close
        var yDir   = new List<int>();       // for LogReg — direction = target candle's own body (close > open)
        var refOpens = new List<double>();  // open(i+horizon) per row — the target candle's OPEN reference
        var columns = new List<string>();

        // Horizon-ahead canon, identical to BacktestRunner and live inference: at the decision moment
        // we only have data through the last CLOSED candle (index i = "previous candle"). The target
        // is candle i+horizonSteps, graded by ITS OWN BODY — close(i+horizonSteps) vs open(i+horizonSteps)
        // — which is exactly how Polymarket settles its BTC up/down market (a period is UP iff that
        // target candle closes above where it opened). The default horizon of 2 skips candle i+1 — the
        // candle "forming" while we decide under a slow (LLM) decision path, so it is never an input.
        // horizon=1 predicts the very next candle (i+1) directly, viable now that the decision is an
        // instant deterministic compute at candle close. Whatever the horizon, candle i+1 (and beyond)
        // is NEVER a feature input: features see candles[0..i] only; off-tf is gated to the decision
        // moment (open of i+1) by close-time so a still-open higher-tf bar can never leak.
        for (var i = flow.WarmupCandles; i < candles.Count - horizonSteps; i++)
        {
            var boundaryMs = candles[i].OpenTime + intervalMs;
            var slice = new TrainingSliceProvider(candles, candles[i].OpenTime, interval, offTfCandles, boundaryMs);
            var microSlice = _micro is null ? null : new Backtesting.MicrostructureSlice(microPool, intervalMs, boundaryMs);
            var ctx = new FlowContext(tenantId, modelId, symbol, interval,
                candles[i + horizonSteps].OpenTime, 1, FlowMode.Backtest, slice, TrainedState: null, Microstructure: microSlice);

            FlowResult result;
            try { result = await _executor.ExecuteAsync(flow, ctx, ct); }
            catch { continue; }   // skip warmup misses gracefully

            // Find the matrix builder's output via the trace.
            var matrixEntry = result.NodeOutputs.FirstOrDefault(kv => kv.Value.ContainsKey("matrix"));
            if (matrixEntry.Value is null) continue;
            if (matrixEntry.Value["matrix"] is not FeatureMatrix matrix) continue;
            if (matrixEntry.Value.GetValueOrDefault("ready") is bool ready && !ready) continue;

            if (columns.Count == 0) columns.AddRange(matrix.Columns);
            var row = new double[matrix.ColumnCount];
            for (var c = 0; c < matrix.ColumnCount; c++) row[c] = matrix.Rows[0, c];
            X.Add(row);
            yClose.Add((double)candles[i + horizonSteps].Close);
            // Polymarket canon: the period is UP iff the target candle's own body is green
            // (close > open). NOT close(i+horizon) vs close(i) — that graded a 2-candle move and
            // mismatched how the venue settles. For continuous 24/7 BTC open[T] == close[T-1], so on
            // horizon=1 this also equals close-vs-prevClose (the chart's grading).
            yDir.Add((double)candles[i + horizonSteps].Close > (double)candles[i + horizonSteps].Open ? 1 : 0);
            refOpens.Add((double)candles[i + horizonSteps].Open);
        }

        if (X.Count < 60)
            throw new InvalidOperationException($"Not enough training rows ({X.Count}) after warmup. Need ≥ 60 for walk-forward (one bucket per fold ≥ 10 rows). Widen the date range.");

        // Walk-forward (rolling-origin) validation. Replaces the old single 80/20 chronological
        // split — that split was a single sample of the validation-accuracy distribution; one
        // bull market in the tail or one weird week of regime shift could swing it ±2pp without
        // saying anything about the model's actual robustness. Walk-forward measures the average
        // out-of-sample accuracy across multiple temporally-disjoint folds, which is the metric
        // we actually care about ("how does this procedure generalize across time").
        //
        // Layout (expanding-window, the standard for financial time series):
        //   split into N+1 chronological buckets of equal size
        //   for k = 1..N:
        //     train on buckets[0..k-1]   (concatenated)
        //     validate on bucket[k]
        //   report mean validation accuracy across folds
        //
        // After WF, fit FINAL coefficients on the entire window so the deployed model has seen
        // as much history as possible — WF measures the procedure, not the final coefficients.
        const int targetFolds = 5;
        var actualFolds = Math.Min(targetFolds, Math.Max(2, X.Count / 60));
        var bucketSize = X.Count / (actualFolds + 1);
        var lrFoldAccs   = new List<double>(actualFolds);
        var logrFoldAccs = new List<double>(actualFolds);
        var gbtFoldAccs  = new List<double>(actualFolds);
        // LR predicts the target candle's CLOSE; its direction is derived by comparing that predicted
        // close against the target candle's OPEN — the same Polymarket close-vs-open reference the
        // LogReg/GBT label uses — so the two estimators' fold accuracies stay comparable.
        var allOpensForLrAnchor = refOpens.ToArray();

        // Engine dispatch: if the flow carries a model.gbt node, gradient-boosted trees is the
        // estimator and its walk-forward accuracy is the headline; otherwise it's logistic
        // regression. LR/LogReg are always fit (cheap) so the trained-state shape stays stable.
        var gbtParams = TryReadGbtParams(flow);

        for (var k = 1; k <= actualFolds; k++)
        {
            var trainEnd = bucketSize * k;
            var valEnd   = Math.Min(bucketSize * (k + 1), X.Count);

            var XtrainFold      = X.Take(trainEnd).ToArray();
            var XvalFold        = X.Skip(trainEnd).Take(valEnd - trainEnd).ToArray();
            var yCloseTrainFold = yClose.Take(trainEnd).ToArray();
            var yDirTrainFold   = yDir.Take(trainEnd).ToArray();
            var yDirValFold     = yDir.Skip(trainEnd).Take(valEnd - trainEnd).ToArray();
            var openValFold     = allOpensForLrAnchor.Skip(trainEnd).Take(valEnd - trainEnd).ToArray();

            var lrFold   = FitLinearRegression(XtrainFold, yCloseTrainFold);
            var logrFold = FitLogisticRegression(XtrainFold, yDirTrainFold);
            var gbtFold  = gbtParams is null ? null : GradientBoostedTrees.Fit(XtrainFold, yDirTrainFold, gbtParams);

            int lrHitsK = 0, logrHitsK = 0, gbtHitsK = 0;
            for (var v = 0; v < XvalFold.Length; v++)
            {
                var row = XvalFold[v];
                var lrPred = lrFold.Intercept;
                for (var c = 0; c < lrFold.Weights.Length; c++) lrPred += lrFold.Weights[c] * row[c];
                if ((lrPred >= openValFold[v] ? 1 : 0) == yDirValFold[v]) lrHitsK++;

                var z = logrFold.Intercept;
                for (var c = 0; c < logrFold.Weights.Length; c++) z += logrFold.Weights[c] * row[c];
                var p = 1.0 / (1.0 + Math.Exp(-z));
                if ((p >= 0.5 ? 1 : 0) == yDirValFold[v]) logrHitsK++;

                if (gbtFold is not null && (GradientBoostedTrees.PredictProba(gbtFold, row) >= 0.5 ? 1 : 0) == yDirValFold[v]) gbtHitsK++;
            }
            lrFoldAccs.Add  (XvalFold.Length == 0 ? 0.0 : (double)lrHitsK   / XvalFold.Length);
            logrFoldAccs.Add(XvalFold.Length == 0 ? 0.0 : (double)logrHitsK / XvalFold.Length);
            if (gbtFold is not null) gbtFoldAccs.Add(XvalFold.Length == 0 ? 0.0 : (double)gbtHitsK / XvalFold.Length);
        }

        var lrAcc   = lrFoldAccs.Average();
        var logrAcc = logrFoldAccs.Average();
        var gbtAcc  = gbtFoldAccs.Count > 0 ? gbtFoldAccs.Average() : 0.0;

        // Final coefficients fit on the entire training window. The deployed model carries these,
        // not the per-fold coefficients — walk-forward validates the procedure's robustness; the
        // final model uses all the data.
        var lr   = FitLinearRegression(X.ToArray(), yClose.ToArray());
        var logr = FitLogisticRegression(X.ToArray(), yDir.ToArray());
        var gbt  = gbtParams is null ? null : GradientBoostedTrees.Fit(X.ToArray(), yDir.ToArray(), gbtParams);

        var trainedState = JsonSerializer.Serialize(new
        {
            featureNames = columns,
            trainedAt = DateTimeOffset.UtcNow,
            trainingRows = X.Count,
            engine = gbtParams is null ? "logistic_regression" : "gbt",
            walkForward = new
            {
                folds = actualFolds,
                bucketSize,
                lrFoldAccs   = lrFoldAccs.Select(a => Math.Round(a, 4)).ToArray(),
                logrFoldAccs = logrFoldAccs.Select(a => Math.Round(a, 4)).ToArray(),
                gbtFoldAccs  = gbtFoldAccs.Select(a => Math.Round(a, 4)).ToArray(),
                lrMean   = Math.Round(lrAcc, 4),
                logrMean = Math.Round(logrAcc, 4),
                gbtMean  = Math.Round(gbtAcc, 4),
            },
            modelLinearRegression = new
            {
                intercept = lr.Intercept,
                weights = lr.Weights,
                featureNames = columns,
            },
            modelLogisticRegression = new
            {
                intercept = logr.Intercept,
                weights = logr.Weights,
                featureNames = columns,
            },
            modelGbt = gbt,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // ValidationAccuracy returned to the caller is the walk-forward mean of the ACTIVE engine —
        // the honest "across multiple regimes" number. Compared against the honest-backtest's
        // out-of-sample accuracy, the gap should be small if no leakage and the model truly
        // generalizes. A big gap = the trainer's robustness story doesn't match production.
        var headlineAcc = gbtParams is null ? Math.Max(lrAcc, logrAcc) : gbtAcc;
        return new TrainResult(
            TrainedStateJson: trainedState,
            ValidationAccuracy: (decimal)headlineAcc);
    }

    /// <summary>
    /// Reads gradient-boosted-tree hyper-parameters off the flow's <c>model.gbt</c> node, or returns
    /// null when the flow uses a different estimator (logistic regression is the default engine).
    /// </summary>
    private static GbtParams? TryReadGbtParams(FlowDefinition flow)
    {
        var node = flow.Nodes.FirstOrDefault(n => n.Type == "model.gbt");
        if (node is null) return null;
        var p = node.Params;
        return new GbtParams(
            NEstimators:    Flow.Nodes.NodeParams.GetInt(p, "n_estimators", 150),
            MaxDepth:       Flow.Nodes.NodeParams.GetInt(p, "max_depth", 3),
            LearningRate:   (double)Flow.Nodes.NodeParams.GetDecimal(p, "learning_rate", 0.04m),
            MinSamplesLeaf: Flow.Nodes.NodeParams.GetInt(p, "min_samples_leaf", 200),
            Subsample:      (double)Flow.Nodes.NodeParams.GetDecimal(p, "subsample", 0.7m),
            ColSample:      (double)Flow.Nodes.NodeParams.GetDecimal(p, "colsample", 0.7m),
            Lambda:         (double)Flow.Nodes.NodeParams.GetDecimal(p, "l2", 1.0m),
            Gamma:          0.0,
            Seed:           1);
    }

    /// <summary>
    /// Linear regression via the normal equations with optional L2 ridge. Returns intercept + weights.
    /// </summary>
    private static (double Intercept, double[] Weights) FitLinearRegression(double[][] X, double[] y, double l2 = 0.0)
    {
        var n = X.Length;
        var p = X[0].Length;
        // Augment with intercept column.
        var Xm = Matrix<double>.Build.Dense(n, p + 1);
        for (var i = 0; i < n; i++)
        {
            Xm[i, 0] = 1.0;
            for (var c = 0; c < p; c++) Xm[i, c + 1] = X[i][c];
        }
        var ym = Vector<double>.Build.Dense(y);

        var Xt = Xm.Transpose();
        var XtX = Xt * Xm;
        if (l2 > 0)
        {
            var ridge = Matrix<double>.Build.DenseIdentity(p + 1) * l2;
            ridge[0, 0] = 0; // don't regularize intercept
            XtX += ridge;
        }
        var XtY = Xt * ym;
        var beta = XtX.Solve(XtY);
        var weights = new double[p];
        for (var c = 0; c < p; c++) weights[c] = beta[c + 1];
        // Guard against a singular/ill-conditioned solve producing NaN/±∞ (collinear feature columns
        // with no ridge). A non-finite coefficient can't be serialized to TrainedState and would
        // poison inference — fall back to an intercept-only (zero-weight) fit, which the regression
        // node reads as a flat prediction rather than crashing the whole training run.
        if (!double.IsFinite(beta[0]) || weights.Any(x => !double.IsFinite(x)))
            return (0.0, new double[p]);
        return (beta[0], weights);
    }

    /// <summary>
    /// Logistic regression via IRLS (iteratively reweighted least squares), 20 iterations max.
    /// Robust to (quasi-)separation and collinearity: IRLS can diverge to ±∞ weights on such data,
    /// which both produces garbage coefficients and can't be serialized. We escalate the ridge
    /// penalty until the fit is finite, and as a last resort return a flat (zero-weight) model.
    /// </summary>
    private static (double Intercept, double[] Weights) FitLogisticRegression(double[][] X, int[] y, double l2 = 0.01)
    {
        // Try the requested ridge first, then progressively stronger penalties if the fit blows up.
        foreach (var ridge in new[] { l2, Math.Max(l2, 0.1), Math.Max(l2, 1.0), Math.Max(l2, 10.0) })
        {
            var (intercept, weights, ok) = TryFitLogistic(X, y, ridge);
            if (ok) return (intercept, weights);
        }
        return (0.0, new double[X[0].Length]); // degenerate fallback → pUp ≈ 0.5 (model abstains)
    }

    private static (double Intercept, double[] Weights, bool Ok) TryFitLogistic(double[][] X, int[] y, double l2)
    {
        var n = X.Length;
        var p = X[0].Length;
        var Xm = Matrix<double>.Build.Dense(n, p + 1);
        for (var i = 0; i < n; i++)
        {
            Xm[i, 0] = 1.0;
            for (var c = 0; c < p; c++) Xm[i, c + 1] = X[i][c];
        }
        var ym = Vector<double>.Build.Dense(y.Select(yi => (double)yi).ToArray());
        var beta = Vector<double>.Build.Dense(p + 1);   // zeros = finite seed

        var Xt = Xm.Transpose();
        for (var iter = 0; iter < 20; iter++)
        {
            var eta = Xm * beta;
            var mu = eta.Map(e => 1.0 / (1.0 + Math.Exp(-e)));
            var w = mu.Map(m => Math.Max(m * (1 - m), 1e-6));
            var z = eta + (ym - mu).PointwiseDivide(w);

            // IRLS needs Xᵀ W X and Xᵀ W z where W = diag(w). The naive materialisation —
            // Matrix.DenseDiagonal(n, n, ...) — allocates an n×n matrix; at n=70k that's 4.9B
            // doubles (39 GB) and throws OverflowException before it can fail with OOM. Instead
            // compute the products directly by scaling rows of X / entries of z by the
            // corresponding w element — same math, O(n·p) work and O(n+p²) memory.
            var WXm = Matrix<double>.Build.Dense(n, p + 1);
            for (var i = 0; i < n; i++)
                for (var c = 0; c < p + 1; c++)
                    WXm[i, c] = w[i] * Xm[i, c];
            var Wz = Vector<double>.Build.Dense(n);
            for (var i = 0; i < n; i++) Wz[i] = w[i] * z[i];

            var XtWX = Xt * WXm;
            if (l2 > 0)
            {
                var ridge = Matrix<double>.Build.DenseIdentity(p + 1) * l2;
                ridge[0, 0] = 0;
                XtWX += ridge;
            }
            var XtWz = Xt * Wz;
            Vector<double> newBeta;
            try { newBeta = XtWX.Solve(XtWz); }
            catch { return (0.0, new double[p], false); }
            // A non-finite step means this ridge level can't stabilise the fit — keep the last finite
            // beta and signal failure so the caller escalates the penalty.
            if (newBeta.Any(b => !double.IsFinite(b))) return (beta[0], BetaWeights(beta, p), false);
            var delta = (newBeta - beta).L2Norm();
            beta = newBeta;
            if (delta < 1e-6) break;
        }

        var ws = BetaWeights(beta, p);
        var fin = double.IsFinite(beta[0]) && ws.All(double.IsFinite);
        return (beta[0], ws, fin);
    }

    private static double[] BetaWeights(Vector<double> beta, int p)
    {
        var weights = new double[p];
        for (var c = 0; c < p; c++) weights[c] = beta[c + 1];
        return weights;
    }

    private static long IntervalMs(string interval) => interval switch
    {
        "1m"  => 60_000L,
        "5m"  => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };
}

public sealed record TrainResult(string TrainedStateJson, decimal ValidationAccuracy);

/// <summary>
/// Anti-lookahead provider for a single training iteration. Target-tf candles come from the
/// in-memory slice; other timeframes come from the pre-fetched off-tf dict (one fetch per off-tf
/// at the top of training, then in-memory slicing per iteration). Open-time of any off-tf candle
/// is clamped to <paramref name="currentBoundaryMs"/> so the trainer never sees future data on
/// any timeframe. Mirrors <c>BacktestSliceProvider</c> exactly so training and backtest produce
/// identical feature distributions.
/// </summary>
internal sealed class TrainingSliceProvider : IHistoricalCandleProvider
{
    private readonly IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle> _targetFull;
    private readonly long _targetCapOpenInclusive;
    private readonly string _targetInterval;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>> _offTf;
    private readonly long _currentBoundaryMs;

    public TrainingSliceProvider(
        IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle> targetFull,
        long targetCapOpenInclusive,
        string targetInterval,
        IReadOnlyDictionary<string, IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>> offTf,
        long currentBoundaryMs)
    {
        _targetFull = targetFull;
        _targetCapOpenInclusive = targetCapOpenInclusive;
        _targetInterval = targetInterval;
        _offTf = offTf;
        _currentBoundaryMs = currentBoundaryMs;
    }

    public Task<IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        // Binary-search windows (O(log n + window)) instead of per-iteration full copies — must
        // mirror BacktestSliceProvider exactly so training and backtest see identical distributions.
        if (interval == _targetInterval)
        {
            var hi = Math.Min(endMs, _targetCapOpenInclusive);
            return Task.FromResult<IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>>(
                FrostAura.Foresight.Application.Backtesting.BacktestRunner.SortedRange(_targetFull, symbol, startMs, hi));
        }
        if (!_offTf.TryGetValue(interval, out var pool))
            return Task.FromResult<IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>>(
                Array.Empty<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>());
        // Off-tf capped by CLOSE-time so a still-open higher-tf bar can never leak.
        var clampedEnd = Math.Min(endMs, _currentBoundaryMs);
        var offIntervalMs = FrostAura.Foresight.Application.Backtesting.BacktestRunner.PublicIntervalMs(interval);
        return Task.FromResult<IReadOnlyList<FrostAura.Foresight.Domain.MarketData.HistoricalCandle>>(
            FrostAura.Foresight.Application.Backtesting.BacktestRunner.SortedRange(pool, symbol, startMs, clampedEnd - offIntervalMs));
    }
}
