namespace IISMonitor.Core.Collection;

public readonly record struct PoolMembership(string AppPool, bool IsWorkerProcess);

public static class ProcessTree
{
    /// <summary>
    /// Finds every process that belongs to an app pool: the worker processes themselves and all of
    /// their descendants (out-of-process ASP.NET Core, FastCGI, iisnode and so on).
    /// </summary>
    /// <param name="creationTicks">
    /// Optional process creation times. When given, a child is only accepted if it started after its
    /// parent, which filters out stale parent PIDs that Windows has since reused.
    /// </param>
    public static Dictionary<int, PoolMembership> ResolvePoolMembers(
        IReadOnlyList<ProcessEntry> processes,
        IReadOnlyDictionary<int, string> workerPools,
        Func<int, long?>? creationTicks = null)
    {
        var result = new Dictionary<int, PoolMembership>();
        var alive = new HashSet<int>(processes.Select(p => p.Pid));

        var children = new Dictionary<int, List<int>>();
        foreach (var p in processes)
        {
            if (p.Pid == p.ParentPid || p.Pid == 0)
                continue;
            if (!children.TryGetValue(p.ParentPid, out var list))
                children[p.ParentPid] = list = [];
            list.Add(p.Pid);
        }

        var queue = new Queue<(int Pid, string Pool)>();
        foreach (var (pid, pool) in workerPools)
        {
            if (!alive.Contains(pid))
                continue;
            result[pid] = new PoolMembership(pool, true);
            queue.Enqueue((pid, pool));
        }

        while (queue.Count > 0)
        {
            var (parent, pool) = queue.Dequeue();
            if (!children.TryGetValue(parent, out var kids))
                continue;

            foreach (var child in kids)
            {
                if (result.ContainsKey(child))
                    continue;
                if (creationTicks is not null)
                {
                    var parentCreated = creationTicks(parent);
                    var childCreated = creationTicks(child);
                    if (parentCreated is not null && childCreated is not null && childCreated < parentCreated)
                        continue;
                }

                result[child] = new PoolMembership(pool, false);
                queue.Enqueue((child, pool));
            }
        }

        return result;
    }
}
