using Microsoft.Data.Sqlite;

namespace IISMonitor.Core.RequestLog;

/// <summary>SQLite storage for tracked requests (requests.db). Thread-safe; each call uses its own pooled connection.</summary>
/// <remarks>
/// Rows are keyed by (pool, minute, …) in a WITHOUT ROWID table, so new rows land at the end of each
/// pool's key range and there is deliberately no secondary index: queries scan one pool's minute range.
/// </remarks>
public sealed class RequestLogStore
{
    private const long MinuteMs = 60_000;

    /// <summary>Old rows are deleted this many milliseconds of one pool at a time, one transaction each.</summary>
    private const long PurgeChunkMs = 5 * MinuteMs;

    private readonly string _connectionString;

    public RequestLogStore(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var connection = Open();
        // Must come before the first table is created; lets a purge give space back (see ReleaseFreeSpace).
        Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS requests (
                pool TEXT NOT NULL COLLATE NOCASE,
                minute INTEGER NOT NULL,
                client_ip TEXT NOT NULL,
                url TEXT NOT NULL,
                method TEXT NOT NULL,
                status INTEGER NOT NULL,
                substatus INTEGER NOT NULL,
                hits INTEGER NOT NULL,
                first_ts INTEGER NOT NULL,
                last_ts INTEGER NOT NULL,
                time_ms REAL NOT NULL,
                PRIMARY KEY (pool, minute, client_ip, url, method, status, substatus)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS pools (pool TEXT NOT NULL COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;
            """);
    }

    /// <summary>Adds rows; a row whose key is already stored (a minute written twice) is merged into it.</summary>
    public void Write(IReadOnlyCollection<RequestLogEntry> entries)
    {
        if (entries.Count == 0)
            return;

        // In key order, so the B-tree is appended to rather than written all over.
        var sorted = entries
            .OrderBy(e => e.AppPool, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.MinuteUnixMs)
            .ThenBy(e => e.ClientIp, StringComparer.Ordinal)
            .ThenBy(e => e.Url, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Status)
            .ThenBy(e => e.SubStatus)
            .ToList();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO requests (pool, minute, client_ip, url, method, status, substatus, hits, first_ts, last_ts, time_ms)
                VALUES ($pool, $minute, $ip, $url, $method, $status, $substatus, $hits, $first, $last, $time)
                ON CONFLICT (pool, minute, client_ip, url, method, status, substatus) DO UPDATE SET
                    hits = hits + excluded.hits,
                    first_ts = min(first_ts, excluded.first_ts),
                    last_ts = max(last_ts, excluded.last_ts),
                    time_ms = time_ms + excluded.time_ms;
                """;
            var pool = insert.Parameters.Add("$pool", SqliteType.Text);
            var minute = insert.Parameters.Add("$minute", SqliteType.Integer);
            var ip = insert.Parameters.Add("$ip", SqliteType.Text);
            var url = insert.Parameters.Add("$url", SqliteType.Text);
            var method = insert.Parameters.Add("$method", SqliteType.Text);
            var status = insert.Parameters.Add("$status", SqliteType.Integer);
            var substatus = insert.Parameters.Add("$substatus", SqliteType.Integer);
            var hits = insert.Parameters.Add("$hits", SqliteType.Integer);
            var first = insert.Parameters.Add("$first", SqliteType.Integer);
            var last = insert.Parameters.Add("$last", SqliteType.Integer);
            var time = insert.Parameters.Add("$time", SqliteType.Real);

            foreach (var entry in sorted)
            {
                pool.Value = entry.AppPool;
                minute.Value = entry.MinuteUnixMs;
                ip.Value = entry.ClientIp;
                url.Value = entry.Url;
                method.Value = entry.Method;
                status.Value = entry.Status;
                substatus.Value = entry.SubStatus;
                hits.Value = entry.Hits;
                first.Value = entry.FirstUnixMs;
                last.Value = entry.LastUnixMs;
                time.Value = double.IsFinite(entry.TotalTimeMs) ? entry.TotalTimeMs : 0;
                insert.ExecuteNonQuery();
            }
        }

        using (var pools = connection.CreateCommand())
        {
            pools.Transaction = transaction;
            pools.CommandText = "INSERT OR IGNORE INTO pools (pool) VALUES ($pool);";
            var pool = pools.Parameters.Add("$pool", SqliteType.Text);
            foreach (var name in sorted.Select(e => e.AppPool).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                pool.Value = name;
                pools.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Deletes rows older than <paramref name="cutoffUtc"/>, a few minutes per transaction. Returns rows removed.</summary>
    public int Purge(DateTime cutoffUtc)
    {
        var removed = 0;
        int chunk;
        while ((chunk = PurgeChunk(cutoffUtc)) > 0)
            removed += chunk;
        return removed;
    }

    /// <summary>
    /// Deletes the oldest rows of one pool that are older than <paramref name="cutoffUtc"/>, at most five
    /// minutes of them, in one transaction. Returns rows removed; 0 once nothing older is left, after
    /// forgetting pools that have no rows. Lets the caller do other writes between chunks.
    /// </summary>
    internal int PurgeChunk(DateTime cutoffUtc)
    {
        var cutoff = RequestLogAggregator.ToUnixMs(cutoffUtc);
        using var connection = Open();
        // Short transactions keep the writer lock brief and the WAL small.
        using var transaction = connection.BeginTransaction();

        var pools = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT pool FROM pools;";
            using var reader = list.ExecuteReader();
            while (reader.Read())
                pools.Add(reader.GetString(0));
        }

        using (var oldest = connection.CreateCommand())
        {
            oldest.Transaction = transaction;
            oldest.CommandText = "SELECT MIN(minute) FROM requests WHERE pool = $pool;";
            var pool = oldest.Parameters.Add("$pool", SqliteType.Text);
            foreach (var name in pools)
            {
                pool.Value = name;
                if (oldest.ExecuteScalar() is not long start || start >= cutoff)
                    continue;

                // The row at 'start' is older than the cutoff, so this always removes at least one row.
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM requests WHERE pool = $pool AND minute < $end;";
                delete.Parameters.AddWithValue("$pool", name);
                delete.Parameters.AddWithValue("$end", Math.Min(start + PurgeChunkMs, cutoff));
                var removed = delete.ExecuteNonQuery();
                transaction.Commit();
                return removed;
            }
        }

        using (var emptyPools = connection.CreateCommand())
        {
            emptyPools.Transaction = transaction;
            emptyPools.CommandText = "DELETE FROM pools WHERE NOT EXISTS (SELECT 1 FROM requests r WHERE r.pool = pools.pool);";
            emptyPools.ExecuteNonQuery();
        }

        transaction.Commit();
        ReleaseFreeSpace(connection);
        return 0;
    }

    /// <summary>
    /// Shrinks the file when much of it is free, e.g. after the retention was shortened or a pool stopped
    /// being tracked. Normally the space an hourly purge frees is reused by the next hour's rows, so this
    /// does nothing and no pages are moved.
    /// </summary>
    private static void ReleaseFreeSpace(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA freelist_count;";
        var free = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "PRAGMA page_count;";
        var total = Convert.ToInt64(command.ExecuteScalar());
        if (free < 256 || free * 4 < total)
            return;

        command.CommandText = "PRAGMA incremental_vacuum;";
        command.ExecuteNonQuery();
    }

    public RequestLogReport Query(RequestLogQuery query)
    {
        var report = new RequestLogReport { View = query.View };
        var detail = query.View is RequestLogView.ClientDetail or RequestLogView.UrlDetail;
        if (string.IsNullOrEmpty(query.AppPool) || (detail && string.IsNullOrEmpty(query.Key)))
            return report;

        var limit = Math.Clamp(query.Limit, 1, RequestLogQuery.MaxLimit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$pool", query.AppPool);
        command.Parameters.AddWithValue("$from", RequestLogAggregator.FloorToMinute(RequestLogAggregator.ToUnixMs(query.FromUtc)));
        command.Parameters.AddWithValue("$to", RequestLogAggregator.ToUnixMs(query.ToUtc));
        const string range = "pool = $pool AND minute >= $from AND minute <= $to";

        if (!detail)
        {
            var (column, other) = query.View == RequestLogView.Clients ? ("client_ip", "url") : ("url", "client_ip");
            var filter = query.Filter?.Trim();
            var filterSql = "";
            if (!string.IsNullOrEmpty(filter))
            {
                filterSql = $"AND instr(lower({column}), lower($filter)) > 0";
                command.Parameters.AddWithValue("$filter", filter);
            }

            command.CommandText = $"""
                SELECT {column}, SUM(hits), COUNT(DISTINCT {other}),
                       SUM(CASE WHEN status BETWEEN 200 AND 299 THEN hits ELSE 0 END),
                       SUM(CASE WHEN status BETWEEN 300 AND 399 THEN hits ELSE 0 END),
                       SUM(CASE WHEN status BETWEEN 400 AND 499 THEN hits ELSE 0 END),
                       SUM(CASE WHEN status BETWEEN 500 AND 599 THEN hits ELSE 0 END),
                       SUM(time_ms), MIN(first_ts), MAX(last_ts)
                FROM requests
                WHERE {range} {filterSql}
                GROUP BY {column}
                ORDER BY SUM(hits) DESC, {column};
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hits = reader.GetInt64(1);
                report.TotalRows++;
                report.TotalHits += hits;
                if (report.Summaries.Count >= limit)
                    continue;

                report.Summaries.Add(new RequestLogSummaryRow
                {
                    Key = reader.GetString(0),
                    Hits = hits,
                    Distinct = reader.GetInt64(2),
                    Status2xx = reader.GetInt64(3),
                    Status3xx = reader.GetInt64(4),
                    Status4xx = reader.GetInt64(5),
                    Status5xx = reader.GetInt64(6),
                    AverageTimeMs = hits > 0 ? reader.GetDouble(7) / hits : 0,
                    FirstUnixMs = reader.GetInt64(8),
                    LastUnixMs = reader.GetInt64(9),
                });
            }

            report.Truncated = report.TotalRows > report.Summaries.Count;
            return report;
        }

        var (keyColumn, otherColumn) = query.View == RequestLogView.ClientDetail ? ("client_ip", "url") : ("url", "client_ip");
        command.Parameters.AddWithValue("$key", query.Key);
        command.CommandText = query.PerMinute
            ? $"""
                SELECT minute, client_ip, url, method, status, substatus, hits, time_ms, first_ts, last_ts
                FROM requests
                WHERE {range} AND {keyColumn} = $key
                ORDER BY minute DESC, hits DESC, {otherColumn}, method, status, substatus;
                """
            : $"""
                SELECT 0, MIN(client_ip), MIN(url), method, status, substatus, SUM(hits), SUM(time_ms), MIN(first_ts), MAX(last_ts)
                FROM requests
                WHERE {range} AND {keyColumn} = $key
                GROUP BY {otherColumn}, method, status, substatus
                ORDER BY SUM(hits) DESC, {otherColumn}, method, status, substatus;
                """;

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var hits = reader.GetInt64(6);
                report.TotalRows++;
                report.TotalHits += hits;
                if (report.Details.Count >= limit)
                    continue;

                report.Details.Add(new RequestLogDetailRow
                {
                    MinuteUnixMs = reader.GetInt64(0),
                    ClientIp = reader.GetString(1),
                    Url = reader.GetString(2),
                    Method = reader.GetString(3),
                    Status = reader.GetInt32(4),
                    SubStatus = reader.GetInt32(5),
                    Hits = hits,
                    AverageTimeMs = hits > 0 ? reader.GetDouble(7) / hits : 0,
                    FirstUnixMs = reader.GetInt64(8),
                    LastUnixMs = reader.GetInt64(9),
                });
            }
        }

        report.Truncated = report.TotalRows > report.Details.Count;
        return report;
    }

    /// <summary>Size of the database and its WAL file.</summary>
    public long SizeBytes() => FileLength(DatabasePath) + FileLength(DatabasePath + "-wal");

    private static long FileLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // Every connection, so the WAL file is truncated back to this size after checkpoints.
            Execute(connection, "PRAGMA synchronous = NORMAL; PRAGMA journal_size_limit = 67108864;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
