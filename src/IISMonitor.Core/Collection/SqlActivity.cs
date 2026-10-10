using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>One running request, as read from sys.dm_exec_requests joined to its session.</summary>
/// <param name="SessionId">Session ID made unique across servers (server index × 1,000,000 + session_id).</param>
/// <param name="RawStart">start_time exactly as SQL Server returned it (server local time); identifies the request.</param>
/// <param name="StartUtc">start_time converted to UTC, for display.</param>
/// <param name="BlockedBy">Blocking session (same numbering as SessionId), or 0. Never the request's own session.</param>
/// <param name="TextKey">Key of the statement text currently executing.</param>
/// <param name="BatchKey">Key of the whole batch or module currently executing.</param>
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
    string Login,
    string? BatchKey = null,
    DateTime RawStart = default,
    int ServerIndex = 0);

/// <summary>A session opened by a process on this server (sys.dm_exec_sessions).</summary>
/// <param name="LastRequestStartRaw">last_request_start_time (server local time).</param>
/// <param name="LastRequestEndRaw">last_request_end_time (server local time).</param>
public readonly record struct SessionObservation(
    int SessionId,
    int Pid,
    string Status,
    int OpenTransactions,
    DateTime? LastRequestStartRaw = null,
    DateTime? LastRequestEndRaw = null);

/// <summary>Everything one sample of SQL Server returned.</summary>
public sealed class DbSample
{
    public DateTime TimeUtc { get; init; }
    public IReadOnlyList<RequestObservation> Requests { get; init; } = [];
    public IReadOnlyList<SessionObservation> LocalSessions { get; init; } = [];

    /// <summary>Servers that answered this sample; null means all. In-flight queries of other servers are left alone.</summary>
    public IReadOnlySet<int>? ServersSampled { get; init; }

    /// <summary>
    /// An extra sample taken when a watched query is about to cross the slow threshold. It updates
    /// slow-query tracking but is left out of load averages, which need evenly spaced samples.
    /// </summary>
    public bool IsProbe { get; init; }
}

/// <summary>Per client process: load averages plus the latest counts.</summary>
/// <param name="Load">Average running queries over the samples since the previous drain.</param>
/// <param name="Load1m">Average running queries over the last minute (steadier, for ranking and charts).</param>
public readonly record struct DbPidActivity(
    double Load,
    double CpuLoad,
    int Blocked,
    int Blocking,
    int IdleInTransaction,
    int SlowRunning,
    int Sessions,
    int Active,
    double Load1m = 0,
    double CpuLoad1m = 0);

/// <summary>SQL Server activity collected between two engine ticks.</summary>
public sealed class DbActivityInput
{
    /// <summary>Evenly spaced samples behind the load averages; 0 when no new sample arrived (values carried over).</summary>
    public int Samples { get; init; }

    public double SlowThresholdMs { get; init; }
    public Dictionary<int, DbPidActivity> ByPid { get; init; } = [];
    public double OtherServersLoad { get; init; }
    public double OtherServersLoad1m { get; init; }
    public List<RunningQuery> Running { get; init; } = [];
    public List<SlowQuery> CompletedSlow { get; init; } = [];
}

/// <summary>Resolved text for a text key: statement (literals removed) and the module it belongs to.</summary>
public readonly record struct SqlText(string? Statement, string? ObjectName);

/// <summary>
/// Turns per-second samples of running requests into database load per client process and a list
/// of slow queries. Sampling is used because SQL Server's per-session totals only update when a
/// query finishes and reset whenever a pooled connection is reused, which hides most of a web
/// app's work. Thread-safe: the sampler adds, the engine drains.
/// </summary>
/// <remarks>
/// Slow queries: a request is watched from the moment it could cross the threshold before the next
/// sample (elapsed ≥ threshold − interval). <see cref="NextThresholdCrossingUtc"/> tells the sampler
/// when to take an extra sample so a watched request is seen at or after the threshold. When a
/// watched request ends, its exact duration comes from its session's last_request_start/end_time if
/// the session hasn't started another request since; otherwise the last sampled elapsed time is a
/// lower bound. It is recorded only if that duration reaches the threshold.
/// </remarks>
public sealed class DbActivityAccumulator(double slowThresholdMs, double sampleIntervalMs = 1000)
{
    public const int MaxRunningListed = 30;
    private static readonly TimeSpan RollingWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProbeMargin = TimeSpan.FromMilliseconds(100);

