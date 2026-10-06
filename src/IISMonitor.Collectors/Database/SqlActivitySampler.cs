using System.Collections.Concurrent;
using System.Data;
using IISMonitor.Core.Collection;
using Microsoft.Data.SqlClient;

namespace IISMonitor.Collectors.Database;

/// <summary>
/// Asks each configured SQL Server, once per interval, which user queries are running and which
/// sessions this machine's processes hold, and feeds the results to a
/// <see cref="DbActivityAccumulator"/>. Only needs VIEW SERVER STATE; nothing is installed on the
/// SQL Server. Runs in the background; the engine drains the accumulator on every tick.
/// </summary>
internal sealed class SqlActivitySampler : IAsyncDisposable
{
    private const int MaxTextLookupsPerSample = 25;
    private const int TextCacheLimit = 5000;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    // start_time is in the SQL Server's local time; convert it to UTC on the server.
    private const string SampleSql = """
        SELECT r.session_id, r.request_id,
               DATEADD(minute, DATEDIFF(minute, SYSDATETIME(), SYSUTCDATETIME()), r.start_time) AS start_utc,
               r.status, r.command, r.total_elapsed_time, r.cpu_time, r.logical_reads, r.writes,
               r.wait_type, r.blocking_session_id, DB_NAME(r.database_id) AS database_name,
               CONVERT(varchar(18), r.query_hash, 1) AS query_hash,
               r.sql_handle, r.statement_start_offset, r.statement_end_offset,
               s.host_name, s.host_process_id, s.program_name, s.login_name
        FROM sys.dm_exec_requests AS r
        JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
        WHERE s.is_user_process = 1 AND r.session_id <> @@SPID;

        SELECT s.session_id, s.host_process_id, s.status, s.open_transaction_count
        FROM sys.dm_exec_sessions AS s
        WHERE s.is_user_process = 1 AND s.host_name = @host AND s.session_id <> @@SPID AND s.host_process_id IS NOT NULL;
        """;

    private readonly IReadOnlyList<string> _connectionStrings;
    private readonly TimeSpan _interval;
    private readonly ServerState[] _servers;
    private readonly ConcurrentDictionary<string, (string? Statement, string? ObjectName)> _texts = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile string _status = "Waiting for the first sample.";
    private volatile bool _ok = true;

    public SqlActivitySampler(IReadOnlyList<string> connectionStrings, TimeSpan interval, double slowThresholdMs)
    {
        _connectionStrings = connectionStrings;
        _interval = interval;
        _servers = connectionStrings.Select(_ => new ServerState()).ToArray();
        Accumulator = new DbActivityAccumulator(slowThresholdMs);
        _loop = Task.Run(LoopAsync);
    }

    public DbActivityAccumulator Accumulator { get; }

    public (bool Ok, string Message) Status => (_ok, _status);

    public DbActivityInput? Drain() => Accumulator.Drain(Describe);

    private (string? Statement, string? ObjectName) Describe(string? textKey) =>
        textKey is not null && _texts.TryGetValue(textKey, out var text) ? text : (null, null);

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
        var now = DateTime.UtcNow;
        var requests = new List<RequestObservation>();
        var sessions = new List<SessionObservation>();
        var problems = new List<string>();
        var sampled = 0;

        for (var index = 0; index < _connectionStrings.Count; index++)
        {
            var server = _servers[index];
            if (now < server.RetryAtUtc)
            {
                problems.Add(server.LastError ?? "retrying");
                continue;
            }

            try
            {
                var connection = await server.GetConnectionAsync(_connectionStrings[index], token).ConfigureAwait(false);
                await ReadSampleAsync(connection, index, requests, sessions, token).ConfigureAwait(false);
                sampled++;
                if (server.Warning is { } warning)
                    problems.Add(warning);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException or IOException or TimeoutException)
            {
                server.Fail($"{server.Name}: {e.Message}", now + RetryAfterFailure);
                problems.Add(server.LastError!);
            }
        }

