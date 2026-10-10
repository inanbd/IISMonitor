using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;

namespace IISMonitor.Dashboard.ViewModels;

/// <summary>
/// One app pool in the grid. Numeric properties drive sorting; *Text properties are what the
/// cells show. Updated in place on every snapshot so selection and scroll position survive.
/// </summary>
public sealed class AppPoolRow : ObservableObject
{
    public AppPoolRow(string name) => Name = name;

    public string Name { get; }

    public AppPoolMetrics Metrics { get; private set; } = new();

    public string State => Metrics.State;
    public bool IsStarted => Metrics.State.Equals("Started", StringComparison.OrdinalIgnoreCase);
    public string Runtime => $"{Metrics.RuntimeVersion} · {Metrics.PipelineMode}";
    public string Identity => Metrics.Identity;
    public string ApplicationsText => string.Join(", ", Metrics.Applications);

    public int ProcessCount => Metrics.ProcessCount;
    public string ProcessText => Metrics.ProcessCount == Metrics.WorkerProcessCount
        ? Metrics.ProcessCount.ToString()
        : $"{Metrics.ProcessCount} ({Metrics.WorkerProcessCount} w3wp)";

    public double Cpu => Metrics.CpuPercent;
    public string CpuText => MetricFormatter.Format(MetricUnit.Percent, Metrics.CpuPercent);

    public long PrivateBytes => Metrics.PrivateBytes;
    public string PrivateText => MetricFormatter.Format(MetricUnit.Bytes, Metrics.PrivateBytes);
    public long WorkingSet => Metrics.WorkingSetBytes;
    public string WorkingSetText => MetricFormatter.Format(MetricUnit.Bytes, Metrics.WorkingSetBytes);

    public double DiskTotal => (Metrics.DiskReadBytesPerSec ?? 0) + (Metrics.DiskWriteBytesPerSec ?? 0);
    public string DiskText => Pair(MetricUnit.BytesPerSecond, Metrics.DiskReadBytesPerSec, Metrics.DiskWriteBytesPerSec);

    /// <summary>Site traffic plus outbound traffic, as on the Overview tab.</summary>
    public double NetworkTotal => MetricCatalog.NetworkTotal(Metrics) ?? -1;
    public string NetworkText => Pair(
        MetricUnit.BytesPerSecond,
        SumOrNull(Metrics.HttpBytesSentPerSec, Metrics.NetworkSentBytesPerSec),
        SumOrNull(Metrics.HttpBytesReceivedPerSec, Metrics.NetworkReceivedBytesPerSec));

    public int DbConnections => Metrics.DbConnections ?? -1;
    public string DbConnectionsText => MetricFormatter.Format(MetricUnit.Count, Metrics.DbConnections);
    public int DbSessions => Metrics.DbSessions ?? -1;
    public string DbSessionsText => Metrics.DbSessions is null
        ? MetricFormatter.Missing
        : $"{Metrics.DbSessions} ({Metrics.DbActiveSessions ?? 0} active)";

    /// <summary>Average number of the pool's queries running in SQL Server over the last minute (steady enough to compare pools).</summary>
    public double DbLoad => Metrics.DbLoad1m ?? Metrics.DbLoad ?? -1;
    public string DbLoadText => (Metrics.DbLoad1m ?? Metrics.DbLoad) is { } load ? load.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) : MetricFormatter.Missing;

    public double RequestsPerSec => Metrics.RequestsPerSec ?? -1;
    public string RequestsText => MetricFormatter.Format(MetricUnit.PerSecond, Metrics.RequestsPerSec);
    public int ActiveRequests => Metrics.ActiveRequests ?? -1;
    public string ActiveRequestsText => MetricFormatter.Format(MetricUnit.Count, Metrics.ActiveRequests);
    public int Queue => Metrics.QueueLength ?? -1;
    public string QueueText => MetricFormatter.Format(MetricUnit.Count, Metrics.QueueLength);

    public double ResponseAvg => Metrics.Response is { RequestCount: > 0 } r ? r.AverageMs : -1;
    public string ResponseAvgText => ResponseText(Metrics.Response, r => r.AverageMs);
    public double ResponseP95 => Metrics.Response is { RequestCount: > 0 } r ? r.P95Ms : -1;
    public string ResponseP95Text => ResponseText(Metrics.Response, r => r.P95Ms);

    public void Update(AppPoolMetrics metrics)
    {
        Metrics = metrics;
        Raise(string.Empty);
    }

    private static double? SumOrNull(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    internal static string Pair(MetricUnit unit, double? first, double? second) =>
        first is null && second is null
            ? MetricFormatter.Missing
            : $"{MetricFormatter.Format(unit, first)} / {MetricFormatter.Format(unit, second)}";

    internal static string ResponseText(ResponseStats? response, Func<ResponseStats, double> select) =>
        response switch
        {
            null => MetricFormatter.Missing,
            { RequestCount: 0 } => "idle",
            _ => MetricFormatter.Format(MetricUnit.Milliseconds, select(response)),
        };
}

