using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shapes;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Presentation;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using WpfColor = System.Windows.Media.Color;

namespace IISMonitor.Dashboard.Controls;

/// <summary>
/// A grid of time-series charts, one per <see cref="ChartDefinition"/>, with a hover crosshair that
/// snaps to the nearest sample and a read-out listing every series' value at that moment.
/// </summary>
public sealed class ChartPanel : UserControl
{
    private const int MaxTooltipRows = 15;

    private readonly UniformGrid _grid = new() { Columns = 2 };
    private readonly List<ChartSlot> _charts = [];
    private readonly Popup _tooltip;
    private readonly StackPanel _tooltipRows = new();
    private IReadOnlyList<ChartDefinition>? _definitions;
    private ChartSlot? _hovered;

    public ChartPanel()
    {
        Content = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        SizeChanged += (_, e) => _grid.Columns = Math.Min(MaxColumns, e.NewSize.Width switch
        {
            < 700 => 1,
            < 1400 => 2,
            _ => 3,
        });

        _tooltip = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            IsHitTestVisible = false,
            Focusable = false,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xD5, 0xDC, 0xE4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Child = _tooltipRows,
            },
        };

        // A popup is its own window and would otherwise take the mouse from the chart beneath it
        // (MouseLeave, flicker). Mark the window transparent to input once it exists.
        _tooltip.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_tooltip.Child) is HwndSource source)
            {
                var style = GetWindowLongPtr(source.Handle, GwlExStyle);
                SetWindowLongPtr(source.Handle, GwlExStyle, style | WsExTransparent);
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                EndHover();
        };
    }

    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    public double ChartHeight { get; set; } = 210;

    /// <summary>Upper limit on charts per row.</summary>
    public int MaxColumns { get; set; } = 3;

    /// <summary>Shows a legend inside each chart (off when the legend lives outside, as on the Overview tab).</summary>
    public bool ShowLegend { get; set; } = true;

    /// <summary>Mouse drag to pan/zoom. Off for live charts, which re-fit themselves on every update.</summary>
    public bool AllowZoom { get; set; }

    /// <summary>Message for a chart with nothing to draw (no series at all).</summary>
    public string? EmptyText { get; set; }

    /// <summary>Time format in the hover read-out ("T" = time only, "g" = date and time).</summary>
    public string TimeFormat { get; set; } = "T";

    /// <summary>Rebuilds the charts when the set of definitions changes.</summary>
    public void SetCharts(IReadOnlyList<ChartDefinition> definitions)
    {
        if (ReferenceEquals(definitions, _definitions))
            return;
        _definitions = definitions;

        EndHover();
        _grid.Children.Clear();
        _charts.Clear();
        foreach (var definition in definitions)
        {
            var plot = new WpfPlot { Height = ChartHeight, Margin = new Thickness(4) };
            ChartRenderer.ApplyStyle(plot.Plot);
            ConfigureInput(plot.UserInputProcessor);

            var slot = new ChartSlot(definition, plot);
            plot.MouseMove += (_, e) => OnHover(slot, e);
            plot.MouseLeave += (_, _) => EndHover();

            _grid.Children.Add(new Border
            {
                Child = plot,
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ControlLightBrush,
                Margin = new Thickness(4),
            });
            _charts.Add(slot);
        }
    }

    /// <summary>
    /// Keeps the right-click menu (save/copy image) everywhere. The mouse wheel never zooms, so it
    /// keeps scrolling the page; history charts can be panned and zoomed by dragging.
    /// </summary>
    private void ConfigureInput(UserInputProcessor input)
    {
        if (AllowZoom)
        {
            input.RemoveAll<MouseWheelZoom>();
            input.RemoveAll<DoubleClickBenchmark>();
            return;
        }

        input.UserActionResponses.Clear();
        input.UserActionResponses.Add(new SingleClickContextMenu(StandardMouseButtons.Right));
    }

    /// <summary>Redraws every chart from the series the callback returns.</summary>
    /// <param name="xRange">Fixed X range (OLE automation dates), or null to fit the data.</param>
    /// <param name="titleFor">Chart title; defaults to the definition's title.</param>
    public void Render(
        Func<ChartDefinition, IReadOnlyList<ChartSeries>> seriesFor,
        (double From, double To)? xRange,
        Func<ChartDefinition, string>? titleFor = null)
    {
        foreach (var slot in _charts)
        {
            slot.Series = seriesFor(slot.Definition);
            ChartRenderer.Draw(
                slot.Plot.Plot, slot.Definition, titleFor?.Invoke(slot.Definition) ?? slot.Definition.Title,
                slot.Series, xRange, ShowLegend, EmptyText);

            // Drawing cleared the crosshair. A still pointer gets no MouseMove while a live chart
            // scrolls under it, so re-snap to whatever sample is under the pointer now.
            slot.Crosshair = null;
            if (ReferenceEquals(slot, _hovered))
            {
                if (slot.Plot.IsMouseOver)
                {
                    HoverAt(slot, Mouse.GetPosition(slot.Plot), refresh: false);
                }
                else
                {
                    _hovered = null;
                    slot.HoverX = null;
                    _tooltip.IsOpen = false;
                }
            }

            slot.Plot.Refresh();
        }
    }

    private void OnHover(ChartSlot slot, MouseEventArgs e) => HoverAt(slot, e.GetPosition(slot.Plot), refresh: true);

    /// <param name="position">Pointer position in device-independent pixels relative to the plot.</param>
    private void HoverAt(ChartSlot slot, Point position, bool refresh)
    {
        var scale = slot.Plot.DisplayScale;
        var mouse = slot.Plot.Plot.GetCoordinates(new ScottPlot.Pixel((float)(position.X * scale), (float)(position.Y * scale)));
        var limits = slot.Plot.Plot.Axes.GetLimits();
        if (mouse.X < limits.Left || mouse.X > limits.Right)
        {
            EndHover();
            return;
        }

        // Snap to the sample time nearest the pointer, across all series.
        double? snapped = null;
        foreach (var series in slot.Series)
        {
            var index = ChartRenderer.NearestIndex(series.Xs, mouse.X);
            if (index >= 0 && (snapped is null || Math.Abs(series.Xs[index] - mouse.X) < Math.Abs(snapped.Value - mouse.X)))
                snapped = series.Xs[index];
        }

        if (snapped is not { } x)
        {
            EndHover();
            return;
        }

        if (!ReferenceEquals(_hovered, slot))
            EndHover();
        _hovered = slot;
        slot.HoverX = x;

        if (slot.Crosshair is not null)
            slot.Plot.Plot.Remove(slot.Crosshair);
        AddCrosshair(slot, x);
        if (refresh)
            slot.Plot.Refresh();

        UpdateTooltip(slot, x);
        PlaceTooltip(slot, position);
    }

    private static void AddCrosshair(ChartSlot slot, double x)
    {
        var line = slot.Plot.Plot.Add.VerticalLine(x);
        line.LineWidth = 1;
        line.Color = ChartRenderer.MutedInk;
        line.LabelText = "";
        slot.Crosshair = line;
    }

    private void UpdateTooltip(ChartSlot slot, double x)
    {
        _tooltipRows.Children.Clear();
        _tooltipRows.Children.Add(new TextBlock
        {
            Text = DateTime.FromOADate(x).ToString(TimeFormat),
            Foreground = Brushes.DimGray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // Values lead (bold), names follow; highest value first.
        var rows = slot.Series
            .Select((series, index) => (Series: series, Index: index, Value: ChartRenderer.ValueAt(series, x)))
            .Where(r => r.Value is not null)
            .OrderByDescending(r => r.Value)
            .ToList();

        foreach (var (series, index, value) in rows.Take(MaxTooltipRows))
        {
            var color = series.ColorHex ?? SeriesStyles.Palette[index % SeriesStyles.Palette.Count];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(LineKey(color, series.Pattern));
            row.Children.Add(new TextBlock
            {
                Text = MetricFormatter.Format(slot.Definition.Unit, value),
                FontWeight = FontWeights.SemiBold,
                MinWidth = 70,
                Margin = new Thickness(6, 0, 8, 0),
            });
            row.Children.Add(new TextBlock { Text = series.Label, Foreground = Brushes.DimGray });
            _tooltipRows.Children.Add(row);
        }

        if (rows.Count > MaxTooltipRows)
        {
            _tooltipRows.Children.Add(new TextBlock
            {
                Text = $"+ {rows.Count - MaxTooltipRows} more",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        if (rows.Count == 0)
            _tooltipRows.Children.Add(new TextBlock { Text = "No data at this time", Foreground = Brushes.DimGray });
    }

    /// <summary>A short stroke in the series color and pattern, like the line on the chart.</summary>
    public static FrameworkElement LineKey(string colorHex, SeriesLinePattern pattern) => new Line
    {
        X1 = 0,
        X2 = 18,
        Y1 = 0,
        Y2 = 0,
        Stroke = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(colorHex)),
        StrokeThickness = 2,
        StrokeDashArray = DashArray(pattern),
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true,
    };

    public static DoubleCollection? DashArray(SeriesLinePattern pattern) => pattern switch
    {
        SeriesLinePattern.Dashed => [3, 2],
        SeriesLinePattern.Dotted => [1, 1.5],
        SeriesLinePattern.DenselyDashed => [2, 1],
        _ => null,
    };

    private void PlaceTooltip(ChartSlot slot, Point position)
    {
        _tooltip.PlacementTarget = slot.Plot;
        _tooltip.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tooltip.Child.DesiredSize;

        // Beside the pointer, never over it: right of it, or left of it when that fits better.
        // The popup is its own window, so it may extend past the chart's edges.
        var right = position.X + 16;
        var left = position.X - 16 - size.Width;
        var useLeft = right + size.Width > slot.Plot.ActualWidth && left >= 0;
        _tooltip.HorizontalOffset = useLeft ? left : right;
        _tooltip.VerticalOffset = position.Y - size.Height / 2;
        _tooltip.IsOpen = true;
    }

    private void EndHover()
    {
        _tooltip.IsOpen = false;
        if (_hovered is not { } slot)
            return;

        _hovered = null;
        slot.HoverX = null;
        if (slot.Crosshair is not null)
        {
            slot.Plot.Plot.Remove(slot.Crosshair);
            slot.Crosshair = null;
            slot.Plot.Refresh();
        }
    }

    private sealed class ChartSlot(ChartDefinition definition, WpfPlot plot)
    {
        public ChartDefinition Definition { get; } = definition;
        public WpfPlot Plot { get; } = plot;
        public IReadOnlyList<ChartSeries> Series { get; set; } = [];
        public VerticalLine? Crosshair { get; set; }
        public double? HoverX { get; set; }
    }
}
