using IISMonitor.Core.Collection;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;
using Microsoft.Data.Sqlite;

namespace IISMonitor.Core.Tests;

public sealed class RequestLogTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iismonitor-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Best effort: Windows can hold SQLite files open briefly after the pools are cleared.
        }
    }

    private string DatabasePath => Path.Combine(_directory, "requests.db");

    private RequestLogStore CreateStore()
    {
        var store = new RequestLogStore(DatabasePath);
        store.Initialize();
        return store;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static RequestLogEntry Entry(
        string pool, DateTime minute, string ip, string url, int status, long hits, double totalTimeMs,
        string method = "GET", int subStatus = 0, int firstSecond = 0, int lastSecond = 59) => new()
    {
        AppPool = pool,
        MinuteUnixMs = Ms(minute),
        ClientIp = ip,
        Url = url,
        Method = method,
        Status = status,
        SubStatus = subStatus,
        Hits = hits,
        FirstUnixMs = Ms(minute.AddSeconds(firstSecond)),
        LastUnixMs = Ms(minute.AddSeconds(lastSecond)),
        TotalTimeMs = totalTimeMs,
    };

    /// <summary>Two minutes of ShopPool traffic from three clients, and some OtherPool traffic.</summary>
    private RequestLogStore CreateSeededStore()
    {
        var store = CreateStore();
        var m1 = T0.AddMinutes(1);
        store.Write(
        [
            Entry("ShopPool", T0, "203.0.113.7", "/login", 200, 3, 30, firstSecond: 1, lastSecond: 50),
            Entry("ShopPool", T0, "203.0.113.7", "/login", 401, 2, 10, method: "POST", subStatus: 1, firstSecond: 2, lastSecond: 3),
            Entry("ShopPool", T0, "203.0.113.7", "/cart", 302, 1, 5, firstSecond: 40, lastSecond: 40),
            Entry("ShopPool", T0, "198.51.100.4", "/login", 200, 1, 100, firstSecond: 10, lastSecond: 10),
            Entry("ShopPool", T0, "198.51.100.4", "/missing", 404, 4, 8, firstSecond: 20, lastSecond: 30),
            Entry("ShopPool", m1, "203.0.113.7", "/login", 200, 5, 50, firstSecond: 0, lastSecond: 45),
            Entry("ShopPool", m1, "10.0.0.5", "/api", 500, 2, 400, firstSecond: 5, lastSecond: 6),
            Entry("OtherPool", T0, "203.0.113.7", "/other", 200, 100, 100),
        ]);
        return store;
    }

    private static RequestLogQuery Query(RequestLogView view, string? key = null, string pool = "ShopPool") => new()
    {
        AppPool = pool,
        FromUtc = T0,
        ToUtc = T0.AddHours(1),
        View = view,
        Key = key,
    };

    // ---- Aggregator ----

    [Fact]
    public void Aggregator_merges_requests_with_the_same_key()
    {
        var aggregator = new RequestLogAggregator();
        aggregator.Record("ShopPool", T0.AddSeconds(30), "203.0.113.7", "GET", "/login", 200, 0, 10);
        aggregator.Record("SHOPPOOL", T0.AddSeconds(5), "203.0.113.7", "GET", "/login", 200, 0, 20);
        aggregator.Record("ShopPool", T0.AddSeconds(50), "203.0.113.7", "GET", "/login", 200, 0, 30);

        Assert.Equal(1, aggregator.PendingRows);
        var entry = Assert.Single(aggregator.Drain());
        Assert.Equal("ShopPool", entry.AppPool);
        Assert.Equal(Ms(T0), entry.MinuteUnixMs);
        Assert.Equal(3, entry.Hits);
        Assert.Equal(60, entry.TotalTimeMs);
        Assert.Equal(Ms(T0.AddSeconds(5)), entry.FirstUnixMs);
        Assert.Equal(Ms(T0.AddSeconds(50)), entry.LastUnixMs);
        Assert.Equal(3, aggregator.RecordedRequests);
    }

    [Fact]
    public void Aggregator_keeps_minutes_ips_urls_methods_and_statuses_apart()
    {
        var aggregator = new RequestLogAggregator();
        aggregator.Record("Pool", T0.AddMilliseconds(59_999), "203.0.113.7", "GET", "/a", 200, 0, 1);
        aggregator.Record("Pool", T0.AddMinutes(1), "203.0.113.7", "GET", "/a", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.8", "GET", "/a", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", "/A", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "POST", "/a", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", "/a", 404, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", "/a", 404, 2, 1);
        aggregator.Record("Other", T0, "203.0.113.7", "GET", "/a", 200, 0, 1);

        var entries = aggregator.Drain();
        Assert.Equal(8, entries.Count);
        Assert.All(entries, e => Assert.Equal(1, e.Hits));
        Assert.Equal(
            [Ms(T0), Ms(T0.AddMinutes(1))],
            entries.Select(e => e.MinuteUnixMs).Distinct().Order());
        var edge = entries.Single(e => e.MinuteUnixMs == Ms(T0) && e.Url == "/a" && e.Status == 200 && e.Method == "GET" && e.ClientIp == "203.0.113.7" && e.AppPool == "Pool");
        Assert.Equal(Ms(T0) + 59_999, edge.FirstUnixMs);
    }

    [Fact]
    public void Aggregator_takes_unspecified_times_as_utc_and_converts_local_times()
    {
        var aggregator = new RequestLogAggregator();
        aggregator.Record("Pool", DateTime.SpecifyKind(T0.AddSeconds(1), DateTimeKind.Unspecified), "203.0.113.7", "GET", "/", 200, 0, 1);
        aggregator.Record("Pool", T0.AddSeconds(2).ToLocalTime(), "203.0.113.7", "GET", "/", 200, 0, 1);

        var entry = Assert.Single(aggregator.Drain());
        Assert.Equal(2, entry.Hits);
        Assert.Equal(Ms(T0.AddSeconds(1)), entry.FirstUnixMs);
        Assert.Equal(Ms(T0.AddSeconds(2)), entry.LastUnixMs);
    }

    [Fact]
    public void Aggregator_normalizes_urls_clients_and_methods()
    {
        var aggregator = new RequestLogAggregator();
        var longUrl = "/" + new string('a', 1000);
        aggregator.Record("Pool", T0, null, null, longUrl, 200, 0, 7);
        aggregator.Record("Pool", T0, "", "-", "", 200, 0, double.NaN);
        aggregator.Record("Pool", T0, "-", "-", "/", 200, 0, -5);
        aggregator.Record("Pool", T0, "-", "GET", "/", 200, 0, double.PositiveInfinity);

        var entries = aggregator.Drain();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(RequestLogAggregator.UnknownClient, e.ClientIp));

        // An empty URL is the root; unusable times count as 0 ms.
        var roots = entries.Where(e => e.Url == "/").OrderBy(e => e.Method).ToList();
        Assert.Equal([("", 2L, 0.0), ("GET", 1L, 0.0)], roots.Select(e => (e.Method, e.Hits, e.TotalTimeMs)));

        var truncated = entries.Single(e => e.Url != "/");
        Assert.Equal(7, truncated.TotalTimeMs);
        Assert.Equal(RequestLogAggregator.MaxUrlLength, truncated.Url.Length);
        Assert.Equal(longUrl[..(RequestLogAggregator.MaxUrlLength - 1)] + "…", truncated.Url);
        Assert.Equal(RequestLogAggregator.UnknownClient, truncated.ClientIp);
        Assert.Equal("", truncated.Method);
    }

    [Fact]
    public void Aggregator_maps_a_missing_method_to_empty_and_keeps_real_ones()
    {
        var aggregator = new RequestLogAggregator();
        aggregator.Record("Pool", T0, "203.0.113.7", null, "/", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "-", "/", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", "/", 200, 0, 1);

        var entries = aggregator.Drain().OrderBy(e => e.Method).ToList();
        Assert.Equal(["", "GET"], entries.Select(e => e.Method));
        Assert.Equal([2L, 1L], entries.Select(e => e.Hits));
    }

    [Fact]
    public void Aggregator_does_not_split_a_surrogate_pair_when_truncating()
    {
        var aggregator = new RequestLogAggregator();
        var url = "/" + new string('a', RequestLogAggregator.MaxUrlLength - 3) + "😀" + "tail";
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", url, 200, 0, 1);

        var stored = Assert.Single(aggregator.Drain()).Url;
        Assert.EndsWith("a…", stored);
        Assert.Equal(RequestLogAggregator.MaxUrlLength - 1, stored.Length);
    }

    [Fact]
    public void Aggregator_counts_new_combinations_beyond_the_cap_as_dropped()
    {
        var aggregator = new RequestLogAggregator(maxPendingRows: 2);
        aggregator.Record("Pool", T0, "203.0.113.1", "GET", "/", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.2", "GET", "/", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.3", "GET", "/", 200, 0, 1);
        aggregator.Record("Pool", T0, "203.0.113.4", "GET", "/", 200, 0, 1);
        // Existing combinations keep counting.
        aggregator.Record("Pool", T0, "203.0.113.1", "GET", "/", 200, 0, 1);

        Assert.Equal(5, aggregator.RecordedRequests);
        Assert.Equal(2, aggregator.DroppedRequests);
        Assert.Equal(2, aggregator.PendingRows);
        var entries = aggregator.Drain().OrderBy(e => e.ClientIp).ToList();
        Assert.Equal(["203.0.113.1", "203.0.113.2"], entries.Select(e => e.ClientIp));
        Assert.Equal([2L, 1L], entries.Select(e => e.Hits));

        // The cap applies per flush window.
        aggregator.Record("Pool", T0, "203.0.113.3", "GET", "/", 200, 0, 1);
        Assert.Equal(1, aggregator.PendingRows);
        Assert.Equal(2, aggregator.DroppedRequests);
    }

    [Fact]
    public void Drain_empties_the_aggregator()
    {
        var aggregator = new RequestLogAggregator();
        Assert.Empty(aggregator.Drain());
        aggregator.Record("Pool", T0, "203.0.113.7", "GET", "/", 200, 0, 1);

        Assert.Single(aggregator.Drain());
        Assert.Equal(0, aggregator.PendingRows);
        Assert.Empty(aggregator.Drain());
        Assert.Equal(1, aggregator.RecordedRequests);
    }

    // ---- Store ----

    [Fact]
    public void Store_creates_rowid_less_tables_without_secondary_indexes_and_initializes_twice()
    {
        CreateStore();
        CreateStore();

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master ORDER BY name;";
        var objects = new List<(string Type, string Name, string? Sql)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                objects.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        Assert.Equal([("table", "pools"), ("table", "requests")], objects.Select(o => (o.Type, o.Name)));
        Assert.All(objects, o => Assert.Contains("WITHOUT ROWID", o.Sql));
        Assert.Contains("PRIMARY KEY (pool, minute, client_ip, url, method, status, substatus)", objects[1].Sql);

        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "PRAGMA auto_vacuum;";
        Assert.Equal(2L, command.ExecuteScalar()); // INCREMENTAL
    }

    [Fact]
    public void Purge_gives_space_back_when_most_of_the_file_is_free()
    {
        var store = CreateStore();
        var entries = new List<RequestLogEntry>();
        for (var minute = -120; minute <= 0; minute++)
        {
            for (var client = 0; client < 300; client++)
                entries.Add(Entry("ShopPool", T0.AddMinutes(minute), $"203.0.{client / 256}.{client % 256}", "/products/" + new string('x', 60), 200, 1, 1));
        }

        store.Write(entries);
        var before = PageCount();

        // Keep the last 10 of 121 minutes: most of the file becomes free and is released.
        store.Purge(T0.AddMinutes(-9));
        var after = PageCount();
        Assert.True(after * 4 < before, $"page_count went from {before} to {after}.");

        var left = Query(RequestLogView.Clients);
        left.FromUtc = T0.AddDays(-1);
        Assert.Equal(3000, store.Query(left).TotalHits);

        long PageCount()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    [Fact]
    public void Writing_the_same_key_twice_merges_the_rows()
    {
        var store = CreateStore();
        store.Write([Entry("ShopPool", T0, "203.0.113.7", "/login", 200, 2, 10, firstSecond: 10, lastSecond: 20)]);
        store.Write([Entry("SHOPPOOL", T0, "203.0.113.7", "/login", 200, 3, 20, firstSecond: 5, lastSecond: 15)]);
        store.Write([Entry("ShopPool", T0, "203.0.113.7", "/login", 200, 1, 30, firstSecond: 12, lastSecond: 58)]);

        var report = store.Query(Query(RequestLogView.ClientDetail, "203.0.113.7"));
        var row = Assert.Single(report.Details);
        Assert.Equal(6, row.Hits);
        Assert.Equal(10, row.AverageTimeMs, 9);
        Assert.Equal(Ms(T0.AddSeconds(5)), row.FirstUnixMs);
        Assert.Equal(Ms(T0.AddSeconds(58)), row.LastUnixMs);
        Assert.Equal(Ms(T0), row.MinuteUnixMs);
        Assert.Equal((1L, 6L, false), (report.TotalRows, report.TotalHits, report.Truncated));
        Assert.True(store.SizeBytes() > 0);
    }

    [Fact]
    public void Clients_view_groups_by_ip_with_status_buckets_and_averages()
    {
        var store = CreateSeededStore();
        var report = store.Query(Query(RequestLogView.Clients));

        Assert.Equal(RequestLogView.Clients, report.View);
        Assert.Empty(report.Details);
        Assert.Equal(["203.0.113.7", "198.51.100.4", "10.0.0.5"], report.Summaries.Select(s => s.Key));
        Assert.Equal((3L, 18L, false), (report.TotalRows, report.TotalHits, report.Truncated));

        var top = report.Summaries[0];
        Assert.Equal((11L, 2L), (top.Hits, top.Distinct));
        Assert.Equal((8L, 1L, 2L, 0L), (top.Status2xx, top.Status3xx, top.Status4xx, top.Status5xx));
        Assert.Equal(95.0 / 11, top.AverageTimeMs, 9);
        Assert.Equal(Ms(T0.AddSeconds(1)), top.FirstUnixMs);
        Assert.Equal(Ms(T0.AddMinutes(1).AddSeconds(45)), top.LastUnixMs);

        var second = report.Summaries[1];
        Assert.Equal((5L, 2L), (second.Hits, second.Distinct));
        Assert.Equal((1L, 0L, 4L, 0L), (second.Status2xx, second.Status3xx, second.Status4xx, second.Status5xx));
        Assert.Equal(21.6, second.AverageTimeMs, 9);

        var third = report.Summaries[2];
        Assert.Equal((2L, 1L, 2L, 200.0), (third.Hits, third.Distinct, third.Status5xx, third.AverageTimeMs));
    }

    [Fact]
    public void Urls_view_groups_by_url_and_counts_distinct_clients()
    {
        var store = CreateSeededStore();
        var report = store.Query(Query(RequestLogView.Urls));

        Assert.Equal(["/login", "/missing", "/api", "/cart"], report.Summaries.Select(s => s.Key));
        Assert.Equal([11L, 4L, 2L, 1L], report.Summaries.Select(s => s.Hits));
        Assert.Equal([2L, 1L, 1L, 1L], report.Summaries.Select(s => s.Distinct));
        var login = report.Summaries[0];
        Assert.Equal((9L, 0L, 2L, 0L), (login.Status2xx, login.Status3xx, login.Status4xx, login.Status5xx));
        Assert.Equal(190.0 / 11, login.AverageTimeMs, 9);
        Assert.Equal(1, report.Summaries[3].Status3xx);
        Assert.Equal((4L, 18L), (report.TotalRows, report.TotalHits));
    }

    [Fact]
    public void Filter_matches_part_of_the_ip_or_url_ignoring_case()
    {
        var store = CreateSeededStore();

        var clients = Query(RequestLogView.Clients);
        clients.Filter = "203.0";
        Assert.Equal(["203.0.113.7"], store.Query(clients).Summaries.Select(s => s.Key));

        var urls = Query(RequestLogView.Urls);
        urls.Filter = " LOGIN ";
        var report = store.Query(urls);
        Assert.Equal(["/login"], report.Summaries.Select(s => s.Key));
        Assert.Equal((1L, 11L), (report.TotalRows, report.TotalHits));

        urls.Filter = "%";
        Assert.Empty(store.Query(urls).Summaries);

        urls.Filter = "  ";
        Assert.Equal(4, store.Query(urls).Summaries.Count);
    }

    [Fact]
    public void Limit_keeps_the_busiest_rows_and_reports_the_totals()
    {
        var store = CreateSeededStore();
        var query = Query(RequestLogView.Clients);
        query.Limit = 2;

        var report = store.Query(query);
        Assert.Equal(["203.0.113.7", "198.51.100.4"], report.Summaries.Select(s => s.Key));
        Assert.Equal((3L, 18L, true), (report.TotalRows, report.TotalHits, report.Truncated));

        query.Limit = 0;
        Assert.Single(store.Query(query).Summaries);
        query.Limit = int.MaxValue;
        Assert.False(store.Query(query).Truncated);
    }

    [Fact]
    public void Client_detail_lists_each_minute_or_totals()
    {
        var store = CreateSeededStore();
        var query = Query(RequestLogView.ClientDetail, "203.0.113.7");

        var perMinute = store.Query(query);
        Assert.Equal(
            [
                (Ms(T0.AddMinutes(1)), "/login", "GET", 200, 0, 5L),
                (Ms(T0), "/login", "GET", 200, 0, 3L),
                (Ms(T0), "/login", "POST", 401, 1, 2L),
                (Ms(T0), "/cart", "GET", 302, 0, 1L),
            ],
            perMinute.Details.Select(d => (d.MinuteUnixMs, d.Url, d.Method, d.Status, d.SubStatus, d.Hits)));
        Assert.All(perMinute.Details, d => Assert.Equal("203.0.113.7", d.ClientIp));
        Assert.Equal((4L, 11L, false), (perMinute.TotalRows, perMinute.TotalHits, perMinute.Truncated));
        Assert.Empty(perMinute.Summaries);

        query.PerMinute = false;
        var totals = store.Query(query);
        Assert.Equal(
            [("/login", "GET", 200, 8L), ("/login", "POST", 401, 2L), ("/cart", "GET", 302, 1L)],
            totals.Details.Select(d => (d.Url, d.Method, d.Status, d.Hits)));
        var login = totals.Details[0];
        Assert.Equal((0L, "203.0.113.7"), (login.MinuteUnixMs, login.ClientIp));
        Assert.Equal(10, login.AverageTimeMs, 9);
        Assert.Equal(Ms(T0.AddSeconds(1)), login.FirstUnixMs);
        Assert.Equal(Ms(T0.AddMinutes(1).AddSeconds(45)), login.LastUnixMs);
        Assert.Equal((3L, 11L), (totals.TotalRows, totals.TotalHits));
    }

    [Fact]
    public void Url_detail_lists_who_requested_a_url_each_minute_or_totals()
    {
        var store = CreateSeededStore();
        var query = Query(RequestLogView.UrlDetail, "/login");

        var perMinute = store.Query(query);
        Assert.Equal(
            [
                (Ms(T0.AddMinutes(1)), "203.0.113.7", "GET", 5L),
                (Ms(T0), "203.0.113.7", "GET", 3L),
                (Ms(T0), "203.0.113.7", "POST", 2L),
                (Ms(T0), "198.51.100.4", "GET", 1L),
            ],
            perMinute.Details.Select(d => (d.MinuteUnixMs, d.ClientIp, d.Method, d.Hits)));
        Assert.All(perMinute.Details, d => Assert.Equal("/login", d.Url));

        query.PerMinute = false;
        query.Limit = 2;
        var totals = store.Query(query);
        Assert.Equal(
            [("203.0.113.7", "GET", 200, 8L), ("203.0.113.7", "POST", 401, 2L)],
            totals.Details.Select(d => (d.ClientIp, d.Method, d.Status, d.Hits)));
        Assert.Equal(1, totals.Details[1].SubStatus);
        Assert.Equal((3L, 11L, true), (totals.TotalRows, totals.TotalHits, totals.Truncated));
    }

    [Fact]
    public void Queries_stay_within_one_pool_ignoring_its_case()
    {
        var store = CreateSeededStore();

        var other = store.Query(Query(RequestLogView.Clients, pool: "OtherPool"));
        var row = Assert.Single(other.Summaries);
        Assert.Equal(("203.0.113.7", 100L, 1L), (row.Key, row.Hits, row.Distinct));

        Assert.Equal(18, store.Query(Query(RequestLogView.Clients, pool: "shoppool")).TotalHits);
        Assert.Empty(store.Query(Query(RequestLogView.Urls, pool: "NoSuchPool")).Summaries);
        Assert.Empty(store.Query(Query(RequestLogView.Urls, pool: "")).Summaries);
        Assert.Empty(store.Query(Query(RequestLogView.ClientDetail, key: null)).Details);
        Assert.Empty(store.Query(Query(RequestLogView.UrlDetail, key: "")).Details);
        Assert.Empty(store.Query(Query(RequestLogView.UrlDetail, key: "/other")).Details);
    }

    [Fact]
    public void Queries_cover_whole_minutes_of_the_range()
    {
        var store = CreateSeededStore();

        // From is floored to its minute, so the second minute is included.
        var late = Query(RequestLogView.Clients);
        late.FromUtc = T0.AddMinutes(1).AddSeconds(30);
        Assert.Equal([("203.0.113.7", 5L), ("10.0.0.5", 2L)], store.Query(late).Summaries.Select(s => (s.Key, s.Hits)));

        var early = Query(RequestLogView.Clients);
        early.ToUtc = T0.AddSeconds(30);
        Assert.Equal([("203.0.113.7", 6L), ("198.51.100.4", 5L)], store.Query(early).Summaries.Select(s => (s.Key, s.Hits)));

        var before = Query(RequestLogView.Urls);
        before.FromUtc = T0.AddHours(-2);
        before.ToUtc = T0.AddMilliseconds(-1);
        Assert.Equal(0, store.Query(before).TotalRows);
    }

    [Fact]
    public void Purge_removes_only_old_rows_and_empty_pools()
    {
        var store = CreateStore();
        var entries = new List<RequestLogEntry>();
        for (var minute = -60; minute <= 0; minute++)
            entries.Add(Entry("ShopPool", T0.AddMinutes(minute), "203.0.113.7", "/", 200, 1, 1));
        entries.Add(Entry("ShopPool", T0.AddMinutes(-45), "198.51.100.4", "/", 200, 1, 1));
        entries.Add(Entry("OldPool", T0.AddHours(-2), "203.0.113.7", "/", 200, 1, 1));
        entries.Add(Entry("NewPool", T0, "203.0.113.7", "/", 200, 1, 1));
        store.Write(entries);

        // 30 old minutes of ShopPool (several chunks), one extra ShopPool row and OldPool's row.
        Assert.Equal(32, store.Purge(T0.AddMinutes(-30)));
        Assert.Equal(0, store.Purge(T0.AddMinutes(-30)));

        var all = Query(RequestLogView.Clients);
        all.FromUtc = T0.AddDays(-1);
        var shop = store.Query(all);
        Assert.Equal(("203.0.113.7", 31L), (Assert.Single(shop.Summaries).Key, shop.TotalHits));
        Assert.Equal(Ms(T0.AddMinutes(-30)), shop.Summaries[0].FirstUnixMs);
        all.AppPool = "NewPool";
        Assert.Equal(1, store.Query(all).TotalHits);

        Assert.Equal(["NewPool", "ShopPool"], ReadPools());
    }

    [Fact]
    public void Purge_deletes_at_most_five_minutes_of_one_pool_per_transaction()
    {
        var store = CreateStore();
        var entries = new List<RequestLogEntry>();
        // 12 old minutes of ShopPool, and two OtherPool rows whose minute values are not whole minutes.
        for (var minute = -12; minute <= 0; minute++)
            entries.Add(Entry("ShopPool", T0.AddMinutes(minute), "203.0.113.7", "/", 200, 1, 1));
        entries.Add(Entry("OtherPool", T0.AddMinutes(-3).AddSeconds(17), "203.0.113.7", "/", 200, 1, 1));
        entries.Add(Entry("OtherPool", T0.AddMinutes(-1).AddMilliseconds(1), "203.0.113.7", "/", 200, 1, 1));
        store.Write(entries);

        var chunks = new List<int>();
        int removed;
        while ((removed = store.PurgeChunk(T0)) > 0)
        {
            chunks.Add(removed);
            Assert.True(chunks.Count < 100, "Purge does not finish.");
        }

        // ShopPool in chunks of 5, 5 and 2 minutes; both OtherPool rows lie within five minutes, so one chunk.
        Assert.Equal([2, 2, 5, 5], chunks.Order());
        Assert.Equal(["ShopPool"], ReadPools());
        Assert.Equal(0, store.PurgeChunk(T0));

        var left = Query(RequestLogView.Clients);
        left.FromUtc = T0.AddDays(-1);
        Assert.Equal(1, store.Query(left).TotalHits);
    }

    private List<string> ReadPools()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pool FROM pools ORDER BY pool;";
        var pools = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            pools.Add(reader.GetString(0));
        return pools;
    }

    // ---- Recorder ----

    [Fact]
    public async Task Recorder_query_includes_requests_not_yet_flushed()
    {
        var now = DateTime.UtcNow;
        var recorder = new RequestLogRecorder(new RequestLogStore(DatabasePath));
        await using (recorder)
        {
            // Not started: only the query itself can have written these rows.
            recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "GET", "/login", 200, 0, 12);
            recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "GET", "/login", 200, 0, 18);
            recorder.Aggregator.Record("ShopPool", now, "198.51.100.4", "GET", "/", 404, 0, 3);

            var report = await recorder.QueryAsync(new RequestLogQuery
            {
                AppPool = "ShopPool",
                FromUtc = now.AddMinutes(-5),
                ToUtc = now.AddMinutes(5),
                View = RequestLogView.Clients,
            });

            Assert.Equal([("203.0.113.7", 2L, 15.0), ("198.51.100.4", 1L, 3.0)], report.Summaries.Select(s => (s.Key, s.Hits, s.AverageTimeMs)));
            Assert.Equal(0, recorder.Aggregator.PendingRows);
            Assert.Equal(2, recorder.RowsWritten);
            Assert.Null(recorder.Error);
        }
    }

    [Fact]
    public async Task Recorder_writes_pending_requests_when_disposed()
    {
        var now = DateTime.UtcNow;
        // Not started: only FlushAsync and DisposeAsync write.
        var recorder = new RequestLogRecorder(new RequestLogStore(DatabasePath));
        recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "POST", "/login", 401, 1, 5);
        await recorder.FlushAsync();
        Assert.Equal(1, recorder.RowsWritten);
        recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "POST", "/login", 401, 1, 7);
        await recorder.DisposeAsync();
        await recorder.DisposeAsync();

        Assert.Equal(2, recorder.RowsWritten);
        var report = new RequestLogStore(DatabasePath).Query(new RequestLogQuery
        {
            AppPool = "ShopPool",
            FromUtc = now.AddMinutes(-5),
            ToUtc = now.AddMinutes(5),
            View = RequestLogView.UrlDetail,
            Key = "/login",
        });
        var row = Assert.Single(report.Details);
        Assert.Equal(("203.0.113.7", "POST", 401, 1, 2L, 6.0), (row.ClientIp, row.Method, row.Status, row.SubStatus, row.Hits, row.AverageTimeMs));
    }

    [Fact]
    public async Task Recorder_loses_no_requests_while_recording_flushing_and_querying_concurrently()
    {
        const int threads = 4, perThread = 5000;
        var now = DateTime.UtcNow;
        var recorder = new RequestLogRecorder(new RequestLogStore(DatabasePath));
        recorder.Start();
        var query = new RequestLogQuery { AppPool = "ShopPool", FromUtc = now.AddMinutes(-5), ToUtc = now.AddMinutes(5), View = RequestLogView.Urls };

        using var done = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            long lastSeen = 0;
            while (!done.IsCancellationRequested)
            {
                if (i == 0)
                {
                    await recorder.FlushAsync();
                }
                else
                {
                    // What a query sees never goes backwards.
                    var hits = (await recorder.QueryAsync(query)).TotalHits;
                    Assert.True(hits >= lastSeen);
                    lastSeen = hits;
                }
            }
        })).ToList();

        var writers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                recorder.Aggregator.Record("ShopPool", now, $"203.0.113.{t}", "GET", $"/page{i % 50}", 200, 0, 1);
        })).ToList();

        await Task.WhenAll(writers);
        done.Cancel();
        await Task.WhenAll(readers);
        await recorder.DisposeAsync();

        var report = new RequestLogStore(DatabasePath).Query(query);
        Assert.Equal(threads * perThread, report.TotalHits);
        Assert.Equal(50, report.TotalRows);
        Assert.All(report.Summaries, s => Assert.Equal(threads, s.Distinct));
        Assert.Equal(threads * perThread, recorder.Aggregator.RecordedRequests);
        Assert.Equal(0, recorder.Aggregator.DroppedRequests);
        Assert.Null(recorder.Error);
    }

    [Fact]
    public async Task Recorder_reports_a_database_it_cannot_open_and_counts_lost_requests()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "");
        var logged = new List<string>();
        var recorder = new RequestLogRecorder(new RequestLogStore(Path.Combine(blocker, "requests.db")), (message, _) =>
        {
            lock (logged)
                logged.Add(message);
        });

        await using (recorder)
        {
            recorder.Start();
            Assert.StartsWith("Could not open", recorder.Error);

            recorder.Aggregator.Record("ShopPool", DateTime.UtcNow, "203.0.113.7", "GET", "/", 200, 0, 1);
            recorder.Aggregator.Record("ShopPool", DateTime.UtcNow, "203.0.113.7", "GET", "/", 200, 0, 1);
            await recorder.FlushAsync();
            Assert.EndsWith("Requests lost so far: 2.", recorder.Error);
            Assert.Equal(0, recorder.RowsWritten);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.QueryAsync(new RequestLogQuery { AppPool = "ShopPool", FromUtc = DateTime.UtcNow.AddHours(-1), ToUtc = DateTime.UtcNow }));
            Assert.StartsWith("Could not open", error.Message);
        }

        // The same failure is logged once, not on every attempt.
        lock (logged)
            Assert.Single(logged);
    }

    [Fact]
    public async Task Recorder_drops_a_batch_it_cannot_write_and_recovers()
    {
        var now = DateTime.UtcNow;
        var logged = new List<string>();
        var recorder = new RequestLogRecorder(new RequestLogStore(DatabasePath), (message, _) =>
        {
            lock (logged)
                logged.Add(message);
        });

        var query = new RequestLogQuery
        {
            AppPool = "ShopPool",
            FromUtc = now.AddMinutes(-5),
            ToUtc = now.AddMinutes(5),
            View = RequestLogView.Urls,
        };

        await using (recorder)
        {
            // Not started, so no background purge races with this test; the query opens the database.
            Assert.Empty((await recorder.QueryAsync(query)).Summaries);
            Assert.Null(recorder.Error);

            // Someone removes the table behind the recorder's back.
            using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE requests;";
                drop.ExecuteNonQuery();
            }

            recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "GET", "/lost", 200, 0, 1);
            recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "GET", "/lost", 200, 0, 1);
            recorder.Aggregator.Record("ShopPool", now, "203.0.113.8", "GET", "/lost", 200, 0, 1);
            await recorder.FlushAsync();
            Assert.StartsWith("Writing recorded requests to", recorder.Error);
            Assert.EndsWith("Requests lost so far: 3.", recorder.Error);
            Assert.Equal(0, recorder.RowsWritten);

            // The next write re-creates the table and clears the error.
            recorder.Aggregator.Record("ShopPool", now, "203.0.113.7", "GET", "/kept", 200, 0, 1);
            var report = await recorder.QueryAsync(query);
            Assert.Equal([("/kept", 1L)], report.Summaries.Select(s => (s.Key, s.Hits)));
            Assert.Null(recorder.Error);
            Assert.Equal(1, recorder.RowsWritten);
        }

        lock (logged)
            Assert.StartsWith("Writing recorded requests to", Assert.Single(logged));
    }

    [Fact]
    public async Task Recorder_purges_rows_older_than_the_retention_right_after_start()
    {
        var now = DateTime.UtcNow;
        var store = CreateStore();
        var entries = new List<RequestLogEntry>();
        for (var minute = 0; minute < 60; minute++)
            entries.Add(Entry("OldPool", now.AddDays(-10).AddMinutes(minute), "203.0.113.7", "/", 200, 1, 1));
        entries.Add(Entry("ShopPool", now.AddDays(-5), "203.0.113.7", "/old", 200, 1, 1));
        entries.Add(Entry("ShopPool", now.AddMinutes(-1), "203.0.113.7", "/new", 200, 1, 1));
        store.Write(entries);

        var recorder = new RequestLogRecorder(store) { RetentionDays = 3 };
        await using (recorder)
        {
            recorder.Start();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (ReadPools().Count > 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }

        Assert.Equal(["ShopPool"], ReadPools());
        var report = store.Query(new RequestLogQuery { AppPool = "ShopPool", FromUtc = now.AddDays(-30), ToUtc = now, View = RequestLogView.Urls });
        Assert.Equal(["/new"], report.Summaries.Select(s => s.Key));
        Assert.Null(recorder.Error);
    }

    [Fact]
    public async Task Recorder_keeps_retention_within_limits()
    {
        await using var recorder = new RequestLogRecorder(new RequestLogStore(DatabasePath));
        Assert.Equal(3, recorder.RetentionDays);
        recorder.RetentionDays = 0;
        Assert.Equal(1, recorder.RetentionDays);
        recorder.RetentionDays = 400;
        Assert.Equal(MonitorSettings.MaxRequestLogRetentionDays, recorder.RetentionDays);
    }

    // ---- IP address rules ----

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("  198.51.100.4 ", "198.51.100.4")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("fe80::1%12", "fe80::1")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void Accepts_addresses_that_can_be_blocked(string text, string expected)
    {
        Assert.True(IpAddressRules.TryNormalizeForBlocking(text, out var canonical, out var error));
        Assert.Equal(expected, canonical);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null, "Enter an IP address.")]
    [InlineData("   ", "Enter an IP address.")]
    [InlineData("garbage", "is not an IP address")]
    [InlineData(RequestLogAggregator.UnknownClient, "is not an IP address")]
    [InlineData("10.1", "is not an IP address")]
    [InlineData("1", "is not an IP address")]
    [InlineData("203.0.113.256", "is not an IP address")]
    [InlineData("203.0.113.7%2", "is not an IP address")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.5.6.7", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("::", "unspecified")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("255.255.255.255", "broadcast")]
    public void Rejects_addresses_that_cannot_be_blocked(string? text, string expectedError)
    {
        Assert.False(IpAddressRules.TryNormalizeForBlocking(text, out var canonical, out var error));
        Assert.Equal("", canonical);
        Assert.Contains(expectedError, error);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1%3", true)]
    [InlineData("::ffff:192.168.0.10", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("(unknown)", false)]
    [InlineData("", false)]
    public void Detects_private_addresses(string address, bool expected) =>
        Assert.Equal(expected, IpAddressRules.IsPrivate(address));

    [Fact]
    public void Locations_cover_every_application_of_the_pool()
    {
        var topology = new IisTopology
        {
            Sites =
            [
                new SiteInfo { Id = 1, Name = "Default Web Site", Applications = [new("/", "DefaultAppPool"), new("/api", "ShopPool")] },
                new SiteInfo { Id = 2, Name = "Shop", Applications = [new("/", "ShopPool"), new("/admin/", "ShopPool"), new("/blog", "BlogPool")] },
            ],
        };

        Assert.Equal(["Default Web Site/api", "Shop", "Shop/admin"], IpAddressRules.LocationsForPool(topology, "shoppool"));
        Assert.Equal(["Default Web Site"], IpAddressRules.LocationsForPool(topology, "DefaultAppPool"));
        Assert.Empty(IpAddressRules.LocationsForPool(topology, "NoSuchPool"));
    }

    [Fact]
    public void Describes_locations()
    {
        Assert.Equal("All sites on this server", IpAddressRules.DescribeLocation(""));
        Assert.Equal("Shop/admin", IpAddressRules.DescribeLocation("Shop/admin"));
    }
}
