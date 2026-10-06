using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IISMonitor.Core.Metrics;
using IISMonitor.Dashboard.ViewModels;

namespace IISMonitor.Dashboard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(Dispatcher)
        {
            Confirm = (title, message) =>
                MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK,
            ShowMessage = (title, message, isError) =>
                MessageBox.Show(this, message, title, MessageBoxButton.OK, isError ? MessageBoxImage.Warning : MessageBoxImage.Information),
        };
        DataContext = _viewModel;

        PoolCharts.SetCharts(MetricCatalog.Charts(EntityKind.AppPool));
        SiteCharts.SetCharts(MetricCatalog.Charts(EntityKind.Site));
        ServerCharts.SetCharts(MetricCatalog.Charts(EntityKind.Server));
        HistoryCharts.SetCharts(MetricCatalog.Charts(EntityKind.AppPool));

        _viewModel.LiveChartsInvalidated += (_, _) => RenderLiveCharts();
        _viewModel.History.ResultChanged += (_, _) => RenderHistoryCharts();
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
            return;
        RenderLiveCharts();
        if (Tabs.SelectedIndex == 3)
            _ = _viewModel.History.ReloadEntitiesAsync();
    }

    /// <summary>Only the visible tab's charts are drawn.</summary>
    private void RenderLiveCharts()
    {
        switch (Tabs.SelectedIndex)
        {
            case 0:
                Render(PoolCharts, EntityKind.AppPool, _viewModel.SelectedPool?.Name);
                break;
            case 1:
                Render(SiteCharts, EntityKind.Site, _viewModel.SelectedSite?.Name);
                break;
            case 2:
                Render(ServerCharts, EntityKind.Server, "");
                break;
        }
    }

    private void Render(Controls.ChartPanel panel, EntityKind kind, string? name)
    {
        if (name is null)
        {
            panel.Visibility = Visibility.Hidden;
            return;
        }

        panel.Visibility = Visibility.Visible;
        panel.Render(chart => _viewModel.LiveSeries(kind, name, chart), _viewModel.LiveRange());
    }

    private void RenderHistoryCharts()
    {
        var history = _viewModel.History;
        HistoryCharts.SetCharts(MetricCatalog.Charts(history.Result?.Kind ?? history.SelectedKind.Kind));
        HistoryCharts.Render(history.Series, history.Range());
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Backend is not { } backend)
            return;

        var dialog = new SettingsWindow(backend.Settings.Clone(), backend.IsStandalone) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings)
            return;

        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _viewModel.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Saving settings failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing)
            return;

        // Stop the collector (standalone mode) or disconnect cleanly before the window goes away.
        e.Cancel = true;
        _closing = true;
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            Close();
        }
    }
}
