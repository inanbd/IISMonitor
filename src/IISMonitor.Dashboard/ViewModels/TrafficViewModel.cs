using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Presentation;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Dashboard.ViewModels;

public sealed record TrafficViewOption(RequestLogView View, string Label);

/// <summary>What the block dialog shows: the address, and the app pool whose sites and applications it would apply to.</summary>
public sealed record IpBlockPrompt(string IpAddress, string? AppPool, IReadOnlyList<string> Applications);

/// <summary>A client IP (IP view) or a URL (URL view) in the summary grid.</summary>
public sealed class TrafficSummaryRow(RequestLogSummaryRow row, RequestLogView view) : ObservableObject
{
    private string _blocked = "";

    public RequestLogSummaryRow Row { get; } = row;
    public RequestLogView View { get; } = view;
    public string Key => Row.Key;

    /// <summary>The client IP in the IP view (what "Block this IP…" acts on); null in the URL view.</summary>
    public string? ClientIp => View == RequestLogView.Clients ? Row.Key : null;

    public long Hits => Row.Hits;
    public string HitsText => Number(Row.Hits);
    public long Distinct => Row.Distinct;
    public string DistinctText => Number(Row.Distinct);
    public long Status2xx => Row.Status2xx;
    public string Status2xxText => Number(Row.Status2xx);
    public long Status3xx => Row.Status3xx;
    public string Status3xxText => Number(Row.Status3xx);
    public long Status4xx => Row.Status4xx;
    public string Status4xxText => Number(Row.Status4xx);
    public long Status5xx => Row.Status5xx;
    public string Status5xxText => Number(Row.Status5xx);
    public double AverageTimeMs => Row.AverageTimeMs;
    public string AverageText => MetricFormatter.Format(MetricUnit.Milliseconds, Row.AverageTimeMs);
    public long FirstUnixMs => Row.FirstUnixMs;
    public string FirstText { get; } = TrafficPresentation.FormatTime(row.FirstUnixMs, seconds: true);
    public long LastUnixMs => Row.LastUnixMs;
    public string LastText { get; } = TrafficPresentation.FormatTime(row.LastUnixMs, seconds: true);

    /// <summary>Where IIS refuses this IP ("All sites on this server", "Shop"), or "".</summary>
    public string Blocked { get => _blocked; set => Set(ref _blocked, value); }

    internal static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>Requests from one IP for one URL, method and status: in one minute ("Each minute") or over the whole range ("Totals").</summary>
public sealed class TrafficDetailRow(RequestLogDetailRow row)
{
    public RequestLogDetailRow Row { get; } = row;
    public long MinuteUnixMs => Row.MinuteUnixMs;
    public string TimeText { get; } = TrafficPresentation.FormatTime(row.MinuteUnixMs, seconds: false);
    public string ClientIp => Row.ClientIp;
    public string Url => Row.Url;
    public string Method => Row.Method;
    public long StatusSort => TrafficPresentation.StatusSortKey(Row.Status, Row.SubStatus);
    public string StatusText => TrafficPresentation.StatusText(Row.Status, Row.SubStatus);
    public bool IsError => Row.Status >= 400;
    public long Hits => Row.Hits;
    public string HitsText => TrafficSummaryRow.Number(Row.Hits);
    public double AverageTimeMs => Row.AverageTimeMs;
    public string AverageText => MetricFormatter.Format(MetricUnit.Milliseconds, Row.AverageTimeMs);
    public long FirstUnixMs => Row.FirstUnixMs;
    public string FirstText { get; } = TrafficPresentation.FormatTime(row.FirstUnixMs, seconds: true);
    public long LastUnixMs => Row.LastUnixMs;
    public string LastText { get; } = TrafficPresentation.FormatTime(row.LastUnixMs, seconds: true);
}

/// <summary>An address IIS refuses, as a grid row.</summary>
public sealed class BlockedIpRow(BlockedIp entry)
{
    public BlockedIp Entry { get; } = entry;
    public string IpAddress => Entry.IpAddress;
    public string Mask => Entry.SubnetMask ?? "";
    public string AppliesTo => IpAddressRules.DescribeLocation(Entry.Location);
}

/// <summary>
/// The IPs &amp; URLs tab: for each tracked app pool, which client IPs requested which URLs, when, and
/// with which status codes; and the addresses IIS refuses. It asks the collector only while the
/// tab is showing: when it opens, when a choice changes, and every 30 seconds.
/// </summary>
public sealed class TrafficViewModel : ObservableObject
{
    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);
    public const int SummaryLimit = 1000;
    public const int DetailLimit = 1000;

