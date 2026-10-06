using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>
/// Turns raw collector output into a <see cref="MonitorSnapshot"/>: computes rates from cumulative
/// counters, assigns processes to app pools and rolls everything up per pool and per site.
/// Keeps the previous tick's counters, so one instance must be used for consecutive ticks.
/// </summary>
public sealed class SnapshotComposer(string machineName, int processorCount)
{
    private readonly Dictionary<(int Pid, long Created), ProcessState> _previous = [];
    private SystemSample? _previousSystem;
    private DateTime? _previousTimestamp;

    public MonitorSnapshot Compose(CollectionInput input)
    {
        var elapsed = _previousTimestamp is { } prev ? (input.TimestampUtc - prev).TotalSeconds : 0;
        if (elapsed < 0)
            elapsed = 0;
        _previousTimestamp = input.TimestampUtc;

        var snapshot = new MonitorSnapshot
        {
            TimestampUtc = input.TimestampUtc,
            IntervalSeconds = elapsed,
            Server = ComposeServer(input.System),
            Health = input.Health,
        };

        var members = ProcessTree.ResolvePoolMembers(
            input.Processes,
            input.WorkerProcessPools,
            pid => input.ProcessSamples.TryGetValue(pid, out var s) ? s.CreationTimeUtcTicks : null);

        var processes = ComposeProcesses(input, members, elapsed);
        var pools = ComposePools(input, members, processes);
        snapshot.AppPools = pools;
        snapshot.Sites = ComposeSites(input);
        return snapshot;
    }

    private ServerMetrics ComposeServer(SystemSample? sample)
    {
        var server = new ServerMetrics { MachineName = machineName, ProcessorCount = processorCount };
        if (sample is not { } current)
            return server;

        server.MemoryTotalBytes = current.MemoryTotalBytes;
        server.MemoryUsedBytes = current.MemoryTotalBytes - current.MemoryAvailableBytes;

        if (_previousSystem is { } previous)
        {
            // Kernel time includes idle time.
            var total = (current.KernelTime100ns - previous.KernelTime100ns) + (current.UserTime100ns - previous.UserTime100ns);
            var idle = current.IdleTime100ns - previous.IdleTime100ns;
            if (total > 0)
                server.CpuPercent = Math.Clamp((total - idle) * 100.0 / total, 0, 100);
        }

        _previousSystem = current;
        return server;
    }

    private Dictionary<int, ProcessMetrics> ComposeProcesses(
        CollectionInput input, Dictionary<int, PoolMembership> members, double elapsed)
    {
        var entries = input.Processes.ToDictionary(p => p.Pid);
        var result = new Dictionary<int, ProcessMetrics>();
        var seen = new HashSet<(int, long)>();

        foreach (var (pid, membership) in members)
        {
            entries.TryGetValue(pid, out var entry);
            var metrics = new ProcessMetrics
            {
                Pid = pid,
                ParentPid = entry.ParentPid,
                Name = entry.Name ?? "",
                IsWorkerProcess = membership.IsWorkerProcess,
                ThreadCount = entry.ThreadCount,
            };

            input.ProcessSamples.TryGetValue(pid, out var sample);
            var hasSample = input.ProcessSamples.ContainsKey(pid);
            IoTotals? etw = input.EtwIo is null ? null : input.EtwIo.TryGetValue(pid, out var io) ? io : new IoTotals();

            if (hasSample)
            {
                metrics.StartTimeUtc = sample.CreationTimeUtcTicks > 0
                    ? new DateTime(sample.CreationTimeUtcTicks, DateTimeKind.Utc)
                    : null;
                metrics.WorkingSetBytes = sample.WorkingSetBytes;
                metrics.PrivateBytes = sample.PrivateBytes;
                metrics.HandleCount = sample.HandleCount;

                var key = (pid, sample.CreationTimeUtcTicks);
                seen.Add(key);
                var state = new ProcessState(sample, etw);
                if (_previous.TryGetValue(key, out var last) && elapsed > 0)
                    ApplyRates(metrics, last, state, elapsed);
                else
                    ApplyZeroRates(metrics, etw is not null);
                _previous[key] = state;
            }
            else
            {
                ApplyZeroRates(metrics, etw is not null);
            }

            if (input.DbConnectionsByPid is { } tcp)
                metrics.DbConnections = tcp.GetValueOrDefault(pid);
            if (input.DbSessionsByPid is { } dmv)
                metrics.DbSessions = dmv.GetValueOrDefault(pid).Sessions;

            result[pid] = metrics;
        }

        foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
            _previous.Remove(stale);

        return result;
    }

