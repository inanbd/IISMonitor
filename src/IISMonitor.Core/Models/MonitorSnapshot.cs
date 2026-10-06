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

    /// <summary>SQL Server activity; null when no SQL Server connection string is configured.</summary>
    public DatabaseActivity? Database { get; set; }
}

public sealed class ServerMetrics
{
    public string MachineName { get; set; } = "";
    public int ProcessorCount { get; set; }
    public double? CpuPercent { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }

    /// <summary>CPU of the local SQL Server process(es), as a share of the whole machine. Null when SQL Server isn't on this server.</summary>
    public double? SqlServerCpuPercent { get; set; }

    public long? SqlServerMemoryBytes { get; set; }
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

    /// <summary>
    /// HTTP traffic of the sites whose root application runs in this pool (IIS "Web Service" counters).
    /// HTTP.sys handles this traffic, so it is not part of <see cref="NetworkSentBytesPerSec"/>.
    /// </summary>
    public double? HttpBytesSentPerSec { get; set; }
    public double? HttpBytesReceivedPerSec { get; set; }

    /// <summary>Open TCP connections from the pool's processes to SQL Server ports.</summary>
    public int? DbConnections { get; set; }

    /// <summary>Sessions SQL Server reports for the pool's processes (needs a configured DMV connection).</summary>
    public int? DbSessions { get; set; }

    /// <summary>Of <see cref="DbSessions"/>, the ones executing a request right now.</summary>
    public int? DbActiveSessions { get; set; }

    /// <summary>
    /// Database load: how many of the pool's queries were running in SQL Server at a time, on
    /// average since the previous update (sampled every second). 1.0 = one query busy all the time.
    /// </summary>
    public double? DbLoad { get; set; }

    /// <summary>Part of <see cref="DbLoad"/> spent running on CPU (not waiting).</summary>
    public double? DbCpuLoad { get; set; }

    /// <summary>The pool's queries waiting for another session's locks right now.</summary>
    public int? DbBlocked { get; set; }

    /// <summary>Other sessions waiting for locks held by this pool's sessions right now.</summary>
    public int? DbBlocking { get; set; }

    /// <summary>Sessions sitting idle with an open transaction (holding locks without working).</summary>
    public int? DbIdleInTransaction { get; set; }

    /// <summary>The pool's queries running longer than the slow-query threshold right now.</summary>
    public int? DbSlowRunning { get; set; }

    /// <summary>Slow queries from this pool that finished since the previous update.</summary>
    public int? DbSlowCompleted { get; set; }

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

/// <summary>What SQL Server is doing for this machine's processes, sampled every second.</summary>
public sealed class DatabaseActivity
{
    public double SlowThresholdMs { get; set; }

    /// <summary>Average running queries since the previous update, from IIS app pools.</summary>
    public double PoolLoad { get; set; }

    /// <summary>Same, from other processes on this server (SQL Server Management Studio, services...).</summary>
    public double OtherLocalLoad { get; set; }

    /// <summary>Same, from other machines.</summary>
    public double OtherServersLoad { get; set; }

    /// <summary>Queries from this server running right now, longest first.</summary>
    public List<RunningQuery> Running { get; set; } = [];

    /// <summary>Slow queries that finished since the previous update.</summary>
    public List<SlowQuery> CompletedSlow { get; set; } = [];
}

public sealed class RunningQuery
{
    public int SessionId { get; set; }
    public int Pid { get; set; }

    /// <summary>App pool of the client process; null when the client isn't an IIS worker.</summary>
    public string? AppPool { get; set; }

    public string Program { get; set; } = "";
    public string Login { get; set; } = "";
    public string Database { get; set; } = "";
    public string Command { get; set; } = "";
    public string Status { get; set; } = "";
    public string? WaitType { get; set; }

    /// <summary>Session holding the lock this query waits for.</summary>
    public int? BlockedBy { get; set; }

    public DateTime StartUtc { get; set; }
    public double ElapsedMs { get; set; }
    public double CpuMs { get; set; }
    public long LogicalReads { get; set; }
    public long Writes { get; set; }
    public string? QueryHash { get; set; }
    public string? ObjectName { get; set; }

    /// <summary>The statement, with literal values replaced by "?".</summary>
    public string? Statement { get; set; }
}

public sealed class SlowQuery
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    /// <summary>
    /// Measured from samples, so the query ran at least this long (at most one sample interval more).
    /// </summary>
    public double DurationMs { get; set; }

    public double CpuMs { get; set; }
    public long LogicalReads { get; set; }
    public long Writes { get; set; }
    public int Pid { get; set; }
    public string? AppPool { get; set; }
    public string Program { get; set; } = "";
    public string Login { get; set; } = "";
    public string Database { get; set; } = "";
    public string? QueryHash { get; set; }
    public string? ObjectName { get; set; }
    public string? Statement { get; set; }

    /// <summary>The wait type seen most often while it ran; null if it was always on CPU.</summary>
    public string? MainWait { get; set; }

    /// <summary>Whether another session's locks blocked it at some point.</summary>
    public bool WasBlocked { get; set; }
}
