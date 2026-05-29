using System.Text.Json.Serialization;

namespace FrostAura.Foresight.Application.Models;

/// <summary>
/// Compact, dependency-free gradient-boosted decision trees for binary classification (logistic
/// loss), XGBoost-style: each tree fits the gradient/hessian of the current log-odds, split gain
/// and leaf weights use the regularised Newton formulation, and predictions are the base log-odds
/// plus a shrunk sum of tree outputs passed through a sigmoid.
///
/// Chosen as the v1 5m estimator's non-linear option: it captures threshold/interaction structure a
/// logistic regression can't, while the hyper-parameters (shallow depth, large min-leaf, row/feature
/// subsampling, L2 leaf penalty, shrinkage) are exactly the knobs that keep it from overfitting a
/// thin-edge target. Fully deterministic given <see cref="GbtParams.Seed"/> so training is
/// reproducible and the walk-forward harness measures the model, not RNG noise.
/// </summary>
public static class GradientBoostedTrees
{
    public static GbtModel Fit(double[][] x, int[] y, GbtParams p)
    {
        var n = x.Length;
        if (n == 0) return new GbtModel { BaseScore = 0, LearningRate = p.LearningRate, FeatureCount = 0, Trees = new() };
        var featureCount = x[0].Length;

        // Base score = log-odds of the class prior, clamped away from 0/1 so the first gradients are sane.
        var pos = y.Count(v => v == 1);
        var baseRate = Math.Clamp((double)pos / n, 1e-3, 1 - 1e-3);
        var baseScore = Math.Log(baseRate / (1 - baseRate));

        var f = new double[n];
        Array.Fill(f, baseScore);
        var rng = new Random(p.Seed);
        var model = new GbtModel { BaseScore = baseScore, LearningRate = p.LearningRate, FeatureCount = featureCount, Trees = new(p.NEstimators) };

        for (var m = 0; m < p.NEstimators; m++)
        {
            // Gradient + hessian of logistic loss at the current scores.
            var g = new double[n];
            var h = new double[n];
            for (var i = 0; i < n; i++)
            {
                var pi = 1.0 / (1.0 + Math.Exp(-f[i]));
                g[i] = pi - y[i];
                h[i] = Math.Max(pi * (1 - pi), 1e-6);
            }

            // Row subsample (stochastic boosting — decorrelates trees, regularises).
            var rows = SubsampleRows(n, p.Subsample, rng);
            var tree = BuildTree(x, g, h, rows, depth: 0, p, rng, featureCount);

            for (var i = 0; i < n; i++)
                f[i] += p.LearningRate * Eval(tree, x[i]);
            model.Trees.Add(tree);
        }
        return model;
    }

    private static int[] SubsampleRows(int n, double frac, Random rng)
    {
        if (frac >= 1.0) return Enumerable.Range(0, n).ToArray();
        var take = Math.Max(1, (int)(n * frac));
        // Reservoir-free partial shuffle is overkill; a simple Bernoulli draw with a floor is fine.
        var picked = new List<int>(take);
        for (var i = 0; i < n; i++) if (rng.NextDouble() < frac) picked.Add(i);
        if (picked.Count == 0) picked.Add(rng.Next(n));
        return picked.ToArray();
    }

