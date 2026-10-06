using IISMonitor.Core.Models;

namespace IISMonitor.Core.Metrics;

public enum EntityKind
{
    AppPool,
    Site,
    Server,
}

public enum MetricUnit
{
    Percent,
    Bytes,
    BytesPerSecond,
    Count,
    PerSecond,
    Milliseconds,
}

/// <summary>How live samples are combined into one history row.</summary>
public enum WindowAggregation
{
    Average,
    Max,

    /// <summary>Taken from the response-time window that is drained when the history row is written.</summary>
    ResponseWindow,
}

/// <param name="Key">Metric key; also the column name in the history database.</param>
/// <param name="SourceKey">Live metric the history value is computed from (usually the same key).</param>
/// <param name="RollupSql">SQL aggregate used when several history rows are merged for a chart.</param>
/// <param name="HistoryOnly">True for metrics that only exist in history (such as per-row maximums).</param>
public sealed record MetricDefinition(
    string Key,
    string DisplayName,
    MetricUnit Unit,
    WindowAggregation Aggregation,
    string RollupSql,
    string SourceKey,
    bool HistoryOnly = false);

public sealed record ChartDefinition(string Title, MetricUnit Unit, IReadOnlyList<string> MetricKeys);

/// <summary>
/// The single list of metrics: used to extract live values, build the history schema, aggregate
/// history rows and lay out charts, so all of those always agree.
/// </summary>
public static class MetricCatalog
{
    private static readonly MetricDefinition[] PoolMetricDefs =
    [
        Avg("cpu", "CPU %", MetricUnit.Percent),
        Max("cpu_max", "CPU % (max)", MetricUnit.Percent, "cpu"),
        Avg("private_bytes", "Private memory", MetricUnit.Bytes),
        Avg("working_set", "Working set", MetricUnit.Bytes),
        Max("processes", "Processes", MetricUnit.Count),
        Avg("threads", "Threads", MetricUnit.Count),
        Avg("handles", "Handles", MetricUnit.Count),
        Avg("disk_read", "Disk read", MetricUnit.BytesPerSecond),
        Avg("disk_write", "Disk write", MetricUnit.BytesPerSecond),
        Avg("net_sent", "Network sent", MetricUnit.BytesPerSecond),
        Avg("net_recv", "Network received", MetricUnit.BytesPerSecond),
        Avg("db_conn", "DB connections (TCP)", MetricUnit.Count),
        Max("db_conn_max", "DB connections (max)", MetricUnit.Count, "db_conn"),
        Avg("db_sessions", "DB sessions (SQL)", MetricUnit.Count),
        Avg("db_active", "DB active sessions", MetricUnit.Count),
        Avg("rps", "Requests/sec", MetricUnit.PerSecond),
        Avg("active_requests", "Active requests", MetricUnit.Count),
        Max("queue", "Queue length", MetricUnit.Count),
        .. ResponseMetrics(),
    ];

    private static readonly MetricDefinition[] SiteMetricDefs =
    [
        Avg("rps", "Requests/sec", MetricUnit.PerSecond),
        Avg("connections", "Connections", MetricUnit.Count),
        Max("connections_max", "Connections (max)", MetricUnit.Count, "connections"),
        Avg("bytes_sent", "Bytes sent", MetricUnit.BytesPerSecond),
        Avg("bytes_recv", "Bytes received", MetricUnit.BytesPerSecond),
        .. ResponseMetrics(),
    ];

    private static readonly MetricDefinition[] ServerMetricDefs =
    [
        Avg("cpu", "CPU %", MetricUnit.Percent),
        Max("cpu_max", "CPU % (max)", MetricUnit.Percent, "cpu"),
        Avg("mem_used", "Memory used", MetricUnit.Bytes),
        Avg("mem_total", "Memory total", MetricUnit.Bytes),
    ];

    private static readonly ChartDefinition[] PoolCharts =
    [
        new("CPU", MetricUnit.Percent, ["cpu", "cpu_max"]),
        new("Memory", MetricUnit.Bytes, ["private_bytes", "working_set"]),
        new("Disk I/O", MetricUnit.BytesPerSecond, ["disk_read", "disk_write"]),
        new("Network (outbound from the pool)", MetricUnit.BytesPerSecond, ["net_sent", "net_recv"]),
        new("Database connections", MetricUnit.Count, ["db_conn", "db_conn_max", "db_sessions", "db_active"]),
        new("Requests/sec", MetricUnit.PerSecond, ["rps"]),
        new("Response time", MetricUnit.Milliseconds, ["resp_avg", "resp_p95", "resp_max"]),
        new("Active requests & queue", MetricUnit.Count, ["active_requests", "queue"]),
        new("Errors/sec", MetricUnit.PerSecond, ["err_4xx", "err_5xx"]),
        new("Processes", MetricUnit.Count, ["processes"]),
    ];

    private static readonly ChartDefinition[] SiteCharts =
    [
        new("Requests/sec", MetricUnit.PerSecond, ["rps"]),
        new("Response time", MetricUnit.Milliseconds, ["resp_avg", "resp_p95", "resp_max"]),
        new("Connections", MetricUnit.Count, ["connections", "connections_max"]),
        new("Bandwidth", MetricUnit.BytesPerSecond, ["bytes_sent", "bytes_recv"]),
        new("Errors/sec", MetricUnit.PerSecond, ["err_4xx", "err_5xx"]),
    ];

