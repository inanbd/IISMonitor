using IISMonitor.Core.Collection;
using Microsoft.Data.SqlClient;

namespace IISMonitor.Collectors.Database;

/// <summary>
/// Periodically asks each configured SQL Server how many sessions this machine's processes hold
/// (sys.dm_exec_sessions.host_process_id is the client PID). Runs in the background; the engine
/// reads the latest result.
/// </summary>
internal sealed class SqlSessionSampler : IAsyncDisposable
{
    private const string Query = """
        SELECT s.host_process_id,
               COUNT(*) AS sessions,
               SUM(CASE WHEN r.session_id IS NULL THEN 0 ELSE 1 END) AS active
        FROM sys.dm_exec_sessions AS s
        LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
        WHERE s.is_user_process = 1
          AND s.host_name = @host
          AND s.session_id <> @@SPID
          AND s.host_process_id IS NOT NULL
        GROUP BY s.host_process_id;
        """;

    private readonly IReadOnlyList<string> _connectionStrings;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile Dictionary<int, DbSessionCount>? _latest;
    private volatile string _status = "Waiting for the first query.";
    private volatile bool _ok = true;

    public SqlSessionSampler(IReadOnlyList<string> connectionStrings, TimeSpan interval)
    {
        _connectionStrings = connectionStrings;
        _interval = interval;
        _loop = Task.Run(LoopAsync);
    }

    public Dictionary<int, DbSessionCount>? Latest => _latest;

    public (bool Ok, string Message) Status => (_ok, _status);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await SampleAsync(_cts.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        var combined = new Dictionary<int, DbSessionCount>();
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var connectionString in _connectionStrings)
        {
            var server = "SQL Server";
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = "IISMonitor",
                    ConnectTimeout = 5,
                };
                server = builder.DataSource;

                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);

                await using (var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE');", connection))
                {
                    permission.CommandTimeout = 5;
                    if (await permission.ExecuteScalarAsync(token).ConfigureAwait(false) is int granted && granted == 0)
                        warnings.Add($"{server}: the login lacks VIEW SERVER STATE, so only its own sessions are visible.");
                }

                await using var command = new SqlCommand(Query, connection) { CommandTimeout = 10 };
                command.Parameters.AddWithValue("@host", Environment.MachineName);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var pid = Convert.ToInt32(reader.GetValue(0));
                    var sessions = Convert.ToInt32(reader.GetValue(1));
                    var active = Convert.ToInt32(reader.GetValue(2));
                    var existing = combined.GetValueOrDefault(pid);
                    combined[pid] = new DbSessionCount(existing.Sessions + sessions, existing.Active + active);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException or TimeoutException)
            {
                errors.Add($"{server}: {e.Message}");
            }
        }

        _latest = errors.Count == _connectionStrings.Count ? null : combined;
        _ok = errors.Count == 0;
        _status = errors.Count == 0 && warnings.Count == 0
            ? $"Querying {_connectionStrings.Count} SQL Server(s) every {_interval.TotalSeconds:0} s."
            : string.Join(" ", errors.Concat(warnings));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }
}
