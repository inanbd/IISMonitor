using IISMonitor.Core.Collection;

namespace IISMonitor.Collectors.Database;

/// <summary>
/// Finds loopback TCP connections between two processes of the same app pool, such as w3wp.exe
/// forwarding requests to an out-of-process ASP.NET Core or iisnode child. That traffic is the
/// site's own HTTP traffic relayed inside the machine; counting it as the pool's network traffic
/// would add it twice more on top of the site's IIS counters.
/// </summary>
internal static class LoopbackPairs
{
    public static HashSet<(int Pid, int LocalPort, int RemotePort)> WithinPools(
        IReadOnlyList<TcpConnection> connections,
        IReadOnlyDictionary<int, PoolMembership> members)
    {
        var result = new HashSet<(int, int, int)>();
        var byEndpoints = new Dictionary<(int Local, int Remote), int>();
        foreach (var c in connections)
        {
            if (c.RemoteIsLoopback && c.State == Native.NativeMethods.MIB_TCP_STATE_ESTAB)
                byEndpoints.TryAdd((c.LocalPort, c.RemotePort), c.Pid);
        }

        foreach (var c in connections)
        {
            if (!c.RemoteIsLoopback || c.State != Native.NativeMethods.MIB_TCP_STATE_ESTAB || !members.TryGetValue(c.Pid, out var pool))
                continue;
            if (byEndpoints.TryGetValue((c.RemotePort, c.LocalPort), out var peer)
                && members.TryGetValue(peer, out var peerPool)
                && string.Equals(pool.AppPool, peerPool.AppPool, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((c.Pid, c.LocalPort, c.RemotePort));
            }
        }

        return result;
    }
}
