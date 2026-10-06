namespace IISMonitor.Core.Presentation;

/// <summary>Plain-language meaning of the SQL Server wait types that matter most for web apps.</summary>
public static class WaitTypes
{
    private static readonly (string Prefix, string Meaning)[] Known =
    [
        ("LCK_", "Waiting for a lock held by another session"),
        ("ASYNC_NETWORK_IO", "Finished, but the app is reading the results slowly (too many rows, or row-by-row processing)"),
        ("PAGEIOLATCH_", "Reading data pages from disk (missing index or too little memory)"),
        ("WRITELOG", "Waiting for the transaction log to be written"),
        ("RESOURCE_SEMAPHORE", "Waiting for memory to run the query (large sort or hash)"),
        ("SOS_SCHEDULER_YIELD", "Long CPU work, sharing the CPU with other queries"),
        ("CXPACKET", "Parallel query waiting for its slowest thread"),
        ("CXCONSUMER", "Parallel query waiting for its slowest thread"),
        ("CXSYNC_", "Parallel query synchronizing threads"),
        ("PAGELATCH_", "Contention on hot in-memory pages (often tempdb or inserts at the end of a table)"),
        ("THREADPOOL", "SQL Server has run out of worker threads"),
        ("IO_COMPLETION", "Waiting for disk I/O (sorts or spills to tempdb)"),
        ("OLEDB", "Waiting for a linked server or remote call"),
        ("HADR_SYNC_COMMIT", "Waiting for an availability-group replica to confirm the commit"),
        ("LATCH_", "Contention on an internal SQL Server structure"),
        ("PREEMPTIVE_", "Calling outside SQL Server (OS, network or external code)"),
    ];

    public static string Describe(string? waitType, string? status = null)
    {
        if (string.IsNullOrEmpty(waitType))
        {
            return status?.Equals("runnable", StringComparison.OrdinalIgnoreCase) == true
                ? "Ready to run, waiting for a CPU"
                : "Running on CPU";
        }

        foreach (var (prefix, meaning) in Known)
        {
            if (waitType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return meaning;
        }

        return "Waiting (" + waitType + ")";
    }
}
