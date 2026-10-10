using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using IISMonitor.Core.Collection;
using Microsoft.Data.SqlClient;

namespace IISMonitor.Collectors.Database;

/// <summary>
/// Asks each configured SQL Server, once per interval, which user queries are running and which
/// sessions this machine's processes hold, and feeds the results to a
/// <see cref="DbActivityAccumulator"/>. Only needs VIEW SERVER STATE (or VIEW SERVER PERFORMANCE
/// STATE on SQL Server 2022+); nothing is installed on the SQL Server. Runs in the background; the
/// engine drains the accumulator on every tick.
/// </summary>
internal sealed partial class SqlActivitySampler : IAsyncDisposable
{
    private const int MaxTextLookupsPerSample = 25;
    private const int MaxEntryPointLookupsPerSample = 25;
    private const int TextCacheLimit = 5000;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EntryPointRetryAfterFailure = TimeSpan.FromMinutes(10);

    /// <summary>Extra samples (taken when a watched query reaches the slow threshold) are at least this far apart.</summary>
    private static readonly TimeSpan MinProbeGap = TimeSpan.FromMilliseconds(200);

    // start_time is in the SQL Server's local time: kept as is to identify the request, and
    // converted to UTC on the server for display.
    private const string SampleSql = """
        SELECT r.session_id, r.request_id, r.start_time,
               DATEADD(minute, DATEDIFF(minute, SYSDATETIME(), SYSUTCDATETIME()), r.start_time) AS start_utc,
               r.status, r.command, r.total_elapsed_time, r.cpu_time, r.logical_reads, r.writes,
               r.wait_type, r.blocking_session_id, DB_NAME(r.database_id) AS database_name,
               CONVERT(varchar(18), r.query_hash, 1) AS query_hash,
               r.sql_handle, r.statement_start_offset, r.statement_end_offset,
               s.host_name, s.host_process_id, s.program_name, s.login_name
        FROM sys.dm_exec_requests AS r
        JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
        WHERE s.is_user_process = 1 AND r.session_id <> @@SPID;

        SELECT s.session_id, s.host_process_id, s.status, s.open_transaction_count,
               s.last_request_start_time, s.last_request_end_time
        FROM sys.dm_exec_sessions AS s
        WHERE s.is_user_process = 1 AND s.host_name = @host AND s.session_id <> @@SPID AND s.host_process_id IS NOT NULL;
        """;

    private readonly IReadOnlyList<string> _connectionStrings;
    private readonly TimeSpan _interval;
    private readonly ServerState[] _servers;
    private readonly ConcurrentDictionary<string, SqlText> _texts = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile string _status = "Waiting for the first sample.";
    private volatile bool _ok = true;
    private DateTime _lastSampleUtc = DateTime.MinValue;

    public SqlActivitySampler(IReadOnlyList<string> connectionStrings, TimeSpan interval, double slowThresholdMs)
    {
        _connectionStrings = connectionStrings;
        _interval = interval;
        _servers = connectionStrings.Select(_ => new ServerState()).ToArray();
        Accumulator = new DbActivityAccumulator(slowThresholdMs, interval.TotalMilliseconds);
        _loop = Task.Run(LoopAsync);
    }

    public DbActivityAccumulator Accumulator { get; }

    public (bool Ok, string Message) Status => (_ok, _status);

    public DbActivityInput? Drain() => Accumulator.Drain(Describe);

    private SqlText Describe(string? textKey) =>
        textKey is not null && _texts.TryGetValue(textKey, out var text) ? text : default;

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        var nextRegular = DateTime.UtcNow;
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                if (now >= nextRegular)
                {
                    await SafeSampleAsync(probe: false, token).ConfigureAwait(false);
                    nextRegular += _interval;
                    if (nextRegular <= DateTime.UtcNow)
                        nextRegular = DateTime.UtcNow + _interval;
                    continue;
                }

                // A watched query about to reach the slow threshold: look again right after it does,
                // so a query that ends between two regular samples isn't missed. Not needed when the
                // next regular sample comes soon enough anyway.
                var wake = nextRegular;
                if (Accumulator.NextThresholdCrossingUtc() is { } crossing && crossing < nextRegular - MinProbeGap)
                {
                    var probeAt = crossing < _lastSampleUtc + MinProbeGap ? _lastSampleUtc + MinProbeGap : crossing;
                    if (probeAt <= now)
                    {
                        await SafeSampleAsync(probe: true, token).ConfigureAwait(false);
                        continue;
                    }

                    if (probeAt < wake)
                        wake = probeAt;
                }

