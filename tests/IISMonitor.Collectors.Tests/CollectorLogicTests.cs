using IISMonitor.Collectors.Database;
using IISMonitor.Collectors.Iis;
using IISMonitor.Collectors.Native;
using IISMonitor.Core.Collection;

namespace IISMonitor.Collectors.Tests;

public class CollectorLogicTests
{
    private const int Listen = NativeMethods.MIB_TCP_STATE_LISTEN;
    private const int Established = NativeMethods.MIB_TCP_STATE_ESTAB;

    [Fact]
    public void Counts_established_sql_connections_per_pool_process()
    {
        TcpConnection[] connections =
        [
            new(100, Established, 50001, 1433),
            new(100, Established, 50002, 1433),
            new(100, 8 /* CLOSE_WAIT */, 50003, 1433),
            new(100, Established, 50004, 443),
            new(200, Established, 50005, 1433),
            new(300, Established, 50006, 1433),
        ];

        var counts = DbConnectionCounter.Count(connections, [100, 200], [], [1433], detectLocalSqlServer: false, out var ports);

        Assert.Equal(2, counts[100]);
        Assert.Equal(1, counts[200]);
        Assert.False(counts.ContainsKey(300));
        Assert.Equal([1433], ports);
    }

    [Fact]
    public void Detects_ports_a_local_sql_server_listens_on()
    {
        ProcessEntry[] processes = [new(50, 1, "sqlservr.exe", 80), new(100, 1, "w3wp.exe", 30)];
        TcpConnection[] connections =
        [
            new(50, Listen, 49733, 0),
            new(100, Established, 51000, 49733),
            new(100, Listen, 8080, 0),
        ];

        var counts = DbConnectionCounter.Count(connections, [100], processes, [1433], detectLocalSqlServer: true, out var ports);

        Assert.Equal(1, counts[100]);
        Assert.Equal([1433, 49733], ports.Order());
    }

    [Fact]
    public void Finds_loopback_relays_inside_one_pool_only()
    {
        var members = new Dictionary<int, PoolMembership>
        {
            [100] = new("Shop", true),     // w3wp
            [101] = new("Shop", false),    // its out-of-process dotnet.exe
            [200] = new("Api", true),
        };
        TcpConnection[] connections =
        [
            // w3wp 100 <-> child 101 over loopback: excluded on both ends.
            new(100, Established, 50000, 5000, RemoteIsLoopback: true),
            new(101, Established, 5000, 50000, RemoteIsLoopback: true),
            // Shop -> local SQL Server (pid 50) over loopback: real database traffic, kept.
            new(100, Established, 50001, 1433, RemoteIsLoopback: true),
            new(50, Established, 1433, 50001, RemoteIsLoopback: true),
            // Shop -> Api pool over loopback: different pools, kept.
            new(101, Established, 50002, 8080, RemoteIsLoopback: true),
            new(200, Established, 8080, 50002, RemoteIsLoopback: true),
            // Not loopback.
            new(100, Established, 50003, 443),
        ];

        var excluded = LoopbackPairs.WithinPools(connections, members);

        Assert.Equal(
            new HashSet<(int, int, int)> { (100, 50000, 5000), (101, 5000, 50000) },
            excluded);
    }

    [Theory]
    [InlineData(3, 3L)]
    [InlineData("File,ETW", 3L)]
    [InlineData("ETW", 2L)]
    [InlineData("2", 2L)]
    [InlineData(null, 0L)]
    public void Reads_log_target_flags(object? value, long expected)
    {
        var names = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["File"] = 1, ["ETW"] = 2 };
        Assert.Equal(expected, IisLoggingConfig.Flags(value, names));
    }
}
