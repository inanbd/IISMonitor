using System.Globalization;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Presentation;
using ScottPlot;
using ScottPlot.TickGenerators;
using ScottPlot.TickGenerators.TimeUnits;

namespace IISMonitor.Dashboard.Controls;

/// <param name="ColorHex">Series color; when null the series takes the palette slot of its position in the chart.</param>
public sealed record ChartSeries(
    string Label,
    double[] Xs,
    double[] Ys,
    string? ColorHex = null,
    SeriesLinePattern Pattern = SeriesLinePattern.Solid);

/// <summary>
/// Draws one time-series chart onto a ScottPlot <see cref="Plot"/>. Has no WPF dependency, so the
/// same drawing code can render preview images off-screen.
/// </summary>
public static class ChartRenderer
{
    public static readonly Color Ink = Color.FromHex("#0b0b0b");
    public static readonly Color MutedInk = Color.FromHex("#898781");
    public static readonly Color Gridline = Color.FromHex("#e1e0d9");

    public static void ApplyStyle(Plot plot)
    {
        var timeAxis = plot.Axes.DateTimeTicksBottom();
        if (timeAxis.TickGenerator is DateTimeAutomatic ticks)
        {
            // Decided when ticks are generated, so zooming or a sliding live window always gets
            // labels that match the current tick spacing and visible span.
            ticks.LabelFormatter = time => FormatTick(time, ticks.TimeUnit, plot.Axes.Bottom.Range.Span);
        }

        plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.Label.FontSize = 11;
        plot.Axes.Color(MutedInk);

        // Axes.Color also recolors the title; the title is text and stays in primary ink.
        plot.Axes.Title.Label.FontSize = 13;
        plot.Axes.Title.Label.ForeColor = Ink;
        plot.Grid.MajorLineColor = Gridline;
        plot.Grid.MajorLineWidth = 1;
    }

    /// <summary>
    /// A time-axis label: seconds when ticks are seconds apart, minutes otherwise, with the date
    /// added once the visible span reaches a day.
    /// </summary>
    public static string FormatTick(DateTime time, ITimeUnit? tickUnit, double visibleDays)
    {
        var culture = CultureInfo.CurrentCulture;
        var seconds = tickUnit is Second or Decisecond or Centisecond or Millisecond;
        var clock = time.ToString(seconds ? "T" : "t", culture);
        return visibleDays >= 1 ? time.ToString("MMM d", culture) + " " + clock : clock;
    }

    /// <summary>
    /// Clears and redraws the chart. Returns the divisor applied to Y values (e.g. 1024² for MB),
    /// which hover read-outs need to map the axis back to real values.
    /// </summary>
    /// <param name="xRange">Fixed X range (OLE automation dates), or null to fit the data.</param>
    /// <param name="emptyText">Message shown when there are no series at all.</param>
    public static double Draw(
        Plot plot,
        ChartDefinition definition,
        string title,
        IReadOnlyList<ChartSeries> series,
        (double From, double To)? xRange,
        bool showLegend,
        string? emptyText = null)
    {
        plot.Clear();
        plot.Title(title);

        // Colors follow the series' position in the definition, not its rank among series that
        // happen to have data, so a series never changes color when another one is empty.
        var drawable = series
            .Select((s, index) => (Series: s, Color: s.ColorHex ?? SeriesStyles.Palette[index % SeriesStyles.Palette.Count]))
            .Where(x => x.Series.Xs.Length > 0)
            .ToList();

        var max = drawable.Count == 0 ? 0 : drawable.Max(x => x.Series.Ys.Length == 0 ? 0 : x.Series.Ys.Max());
        var (divisor, unitLabel) = MetricFormatter.AxisScale(definition.Unit, max);
        plot.Axes.Left.Label.Text = unitLabel;

        foreach (var (s, color) in drawable)
        {
            var ys = divisor == 1 ? s.Ys : s.Ys.Select(y => y / divisor).ToArray();
            var scatter = plot.Add.Scatter(s.Xs, ys);
            scatter.LegendText = s.Label;
            scatter.Color = Color.FromHex(color);
            scatter.LineWidth = 2;
            scatter.LinePattern = ToScottPlot(s.Pattern);
            scatter.MarkerSize = s.Xs.Length == 1 ? 8 : 0;
        }

        if (drawable.Count == 0)
        {
            var text = plot.Add.Annotation(series.Count == 0 ? emptyText ?? "Nothing selected" : "No data yet");
            text.Alignment = Alignment.MiddleCenter;
        }

        plot.Legend.IsVisible = showLegend && drawable.Count > 1;
        plot.Legend.Alignment = Alignment.UpperLeft;

        var top = max / divisor;
        if (definition.Unit == MetricUnit.Percent)
            top = Math.Max(top, 5);
        plot.Axes.SetLimitsY(0, top <= 0 ? 1 : top * 1.15);
        var (from, to) = xRange
            ?? (drawable.Count > 0 ? (drawable.Min(x => x.Series.Xs[0]), drawable.Max(x => x.Series.Xs[^1])) : (0d, 0d));
        if (to > from)
            plot.Axes.SetLimitsX(from, to);

        return divisor;
    }

    public static LinePattern ToScottPlot(SeriesLinePattern pattern) => pattern switch
    {
        SeriesLinePattern.Dashed => LinePattern.Dashed,
        SeriesLinePattern.Dotted => LinePattern.Dotted,
        SeriesLinePattern.DenselyDashed => LinePattern.DenselyDashed,
        _ => LinePattern.Solid,
    };

    /// <summary>Index of the X closest to <paramref name="x"/> in an ascending array, or -1 when empty.</summary>
    public static int NearestIndex(double[] xs, double x)
    {
        if (xs.Length == 0)
            return -1;

        var index = Array.BinarySearch(xs, x);
        if (index >= 0)
            return index;

        index = ~index;
        if (index == 0)
            return 0;
        if (index >= xs.Length)
            return xs.Length - 1;
        return x - xs[index - 1] <= xs[index] - x ? index - 1 : index;
    }

    /// <summary>
    /// The value of a series at <paramref name="x"/>, if it has a point within half a sample step of
    /// it (series from the same snapshots share timestamps, so this lines them up).
    /// </summary>
    public static double? ValueAt(ChartSeries series, double x)
    {
        var index = NearestIndex(series.Xs, x);
        if (index < 0)
            return null;

        var step = series.Xs.Length > 1 ? (series.Xs[^1] - series.Xs[0]) / (series.Xs.Length - 1) : 0;
        var tolerance = Math.Max(step / 2, 1.0 / 86_400); // at least one second, in days
        return Math.Abs(series.Xs[index] - x) <= tolerance ? series.Ys[index] : null;
    }
}
