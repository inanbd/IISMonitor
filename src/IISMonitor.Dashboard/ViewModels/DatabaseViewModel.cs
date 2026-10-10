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
public sealed record DbPoolRow(
    string Name,
    double Load,
    double CpuLoad,
    int Blocked,
    int Blocking,
    int IdleInTransaction,
    int SlowRunning,
    int Sessions,
    int ActiveSessions)
{
    public string LoadText => Load.ToString("0.00", CultureInfo.CurrentCulture);
    public string CpuText => CpuLoad.ToString("0.00", CultureInfo.CurrentCulture);
    public string WaitingText => Math.Max(0, Load - CpuLoad).ToString("0.00", CultureInfo.CurrentCulture);
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
    public string BlockedByText => Query.BlockedBy is { } session ? $"session {session % 1_000_000}" : "";
    public string StatementLine => OneLine(Query.ObjectName is null ? Query.Statement : $"{Query.ObjectName}: {Query.Statement}");
    public bool IsSlow { get; init; }

    public string Details => $"""
        Client: {Client} (process {Query.Pid}, {Query.Program}, login {Query.Login})
        Database: {Query.Database}    Session: {Query.SessionId % 1_000_000}    Command: {Query.Command}
        Running for {ElapsedText} · CPU {CpuText} · {ReadsText} logical reads · {Query.Writes:N0} writes
        Now: {State}{(Query.WaitType is null ? "" : $" [{Query.WaitType}]")}{(Query.BlockedBy is null ? "" : $" · blocked by {BlockedByText}")}
        {(Query.ObjectName is null ? "" : "In: " + Query.ObjectName + Environment.NewLine)}
        {Query.Statement ?? "(statement text not available yet)"}
        """;

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
    public string Pool => string.IsNullOrEmpty(Group.AppPool) ? "(not IIS)" : Group.AppPool;
    public string AverageText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.AverageMs);
    public string MaxText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.MaxMs);
    public double Total => Group.TotalMs;
    public string TotalText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.TotalMs);
    public string CpuText => MetricFormatter.Format(MetricUnit.Milliseconds, Group.TotalCpuMs);
    public string ReadsText => Group.TotalLogicalReads.ToString("N0", CultureInfo.CurrentCulture);
    public string WaitText => Group.MainWait is null ? "CPU" : WaitTypes.Describe(Group.MainWait);
    public string LastSeen => DateTimeOffset.FromUnixTimeMilliseconds(Group.LastSeenUnixMs).LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    /// <summary>The procedure, or else the batch, the runs belong to.</summary>
    public string StatementLine => RunningQueryRow.OneLine(Group.ObjectName ?? Group.Batch ?? Group.Statement);

    public string Details => $"""
        App pool: {Pool}    Database: {Group.Database}
        {Group.Count} slow runs · average {AverageText} · longest {MaxText} · {TotalText} in total · CPU {CpuText} · {ReadsText} logical reads
        {(Group.BlockedCount > 0 ? $"Blocked by other sessions in {Group.BlockedCount} of {Group.Count} runs. " : "")}Mostly: {(Group.MainWait is null ? "running on CPU" : $"{WaitTypes.Describe(Group.MainWait)} [{Group.MainWait}]")}
        Last seen: {LastSeen}

        {(Group.ObjectName is not null ? "Procedure: " + Group.ObjectName : "Batch: " + (Group.Batch ?? Group.Statement))}

        Where the longest run spent most of its time:
        {(string.IsNullOrEmpty(Group.Statement) ? "(statement text not available)" : Group.Statement)}
        """;
}

public sealed record SlowRangeOption(TimeSpan Range, string Label);

/// <summary>
/// The Database tab: which app pools keep SQL Server busy (database load sampled every second),
/// what is running right now, and slow queries over a chosen period.
/// </summary>
public sealed class DatabaseViewModel : ObservableObject
{
    public const string AllPools = "All app pools";

    public static readonly IReadOnlyList<ChartDefinition> Charts =
    [
        new("Database load by app pool", MetricUnit.Count, ["db_load"]),
        new("Blocked queries by app pool", MetricUnit.Count, ["db_blocked"]),
    ];

    private readonly Func<IMonitorBackend?> _backend;
    private string _summary = "";
    private string _slowSummary = "";
    private string _details = "Select a query to see its full text.";
    private bool _isConfigured;
    private bool _hasData;
    private SlowRangeOption _selectedRange;
    private string _selectedPool = AllPools;
    private RunningQueryRow? _selectedRunning;
    private SlowQueryRow? _selectedSlow;
    private string _slowHeader = "Slow queries";
    private DateTime _lastSlowRefreshUtc = DateTime.MinValue;
    private bool _refreshing;
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

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string SlowSummary { get => _slowSummary; private set => Set(ref _slowSummary, value); }

