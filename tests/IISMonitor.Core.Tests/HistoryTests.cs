using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;

namespace IISMonitor.Core.Tests;

public sealed class HistoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iismonitor-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: Windows can hold SQLite files open briefly after the pools are cleared.
        }
    }

    private HistoryStore CreateStore()
    {
        var store = new HistoryStore(Path.Combine(_directory, "history.db"));
        store.Initialize();
        return store;
    }

    private static MonitorSnapshot Snapshot(DateTime time, double cpu, int dbConnections) => new()
    {
        TimestampUtc = time,
        IntervalSeconds = 1,
        Server = new ServerMetrics { MachineName = "WEB01", CpuPercent = cpu, MemoryTotalBytes = 1000, MemoryUsedBytes = 500 },
        AppPools = [new AppPoolMetrics { Name = "ShopPool", CpuPercent = cpu, DbConnections = dbConnections, ProcessCount = 1 }],
        Sites = [new SiteMetrics { Id = 1, Name = "Shop", RequestsPerSec = 10, CurrentConnections = 5 }],
    };

    [Fact]
    public void Aggregator_averages_and_keeps_maximums()
    {
        var aggregator = new HistoryAggregator();
        aggregator.Add(Snapshot(T0, 10, 2));
        aggregator.Add(Snapshot(T0.AddSeconds(1), 30, 6));

        var rows = aggregator.Flush(
            T0.AddSeconds(10),
            new Dictionary<string, ResponseStats> { ["Shop"] = new() { RequestCount = 20, AverageMs = 40, P95Ms = 90, MaxMs = 120, ServerErrors = 5 } },
            new Dictionary<string, ResponseStats>());

        var pool = rows.Single(r => r.Kind == EntityKind.AppPool);
        Assert.Equal(T0, pool.BucketStartUtc);
        Assert.Equal(20, pool.Values["cpu"]);
        Assert.Equal(30, pool.Values["cpu_max"]);
        Assert.Equal(4, pool.Values["db_conn"]);
        Assert.Equal(6, pool.Values["db_conn_max"]);
        Assert.Null(pool.Values["db_sessions"]);
        Assert.Equal(0, pool.Values["req_count"]);
        Assert.Null(pool.Values["resp_avg"]);

        var site = rows.Single(r => r.Kind == EntityKind.Site);
        Assert.Equal(20, site.Values["req_count"]);
        Assert.Equal(40, site.Values["resp_avg"]);
        Assert.Equal(0.5, site.Values["err_5xx"]);

        Assert.Single(rows, r => r.Kind == EntityKind.Server && r.Name == "WEB01");
        Assert.Empty(aggregator.Flush(T0.AddSeconds(20), null, null));
    }

    [Fact]
    public void Aggregator_omits_response_values_for_untraced_sites()
    {
        var aggregator = new HistoryAggregator();
        aggregator.Add(Snapshot(T0, 10, 2));
        var rows = aggregator.Flush(T0.AddSeconds(10), new Dictionary<string, ResponseStats>(), new Dictionary<string, ResponseStats>(),
            loggedSites: new HashSet<string>(), loggedPools: new HashSet<string>());

        Assert.Null(rows.Single(r => r.Kind == EntityKind.Site).Values["req_count"]);
    }

    [Fact]
    public void Store_round_trips_and_downsamples()
    {
        var store = CreateStore();
        var rows = new List<HistoryRow>();
        for (var i = 0; i < 60; i++)
        {
            rows.Add(new HistoryRow(EntityKind.AppPool, "ShopPool", T0.AddSeconds(i * 10), new Dictionary<string, double?>
            {
                ["cpu"] = i,
                ["cpu_max"] = i + 1,
                ["req_count"] = i % 2 == 0 ? 10 : 30,
                ["resp_avg"] = i % 2 == 0 ? 100 : 200,
            }));
        }

        store.Write(rows);

        var full = store.Query(new HistoryQuery { Kind = EntityKind.AppPool, Name = "ShopPool", FromUtc = T0, ToUtc = T0.AddMinutes(10), MaxPoints = 1000 });
        Assert.Equal(60, full.Timestamps.Count);
        Assert.Equal(59, full.Series["cpu"][59]);
        Assert.Null(full.Series["db_conn"][0]);

        // 10 minutes into 10 points = 60-second buckets of 6 rows each.
        var coarse = store.Query(new HistoryQuery { Kind = EntityKind.AppPool, Name = "ShopPool", FromUtc = T0, ToUtc = T0.AddMinutes(10), MaxPoints = 10 });
        Assert.Equal(60, coarse.BucketSeconds);
        Assert.Equal(10, coarse.Timestamps.Count);
        Assert.Equal(2.5, coarse.Series["cpu"][0]);
        Assert.Equal(6, coarse.Series["cpu_max"][0]);
        // Weighted by request count: (3*10*100 + 3*30*200) / 120 = 175.
        Assert.Equal(175, coarse.Series["resp_avg"][0]!.Value, 6);
        Assert.Equal(120, coarse.Series["req_count"][0]);
    }

    [Fact]
    public void Store_lists_entities_and_purges_old_rows()
    {
        var store = CreateStore();
        store.Write(
        [
            new HistoryRow(EntityKind.Site, "Old", T0, new Dictionary<string, double?> { ["rps"] = 1 }),
            new HistoryRow(EntityKind.Site, "New", T0.AddDays(8), new Dictionary<string, double?> { ["rps"] = 2 }),
            new HistoryRow(EntityKind.AppPool, "Pool", T0.AddDays(8), new Dictionary<string, double?> { ["cpu"] = 2 }),
        ]);

        Assert.Equal(["New", "Old"], store.ListEntities(EntityKind.Site, T0.AddDays(-1)));
        Assert.Equal(1, store.Purge(T0.AddDays(1)));
        Assert.Equal(["New"], store.ListEntities(EntityKind.Site, T0.AddDays(-1)));
        Assert.Equal(["Pool"], store.ListEntities(EntityKind.AppPool, T0));
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        CreateStore();
        var again = CreateStore();
        again.Write([new HistoryRow(EntityKind.Server, "WEB01", T0, new Dictionary<string, double?> { ["cpu"] = 5 })]);
        Assert.Single(again.Query(new HistoryQuery { Kind = EntityKind.Server, Name = "WEB01", FromUtc = T0, ToUtc = T0.AddHours(1) }).Timestamps);
    }
}
