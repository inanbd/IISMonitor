using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>
/// Everything the Windows collectors gathered for one tick, in raw (mostly cumulative) form.
/// <see cref="SnapshotComposer"/> turns it into a <see cref="MonitorSnapshot"/>.
/// Any part may be null when its collector is disabled or failed.
/// </summary>
public sealed class CollectionInput
{
    public DateTime TimestampUtc { get; init; }

    public IisTopology? Topology { get; init; }

    /// <summary>Every process on the machine (used to find worker processes and their children).</summary>
    public IReadOnlyList<ProcessEntry> Processes { get; init; } = [];

    /// <summary>Worker process PID to app pool name.</summary>
    public IReadOnlyDictionary<int, string> WorkerProcessPools { get; init; } = new Dictionary<int, string>();

    /// <summary>Cumulative counters for the processes that belong to app pools, by PID.</summary>
    public IReadOnlyDictionary<int, ProcessSample> ProcessSamples { get; init; } = new Dictionary<int, ProcessSample>();

    /// <summary>Cumulative per-process disk and network bytes from kernel ETW, by PID. Null when ETW is unavailable.</summary>
    public IReadOnlyDictionary<int, IoTotals>? EtwIo { get; init; }

    public IisCounterValues? Counters { get; init; }

    /// <summary>Established TCP connections to SQL Server ports, by owning PID.</summary>
    public IReadOnlyDictionary<int, int>? DbConnectionsByPid { get; init; }

    /// <summary>Sessions reported by SQL Server, by client PID. Null when no DMV connection is configured.</summary>
    public IReadOnlyDictionary<int, DbSessionCount>? DbSessionsByPid { get; init; }

    public IReadOnlyDictionary<long, ResponseStats>? ResponseBySite { get; init; }
    public IReadOnlyDictionary<string, ResponseStats>? ResponseByPool { get; init; }

    public SystemSample? System { get; init; }

    public List<CollectorHealth> Health { get; init; } = [];
}

public sealed class IisTopology
{
    public List<SiteInfo> Sites { get; init; } = [];
    public List<AppPoolInfo> AppPools { get; init; } = [];
}

public sealed class SiteInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public List<string> Bindings { get; init; } = [];
    public List<ApplicationInfo> Applications { get; init; } = [];
    public bool EtwLoggingEnabled { get; init; }

    public string RootAppPool =>
        Applications.FirstOrDefault(a => a.Path == "/")?.AppPool ?? Applications.FirstOrDefault()?.AppPool ?? "";
}

public sealed record ApplicationInfo(string Path, string AppPool);

public sealed class AppPoolInfo
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public string PipelineMode { get; init; } = "";
    public string Identity { get; init; } = "";
}

public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name, int ThreadCount);

/// <summary>Cumulative counters read from one process.</summary>
public readonly record struct ProcessSample(
    int Pid,
    long CreationTimeUtcTicks,
    long CpuTime100ns,
    long WorkingSetBytes,
    long PrivateBytes,
    int HandleCount,
    long IoReadBytes,
    long IoWriteBytes);

/// <summary>Cumulative byte counts for one process since tracing started.</summary>
public readonly record struct IoTotals(long DiskReadBytes, long DiskWriteBytes, long NetSentBytes, long NetReceivedBytes);

public readonly record struct DbSessionCount(int Sessions, int Active);

public readonly record struct SystemSample(long IdleTime100ns, long KernelTime100ns, long UserTime100ns, long MemoryTotalBytes, long MemoryAvailableBytes);

/// <summary>IIS performance counter values, already converted to rates where applicable.</summary>
public sealed class IisCounterValues
{
    /// <summary>"Web Service" counters keyed by perf counter instance name (site name, mangled by perflib).</summary>
    public Dictionary<string, SiteCounterValues> Sites { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"W3SVC_W3WP" counters by worker process PID.</summary>
    public Dictionary<int, WorkerCounterValues> Workers { get; init; } = [];

    /// <summary>"HTTP Service Request Queues" current queue size keyed by instance name (app pool name).</summary>
    public Dictionary<string, int> QueueLengths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct SiteCounterValues(int? CurrentConnections, double? RequestsPerSec, double? BytesSentPerSec, double? BytesReceivedPerSec);

public readonly record struct WorkerCounterValues(string AppPoolInstance, int? ActiveRequests, double? RequestsPerSec);
