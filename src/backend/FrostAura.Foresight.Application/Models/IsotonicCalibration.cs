namespace FrostAura.Foresight.Application.Models;

/// <summary>
/// Isotonic regression via the pool-adjacent-violators algorithm (PAVA), used to calibrate raw
/// model probabilities against observed outcome frequencies. <see cref="Fit"/> consumes the
/// walk-forward OUT-OF-FOLD (prediction, label) pairs — already embargoed by the trainer's
/// rolling-origin layout — and returns compact breakpoints. <see cref="Apply"/> maps a raw
/// probability through those breakpoints by linear interpolation, clamped at the ends.
/// Both functions are pure and deterministic.
/// </summary>
public static class IsotonicCalibration
{
    /// <summary>
    /// Fits a monotone non-decreasing step function to (prediction, label) pairs and returns it as
    /// interpolation breakpoints: each PAVA block contributes its first/last x with the pooled
    /// block mean as y. X is strictly increasing; Y is non-decreasing.
    /// </summary>
    public static (double[] X, double[] Y) Fit(double[] predictions, int[] labels)
    {
        var n = predictions.Length;
        if (n == 0 || n != labels.Length) return (Array.Empty<double>(), Array.Empty<double>());

        var order = Enumerable.Range(0, n).OrderBy(i => predictions[i]).ToArray();

        // PAVA: walk sorted points, pooling adjacent blocks whenever monotonicity is violated.
        var value = new double[n];   // pooled mean per block
        var weight = new double[n];  // points per block
        var start = new int[n];      // first sorted-index of block
        var top = -1;
        for (var k = 0; k < n; k++)
        {
            top++;
            value[top] = labels[order[k]];
            weight[top] = 1.0;
            start[top] = k;
            while (top > 0 && value[top - 1] >= value[top])
            {
                var w = weight[top - 1] + weight[top];
                value[top - 1] = (value[top - 1] * weight[top - 1] + value[top] * weight[top]) / w;
                weight[top - 1] = w;
                top--;
            }
        }

        // Emit breakpoints: (xFirst, y) and (xLast, y) per block so interpolation is flat within a
        // block and linear between blocks. Strictly-increasing X enforced (ties keep the later,
        // larger y — preserves monotonicity when equal predictions straddle a block boundary).
        var xs = new List<double>(2 * (top + 1));
        var ys = new List<double>(2 * (top + 1));
        for (var b = 0; b <= top; b++)
        {
            var firstX = predictions[order[start[b]]];
            var lastK = b == top ? n - 1 : start[b + 1] - 1;
            var lastX = predictions[order[lastK]];
            AddPoint(xs, ys, firstX, value[b]);
            if (lastX > firstX) AddPoint(xs, ys, lastX, value[b]);
        }
        return (xs.ToArray(), ys.ToArray());
    }

    private static void AddPoint(List<double> xs, List<double> ys, double x, double y)
    {
        if (xs.Count > 0 && xs[^1] == x) { ys[^1] = Math.Max(ys[^1], y); return; }
        xs.Add(x);
        ys.Add(y);
    }

    /// <summary>
    /// Maps a raw probability through the fitted breakpoints by linear interpolation, clamped to
    /// the endpoint y values outside the fitted x range. Empty breakpoints pass p through unchanged.
    /// </summary>
    public static double Apply(double[] x, double[] y, double p)
    {
        if (x.Length == 0 || x.Length != y.Length) return p;
        if (p <= x[0]) return y[0];
        if (p >= x[^1]) return y[^1];

        var idx = Array.BinarySearch(x, p);
        if (idx >= 0) return y[idx];
        var hi = ~idx;       // first index with x[hi] > p; hi >= 1 given the clamps above
        var lo = hi - 1;
        var span = x[hi] - x[lo];
        if (span <= 0) return y[lo];
        var t = (p - x[lo]) / span;
        return y[lo] + t * (y[hi] - y[lo]);
    }
}
