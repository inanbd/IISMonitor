using IISMonitor.Collectors.Native;
using IISMonitor.Core.Collection;

namespace IISMonitor.Collectors.Database;

/// <summary>
/// Counts established TCP connections from app pool processes to SQL Server, identified by the
/// remote port: the configured ports plus, optionally, every port a local sqlservr.exe listens on
/// (named instances usually use dynamic ports).
/// </summary>
internal static class DbConnectionCounter
{
    public static Dictionary<int, int> Count(
        IReadOnlyList<TcpConnection> connections,
        IReadOnlyCollection<int> poolPids,
        IReadOnlyList<ProcessEntry> processes,
        IReadOnlyCollection<int> configuredPorts,
        bool detectLocalSqlServer,
        out IReadOnlySet<int> sqlPorts)
    {
        var ports = new HashSet<int>(configuredPorts);
        if (detectLocalSqlServer)
        {
            var sqlServerPids = processes
                .Where(p => p.Name.Equals("sqlservr.exe", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Pid)
                .ToHashSet();
            foreach (var c in connections)
            {
                if (c.State == NativeMethods.MIB_TCP_STATE_LISTEN && sqlServerPids.Contains(c.Pid))
                    ports.Add(c.LocalPort);
            }
        }

        sqlPorts = ports;
        var wanted = poolPids as IReadOnlySet<int> ?? poolPids.ToHashSet();
        var counts = new Dictionary<int, int>();
        foreach (var c in connections)
        {
            if (c.State != NativeMethods.MIB_TCP_STATE_ESTAB || !wanted.Contains(c.Pid) || !ports.Contains(c.RemotePort))
                continue;
            counts[c.Pid] = counts.GetValueOrDefault(c.Pid) + 1;
        }

        return counts;
    }
}
