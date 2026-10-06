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
    /// Optional connection strings used to ask SQL Server for its sessions per client process.
    /// This also counts shared-memory and named-pipe connections and splits active from idle.
    /// The login needs the VIEW SERVER STATE permission.
    /// </summary>
    public List<string> SqlServerConnectionStrings { get; set; } = [];

    public int SqlSessionQueryIntervalSeconds { get; set; } = 5;

    /// <summary>Kernel ETW tracing for per-process disk and network bytes.</summary>
    public bool EnableKernelTracing { get; set; } = true;

    /// <summary>Listen to IIS's ETW log stream for live response times.</summary>
    public bool EnableResponseTimeTracing { get; set; } = true;

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
        copy.SqlSessionQueryIntervalSeconds = Math.Clamp(copy.SqlSessionQueryIntervalSeconds, 1, 300);
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
    };
}
