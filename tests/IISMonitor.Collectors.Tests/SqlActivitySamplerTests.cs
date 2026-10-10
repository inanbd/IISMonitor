using IISMonitor.Collectors.Database;
using IISMonitor.Core.Collection;
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

public class SqlActivitySamplerTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlFactAttribute.Variable)!;

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

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
