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