    private void ApplyRates(ProcessMetrics metrics, ProcessState last, ProcessState now, double elapsed)
    {
        var cpuDelta = now.Sample.CpuTime100ns - last.Sample.CpuTime100ns;
        var capacity = elapsed * 10_000_000.0 * Math.Max(1, processorCount);
        metrics.CpuPercent = Math.Clamp(cpuDelta / capacity * 100.0, 0, 100);

        if (now.Etw is { } io && last.Etw is { } lastIo)
        {
            metrics.DiskReadBytesPerSec = Rate(io.DiskReadBytes, lastIo.DiskReadBytes, elapsed);
            metrics.DiskWriteBytesPerSec = Rate(io.DiskWriteBytes, lastIo.DiskWriteBytes, elapsed);
            metrics.NetworkSentBytesPerSec = Rate(io.NetSentBytes, lastIo.NetSentBytes, elapsed);
            metrics.NetworkReceivedBytesPerSec = Rate(io.NetReceivedBytes, lastIo.NetReceivedBytes, elapsed);
        }
        else if (now.Etw is not null)
        {
            ApplyZeroRates(metrics, true);
        }
        else
        {
            // Without ETW, fall back to the process I/O counters. Read/write calls on sockets are
            // counted as "other" I/O there, so this is close to file I/O for worker processes.
            metrics.DiskReadBytesPerSec = Rate(now.Sample.IoReadBytes, last.Sample.IoReadBytes, elapsed);
            metrics.DiskWriteBytesPerSec = Rate(now.Sample.IoWriteBytes, last.Sample.IoWriteBytes, elapsed);
        }
    }

    private static void ApplyZeroRates(ProcessMetrics metrics, bool hasEtw)
    {
        metrics.CpuPercent = 0;
        metrics.DiskReadBytesPerSec = 0;
        metrics.DiskWriteBytesPerSec = 0;
        metrics.NetworkSentBytesPerSec = hasEtw ? 0 : null;
        metrics.NetworkReceivedBytesPerSec = hasEtw ? 0 : null;
    }

    private static double Rate(long current, long previous, double elapsed) =>
        current >= previous ? (current - previous) / elapsed : 0;

    private static List<AppPoolMetrics> ComposePools(
        CollectionInput input, Dictionary<int, PoolMembership> members, Dictionary<int, ProcessMetrics> processes)
    {
        var poolInfos = new Dictionary<string, AppPoolInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in input.Topology?.AppPools ?? [])
            poolInfos.TryAdd(info.Name, info);
        foreach (var pool in members.Values.Select(m => m.AppPool))
            poolInfos.TryAdd(pool, new AppPoolInfo { Name = pool, State = "Unknown" });

