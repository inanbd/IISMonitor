using System.Text.RegularExpressions;
using IISMonitor.Core.Metrics;
using Microsoft.Data.Sqlite;

namespace IISMonitor.Core.History;

/// <summary>
/// SQLite storage for history rows: one wide table per entity kind with a column per metric,
/// generated from <see cref="MetricCatalog"/>. Thread-safe; each call uses its own pooled connection.
/// </summary>
public sealed partial class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string databasePath)
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

    public static string TableName(EntityKind kind) => kind switch
    {
        EntityKind.AppPool => "pool_history",
        EntityKind.Site => "site_history",
        _ => "server_history",
    };

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS entities (
                kind INTEGER NOT NULL,
                name TEXT NOT NULL,
                last_seen INTEGER NOT NULL,
                PRIMARY KEY (kind, name)
            ) WITHOUT ROWID;
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS slow_queries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_ts INTEGER NOT NULL,
                end_ts INTEGER NOT NULL,
                app_pool TEXT NOT NULL,
                pid INTEGER NOT NULL,
                database_name TEXT NOT NULL,
                login_name TEXT NOT NULL,
                program_name TEXT NOT NULL,
                query_hash TEXT,
                object_name TEXT,
                statement TEXT NOT NULL,
                duration_ms REAL NOT NULL,
                cpu_ms REAL NOT NULL,
                logical_reads INTEGER NOT NULL,
                writes INTEGER NOT NULL,
                main_wait TEXT,
                was_blocked INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_slow_queries_end ON slow_queries (end_ts);
            """);

        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            var table = TableName(kind);
            Execute(connection, $"""
                CREATE TABLE IF NOT EXISTS {table} (
                    name TEXT NOT NULL,
                    ts INTEGER NOT NULL,
                    PRIMARY KEY (name, ts)
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_{table}_ts ON {table} (ts);
                """);

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info({table});";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(1));
            }

            foreach (var metric in MetricCatalog.Metrics(kind))
            {
                EnsureSafeIdentifier(metric.Key);
                if (!existing.Contains(metric.Key))
                    Execute(connection, $"ALTER TABLE {table} ADD COLUMN {metric.Key} REAL;");
            }
        }
    }

    public void Write(IReadOnlyCollection<HistoryRow> rows)
    {
        if (rows.Count == 0)
            return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var group in rows.GroupBy(r => r.Kind))
        {
            var metrics = MetricCatalog.Metrics(group.Key);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var columns = string.Join(", ", metrics.Select(m => m.Key));
            var parameters = string.Join(", ", metrics.Select((_, i) => "$m" + i));
            insert.CommandText = $"INSERT OR REPLACE INTO {TableName(group.Key)} (name, ts, {columns}) VALUES ($name, $ts, {parameters});";
            var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
            var tsParameter = insert.Parameters.Add("$ts", SqliteType.Integer);
            var metricParameters = metrics.Select((_, i) => insert.Parameters.Add("$m" + i, SqliteType.Real)).ToArray();

            using var entity = connection.CreateCommand();
            entity.Transaction = transaction;
            entity.CommandText = "INSERT OR REPLACE INTO entities (kind, name, last_seen) VALUES ($kind, $name, $ts);";
            var entityKind = entity.Parameters.Add("$kind", SqliteType.Integer);
            var entityName = entity.Parameters.Add("$name", SqliteType.Text);
            var entityTs = entity.Parameters.Add("$ts", SqliteType.Integer);

            foreach (var row in group)
            {
                var ts = ToUnixMs(row.BucketStartUtc);
                nameParameter.Value = row.Name;
                tsParameter.Value = ts;
                for (var i = 0; i < metrics.Count; i++)
                {
                    metricParameters[i].Value = row.Values.TryGetValue(metrics[i].Key, out var v) && v is { } value && !double.IsNaN(value)
                        ? value
                        : DBNull.Value;
                }

                insert.ExecuteNonQuery();

                entityKind.Value = (int)row.Kind;
                entityName.Value = row.Name;
                entityTs.Value = ts;
                entity.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public void WriteSlowQueries(IReadOnlyCollection<Models.SlowQuery> queries)
    {
        if (queries.Count == 0)
            return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO slow_queries (start_ts, end_ts, app_pool, pid, database_name, login_name, program_name, query_hash,
                                      object_name, statement, duration_ms, cpu_ms, logical_reads, writes, main_wait, was_blocked)
            VALUES ($start, $end, $pool, $pid, $db, $login, $program, $hash, $object, $statement, $duration, $cpu, $reads, $writes, $wait, $blocked);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "start", "end", "pool", "pid", "db", "login", "program", "hash", "object", "statement", "duration", "cpu", "reads", "writes", "wait", "blocked" })
            p[name] = insert.Parameters.Add(new SqliteParameter { ParameterName = "$" + name });

        foreach (var q in queries)
        {
            p["start"].Value = ToUnixMs(q.StartUtc);
            p["end"].Value = ToUnixMs(q.EndUtc);
            p["pool"].Value = q.AppPool ?? "";
            p["pid"].Value = q.Pid;
            p["db"].Value = q.Database;
            p["login"].Value = q.Login;
            p["program"].Value = q.Program;
            p["hash"].Value = (object?)q.QueryHash ?? DBNull.Value;
            p["object"].Value = (object?)q.ObjectName ?? DBNull.Value;
            p["statement"].Value = q.Statement ?? "";
            p["duration"].Value = q.DurationMs;
            p["cpu"].Value = q.CpuMs;
            p["reads"].Value = q.LogicalReads;
            p["writes"].Value = q.Writes;
            p["wait"].Value = (object?)q.MainWait ?? DBNull.Value;
            p["blocked"].Value = q.WasBlocked ? 1 : 0;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public SlowQueryReport QuerySlowQueries(SlowQueryRequest request)
    {
        var report = new SlowQueryReport();
        using var connection = Open();

        using (var groups = connection.CreateCommand())
        {
            // The same query shape groups by its query hash; statements without one group by text.
            groups.CommandText = """
                SELECT app_pool, database_name, MAX(object_name), MAX(statement), COUNT(*), AVG(duration_ms), MAX(duration_ms),
                       SUM(duration_ms), SUM(cpu_ms), SUM(logical_reads), SUM(was_blocked), MAX(end_ts),
                       (SELECT w.main_wait FROM slow_queries w
                        WHERE w.app_pool = s.app_pool AND w.database_name = s.database_name
                          AND COALESCE(w.query_hash, w.statement) = COALESCE(s.query_hash, s.statement)
                          AND w.end_ts >= $from AND w.end_ts < $to AND w.main_wait IS NOT NULL
                        GROUP BY w.main_wait ORDER BY COUNT(*) DESC LIMIT 1)
                FROM slow_queries s
                WHERE end_ts >= $from AND end_ts < $to AND ($pool IS NULL OR app_pool = $pool)
                GROUP BY app_pool, database_name, COALESCE(query_hash, statement)
                ORDER BY SUM(duration_ms) DESC
                LIMIT $limit;
                """;
            AddRange(groups, request);
            groups.Parameters.AddWithValue("$limit", Math.Clamp(request.MaxGroups, 1, 5000));
            using var reader = groups.ExecuteReader();
            while (reader.Read())
            {
                report.Groups.Add(new SlowQueryGroup
                {
                    AppPool = reader.GetString(0),
                    Database = reader.GetString(1),
                    ObjectName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Statement = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Count = reader.GetInt32(4),
                    AverageMs = reader.GetDouble(5),
                    MaxMs = reader.GetDouble(6),
                    TotalMs = reader.GetDouble(7),
                    TotalCpuMs = reader.GetDouble(8),
                    TotalLogicalReads = reader.GetInt64(9),
                    BlockedCount = reader.GetInt32(10),
                    LastSeenUnixMs = reader.GetInt64(11),
                    MainWait = reader.IsDBNull(12) ? null : reader.GetString(12),
                });
            }
        }

        using (var pools = connection.CreateCommand())
        {
            pools.CommandText = """
                SELECT app_pool, COUNT(*), SUM(duration_ms), SUM(cpu_ms)
                FROM slow_queries
                WHERE end_ts >= $from AND end_ts < $to AND ($pool IS NULL OR app_pool = $pool)
                GROUP BY app_pool
                ORDER BY SUM(duration_ms) DESC;
                """;
            AddRange(pools, request);
            using var reader = pools.ExecuteReader();
            while (reader.Read())
            {
                report.Pools.Add(new SlowQueryPoolTotal
                {
                    AppPool = reader.GetString(0),
                    Count = reader.GetInt32(1),
                    TotalMs = reader.GetDouble(2),
                    TotalCpuMs = reader.GetDouble(3),
                });
            }
        }

        return report;

        static void AddRange(SqliteCommand command, SlowQueryRequest request)
        {
            command.Parameters.AddWithValue("$from", ToUnixMs(request.FromUtc));
            command.Parameters.AddWithValue("$to", ToUnixMs(request.ToUtc));
            command.Parameters.AddWithValue("$pool", (object?)request.AppPool ?? DBNull.Value);
        }
    }

    public HistoryResult Query(HistoryQuery query)
    {
        var from = ToUnixMs(query.FromUtc);
        var to = ToUnixMs(query.ToUtc);
        if (to <= from)
            to = from + 1000;

        var maxPoints = Math.Clamp(query.MaxPoints, 10, 20_000);
        var bucketMs = Math.Max(1000L, (long)Math.Ceiling((to - from) / (double)maxPoints));
        var metrics = MetricCatalog.Metrics(query.Kind);
        var result = new HistoryResult
        {
            Kind = query.Kind,
            Name = query.Name,
            BucketSeconds = (int)(bucketMs / 1000),
            Series = metrics.ToDictionary(m => m.Key, _ => new List<double?>()),
        };

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT (ts / $bucket) * $bucket AS t, {string.Join(", ", metrics.Select(m => m.RollupSql))}
            FROM {TableName(query.Kind)}
            WHERE name = $name AND ts >= $from AND ts < $to
            GROUP BY t
            ORDER BY t;
            """;
        command.Parameters.AddWithValue("$bucket", bucketMs);
        command.Parameters.AddWithValue("$name", query.Name);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Timestamps.Add(reader.GetInt64(0));
            for (var i = 0; i < metrics.Count; i++)
                result.Series[metrics[i].Key].Add(reader.IsDBNull(i + 1) ? null : reader.GetDouble(i + 1));
        }

        return result;
    }

    /// <summary>Entity names that have history since <paramref name="sinceUtc"/>, alphabetically.</summary>
    public List<string> ListEntities(EntityKind kind, DateTime sinceUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM entities WHERE kind = $kind AND last_seen >= $since ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$since", ToUnixMs(sinceUtc));

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Deletes everything older than <paramref name="cutoffUtc"/>. Returns the number of rows removed.</summary>
    public int Purge(DateTime cutoffUtc)
    {
        var cutoff = ToUnixMs(cutoffUtc);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var removed = 0;
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {TableName(kind)} WHERE ts < $cutoff;";
            delete.Parameters.AddWithValue("$cutoff", cutoff);
            removed += delete.ExecuteNonQuery();
        }

        using (var slow = connection.CreateCommand())
        {
            slow.Transaction = transaction;
            slow.CommandText = "DELETE FROM slow_queries WHERE end_ts < $cutoff;";
            slow.Parameters.AddWithValue("$cutoff", cutoff);
            removed += slow.ExecuteNonQuery();
        }

        using (var entities = connection.CreateCommand())
        {
            entities.Transaction = transaction;
            entities.CommandText = "DELETE FROM entities WHERE last_seen < $cutoff;";
            entities.Parameters.AddWithValue("$cutoff", cutoff);
            entities.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    public static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void EnsureSafeIdentifier(string identifier)
    {
        if (!SafeIdentifier().IsMatch(identifier))
            throw new InvalidOperationException($"Metric key '{identifier}' is not a valid column name.");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();
}