    private readonly Func<IMonitorBackend?> _backend;
    private readonly Action<string, string, bool> _showMessage;
    private readonly Func<IpBlockPrompt, IpBlockRequest?> _promptBlock;
    private MonitorSnapshot? _snapshot;
    private List<BlockedIp> _blockedEntries = [];
    private int _retentionDays;
    private bool _isConfigured;
    private string? _selectedPool;
    private TrafficRange _selectedRange;
    private TrafficViewOption _selectedView;
    private string _filter = "";
    private bool _eachMinute = true;
    private TrafficSummaryRow? _selectedSummary;
    private TrafficDetailRow? _selectedDetail;
    private BlockedIpRow? _selectedBlocked;
    private bool _replacingSummaries;
    private bool _loggingFieldsMissing;
    private string _statusText = "";
    private string _detailHeader = "";
    private string _detailStatus = "";
    private string _blockedHeader = "Blocked IP addresses";
    private string _blockedStatus = "";
    private DateTime _rangeFromUtc;
    private DateTime _rangeToUtc;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private int _summaryLoads;
    private int _summaryVersion;
    private int _detailVersion;
    private int _blockedVersion;

    public TrafficViewModel(
        Func<IMonitorBackend?> backend,
        Action<Exception> onError,
        Action<string, string, bool> showMessage,
        Func<IpBlockPrompt, IpBlockRequest?> promptBlock,
        Func<Task> enableLogging)
    {
        _backend = backend;
        _showMessage = showMessage;
        _promptBlock = promptBlock;
        Views = [new(RequestLogView.Clients, "By IP address"), new(RequestLogView.Urls, "By URL")];
        _selectedView = Views[0];
        _retentionDays = new MonitorSettings().RequestLogRetentionDays;
        foreach (var range in TrafficPresentation.RangesWithin(_retentionDays))
            Ranges.Add(range);
        _selectedRange = Ranges[1];
        _detailHeader = NoSelectionHeader;

        RefreshCommand = new AsyncCommand(() => RefreshAsync(), onError, () => _backend() is not null && SelectedPool is not null);
        BlockIpCommand = new AsyncCommand<object>(BlockAsync, onError, row => _backend() is not null && CanBlock(IpOf(row)));
        UnblockCommand = new AsyncCommand(UnblockAsync, onError, () => _backend() is not null && SelectedBlocked is not null);
        EnableLoggingCommand = new AsyncCommand(
            async () =>
            {
                await enableLogging();
                await RefreshAsync(quiet: true);
            },
            onError,
            () => _backend() is not null);
    }

    /// <summary>Tracked app pools (Settings, IP and URL tracking).</summary>
    public ObservableCollection<string> Pools { get; } = [];

    /// <summary>Ranges that fit in the retention.</summary>
    public ObservableCollection<TrafficRange> Ranges { get; } = [];

    public IReadOnlyList<TrafficViewOption> Views { get; }
    public RowCollection<TrafficSummaryRow> Summaries { get; } = [];
    public RowCollection<TrafficDetailRow> Details { get; } = [];
    public RowCollection<BlockedIpRow> Blocked { get; } = [];

    /// <summary>What the collector says the user should know: missing IIS log fields, tracking off, dropped requests, errors.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    public ICommand RefreshCommand { get; }

    /// <summary>Blocks the IP of the row it is invoked on (context menu), else of the selection.</summary>
    public ICommand BlockIpCommand { get; }

