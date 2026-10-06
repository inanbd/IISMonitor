namespace IISMonitor.Core.Models;

/// <summary>One complete sample of the server, its app pools and its sites.</summary>
public sealed class MonitorSnapshot
{
    public DateTime TimestampUtc { get; set; }

    /// <summary>Seconds covered by the rate values in this snapshot (time since the previous sample).</summary>
    public double IntervalSeconds { get; set; }

    public ServerMetrics Server { get; set; } = new();

    public List<AppPoolMetrics> AppPools { get; set; } = [];

    public List<SiteMetrics> Sites { get; set; } = [];

    public List<CollectorHealth> Health { get; set; } = [];
}

public sealed class ServerMetrics
{
    public string MachineName { get; set; } = "";
    public int ProcessorCount { get; set; }
    public double? CpuPercent { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
}

public sealed class AppPoolMetrics
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string PipelineMode { get; set; } = "";
    public string Identity { get; set; } = "";

    /// <summary>Applications served by this pool, as "Site name/path".</summary>
    public List<string> Applications { get; set; } = [];

    /// <summary>Worker processes (w3wp.exe) and every process they started.</summary>
    public List<ProcessMetrics> Processes { get; set; } = [];

    public int ProcessCount { get; set; }
    public int WorkerProcessCount { get; set; }

    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }

    public double? DiskReadBytesPerSec { get; set; }
    public double? DiskWriteBytesPerSec { get; set; }

    /// <summary>Traffic the pool's processes send themselves (database, outbound APIs). Inbound HTTP is accounted per site.</summary>
    public double? NetworkSentBytesPerSec { get; set; }
    public double? NetworkReceivedBytesPerSec { get; set; }

    /// <summary>Open TCP connections from the pool's processes to SQL Server ports.</summary>
    public int? DbConnections { get; set; }

    /// <summary>Sessions SQL Server reports for the pool's processes (needs a configured DMV connection).</summary>
    public int? DbSessions { get; set; }

    /// <summary>Of <see cref="DbSessions"/>, the ones executing a request right now.</summary>
    public int? DbActiveSessions { get; set; }

    public double? RequestsPerSec { get; set; }
    public int? ActiveRequests { get; set; }
    public int? QueueLength { get; set; }

    public ResponseStats? Response { get; set; }
}

public sealed class SiteMetrics
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";

    /// <summary>App pool of the site's root application.</summary>
    public string AppPool { get; set; } = "";

    public List<string> Bindings { get; set; } = [];

    /// <summary>Whether IIS sends this site's log entries to ETW (required for live response times).</summary>
    public bool EtwLoggingEnabled { get; set; }

    public int? CurrentConnections { get; set; }
    public double? RequestsPerSec { get; set; }
    public double? BytesSentPerSec { get; set; }
    public double? BytesReceivedPerSec { get; set; }

    public ResponseStats? Response { get; set; }
}

public sealed class ProcessMetrics
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public bool IsWorkerProcess { get; set; }
    public DateTime? StartTimeUtc { get; set; }

    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }

    public double? DiskReadBytesPerSec { get; set; }
    public double? DiskWriteBytesPerSec { get; set; }
    public double? NetworkSentBytesPerSec { get; set; }
    public double? NetworkReceivedBytesPerSec { get; set; }

    public int? DbConnections { get; set; }
    public int? DbSessions { get; set; }
}

/// <summary>Response-time statistics for the requests completed in one interval.</summary>
public sealed class ResponseStats
{
    public int RequestCount { get; set; }
    public double AverageMs { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
    public int ClientErrors { get; set; }
    public int ServerErrors { get; set; }
}

public sealed class CollectorHealth
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}
