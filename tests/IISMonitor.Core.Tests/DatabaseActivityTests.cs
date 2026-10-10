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
    // With QUOTED_IDENTIFIER OFF, "..." is a string: values go, names stay.
    [InlineData("SELECT \"Col\" FROM dbo.\"Order Lines\" WHERE Name = \"Jane Doe\" AND City IN (\"Oslo\", \"Rome\")",
        "SELECT \"Col\" FROM dbo.\"Order Lines\" WHERE Name = ? AND City IN (?, ?)")]
    [InlineData("SELECT \"x@example.com\" AS \"Mail Box\"", "SELECT ? AS \"Mail Box\"")]
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

        var result = acc.Drain(_ => new SqlText("SELECT ?", null))!;

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

        // No new sample before the next tick: the same picture again, nothing new finished.
        var again = acc.Drain(_ => default)!;
        Assert.Equal(0, again.Samples);
        Assert.Equal(1.5, again.ByPid[100].Load);
        Assert.Equal(1, again.ByPid[100].Blocked);
        Assert.Single(again.Running);
        Assert.Empty(again.CompletedSlow);

        Assert.Null(new DbActivityAccumulator(2000).Drain(_ => default));
    }

    [Fact]
    public void Accumulator_reports_a_slow_query_once_it_finishes()
    {
        var acc = new DbActivityAccumulator(2000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), Requests = [Request(51, 100, 2500, "suspended", "LCK_M_U", blockedBy: 70)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(2), Requests = [Request(51, 100, 3500, "suspended", "LCK_M_U", blockedBy: 70)] });
        Assert.Equal(1, acc.Drain(_ => default)!.ByPid[100].SlowRunning);
        Assert.Contains("k1", acc.PendingTextKeys());

        // Same session, new request (different start time): the first one has finished.
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(3), Requests = [Request(51, 100, 100, start: T0.AddSeconds(3))] });
        var result = acc.Drain(key => key == "k1" ? new SqlText("UPDATE t SET a = ?", "Shop.dbo.SaveOrder") : default)!;

        var slow = Assert.Single(result.CompletedSlow);
        Assert.Equal(3500, slow.DurationMs);
        Assert.Equal(T0.AddMilliseconds(-1500), slow.StartUtc);   // first seen 1.5 s into the query
        Assert.Equal(T0.AddMilliseconds(2000), slow.EndUtc);
        Assert.Equal("LCK_M_U", slow.MainWait);
        Assert.True(slow.WasBlocked);
        Assert.Equal("UPDATE t SET a = ?", slow.Statement);
        Assert.Equal("Shop.dbo.SaveOrder", slow.ObjectName);
        Assert.Equal(100, slow.Pid);
    }

    [Fact]
    public void Accumulator_names_the_statement_a_batch_spent_most_time_in()
    {
        var acc = new DbActivityAccumulator(1000);
        RequestObservation At(double ms, string statementKey) =>
            Request(51, 100, ms, textKey: statementKey) with { BatchKey = "batch" };

        acc.Add(new DbSample { TimeUtc = T0, Requests = [At(1100, "s1")] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), Requests = [At(2100, "s2")] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(2), Requests = [At(3100, "s2")] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(3), Requests = [At(4100, "s3")] });
        Assert.Contains("batch", acc.PendingTextKeys());
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(4) });

        var slow = Assert.Single(acc.Drain(key => key switch
        {
            "batch" => new SqlText("DECLARE @i int = ?; WHILE ...", null),
            "s2" => new SqlText("SET @i += ?;", null),
            _ => new SqlText("other", null),
        })!.CompletedSlow);
        Assert.Equal("SET @i += ?;", slow.Statement);
        Assert.Equal("DECLARE @i int = ?; WHILE ...", slow.Batch);
        Assert.Null(slow.ObjectName);
        Assert.Equal(4100, slow.DurationMs);
    }

    [Fact]
    public void Accumulator_names_the_procedure_instead_of_the_batch()
    {
        var acc = new DbActivityAccumulator(1000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500, textKey: "s1") with { BatchKey = "proc" }] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1) });

        var slow = Assert.Single(acc.Drain(key => key == "proc" ? new SqlText("CREATE PROCEDURE ...", "Shop.dbo.Save") : new SqlText("UPDATE t SET a = ?", "Shop.dbo.Save"))!.CompletedSlow);
        Assert.Equal("Shop.dbo.Save", slow.ObjectName);
        Assert.Null(slow.Batch);
        Assert.Equal("UPDATE t SET a = ?", slow.Statement);
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

        var result = acc.Drain(_ => default)!;
        Assert.Equal(0, result.ByPid.GetValueOrDefault(100).Load);
        Assert.Empty(result.CompletedSlow);
        Assert.Empty(result.Running);
    }

    private static readonly DateTime RawStart = new(2026, 10, 6, 11, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Accumulator_takes_the_real_duration_from_the_session_when_a_query_ends_between_samples()
    {
        var acc = new DbActivityAccumulator(2000, 1000);
        Assert.Equal(750, acc.CandidateMs);
        RequestObservation At(DateTime raw, double ms) => Request(51, 100, ms) with { RawStart = raw };
        SessionObservation Session(DateTime raw, DateTime? end) => new(51, 100, end is null ? "running" : "sleeping", 0, raw, end);

        // 2.4 s query, seen once at 0.9 s; the session says when it really ended.
        acc.Add(new DbSample { TimeUtc = T0, Requests = [At(RawStart, 900)], LocalSessions = [Session(RawStart, null)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), LocalSessions = [Session(RawStart, RawStart.AddMilliseconds(2400))] });

        // 1.6 s query: watched, but not slow.
        var second = RawStart.AddSeconds(5);
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(6), Requests = [At(second, 1000)], LocalSessions = [Session(second, null)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(7), LocalSessions = [Session(second, second.AddMilliseconds(1600))] });

        // The session moved on to another request: only the elapsed time seen is known.
        var third = RawStart.AddSeconds(10);
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(11), Requests = [At(third, 1000)], LocalSessions = [Session(third, null)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(12), LocalSessions = [Session(third.AddSeconds(1.5), null)] });

        var slow = Assert.Single(acc.Drain(_ => default)!.CompletedSlow);
        Assert.Equal(2400, slow.DurationMs);
        Assert.Equal(T0.AddMilliseconds(-900), slow.StartUtc);
        Assert.Equal("CPU", slow.MainWait);
    }

    [Fact]
    public void Accumulator_samples_again_when_a_watched_query_reaches_the_threshold()
    {
        var acc = new DbActivityAccumulator(2000, 1000);
        Assert.Null(acc.NextThresholdCrossingUtc());
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1200)] });
        Assert.Equal(T0.AddMilliseconds(900), acc.NextThresholdCrossingUtc());

        // The extra sample counts for slow queries, not for load.
        acc.Add(new DbSample { TimeUtc = T0.AddMilliseconds(900), Requests = [Request(51, 100, 2100), Request(52, 100, 10)], IsProbe = true });
        Assert.Null(acc.NextThresholdCrossingUtc());
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1) });

        var result = acc.Drain(_ => default)!;
        Assert.Equal(2, result.Samples);
        Assert.Equal(0.5, result.ByPid[100].Load);
        Assert.Equal(2100, Assert.Single(result.CompletedSlow).DurationMs);
    }

    [Fact]
    public void Accumulator_keeps_watching_queries_of_a_server_that_did_not_answer()
    {
        var acc = new DbActivityAccumulator(2000, 1000);
        RequestObservation Remote(double ms) => Request(1_000_051, 100, ms) with { ServerIndex = 1 };

        acc.Add(new DbSample { TimeUtc = T0, Requests = [Remote(2500)], ServersSampled = new HashSet<int> { 0, 1 } });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), ServersSampled = new HashSet<int> { 0 } });
        Assert.Empty(acc.Drain(_ => default)!.CompletedSlow);
        Assert.Null(acc.NextThresholdCrossingUtc());

        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(2), Requests = [Remote(4500)], ServersSampled = new HashSet<int> { 0, 1 } });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(3), ServersSampled = new HashSet<int> { 0, 1 } });
        Assert.Equal(4500, Assert.Single(acc.Drain(_ => default)!.CompletedSlow).DurationMs);
    }

    [Fact]
    public void Accumulator_keeps_one_record_for_a_batch_that_pauses_in_waitfor()
    {
        var acc = new DbActivityAccumulator(1000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), Requests = [Request(51, 100, 2500, "suspended", "WAITFOR", command: "WAITFOR")] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(2), Requests = [Request(51, 100, 3500)] });
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(3) });

        var result = acc.Drain(_ => default)!;
        var slow = Assert.Single(result.CompletedSlow);
        Assert.Equal(3500, slow.DurationMs);
        Assert.Equal("CPU", slow.MainWait);   // the pause is neither load nor a wait worth naming
        Assert.Equal(0.5, result.ByPid[100].Load);
    }

    [Theory]
    [InlineData("proc:Shop.dbo.Outer", "Shop.dbo.Outer", null, "Shop.dbo.Inner")]
    [InlineData("text:SELECT dbo.f(?)", null, "SELECT dbo.f(?)", "Shop.dbo.Inner")]
    [InlineData(null, "Shop.dbo.Inner", null, null)]
    public void Accumulator_names_slow_queries_by_the_call_the_app_made(string? entryPoint, string? objectName, string? batch, string? statementObject)
    {
        var acc = new DbActivityAccumulator(1000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500, textKey: "inner-statement") with { BatchKey = "inner-batch" }] });
        var watched = Assert.Single(acc.PendingEntryPoints());
        Assert.Equal(51, watched.RawSession);
        acc.SetEntryPoint(watched, entryPoint);
        Assert.Empty(acc.PendingEntryPoints());
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1) });

        var slow = Assert.Single(acc.Drain(key => key switch
        {
            "inner-batch" => new SqlText("CREATE PROCEDURE dbo.Inner AS ...", "Shop.dbo.Inner"),
            "inner-statement" => new SqlText("UPDATE t SET a = ?", "Shop.dbo.Inner"),
            _ => default,
        })!.CompletedSlow);
        Assert.Equal(objectName, slow.ObjectName);
        Assert.Equal(batch, slow.Batch);
        Assert.Equal("UPDATE t SET a = ?", slow.Statement);
        Assert.Equal(statementObject, slow.StatementObject);
    }

    [Fact]
    public void Accumulator_records_which_process_blocked_a_query()
    {
        var acc = new DbActivityAccumulator(1000);
        SessionObservation[] sessions = [new(51, 100, "running", 0), new(70, 300, "sleeping", 1)];
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 1500, "suspended", "LCK_M_U", blockedBy: 70)], LocalSessions = sessions });
        var running = Assert.Single(acc.Drain(_ => default)!.Running);
        Assert.Equal(300, running.BlockerPid);

        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(1), LocalSessions = sessions });
        var slow = Assert.Single(acc.Drain(_ => default)!.CompletedSlow);
        Assert.True(slow.WasBlocked);
        Assert.Equal(300, slow.BlockerPid);
        Assert.Equal("LCK_M_U", slow.MainWait);
    }

    [Fact]
    public void Accumulator_averages_load_over_the_last_minute()
    {
        var acc = new DbActivityAccumulator(2000);
        acc.Add(new DbSample { TimeUtc = T0, Requests = [Request(51, 100, 10), Request(52, 100, 10), Request(53, 100, 10), Request(54, 100, 10)] });
        acc.Drain(_ => default);
        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(30) });
        var recent = acc.Drain(_ => default)!.ByPid[100];
        Assert.Equal(0, recent.Load);
        Assert.Equal(2, recent.Load1m);   // 4 then 0

        acc.Add(new DbSample { TimeUtc = T0.AddSeconds(61), Requests = [Request(55, 100, 10)] });
        Assert.Equal(0.5, acc.Drain(_ => default)!.ByPid[100].Load1m);   // the first sample has left the window
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
    public void Composer_keeps_the_pool_of_a_worker_that_exited_and_names_the_blocking_pool()
    {
        var composer = new SnapshotComposer("WEB01", 4);
        CollectionInput Tick(DateTime time, bool shopRunning, DbActivityInput? db = null) => new()
        {
            TimestampUtc = time,
            Topology = new IisTopology { AppPools = [new AppPoolInfo { Name = "Shop" }, new AppPoolInfo { Name = "Api" }] },
            Processes = shopRunning ? [new(100, 4, "w3wp.exe", 10), new(200, 4, "w3wp.exe", 10)] : [new(200, 4, "w3wp.exe", 10)],
            WorkerProcessPools = shopRunning ? new Dictionary<int, string> { [100] = "Shop", [200] = "Api" } : new Dictionary<int, string> { [200] = "Api" },
            SqlServerPids = [400],
            DbActivity = db,
        };

        composer.Compose(Tick(T0, shopRunning: true));
        var snapshot = composer.Compose(Tick(T0.AddSeconds(1), shopRunning: false, new DbActivityInput
        {
            Samples = 1,
            CompletedSlow = [new SlowQuery { Pid = 100, WasBlocked = true, BlockerPid = 200 }],
            Running = [new RunningQuery { Pid = 200, BlockerPid = 999 }],
        }));

        var slow = Assert.Single(snapshot.Database!.CompletedSlow);
        Assert.Equal("Shop", slow.AppPool);
        Assert.Equal("Api", slow.BlockerAppPool);
        Assert.Null(Assert.Single(snapshot.Database.Running).BlockerAppPool);

        // sqlservr.exe couldn't be read: unknown, not 0.
        Assert.Null(snapshot.Server.SqlServerCpuPercent);
        Assert.Null(snapshot.Server.SqlServerMemoryBytes);

        var later = composer.Compose(Tick(T0.AddMinutes(10), shopRunning: false, new DbActivityInput
        {
            Samples = 1,
            CompletedSlow = [new SlowQuery { Pid = 100 }],
        }));
        Assert.Null(Assert.Single(later.Database!.CompletedSlow).AppPool);
    }

    [Fact]
    public void Slow_queries_keep_the_nested_module_the_blocker_and_cpu_as_main_wait()
    {
        var store = new HistoryStore(Path.Combine(_directory, "blockers.db"));
        store.Initialize();
        SlowQuery Run(double ms, string? inner, string? blocker, string? wait) => new()
        {
            StartUtc = T0, EndUtc = T0.AddMilliseconds(ms), DurationMs = ms, AppPool = "Shop", Database = "Shop",
            Statement = "UPDATE t SET a = ?", ObjectName = "Shop.dbo.Outer", StatementObject = inner, Pid = 1,
            WasBlocked = blocker is not null, BlockerAppPool = blocker == "-" ? null : blocker, MainWait = wait,
        };

        store.WriteSlowQueries(
        [
            Run(3000, "Shop.dbo.Inner", "Api", "LCK_M_U"),
            Run(9000, "Shop.dbo.Other", "Api", "CPU"),
            Run(2500, null, "-", "CPU"),
            Run(2200, null, null, null),
        ]);

        var group = Assert.Single(store.QuerySlowQueries(new SlowQueryRequest { FromUtc = T0.AddMinutes(-1), ToUtc = T0.AddMinutes(1) }).Groups);
        Assert.Equal("Shop.dbo.Other", group.StatementObject);   // the longest run's
        Assert.Equal("Api", group.BlockerAppPool);
        Assert.Equal(3, group.BlockedCount);
        Assert.Equal("CPU", group.MainWait);   // NULL from older rows counts as CPU too
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
    public void Slow_queries_group_by_procedure_or_batch_and_show_the_longest_runs_statement()
    {
        var store = new HistoryStore(Path.Combine(_directory, "grouping.db"));
        store.Initialize();
        SlowQuery Run(double ms, string statement, string? batch = null, string? procedure = null) => new()
        {
            StartUtc = T0, EndUtc = T0.AddMilliseconds(ms), DurationMs = ms, AppPool = "Shop", Database = "Shop",
            Statement = statement, Batch = batch, ObjectName = procedure, Pid = 1,
        };

        store.WriteSlowQueries(
        [
            Run(3000, "SET @i += ?", batch: "DECLARE ...; WHILE ..."),
            Run(5000, "WHILE ...", batch: "DECLARE ...; WHILE ..."),
            Run(2500, "UPDATE a", procedure: "Shop.dbo.Save"),
            Run(4500, "SELECT b", procedure: "Shop.dbo.Save"),
        ]);

        var groups = store.QuerySlowQueries(new SlowQueryRequest { FromUtc = T0.AddMinutes(-1), ToUtc = T0.AddMinutes(1) }).Groups;

        Assert.Equal(2, groups.Count);
        var batch = groups.Single(g => g.Batch is not null);
        Assert.Equal(2, batch.Count);
        Assert.Equal("WHILE ...", batch.Statement);
        var procedure = groups.Single(g => g.ObjectName == "Shop.dbo.Save");
        Assert.Equal(2, procedure.Count);
        Assert.Equal("SELECT b", procedure.Statement);
        Assert.Null(procedure.Batch);
    }

    [Fact]
    public void Slow_query_table_from_the_first_version_is_upgraded()
    {
        var path = Path.Combine(_directory, "old.db");
        Directory.CreateDirectory(_directory);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE slow_queries (id INTEGER PRIMARY KEY AUTOINCREMENT, start_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL,
                    app_pool TEXT NOT NULL, pid INTEGER NOT NULL, database_name TEXT NOT NULL, login_name TEXT NOT NULL,
                    program_name TEXT NOT NULL, query_hash TEXT, object_name TEXT, statement TEXT NOT NULL, duration_ms REAL NOT NULL,
                    cpu_ms REAL NOT NULL, logical_reads INTEGER NOT NULL, writes INTEGER NOT NULL, main_wait TEXT, was_blocked INTEGER NOT NULL);
                INSERT INTO slow_queries (start_ts, end_ts, app_pool, pid, database_name, login_name, program_name, statement,
                    duration_ms, cpu_ms, logical_reads, writes, was_blocked)
                VALUES (1000, 4000, 'Shop', 1, 'Shop', 'web', 'app', 'SELECT ?', 3000, 10, 5, 0, 0);
                """;
            create.ExecuteNonQuery();
        }

        var store = new HistoryStore(path);
        store.Initialize();
        store.WriteSlowQueries([new SlowQuery { StartUtc = T0, EndUtc = T0.AddSeconds(3), DurationMs = 3000, AppPool = "Shop", Database = "Shop", Statement = "SELECT ?", Batch = "SELECT ?" }]);

        var report = store.QuerySlowQueries(new SlowQueryRequest { FromUtc = DateTimeOffset.FromUnixTimeMilliseconds(0).UtcDateTime, ToUtc = T0.AddHours(1) });
        Assert.Equal(2, Assert.Single(report.Groups).Count);
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