        if (sampled > 0)
        {
            Accumulator.Add(new DbSample { TimeUtc = now, Requests = requests, LocalSessions = sessions });
            await LookUpTextsAsync(token).ConfigureAwait(false);
        }

        _ok = problems.Count == 0;
        _status = problems.Count == 0
            ? $"Sampling {_connectionStrings.Count} SQL Server(s) every {_interval.TotalSeconds:0.#} s; queries over {Accumulator.SlowThresholdMs / 1000:0.#} s are recorded as slow."
            : string.Join(" ", problems.Distinct());
    }

    private static async Task ReadSampleAsync(
        SqlConnection connection, int serverIndex, List<RequestObservation> requests, List<SessionObservation> sessions, CancellationToken token)
    {
        await using var command = new SqlCommand(SampleSql, connection) { CommandTimeout = 5 };
        command.Parameters.Add("@host", SqlDbType.NVarChar, 128).Value = Environment.MachineName;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        // Session IDs are only unique per server; keep them apart when several servers are watched.
        var offset = serverIndex * 1_000_000;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var hostName = reader.IsDBNull(16) ? "" : reader.GetString(16);
            var blockedBy = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10));
            var handle = reader.IsDBNull(13) ? null : (byte[])reader.GetValue(13);
            var start = reader.IsDBNull(14) ? 0 : Convert.ToInt32(reader.GetValue(14));
            var end = reader.IsDBNull(15) ? -1 : Convert.ToInt32(reader.GetValue(15));
            requests.Add(new RequestObservation(
                SessionId: offset + Convert.ToInt32(reader.GetValue(0)),
                RequestId: Convert.ToInt32(reader.GetValue(1)),
                StartUtc: DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                Status: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Command: reader.IsDBNull(4) ? "" : reader.GetString(4),
                ElapsedMs: Convert.ToDouble(reader.GetValue(5)),
                CpuMs: Convert.ToDouble(reader.GetValue(6)),
                LogicalReads: Convert.ToInt64(reader.GetValue(7)),
                Writes: Convert.ToInt64(reader.GetValue(8)),
                WaitType: reader.IsDBNull(9) ? null : reader.GetString(9),
                BlockedBy: blockedBy > 0 ? offset + blockedBy : 0,
                Database: reader.IsDBNull(11) ? "" : reader.GetString(11),
                QueryHash: reader.IsDBNull(12) ? null : reader.GetString(12),
                TextKey: handle is null ? null : TextKey(serverIndex, handle, start, end),
                IsLocal: string.Equals(hostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
                Pid: reader.IsDBNull(17) ? 0 : Convert.ToInt32(reader.GetValue(17)),
                Program: reader.IsDBNull(18) ? "" : reader.GetString(18),
                Login: reader.IsDBNull(19) ? "" : reader.GetString(19)));
        }

        await reader.NextResultAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            sessions.Add(new SessionObservation(
                SessionId: offset + Convert.ToInt32(reader.GetValue(0)),
                Pid: Convert.ToInt32(reader.GetValue(1)),
                Status: reader.IsDBNull(2) ? "" : reader.GetString(2),
                OpenTransactions: reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3))));
        }
    }

    /// <summary>Fetches statement text (literals removed) for slow and listed queries not seen before.</summary>
    private async Task LookUpTextsAsync(CancellationToken token)
    {
        var missing = Accumulator.PendingTextKeys().Where(k => !_texts.ContainsKey(k)).Take(MaxTextLookupsPerSample).ToList();
        if (missing.Count == 0)
            return;
        if (_texts.Count > TextCacheLimit)
            _texts.Clear();

        foreach (var group in missing.Select(ParseTextKey).Where(k => k is not null).Select(k => k!.Value).GroupBy(k => k.Server))
        {
            var server = _servers[group.Key];
            try
            {
                var connection = await server.GetConnectionAsync(_connectionStrings[group.Key], token).ConfigureAwait(false);
                var keys = group.ToList();
                var values = string.Join(", ", keys.Select((_, i) => $"({i}, @h{i}, @s{i}, @e{i})"));
                await using var command = new SqlCommand($"""
                    SELECT v.k,
                           SUBSTRING(t.text, (v.s / 2) + 1,
                                     ((CASE v.e WHEN -1 THEN DATALENGTH(t.text) ELSE v.e END) - v.s) / 2 + 1) AS statement_text,
                           CASE WHEN t.objectid IS NULL THEN NULL
                                ELSE CONCAT(DB_NAME(t.dbid), '.', OBJECT_SCHEMA_NAME(t.objectid, t.dbid), '.', OBJECT_NAME(t.objectid, t.dbid)) END
                    FROM (VALUES {values}) AS v(k, h, s, e)
                    CROSS APPLY sys.dm_exec_sql_text(v.h) AS t;
                    """, connection) { CommandTimeout = 5 };
                for (var i = 0; i < keys.Count; i++)
                {
                    command.Parameters.Add($"@h{i}", SqlDbType.VarBinary, 64).Value = keys[i].Handle;
                    command.Parameters.Add($"@s{i}", SqlDbType.Int).Value = keys[i].Start;
                    command.Parameters.Add($"@e{i}", SqlDbType.Int).Value = keys[i].End;
                }

                var found = new HashSet<int>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var i = reader.GetInt32(0);
                    found.Add(i);
                    var statement = reader.IsDBNull(1) ? null : SqlTextNormalizer.Normalize(reader.GetString(1));
                    var objectName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    _texts[keys[i].Key] = (string.IsNullOrEmpty(statement) ? null : statement, string.IsNullOrEmpty(objectName) ? null : objectName);
                }

                // Plans already evicted from the cache have no text; don't ask again.
                for (var i = 0; i < keys.Count; i++)
                {
                    if (!found.Contains(i))
                        _texts.TryAdd(keys[i].Key, (null, null));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or IOException or TimeoutException)
            {
                // Text is a nice-to-have; the next sample tries again.
            }
        }
    }

    internal static string TextKey(int server, byte[] handle, int start, int end) =>
        $"{server}:{Convert.ToHexString(handle)}:{start}:{end}";

    private static (string Key, int Server, byte[] Handle, int Start, int End)? ParseTextKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var server) || !int.TryParse(parts[2], out var start) || !int.TryParse(parts[3], out var end))
            return null;
        try
        {
            return (key, server, Convert.FromHexString(parts[1]), start, end);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var server in _servers)
            await server.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>One long-lived connection per SQL Server, reopened after a failure.</summary>
    private sealed class ServerState : IAsyncDisposable
    {
        private SqlConnection? _connection;

        public string Name { get; private set; } = "SQL Server";
        public string? LastError { get; private set; }
        public string? Warning { get; private set; }
        public DateTime RetryAtUtc { get; private set; }

        public async Task<SqlConnection> GetConnectionAsync(string connectionString, CancellationToken token)
        {
            if (_connection is { State: ConnectionState.Open })
                return _connection;

            await DisposeAsync().ConfigureAwait(false);
            var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = "IISMonitor", ConnectTimeout = 5 };
            Name = builder.DataSource;
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);

            await using (var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE');", connection) { CommandTimeout = 5 })
            {
                var granted = await permission.ExecuteScalarAsync(token).ConfigureAwait(false);
                Warning = granted is int value && value == 0
                    ? $"{Name}: the login lacks VIEW SERVER STATE, so only its own queries are visible. Grant it to see the app pools' queries."
                    : null;
            }

            LastError = null;
            _connection = connection;
            return connection;
        }

        public void Fail(string error, DateTime retryAtUtc)
        {
            LastError = error;
            RetryAtUtc = retryAtUtc;
            _connection?.Dispose();
            _connection = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