    /// <summary>Waits that mean "idle by design" (Service Broker / SqlDependency listeners, traces, WAITFOR); not load, not slow.</summary>
    private static readonly HashSet<string> IdleWaits = new(StringComparer.OrdinalIgnoreCase)
    {
        "BROKER_RECEIVE_WAITFOR", "WAITFOR", "TRACEWRITE", "SP_SERVER_DIAGNOSTICS_SLEEP", "XE_LIVE_TARGET_TVF",
        "WAITFOR_TASKSHUTDOWN", "BROKER_TASK_STOP",
    };

    private readonly object _gate = new();
    private readonly Dictionary<RequestKey, InFlight> _inFlight = [];
    private readonly Dictionary<int, (double Load, double Cpu)> _sums = [];
    private readonly List<PendingSlow> _completed = [];
    private readonly Queue<(DateTime TimeUtc, Dictionary<int, (int Load, int Cpu)> ByPid, int OtherServers)> _window = new();
    private Dictionary<int, (double Load, double Cpu)> _lastLoads = [];
    private double _lastOtherServersLoad;
    private double _otherServersSum;
    private int _samples;
    private DbSample? _latest;

    public double SlowThresholdMs { get; } = slowThresholdMs;

    /// <summary>Requests at least this old are watched as possible slow queries.</summary>
    public double CandidateMs { get; } = Math.Max(0, slowThresholdMs - sampleIntervalMs - 250);

    public static bool IsIdleWait(RequestObservation r) =>
        (r.WaitType is { } wait && IdleWaits.Contains(wait)) || r.Command.Equals("WAITFOR", StringComparison.OrdinalIgnoreCase);