                await Task.Delay(wake - now, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SafeSampleAsync(bool probe, CancellationToken token)
    {
        try
        {
            await SampleAsync(probe, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unexpected data from a server must not stop sampling for good.
            _ok = false;
            _status = $"SQL Server sampling failed: {e.Message}";
        }
    }

    private async Task SampleAsync(bool probe, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        _lastSampleUtc = now;
        var requests = new List<RequestObservation>();
        var sessions = new List<SessionObservation>();
        var problems = new List<string>();
        var sampled = new HashSet<int>();

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
                sampled.Add(index);
                if (server.Warning is { } warning)
                    problems.Add(warning);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException or IOException or TimeoutException)
            {
                server.Fail($"{server.Name}: {e.Message}", now + RetryAfterFailure);
                problems.Add(server.LastError!);
            }
        }

        if (sampled.Count > 0)
        {
            Accumulator.Add(new DbSample
            {
                TimeUtc = now,
                Requests = requests,
                LocalSessions = sessions,
                ServersSampled = sampled,
                IsProbe = probe,
            });
            await LookUpEntryPointsAsync(token).ConfigureAwait(false);
            await LookUpTextsAsync(token).ConfigureAwait(false);
        }

        if (probe && problems.Count == 0)
            return;
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
            var sessionId = Convert.ToInt32(reader["session_id"]);
            var blockedBy = reader["blocking_session_id"] is DBNull ? 0 : Convert.ToInt32(reader["blocking_session_id"]);
            var handle = reader["sql_handle"] as byte[];
            var start = reader["statement_start_offset"] is DBNull ? 0 : Convert.ToInt32(reader["statement_start_offset"]);
            var end = reader["statement_end_offset"] is DBNull ? -1 : Convert.ToInt32(reader["statement_end_offset"]);
            requests.Add(new RequestObservation(
                SessionId: offset + sessionId,
                RequestId: Convert.ToInt32(reader["request_id"]),
                StartUtc: DateTime.SpecifyKind((DateTime)reader["start_utc"], DateTimeKind.Utc),
                Status: reader["status"] as string ?? "",
                Command: reader["command"] as string ?? "",
                ElapsedMs: Convert.ToDouble(reader["total_elapsed_time"]),
                CpuMs: Convert.ToDouble(reader["cpu_time"]),
                LogicalReads: Convert.ToInt64(reader["logical_reads"]),
                Writes: Convert.ToInt64(reader["writes"]),
                WaitType: reader["wait_type"] as string,
                // A parallel query can wait on its own session; that isn't blocking.
                BlockedBy: blockedBy > 0 && blockedBy != sessionId ? offset + blockedBy : 0,
                Database: reader["database_name"] as string ?? "",
                QueryHash: reader["query_hash"] as string,
                TextKey: handle is null ? null : TextKey(serverIndex, handle, start, end),
                IsLocal: string.Equals(reader["host_name"] as string, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
                Pid: reader["host_process_id"] is DBNull ? 0 : Convert.ToInt32(reader["host_process_id"]),
                Program: reader["program_name"] as string ?? "",
                Login: reader["login_name"] as string ?? "",
                // The whole batch: offset 0 to the end.
                BatchKey: handle is null ? null : TextKey(serverIndex, handle, 0, -1),
                RawStart: (DateTime)reader["start_time"],
                ServerIndex: serverIndex));
        }

        await reader.NextResultAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            sessions.Add(new SessionObservation(
                SessionId: offset + Convert.ToInt32(reader["session_id"]),
                Pid: Convert.ToInt32(reader["host_process_id"]),
                Status: reader["status"] as string ?? "",
                OpenTransactions: reader["open_transaction_count"] is DBNull ? 0 : Convert.ToInt32(reader["open_transaction_count"]),
                LastRequestStartRaw: reader["last_request_start_time"] as DateTime?,
                LastRequestEndRaw: reader["last_request_end_time"] as DateTime?));
        }
    }

    /// <summary>
    /// Reads what the app actually sent for newly watched requests (sys.dm_exec_input_buffer): the
    /// procedure it called, or the batch. The request itself may be deep inside a nested procedure
    /// or function by now.
    /// </summary>
    private async Task LookUpEntryPointsAsync(CancellationToken token)
    {
        var pending = Accumulator.PendingEntryPoints();
        if (pending.Count == 0)
            return;

        foreach (var group in pending.GroupBy(p => p.Server))
        {
            var server = _servers[group.Key];
            if (DateTime.UtcNow < server.EntryPointsRetryAtUtc)
            {
                foreach (var request in group)
                    Accumulator.SetEntryPoint(request, null);
                continue;
            }

            var requests = group.Take(MaxEntryPointLookupsPerSample).ToList();
            try
            {
                var connection = await server.GetConnectionAsync(_connectionStrings[group.Key], token).ConfigureAwait(false);
                var values = string.Join(", ", requests.Select((_, i) => $"({i}, @s{i}, @r{i}, @t{i})"));

                // Joined to the running request so a session that has moved on to its next request
                // doesn't report that one instead.
                await using var command = new SqlCommand($"""
                    SELECT v.k, b.event_type, b.event_info
                    FROM (VALUES {values}) AS v(k, s, r, t)
                    JOIN sys.dm_exec_requests AS q ON q.session_id = v.s AND q.request_id = v.r AND q.start_time = v.t
                    CROSS APPLY sys.dm_exec_input_buffer(v.s, v.r) AS b;
                    """, connection) { CommandTimeout = 5 };
                for (var i = 0; i < requests.Count; i++)
                {
                    command.Parameters.Add($"@s{i}", SqlDbType.SmallInt).Value = requests[i].RawSession;
                    command.Parameters.Add($"@r{i}", SqlDbType.Int).Value = requests[i].Request;
                    command.Parameters.Add($"@t{i}", SqlDbType.DateTime).Value = requests[i].RawStart;
                }

                var found = new Dictionary<int, string?>();
                await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        found[reader.GetInt32(0)] = EntryPoint(reader["event_type"] as string, reader["event_info"] as string);
                }

                for (var i = 0; i < requests.Count; i++)
                    Accumulator.SetEntryPoint(requests[i], found.GetValueOrDefault(i));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (SqlException)
            {
                // SQL Server before 2016 SP2 has no dm_exec_input_buffer; slow queries are then named
                // by the batch or procedure they were first seen in.
                server.EntryPointsRetryAtUtc = DateTime.UtcNow + EntryPointRetryAfterFailure;
                foreach (var request in group)
                    Accumulator.SetEntryPoint(request, null);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
            {
                // The next sample tries again.
            }
        }
    }

    /// <summary>
    /// Turns an input buffer into an entry point: "proc:db.schema.name" for a procedure call,
    /// "text:..." (literals removed) for a batch or parameterised query, null if neither.
    /// </summary>
    internal static string? EntryPoint(string? eventType, string? eventInfo)
    {
        // SQL Server ends procedure names with a NUL character.
        eventInfo = eventInfo?.Trim().TrimEnd('\0').Trim();
        if (string.IsNullOrEmpty(eventInfo))
            return null;

        if (eventType?.StartsWith("RPC", StringComparison.OrdinalIgnoreCase) == true)
        {
            // "AppDb.dbo.GetOrders;1"
            var name = eventInfo;
            var semicolon = name.LastIndexOf(';');
            if (semicolon > 0 && name[(semicolon + 1)..].All(char.IsDigit))
                name = name[..semicolon];
            var parts = name.Split('.').Select(p => p.Trim().Trim('[', ']', '"')).ToArray();

            // sp_executesql, sp_prepexec, sp_execute and the cursor procedures only carry the real
            // query, which isn't in the input buffer; fall back to the batch.
            if (parts.Length <= 2 && parts[^1].StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                return null;
            return "proc:" + string.Join('.', parts);
        }

        var text = SqlTextNormalizer.Normalize(eventInfo);
        return text.Length == 0 ? null : "text:" + text;
    }

    /// <summary>Fetches statement text (literals removed) for slow and listed queries not seen before.</summary>
    private async Task LookUpTextsAsync(CancellationToken token)
    {
        var pending = Accumulator.PendingTextKeys();
        var missing = pending.Where(k => !_texts.ContainsKey(k)).Take(MaxTextLookupsPerSample).ToList();
        if (missing.Count == 0)
            return;

        if (_texts.Count > TextCacheLimit)
        {
            // Forget texts no longer needed; keep those still waiting to be drained.
            var keep = pending.ToHashSet();
            foreach (var key in _texts.Keys)
            {
                if (!keep.Contains(key))
                    _texts.TryRemove(key, out _);
            }
        }

        foreach (var group in missing.Select(ParseTextKey).Where(k => k is not null).Select(k => k!.Value).GroupBy(k => k.Server))
        {
            var server = _servers[group.Key];
            try
            {
                var connection = await server.GetConnectionAsync(_connectionStrings[group.Key], token).ConfigureAwait(false);
                var keys = group.ToList();
                var values = string.Join(", ", keys.Select((_, i) => $"({i}, @h{i}, @s{i}, @e{i})"));

                // OBJECT_NAME needs metadata access in that database, which a login with only
                // VIEW SERVER STATE lacks; the name is then read from the module's CREATE header.
                await using var command = new SqlCommand($"""
                    SELECT v.k,
                           SUBSTRING(t.text, (v.s / 2) + 1,
                                     ((CASE v.e WHEN -1 THEN DATALENGTH(t.text) ELSE v.e END) - v.s) / 2 + 1) AS statement_text,
                           DB_NAME(t.dbid) AS database_name, t.objectid,
                           OBJECT_SCHEMA_NAME(t.objectid, t.dbid) AS schema_name, OBJECT_NAME(t.objectid, t.dbid) AS object_name,
                           CASE WHEN t.objectid IS NOT NULL AND OBJECT_NAME(t.objectid, t.dbid) IS NULL THEN LEFT(t.text, 2000) END AS header
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
                    var statement = reader["statement_text"] is string text ? SqlTextNormalizer.Normalize(text) : null;
                    var objectName = ObjectName(
                        reader["database_name"] as string,
                        reader["objectid"] is DBNull ? null : Convert.ToInt32(reader["objectid"]),
                        reader["schema_name"] as string,
                        reader["object_name"] as string,
                        reader["header"] as string);
                    _texts[keys[i].Key] = new SqlText(string.IsNullOrEmpty(statement) ? null : statement, objectName);
                }

                // Plans already evicted from the cache have no text; don't ask again.
                for (var i = 0; i < keys.Count; i++)
                {
                    if (!found.Contains(i))
                        _texts.TryAdd(keys[i].Key, default);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or IOException or TimeoutException)
            {
                // Text is a nice-to-have; the next sample tries again.
            }
        }
    }

    /// <summary>database.schema.name of a module, or null for an ad hoc batch.</summary>
    internal static string? ObjectName(string? database, int? objectId, string? schema, string? name, string? header)
    {
        if (objectId is null)
            return null;
        var prefix = string.IsNullOrEmpty(database) ? "" : database + ".";
        if (!string.IsNullOrEmpty(name))
            return prefix + (string.IsNullOrEmpty(schema) ? "" : schema + ".") + name;
        if (header is not null && ModuleHeader().Match(SqlTextNormalizer.Normalize(header)) is { Success: true } match)
            return prefix + string.Join('.', match.Groups["name"].Value.Split('.').Select(p => p.Trim().Trim('[', ']', '"')));
        return $"{prefix}#{objectId}";
    }

    [GeneratedRegex("""^(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC|PROCEDURE|FUNCTION|TRIGGER)\s+(?<name>(?:\[[^\]]+\]|"[^"]+"|[\w@#$]+)(?:\s*\.\s*(?:\[[^\]]+\]|"[^"]+"|[\w@#$]+))*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleHeader();

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
        public DateTime EntryPointsRetryAtUtc { get; set; }

        public async Task<SqlConnection> GetConnectionAsync(string connectionString, CancellationToken token)
        {
            if (_connection is { State: ConnectionState.Open })
                return _connection;

            await DisposeAsync().ConfigureAwait(false);
            var builder = new SqlConnectionStringBuilder(connectionString) { ApplicationName = "IISMonitor", ConnectTimeout = 5 };
            Name = builder.DataSource;
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);

            // SQL Server 2022 split VIEW SERVER STATE; VIEW SERVER PERFORMANCE STATE is enough for these views.
            await using (var permission = new SqlCommand("""
                SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'),
                       CASE WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 16
                            THEN HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE') END;
                """, connection) { CommandTimeout = 5 })
            await using (var reader = await permission.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                var granted = await reader.ReadAsync(token).ConfigureAwait(false)
                              && ((reader[0] is int state && state == 1) || (reader[1] is int performance && performance == 1));
                Warning = granted
                    ? null
                    : $"{Name}: the login lacks VIEW SERVER STATE, so only its own queries are visible. Grant it to see the app pools' queries.";
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