        var applications = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var loggedPools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in input.Topology?.Sites ?? [])
        {
            foreach (var app in site.Applications)
            {
                if (site.EtwLoggingEnabled)
                    loggedPools.Add(app.AppPool);
                if (!applications.TryGetValue(app.AppPool, out var list))
                    applications[app.AppPool] = list = [];
                list.Add(site.Name + (app.Path == "/" ? "/" : app.Path));
            }
        }

        // Site HTTP traffic is credited to the pool of the site's root application.
        var httpByPool = new Dictionary<string, (double Sent, double Received)>(StringComparer.OrdinalIgnoreCase);
        if (input.Counters is { } siteCounters)
        {
            foreach (var site in input.Topology?.Sites ?? [])
            {
                if (!PerfInstanceName.TryGet(siteCounters.Sites, site.Name, out var values))
                    continue;
                var current = httpByPool.GetValueOrDefault(site.RootAppPool);
                httpByPool[site.RootAppPool] = (current.Sent + (values.BytesSentPerSec ?? 0), current.Received + (values.BytesReceivedPerSec ?? 0));
            }
        }

        var byPool = members
            .GroupBy(kv => kv.Value.AppPool, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(kv => processes[kv.Key]).OrderBy(p => p.IsWorkerProcess ? 0 : 1).ThenBy(p => p.Pid).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<AppPoolMetrics>();
        foreach (var info in poolInfos.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var procs = byPool.GetValueOrDefault(info.Name) ?? [];
            var pool = new AppPoolMetrics
            {
                Name = info.Name,
                State = info.State,
                RuntimeVersion = info.RuntimeVersion,
                PipelineMode = info.PipelineMode,
                Identity = info.Identity,
                Applications = applications.GetValueOrDefault(info.Name) ?? [],
                Processes = procs,
                ProcessCount = procs.Count,
                WorkerProcessCount = procs.Count(p => p.IsWorkerProcess),
                CpuPercent = procs.Sum(p => p.CpuPercent),
                WorkingSetBytes = procs.Sum(p => p.WorkingSetBytes),
                PrivateBytes = procs.Sum(p => p.PrivateBytes),
                ThreadCount = procs.Sum(p => p.ThreadCount),
                HandleCount = procs.Sum(p => p.HandleCount),
                DiskReadBytesPerSec = SumOrNull(procs, p => p.DiskReadBytesPerSec) ?? 0,
                DiskWriteBytesPerSec = SumOrNull(procs, p => p.DiskWriteBytesPerSec) ?? 0,
                NetworkSentBytesPerSec = input.EtwIo is null ? null : SumOrNull(procs, p => p.NetworkSentBytesPerSec) ?? 0,
                NetworkReceivedBytesPerSec = input.EtwIo is null ? null : SumOrNull(procs, p => p.NetworkReceivedBytesPerSec) ?? 0,
                DbConnections = input.DbConnectionsByPid is null ? null : procs.Sum(p => p.DbConnections ?? 0),
                Response = input.ResponseByPool is null || !loggedPools.Contains(info.Name)
                    ? null
                    : input.ResponseByPool.GetValueOrDefault(info.Name) ?? new ResponseStats(),
            };

            if (input.Counters is not null)
            {
                var http = httpByPool.GetValueOrDefault(info.Name);
                pool.HttpBytesSentPerSec = http.Sent;
                pool.HttpBytesReceivedPerSec = http.Received;
            }

            if (input.DbSessionsByPid is { } dmv)
            {
                pool.DbSessions = procs.Sum(p => dmv.GetValueOrDefault(p.Pid).Sessions);
                pool.DbActiveSessions = procs.Sum(p => dmv.GetValueOrDefault(p.Pid).Active);
            }

            if (input.Counters is { } counters)
            {
                var workers = procs.Where(p => p.IsWorkerProcess)
                    .Select(p => counters.Workers.TryGetValue(p.Pid, out var w) ? w : (WorkerCounterValues?)null)
                    .Where(w => w is not null)
                    .Select(w => w!.Value)
                    .ToList();
                pool.ActiveRequests = workers.Sum(w => w.ActiveRequests ?? 0);
                pool.RequestsPerSec = workers.Sum(w => w.RequestsPerSec ?? 0);
                pool.QueueLength = PerfInstanceName.TryGet(counters.QueueLengths, info.Name, out var queue) ? queue : 0;
            }

            result.Add(pool);
        }

        return result;
    }

    private static List<SiteMetrics> ComposeSites(CollectionInput input)
    {
        var result = new List<SiteMetrics>();
        foreach (var site in (input.Topology?.Sites ?? []).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var metrics = new SiteMetrics
            {
                Id = site.Id,
                Name = site.Name,
                State = site.State,
                AppPool = site.RootAppPool,
                Bindings = site.Bindings,
                EtwLoggingEnabled = site.EtwLoggingEnabled,
                Response = input.ResponseBySite is null || !site.EtwLoggingEnabled
                    ? null
                    : input.ResponseBySite.GetValueOrDefault(site.Id) ?? new ResponseStats(),
            };

            if (input.Counters is { } counters)
            {
                var values = PerfInstanceName.TryGet(counters.Sites, site.Name, out var v) ? v : new SiteCounterValues(0, 0, 0, 0);
                metrics.CurrentConnections = values.CurrentConnections;
                metrics.RequestsPerSec = values.RequestsPerSec;
                metrics.BytesSentPerSec = values.BytesSentPerSec;
                metrics.BytesReceivedPerSec = values.BytesReceivedPerSec;
            }

            result.Add(metrics);
        }

        return result;
    }

    private static double? SumOrNull(List<ProcessMetrics> processes, Func<ProcessMetrics, double?> selector)
    {
        double? sum = null;
        foreach (var p in processes)
        {
            if (selector(p) is { } value)
                sum = (sum ?? 0) + value;
        }

        return sum;
    }

    private readonly record struct ProcessState(ProcessSample Sample, IoTotals? Etw);
}