    private static GbtTreeNode BuildTree(double[][] x, double[] g, double[] h, int[] rows, int depth, GbtParams p, Random rng, int featureCount)
    {
        double G = 0, H = 0;
        foreach (var i in rows) { G += g[i]; H += h[i]; }
        var leafValue = -G / (H + p.Lambda);
        var node = new GbtTreeNode { Leaf = leafValue };

        if (depth >= p.MaxDepth || rows.Length < 2 * p.MinSamplesLeaf) return node;

        // Feature subsample per split.
        var features = ColumnSubsample(featureCount, p.ColSample, rng);
        var bestGain = 0.0;
        var bestFeature = -1;
        var bestThreshold = 0.0;
        var parentScore = G * G / (H + p.Lambda);

        foreach (var feat in features)
        {
            var ordered = rows.OrderBy(i => x[i][feat]).ToArray();
            double gl = 0, hl = 0;
            for (var s = 0; s < ordered.Length - 1; s++)
            {
                var i = ordered[s];
                gl += g[i]; hl += h[i];
                var leftCount = s + 1;
                var rightCount = ordered.Length - leftCount;
                if (leftCount < p.MinSamplesLeaf || rightCount < p.MinSamplesLeaf) continue;
                // Don't split between identical feature values.
                if (x[ordered[s]][feat] == x[ordered[s + 1]][feat]) continue;
                var gr = G - gl; var hr = H - hl;
                var gain = 0.5 * (gl * gl / (hl + p.Lambda) + gr * gr / (hr + p.Lambda) - parentScore) - p.Gamma;
                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestFeature = feat;
                    bestThreshold = (x[ordered[s]][feat] + x[ordered[s + 1]][feat]) / 2.0;
                }
            }
        }

        if (bestFeature < 0) return node; // no positive-gain split — stay a leaf

        var left = rows.Where(i => x[i][bestFeature] < bestThreshold).ToArray();
        var right = rows.Where(i => x[i][bestFeature] >= bestThreshold).ToArray();
        if (left.Length == 0 || right.Length == 0) return node;

        node.Feature = bestFeature;
        node.Threshold = bestThreshold;
        node.Left = BuildTree(x, g, h, left, depth + 1, p, rng, featureCount);
        node.Right = BuildTree(x, g, h, right, depth + 1, p, rng, featureCount);
        return node;
    }

    private static int[] ColumnSubsample(int featureCount, double frac, Random rng)
    {
        if (frac >= 1.0) return Enumerable.Range(0, featureCount).ToArray();
        var take = Math.Max(1, (int)(featureCount * frac));
        var all = Enumerable.Range(0, featureCount).OrderBy(_ => rng.Next()).Take(take).ToArray();
        return all.Length == 0 ? new[] { rng.Next(featureCount) } : all;
    }

    private static double Eval(GbtTreeNode node, double[] row)
    {
        while (!node.IsLeaf)
            node = row[node.Feature] < node.Threshold ? node.Left! : node.Right!;
        return node.Leaf;
    }

    /// <summary>Predicted P(up) for a feature row.</summary>
    public static double PredictProba(GbtModel model, double[] row)
    {
        var logit = model.BaseScore;
        foreach (var t in model.Trees) logit += model.LearningRate * Eval(t, row);
        return 1.0 / (1.0 + Math.Exp(-logit));
    }
}

/// <summary>Hyper-parameters for <see cref="GradientBoostedTrees"/>. Defaults are tuned conservative for a thin-edge target.</summary>
public sealed record GbtParams(
    int NEstimators = 150,
    int MaxDepth = 3,
    double LearningRate = 0.04,
    int MinSamplesLeaf = 200,
    double Subsample = 0.7,
    double ColSample = 0.7,
    double Lambda = 1.0,
    double Gamma = 0.0,
    int Seed = 1);

/// <summary>Serializable boosted-tree ensemble stored on Model.TrainedState under the "model.gbt" key.</summary>
public sealed class GbtModel
{
    [JsonPropertyName("baseScore")] public double BaseScore { get; set; }
    [JsonPropertyName("learningRate")] public double LearningRate { get; set; }
    [JsonPropertyName("featureCount")] public int FeatureCount { get; set; }
    [JsonPropertyName("trees")] public List<GbtTreeNode> Trees { get; set; } = new();
}

/// <summary>One node of a regression tree. A leaf has null children and carries <see cref="Leaf"/>.</summary>
public sealed class GbtTreeNode
{
    [JsonPropertyName("f")] public int Feature { get; set; } = -1;
    [JsonPropertyName("t")] public double Threshold { get; set; }
    [JsonPropertyName("v")] public double Leaf { get; set; }
    [JsonPropertyName("l")] public GbtTreeNode? Left { get; set; }
    [JsonPropertyName("r")] public GbtTreeNode? Right { get; set; }
    [JsonIgnore] public bool IsLeaf => Left is null || Right is null;
}
