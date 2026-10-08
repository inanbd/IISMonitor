using System.Globalization;
using System.Windows;
using IISMonitor.Core.Settings;
using IISMonitor.Dashboard.ViewModels;

namespace IISMonitor.Dashboard;

public partial class SettingsWindow : Window
{
    private static readonly IntervalOption[] SampleOptions =
    [
        new(250, "0.25 seconds"), new(500, "0.5 seconds"), new(1000, "1 second"), new(2000, "2 seconds"),
        new(5000, "5 seconds"), new(10_000, "10 seconds"), new(30_000, "30 seconds"), new(60_000, "1 minute"),
    ];

    private static readonly IntervalOption[] HistoryOptions =
    [
        new(5, "5 seconds"), new(10, "10 seconds"), new(30, "30 seconds"), new(60, "1 minute"), new(300, "5 minutes"),
    ];

    private readonly MonitorSettings _settings;
    private readonly List<TrackedPoolChoice> _poolChoices;

    /// <param name="appPools">App pools IIS has now, offered for IP and URL tracking.</param>
    public SettingsWindow(MonitorSettings settings, bool standalone, IReadOnlyList<string> appPools)
    {
        InitializeComponent();
        _settings = settings;
        MaxHeight = SystemParameters.WorkArea.Height;

        SampleInterval.ItemsSource = SampleOptions;
        SampleInterval.SelectedItem = SampleOptions.FirstOrDefault(o => o.Milliseconds == settings.SampleIntervalMs) ?? SampleOptions[2];
        HistoryInterval.ItemsSource = HistoryOptions;
        HistoryInterval.SelectedItem = HistoryOptions.FirstOrDefault(o => o.Milliseconds == settings.HistoryIntervalSeconds) ?? HistoryOptions[1];
        RetentionDays.Text = settings.HistoryRetentionDays.ToString(CultureInfo.CurrentCulture);
        SqlPorts.Text = string.Join(", ", settings.SqlServerPorts);
        DetectLocalSql.IsChecked = settings.DetectLocalSqlServerPorts;
        SqlConnectionStrings.Text = string.Join(Environment.NewLine, settings.SqlServerConnectionStrings);
        SlowThreshold.Text = settings.SlowQueryThresholdSeconds.ToString("0.##", CultureInfo.CurrentCulture);
        KernelTracing.IsChecked = settings.EnableKernelTracing;
        ResponseTracing.IsChecked = settings.EnableResponseTimeTracing;
        StandaloneNote.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;

        // Every pool IIS has, plus tracked pools that are gone (so they can be unticked).
        var tracked = new HashSet<string>(settings.RequestTrackingPools, StringComparer.OrdinalIgnoreCase);
        var current = new HashSet<string>(appPools, StringComparer.OrdinalIgnoreCase);
        _poolChoices = current
            .Select(pool => new TrackedPoolChoice(pool, pool, tracked.Contains(pool)))
            .Concat(tracked.Where(pool => !current.Contains(pool)).Select(pool => new TrackedPoolChoice(pool, pool + " (not in IIS now)", true)))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TrackedPools.ItemsSource = _poolChoices;
        NoPools.Visibility = _poolChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RequestRetentionDays.Text = settings.RequestLogRetentionDays.ToString(CultureInfo.CurrentCulture);
    }

    public MonitorSettings? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionDays.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var days)
            || days < 1 || days > MonitorSettings.MaxRetentionDays)
        {
            ShowError($"Keep history for: enter a number of days from 1 to {MonitorSettings.MaxRetentionDays}.");
            return;
        }

        if (!double.TryParse(SlowThreshold.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var slowSeconds)
            || slowSeconds < 0.5 || slowSeconds > 3600)
        {
            ShowError("Slow query: enter a number of seconds from 0.5 to 3600.");
            return;
        }

        if (!int.TryParse(RequestRetentionDays.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var requestDays)
            || requestDays < 1 || requestDays > MonitorSettings.MaxRequestLogRetentionDays)
        {
            ShowError($"Keep recorded requests for: enter a number of days from 1 to {MonitorSettings.MaxRequestLogRetentionDays}.");
            return;
        }

        var trackedPools = _poolChoices.Where(choice => choice.IsChecked).Select(choice => choice.Name).ToList();
        if (trackedPools.Count > 0 && ResponseTracing.IsChecked != true)
        {
            ShowError("IP and URL tracking uses the IIS ETW log stream: also tick Live response times.");
            return;
        }

        var ports = new List<int>();
        foreach (var part in SqlPorts.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                ShowError($"\"{part}\" is not a valid port number.");
                return;
            }

            ports.Add(port);
        }

        var result = _settings.Clone();
        result.SampleIntervalMs = ((IntervalOption)SampleInterval.SelectedItem).Milliseconds;
        result.HistoryIntervalSeconds = ((IntervalOption)HistoryInterval.SelectedItem).Milliseconds;
        result.HistoryRetentionDays = days;
        result.SqlServerPorts = ports;
        result.DetectLocalSqlServerPorts = DetectLocalSql.IsChecked == true;
        result.SqlServerConnectionStrings = SqlConnectionStrings.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        result.SlowQueryThresholdSeconds = slowSeconds;
        result.EnableKernelTracing = KernelTracing.IsChecked == true;
        result.EnableResponseTimeTracing = ResponseTracing.IsChecked == true;
        result.RequestTrackingPools = trackedPools;
        result.RequestLogRetentionDays = requestDays;

        Result = result;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}

/// <summary>An app pool in the settings' IP and URL tracking list.</summary>
public sealed class TrackedPoolChoice(string name, string label, bool isChecked)
{
    public string Name { get; } = name;
    public string Label { get; } = label;
    public bool IsChecked { get; set; } = isChecked;
}
