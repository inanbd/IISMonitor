using IISMonitor.Core.Metrics;

namespace IISMonitor.Core.History;

public sealed class HistoryQuery
{
    public EntityKind Kind { get; set; }
    public string Name { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>Rows are merged so that no more than this many points come back.</summary>
    public int MaxPoints { get; set; } = 1500;
}

public sealed class HistoryResult
{
    public EntityKind Kind { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Seconds each returned point covers.</summary>
    public int BucketSeconds { get; set; }

    /// <summary>Point times, in Unix milliseconds (UTC).</summary>
    public List<long> Timestamps { get; set; } = [];

    /// <summary>Values per metric key, aligned with <see cref="Timestamps"/>.</summary>
    public Dictionary<string, List<double?>> Series { get; set; } = [];
}

/// <summary>One history row: aggregated metric values for an entity over one history interval.</summary>
public sealed record HistoryRow(EntityKind Kind, string Name, DateTime BucketStartUtc, IReadOnlyDictionary<string, double?> Values);

public sealed class SlowQueryRequest
{
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>Only this app pool; null for all.</summary>
    public string? AppPool { get; set; }

    public int MaxGroups { get; set; } = 200;
}

/// <summary>Slow queries in a time range, grouped by app pool, database and query shape.</summary>
public sealed class SlowQueryReport
{
    public List<SlowQueryGroup> Groups { get; set; } = [];

    /// <summary>Totals per app pool, most slow time first ("" = not an IIS app pool).</summary>
    public List<SlowQueryPoolTotal> Pools { get; set; } = [];
}

public sealed class SlowQueryGroup
{
    public string AppPool { get; set; } = "";
    public string Database { get; set; } = "";
    public string? ObjectName { get; set; }
    public string Statement { get; set; } = "";
    public int Count { get; set; }
    public double AverageMs { get; set; }
    public double MaxMs { get; set; }
    public double TotalMs { get; set; }
    public double TotalCpuMs { get; set; }
    public long TotalLogicalReads { get; set; }
    public int BlockedCount { get; set; }
    public string? MainWait { get; set; }
    public long LastSeenUnixMs { get; set; }
}

public sealed class SlowQueryPoolTotal
{
    public string AppPool { get; set; } = "";
    public int Count { get; set; }
    public double TotalMs { get; set; }
    public double TotalCpuMs { get; set; }
}
