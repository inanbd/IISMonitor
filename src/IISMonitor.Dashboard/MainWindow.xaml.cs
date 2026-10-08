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
            PromptBlockIp = prompt =>
            {
                var dialog = new BlockIpWindow(prompt) { Owner = this };
                return dialog.ShowDialog() == true ? dialog.Result : null;
            },
        };
        DataContext = _viewModel;

        OverviewCharts.SetCharts(OverviewViewModel.Charts);
        DatabaseCharts.SetCharts(DatabaseViewModel.Charts);
        PoolCharts.SetCharts(MetricCatalog.Charts(EntityKind.AppPool));
        SiteCharts.SetCharts(MetricCatalog.Charts(EntityKind.Site));
        ServerCharts.SetCharts(MetricCatalog.Charts(EntityKind.Server));
        HistoryCharts.SetCharts(MetricCatalog.Charts(EntityKind.AppPool));

        _viewModel.LiveChartsInvalidated += (_, _) => RenderLiveCharts();
        _viewModel.History.ResultChanged += (_, _) => RenderHistoryCharts();
        _viewModel.Traffic.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrafficViewModel.IsIpView) or nameof(TrafficViewModel.IsEachMinute))
                UpdateTrafficColumns();
        };
        UpdateTrafficColumns();
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
            return;
        _viewModel.IsDatabaseTabVisible = ReferenceEquals(Tabs.SelectedItem, DatabaseTab);
        _viewModel.IsTrafficTabVisible = ReferenceEquals(Tabs.SelectedItem, TrafficTab);
        RenderLiveCharts();
        if (ReferenceEquals(Tabs.SelectedItem, HistoryTab))
            _ = _viewModel.History.ReloadEntitiesAsync();
        if (_viewModel.IsDatabaseTabVisible)
            _ = _viewModel.Database.RefreshSlowAsync(force: true);
        if (_viewModel.IsTrafficTabVisible)
            _ = _viewModel.Traffic.RefreshAsync();
    }

    /// <summary>
    /// The IPs &amp; URLs grids show the columns of the chosen view: by IP (URLs per IP, Blocked) or by URL
    /// (IP addresses per URL); the detail grid shows Time only for "Each minute".
    /// </summary>
    private void UpdateTrafficColumns()
    {
        var traffic = _viewModel.Traffic;
        var byIp = traffic.IsIpView;
        TrafficIpColumn.Visibility = Show(byIp);
        TrafficUrlsColumn.Visibility = Show(byIp);
        TrafficBlockedColumn.Visibility = Show(byIp);
        TrafficUrlColumn.Visibility = Show(!byIp);
        TrafficIpsColumn.Visibility = Show(!byIp);
        TrafficDetailUrlColumn.Visibility = Show(byIp);
        TrafficDetailIpColumn.Visibility = Show(!byIp);
        TrafficTimeColumn.Visibility = Show(traffic.IsEachMinute);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Only the visible tab's charts are drawn.</summary>
    private void RenderLiveCharts()
    {
        var tab = Tabs.SelectedItem;
        if (ReferenceEquals(tab, OverviewTab))
        {
            OverviewCharts.EmptyText = _viewModel.Overview.EmptyText;
            OverviewCharts.Render(_viewModel.OverviewSeries, _viewModel.LiveRange(), _viewModel.OverviewTitle);
        }
        else if (ReferenceEquals(tab, PoolsTab))
            Render(PoolCharts, EntityKind.AppPool, _viewModel.SelectedPool?.Name);
        else if (ReferenceEquals(tab, SitesTab))
            Render(SiteCharts, EntityKind.Site, _viewModel.SelectedSite?.Name);
        else if (ReferenceEquals(tab, DatabaseTab))
        {
            DatabaseCharts.EmptyText = _viewModel.Overview.EmptyText;
            DatabaseCharts.Render(_viewModel.OverviewSeries, _viewModel.LiveRange(), _viewModel.OverviewTitle);
        }
        else if (ReferenceEquals(tab, ServerTab))
            Render(ServerCharts, EntityKind.Server, "");
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

        var pools = _viewModel.Pools.Select(p => p.Name).ToList();
        var dialog = new SettingsWindow(backend.Settings.Clone(), backend.IsStandalone, pools) { Owner = this };
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
