using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IISMonitor.Core.Metrics;
using ScottPlot;
using ScottPlot.WPF;

namespace IISMonitor.Dashboard.Controls;

public sealed record ChartSeries(string Label, double[] Xs, double[] Ys);

/// <summary>A grid of time-series charts, one per <see cref="ChartDefinition"/>.</summary>
public sealed class ChartPanel : UserControl
{
    private readonly UniformGrid _grid = new() { Columns = 2 };
    private readonly List<(ChartDefinition Definition, WpfPlot Plot)> _charts = [];
    private IReadOnlyList<ChartDefinition>? _definitions;

    public ChartPanel()
    {
        Content = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        SizeChanged += (_, e) => _grid.Columns = e.NewSize.Width switch
        {
            < 700 => 1,
            < 1400 => 2,
            _ => 3,
        };
    }

    public double ChartHeight { get; set; } = 210;

    /// <summary>Rebuilds the charts when the set of definitions changes.</summary>
    public void SetCharts(IReadOnlyList<ChartDefinition> definitions)
    {
        if (ReferenceEquals(definitions, _definitions))
            return;
        _definitions = definitions;

        _grid.Children.Clear();
        _charts.Clear();
        foreach (var definition in definitions)
        {
            var plot = new WpfPlot { Height = ChartHeight, Margin = new Thickness(4) };
            ApplyStyle(plot.Plot, definition.Title);
            _grid.Children.Add(new Border
            {
                Child = plot,
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ControlLightBrush,
                Margin = new Thickness(4),
            });
            _charts.Add((definition, plot));
        }
    }

    /// <summary>Redraws every chart from the series the callback returns.</summary>
    /// <param name="xRange">Fixed X range (OLE automation dates), or null to fit the data.</param>
    public void Render(Func<ChartDefinition, IReadOnlyList<ChartSeries>> seriesFor, (double From, double To)? xRange)
    {
        foreach (var (definition, wpfPlot) in _charts)
        {
            var plot = wpfPlot.Plot;
            plot.Clear();

            var series = seriesFor(definition).Where(s => s.Xs.Length > 0).ToList();
            var max = series.Count == 0 ? 0 : series.Max(s => s.Ys.Length == 0 ? 0 : s.Ys.Max());
            var (divisor, unitLabel) = MetricFormatter.AxisScale(definition.Unit, max);
            plot.Axes.Left.Label.Text = unitLabel;

            foreach (var s in series)
            {
                var ys = divisor == 1 ? s.Ys : s.Ys.Select(y => y / divisor).ToArray();
                var scatter = plot.Add.Scatter(s.Xs, ys);
                scatter.LegendText = s.Label;
                scatter.MarkerSize = s.Xs.Length == 1 ? 5 : 0;
                scatter.LineWidth = 1.5f;
            }

            if (series.Count == 0)
            {
                var text = plot.Add.Annotation("No data");
                text.Alignment = Alignment.MiddleCenter;
            }

            plot.Legend.IsVisible = series.Count > 1;
            plot.Legend.Alignment = Alignment.UpperLeft;

            var top = max / divisor;
            if (definition.Unit == MetricUnit.Percent)
                top = Math.Max(top, 5);
            plot.Axes.SetLimitsY(0, top <= 0 ? 1 : top * 1.15);
            if (xRange is { } range)
                plot.Axes.SetLimitsX(range.From, range.To);
            else if (series.Count > 0)
                plot.Axes.SetLimitsX(series.Min(s => s.Xs[0]), series.Max(s => s.Xs[^1]));

            wpfPlot.Refresh();
        }
    }

    private static void ApplyStyle(Plot plot, string title)
    {
        plot.Title(title);
        plot.Axes.Title.Label.FontSize = 13;
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.TickLabelStyle.FontSize = 10;
        plot.Axes.Left.Label.FontSize = 11;
        plot.Grid.MajorLineColor = Colors.Black.WithAlpha(0.08);
    }
}
