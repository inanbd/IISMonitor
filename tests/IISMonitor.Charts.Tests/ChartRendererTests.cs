using System.Globalization;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Presentation;
using IISMonitor.Dashboard.Controls;
using ScottPlot;
using ScottPlot.Plottables;

namespace IISMonitor.Charts.Tests;

public class ChartRendererTests
{
    private static readonly ChartDefinition Cpu = new("CPU", MetricUnit.Percent, ["cpu"]);
    private static readonly ChartDefinition Memory = new("RAM", MetricUnit.Bytes, ["working_set"]);

    private static ChartSeries Series(string label, int points, double value, string? color = null, SeriesLinePattern pattern = SeriesLinePattern.Solid)
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0).ToOADate();
        var xs = Enumerable.Range(0, points).Select(i => start + i / 86_400.0).ToArray();
        var ys = Enumerable.Range(0, points).Select(i => value + i % 3).ToArray();
        return new ChartSeries(label, xs, ys, color, pattern);
    }

    [Fact]
    public void Draws_one_line_per_series_with_its_own_color_and_pattern()
    {
        var plot = new Plot();
        ChartRenderer.ApplyStyle(plot);
        ChartRenderer.Draw(plot, Cpu, "CPU: 12 % total",
        [
            Series("Shop", 60, 10, "#2a78d6"),
            Series("Api", 60, 20, "#2a78d6", SeriesLinePattern.Dashed),
        ], null, showLegend: false);

        var lines = plot.GetPlottables<Scatter>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Shop", lines[0].LegendText);
        Assert.Equal(LinePattern.Dashed.Name, lines[1].LinePattern.Name);
        Assert.Equal(2, lines[0].LineWidth);
        Assert.False(plot.Legend.IsVisible);
        Assert.Equal("CPU: 12 % total", plot.Axes.Title.Label.Text);
    }

    [Fact]
    public void Default_colors_follow_series_position_even_when_one_is_empty()
    {
        var plot = new Plot();
        ChartRenderer.Draw(plot, Cpu, "CPU",
        [
            new ChartSeries("empty", [], []),
            Series("second", 10, 5),
        ], null, showLegend: true);

        var line = Assert.Single(plot.GetPlottables<Scatter>());
        Assert.Equal(Color.FromHex(SeriesStyles.Palette[1]).ToHex(), line.Color.ToHex());
    }

    [Fact]
    public void Scales_byte_axes_and_keeps_percent_charts_readable()
    {
        var plot = new Plot();
        var divisor = ChartRenderer.Draw(plot, Memory, "RAM", [Series("Shop", 5, 3.0 * 1024 * 1024 * 1024)], null, true);
        Assert.Equal(1024d * 1024 * 1024, divisor);
        Assert.Equal("GB", plot.Axes.Left.Label.Text);

        var idle = new Plot();
        ChartRenderer.Draw(idle, Cpu, "CPU", [Series("Idle", 5, 0)], null, true);
        Assert.True(idle.Axes.GetLimits().Top >= 5);
    }

    [Fact]
    public void Shows_a_message_instead_of_an_empty_chart()
    {
        var nothingTicked = new Plot();
        ChartRenderer.Draw(nothingTicked, Cpu, "CPU", [], null, true);
        Assert.Contains(nothingTicked.GetPlottables<Annotation>(), a => a.Text == "Nothing selected");

        var noData = new Plot();
        ChartRenderer.Draw(noData, Cpu, "CPU", [new ChartSeries("Shop", [], [])], null, true);
        Assert.Contains(noData.GetPlottables<Annotation>(), a => a.Text == "No data yet");
    }

    [Fact]
    public void Renders_to_an_image_without_errors()
    {
        var plot = new Plot();
        ChartRenderer.ApplyStyle(plot);
        var series = Enumerable.Range(0, 12)
            .Select(i => Series($"Pool{i}", 300, i * 3, SeriesStyles.ForSlot(i).ColorHex, SeriesStyles.ForSlot(i).Pattern))
            .ToList();
        ChartRenderer.Draw(plot, Cpu, "CPU", series, (series[0].Xs[0], series[0].Xs[^1]), showLegend: false);

        var png = plot.GetImageBytes(800, 300, ImageFormat.Png);
        Assert.True(png.Length > 1000);
    }

    [Fact]
    public void Title_stays_in_primary_ink_and_time_labels_fit_the_span()
    {
        var plot = new Plot();
        ChartRenderer.ApplyStyle(plot);
        Assert.Equal(ChartRenderer.Ink.ToHex(), plot.Axes.Title.Label.ForeColor.ToHex());

        var noon = new DateTime(2026, 10, 6, 12, 34, 56);
        var culture = CultureInfo.CurrentCulture;
        Assert.Equal(noon.ToString("T", culture), ChartRenderer.TimeLabelFormat(5.0 / 1440)(noon));
        Assert.Equal(noon.ToString("t", culture), ChartRenderer.TimeLabelFormat(6.0 / 24)(noon));
        Assert.StartsWith(noon.ToString("MMM d", culture), ChartRenderer.TimeLabelFormat(7)(noon));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.4, 1)]
    [InlineData(1.6, 2)]
    [InlineData(99, 3)]
    [InlineData(-5, 0)]
    public void Finds_the_nearest_sample(double x, int expected) =>
        Assert.Equal(expected, ChartRenderer.NearestIndex([0, 1, 2, 3], x));

    [Fact]
    public void Reads_a_series_value_only_near_one_of_its_samples()
    {
        var series = Series("Shop", 10, 1);
        Assert.Equal(series.Ys[4], ChartRenderer.ValueAt(series, series.Xs[4]));
        Assert.Equal(series.Ys[4], ChartRenderer.ValueAt(series, series.Xs[4] + 0.4 / 86_400));
        Assert.Null(ChartRenderer.ValueAt(series, series.Xs[^1] + 60.0 / 86_400));
        Assert.Null(ChartRenderer.ValueAt(new ChartSeries("empty", [], []), 1));
    }
}
