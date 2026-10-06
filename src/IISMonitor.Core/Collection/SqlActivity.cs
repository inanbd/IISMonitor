using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>One running request, as read from sys.dm_exec_requests joined to its session.</summary>
public readonly record struct RequestObservation(
    int SessionId,
    int RequestId,
    DateTime StartUtc,
    string Status,
    string Command,
    double ElapsedMs,
    double CpuMs,
    long LogicalReads,
    long Writes,
    string? WaitType,
    int BlockedBy,
    string Database,
    string? QueryHash,
    string? TextKey,
    bool IsLocal,
    int Pid,
    string Program,
    string Login);

/// <summary>A session opened by a process on this server (sys.dm_exec_sessions).</summary>
public readonly record struct SessionObservation(int SessionId, int Pid, string Status, int OpenTransactions);

/// <summary>Everything one sample of SQL Server returned.</summary>
public sealed class DbSample
{
    public DateTime TimeUtc { get; init; }
    public IReadOnlyList<RequestObservation> Requests { get; init; } = [];
    public IReadOnlyList<SessionObservation> LocalSessions { get; init; } = [];
}

/// <summary>Per client process: averages over the samples since the last drain, plus the latest counts.</summary>
public readonly record struct DbPidActivity(
    double Load,
    double CpuLoad,
    int Blocked,
    int Blocking,
    int IdleInTransaction,
    int SlowRunning,
    int Sessions,
    int Active);

/// <summary>SQL Server activity collected between two engine ticks.</summary>
public sealed class DbActivityInput
{
    public int Samples { get; init; }
    public double SlowThresholdMs { get; init; }
    public Dictionary<int, DbPidActivity> ByPid { get; init; } = [];
    public double OtherServersLoad { get; init; }
    public List<RunningQuery> Running { get; init; } = [];
    public List<SlowQuery> CompletedSlow { get; init; } = [];
}

/// <summary>
/// Turns per-second samples of running requests into database load per client process and a list
/// of slow queries. Sampling is used because SQL Server's per-session totals only update when a
/// query finishes and reset whenever a pooled connection is reused, which hides most of a web
/// app's work. Thread-safe: the sampler adds, the engine drains.
/// </summary>
public sealed class DbActivityAccumulator(double slowThresholdMs)
{
    public const int MaxRunningListed = 30;

    /// <summary>Waits that mean "idle by design" (Service Broker / SqlDependency listeners, traces, WAITFOR); not load, not slow.</summary>
    private static readonly HashSet<string> IdleWaits = new(StringComparer.OrdinalIgnoreCase)
    {
        "BROKER_RECEIVE_WAITFOR", "WAITFOR", "TRACEWRITE", "SP_SERVER_DIAGNOSTICS_SLEEP", "XE_LIVE_TARGET_TVF",
        "WAITFOR_TASKSHUTDOWN", "BROKER_TASK_STOP",
    };

    private readonly object _gate = new();
    private readonly Dictionary<(int Session, int Request, DateTime Start), InFlight> _inFlight = [];
    private readonly Dictionary<int, (double Load, double Cpu)> _sums = [];
    private readonly List<SlowQuery> _completed = [];
    private double _otherServersSum;
    private int _samples;
    private DbSample? _latest;

    public double SlowThresholdMs { get; } = slowThresholdMs;

    public static bool IsIdleWait(RequestObservation r) =>
        (r.WaitType is { } wait && IdleWaits.Contains(wait)) || r.Command.Equals("WAITFOR", StringComparison.OrdinalIgnoreCase);

    public void Add(DbSample sample)
    {
        lock (_gate)
        {
            _samples++;
            _latest = sample;
            var seen = new HashSet<(int, int, DateTime)>();

            foreach (var r in sample.Requests)
            {
                if (IsIdleWait(r))
                    continue;

                if (!r.IsLocal)
                {
                    _otherServersSum++;
                    continue;
                }

                var cpu = IsOnCpu(r) ? 1 : 0;
                var sum = _sums.GetValueOrDefault(r.Pid);
                _sums[r.Pid] = (sum.Load + 1, sum.Cpu + cpu);

                if (r.ElapsedMs < SlowThresholdMs)
                    continue;

                var key = (r.SessionId, r.RequestId, r.StartUtc);
                seen.Add(key);
                if (!_inFlight.TryGetValue(key, out var flight))
                    _inFlight[key] = flight = new InFlight();
                flight.Observe(r);
            }

            foreach (var (key, flight) in _inFlight.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                _inFlight.Remove(key);
                _completed.Add(flight.ToSlowQuery());
            }
        }
    }

