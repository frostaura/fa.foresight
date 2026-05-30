using ScottPlot;

namespace FrostAura.Foresight.Infrastructure.Charts;

public enum ChartDotState { None, Hit, Miss, Open }

public sealed record ChartCandle(long OpenTime, double Open, double High, double Low, double Close, ChartDotState Dot);
public sealed record ChartBalance(long OpenTime, double Balance);

/// <summary>What the renderer needs to draw one snapshot of the live chart.</summary>
public sealed record ChartModel(
    IReadOnlyList<ChartCandle> Candles,
    IReadOnlyList<ChartBalance> Balance,
    /// <summary>The session's ALL-TIME peak balance (across the whole run, not just in-view candles).
    /// Drawn as the dotted horizontal line so drawdown + recovery from the peak are always visible.</summary>
    double? MaxBalance,
    long IntervalMs);

/// <summary>
/// Server-side re-draw of the Foresight live chart (ScottPlot → PNG): minimalist dark theme,
/// green/red candles, prediction hit/miss dots along the top, the bank-balance overlay line with a
/// translucent fill below it, and a dotted line at the session's all-time peak balance. No axes,
/// titles, gridlines or tick labels — just the chart. Crisp at the requested pixel size.
/// </summary>
public static class CandlestickChartRenderer
{
    private static readonly Color Bg = Color.FromHex("#0B1220");
    private static readonly Color Up = Color.FromHex("#6CC58F");
    private static readonly Color Down = Color.FromHex("#F08484");
    private static readonly Color Bal = Color.FromHex("#A4D4F4");
    private static readonly Color Hit = Color.FromHex("#34D399");
    private static readonly Color Miss = Color.FromHex("#F87171");
    private static readonly Color Pend = Color.FromHex("#E8C26A");

    public static byte[] Render(ChartModel m, int width = 1600, int height = 720)
    {
        var plot = new Plot();
        plot.FigureBackground.Color = Bg;
        plot.DataBackground.Color = Bg;

        var candles = m.Candles;
        int n = candles.Count;
        if (n == 0) return plot.GetImage(width, height).GetImageBytes();

        // ── Candles: manual wicks + bodies at integer x (0..n-1) ──────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            var c = candles[i];
            var color = c.Close >= c.Open ? Up : Down;

            var wick = plot.Add.Line(i, c.Low, i, c.High);
            wick.Color = color;
            wick.LineWidth = 1.6f;

            double bodyLo = Math.Min(c.Open, c.Close);
            double bodyHi = Math.Max(c.Open, c.Close);
            if (bodyHi - bodyLo < (c.High - c.Low) * 0.02 + 1e-9)
                bodyHi = bodyLo + Math.Max((c.High - c.Low) * 0.04, 1e-6); // give dojis a sliver of height

            var body = plot.Add.Rectangle(i - 0.30, i + 0.30, bodyLo, bodyHi);
            body.FillColor = color;
            body.LineColor = color;
        }

        double lo = candles.Min(c => c.Low);
        double hi = candles.Max(c => c.High);
        double range = Math.Max(hi - lo, 1e-6);
        double dotY = hi + range * 0.07;
        plot.Axes.SetLimitsY(lo - range * 0.06, hi + range * 0.13);
        plot.Axes.SetLimitsX(-0.7, n - 0.3);

        // ── Prediction dots along the top ─────────────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            var d = candles[i].Dot;
            if (d == ChartDotState.None) continue;
            var col = d == ChartDotState.Hit ? Hit : d == ChartDotState.Miss ? Miss : Pend;
            var mk = plot.Add.Marker(i, dotY, MarkerShape.FilledCircle, 13, col);
            mk.MarkerLineColor = Bg;
            mk.MarkerLineWidth = 2;
        }

        // ── Balance overlay (hidden right axis): translucent fill + line + dotted all-time-peak line ─
        var rax = plot.Axes.AddRightAxis();
        rax.IsVisible = false;
        if (m.Balance.Count > 1)
        {
            var idxByTime = new Dictionary<long, int>();
            for (int i = 0; i < n; i++) idxByTime[candles[i].OpenTime] = i;

            var bx = new List<double>();
            var by = new List<double>();
            foreach (var b in m.Balance)
                if (idxByTime.TryGetValue(b.OpenTime, out var xi)) { bx.Add(xi); by.Add(b.Balance); }

            if (by.Count > 1)
            {
                double bmin = by.Min();
                double bmax = by.Max();
                double peak = m.MaxBalance is double mb ? Math.Max(mb, bmax) : bmax;
                double top = peak;
                double pad = Math.Max(top - bmin, 1) * 0.16;
                double bottom = bmin - pad * 2.2;          // headroom below for the fill to read
                plot.Axes.SetLimitsY(bottom, top + pad * 0.5, rax);

                var line = plot.Add.Scatter(bx.ToArray(), by.ToArray());
                line.Color = Bal;
                line.LineWidth = 3;
                line.MarkerSize = 0;
                line.Axes.YAxis = rax;
                line.FillY = true;
                line.FillYColor = Colors.White.WithAlpha(18);  // translucent bottom-fill
                line.FillYValue = bottom;

                if (m.MaxBalance is double maxLine)
                {
                    var hl = plot.Add.HorizontalLine(Math.Max(maxLine, bmax));
                    hl.Axes.YAxis = rax;
                    hl.Color = Bal.WithAlpha(0.40);
                    hl.LinePattern = LinePattern.Dotted;
                    hl.LineWidth = 1.6f;
                }
            }
        }

        // ── Strip everything else: no axes, ticks, labels, title or gridlines ─────────────────────
        plot.Axes.Frameless();
        plot.HideGrid();

        return plot.GetImage(width, height).GetImageBytes();
    }
}
