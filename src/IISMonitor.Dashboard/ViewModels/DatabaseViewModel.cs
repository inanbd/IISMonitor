using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Presentation;
using IISMonitor.Core.Protocol;

namespace IISMonitor.Dashboard.ViewModels;

/// <summary>One app pool's use of SQL Server.</summary>
/// <param name="Load1m">Average running queries over the last minute; the grid ranks by it.</param>
/// <param name="Load">Average since the previous update.</param>
public sealed record DbPoolRow(
    string Name,
    double Load1m,
    double CpuLoad1m,
    double Load,
    int Blocked,
    int Blocking,
    int IdleInTransaction,
    int SlowRunning,
    int Sessions,
    int ActiveSessions)
{
    public double Waiting1m => Math.Max(0, Load1m - CpuLoad1m);
    public string Load1mText => Load1m.ToString("0.00", CultureInfo.CurrentCulture);
    public string CpuText => CpuLoad1m.ToString("0.00", CultureInfo.CurrentCulture);
    public string WaitingText => Waiting1m.ToString("0.00", CultureInfo.CurrentCulture);
    public string LoadText => Load.ToString("0.00", CultureInfo.CurrentCulture);
    public string SessionsText => $"{Sessions} ({ActiveSessions} active)";
}

/// <summary>A query running right now, as a grid row.</summary>
public sealed class RunningQueryRow(RunningQuery query)
{
    public RunningQuery Query { get; } = query;
    public string Client => Query.AppPool ?? $"PID {Query.Pid} · {Query.Program}";
    public double Elapsed => Query.ElapsedMs;
    public string ElapsedText => MetricFormatter.Format(MetricUnit.Milliseconds, Query.ElapsedMs);
    public string CpuText => MetricFormatter.Format(MetricUnit.Milliseconds, Query.CpuMs);
    public string ReadsText => Query.LogicalReads.ToString("N0", CultureInfo.CurrentCulture);
    public string State => WaitTypes.Describe(Query.WaitType, Query.Status);

    public string BlockedByText => Query.BlockedBy is not { } session
        ? ""
        : $"session {session % 1_000_000}" + (Query.BlockerAppPool is { } pool ? $" ({pool})" : Query.BlockerPid is { } pid ? $" (PID {pid})" : "");

    public string StatementLine => OneLine(Query.ObjectName is null ? Query.Statement : $"{Query.ObjectName}: {Query.Statement}");
    public bool IsSlow { get; init; }

    /// <summary>Identifies the query across updates.</summary>
    public (int Session, DateTime Start) Key => (Query.SessionId, Query.StartUtc);

    /// <summary>Live facts, refreshed on every update.</summary>
    public string Info =>
        $"Client: {Client} (process {Query.Pid}, {Query.Program}, login {Query.Login})" + Environment.NewLine +
        $"Database: {Query.Database}    Session: {Query.SessionId % 1_000_000}    Command: {Query.Command}" + Environment.NewLine +
        $"Running for {ElapsedText} · CPU {CpuText} · {ReadsText} logical reads · {Query.Writes:N0} writes" + Environment.NewLine +
        $"Now: {State}{(Query.WaitType is null ? "" : $" [{Query.WaitType}]")}{(Query.BlockedBy is null ? "" : $" · blocked by {BlockedByText}")}";

    /// <summary>The statement; only changes when the query moves on to another one.</summary>
    public string Text =>
        (Query.ObjectName is null ? "" : "In " + Query.ObjectName + ":" + Environment.NewLine) +
        (Query.Statement ?? "(statement text not available yet)");

