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

    public SettingsWindow(MonitorSettings settings, bool standalone)
    {
        InitializeComponent();
        _settings = settings;

        SampleInterval.ItemsSource = SampleOptions;
        SampleInterval.SelectedItem = SampleOptions.FirstOrDefault(o => o.Milliseconds == settings.SampleIntervalMs) ?? SampleOptions[2];
        HistoryInterval.ItemsSource = HistoryOptions;
        HistoryInterval.SelectedItem = HistoryOptions.FirstOrDefault(o => o.Milliseconds == settings.HistoryIntervalSeconds) ?? HistoryOptions[1];
        RetentionDays.Text = settings.HistoryRetentionDays.ToString(CultureInfo.CurrentCulture);
        SqlPorts.Text = string.Join(", ", settings.SqlServerPorts);
        DetectLocalSql.IsChecked = settings.DetectLocalSqlServerPorts;
        SqlConnectionStrings.Text = string.Join(Environment.NewLine, settings.SqlServerConnectionStrings);
        KernelTracing.IsChecked = settings.EnableKernelTracing;
        ResponseTracing.IsChecked = settings.EnableResponseTimeTracing;
        StandaloneNote.Visibility = standalone ? Visibility.Visible : Visibility.Collapsed;
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
        result.EnableKernelTracing = KernelTracing.IsChecked == true;
        result.EnableResponseTimeTracing = ResponseTracing.IsChecked == true;

        Result = result;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}
