using Microsoft.Data.Sqlite;

namespace IISMonitor.Core.Blocking;

/// <summary>
/// The blacklist's own record of every block (in the history database file, its own table), so
/// blocks survive restarts and expire even while the service was down. The firewall rules and IIS
/// entries are projections of the current rows. Thread-safe.
/// </summary>
public sealed class BlockStore
{
    private readonly string _connectionString;

    public BlockStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS blocked_ips (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ip TEXT NOT NULL,
                method INTEGER NOT NULL,
                state INTEGER NOT NULL,
                rule_id TEXT,
                reason TEXT NOT NULL,
                manual INTEGER NOT NULL,
                site TEXT,
                path TEXT,
                hits INTEGER NOT NULL,
                created_ts INTEGER NOT NULL,
                expires_ts INTEGER,
                ended_ts INTEGER,
                message TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_blocked_ips_state ON blocked_ips (state);
            CREATE INDEX IF NOT EXISTS ix_blocked_ips_ip ON blocked_ips (ip);
            """);
    }

    public long Insert(BlockEntry entry)
    {
        using var connection = Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO blocked_ips (ip, method, state, rule_id, reason, manual, site, path, hits, created_ts, expires_ts, ended_ts, message)
            VALUES ($ip, $method, $state, $rule, $reason, $manual, $site, $path, $hits, $created, $expires, $ended, $message);
            SELECT last_insert_rowid();
            """;
        Bind(insert, entry);
        entry.Id = Convert.ToInt64(insert.ExecuteScalar());
        return entry.Id;
    }

    public void Update(BlockEntry entry)
    {
        using var connection = Open();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE blocked_ips SET ip = $ip, method = $method, state = $state, rule_id = $rule, reason = $reason, manual = $manual,
                site = $site, path = $path, hits = $hits, created_ts = $created, expires_ts = $expires, ended_ts = $ended, message = $message
            WHERE id = $id;
            """;
        Bind(update, entry);
        update.Parameters.AddWithValue("$id", entry.Id);
        update.ExecuteNonQuery();
    }

    /// <summary>Blocks that are pending, active, failed or watched.</summary>
    public List<BlockEntry> Current() => Select(
        $"WHERE state IN ({(int)BlockState.Pending}, {(int)BlockState.Active}, {(int)BlockState.Watch}, {(int)BlockState.Failed}) ORDER BY created_ts DESC",
        null);

    /// <summary>Current blocks, then (if asked) lifted and expired ones, most recent first.</summary>
    public List<BlockEntry> List(BlockListQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 10_000);
        var current = Current();
        if (!query.IncludeEnded || current.Count >= limit)
            return current.Take(limit).ToList();
        var ended = Select(
            $"WHERE state IN ({(int)BlockState.Lifted}, {(int)BlockState.Expired}) ORDER BY COALESCE(ended_ts, created_ts) DESC LIMIT $limit",
            command => command.Parameters.AddWithValue("$limit", limit - current.Count));
        return current.Concat(ended).ToList();
    }

    /// <summary>Removes lifted and expired blocks that ended before <paramref name="cutoffUtc"/>; current blocks are kept.</summary>
    public int Purge(DateTime cutoffUtc)
    {
        using var connection = Open();
        using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM blocked_ips WHERE state IN ({(int)BlockState.Lifted}, {(int)BlockState.Expired}) AND COALESCE(ended_ts, created_ts) < $cutoff;";
        delete.Parameters.AddWithValue("$cutoff", ToUnixMs(cutoffUtc));
        return delete.ExecuteNonQuery();
    }

    private List<BlockEntry> Select(string where, Action<SqliteCommand>? bind)
    {
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = $"""
            SELECT id, ip, method, state, rule_id, reason, manual, site, path, hits, created_ts, expires_ts, ended_ts, message
            FROM blocked_ips {where};
            """;
        bind?.Invoke(select);
        var result = new List<BlockEntry>();
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new BlockEntry
            {
                Id = reader.GetInt64(0),
                Ip = reader.GetString(1),
                Method = (BlockMethod)reader.GetInt32(2),
                State = (BlockState)reader.GetInt32(3),
                RuleId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Reason = reader.GetString(5),
                Manual = reader.GetInt32(6) != 0,
                Site = reader.IsDBNull(7) ? null : reader.GetString(7),
                Path = reader.IsDBNull(8) ? null : reader.GetString(8),
                Hits = reader.GetInt32(9),
                CreatedUtc = FromUnixMs(reader.GetInt64(10)),
                ExpiresUtc = reader.IsDBNull(11) ? null : FromUnixMs(reader.GetInt64(11)),
                EndedUtc = reader.IsDBNull(12) ? null : FromUnixMs(reader.GetInt64(12)),
                Message = reader.IsDBNull(13) ? null : reader.GetString(13),
            });
        }

        return result;
    }

    private static void Bind(SqliteCommand command, BlockEntry entry)
    {
        command.Parameters.AddWithValue("$ip", entry.Ip);
        command.Parameters.AddWithValue("$method", (int)entry.Method);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$rule", (object?)entry.RuleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", entry.Reason);
        command.Parameters.AddWithValue("$manual", entry.Manual ? 1 : 0);
        command.Parameters.AddWithValue("$site", (object?)entry.Site ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)entry.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$hits", entry.Hits);
        command.Parameters.AddWithValue("$created", ToUnixMs(entry.CreatedUtc));
        command.Parameters.AddWithValue("$expires", entry.ExpiresUtc is { } expires ? ToUnixMs(expires) : DBNull.Value);
        command.Parameters.AddWithValue("$ended", entry.EndedUtc is { } ended ? ToUnixMs(ended) : DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)entry.Message ?? DBNull.Value);
    }

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

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
}