    internal static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>A group of slow queries with the same shape, as a grid row.</summary>
public sealed class SlowQueryRow(SlowQueryGroup group)
{
    public SlowQueryGroup Group { get; } = group;
    public string Pool => PoolName(Group.AppPool);
    public string AverageText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.AverageMs);
    public string MaxText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.MaxMs);
    public double Total => Group.TotalMs;
    public string TotalText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.TotalMs);
    public string CpuText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.TotalCpuMs);
    public string ReadsText => Group.TotalLogicalReads.ToString("N0", CultureInfo.CurrentCulture);
    public string WaitText => Group.MainWait is null or "CPU" ? "Running on CPU" : WaitTypes.Describe(Group.MainWait);
    public string LastSeen => DateTimeOffset.FromUnixTimeMilliseconds(Group.LastSeenUnixMs).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The procedure, or else the batch, the runs belong to.</summary>
    public string StatementLine => RunningQueryRow.OneLine(Group.ObjectName ?? Group.Batch ?? Group.Statement);

    /// <summary>Whether two rows are the same group (as the history store groups them).</summary>
    public bool SameGroupAs(SlowQueryGroup other) =>
        Group.AppPool == other.AppPool && Group.Database == other.Database
        && Group.ObjectName == other.ObjectName && Group.Batch == other.Batch
        && (Group.ObjectName is not null || Group.Batch is not null || Group.Statement == other.Statement);

    public string Info
    {
        get
        {
            var blocked = Group.BlockedCount == 0
                ? ""
                : $"Blocked by other sessions in {Group.BlockedCount} of {Group.Count} runs"
                  + Group.BlockerAppPool switch
                  {
                      null => "",
                      "" => ", most often by a program that isn't an IIS app pool",
                      var pool => $", most often by app pool {pool}",
                  } + ". ";
            var mostly = Group.MainWait is null or "CPU" ? "running on CPU" : $"{WaitTypes.Describe(Group.MainWait)} [{Group.MainWait}]";
            return $"App pool: {Pool}    Database: {Group.Database}    Last seen: {LastSeen}" + Environment.NewLine +
                   $"{Group.Count} slow runs · average {AverageText} · longest {MaxText} · {TotalText} in total · CPU {CpuText} · {ReadsText} logical reads" + Environment.NewLine +
                   $"{blocked}Mostly: {mostly}";
        }
    }

    public string Text =>
        (Group.ObjectName is not null ? "Procedure called: " + Group.ObjectName : "Batch sent: " + (Group.Batch ?? Group.Statement)) +
        Environment.NewLine + Environment.NewLine +
        "Where the longest run spent most of its time" + (Group.StatementObject is null ? "" : $" (in {Group.StatementObject})") + ":" +
        Environment.NewLine + (string.IsNullOrEmpty(Group.Statement) ? "(statement text not available)" : Group.Statement);

    private static string PoolName(string pool) => string.IsNullOrEmpty(pool) ? "(not IIS)" : pool;
}

public sealed record SlowRangeOption(TimeSpan Range, string Label);

/// <summary>
/// The Database tab: which app pools keep SQL Server busy (database load sampled every second),
/// what is running right now, and slow queries over a chosen period.
/// </summary>
public sealed class DatabaseViewModel : ObservableObject
{
    public const string AllPools = "All app pools";
    public const string HealthName = "SQL Server activity";
    private static readonly TimeSpan SlowRefreshThrottle = TimeSpan.FromSeconds(5);
    private const string NoSelection = "Select a query to see its full text.";

    public static readonly IReadOnlyList<ChartDefinition> Charts =
    [
        new("Database load by app pool", MetricUnit.Count, ["db_load"]),
        new("Blocked queries by app pool", MetricUnit.Count, ["db_blocked"]),
    ];

    private readonly Func<IMonitorBackend?> _backend;
    private string _summary = "";
    private string _slowSummary = "";
    private string _detailsInfo = NoSelection;
    private string _detailsText = "";
    private string _statusMessage = "";
    private bool _isConfigured;
    private bool _hasData;
    private bool _serviceOutdated;
    private SlowRangeOption _selectedRange;
    private string _selectedPool = AllPools;
    private RunningQueryRow? _selectedRunning;
    private SlowQueryRow? _selectedSlow;
    private string _slowHeader = "Slow queries";
    private DateTime _lastSlowRefreshUtc = DateTime.MinValue;
    private bool _refreshing;
    private bool _refreshPending;
    private int _version;

    public DatabaseViewModel(Func<IMonitorBackend?> backend, Action<Exception> onError)
    {
        _backend = backend;
        Ranges =
        [
            new(TimeSpan.FromMinutes(15), "Last 15 minutes"), new(TimeSpan.FromHours(1), "Last hour"),
            new(TimeSpan.FromHours(24), "Last 24 hours"), new(TimeSpan.FromDays(7), "Last 7 days"),
        ];
        _selectedRange = Ranges[1];
        PoolFilter.Add(AllPools);
        RefreshSlowCommand = new AsyncCommand(() => RefreshSlowAsync(force: true), onError);
    }