public sealed class SiteRow : ObservableObject
{
    public SiteRow(string name) => Name = name;

    public string Name { get; }

    public SiteMetrics Metrics { get; private set; } = new();

    public long Id => Metrics.Id;
    public string State => Metrics.State;
    public bool IsStarted => Metrics.State.Equals("Started", StringComparison.OrdinalIgnoreCase);
    public string AppPool => Metrics.AppPool;
    public string BindingsText => string.Join(", ", Metrics.Bindings);
    public string TracingText => Metrics.EtwLoggingEnabled ? "On" : "Off";

    public int Connections => Metrics.CurrentConnections ?? -1;
    public string ConnectionsText => MetricFormatter.Format(MetricUnit.Count, Metrics.CurrentConnections);

    public double RequestsPerSec => Metrics.RequestsPerSec ?? -1;
    public string RequestsText => MetricFormatter.Format(MetricUnit.PerSecond, Metrics.RequestsPerSec);

    public double Bandwidth => (Metrics.BytesSentPerSec ?? 0) + (Metrics.BytesReceivedPerSec ?? 0);
    public string BandwidthText => AppPoolRow.Pair(MetricUnit.BytesPerSecond, Metrics.BytesSentPerSec, Metrics.BytesReceivedPerSec);

    public double ResponseAvg => Metrics.Response is { RequestCount: > 0 } r ? r.AverageMs : -1;
    public string ResponseAvgText => AppPoolRow.ResponseText(Metrics.Response, r => r.AverageMs);
    public double ResponseP95 => Metrics.Response is { RequestCount: > 0 } r ? r.P95Ms : -1;
    public string ResponseP95Text => AppPoolRow.ResponseText(Metrics.Response, r => r.P95Ms);
    public double ResponseMax => Metrics.Response is { RequestCount: > 0 } r ? r.MaxMs : -1;
    public string ResponseMaxText => AppPoolRow.ResponseText(Metrics.Response, r => r.MaxMs);

    public int ServerErrors => Metrics.Response?.ServerErrors ?? -1;
    public string ErrorsText => Metrics.Response is { } r ? $"{r.ClientErrors} / {r.ServerErrors}" : MetricFormatter.Missing;

    public void Update(SiteMetrics metrics)
    {
        Metrics = metrics;
        Raise(string.Empty);
    }
}

public sealed class ProcessRow
{
    public ProcessRow(ProcessMetrics p)
    {
        Pid = p.Pid;
        Name = p.Name;
        Role = p.IsWorkerProcess ? "Worker" : $"Child of {p.ParentPid}";
        Cpu = p.CpuPercent;
        CpuText = MetricFormatter.Format(MetricUnit.Percent, p.CpuPercent);
        PrivateText = MetricFormatter.Format(MetricUnit.Bytes, p.PrivateBytes);
        WorkingSetText = MetricFormatter.Format(MetricUnit.Bytes, p.WorkingSetBytes);
        Threads = p.ThreadCount;
        Handles = p.HandleCount;
        DiskText = AppPoolRow.Pair(MetricUnit.BytesPerSecond, p.DiskReadBytesPerSec, p.DiskWriteBytesPerSec);
        NetworkText = AppPoolRow.Pair(MetricUnit.BytesPerSecond, p.NetworkSentBytesPerSec, p.NetworkReceivedBytesPerSec);
        DbText = p.DbSessions is { } sessions
            ? $"{MetricFormatter.Format(MetricUnit.Count, p.DbConnections)} TCP · {sessions} sessions"
            : MetricFormatter.Format(MetricUnit.Count, p.DbConnections);
        Started = p.StartTimeUtc?.ToLocalTime().ToString("g") ?? MetricFormatter.Missing;
    }

    public int Pid { get; }
    public string Name { get; }
    public string Role { get; }
    public double Cpu { get; }
    public string CpuText { get; }
    public string PrivateText { get; }
    public string WorkingSetText { get; }
    public int Threads { get; }
    public int Handles { get; }
    public string DiskText { get; }
    public string NetworkText { get; }
    public string DbText { get; }
    public string Started { get; }
}

public sealed record HealthRow(string Name, bool Ok, string Message)
{
    public string Icon => Ok ? "✔" : "⚠";
}