    private static readonly ChartDefinition[] ServerCharts =
    [
        new("CPU", MetricUnit.Percent, ["cpu", "cpu_max"]),
        new("Memory", MetricUnit.Bytes, ["mem_used", "mem_total"]),
    ];

    public static IReadOnlyList<MetricDefinition> Metrics(EntityKind kind) => kind switch
    {
        EntityKind.AppPool => PoolMetricDefs,
        EntityKind.Site => SiteMetricDefs,
        _ => ServerMetricDefs,
    };

    public static IReadOnlyList<ChartDefinition> Charts(EntityKind kind) => kind switch
    {
        EntityKind.AppPool => PoolCharts,
        EntityKind.Site => SiteCharts,
        _ => ServerCharts,
    };

    public static MetricDefinition? Find(EntityKind kind, string key) =>
        Metrics(kind).FirstOrDefault(m => m.Key == key);

    public static Dictionary<string, double?> Extract(AppPoolMetrics pool, double intervalSeconds)
    {
        var values = new Dictionary<string, double?>
        {
            ["cpu"] = pool.CpuPercent,
            ["private_bytes"] = pool.PrivateBytes,
            ["working_set"] = pool.WorkingSetBytes,
            ["processes"] = pool.ProcessCount,
            ["threads"] = pool.ThreadCount,
            ["handles"] = pool.HandleCount,
            ["disk_read"] = pool.DiskReadBytesPerSec,
            ["disk_write"] = pool.DiskWriteBytesPerSec,
            ["net_sent"] = pool.NetworkSentBytesPerSec,
            ["net_recv"] = pool.NetworkReceivedBytesPerSec,
            ["db_conn"] = pool.DbConnections,
            ["db_sessions"] = pool.DbSessions,
            ["db_active"] = pool.DbActiveSessions,
            ["rps"] = pool.RequestsPerSec,
            ["active_requests"] = pool.ActiveRequests,
            ["queue"] = pool.QueueLength,
        };
        AddResponse(values, pool.Response, intervalSeconds);
        return values;
    }

    public static Dictionary<string, double?> Extract(SiteMetrics site, double intervalSeconds)
    {
        var values = new Dictionary<string, double?>
        {
            ["rps"] = site.RequestsPerSec,
            ["connections"] = site.CurrentConnections,
            ["bytes_sent"] = site.BytesSentPerSec,
            ["bytes_recv"] = site.BytesReceivedPerSec,
        };
        AddResponse(values, site.Response, intervalSeconds);
        return values;
    }

    public static Dictionary<string, double?> Extract(ServerMetrics server) => new()
    {
        ["cpu"] = server.CpuPercent,
        ["mem_used"] = server.MemoryUsedBytes,
        ["mem_total"] = server.MemoryTotalBytes,
    };

    /// <summary>Adds response-time values; averages and percentiles stay empty when there were no requests.</summary>
    public static void AddResponse(Dictionary<string, double?> values, ResponseStats? response, double seconds)
    {
        values["req_count"] = response?.RequestCount;
        var any = response is { RequestCount: > 0 };
        values["resp_avg"] = any ? response!.AverageMs : null;
        values["resp_p95"] = any ? response!.P95Ms : null;
        values["resp_max"] = any ? response!.MaxMs : null;
        values["err_4xx"] = response is null || seconds <= 0 ? null : response.ClientErrors / seconds;
        values["err_5xx"] = response is null || seconds <= 0 ? null : response.ServerErrors / seconds;
    }

    private static MetricDefinition[] ResponseMetrics() =>
    [
        new("req_count", "Requests logged", MetricUnit.Count, WindowAggregation.ResponseWindow, "SUM(req_count)", "req_count", HistoryOnly: true),
        new("resp_avg", "Response avg", MetricUnit.Milliseconds, WindowAggregation.ResponseWindow,
            "SUM(resp_avg * req_count) / NULLIF(SUM(CASE WHEN resp_avg IS NOT NULL THEN req_count END), 0)", "resp_avg"),
        new("resp_p95", "Response p95", MetricUnit.Milliseconds, WindowAggregation.ResponseWindow, "MAX(resp_p95)", "resp_p95"),
        new("resp_max", "Response max", MetricUnit.Milliseconds, WindowAggregation.ResponseWindow, "MAX(resp_max)", "resp_max"),
        new("err_4xx", "4xx errors/sec", MetricUnit.PerSecond, WindowAggregation.ResponseWindow, "AVG(err_4xx)", "err_4xx"),
        new("err_5xx", "5xx errors/sec", MetricUnit.PerSecond, WindowAggregation.ResponseWindow, "AVG(err_5xx)", "err_5xx"),
    ];

    private static MetricDefinition Avg(string key, string name, MetricUnit unit) =>
        new(key, name, unit, WindowAggregation.Average, $"AVG({key})", key);

    private static MetricDefinition Max(string key, string name, MetricUnit unit, string? source = null) =>
        new(key, name, unit, WindowAggregation.Max, $"MAX({key})", source ?? key, HistoryOnly: source is not null);
}
