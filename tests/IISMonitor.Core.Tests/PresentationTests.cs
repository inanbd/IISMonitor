using IISMonitor.Core.Collection;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Presentation;
using Microsoft.Data.Sqlite;

namespace IISMonitor.Core.Tests;

public class PresentationTests
{
    [Theory]
    [InlineData(0, "#2a78d6", SeriesLinePattern.Solid)]
    [InlineData(7, "#e34948", SeriesLinePattern.Solid)]
    [InlineData(8, "#2a78d6", SeriesLinePattern.Dashed)]
    [InlineData(17, "#eb6834", SeriesLinePattern.Dotted)]
    [InlineData(33, "#eb6834", SeriesLinePattern.Solid)]
    public void Slots_past_eight_reuse_colors_with_another_pattern(int slot, string color, SeriesLinePattern pattern) =>
        Assert.Equal(new SeriesStyle(color, pattern), SeriesStyles.ForSlot(slot));

    [Fact]
    public void Slot_registry_keeps_colors_stable_as_pools_come_and_go()
    {
        var registry = new SeriesSlotRegistry();
        Assert.True(registry.Assign(["Shop", "Api", "Admin"]));
        Assert.Equal(0, registry.SlotOf("Admin"));
        Assert.Equal(1, registry.SlotOf("Api"));
        Assert.Equal(2, registry.SlotOf("Shop"));

        // Nothing new: no change, same slots.
        Assert.False(registry.Assign(["Shop", "Admin", "Api"]));

        // Api is deleted; a new pool reuses its slot while the others keep theirs.
        Assert.True(registry.Assign(["Shop", "Admin", "Billing"]));
        Assert.Equal(0, registry.SlotOf("Admin"));
        Assert.Equal(2, registry.SlotOf("Shop"));
        Assert.Equal(1, registry.SlotOf("Billing"));
        Assert.False(registry.Slots.ContainsKey("Api"));
    }

    [Fact]
    public void Slot_registry_honours_saved_slots_and_repairs_duplicates()
    {
        var registry = new SeriesSlotRegistry(new Dictionary<string, int> { ["Shop"] = 5, ["Api"] = 5, ["Gone"] = 0 });
        registry.Assign(["Shop", "Api", "New"]);

        // Shop keeps its saved slot; Api lost the tie and takes the lowest free slot, recycling the
        // deleted pool's; New takes the next one.
        Assert.Equal(5, registry.SlotOf("Shop"));
        Assert.Equal(0, registry.SlotOf("Api"));
        Assert.Equal(1, registry.SlotOf("New"));
        Assert.False(registry.Slots.ContainsKey("Gone"));
    }

    [Fact]
    public void Preferences_round_trip_and_survive_a_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "iismonitor-prefs-" + Guid.NewGuid().ToString("N"), "dashboard.json");
        try
        {
            var store = new DashboardPreferencesStore(path);
            Assert.Empty(store.Load().OverviewHiddenPools);

            Assert.True(store.Save(new DashboardPreferences
            {
                OverviewHiddenPools = ["Admin"],
                OverviewMemoryMetric = "private_bytes",
                PoolStyleSlots = new() { ["Shop"] = 2 },
            }));
            var loaded = store.Load();
            Assert.Equal(["Admin"], loaded.OverviewHiddenPools);
            Assert.Equal("private_bytes", loaded.OverviewMemoryMetric);
            Assert.Equal(2, loaded.PoolStyleSlots["Shop"]);

            File.WriteAllText(path, "{ broken");
            Assert.Equal("working_set", store.Load().OverviewMemoryMetric);

            File.WriteAllText(path, """{ "OverviewHiddenPools": null, "PoolStyleSlots": null, "OverviewMemoryMetric": null }""");
            var nulls = store.Load();
            Assert.Empty(nulls.OverviewHiddenPools);
            Assert.Empty(nulls.PoolStyleSlots);
            Assert.Equal("working_set", nulls.OverviewMemoryMetric);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Network_total_adds_site_traffic_and_outbound_traffic()
    {
        Assert.Null(MetricCatalog.NetworkTotal(new AppPoolMetrics()));
        Assert.Equal(10, MetricCatalog.NetworkTotal(new AppPoolMetrics { HttpBytesSentPerSec = 10 }));
        Assert.Equal(15, MetricCatalog.NetworkTotal(new AppPoolMetrics
        {
            HttpBytesSentPerSec = 1, HttpBytesReceivedPerSec = 2, NetworkSentBytesPerSec = 4, NetworkReceivedBytesPerSec = 8,
        }));
    }

    [Fact]
    public void Site_traffic_is_credited_to_the_root_application_pool()
    {
        var input = new CollectionInput
        {
            TimestampUtc = DateTime.UtcNow,
            Topology = new IisTopology
            {
                AppPools = [new AppPoolInfo { Name = "Shared" }, new AppPoolInfo { Name = "ApiPool" }, new AppPoolInfo { Name = "Idle" }],
                Sites =
                [
                    new SiteInfo { Id = 1, Name = "Shop", Applications = [new("/", "Shared"), new("/api", "ApiPool")] },
                    new SiteInfo { Id = 2, Name = "Blog", Applications = [new("/", "Shared")] },
                ],
            },
            Counters = new IisCounterValues
            {
                Sites = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Shop"] = new SiteCounterValues(1, 1, 1000, 100),
                    ["Blog"] = new SiteCounterValues(1, 1, 500, 50),
                },
            },
        };

        var pools = new SnapshotComposer("WEB01", 2).Compose(input).AppPools.ToDictionary(p => p.Name);

        Assert.Equal(1500, pools["Shared"].HttpBytesSentPerSec);
        Assert.Equal(150, pools["Shared"].HttpBytesReceivedPerSec);
        Assert.Equal(0, pools["ApiPool"].HttpBytesSentPerSec);
        Assert.Equal(1650, MetricCatalog.Extract(pools["Shared"], 1)["net_total"]);
        Assert.Null(new SnapshotComposer("WEB01", 2).Compose(new CollectionInput { Topology = input.Topology }).AppPools[0].HttpBytesSentPerSec);
    }

    [Fact]
    public void Existing_history_database_gains_new_metric_columns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iismonitor-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        try
        {
            // A database from an older version, without the site-traffic columns.
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE pool_history (name TEXT NOT NULL, ts INTEGER NOT NULL, cpu REAL, PRIMARY KEY (name, ts)) WITHOUT ROWID;"
                                     + "INSERT INTO pool_history (name, ts, cpu) VALUES ('Old', 1000, 7);";
                create.ExecuteNonQuery();
            }

            var store = new HistoryStore(path);
            store.Initialize();
            var t = DateTimeOffset.FromUnixTimeMilliseconds(5000).UtcDateTime;
            store.Write([new HistoryRow(EntityKind.AppPool, "Shop", t, new Dictionary<string, double?> { ["http_sent"] = 42, ["net_total"] = 50 })]);

            var result = store.Query(new HistoryQuery { Kind = EntityKind.AppPool, Name = "Shop", FromUtc = t.AddSeconds(-1), ToUtc = t.AddSeconds(1) });
            Assert.Equal(42, result.Series["http_sent"][0]);
            Assert.Equal(50, result.Series["net_total"][0]);

            var old = store.Query(new HistoryQuery
            {
                Kind = EntityKind.AppPool, Name = "Old",
                FromUtc = DateTimeOffset.FromUnixTimeMilliseconds(0).UtcDateTime, ToUtc = t,
            });
            Assert.Equal(7, old.Series["cpu"][0]);
            Assert.Null(old.Series["http_sent"][0]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
