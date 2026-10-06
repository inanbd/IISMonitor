using IISMonitor.Core.Collection;
using IISMonitor.Core.Models;

namespace IISMonitor.Core.Tests;

public class SnapshotComposerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IisTopology Topology() => new()
    {
        AppPools =
        [
            new AppPoolInfo { Name = "ShopPool", State = "Started" },
            new AppPoolInfo { Name = "ApiPool", State = "Started" },
            new AppPoolInfo { Name = "IdlePool", State = "Started" },
        ],
        Sites =
        [
            new SiteInfo
            {
                Id = 1, Name = "Shop (Live)", State = "Started", EtwLoggingEnabled = true,
                Applications = [new ApplicationInfo("/", "ShopPool"), new ApplicationInfo("/api", "ApiPool")],
            },
            new SiteInfo { Id = 2, Name = "Intranet", State = "Stopped", Applications = [new ApplicationInfo("/", "IdlePool")] },
        ],
    };

    private static CollectionInput Input(DateTime time, long shopCpu, long apiCpu, long etwRead = 0, IisCounterValues? counters = null) => new()
    {
        TimestampUtc = time,
        Topology = Topology(),
        Processes =
        [
            new ProcessEntry(100, 4, "w3wp.exe", 30),
            new ProcessEntry(101, 4, "w3wp.exe", 20),
            new ProcessEntry(200, 101, "dotnet.exe", 10),
            new ProcessEntry(300, 4, "notepad.exe", 1),
        ],
        WorkerProcessPools = new Dictionary<int, string> { [100] = "ShopPool", [101] = "ApiPool" },
        ProcessSamples = new Dictionary<int, ProcessSample>
        {
            [100] = new(100, 1000, shopCpu, 100_000_000, 80_000_000, 500, 0, 0),
            [101] = new(101, 1000, apiCpu, 50_000_000, 40_000_000, 300, 0, 0),
            [200] = new(200, 2000, 0, 20_000_000, 10_000_000, 100, 0, 0),
        },
        EtwIo = new Dictionary<int, IoTotals> { [100] = new(etwRead, 0, 0, 0) },
        Counters = counters,
        DbConnectionsByPid = new Dictionary<int, int> { [100] = 3, [200] = 2 },
        ResponseBySite = new Dictionary<long, ResponseStats> { [1] = new() { RequestCount = 10, AverageMs = 50 } },
        ResponseByPool = new Dictionary<string, ResponseStats>(),
    };

    [Fact]
    public void Groups_worker_processes_and_children_by_pool()
    {
        var composer = new SnapshotComposer("WEB01", 4);
        var snapshot = composer.Compose(Input(T0, 0, 0));

        var shop = snapshot.AppPools.Single(p => p.Name == "ShopPool");
        var api = snapshot.AppPools.Single(p => p.Name == "ApiPool");
        var idle = snapshot.AppPools.Single(p => p.Name == "IdlePool");

        Assert.Equal(1, shop.ProcessCount);
        Assert.Equal(2, api.ProcessCount);
        Assert.Equal(1, api.WorkerProcessCount);
        Assert.Equal([101, 200], api.Processes.Select(p => p.Pid));
        Assert.Equal(0, idle.ProcessCount);
        Assert.Equal(70_000_000, api.WorkingSetBytes);
        Assert.Equal(30, api.ThreadCount);
        Assert.Equal(2, api.DbConnections);
        Assert.Equal(3, shop.DbConnections);
        Assert.DoesNotContain(snapshot.AppPools.SelectMany(p => p.Processes), p => p.Pid == 300);
        Assert.Equal(["Shop (Live)/"], shop.Applications);
        Assert.Equal(["Shop (Live)/api"], api.Applications);
    }

    [Fact]
    public void Computes_cpu_percent_against_all_cores()
    {
        var composer = new SnapshotComposer("WEB01", 4);
        composer.Compose(Input(T0, 0, 0));

        // 2 seconds of CPU time over 1 second on 4 cores = 50 %.
        var snapshot = composer.Compose(Input(T0.AddSeconds(1), 20_000_000, 0));

        Assert.Equal(1, snapshot.IntervalSeconds, 3);
        Assert.Equal(50, snapshot.AppPools.Single(p => p.Name == "ShopPool").CpuPercent, 3);
        Assert.Equal(0, snapshot.AppPools.Single(p => p.Name == "ApiPool").CpuPercent, 3);
    }

    [Fact]
    public void Uses_etw_bytes_for_disk_rates()
    {
        var composer = new SnapshotComposer("WEB01", 4);
        composer.Compose(Input(T0, 0, 0, etwRead: 1000));
        var snapshot = composer.Compose(Input(T0.AddSeconds(2), 0, 0, etwRead: 5000));

        var shop = snapshot.AppPools.Single(p => p.Name == "ShopPool");
        Assert.Equal(2000, shop.DiskReadBytesPerSec);
        Assert.Equal(0, shop.NetworkSentBytesPerSec);
    }

    [Fact]
    public void Restarted_process_with_same_pid_starts_fresh()
    {
        var composer = new SnapshotComposer("WEB01", 1);
        composer.Compose(Input(T0, 50_000_000, 0));

        var restarted = Input(T0.AddSeconds(1), 1_000, 0);
        var samples = new Dictionary<int, ProcessSample>(restarted.ProcessSamples)
        {
            [100] = new(100, 9999, 1_000, 1, 1, 1, 0, 0),
        };
        var snapshot = composer.Compose(new CollectionInput
        {
            TimestampUtc = restarted.TimestampUtc,
            Topology = restarted.Topology,
            Processes = restarted.Processes,
            WorkerProcessPools = restarted.WorkerProcessPools,
            ProcessSamples = samples,
        });

        Assert.Equal(0, snapshot.AppPools.Single(p => p.Name == "ShopPool").CpuPercent);
    }

    [Fact]
    public void Maps_iis_counters_to_sites_and_pools()
    {
        var counters = new IisCounterValues
        {
            Sites = new(StringComparer.OrdinalIgnoreCase) { ["Shop [Live]"] = new SiteCounterValues(12, 34.5, 1000, 200) },
            Workers = { [100] = new WorkerCounterValues("100_ShopPool", 3, 20), [101] = new WorkerCounterValues("101_ApiPool", 1, 14.5) },
            QueueLengths = new(StringComparer.OrdinalIgnoreCase) { ["ShopPool"] = 7 },
        };

        var snapshot = new SnapshotComposer("WEB01", 4).Compose(Input(T0, 0, 0, counters: counters));

        var site = snapshot.Sites.Single(s => s.Id == 1);
        Assert.Equal(12, site.CurrentConnections);
        Assert.Equal(34.5, site.RequestsPerSec);
        Assert.Equal("ShopPool", site.AppPool);
        Assert.Equal(10, site.Response!.RequestCount);

        var shop = snapshot.AppPools.Single(p => p.Name == "ShopPool");
        Assert.Equal(3, shop.ActiveRequests);
        Assert.Equal(20, shop.RequestsPerSec);
        Assert.Equal(7, shop.QueueLength);

        // Site 2 has no ETW logging, so no response stats rather than misleading zeros.
        Assert.Null(snapshot.Sites.Single(s => s.Id == 2).Response);
        Assert.Equal(0, snapshot.Sites.Single(s => s.Id == 2).CurrentConnections);
    }

    [Fact]
    public void Computes_server_cpu_from_system_times()
    {
        var composer = new SnapshotComposer("WEB01", 2);
        composer.Compose(new CollectionInput { TimestampUtc = T0, System = new SystemSample(0, 0, 0, 1000, 600) });
        var snapshot = composer.Compose(new CollectionInput
        {
            TimestampUtc = T0.AddSeconds(1),
            // 20M ticks of kernel+user (kernel includes 15M idle) → 25 % busy.
            System = new SystemSample(15_000_000, 17_000_000, 3_000_000, 1000, 400),
        });

        Assert.Equal(25, snapshot.Server.CpuPercent!.Value, 3);
        Assert.Equal(600, snapshot.Server.MemoryUsedBytes);
    }
}
