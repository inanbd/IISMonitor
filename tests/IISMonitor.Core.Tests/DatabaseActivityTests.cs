using IISMonitor.Core.Collection;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;

namespace IISMonitor.Core.Tests;

public sealed class DatabaseActivityTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iismonitor-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData("SELECT * FROM Users WHERE Email = 'alice@example.com' AND Id = 42", "SELECT * FROM Users WHERE Email = ? AND Id = ?")]
    [InlineData("select N'it''s' , 3.14, 1e10, 0xFF, -7", "select ? , ?, ?, ?, -?")]
    [InlineData("SELECT [Order 1].[Col2] FROM \"T 3\" WHERE x=@p0 AND y = #tmp1.z", "SELECT [Order 1].[Col2] FROM \"T 3\" WHERE x=@p0 AND y = #tmp1.z")]
    [InlineData("SELECT 1 -- note 'secret'\n/* outer /* inner 'x' */ still */ FROM t2", "SELECT ? FROM t2")]
    [InlineData("  EXEC   dbo.GetOrders\r\n  @CustomerId = 17  ", "EXEC dbo.GetOrders @CustomerId = ?")]
    [InlineData("UPDATE t SET a = 'unterminated", "UPDATE t SET a = ?")]
    public void Normalizer_strips_literals_but_keeps_names(string sql, string expected) =>
        Assert.Equal(expected, SqlTextNormalizer.Normalize(sql));

    [Fact]
    public void Normalizer_caps_length()
    {
        var sql = "SELECT " + string.Join(", ", Enumerable.Range(0, 5000).Select(i => "col" + i));
        Assert.Equal(SqlTextNormalizer.MaxLength, SqlTextNormalizer.Normalize(sql).Length);
        Assert.Equal("", SqlTextNormalizer.Normalize(null));
    }

    private static RequestObservation Request(
        int session, int pid, double elapsedMs, string status = "running", string? wait = null, int blockedBy = 0,
        bool local = true, string? textKey = "k1", DateTime? start = null, string command = "SELECT") =>
        new(session, 0, start ?? T0, status, command, elapsedMs, elapsedMs / 2, 100, 0, wait, blockedBy, "Shop", "0xABC", textKey, local, pid, "app", "web");

    [Fact]
    public void Accumulator_averages_load_and_reports_the_latest_blocking_picture()
    {
        var acc = new DbActivityAccumulator(2000);
        acc.Add(new DbSample
        {
            TimeUtc = T0,
            Requests = [Request(51, 100, 10), Request(52, 100, 20, "suspended", "PAGEIOLATCH_SH"), Request(60, 0, 5, local: false)],
            LocalSessions = [new(51, 100, "running", 0), new(52, 100, "running", 0), new(53, 200, "sleeping", 1)],
        });
        acc.Add(new DbSample
        {
            TimeUtc = T0.AddSeconds(1),
            Requests = [Request(52, 100, 1020, "suspended", "LCK_M_S", blockedBy: 53)],
            LocalSessions = [new(52, 100, "running", 0), new(53, 200, "sleeping", 1)],
        });

        var result = acc.Drain(_ => ("SELECT ?", null))!;

        Assert.Equal(2, result.Samples);
        var shop = result.ByPid[100];
        Assert.Equal(1.5, shop.Load);         // 2 then 1 running
        Assert.Equal(0.5, shop.CpuLoad);      // one "running" in the first sample only
        Assert.Equal(1, shop.Blocked);
        Assert.Equal(1, shop.Sessions);
        var holder = result.ByPid[200];
        Assert.Equal(1, holder.Blocking);
        Assert.Equal(1, holder.IdleInTransaction);
        Assert.Equal(0.5, result.OtherServersLoad);
        Assert.Equal("SELECT ?", Assert.Single(result.Running).Statement);

        Assert.Null(acc.Drain(_ => (null, null)));
    }

    [Fact]
    public void Accumulator_reports_a_slow_query_once_it_finishes()
    {
        var acc = new DbActivityAccumulator(2000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), Requests = [Request(51, 100, 2500, "suspended", "LCK_M_U", blockedBy: 70)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(2), Requests = [Request(51, 100, 3500, "suspended", "LCK_M_U", blockedBy: 70)] });
        Assert.Equal(1, acc.Drain(_ => (null, null))!.ByPid[100].SlowRunning);
        Assert.Contains("k1", acc.PendingTextKeys());

        // Same session, new request (different start time): the first one has finished.
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(3), Requests = [Request(51, 100, 100, start: T0.AddSeconds(3))] });
        var result = acc.Drain(key => key == "k1" ? ("UPDATE t SET a = ?", "Shop.dbo.SaveOrder") : (null, null))!;

        var slow = Assert.Single(result.CompletedSlow);
        Assert.Equal(3500, slow.DurationMs);
        Assert.Equal(T0.AddMilliseconds(3500), slow.EndUtc);
        Assert.Equal("LCK_M_U", slow.MainWait);
        Assert.True(slow.WasBlocked);
        Assert.Equal("UPDATE t SET a = ?", slow.Statement);
        Assert.Equal("Shop.dbo.SaveOrder", slow.ObjectName);
        Assert.Equal(100, slow.Pid);
    }

    [Fact]
    public void Accumulator_ignores_idle_listener_waits()
    {
        var acc = new DbActivityAccumulator(2000);
        acc.Add(new DbSample
        {
            TimeUtc = T0,
            Requests =
            [
                Request(51, 100, 60_000, "suspended", "BROKER_RECEIVE_WAITFOR"),
                Request(52, 100, 9_000, "suspended", "WAITFOR", command: "WAITFOR"),
            ],
        });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1) });

        var result = acc.Drain(_ => (null, null))!;
        Assert.Equal(0, result.ByPid.GetValueOrDefault(100).Load);
        Assert.Empty(result.CompletedSlow);
        Assert.Empty(result.Running);
    }

    [Fact]
    public void Composer_rolls_database_activity_up_to_app_pools()
    {
        var input = new CollectionInput
        {
            TimestampUtc = T0,
            Topology = new IisTopology { AppPools = [new AppPoolInfo { Name = "Shop" }, new AppPoolInfo { Name = "Api" }] },
            Processes = [new(100, 4, "w3wp.exe", 10), new(101, 100, "dotnet.exe", 10), new(200, 4, "w3wp.exe", 10), new(300, 4, "ssms.exe", 5), new(400, 4, "sqlservr.exe", 80)],
            WorkerProcessPools = new Dictionary<int, string> { [100] = "Shop", [200] = "Api" },
            ProcessSamples = new Dictionary<int, ProcessSample> { [400] = new(400, 1, 0, 2_000_000_000, 1, 1, 0, 0) },
            SqlServerPids = [400],
            DbActivity = new DbActivityInput
            {
                Samples = 1,
                SlowThresholdMs = 2000,
                ByPid = new()
                {
                    [100] = new(1.0, 0.5, 1, 0, 0, 1, 5, 1),
                    [101] = new(2.0, 2.0, 0, 1, 1, 0, 3, 2),
                    [300] = new(0.5, 0.5, 0, 0, 0, 0, 1, 1),
                },
                OtherServersLoad = 0.25,
                Running = [new RunningQuery { Pid = 101 }, new RunningQuery { Pid = 300 }],
                CompletedSlow = [new SlowQuery { Pid = 101 }, new SlowQuery { Pid = 100 }],
            },
        };

        var snapshot = new SnapshotComposer("WEB01", 4).Compose(input);

        var shop = snapshot.AppPools.Single(p => p.Name == "Shop");
        Assert.Equal(3.0, shop.DbLoad);
        Assert.Equal(2.5, shop.DbCpuLoad);
        Assert.Equal(1, shop.DbBlocked);
        Assert.Equal(1, shop.DbBlocking);
        Assert.Equal(1, shop.DbIdleInTransaction);
        Assert.Equal(1, shop.DbSlowRunning);
        Assert.Equal(2, shop.DbSlowCompleted);
        Assert.Equal(8, shop.DbSessions);
        Assert.Equal(0, snapshot.AppPools.Single(p => p.Name == "Api").DbLoad);

        var db = snapshot.Database!;
        Assert.Equal(3.0, db.PoolLoad);
        Assert.Equal(0.5, db.OtherLocalLoad);
        Assert.Equal(0.25, db.OtherServersLoad);
        Assert.Equal(["Shop", null], db.Running.Select(r => r.AppPool));
        Assert.All(db.CompletedSlow, q => Assert.Equal("Shop", q.AppPool));
        Assert.Equal(2_000_000_000, snapshot.Server.SqlServerMemoryBytes);
        Assert.Equal(0, snapshot.Server.SqlServerCpuPercent);
        Assert.Equal(1, MetricCatalog.Extract(snapshot.AppPools.Single(p => p.Name == "Shop"), 1)["db_slow"] - 1);
    }

    [Fact]
    public void Slow_queries_are_stored_grouped_and_purged()
    {
        var store = new HistoryStore(Path.Combine(_directory, "history.db"));
        store.Initialize();

        SlowQuery Slow(string pool, string statement, double ms, string? hash, int minute, bool blocked = false, string? wait = null) => new()
        {
            StartUtc = T0.AddMinutes(minute),
            EndUtc = T0.AddMinutes(minute).AddMilliseconds(ms),
            DurationMs = ms,
            CpuMs = ms / 2,
            LogicalReads = 1000,
            AppPool = pool,
            Database = "Shop",
            Statement = statement,
            QueryHash = hash,
            WasBlocked = blocked,
            MainWait = wait,
            Pid = 100,
        };

        store.WriteSlowQueries(
        [
            Slow("ShopPool", "SELECT ? FROM Orders", 3000, "0x1", 0, wait: "PAGEIOLATCH_SH"),
            Slow("ShopPool", "SELECT ? FROM Orders", 5000, "0x1", 1, blocked: true, wait: "LCK_M_S"),
            Slow("ShopPool", "SELECT ? FROM Orders", 4000, "0x1", 2, wait: "LCK_M_S"),
            Slow("ApiPool", "UPDATE Stock SET n = ?", 2500, null, 3),
            Slow("", "SELECT ? FROM sys.objects", 9000, "0x9", 4),
            Slow("ShopPool", "SELECT ? FROM Old", 2200, "0x2", -60 * 24 * 10),
        ]);

        var report = store.QuerySlowQueries(new SlowQueryRequest { FromUtc = T0.AddHours(-1), ToUtc = T0.AddHours(1) });

        Assert.Equal(3, report.Groups.Count);
        var orders = report.Groups.Single(g => g.Statement == "SELECT ? FROM Orders");
        Assert.Equal(3, orders.Count);
        Assert.Equal(12_000, orders.TotalMs);
        Assert.Equal(5000, orders.MaxMs);
        Assert.Equal(4000, orders.AverageMs);
        Assert.Equal(1, orders.BlockedCount);
        Assert.Equal("LCK_M_S", orders.MainWait);
        Assert.Equal(["ShopPool", "", "ApiPool"], report.Pools.Select(p => p.AppPool));
        Assert.Equal(3, report.Pools[0].Count);

        var onlyApi = store.QuerySlowQueries(new SlowQueryRequest { FromUtc = T0.AddHours(-1), ToUtc = T0.AddHours(1), AppPool = "ApiPool" });
        Assert.Equal("UPDATE Stock SET n = ?", Assert.Single(onlyApi.Groups).Statement);

        Assert.Equal(1, store.Purge(T0.AddDays(-1)));
        Assert.Empty(store.QuerySlowQueries(new SlowQueryRequest { FromUtc = T0.AddDays(-20), ToUtc = T0.AddDays(-5) }).Groups);
    }

    [Fact]
    public void Settings_keep_the_slow_threshold_sane()
    {
        Assert.Equal(2, new Settings.MonitorSettings().Normalize().SlowQueryThresholdSeconds);
        Assert.Equal(1, new Settings.MonitorSettings().Normalize().SqlActivityIntervalSeconds);
        Assert.Equal(0.5, new Settings.MonitorSettings { SlowQueryThresholdSeconds = 0 }.Normalize().SlowQueryThresholdSeconds);
        Assert.Equal(2, new Settings.MonitorSettings { SlowQueryThresholdSeconds = double.NaN }.Normalize().SlowQueryThresholdSeconds);
        Assert.Equal(10, new Settings.MonitorSettings { SqlActivityIntervalSeconds = 99 }.Normalize().SqlActivityIntervalSeconds);
    }
}
