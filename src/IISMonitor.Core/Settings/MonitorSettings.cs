using System.Text.Json;
using System.Text.Json.Serialization;

namespace IISMonitor.Core.Settings;

public sealed class MonitorSettings
{
    public const int MinSampleIntervalMs = 250;
    public const int MaxSampleIntervalMs = 60_000;
    public const int MinHistoryIntervalSeconds = 5;
    public const int MaxHistoryIntervalSeconds = 300;
    public const int MaxRetentionDays = 31;
    public const int MaxRequestLogRetentionDays = 31;

    /// <summary>How often live data is collected and pushed to the dashboard.</summary>
    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>Resolution of stored history: one row per entity per this many seconds.</summary>
    public int HistoryIntervalSeconds { get; set; } = 10;

    public int HistoryRetentionDays { get; set; } = 7;

    /// <summary>Remote ports that identify a SQL Server connection.</summary>
    public List<int> SqlServerPorts { get; set; } = [1433];

    /// <summary>Also treat ports that a local SQL Server (sqlservr.exe) listens on as SQL Server ports.</summary>
    public bool DetectLocalSqlServerPorts { get; set; } = true;

    /// <summary>
    /// Optional connection strings of SQL Servers to watch. Every second the service asks each one
    /// which queries are running and which client process sent them: that gives database load and
    /// slow queries per app pool, and sessions per pool including shared-memory and named-pipe
    /// connections. The login needs the VIEW SERVER STATE permission.
    /// </summary>
    public List<string> SqlServerConnectionStrings { get; set; } = [];

    /// <summary>
    /// How often SQL Server is asked which queries are running. One second keeps database load
    /// accurate and catches every query that runs longer than the slow-query threshold.
    /// </summary>
    public int SqlActivityIntervalSeconds { get; set; } = 1;

    /// <summary>Queries running at least this long are recorded as slow queries.</summary>
    public double SlowQueryThresholdSeconds { get; set; } = 2;

    /// <summary>Kernel ETW tracing for per-process disk and network bytes.</summary>
    public bool EnableKernelTracing { get; set; } = true;

    /// <summary>Listen to IIS's ETW log stream for live response times.</summary>
    public bool EnableResponseTimeTracing { get; set; } = true;

    /// <summary>App pools whose requests are recorded per client IP and URL (the IPs &amp; URLs tab).</summary>
    public List<string> RequestTrackingPools { get; set; } = [];

    /// <summary>How long recorded requests are kept, in days.</summary>
    public int RequestLogRetentionDays { get; set; } = 3;

    public MonitorSettings Normalize()
    {
        var copy = Clone();
        copy.SampleIntervalMs = Math.Clamp(copy.SampleIntervalMs, MinSampleIntervalMs, MaxSampleIntervalMs);
        copy.HistoryIntervalSeconds = Math.Clamp(copy.HistoryIntervalSeconds, MinHistoryIntervalSeconds, MaxHistoryIntervalSeconds);
        // A history row can't be finer than the samples it is made of.
        copy.HistoryIntervalSeconds = Math.Max(copy.HistoryIntervalSeconds, (int)Math.Ceiling(copy.SampleIntervalMs / 1000.0));
        copy.HistoryRetentionDays = Math.Clamp(copy.HistoryRetentionDays, 1, MaxRetentionDays);
        copy.SqlServerPorts = (copy.SqlServerPorts ?? []).Where(p => p is > 0 and <= 65535).Distinct().Order().ToList();
        copy.SqlServerConnectionStrings = (copy.SqlServerConnectionStrings ?? [])
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        copy.SqlActivityIntervalSeconds = Math.Clamp(copy.SqlActivityIntervalSeconds, 1, 10);
        copy.SlowQueryThresholdSeconds = double.IsFinite(copy.SlowQueryThresholdSeconds)
            ? Math.Clamp(copy.SlowQueryThresholdSeconds, 0.5, 3600)
            : 2;
        copy.RequestTrackingPools = (copy.RequestTrackingPools ?? [])
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        copy.RequestLogRetentionDays = Math.Clamp(copy.RequestLogRetentionDays, 1, MaxRequestLogRetentionDays);
        return copy;
    }

    public MonitorSettings Clone() =>
        JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(this, SettingsJson.Options), SettingsJson.Options)!;
}

public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Settings can arrive over the pipe with NaN/Infinity; Normalize() replaces them.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