    public string Details { get => _details; private set => Set(ref _details, value); }

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

    /// <summary>Selected running query; its full text shows below the lists.</summary>
    public RunningQueryRow? SelectedRunning
    {
        get => _selectedRunning;
        set
        {
            if (Set(ref _selectedRunning, value) && value is not null)
                Details = value.Details;
        }
    }

    /// <summary>Selected slow-query group; its full text shows below the lists.</summary>
    public SlowQueryRow? SelectedSlow
    {
        get => _selectedSlow;
        set
        {
            if (Set(ref _selectedSlow, value) && value is not null)
                Details = value.Details;
        }
    }

    /// <summary>Updates the live parts from a snapshot. <paramref name="visible"/> lets slow-query lists refresh only when shown.</summary>
    public void Apply(MonitorSnapshot snapshot, bool configured, bool visible)
    {
        IsConfigured = configured;
        var db = snapshot.Database;
        HasData = db is not null;

        var rows = snapshot.AppPools
            .Where(p => p.DbLoad is not null)
            .Select(p => new DbPoolRow(p.Name, p.DbLoad ?? 0, p.DbCpuLoad ?? 0, p.DbBlocked ?? 0, p.DbBlocking ?? 0,
                p.DbIdleInTransaction ?? 0, p.DbSlowRunning ?? 0, p.DbSessions ?? 0, p.DbActiveSessions ?? 0))
            .OrderByDescending(r => r.Load)
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
            Summary = configured ? "Waiting for the first SQL Server sample…" : "";
            Running.Clear();
            return;
        }

        var server = snapshot.Server.SqlServerCpuPercent is { } cpu
            ? $" · SQL Server process CPU {MetricFormatter.Format(MetricUnit.Percent, cpu)}"
            : "";
        Summary = $"Database load (average running queries): IIS app pools {db.PoolLoad:0.00} · other programs on this server {db.OtherLocalLoad:0.00} · other servers {db.OtherServersLoad:0.00}{server}";

        SlowHeader = $"Slow queries (over {MetricFormatter.Format(MetricUnit.Milliseconds, db.SlowThresholdMs)})";

        // Rebuild the list, keeping the selected query selected while it still runs.
        var selectedKey = SelectedRunning?.Query is { } selected ? (selected.SessionId, selected.StartUtc) : default;
        var hadSelection = SelectedRunning is not null;
        Running.Clear();
        RunningQueryRow? reselect = null;
        foreach (var query in db.Running)
        {
            var row = new RunningQueryRow(query) { IsSlow = query.ElapsedMs >= db.SlowThresholdMs };
            Running.Add(row);
            if ((query.SessionId, query.StartUtc) == selectedKey)
                reselect = row;
        }

        if (hadSelection)
        {
            _selectedRunning = reselect;
            Raise(nameof(SelectedRunning));
            if (reselect is not null)
                Details = reselect.Details;
        }

        // New slow queries finished: refresh the report (at most every few seconds) while it is shown.
        if (visible && (db.CompletedSlow.Count > 0 || Slow.Count == 0))
            _ = RefreshSlowAsync(force: false);
    }

    public async Task RefreshSlowAsync(bool force)
    {
        if (_backend() is not { } backend || !IsConfigured)
            return;
        if (!force && (_refreshing || DateTime.UtcNow - _lastSlowRefreshUtc < TimeSpan.FromSeconds(5)))
            return;

        _refreshing = true;
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

            var selectedGroup = SelectedSlow?.Group;
            Slow.Clear();
            SlowQueryRow? reselect = null;
            foreach (var group in report.Groups)
            {
                var row = new SlowQueryRow(group);
                Slow.Add(row);
                if (selectedGroup is not null && group.Statement == selectedGroup.Statement
                    && group.AppPool == selectedGroup.AppPool && group.Database == selectedGroup.Database)
                    reselect = row;
            }

            if (selectedGroup is not null)
            {
                _selectedSlow = reselect;
                Raise(nameof(SelectedSlow));
                if (reselect is not null)
                    Details = reselect.Details;
            }

            SlowSummary = report.Pools.Count == 0
                ? $"No queries over the slow threshold in this period."
                : string.Join("  ·  ", report.Pools.Take(6).Select(p =>
                    $"{(string.IsNullOrEmpty(p.AppPool) ? "(not IIS)" : p.AppPool)}: {p.Count} slow, {MetricFormatter.Format(MetricUnit.Milliseconds, p.TotalMs)} in total"));
        }
        catch (Exception e)
        {
            SlowSummary = "Couldn't load slow queries: " + e.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }
}
