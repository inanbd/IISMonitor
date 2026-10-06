using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Windows.Input;
using System.Windows.Threading;
using IISMonitor.Collectors;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.Settings;
using IISMonitor.Dashboard.Controls;
using IISMonitor.Dashboard.Services;

namespace IISMonitor.Dashboard.ViewModels;

public sealed record IntervalOption(int Milliseconds, string Label);

public sealed record WindowOption(TimeSpan Window, string Label);

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const string ServiceName = "IISMonitor";

    private readonly Dispatcher _dispatcher;
    private readonly LiveSeriesStore _live = new();
    private readonly object _snapshotGate = new();
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private IMonitorBackend? _backend;
    private MonitorSnapshot? _pending;
    private MonitorSnapshot? _latest;
    private int _uiScheduled;
    private bool _syncingSettings;
    private bool _disposed;

    private string _machineName = Environment.MachineName;
    private string _sourceText = "Connecting…";
    private string _statusText = "Waiting for the first sample…";
    private string? _bannerText;
    private bool _bannerIsWarning;
    private bool _canStartService;
    private bool _canEnableResponseTimes;
    private bool _isPaused;
    private IntervalOption _selectedInterval;
    private WindowOption _selectedWindow;
    private AppPoolRow? _selectedPool;
    private SiteRow? _selectedSite;
    private string _serverCpuText = MetricFormatter.Missing;
    private string _memoryText = MetricFormatter.Missing;
    private string _requestsText = MetricFormatter.Missing;
    private string _activeRequestsText = MetricFormatter.Missing;
    private string _dbText = MetricFormatter.Missing;
    private string _responseText = MetricFormatter.Missing;
    private string _selectedPoolHeader = "Select an app pool to see its processes and charts.";
    private string _selectedSiteHeader = "Select a site to see its charts.";

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        IntervalOptions =
        [
            new(250, "0.25 seconds"), new(500, "0.5 seconds"), new(1000, "1 second"), new(2000, "2 seconds"), new(5000, "5 seconds"),
            new(10_000, "10 seconds"), new(30_000, "30 seconds"), new(60_000, "1 minute"),
        ];
        WindowOptions =
        [
            new(TimeSpan.FromMinutes(2), "Last 2 minutes"), new(TimeSpan.FromMinutes(5), "Last 5 minutes"),
            new(TimeSpan.FromMinutes(15), "Last 15 minutes"), new(TimeSpan.FromMinutes(30), "Last 30 minutes"),
            new(TimeSpan.FromHours(1), "Last hour"),
        ];
        _selectedInterval = IntervalOptions[2];
        _selectedWindow = WindowOptions[1];

        History = new HistoryViewModel(() => _backend, ReportError);
        StartServiceCommand = new AsyncCommand(StartServiceAsync, ReportError, () => CanStartService);
        EnableResponseTimesCommand = new AsyncCommand(EnableResponseTimesAsync, ReportError, () => _backend is not null);
        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
    }

    /// <summary>Asks the user to confirm (title, message). Set by the window.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => true;

    /// <summary>Shows a message (title, message, isError). Set by the window.</summary>
    public Action<string, string, bool> ShowMessage { get; set; } = (_, _, _) => { };

    /// <summary>Raised on the UI thread whenever live charts should be redrawn.</summary>
    public event EventHandler? LiveChartsInvalidated;

    public IMonitorBackend? Backend => _backend;

    public ObservableCollection<AppPoolRow> Pools { get; } = [];
    public ObservableCollection<SiteRow> Sites { get; } = [];
    public ObservableCollection<ProcessRow> SelectedPoolProcesses { get; } = [];
    public ObservableCollection<HealthRow> Health { get; } = [];
    public HistoryViewModel History { get; }

    public IReadOnlyList<IntervalOption> IntervalOptions { get; }
    public IReadOnlyList<WindowOption> WindowOptions { get; }

    public ICommand StartServiceCommand { get; }
    public ICommand EnableResponseTimesCommand { get; }
    public ICommand TogglePauseCommand { get; }

    public string MachineName { get => _machineName; private set => Set(ref _machineName, value); }
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string? BannerText { get => _bannerText; private set { Set(ref _bannerText, value); Raise(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(BannerText);
    public bool BannerIsWarning { get => _bannerIsWarning; private set => Set(ref _bannerIsWarning, value); }
    public bool CanStartService { get => _canStartService; private set => Set(ref _canStartService, value); }
    public bool CanEnableResponseTimes { get => _canEnableResponseTimes; private set => Set(ref _canEnableResponseTimes, value); }
    public bool IsStandalone => _backend?.IsStandalone == true;

    public string ServerCpuText { get => _serverCpuText; private set => Set(ref _serverCpuText, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string RequestsText { get => _requestsText; private set => Set(ref _requestsText, value); }
    public string ActiveRequestsText { get => _activeRequestsText; private set => Set(ref _activeRequestsText, value); }
    public string DbText { get => _dbText; private set => Set(ref _dbText, value); }
    public string ResponseText { get => _responseText; private set => Set(ref _responseText, value); }
    public string SelectedPoolHeader { get => _selectedPoolHeader; private set => Set(ref _selectedPoolHeader, value); }
    public string SelectedSiteHeader { get => _selectedSiteHeader; private set => Set(ref _selectedSiteHeader, value); }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (!Set(ref _isPaused, value))
                return;
            Raise(nameof(PauseLabel));
            if (!value && _latest is { } latest)
                ApplySnapshot(latest);
        }
    }

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public IntervalOption SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (value is null || !Set(ref _selectedInterval, value) || _syncingSettings || _backend is null)
                return;
            _ = ChangeIntervalAsync(value.Milliseconds);
        }
    }

    public WindowOption SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (value is not null && Set(ref _selectedWindow, value))
                LiveChartsInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    public AppPoolRow? SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (!Set(ref _selectedPool, value))
                return;
            RefreshSelectedPool();
            LiveChartsInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    public SiteRow? SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (!Set(ref _selectedSite, value))
                return;
            RefreshSelectedSite();
            LiveChartsInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task InitializeAsync()
    {
        BannerText = "Connecting to the IISMonitor service…";
        BannerIsWarning = false;
        if (await TryConnectServiceAsync(TimeSpan.FromSeconds(2)))
            return;
        StartStandalone();
    }

    /// <summary>Series for one live chart of the given entity, inside the selected time window.</summary>
    public IReadOnlyList<ChartSeries> LiveSeries(EntityKind kind, string name, ChartDefinition chart)
    {
        var from = DateTime.Now.Subtract(SelectedWindow.Window).ToOADate();
        var result = new List<ChartSeries>();
        foreach (var key in chart.MetricKeys)
        {
            var metric = MetricCatalog.Find(kind, key);
            if (metric is null || metric.HistoryOnly)
                continue;
            var (xs, ys) = _live.Get(kind, name, key, from);
            result.Add(new ChartSeries(metric.DisplayName, xs, ys));
        }

        return result;
    }

    public (double From, double To) LiveRange()
    {
        var now = DateTime.Now;
        return (now.Subtract(SelectedWindow.Window).ToOADate(), now.ToOADate());
    }

    public async Task SaveSettingsAsync(MonitorSettings settings)
    {
        if (_backend is null)
            throw new InvalidOperationException("Not connected to a collector.");
        var saved = await _backend.UpdateSettingsAsync(settings);
        OnSettingsChanged(saved);
    }

    private async Task<bool> TryConnectServiceAsync(TimeSpan timeout)
    {
        try
        {
            var client = await PipeClientBackend.TryConnectAsync(timeout);
            if (client is null)
                return false;
            await UseBackendAsync(client);
            BannerText = null;
            return true;
        }
        catch (Exception e)
        {
            BannerText = "Could not connect to the IISMonitor service: " + e.Message;
            BannerIsWarning = true;
            return false;
        }
    }

    private void StartStandalone()
    {
        var engine = new MonitorEngine(new MonitorEngineOptions
        {
            EtwSessionPrefix = "IISMonitor-Dashboard",
            Log = (message, exception) => Trace.TraceWarning("{0} {1}", message, exception),
        });
        engine.Start();
        _ = UseBackendAsync(new InProcessBackend(engine, engine));
        UpdateStandaloneBanner();
    }

    private void UpdateStandaloneBanner()
    {
        var status = GetServiceStatus();
        CanStartService = status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused;
        var reason = status is null ? "is not installed" : $"is {status.Value.ToString().ToLowerInvariant()}";
        BannerText = $"Standalone mode: the IISMonitor service {reason}, so this window collects the data itself. " +
                     "History is only recorded while the window is open.";
        BannerIsWarning = true;
    }

    private async Task UseBackendAsync(IMonitorBackend backend)
    {
        var old = _backend;
        lock (_snapshotGate)
        {
            _backend = backend;
            _lastSnapshotUtc = DateTime.MinValue;
        }

        backend.SnapshotReceived += OnSnapshotReceived;
        backend.SettingsChanged += OnBackendSettingsChanged;
        backend.Disconnected += OnDisconnected;
        backend.Start();
        SourceText = backend.Description;
        Raise(nameof(IsStandalone));
        OnSettingsChanged(backend.Settings);

        if (old is not null)
        {
            old.SnapshotReceived -= OnSnapshotReceived;
            old.SettingsChanged -= OnBackendSettingsChanged;
            old.Disconnected -= OnDisconnected;
            await old.DisposeAsync();
        }

        await History.ReloadEntitiesAsync();
    }

    private void OnSnapshotReceived(object? sender, MonitorSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            // Ignore other backends and replays of a snapshot that was already handled.
            if (!ReferenceEquals(sender, _backend) || snapshot.TimestampUtc <= _lastSnapshotUtc)
                return;
            _lastSnapshotUtc = snapshot.TimestampUtc;
            _live.Add(snapshot);
        }

        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _uiScheduled, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPending);
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _uiScheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is null || _disposed)
            return;

        _latest = snapshot;
        if (!IsPaused)
            ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        MachineName = snapshot.Server.MachineName;
        Sync(Pools, snapshot.AppPools, p => p.Name, name => new AppPoolRow(name), (row, m) => row.Update(m));
        Sync(Sites, snapshot.Sites, s => s.Name, name => new SiteRow(name), (row, m) => row.Update(m));
        RefreshSelectedPool();
        RefreshSelectedSite();
        UpdateHealth(snapshot.Health);
        UpdateTiles(snapshot);

        CanEnableResponseTimes = snapshot.Sites.Any(s => !s.EtwLoggingEnabled) && (_backend?.Settings.EnableResponseTimeTracing ?? false);
        var warnings = snapshot.Health.Count(h => !h.Ok);
        StatusText = $"Updated {snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} · every {SelectedInterval.Label}" +
                     (warnings > 0 ? $" · {warnings} collector warning(s), see the Collectors tab" : "");
        LiveChartsInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static void Sync<TRow, TModel>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TModel> models,
        Func<TModel, string> key,
        Func<string, TRow> create,
        Action<TRow, TModel> update)
        where TRow : class
    {
        var byKey = models.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var nameOf = (TRow row) => row switch
        {
            AppPoolRow p => p.Name,
            SiteRow s => s.Name,
            _ => "",
        };

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!byKey.ContainsKey(nameOf(rows[i])))
                rows.RemoveAt(i);
        }

        var existing = rows.ToDictionary(nameOf, StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (!existing.TryGetValue(key(model), out var row))
            {
                row = create(key(model));
                rows.Add(row);
            }

            update(row, model);
        }
    }

    private void RefreshSelectedPool()
    {
        SelectedPoolProcesses.Clear();
        if (SelectedPool is not { } pool)
        {
            SelectedPoolHeader = "Select an app pool to see its processes and charts.";
            return;
        }

        foreach (var process in pool.Metrics.Processes)
            SelectedPoolProcesses.Add(new ProcessRow(process));

        var apps = pool.Metrics.Applications.Count == 0 ? "no applications" : pool.ApplicationsText;
        SelectedPoolHeader = $"{pool.Name} — {pool.State} · {pool.Runtime} · {pool.Identity} · {apps}";
    }

    private void RefreshSelectedSite()
    {
        if (SelectedSite is not { } site)
        {
            SelectedSiteHeader = "Select a site to see its charts.";
            return;
        }

        var tracing = site.Metrics.EtwLoggingEnabled ? "response times traced" : "response times off (ETW logging not enabled)";
        SelectedSiteHeader = $"{site.Name} (ID {site.Id}) — {site.State} · pool {site.AppPool} · {site.BindingsText} · {tracing}";
    }

    private void UpdateHealth(List<CollectorHealth> health)
    {
        var rows = health.Select(h => new HealthRow(h.Name, h.Ok, h.Message)).ToList();
        if (rows.SequenceEqual(Health))
            return;
        Health.Clear();
        foreach (var row in rows)
            Health.Add(row);
    }

    private void UpdateTiles(MonitorSnapshot snapshot)
    {
        var server = snapshot.Server;
        ServerCpuText = MetricFormatter.Format(MetricUnit.Percent, server.CpuPercent);
        MemoryText = server.MemoryTotalBytes is { } total
            ? $"{MetricFormatter.Format(MetricUnit.Bytes, server.MemoryUsedBytes)} of {MetricFormatter.Format(MetricUnit.Bytes, total)}"
            : MetricFormatter.Missing;

        var rps = snapshot.Sites.Where(s => s.RequestsPerSec is not null).ToList();
        RequestsText = rps.Count == 0 ? MetricFormatter.Missing : MetricFormatter.Format(MetricUnit.PerSecond, rps.Sum(s => s.RequestsPerSec));

        var active = snapshot.AppPools.Where(p => p.ActiveRequests is not null).ToList();
        ActiveRequestsText = active.Count == 0 ? MetricFormatter.Missing : active.Sum(p => p.ActiveRequests ?? 0).ToString("N0");

        var tcp = snapshot.AppPools.Where(p => p.DbConnections is not null).ToList();
        var sessions = snapshot.AppPools.Where(p => p.DbSessions is not null).ToList();
        DbText = tcp.Count == 0 ? MetricFormatter.Missing : tcp.Sum(p => p.DbConnections ?? 0).ToString("N0");
        if (sessions.Count > 0)
            DbText += $" · {sessions.Sum(p => p.DbSessions ?? 0):N0} sessions";

        var responses = snapshot.Sites.Select(s => s.Response).Where(r => r is { RequestCount: > 0 }).Select(r => r!).ToList();
        var count = responses.Sum(r => r.RequestCount);
        ResponseText = snapshot.Sites.All(s => s.Response is null)
            ? "not traced"
            : count == 0 ? "idle" : MetricFormatter.Format(MetricUnit.Milliseconds, responses.Sum(r => r.AverageMs * r.RequestCount) / count);
    }

    private void OnBackendSettingsChanged(object? sender, MonitorSettings settings) =>
        _dispatcher.BeginInvoke(() => OnSettingsChanged(settings));

    private void OnSettingsChanged(MonitorSettings settings)
    {
        _syncingSettings = true;
        try
        {
            var match = IntervalOptions.FirstOrDefault(o => o.Milliseconds == settings.SampleIntervalMs);
            if (match is null)
            {
                match = new IntervalOption(settings.SampleIntervalMs, $"{settings.SampleIntervalMs} ms");
            }

            _selectedInterval = match;
            Raise(nameof(SelectedInterval));
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private async Task ChangeIntervalAsync(int milliseconds)
    {
        if (_backend is not { } backend)
            return;
        try
        {
            var settings = backend.Settings.Clone();
            settings.SampleIntervalMs = milliseconds;
            OnSettingsChanged(await backend.UpdateSettingsAsync(settings));
        }
        catch (Exception e)
        {
            ReportError(e);
        }
    }

    private void OnDisconnected(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => _ = ReconnectAsync(sender));

    /// <summary>The service went away (restart, upgrade): retry for a while, then collect locally.</summary>
    private async Task ReconnectAsync(object? lostBackend)
    {
        if (_disposed || !ReferenceEquals(lostBackend, _backend))
            return;

        try
        {
            BannerText = "Lost the connection to the IISMonitor service. Reconnecting…";
            BannerIsWarning = true;
            for (var attempt = 0; attempt < 5 && !_disposed; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (await TryConnectServiceAsync(TimeSpan.FromSeconds(2)))
                    return;
            }

            if (!_disposed)
                StartStandalone();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private async Task StartServiceAsync()
    {
        await Task.Run(() =>
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        });

        // Give the service a moment to open its pipe.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await TryConnectServiceAsync(TimeSpan.FromSeconds(1)))
            {
                CanStartService = false;
                return;
            }

            await Task.Delay(500);
        }

        UpdateStandaloneBanner();
        ShowMessage("IIS Monitor", "The service started but isn't accepting connections yet. Check the Windows Application event log for IISMonitor errors.", true);
    }

    private async Task EnableResponseTimesAsync()
    {
        if (_backend is not { } backend)
            return;

        var ok = Confirm(
            "Enable live response times",
            "IIS Monitor will change the IIS logging configuration (applicationHost.config):\n\n" +
            "• The W3C log target becomes \"File, ETW\": log files are still written as before, and each request is also sent to ETW.\n" +
            "• The log fields site name, URI stem, HTTP status and time-taken are switched on if they are off.\n\n" +
            "This applies to the site defaults and to any site that overrides them. Continue?");
        if (!ok)
            return;

        var result = await backend.EnableIisEtwLoggingAsync();
        ShowMessage("Enable live response times", result.Message, !result.Success);
    }

    private static ServiceControllerStatus? GetServiceStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ReportError(Exception e) => ShowMessage("IIS Monitor", e.Message, true);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_backend is { } backend)
        {
            _backend = null;
            backend.SnapshotReceived -= OnSnapshotReceived;
            backend.SettingsChanged -= OnBackendSettingsChanged;
            backend.Disconnected -= OnDisconnected;
            await backend.DisposeAsync();
        }
    }
}
