using IISMonitor.Collectors.Database;
using IISMonitor.Core.Collection;
using IISMonitor.Core.Models;
using Microsoft.Data.SqlClient;

namespace IISMonitor.Collectors.Tests;

/// <summary>Runs only when IISMONITOR_TEST_SQL holds a connection string to a test SQL Server (sysadmin).</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public const string Variable = "IISMONITOR_TEST_SQL";

    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a SQL Server connection string to run this test.";
    }
}

/// <summary>A theory that runs only when IISMONITOR_TEST_SQL is set.</summary>
public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlFactAttribute.Variable)))
            Skip = $"Set {SqlFactAttribute.Variable} to a SQL Server connection string to run this test.";
    }
}

public class SqlActivitySamplerTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlFactAttribute.Variable)!;

    [Theory]
    [InlineData("RPC Event", "AppDb.dbo.GetOrders;1\0", "proc:AppDb.dbo.GetOrders")]
    [InlineData("RPC Event", "[App Db].[dbo].[Get Orders];1", "proc:App Db.dbo.Get Orders")]
    [InlineData("RPC Event", "sp_executesql;1", null)]
    [InlineData("RPC Event", "sys.sp_prepexec;1", null)]
    [InlineData("RPC Event", "AppDb.dbo.sp_LegacyReport;1", "proc:AppDb.dbo.sp_LegacyReport")]
    [InlineData("Language Event", "SELECT * FROM t WHERE email = 'a@b.c'\0", "text:SELECT * FROM t WHERE email = ?")]
    [InlineData("Language Event", "  ", null)]
    [InlineData(null, null, null)]
    public void Reads_the_entry_point_from_the_input_buffer(string? eventType, string? eventInfo, string? expected) =>
        Assert.Equal(expected, SqlActivitySampler.EntryPoint(eventType, eventInfo));

    [Theory]
    [InlineData("Shop", 5, "dbo", "Save", null, "Shop.dbo.Save")]
    [InlineData("Shop", 5, null, null, "CREATE   PROCEDURE [dbo].[Save Order] @x int AS SELECT 1", "Shop.dbo.Save Order")]
    [InlineData("Shop", 5, null, null, "/* header */ create or alter function dbo.f(@x int) returns int", "Shop.dbo.f")]
    [InlineData("Shop", 5, null, null, "CREATE TRIGGER trg_Audit ON dbo.Orders AFTER INSERT", "Shop.trg_Audit")]
    [InlineData("Shop", 5, null, null, "not a module", "Shop.#5")]
    [InlineData("Shop", null, null, null, "SELECT 1", null)]
    public void Names_modules_even_without_metadata_access(string? database, int? objectId, string? schema, string? name, string? header, string? expected) =>
        Assert.Equal(expected, SqlActivitySampler.ObjectName(database, objectId, schema, name, header));

    [SqlFact]
    public async Task Sees_load_blocking_idle_transactions_and_slow_queries_of_this_process()
    {
        var table = "iismon_test_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync($"CREATE TABLE dbo.{table} (id int PRIMARY KEY, name nvarchar(50)); INSERT dbo.{table} VALUES (1, N'alice@example.com');");
        try
        {
            await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 1500);
            var collected = new List<DbActivityInput>();

            // Holder: takes a lock and sits idle with the transaction open.
            await using var holder = new SqlConnection(ConnectionString);
            await holder.OpenAsync();
            await using (var tx = new SqlCommand($"BEGIN TRAN; UPDATE dbo.{table} SET name = N'bob@example.com' WHERE id = 1;", holder))
                await tx.ExecuteNonQueryAsync();

            // Waiter: blocked by the holder's lock.
            await using var waiter = new SqlConnection(ConnectionString);
            await waiter.OpenAsync();
            var blocked = new SqlCommand($"SELECT name FROM dbo.{table} WHERE id = 1 AND name <> N'carol@example.com';", waiter) { CommandTimeout = 30 }
                .ExecuteScalarAsync();

            // Busy: a 3-second CPU-bound query, slow by the 1.5 s threshold.
            await using var busy = new SqlConnection(ConnectionString);
            await busy.OpenAsync();
            var heavy = new SqlCommand("""
                DECLARE @i bigint = 0, @x bigint = 42; DECLARE @end datetime2 = DATEADD(second, 3, SYSDATETIME());
                WHILE SYSDATETIME() < @end BEGIN SET @i += 1; SET @x = (@x + @i) % 1000003; END
                SELECT @x WHERE N'secret-literal' <> N'x';
                """, busy) { CommandTimeout = 30 }.ExecuteScalarAsync();

            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(500);
                if (sampler.Drain() is { } input)
                    collected.Add(input);
            }

            await using (var commit = new SqlCommand("COMMIT;", holder))
                await commit.ExecuteNonQueryAsync();
            await blocked;
            await heavy;

            // Let the sampler notice the finished queries.
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(500);
                if (sampler.Drain() is { } input)
                    collected.Add(input);
            }

            var status = sampler.Status;
            Assert.True(status.Ok, status.Message);
            var mine = collected.Select(c => c.ByPid.GetValueOrDefault(Environment.ProcessId)).ToList();
            Assert.Contains(mine, a => a.Load >= 1);
            Assert.Contains(mine, a => a.CpuLoad > 0);
            Assert.Contains(mine, a => a.Blocked >= 1);
            Assert.Contains(mine, a => a.Blocking >= 1);
            Assert.Contains(mine, a => a.IdleInTransaction >= 1);
            Assert.Contains(mine, a => a.Sessions >= 3);
            Assert.Contains(mine, a => a.SlowRunning >= 1);

            var running = collected.SelectMany(c => c.Running).Where(r => r.Pid == Environment.ProcessId).ToList();
            Assert.Contains(running, r => r.BlockedBy is not null && r.WaitType is { } w && w.StartsWith("LCK_", StringComparison.Ordinal));
            // The live list shows whichever statement of the loop batch is executing at that second.
            Assert.Contains(running, r => r.Statement is { } s
                                          && (s.Contains("WHILE", StringComparison.Ordinal) || s.Contains("SET @", StringComparison.Ordinal)));

            var slow = collected.SelectMany(c => c.CompletedSlow).Where(q => q.Pid == Environment.ProcessId).ToList();

            // The loop batch is recorded once, as its whole batch, whichever statement each sample caught.
            var heavyQuery = Assert.Single(slow, q => q.Batch is { } b && b.Contains("WHILE", StringComparison.Ordinal));
            Assert.InRange(heavyQuery.DurationMs, 1500, 4000);
            Assert.True(heavyQuery.CpuMs > 500);
            Assert.Null(heavyQuery.ObjectName);
            Assert.False(string.IsNullOrEmpty(heavyQuery.Statement));
            Assert.Contains(heavyQuery.Statement!.TrimEnd(';'), heavyQuery.Batch!);
            Assert.DoesNotContain("secret-literal", heavyQuery.Batch);
            Assert.Contains(slow, q => q.WasBlocked && q.MainWait is { } w && w.StartsWith("LCK_", StringComparison.Ordinal)
                                       && q.Batch is { } b && b.Contains("SELECT", StringComparison.Ordinal)
                                       && !b.Contains("carol", StringComparison.Ordinal));
            Assert.Equal(2, slow.Count);
            Assert.All(slow, q => Assert.False(string.IsNullOrEmpty(q.Database)));
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS dbo.{table};");
        }
    }

    [SqlFact]
    public async Task Names_the_stored_procedure_a_slow_query_ran_in()
    {
        var procedure = "iismon_slow_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync($"""
            CREATE PROCEDURE dbo.{procedure} AS
            BEGIN
                DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, 2500, SYSDATETIME());
                WHILE SYSDATETIME() < @end SET @i += 1;
                SELECT @i;
            END
            """);
        try
        {
            await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 1000);
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            var run = new SqlCommand($"dbo.{procedure}", connection) { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 30 }
                .ExecuteScalarAsync();
            var collected = new List<DbActivityInput>();
            while (!run.IsCompleted)
            {
                await Task.Delay(500);
                if (sampler.Drain() is { } input)
                    collected.Add(input);
            }

            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(500);
                if (sampler.Drain() is { } input)
                    collected.Add(input);
            }

            var slow = Assert.Single(collected.SelectMany(c => c.CompletedSlow), q => q.Pid == Environment.ProcessId);
            Assert.Equal($"tempdb.dbo.{procedure}", slow.ObjectName);
            Assert.Null(slow.Batch);
            Assert.False(string.IsNullOrEmpty(slow.Statement));
        }
        finally
        {
            await ExecuteAsync($"DROP PROCEDURE IF EXISTS dbo.{procedure};");
        }
    }

    [SqlFact]
    public async Task Ignores_idle_waits_like_waitfor()
    {
        await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 1000);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var wait = new SqlCommand("WAITFOR DELAY '00:00:03';", connection) { CommandTimeout = 30 }.ExecuteNonQueryAsync();
        var collected = new List<DbActivityInput>();
        while (!wait.IsCompleted)
        {
            await Task.Delay(500);
            if (sampler.Drain() is { } input)
                collected.Add(input);
        }

        await Task.Delay(1500);
        if (sampler.Drain() is { } last)
            collected.Add(last);

        Assert.All(collected, c => Assert.Equal(0, c.ByPid.GetValueOrDefault(Environment.ProcessId).Load));
        Assert.DoesNotContain(collected.SelectMany(c => c.CompletedSlow), q => q.Pid == Environment.ProcessId);
    }

    [SqlFact]
    public async Task Records_every_query_just_over_the_threshold_with_its_real_duration()
    {
        await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 2000);
        var collected = new List<DbActivityInput>();

        // One session throughout, like a pooled connection: four queries a little over 2 s and two
        // under it, with short random gaps, so a query often ends between two regular samples.
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var random = new Random(7);
        foreach (var ms in new[] { 2300, 1600, 2400, 2250, 1700, 2500 })
        {
            await using var command = new SqlCommand($"""
                DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, {ms}, SYSDATETIME());
                WHILE SYSDATETIME() < @end SET @i += 1;
                """, connection) { CommandTimeout = 30 };
            await command.ExecuteNonQueryAsync();
            await Task.Delay(random.Next(30, 300));
            if (sampler.Drain() is { } input)
                collected.Add(input);
        }

        await Task.Delay(1500);
        collected.Add(sampler.Drain()!);

        var slow = collected.SelectMany(c => c.CompletedSlow).Where(q => q.Pid == Environment.ProcessId).ToList();
        Assert.Equal(4, slow.Count);
        Assert.All(slow, q => Assert.InRange(q.DurationMs, 2000, 2800));
        Assert.All(slow, q => Assert.Equal("CPU", q.MainWait));
    }

    [SqlFact]
    public async Task Names_slow_queries_by_what_the_app_called_and_where_the_time_went()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (inner, outer, function) = ($"iismon_inner_{suffix}", $"iismon_outer_{suffix}", $"iismon_fn_{suffix}");
        await CreateNestedModulesAsync(inner, outer, function);
        try
        {
            await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 1000);
            var slow = await RunAndCollectSlowAsync(sampler, ConnectionString, outer, function);

            // The procedure the app called, though most samples were inside the nested one.
            var procedure = Assert.Single(slow, q => q.ObjectName is not null);
            Assert.Equal($"tempdb.dbo.{outer}", procedure.ObjectName);
            Assert.Equal($"tempdb.dbo.{inner}", procedure.StatementObject);
            Assert.Null(procedure.Batch);

            // The query the app sent, though it spent its time in a scalar function.
            var query = Assert.Single(slow, q => q.ObjectName is null);
            Assert.Equal($"SELECT dbo.{function}(?) AS v", query.Batch);
            Assert.Equal($"tempdb.dbo.{function}", query.StatementObject);
            Assert.True(sampler.Status.Ok, sampler.Status.Message);
        }
        finally
        {
            await ExecuteAsync($"DROP PROCEDURE IF EXISTS dbo.{outer}; DROP PROCEDURE IF EXISTS dbo.{inner}; DROP FUNCTION IF EXISTS dbo.{function};");
        }
    }

    [SqlTheory]
    [InlineData("VIEW SERVER STATE")]
    [InlineData("VIEW SERVER PERFORMANCE STATE")]
    public async Task Works_with_only_the_documented_permission(string permission)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (inner, outer, function) = ($"iismon_inner_{suffix}", $"iismon_outer_{suffix}", $"iismon_fn_{suffix}");
        var login = $"iismon_login_{suffix}";
        const string password = "Only-View_State-1";
        if (permission == "VIEW SERVER PERFORMANCE STATE")
        {
            await using var check = new SqlConnection(ConnectionString);
            await check.OpenAsync();
            var major = Convert.ToInt32(await new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", check).ExecuteScalarAsync());
            if (major < 16)
                return;   // SQL Server 2022 introduced it.
        }

        await ExecuteAsync($"USE master; CREATE LOGIN {login} WITH PASSWORD = '{password}', CHECK_POLICY = OFF; GRANT {permission} TO {login};");
        await CreateNestedModulesAsync(inner, outer, function);
        try
        {
            var limited = new SqlConnectionStringBuilder(ConnectionString) { UserID = login, Password = password }.ConnectionString;
            await using (var sampler = new SqlActivitySampler([limited], TimeSpan.FromSeconds(1), slowThresholdMs: 1000))
            {
                var running = new List<RunningQuery>();
                var slow = await RunAndCollectSlowAsync(sampler, ConnectionString, outer, function, running);

                Assert.True(sampler.Status.Ok, sampler.Status.Message);
                var procedure = Assert.Single(slow, q => q.ObjectName is not null);
                Assert.Equal($"tempdb.dbo.{outer}", procedure.ObjectName);

                // Without metadata access, module names come from their CREATE header.
                Assert.Equal($"tempdb.dbo.{inner}", procedure.StatementObject);
                Assert.Equal($"tempdb.dbo.{function}", Assert.Single(slow, q => q.ObjectName is null).StatementObject);
                Assert.Contains(running, r => r.ObjectName == $"tempdb.dbo.{inner}");
                Assert.DoesNotContain(running, r => r.ObjectName is { } name && name.EndsWith('.'));
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP PROCEDURE IF EXISTS dbo.{outer}; DROP PROCEDURE IF EXISTS dbo.{inner}; DROP FUNCTION IF EXISTS dbo.{function};");
            await ExecuteAsync($"USE master; IF SUSER_ID('{login}') IS NOT NULL DROP LOGIN {login};");
        }
    }

    [SqlFact]
    public async Task Records_a_batch_that_pauses_in_waitfor_once()
    {
        await using var sampler = new SqlActivitySampler([ConnectionString], TimeSpan.FromSeconds(1), slowThresholdMs: 1000);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var run = new SqlCommand("""
            DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, 1500, SYSDATETIME());
            WHILE SYSDATETIME() < @end SET @i += 1;
            WAITFOR DELAY '00:00:01.500';
            SET @end = DATEADD(millisecond, 800, SYSDATETIME());
            WHILE SYSDATETIME() < @end SET @i += 1;
            """, connection) { CommandTimeout = 30 }.ExecuteNonQueryAsync();
        var collected = new List<DbActivityInput>();
        while (!run.IsCompleted)
        {
            await Task.Delay(500);
            if (sampler.Drain() is { } input)
                collected.Add(input);
        }

        await run;
        await Task.Delay(1500);
        collected.Add(sampler.Drain()!);

        var slow = Assert.Single(collected.SelectMany(c => c.CompletedSlow), q => q.Pid == Environment.ProcessId);
        Assert.InRange(slow.DurationMs, 3500, 5000);
    }

    /// <summary>
    /// dbo.outer runs briefly, then calls dbo.inner, which loops for 2 s. dbo.function is a scalar
    /// function that loops for 1.5 s.
    /// </summary>
    private static async Task CreateNestedModulesAsync(string inner, string outer, string function)
    {
        await ExecuteAsync($"""
            CREATE PROCEDURE dbo.{inner} AS
            BEGIN
                DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, 2000, SYSDATETIME());
                WHILE SYSDATETIME() < @end SET @i += 1;
            END
            """);
        await ExecuteAsync($"""
            CREATE PROCEDURE dbo.{outer} @customer int AS
            BEGIN
                DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, 200, SYSDATETIME());
                WHILE SYSDATETIME() < @end SET @i += 1;
                EXEC dbo.{inner};
            END
            """);
        await ExecuteAsync($"""
            CREATE FUNCTION dbo.{function} (@x int) RETURNS bigint AS
            BEGIN
                DECLARE @i bigint = 0; DECLARE @end datetime2 = DATEADD(millisecond, 1500, SYSDATETIME());
                WHILE SYSDATETIME() < @end SET @i += 1;
                RETURN @i + @x;
            END
            """);
    }

    /// <summary>Calls dbo.outer as a procedure, then runs SELECT dbo.function(...) as a batch, and returns this process's slow queries.</summary>
    private static async Task<List<SlowQuery>> RunAndCollectSlowAsync(
        SqlActivitySampler sampler, string appConnectionString, string outer, string function, List<RunningQuery>? running = null)
    {
        var collected = new List<DbActivityInput>();
        await using var connection = new SqlConnection(appConnectionString);
        await connection.OpenAsync();
        var calls = new Func<Task>[]
        {
            () => new SqlCommand($"dbo.{outer}", connection)
            {
                CommandType = System.Data.CommandType.StoredProcedure,
                CommandTimeout = 30,
                Parameters = { new SqlParameter("@customer", 42) },
            }.ExecuteNonQueryAsync(),
            () => new SqlCommand($"SELECT dbo.{function}(7) AS v", connection) { CommandTimeout = 30 }.ExecuteScalarAsync(),
        };
        foreach (var call in calls)
        {
            var task = call();
            while (!task.IsCompleted)
            {
                await Task.Delay(300);
                if (sampler.Drain() is { } input)
                    collected.Add(input);
            }

            await task;
        }

        await Task.Delay(1500);
        collected.Add(sampler.Drain()!);
        running?.AddRange(collected.SelectMany(c => c.Running).Where(r => r.Pid == Environment.ProcessId));
        return collected.SelectMany(c => c.CompletedSlow).Where(q => q.Pid == Environment.ProcessId).ToList();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