    /// <summary>Returns what was collected since the previous drain and starts over.</summary>
    /// <param name="describe">Statement text (literals removed) and object name for a request's text key.</param>
    public DbActivityInput? Drain(Func<string?, (string? Statement, string? ObjectName)> describe)
    {
        lock (_gate)
        {
            if (_samples == 0 || _latest is not { } latest)
                return null;

            var byPid = new Dictionary<int, DbPidActivity>();
            var sessionPid = latest.LocalSessions.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First().Pid);
            var localActive = latest.Requests.Where(r => r.IsLocal && !IsIdleWait(r)).ToList();

            var pids = _sums.Keys
                .Concat(latest.LocalSessions.Select(s => s.Pid))
                .Concat(localActive.Select(r => r.Pid))
                .Distinct();
            foreach (var pid in pids)
            {
                var sums = _sums.GetValueOrDefault(pid);
                var own = localActive.Where(r => r.Pid == pid).ToList();
                byPid[pid] = new DbPidActivity(
                    Load: sums.Load / _samples,
                    CpuLoad: sums.Cpu / _samples,
                    Blocked: own.Count(r => r.BlockedBy > 0),
                    Blocking: latest.Requests.Count(r => r.BlockedBy > 0 && sessionPid.TryGetValue(r.BlockedBy, out var blocker) && blocker == pid),
                    IdleInTransaction: latest.LocalSessions.Count(s => s.Pid == pid && s.OpenTransactions > 0
                                                                       && s.Status.Equals("sleeping", StringComparison.OrdinalIgnoreCase)),
                    SlowRunning: own.Count(r => r.ElapsedMs >= SlowThresholdMs),
                    Sessions: latest.LocalSessions.Count(s => s.Pid == pid),
                    Active: own.Count);
            }

            var running = localActive
                .OrderByDescending(r => r.ElapsedMs)
                .Take(MaxRunningListed)
                .Select(r =>
                {
                    var (statement, objectName) = describe(r.TextKey);
                    return new RunningQuery
                    {
                        SessionId = r.SessionId,
                        Pid = r.Pid,
                        Program = r.Program,
                        Login = r.Login,
                        Database = r.Database,
                        Command = r.Command,
                        Status = r.Status,
                        WaitType = r.WaitType,
                        BlockedBy = r.BlockedBy > 0 ? r.BlockedBy : null,
                        StartUtc = r.StartUtc,
                        ElapsedMs = r.ElapsedMs,
                        CpuMs = r.CpuMs,
                        LogicalReads = r.LogicalReads,
                        Writes = r.Writes,
                        QueryHash = r.QueryHash,
                        ObjectName = objectName,
                        Statement = statement,
                    };
                })
                .ToList();

            var completed = _completed.Select(q =>
            {
                var (statement, objectName) = describe(q.Statement);
                q.Statement = statement;
                q.ObjectName = objectName;
                return q;
            }).ToList();

            var result = new DbActivityInput
            {
                Samples = _samples,
                SlowThresholdMs = SlowThresholdMs,
                ByPid = byPid,
                OtherServersLoad = _otherServersSum / _samples,
                Running = running,
                CompletedSlow = completed,
            };

            _samples = 0;
            _otherServersSum = 0;
            _sums.Clear();
            _completed.Clear();
            return result;
        }
    }

    /// <summary>Text keys of slow or listed requests whose statement text is still needed.</summary>
    public IReadOnlyList<string> PendingTextKeys()
    {
        lock (_gate)
        {
            var keys = new HashSet<string>();
            foreach (var flight in _inFlight.Values)
            {
                if (flight.TextKey is { } key)
                    keys.Add(key);
            }

            if (_latest is { } latest)
            {
                foreach (var r in latest.Requests.Where(r => r.IsLocal && !IsIdleWait(r)).OrderByDescending(r => r.ElapsedMs).Take(MaxRunningListed))
                {
                    if (r.TextKey is { } key)
                        keys.Add(key);
                }
            }

            return keys.ToList();
        }
    }

    private static bool IsOnCpu(RequestObservation r) =>
        r.Status.Equals("running", StringComparison.OrdinalIgnoreCase) || r.Status.Equals("runnable", StringComparison.OrdinalIgnoreCase);

    private sealed class InFlight
    {
        private readonly Dictionary<string, int> _waits = new(StringComparer.OrdinalIgnoreCase);
        private RequestObservation _last;
        private bool _blocked;

        public string? TextKey => _last.TextKey;

        public void Observe(RequestObservation r)
        {
            _last = r;
            if (r.BlockedBy > 0)
                _blocked = true;
            if (!string.IsNullOrEmpty(r.WaitType) && !IsOnCpu(r))
                _waits[r.WaitType] = _waits.GetValueOrDefault(r.WaitType) + 1;
        }

        public SlowQuery ToSlowQuery() => new()
        {
            StartUtc = _last.StartUtc,
            EndUtc = _last.StartUtc.AddMilliseconds(_last.ElapsedMs),
            DurationMs = _last.ElapsedMs,
            CpuMs = _last.CpuMs,
            LogicalReads = _last.LogicalReads,
            Writes = _last.Writes,
            Pid = _last.Pid,
            Program = _last.Program,
            Login = _last.Login,
            Database = _last.Database,
            QueryHash = _last.QueryHash,
            // Holds the text key until Drain resolves it into the statement.
            Statement = _last.TextKey,
            MainWait = _waits.Count == 0 ? null : _waits.MaxBy(kv => kv.Value).Key,
            WasBlocked = _blocked,
        };
    }
}