    public void Add(DbSample sample)
    {
        lock (_gate)
        {
            _latest = sample;
            var sessionPid = SessionPids(sample);
            var seen = new HashSet<RequestKey>();
            var perPid = new Dictionary<int, (int Load, int Cpu)>();
            var otherServers = 0;

            foreach (var r in sample.Requests)
            {
                var key = RequestKey.Of(r);
                var idle = IsIdleWait(r);
                if (_inFlight.TryGetValue(key, out var flight))
                {
                    // A watched request that pauses (WAITFOR inside a batch) is still the same request.
                    seen.Add(key);
                    if (idle)
                    {
                        flight.Touch(r, sample.TimeUtc);
                        continue;
                    }
                }
                else if (idle)
                {
                    continue;
                }

                if (!r.IsLocal)
                {
                    otherServers++;
                    continue;
                }

                var counts = perPid.GetValueOrDefault(r.Pid);
                perPid[r.Pid] = (counts.Load + 1, counts.Cpu + (IsOnCpu(r) ? 1 : 0));

                if (flight is null)
                {
                    if (r.ElapsedMs < CandidateMs)
                        continue;
                    _inFlight[key] = flight = new InFlight(r, sample.TimeUtc);
                    seen.Add(key);
                }

                flight.Observe(r, sample.TimeUtc, sessionPid);
            }

            if (!sample.IsProbe)
            {
                _samples++;
                _otherServersSum += otherServers;
                foreach (var (pid, counts) in perPid)
                {
                    var sum = _sums.GetValueOrDefault(pid);
                    _sums[pid] = (sum.Load + counts.Load, sum.Cpu + counts.Cpu);
                }

                _window.Enqueue((sample.TimeUtc, perPid, otherServers));
                while (_window.Count > 0 && sample.TimeUtc - _window.Peek().TimeUtc >= RollingWindow)
                    _window.Dequeue();
            }

            var sessions = sample.LocalSessions.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First());
            foreach (var (key, flight) in _inFlight.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                // A server that didn't answer this time can't tell us its queries finished.
                if (sample.ServersSampled is { } servers && !servers.Contains(key.Server))
                    continue;

                _inFlight.Remove(key);
                double? exact = null;
                if (sessions.TryGetValue(key.Session, out var session)
                    && session.LastRequestStartRaw == key.RawStart
                    && session.LastRequestEndRaw is { } end && end >= key.RawStart)
                {
                    exact = (end - key.RawStart).TotalMilliseconds;
                }

                // The end time is only kept to the millisecond-ish precision of datetime; never report
                // less than was already seen running.
                var duration = Math.Max(exact ?? 0, flight.LastElapsedMs);
                if (duration >= SlowThresholdMs)
                    _completed.Add(flight.Finish(duration));
            }
        }
    }

    /// <summary>
    /// When the earliest watched request that hasn't reached the threshold yet will reach it (plus a
    /// small margin), so the sampler can look again right then; null when there is none.
    /// </summary>
    public DateTime? NextThresholdCrossingUtc()
    {
        lock (_gate)
        {
            if (_latest is not { } latest)
                return null;

            DateTime? next = null;
            foreach (var flight in _inFlight.Values)
            {
                // Requests of a server that didn't answer the last sample have stale timings.
                if (flight.LastElapsedMs >= SlowThresholdMs || flight.LastSeenUtc < latest.TimeUtc)
                    continue;
                var crossing = flight.LastSeenUtc.AddMilliseconds(SlowThresholdMs - flight.LastElapsedMs) + ProbeMargin;
                if (next is null || crossing < next)
                    next = crossing;
            }

            return next;
        }
    }

    /// <summary>Returns what was collected since the previous drain and starts over.</summary>
    /// <param name="describe">Statement text (literals removed) and module name for a text key.</param>
    /// <returns>Null only until the first sample arrives; afterwards the latest picture, with load carried over if no new sample came in.</returns>
    public DbActivityInput? Drain(Func<string?, SqlText> describe)
    {
        lock (_gate)
        {
            if (_latest is not { } latest)
                return null;

            var sessionPid = SessionPids(latest);
            var localActive = latest.Requests.Where(r => r.IsLocal && !IsIdleWait(r)).ToList();

            Dictionary<int, (double Load, double Cpu)> loads;
            double otherServersLoad;
            if (_samples > 0)
            {
                loads = _sums.ToDictionary(kv => kv.Key, kv => (kv.Value.Load / _samples, kv.Value.Cpu / _samples));
                otherServersLoad = _otherServersSum / _samples;
            }
            else
            {
                // No new evenly spaced sample since the last tick: keep showing the previous averages.
                loads = _lastLoads;
                otherServersLoad = _lastOtherServersLoad;
            }

            var windowSamples = Math.Max(1, _window.Count);
            var rolling = new Dictionary<int, (double Load, double Cpu)>();
            foreach (var (_, byPid, _) in _window)
            {
                foreach (var (pid, counts) in byPid)
                {
                    var sum = rolling.GetValueOrDefault(pid);
                    rolling[pid] = (sum.Load + counts.Load, sum.Cpu + counts.Cpu);
                }
            }

            var byPidResult = new Dictionary<int, DbPidActivity>();
            var pids = loads.Keys
                .Concat(rolling.Keys)
                .Concat(latest.LocalSessions.Select(s => s.Pid))
                .Concat(localActive.Select(r => r.Pid))
                .Distinct();
            foreach (var pid in pids)
            {
                var load = loads.GetValueOrDefault(pid);
                var oneMinute = rolling.GetValueOrDefault(pid);
                var own = localActive.Where(r => r.Pid == pid).ToList();
                byPidResult[pid] = new DbPidActivity(
                    Load: load.Load,
                    CpuLoad: load.Cpu,
                    Blocked: own.Count(r => r.BlockedBy > 0),
                    Blocking: latest.Requests.Count(r => r.BlockedBy > 0 && sessionPid.TryGetValue(r.BlockedBy, out var blocker) && blocker == pid),
                    IdleInTransaction: latest.LocalSessions.Count(s => s.Pid == pid && s.OpenTransactions > 0
                                                                       && s.Status.Equals("sleeping", StringComparison.OrdinalIgnoreCase)),
                    SlowRunning: own.Count(r => r.ElapsedMs >= SlowThresholdMs),
                    Sessions: latest.LocalSessions.Count(s => s.Pid == pid),
                    Active: own.Count,
                    Load1m: oneMinute.Load / windowSamples,
                    CpuLoad1m: oneMinute.Cpu / windowSamples);
            }

            var running = localActive
                .OrderByDescending(r => r.ElapsedMs)
                .Take(MaxRunningListed)
                .Select(r =>
                {
                    var text = describe(r.TextKey);
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
                        BlockerPid = r.BlockedBy > 0 && sessionPid.TryGetValue(r.BlockedBy, out var blocker) ? blocker : null,
                        StartUtc = r.StartUtc,
                        ElapsedMs = r.ElapsedMs,
                        CpuMs = r.CpuMs,
                        LogicalReads = r.LogicalReads,
                        Writes = r.Writes,
                        QueryHash = r.QueryHash,
                        ObjectName = text.ObjectName,
                        Statement = text.Statement,
                    };
                })
                .ToList();

            var completed = _completed.Select(p => p.Resolve(describe)).ToList();

            var result = new DbActivityInput
            {
                Samples = _samples,
                SlowThresholdMs = SlowThresholdMs,
                ByPid = byPidResult,
                OtherServersLoad = otherServersLoad,
                OtherServersLoad1m = _window.Sum(w => w.OtherServers) / (double)windowSamples,
                Running = running,
                CompletedSlow = completed,
            };

            _lastLoads = loads;
            _lastOtherServersLoad = otherServersLoad;
            _samples = 0;
            _otherServersSum = 0;
            _sums.Clear();
            _completed.Clear();
            return result;
        }
    }

    /// <summary>Text keys still needed: watched, finished-but-undrained and listed requests.</summary>
    public IReadOnlyList<string> PendingTextKeys()
    {
        lock (_gate)
        {
            var keys = new HashSet<string>();
            foreach (var flight in _inFlight.Values)
                keys.UnionWith(flight.TextKeys);
            foreach (var pending in _completed)
                keys.UnionWith(pending.TextKeys);

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

    /// <summary>Watched requests whose top-level call (sys.dm_exec_input_buffer) hasn't been read yet.</summary>
    public IReadOnlyList<WatchedRequest> PendingEntryPoints()
    {
        lock (_gate)
        {
            return _inFlight.Where(kv => !kv.Value.HasEntryPoint).Select(kv => kv.Key.Public).ToList();
        }
    }

    /// <summary>
    /// Stores the top-level call of a watched request: "proc:" plus a procedure name, "text:" plus a
    /// batch (literals removed), or null if it couldn't be read.
    /// </summary>
    public void SetEntryPoint(WatchedRequest request, string? entryPoint)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(RequestKey.From(request), out var flight))
                flight.SetEntryPoint(entryPoint);
        }
    }

    private static Dictionary<int, int> SessionPids(DbSample sample) =>
        sample.LocalSessions.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.First().Pid);

    private static bool IsOnCpu(RequestObservation r) =>
        r.Status.Equals("running", StringComparison.OrdinalIgnoreCase) || r.Status.Equals("runnable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifies a request across samples: server, session, request and its raw start time (safe across clock changes).</summary>
    private readonly record struct RequestKey(int Server, int Session, int Request, DateTime RawStart)
    {
        public static RequestKey Of(RequestObservation r) =>
            new(r.ServerIndex, r.SessionId, r.RequestId, r.RawStart == default ? r.StartUtc : r.RawStart);

        public static RequestKey From(WatchedRequest w) => new(w.Server, w.Session, w.Request, w.RawStart);

        public WatchedRequest Public => new(Server, Session, Request, RawStart);
    }

    /// <summary>A finished slow query whose texts are still keys; <see cref="Drain"/> resolves them.</summary>
    private sealed record PendingSlow(SlowQuery Query, string? StatementKey, string? BatchKey, string? EntryPoint)
    {
        public IEnumerable<string> TextKeys => new[] { StatementKey, BatchKey }.OfType<string>();

        public SlowQuery Resolve(Func<string?, SqlText> describe)
        {
            var statement = describe(StatementKey);
            var batch = describe(BatchKey);
            string? objectName;
            string? batchText;
            if (EntryPoint?.StartsWith("proc:", StringComparison.Ordinal) == true)
            {
                objectName = EntryPoint[5..];
                batchText = null;
            }
            else if (EntryPoint?.StartsWith("text:", StringComparison.Ordinal) == true)
            {
                objectName = null;
                batchText = EntryPoint[5..];
            }
            else
            {
                // Top-level call unknown: use the batch or module it was first seen in, or failing
                // that the module of its main statement.
                objectName = batch.ObjectName ?? (batch.Statement is null ? statement.ObjectName : null);
                batchText = objectName is null ? batch.Statement : null;
            }

            Query.ObjectName = objectName;
            Query.Batch = batchText;
            Query.Statement = statement.Statement;
            Query.StatementObject = statement.ObjectName is { } inner && !string.Equals(inner, objectName, StringComparison.OrdinalIgnoreCase)
                ? inner
                : null;
            return Query;
        }
    }

    private sealed class InFlight
    {
        private const string Cpu = "CPU";
        private readonly Dictionary<string, int> _waits = new(StringComparer.OrdinalIgnoreCase);

        // A batch or procedure moves through several statements (and nested modules) while it runs;
        // count where it is seen, so the record names the statement it spent the most time in.
        private readonly Dictionary<string, int> _statements = [];
        private readonly Dictionary<int, int> _blockerPids = [];
        private readonly DateTime _startUtc;
        private readonly string? _firstBatchKey;
        private RequestObservation _last;
        private bool _blocked;
        private string? _entryPoint;

        public InFlight(RequestObservation first, DateTime sampleUtc)
        {
            // Derived once from the first sighting, so later clock changes don't move it.
            _startUtc = sampleUtc.AddMilliseconds(-first.ElapsedMs);
            _firstBatchKey = first.BatchKey;
            _last = first;
            LastSeenUtc = sampleUtc;
        }

        public double LastElapsedMs => _last.ElapsedMs;

        public DateTime LastSeenUtc { get; private set; }

        public bool HasEntryPoint { get; private set; }

        public IEnumerable<string> TextKeys =>
            _statements.Keys.Concat(_firstBatchKey is { } batch ? [batch] : []);

        public void SetEntryPoint(string? entryPoint)
        {
            _entryPoint = entryPoint;
            HasEntryPoint = true;
        }

        /// <summary>Seen while paused in an idle wait: only the timing moves on.</summary>
        public void Touch(RequestObservation r, DateTime sampleUtc)
        {
            _last = _last with { ElapsedMs = r.ElapsedMs, CpuMs = r.CpuMs, LogicalReads = r.LogicalReads, Writes = r.Writes };
            LastSeenUtc = sampleUtc;
        }

        public void Observe(RequestObservation r, DateTime sampleUtc, Dictionary<int, int> sessionPid)
        {
            _last = r;
            LastSeenUtc = sampleUtc;
            if (r.BlockedBy > 0)
            {
                _blocked = true;
                if (sessionPid.TryGetValue(r.BlockedBy, out var blocker))
                    _blockerPids[blocker] = _blockerPids.GetValueOrDefault(blocker) + 1;
            }

            var wait = IsOnCpu(r) || string.IsNullOrEmpty(r.WaitType) ? Cpu : r.WaitType;
            _waits[wait] = _waits.GetValueOrDefault(wait) + 1;
            if (r.TextKey is { } key)
                _statements[key] = _statements.GetValueOrDefault(key) + 1;
        }

        private string? MainStatementKey =>
            _statements.Count == 0
                ? _last.TextKey
                : _statements.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == _last.TextKey ? 0 : 1).First().Key;

        public PendingSlow Finish(double durationMs)
        {
            var query = new SlowQuery
            {
                StartUtc = _startUtc,
                EndUtc = _startUtc.AddMilliseconds(durationMs),
                DurationMs = durationMs,
                CpuMs = _last.CpuMs,
                LogicalReads = _last.LogicalReads,
                Writes = _last.Writes,
                Pid = _last.Pid,
                Program = _last.Program,
                Login = _last.Login,
                Database = _last.Database,
                QueryHash = _last.QueryHash,
                MainWait = _waits.Count == 0 ? Cpu : _waits.MaxBy(kv => kv.Value).Key,
                WasBlocked = _blocked,
                BlockerPid = _blockerPids.Count == 0 ? null : _blockerPids.MaxBy(kv => kv.Value).Key,
            };

            // The batch is the one seen first: later samples may be inside a nested procedure or function.
            return new PendingSlow(query, MainStatementKey, _firstBatchKey, _entryPoint);
        }
    }
}

/// <summary>A watched request, as the sampler needs it to read sys.dm_exec_input_buffer.</summary>
/// <param name="Session">Session ID including the server offset (server index × 1,000,000).</param>
/// <param name="RawStart">start_time as SQL Server reported it.</param>
public readonly record struct WatchedRequest(int Server, int Session, int Request, DateTime RawStart)
{
    public int RawSession => Session % 1_000_000;
}
