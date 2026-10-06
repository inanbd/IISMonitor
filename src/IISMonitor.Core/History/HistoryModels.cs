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