    public ICommand UnblockCommand { get; }

    /// <summary>Turns on the IIS log fields tracking needs (the same change as "Enable response times").</summary>
    public ICommand EnableLoggingCommand { get; }

    /// <summary>Set while the tab is showing; changed choices then query the collector right away.</summary>
    public bool IsVisible { get; set; }

    /// <summary>At least one app pool is tracked.</summary>
    public bool IsConfigured
    {
        get => _isConfigured;
        private set
        {
            if (Set(ref _isConfigured, value))
                Raise(nameof(ShowSetup));
        }
    }

    public bool ShowSetup => !IsConfigured;

    public string? SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (value is not null && Set(ref _selectedPool, value))
                OnPoolChanged();
        }
    }

    public TrafficRange SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && Set(ref _selectedRange, value))
                OnQueryChanged();
        }
    }

    public TrafficViewOption SelectedView
    {
        get => _selectedView;
        set
        {
            if (value is null || !Set(ref _selectedView, value))
                return;
            Raise(nameof(IsIpView));
            Raise(nameof(FilterHint));
            ClearSummaries();
            OnQueryChanged();
        }
    }

    /// <summary>The grids list client IPs (true) or URLs (false); the window shows the matching columns.</summary>
    public bool IsIpView => SelectedView.View == RequestLogView.Clients;

    public string FilterHint => IsIpView ? "Show only IP addresses containing this text" : "Show only URLs containing this text";

    /// <summary>Only IPs or URLs containing this text (the window updates it 400 ms after typing stops).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? ""))
                OnQueryChanged();
        }
    }

    /// <summary>Details per minute. Radio buttons set this or <see cref="IsTotals"/> to true; false is ignored.</summary>
    public bool IsEachMinute
    {
        get => _eachMinute;
        set
        {
            if (value)
                SetEachMinute(true);
        }
    }

    public bool IsTotals
    {
        get => !_eachMinute;
        set
        {
            if (value)
                SetEachMinute(false);
        }
    }

    public TrafficSummaryRow? SelectedSummary
    {
        get => _selectedSummary;
        set
        {
            // While the grid is refilled it reports an empty selection; the refill restores the selection and details itself.
            if (!Set(ref _selectedSummary, value) || _replacingSummaries)
                return;
            ClearDetails();
            if (value is not null)
                _ = LoadDetailsAsync(showLoading: true);
        }
    }

    public TrafficDetailRow? SelectedDetail { get => _selectedDetail; set => Set(ref _selectedDetail, value); }

    public BlockedIpRow? SelectedBlocked { get => _selectedBlocked; set => Set(ref _selectedBlocked, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string DetailHeader { get => _detailHeader; private set => Set(ref _detailHeader, value); }
    public string DetailStatus { get => _detailStatus; private set => Set(ref _detailStatus, value); }
    public string BlockedHeader { get => _blockedHeader; private set => Set(ref _blockedHeader, value); }
    public string BlockedStatus { get => _blockedStatus; private set => Set(ref _blockedStatus, value); }

    /// <summary>Some site of the pool doesn't send the client IP to ETW yet; the tab offers to turn it on.</summary>
    public bool LoggingFieldsMissing
    {
        get => _loggingFieldsMissing;
        private set
        {
            if (Set(ref _loggingFieldsMissing, value))
                Raise(nameof(ShowWarnings));
        }
    }

    public bool ShowWarnings => Warnings.Count > 0 || LoggingFieldsMissing;

    private string NoSelectionHeader => IsIpView
        ? "Select an IP address to see the URLs it visited."
        : "Select a URL to see which IP addresses visited it.";

    /// <summary>Called for every snapshot: follows the settings, and refreshes every 30 seconds while the tab is showing.</summary>
    public void Apply(MonitorSnapshot snapshot, MonitorSettings settings, bool visible)
    {
        _snapshot = snapshot;
        ApplySettings(settings);
        if (visible && IsConfigured && _summaryLoads == 0 && DateTime.UtcNow - _lastRefreshUtc >= AutoRefreshInterval)
            _ = RefreshAsync(quiet: true);
    }

    /// <summary>Follows the tracked app pools and the retention (which limits the ranges offered).</summary>
    public void ApplySettings(MonitorSettings settings)
    {
        var wasConfigured = IsConfigured;
        IsConfigured = settings.RequestTrackingPools.Count > 0;

        if (!Pools.SequenceEqual(settings.RequestTrackingPools, StringComparer.Ordinal))
        {
            var previous = SelectedPool;
            Pools.Clear();
            foreach (var pool in settings.RequestTrackingPools)
                Pools.Add(pool);

            // Keep the chosen pool while it is still tracked (the ComboBox let go of it while the list was refilled).
            var next = Pools.FirstOrDefault(p => string.Equals(p, previous, StringComparison.OrdinalIgnoreCase)) ?? Pools.FirstOrDefault();
            _selectedPool = next;
            Raise(nameof(SelectedPool));
            if (!string.Equals(next, previous, StringComparison.Ordinal))
                OnPoolChanged();
        }

        // Tracking was just turned on while the tab shows its setup text: the blocked list hasn't been read yet.
        if (IsConfigured && !wasConfigured && IsVisible)
            _ = ReloadBlockedAsync();

        if (settings.RequestLogRetentionDays != _retentionDays)
        {
            _retentionDays = settings.RequestLogRetentionDays;
            var previous = SelectedRange;
            Ranges.Clear();
            foreach (var range in TrafficPresentation.RangesWithin(_retentionDays))
                Ranges.Add(range);

            var next = Ranges.LastOrDefault(r => r.Range <= previous.Range) ?? Ranges[0];
            _selectedRange = next;
            Raise(nameof(SelectedRange));
            if (next != previous)
                OnQueryChanged();
        }
    }

    /// <summary>Reloads the lists and the blocked addresses. Failures show in the tab, not as message boxes.</summary>
    public async Task RefreshAsync(bool quiet = false)
    {
        if (!IsConfigured)
            return;

        var blocked = ReloadBlockedAsync();
        await LoadSummariesAsync(quiet);
        await blocked;
    }

    private void OnQueryChanged()
    {
        if (IsVisible)
            _ = LoadSummariesAsync(quiet: false);
    }

    /// <summary>Another pool's rows, warnings and status would mislead while the new pool loads.</summary>
    private void OnPoolChanged()
    {
        ClearSummaries();
        SetWarnings([], loggingFieldsMissing: false);
        StatusText = "";
        OnQueryChanged();
    }

    private void SetEachMinute(bool value)
    {
        if (_eachMinute == value)
            return;
        _eachMinute = value;
        Raise(nameof(IsEachMinute));
        Raise(nameof(IsTotals));
        ClearDetails();
        if (SelectedSummary is not null)
            _ = LoadDetailsAsync(showLoading: true);
    }

    private async Task LoadSummariesAsync(bool quiet)
    {
        if (_backend() is not { } backend || !IsConfigured || SelectedPool is not { } pool)
            return;

        var version = ++_summaryVersion;
        var view = SelectedView.View;
        var now = DateTime.UtcNow;
        var query = new RequestLogQuery
        {
            AppPool = pool,
            FromUtc = now - SelectedRange.Range,
            ToUtc = now,
            View = view,
            Filter = string.IsNullOrWhiteSpace(Filter) ? null : Filter.Trim(),
            Limit = SummaryLimit,
        };
        _lastRefreshUtc = now;
        _summaryLoads++;
        if (!quiet)
            StatusText = $"Loading {pool}…";

        try
        {
            var report = await backend.QueryRequestLogAsync(query);
            if (version != _summaryVersion)
                return;

            _rangeFromUtc = query.FromUtc;
            _rangeToUtc = query.ToUtc;
            ShowSummaries(report, view, query.Filter);
        }
        catch (Exception e)
        {
            if (version != _summaryVersion)
                return;

            // Shown in the tab: while it refreshes every 30 seconds, a message box each time would be worse.
            ClearSummaries();
            SetWarnings(["Couldn't load the recorded requests: " + e.Message], loggingFieldsMissing: false);
            StatusText = "Couldn't load the recorded requests.";
        }
        finally
        {
            _summaryLoads--;
        }
    }

    private void ShowSummaries(RequestLogReport report, RequestLogView view, string? filter)
    {
        var selectedKey = SelectedSummary?.Key;
        var rows = report.Summaries.Select(r => new TrafficSummaryRow(r, view)).ToList();
        foreach (var row in rows)
            row.Blocked = BlockedText(row);
        var reselect = selectedKey is null ? null : rows.FirstOrDefault(r => r.Key == selectedKey);

        _replacingSummaries = true;
        try
        {
            Summaries.ReplaceAll(rows);
        }
        finally
        {
            _replacingSummaries = false;
        }

        _selectedSummary = reselect;
        Raise(nameof(SelectedSummary));
        SetWarnings(report.Warnings, report.LoggingFieldsMissing);
        StatusText = TrafficPresentation.SummaryStatus(report, filter);

        // The details follow the selection: reloaded while the row is still there, cleared once it is gone.
        if (reselect is not null)
            _ = LoadDetailsAsync(showLoading: false);
        else
            ClearDetails();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadDetailsAsync(bool showLoading)
    {
        if (_backend() is not { } backend || SelectedPool is not { } pool || SelectedSummary is not { } summary)
            return;

        var version = ++_detailVersion;
        var perMinute = IsEachMinute;
        DetailHeader = TrafficPresentation.DetailHeader(summary.View, summary.Key);
        if (showLoading)
            DetailStatus = "Loading…";

        try
        {
            var report = await backend.QueryRequestLogAsync(new RequestLogQuery
            {
                AppPool = pool,
                FromUtc = _rangeFromUtc,
                ToUtc = _rangeToUtc,
                View = summary.View == RequestLogView.Clients ? RequestLogView.ClientDetail : RequestLogView.UrlDetail,
                Key = summary.Key,
                PerMinute = perMinute,
                Limit = DetailLimit,
            });
            if (version != _detailVersion)
                return;

            var selected = SelectedDetail?.Row;
            var rows = report.Details.Select(r => new TrafficDetailRow(r)).ToList();
            ReplaceDetails(rows, selected is null ? null : rows.FirstOrDefault(r => SameRow(r.Row, selected)));
            DetailStatus = TrafficPresentation.DetailStatus(report, perMinute);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception e)
        {
            if (version != _detailVersion)
                return;
            ReplaceDetails([], null);
            DetailStatus = "Couldn't load the details: " + e.Message;
        }
    }

    private void ClearSummaries()
    {
        _summaryVersion++;
        _replacingSummaries = true;
        try
        {
            Summaries.ReplaceAll([]);
        }
        finally
        {
            _replacingSummaries = false;
        }

        _selectedSummary = null;
        Raise(nameof(SelectedSummary));
        ClearDetails();
    }

    private void ClearDetails()
    {
        _detailVersion++;
        ReplaceDetails([], null);
        DetailHeader = SelectedSummary is { } summary ? TrafficPresentation.DetailHeader(summary.View, summary.Key) : NoSelectionHeader;
        DetailStatus = "";
    }

    private void ReplaceDetails(List<TrafficDetailRow> rows, TrafficDetailRow? selected)
    {
        Details.ReplaceAll(rows);
        SelectedDetail = selected;
    }

    private static bool SameRow(RequestLogDetailRow a, RequestLogDetailRow b) =>
        a.MinuteUnixMs == b.MinuteUnixMs && a.ClientIp == b.ClientIp && a.Url == b.Url && a.Method == b.Method
        && a.Status == b.Status && a.SubStatus == b.SubStatus;

    private void SetWarnings(IReadOnlyList<string> warnings, bool loggingFieldsMissing)
    {
        // The button needs a reason next to it, even if the collector sent none.
        if (loggingFieldsMissing && warnings.Count == 0)
            warnings = ["IIS doesn't send the client IP of this app pool's requests to ETW yet, so they can't be recorded."];

        if (!Warnings.SequenceEqual(warnings))
        {
            Warnings.Clear();
            foreach (var warning in warnings)
                Warnings.Add(warning);
        }

        LoggingFieldsMissing = loggingFieldsMissing;
        Raise(nameof(ShowWarnings));
    }

    private async Task ReloadBlockedAsync()
    {
        if (_backend() is not { } backend)
            return;

        var version = ++_blockedVersion;
        try
        {
            var entries = await backend.ListBlockedIpsAsync();
            if (version != _blockedVersion)
                return;

            _blockedEntries = entries;
            var selected = SelectedBlocked?.Entry;
            var rows = entries.Select(e => new BlockedIpRow(e)).ToList();
            Blocked.ReplaceAll(rows);
            SelectedBlocked = selected is null
                ? null
                : rows.FirstOrDefault(r => r.Entry.IpAddress == selected.IpAddress && r.Entry.SubnetMask == selected.SubnetMask
                                           && string.Equals(r.Entry.Location, selected.Location, StringComparison.OrdinalIgnoreCase));
            BlockedHeader = $"Blocked IP addresses ({entries.Count})";
            BlockedStatus = entries.Count == 0
                ? "IIS isn't refusing any IP address."
                : "IIS answers requests from these addresses with 403 Forbidden.";
            foreach (var row in Summaries)
                row.Blocked = BlockedText(row);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception e)
        {
            if (version == _blockedVersion)
                BlockedStatus = "Couldn't read the blocked addresses: " + e.Message;
        }
    }

    private string BlockedText(TrafficSummaryRow row) =>
        row.ClientIp is { } ip && _blockedEntries.Count > 0 ? TrafficPresentation.BlockedText(_blockedEntries, ip) : "";

    /// <summary>The IP a command acts on: the row it was invoked on, else the selected IP (IP view) or detail row (URL view).</summary>
    private string? IpOf(object? row) => row switch
    {
        TrafficSummaryRow summary => summary.ClientIp,
        TrafficDetailRow detail => detail.ClientIp,
        _ => IsIpView ? SelectedSummary?.ClientIp : SelectedDetail?.ClientIp,
    };

    /// <summary>
    /// "(unknown)" can't be blocked. Other addresses that can't (loopback, multicast…) stay enabled, so
    /// clicking explains why.
    /// </summary>
    private static bool CanBlock(string? ip) => TrafficPresentation.IsIpAddress(ip);

    private async Task BlockAsync(object? row)
    {
        if (_backend() is not { } backend || IpOf(row) is not { } ip)
            return;
        if (!IpAddressRules.TryNormalizeForBlocking(ip, out var address, out var error))
        {
            _showMessage("Block IP address", error, true);
            return;
        }

        var pool = SelectedPool;
        if (_promptBlock(new IpBlockPrompt(address, pool, ApplicationsOf(pool))) is not { } request)
            return;

        var result = await backend.BlockIpAsync(request);
        _showMessage("Block IP address", result.Message, !result.Success);
        await RefreshAsync(quiet: true);
    }

    private async Task UnblockAsync()
    {
        if (_backend() is not { } backend || SelectedBlocked is not { } row)
            return;

        var result = await backend.UnblockIpAsync(new IpBlockRequest { IpAddress = row.Entry.IpAddress, Location = row.Entry.Location });
        _showMessage("Unblock IP address", result.Message, !result.Success);
        await RefreshAsync(quiet: true);
    }

    /// <summary>The pool's applications from the latest snapshot, "Site" for a root application and "Site/path" otherwise.</summary>
    private List<string> ApplicationsOf(string? pool)
    {
        var metrics = _snapshot?.AppPools.FirstOrDefault(p => string.Equals(p.Name, pool, StringComparison.OrdinalIgnoreCase));
        return metrics?.Applications.Select(a => a.EndsWith('/') ? a[..^1] : a).ToList() ?? [];
    }
}