    public ObservableCollection<DbPoolRow> Pools { get; } = [];
    public ObservableCollection<RunningQueryRow> Running { get; } = [];
    public ObservableCollection<SlowQueryRow> Slow { get; } = [];
    public ObservableCollection<string> PoolFilter { get; } = [];
    public IReadOnlyList<SlowRangeOption> Ranges { get; }
    public ICommand RefreshSlowCommand { get; }

    /// <summary>A SQL Server connection string is configured.</summary>
    public bool IsConfigured { get => _isConfigured; private set { Set(ref _isConfigured, value); Raise(nameof(ShowSetup)); } }

    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

    public bool ShowSetup => !IsConfigured;

    /// <summary>The service the dashboard talks to predates SQL Server activity.</summary>
    public bool ServiceOutdated { get => _serviceOutdated; private set => Set(ref _serviceOutdated, value); }

    /// <summary>Why SQL Server can't be sampled (connection or permission problem); empty when fine.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value))
                Raise(nameof(HasStatusProblem));
        }
    }

    public bool HasStatusProblem => StatusMessage.Length > 0;

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string SlowSummary { get => _slowSummary; private set => Set(ref _slowSummary, value); }

    /// <summary>Live facts about the selected query.</summary>
    public string DetailsInfo { get => _detailsInfo; private set => Set(ref _detailsInfo, value); }

    /// <summary>The selected query's text, kept apart so selecting and copying it isn't undone by updates.</summary>
    public string DetailsText { get => _detailsText; private set => Set(ref _detailsText, value); }

    public SlowRangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is not null && Set(ref _selectedRange, value))
                _ = RefreshSlowAsync(force: true);
        }
    }

    public string SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (value is not null && Set(ref _selectedPool, value))
                _ = RefreshSlowAsync(force: true);
        }
    }

    public string SlowHeader { get => _slowHeader; private set => Set(ref _slowHeader, value); }

    /// <summary>Selected running query; its full text shows below the lists. Selecting one clears the slow-query selection.</summary>
    public RunningQueryRow? SelectedRunning
    {
        get => _selectedRunning;
        set
        {
            if (!Set(ref _selectedRunning, value) || value is null)
                return;
            ClearSlowSelection();
            ShowDetails(value.Info, value.Text);
        }
    }

    /// <summary>Selected slow-query group; its full text shows below the lists. Selecting one clears the running selection.</summary>
    public SlowQueryRow? SelectedSlow
    {
        get => _selectedSlow;
        set
        {
            if (!Set(ref _selectedSlow, value) || value is null)
                return;
            ClearRunningSelection();
            ShowDetails(value.Info, value.Text);
        }
    }

    /// <summary>Updates the live parts from a snapshot. <paramref name="visible"/> lets slow-query lists refresh only when shown.</summary>
    public void Apply(MonitorSnapshot snapshot, bool configured, bool visible)
    {
        IsConfigured = configured;
        ServiceOutdated = _backend() is { } backend && !backend.Features.Contains(PipeProtocol.Feature.Database);
        var health = snapshot.Health.FirstOrDefault(h => h.Name == HealthName);
        StatusMessage = configured && health is { Ok: false } ? health.Message : "";

        var db = snapshot.Database;
        HasData = db is not null;

        var rows = snapshot.AppPools
            .Where(p => p.DbLoad is not null)
            .Select(p => new DbPoolRow(p.Name, p.DbLoad1m ?? p.DbLoad ?? 0, p.DbCpuLoad1m ?? p.DbCpuLoad ?? 0, p.DbLoad ?? 0,
                p.DbBlocked ?? 0, p.DbBlocking ?? 0, p.DbIdleInTransaction ?? 0, p.DbSlowRunning ?? 0, p.DbSessions ?? 0, p.DbActiveSessions ?? 0))
            .OrderByDescending(r => r.Load1m)
            .ThenByDescending(r => r.Load)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!rows.SequenceEqual(Pools))
        {
            Pools.Clear();
            foreach (var row in rows)
                Pools.Add(row);
        }

        foreach (var name in snapshot.AppPools.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!PoolFilter.Contains(name))
                PoolFilter.Add(name);
        }

        if (db is null)
        {
            Summary = !configured ? ""
                : HasStatusProblem ? "No SQL Server sample yet."
                : "Waiting for the first SQL Server sample…";
            Running.Clear();
            ClearRunningSelection();
            return;
        }

        var server = snapshot.Server.SqlServerCpuPercent is { } cpu
            ? $" · SQL Server process CPU {MetricFormatter.Format(MetricUnit.Percent, cpu)}"
            : "";
        Summary = $"Database load over the last minute (average running queries): IIS app pools {db.PoolLoad1m:0.00} · other programs on this server {db.OtherLocalLoad1m:0.00} · other servers {db.OtherServersLoad1m:0.00}{server}";

        SlowHeader = $"Slow queries (over {MetricFormatter.Format(MetricUnit.Milliseconds, db.SlowThresholdMs)})";

        // Rebuild the list, keeping the selected query selected while it still runs.
        var selectedKey = SelectedRunning?.Key;
        Running.Clear();
        RunningQueryRow? reselect = null;
        foreach (var query in db.Running)
        {
            var row = new RunningQueryRow(query) { IsSlow = query.ElapsedMs >= db.SlowThresholdMs };
            Running.Add(row);
            if (row.Key == selectedKey)
                reselect = row;
        }

        if (selectedKey is not null)
        {
            _selectedRunning = reselect;
            Raise(nameof(SelectedRunning));
            if (reselect is not null)
                ShowDetails(reselect.Info, reselect.Text);
            else
                DetailsInfo = "This query has finished. " + DetailsInfo;
        }

        // Refresh the report (at most every few seconds) while it is shown, when new slow queries
        // finished or a refresh was held back earlier.
        if (db.CompletedSlow.Count > 0 || Slow.Count == 0)
            _refreshPending = true;
        if (visible && _refreshPending)
            _ = RefreshSlowAsync(force: false);
    }

    public async Task RefreshSlowAsync(bool force)
    {
        if (_backend() is not { } backend || !IsConfigured)
            return;
        if (!force && (_refreshing || DateTime.UtcNow - _lastSlowRefreshUtc < SlowRefreshThrottle))
        {
            _refreshPending = true;
            return;
        }

        _refreshing = true;
        _refreshPending = false;
        _lastSlowRefreshUtc = DateTime.UtcNow;
        var version = ++_version;
        try
        {
            var now = DateTime.UtcNow;
            var report = await backend.QuerySlowQueriesAsync(new SlowQueryRequest
            {
                FromUtc = now - SelectedRange.Range,
                ToUtc = now.AddMinutes(1),
                AppPool = SelectedPool == AllPools ? null : SelectedPool,
            });
            if (version != _version)
                return;

            var selected = SelectedSlow;
            Slow.Clear();
            SlowQueryRow? reselect = null;
            foreach (var group in report.Groups)
            {
                var row = new SlowQueryRow(group);
                Slow.Add(row);
                if (reselect is null && selected?.SameGroupAs(group) == true)
                    reselect = row;
            }

            if (selected is not null)
            {
                _selectedSlow = reselect;
                Raise(nameof(SelectedSlow));
                if (reselect is not null)
                    ShowDetails(reselect.Info, reselect.Text);
            }

            SlowSummary = report.Pools.Count == 0
                ? "No queries over the slow threshold in this period."
                : string.Join("  ·  ", report.Pools.Take(6).Select(p =>
                    $"{(string.IsNullOrEmpty(p.AppPool) ? "(not IIS)" : p.AppPool)}: {p.Count} slow, {MetricFormatter.Format(MetricUnit.Milliseconds, p.TotalMs)} in total"));
        }
        catch (Exception e)
        {
            SlowSummary = ServiceOutdated
                ? "The IIS Monitor service is older than this dashboard and can't list slow queries."
                : "Couldn't load slow queries: " + e.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowDetails(string info, string text)
    {
        DetailsInfo = info;
        DetailsText = text;
    }

    private void ClearRunningSelection()
    {
        if (_selectedRunning is null)
            return;
        _selectedRunning = null;
        Raise(nameof(SelectedRunning));
    }

    private void ClearSlowSelection()
    {
        if (_selectedSlow is null)
            return;
        _selectedSlow = null;
        Raise(nameof(SelectedSlow));
    }
}
