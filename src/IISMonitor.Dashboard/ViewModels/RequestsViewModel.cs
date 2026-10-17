using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using IISMonitor.Core.Blocking;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Presentation;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.Requests;
using IISMonitor.Core.Settings;

namespace IISMonitor.Dashboard.ViewModels;

/// <summary>A checkbox in a filter bar.</summary>
public sealed class FilterToggle<T>(T value, string label, Action changed) : ObservableObject
{
    private bool _isChecked = true;

    public T Value { get; } = value;

    public string Label { get; } = label;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                changed();
        }
    }
}

public sealed record SiteOption(long? Id, string Name);

public sealed record ModeOption(BlockingMode Mode, string Label);

public sealed record WindowChoice(TopIpWindow Window, string Label);

public sealed class RequestRow(RequestRecord record)
{
    public RequestRecord Record { get; } = record;
    public DateTime Time => Record.TimeUtc.ToLocalTime();
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Client => RequestsPresentation.Client(Record);
    public string Kind => RequestsPresentation.Label(Record.Kind);
    public string Status => Record.SubStatus == 0 ? Record.Status.ToString(CultureInfo.InvariantCulture) : $"{Record.Status}.{Record.SubStatus}";
    public string TimeTakenText => MetricFormatter.Format(MetricUnit.Milliseconds, Record.TimeTakenMs);
    public string BytesText => Record.BytesSent is { } bytes ? MetricFormatter.Format(MetricUnit.Bytes, bytes) : MetricFormatter.Missing;
    public long Bytes => Record.BytesSent ?? -1;
    public bool IsError => Record.Status >= 400;
    public bool HitsRule => Record.Rule is not null;
}

public sealed class TopIpView(TopIpRow row)
{
    public TopIpRow Row { get; } = row;
    public string Ip => Row.ViaProxy ? Row.Ip + " (via proxy)" : Row.Ip;
    public string LastSeen => Row.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Errors => $"{Row.ClientErrors} / {Row.ServerErrors}";
    public int ErrorCount => Row.ClientErrors + Row.ServerErrors;
    public string Blocked => Row.Blocked ?? "";
}

public sealed class BlockView(BlockEntry entry, DateTime nowUtc)
{
    public BlockEntry Entry { get; } = entry;
    public string State => RequestsPresentation.State(Entry);
    public string How => Entry.Method == BlockMethod.Firewall ? "Firewall" : "IIS (proxy)";
    public string Why => Entry.Manual ? "By hand: " + Entry.Reason : $"{Entry.Reason} ({Entry.Hits} hits)";
    public string Since => Entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public DateTime Created => Entry.CreatedUtc;
    public string Expires => RequestsPresentation.Expires(Entry, nowUtc);
    public bool IsCurrent => Entry.IsCurrent;
    public bool IsProblem => Entry.State == BlockState.Failed;
}

/// <summary>One editable rule row in the blacklist editor.</summary>
public sealed class RuleRow : ObservableObject
{
    private bool _enabled = true;
    private string _name = "";
    private string _pattern = "";
    private int _hits = 3;
    private double _withinMinutes = 10;
    private double _blockHours = 24;
    private string _sites = "";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string Pattern { get => _pattern; set => Set(ref _pattern, value ?? ""); }
    public int Hits { get => _hits; set => Set(ref _hits, value); }
    public double WithinMinutes { get => _withinMinutes; set => Set(ref _withinMinutes, value); }
    public double BlockHours { get => _blockHours; set => Set(ref _blockHours, value); }

    /// <summary>Site names, comma-separated; empty for all sites.</summary>
    public string Sites { get => _sites; set => Set(ref _sites, value ?? ""); }

    public static RuleRow From(BlockRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Name = rule.Name,
        Pattern = rule.Pattern,
        Hits = rule.Hits,
        WithinMinutes = rule.WithinMinutes,
        BlockHours = rule.BlockHours,
        Sites = string.Join(", ", rule.Sites),
    };

    public BlockRule ToRule() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Name = Name,
        Pattern = Pattern,
        Hits = Hits,
        WithinMinutes = WithinMinutes,
        BlockHours = BlockHours,
        Sites = Sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };
}

