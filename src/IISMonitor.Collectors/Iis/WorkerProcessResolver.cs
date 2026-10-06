using System.Runtime.InteropServices;
using IISMonitor.Core.Collection;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Works out which app pool each w3wp.exe serves. The "W3SVC_W3WP" counter instance names
/// ("&lt;pid&gt;_&lt;pool&gt;") answer this for free on every tick; for a worker that hasn't registered
/// its counters yet, IIS is asked directly. Answers are cached for as long as the process lives.
/// </summary>
internal sealed class WorkerProcessResolver
{
    private readonly Dictionary<int, string> _cache = [];
    private DateTime _lastIisQueryUtc = DateTime.MinValue;

    public Dictionary<int, string> Resolve(
        IReadOnlyList<ProcessEntry> processes,
        IisCounterValues? counters,
        IReadOnlyCollection<string> poolNames)
    {
        var workers = processes
            .Where(p => p.Name.Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToHashSet();

        foreach (var gone in _cache.Keys.Where(pid => !workers.Contains(pid)).ToList())
            _cache.Remove(gone);

        if (counters is not null)
        {
            foreach (var (pid, values) in counters.Workers)
            {
                if (!workers.Contains(pid) || !PerfInstanceName.TryParseWorkerInstance(values.AppPoolInstance, out _, out var instancePool))
                    continue;
                _cache[pid] = PerfInstanceName.ResolvePoolName(instancePool, poolNames) ?? instancePool;
            }
        }

        // Asking IIS costs an RPC to WAS, so only do it for unknown workers and at most once a second.
        if (workers.Any(pid => !_cache.ContainsKey(pid)) && DateTime.UtcNow - _lastIisQueryUtc > TimeSpan.FromSeconds(1))
        {
            _lastIisQueryUtc = DateTime.UtcNow;
            try
            {
                using var manager = new ServerManager();
                foreach (var worker in manager.WorkerProcesses)
                {
                    if (workers.Contains(worker.ProcessId))
                        _cache[worker.ProcessId] = worker.AppPoolName;
                }
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // WAS not running or no access: those workers stay unassigned this tick.
            }
        }

        return new Dictionary<int, string>(_cache);
    }
}