/// <summary>What the "Block IP" dialog returns.</summary>
public sealed record BlockIpDraft(string Ip, double Hours, BlockMethod? Method, string? Note);

/// <summary>
/// The Requests tab: every request from every IP with method, kind, IP, URL and site filters, the
/// IPs sending the most, the blocked IPs, and the blacklist rules.
/// </summary>
public sealed class RequestsViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BlocksEvery = TimeSpan.FromSeconds(5);

    private readonly Func<IMonitorBackend?> _backend;
    private readonly Action<Exception> _onError;
    private readonly Func<MonitorSettings, Task> _saveSettings;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private DateTime _lastBlocksUtc = DateTime.MinValue;
    private bool _refreshing;
    private int _version;
    private string _ipFilter = "";
    private string _pathFilter = "";
    private SiteOption _selectedSite = AllSites;
    private WindowChoice _selectedWindow;
    private bool _freezeList;
    private bool _showEndedBlocks;
    private string _summary = "";
    private string _topSummary = "";
    private string _loggingStatus = "";
    private bool _needsLoggingSetup;
    private string _blockingStatus = "";
    private bool _blockingProblem;
    private bool _serviceOutdated;
    private RequestRow? _selectedRequest;
    private TopIpView? _selectedTopIp;
    private BlockView? _selectedBlock;
    private ModeOption _selectedMode;
    private string _allowListText = "";
    private string _trustedProxiesText = "";
    private string _iisIntervalText = "60";
    private bool _dropOpenConnections = true;
    private string _editorMessages = "";
    private bool _editorDirty;
    private bool _loadingEditor;

    public static readonly SiteOption AllSites = new(null, "All sites");

    public RequestsViewModel(Func<IMonitorBackend?> backend, Action<Exception> onError, Func<MonitorSettings, Task> saveSettings)
    {
        _backend = backend;
        _onError = onError;
        _saveSettings = saveSettings;

        void FilterChanged() => _ = RefreshAsync(force: true);
        foreach (var method in Enum.GetValues<MethodGroup>())
            MethodToggles.Add(new FilterToggle<MethodGroup>(method, RequestsPresentation.Label(method), FilterChanged));
        foreach (var kind in Enum.GetValues<RequestKind>())
            KindToggles.Add(new FilterToggle<RequestKind>(kind, RequestsPresentation.Label(kind), FilterChanged));
        Sites.Add(AllSites);
        Windows = Enum.GetValues<TopIpWindow>().Select(w => new WindowChoice(w, RequestsPresentation.Label(w))).ToList();
        _selectedWindow = Windows[1];
        Modes = Enum.GetValues<BlockingMode>().Select(m => new ModeOption(m, RequestsPresentation.Label(m))).ToList();
        _selectedMode = Modes[0];
        Rules.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<RuleRow>() ?? [])
                row.PropertyChanged += (_, _) => EditorChanged();
            EditorChanged();
        };

        RefreshCommand = new AsyncCommand(() => RefreshAsync(force: true, blocks: true), onError);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ShowIpCommand = new RelayCommand(() => IpFilter = SelectedIp ?? IpFilter, () => SelectedIp is not null);
        BlockSelectedCommand = new AsyncCommand(() => BlockAsync(SelectedIp), onError, () => SelectedIp is not null && CanBlock);
        BlockIpCommand = new AsyncCommand(() => BlockAsync(null), onError, () => CanBlock);
        UnblockSelectedCommand = new AsyncCommand(UnblockSelectedAsync, onError, () => SelectedBlock?.IsCurrent == true && CanBlock);
        RuleFromRequestCommand = new RelayCommand(RuleFromSelectedRequest, () => SelectedRequest is not null);
        AddRuleCommand = new RelayCommand(() => Rules.Add(new RuleRow { Name = "New rule", Pattern = "/" }));
        AddScannerRuleCommand = new RelayCommand(() => Rules.Add(RuleRow.From(BlockingSettings.ScannerRule())));
        RemoveRuleCommand = new RelayCommand(() => { if (SelectedRule is { } rule) Rules.Remove(rule); }, () => SelectedRule is not null);
        SaveRulesCommand = new AsyncCommand(SaveRulesAsync, onError, () => _backend() is not null && EditorDirty);
        RevertRulesCommand = new RelayCommand(() => LoadEditor(_backend()?.Settings, force: true), () => EditorDirty);
        SetupLoggingCommand = new AsyncCommand(SetupLoggingAsync, onError, () => _backend() is not null);
    }

    /// <summary>Asks for an IP to block (prefilled), its duration and method. Set by the window.</summary>
    public Func<string?, BlockIpDraft?> AskBlock { get; set; } = _ => null;

    /// <summary>Shows the IIS logging setup for the given sites. Set by the window.</summary>
    public Func<IReadOnlyList<SiteMetrics>, IisLoggingRequest?> AskLoggingSetup { get; set; } = _ => null;

    /// <summary>Shows a result (title, message, isError). Set by the main view model.</summary>
    public Action<string, string, bool> ShowMessage { get; set; } = (_, _, _) => { };

    public ObservableCollection<FilterToggle<MethodGroup>> MethodToggles { get; } = [];
    public ObservableCollection<FilterToggle<RequestKind>> KindToggles { get; } = [];
    public ObservableCollection<SiteOption> Sites { get; } = [];
    public IReadOnlyList<WindowChoice> Windows { get; }
    public IReadOnlyList<ModeOption> Modes { get; }
    public ObservableCollection<RequestRow> Requests { get; } = [];
    public ObservableCollection<TopIpView> TopIps { get; } = [];
    public ObservableCollection<BlockView> Blocks { get; } = [];
    public ObservableCollection<RuleRow> Rules { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ShowIpCommand { get; }
    public ICommand BlockSelectedCommand { get; }
    public ICommand BlockIpCommand { get; }
    public ICommand UnblockSelectedCommand { get; }
    public ICommand RuleFromRequestCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand AddScannerRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand SaveRulesCommand { get; }
    public ICommand RevertRulesCommand { get; }
    public ICommand SetupLoggingCommand { get; }

    /// <summary>The IP box: an address, a network ("203.0.113.0/24") or its start ("203.0.").</summary>
    public string IpFilter
    {
        get => _ipFilter;
        set
        {
            if (Set(ref _ipFilter, value?.Trim() ?? ""))
                _ = RefreshAsync(force: true);
        }
    }

    public string PathFilter
    {
        get => _pathFilter;
        set
        {
            if (Set(ref _pathFilter, value ?? ""))
                _ = RefreshAsync(force: true);
        }
    }

    public SiteOption SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (value is not null && Set(ref _selectedSite, value))
                _ = RefreshAsync(force: true);
        }
    }

    public WindowChoice SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (value is not null && Set(ref _selectedWindow, value))
                _ = RefreshAsync(force: true);
        }
    }

    /// <summary>Stops the request list from updating, so it can be read.</summary>
    public bool FreezeList { get => _freezeList; set => Set(ref _freezeList, value); }

    public bool ShowEndedBlocks
    {
        get => _showEndedBlocks;
        set
        {
            if (Set(ref _showEndedBlocks, value))
                _ = RefreshAsync(force: true, blocks: true);
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string TopSummary { get => _topSummary; private set => Set(ref _topSummary, value); }
    public string LoggingStatus { get => _loggingStatus; private set => Set(ref _loggingStatus, value); }
    public bool NeedsLoggingSetup { get => _needsLoggingSetup; private set => Set(ref _needsLoggingSetup, value); }
    public string BlockingStatus { get => _blockingStatus; private set => Set(ref _blockingStatus, value); }
    public bool BlockingProblem { get => _blockingProblem; private set => Set(ref _blockingProblem, value); }

    /// <summary>The service predates the Requests tab.</summary>
    public bool ServiceOutdated { get => _serviceOutdated; private set => Set(ref _serviceOutdated, value); }

    /// <summary>Blocking needs the Windows service; a standalone dashboard can't.</summary>
    public bool CanBlock => _backend() is { IsStandalone: false } backend && backend.Features.Contains(PipeProtocol.Feature.Blocking);

    public RequestRow? SelectedRequest
    {
        get => _selectedRequest;
        set
        {
            if (Set(ref _selectedRequest, value))
                Raise(nameof(SelectedIp));
        }
    }

    public TopIpView? SelectedTopIp
    {
        get => _selectedTopIp;
        set
        {
            if (Set(ref _selectedTopIp, value))
                Raise(nameof(SelectedIp));
        }
    }

    public BlockView? SelectedBlock { get => _selectedBlock; set => Set(ref _selectedBlock, value); }

    public RuleRow? SelectedRule { get; set; }

    /// <summary>The IP of the selected request or Top IPs row.</summary>
    public string? SelectedIp => SelectedTopIp?.Row.Ip ?? SelectedRequest?.Record.ClientIp;

    // ---- Blacklist editor ----

    public ModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is not null && Set(ref _selectedMode, value))
                EditorChanged();
        }
    }

    /// <summary>One address or network per line.</summary>
    public string AllowListText { get => _allowListText; set { if (Set(ref _allowListText, value ?? "")) EditorChanged(); } }

    public string TrustedProxiesText { get => _trustedProxiesText; set { if (Set(ref _trustedProxiesText, value ?? "")) EditorChanged(); } }

    public string IisIntervalText { get => _iisIntervalText; set { if (Set(ref _iisIntervalText, value ?? "")) EditorChanged(); } }

    public bool DropOpenConnections { get => _dropOpenConnections; set { if (Set(ref _dropOpenConnections, value)) EditorChanged(); } }

    public string EditorMessages { get => _editorMessages; private set => Set(ref _editorMessages, value); }

    public bool EditorDirty { get => _editorDirty; private set => Set(ref _editorDirty, value); }

    /// <summary>Updates status lines and the site list; refreshes the lists while the tab is shown.</summary>
    public void Apply(MonitorSnapshot snapshot, bool visible)
    {
        var backend = _backend();
        ServiceOutdated = backend is not null && !backend.Features.Contains(PipeProtocol.Feature.Requests);

        var sites = snapshot.Sites;
        foreach (var site in sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Sites.Any(s => s.Id == site.Id))
                Sites.Add(new SiteOption(site.Id, site.Name));
        }

        var detailed = sites.Count(s => s.RequestFieldsEnabled);
        var headers = sites.Count(s => s.HeaderFieldsEnabled);
        NeedsLoggingSetup = sites.Count > 0 && detailed < sites.Count;
        LoggingStatus = sites.Count == 0 ? ""
            : snapshot.Requests is null ? "IIS's log stream isn't being read (see the Collectors tab)."
            : $"{detailed} of {sites.Count} sites log request details · {headers} log header fields (more accurate kinds; needed for visitors behind a proxy)"
              + (snapshot.Requests.EventsLost > 0 ? $" · {snapshot.Requests.EventsLost:N0} events lost" : "")
              + (snapshot.Requests.Unattributed > 0 ? $" · {snapshot.Requests.Unattributed} proxied requests without a usable X-Forwarded-For" : "");

        var status = snapshot.Blocking;
        BlockingStatus = status is null ? ""
            : status.Mode == BlockingMode.Off ? "Auto blacklist: off."
            : !status.Enforcing ? "Auto blacklist: applied by the IIS Monitor Windows service only. This standalone dashboard shows requests but doesn't block."
            : status.Mode == BlockingMode.Watch ? $"Auto blacklist: watch only · {status.Watching} IPs would have been blocked."
            : $"Auto blacklist: {status.ActiveFirewall} IPs blocked in the firewall · {status.ActiveIis} in IIS · {status.Pending} pending"
              + (status.Failed > 0 ? $" · {status.Failed} failed" : "")
              + (status.NextIisApplyUtc is { } next ? $" · next IIS update {next.ToLocalTime():HH:mm:ss}" : "");
        var problems = new[] { status?.FirewallProblem, status?.IisProblem, status is { Failed: > 0 } ? status.LastError : null }
            .Where(p => !string.IsNullOrEmpty(p)).ToList();
        BlockingProblem = problems.Count > 0;
        if (problems.Count > 0)
            BlockingStatus += " " + string.Join(" ", problems);

        if (visible && DateTime.UtcNow - _lastRefreshUtc >= RefreshEvery)
            _ = RefreshAsync(force: false, blocks: DateTime.UtcNow - _lastBlocksUtc >= BlocksEvery);
    }

    /// <summary>Loads the blacklist settings into the editor, unless there are unsaved edits.</summary>
    public void LoadEditor(MonitorSettings? settings, bool force = false)
    {
        if (settings?.Blocking is not { } blocking || (EditorDirty && !force))
            return;
        _loadingEditor = true;
        try
        {
            SelectedMode = Modes.First(m => m.Mode == blocking.Mode);
            Rules.Clear();
            foreach (var rule in blocking.Rules)
                Rules.Add(RuleRow.From(rule));
            AllowListText = string.Join(Environment.NewLine, blocking.AllowList);
            TrustedProxiesText = string.Join(Environment.NewLine, blocking.TrustedProxies);
            IisIntervalText = blocking.IisApplyIntervalSeconds.ToString(CultureInfo.CurrentCulture);
            DropOpenConnections = blocking.DropOpenConnections;
        }
        finally
        {
            _loadingEditor = false;
        }

        EditorDirty = false;
        Validate();
    }

    public async Task RefreshAsync(bool force, bool blocks = false)
    {
        if (_backend() is not { } backend || backend.Features.Contains(PipeProtocol.Feature.Requests) == false)
            return;
        if (_refreshing && !force)
            return;

        _refreshing = true;
        _lastRefreshUtc = DateTime.UtcNow;
        var version = ++_version;
        try
        {
            var filter = BuildFilter();
            var requestsTask = FreezeList ? null : backend.QueryRequestsAsync(new RequestQuery { Filter = filter, Limit = 1000 });
            var topTask = backend.QueryTopIpsAsync(new TopIpQuery
            {
                Window = SelectedWindow.Window,
                Filter = new RequestFilter { Methods = filter.Methods, Kinds = filter.Kinds, Ip = filter.Ip },
                Limit = 300,
            });
            var blocksTask = blocks || force ? backend.ListBlocksAsync(new BlockListQuery { IncludeEnded = ShowEndedBlocks }) : null;

            if (requestsTask is not null)
            {
                var page = await requestsTask;
                if (version != _version)
                    return;
                var selected = SelectedRequest?.Record.Id;
                Replace(Requests, page.Requests.Select(r => new RequestRow(r)));
                SelectedRequest = Requests.FirstOrDefault(r => r.Record.Id == selected);
                Summary = page.Matched == 0
                    ? $"No request matches. The log keeps the last {page.Capacity:N0} requests."
                    : $"{page.Matched:N0} matching requests"
                      + (page.Matched > page.Requests.Count ? $" (newest {page.Requests.Count:N0} shown)" : "")
                      + $" · the log keeps the last {page.Capacity:N0}"
                      + (page.OldestUtc is { } oldest ? $", since {oldest.ToLocalTime():HH:mm:ss}" : "");
            }

            var top = await topTask;
            if (version != _version)
                return;
            var selectedIp = SelectedTopIp?.Row.Ip;
            Replace(TopIps, top.Rows.Select(r => new TopIpView(r)));
            SelectedTopIp = TopIps.FirstOrDefault(r => r.Row.Ip == selectedIp);
            TopSummary = $"{top.Visitors:N0} visitors · {SelectedWindow.Label.ToLower(CultureInfo.CurrentCulture)} · \"Matching\" counts the requests that fit the method and kind filters.";

            if (blocksTask is not null)
            {
                var list = await blocksTask;
                if (version != _version)
                    return;
                _lastBlocksUtc = DateTime.UtcNow;
                var selectedBlock = SelectedBlock?.Entry.Id;
                var now = DateTime.UtcNow;
                Replace(Blocks, list.Entries.Select(e => new BlockView(e, now)));
                SelectedBlock = Blocks.FirstOrDefault(b => b.Entry.Id == selectedBlock);
            }
        }
        catch (Exception e)
        {
            Summary = "Couldn't load requests: " + e.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private RequestFilter BuildFilter()
    {
        var methods = MethodToggles.Where(t => t.IsChecked).Select(t => t.Value).ToList();
        var kinds = KindToggles.Where(t => t.IsChecked).Select(t => t.Value).ToList();
        return new RequestFilter
        {
            // Everything ticked is the same as no filter.
            Methods = methods.Count == MethodToggles.Count ? [] : methods.Count == 0 ? [(MethodGroup)255] : methods,
            Kinds = kinds.Count == KindToggles.Count ? [] : kinds.Count == 0 ? [(RequestKind)255] : kinds,
            Ip = string.IsNullOrWhiteSpace(IpFilter) ? null : IpFilter,
            PathContains = string.IsNullOrWhiteSpace(PathFilter) ? null : PathFilter,
            SiteId = SelectedSite.Id,
        };
    }

    private void ClearFilters()
    {
        foreach (var toggle in MethodToggles)
            toggle.IsChecked = true;
        foreach (var toggle in KindToggles)
            toggle.IsChecked = true;
        IpFilter = "";
        PathFilter = "";
        SelectedSite = AllSites;
    }

    private async Task BlockAsync(string? ip)
    {
        if (_backend() is not { } backend || AskBlock(ip) is not { } draft)
            return;
        var result = await backend.BlockIpAsync(new ManualBlockRequest { Ip = draft.Ip, Hours = draft.Hours, Method = draft.Method, Note = draft.Note });
        ShowMessage("Block IP", result.Message, !result.Success);
        await RefreshAsync(force: true, blocks: true);
    }

    private async Task UnblockSelectedAsync()
    {
        if (_backend() is not { } backend || SelectedBlock is not { } block)
            return;
        var result = await backend.UnblockIpAsync(new UnblockRequest { Ip = block.Entry.Ip });
        ShowMessage("Unblock IP", result.Message, !result.Success);
        await RefreshAsync(force: true, blocks: true);
    }

    private void RuleFromSelectedRequest()
    {
        if (SelectedRequest is not { } request)
            return;
        var row = new RuleRow { Name = "Requests to " + request.Record.Path, Pattern = request.Record.Path };
        Rules.Add(row);
        SelectedRule = row;
        Raise(nameof(SelectedRule));
    }

    private async Task SaveRulesAsync()
    {
        if (_backend() is not { } backend)
            return;
        var draft = Draft();
        var (errors, _) = RequestsPresentation.Validate(draft);
        if (errors.Count > 0)
        {
            ShowMessage("Blacklist rules", string.Join(Environment.NewLine, errors), true);
            return;
        }

        var settings = backend.Settings.Clone();
        settings.Blocking = draft;
        await _saveSettings(settings);
        EditorDirty = false;
        LoadEditor(backend.Settings, force: true);
        ShowMessage("Blacklist rules", draft.Mode == BlockingMode.Block && !CanBlock
            ? "Saved. Blocking is applied by the IIS Monitor Windows service, which isn't running."
            : "Saved.", false);
    }

    private BlockingSettings Draft() => new()
    {
        Mode = SelectedMode.Mode,
        Rules = Rules.Select(r => r.ToRule()).ToList(),
        AllowList = Lines(AllowListText),
        TrustedProxies = Lines(TrustedProxiesText),
        IisApplyIntervalSeconds = int.TryParse(IisIntervalText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) ? seconds : 60,
        DropOpenConnections = DropOpenConnections,
    };

    private async Task SetupLoggingAsync()
    {
        if (_backend() is not { } backend)
            return;
        if (!backend.Features.Contains(PipeProtocol.Feature.Requests))
        {
            ShowMessage("Set up IIS logging", "The IIS Monitor service is older than this dashboard. Run install-service.cmd from this version's folder first.", true);
            return;
        }

        if (AskLoggingSetup(_lastSites) is not { } request)
            return;
        var result = await backend.ConfigureIisLoggingAsync(request);
        ShowMessage("Set up IIS logging", result.Message, !result.Success);
    }

    private IReadOnlyList<SiteMetrics> _lastSites = [];

    /// <summary>The sites the logging dialog lists; set from each snapshot.</summary>
    public void SetSites(IReadOnlyList<SiteMetrics> sites) => _lastSites = sites;

    private void EditorChanged()
    {
        if (_loadingEditor)
            return;
        EditorDirty = true;
        Validate();
    }

    private void Validate()
    {
        var (errors, warnings) = RequestsPresentation.Validate(Draft());
        EditorMessages = string.Join(Environment.NewLine, errors.Concat(warnings));
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
