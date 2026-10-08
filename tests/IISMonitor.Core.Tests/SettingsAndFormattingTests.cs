using IISMonitor.Core.Metrics;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Tests;

public class SettingsAndFormattingTests
{
    [Fact]
    public void Normalize_clamps_and_cleans_values()
    {
        var settings = new MonitorSettings
        {
            SampleIntervalMs = 10,
            HistoryIntervalSeconds = 1,
            HistoryRetentionDays = 400,
            SqlServerPorts = [1433, 0, 70000, 1433, 50123],
            SqlServerConnectionStrings = ["  Server=.;Integrated Security=true  ", "", "Server=.;Integrated Security=true"],
        }.Normalize();

        Assert.Equal(MonitorSettings.MinSampleIntervalMs, settings.SampleIntervalMs);
        Assert.Equal(MonitorSettings.MinHistoryIntervalSeconds, settings.HistoryIntervalSeconds);
        Assert.Equal(MonitorSettings.MaxRetentionDays, settings.HistoryRetentionDays);
        Assert.Equal([1433, 50123], settings.SqlServerPorts);
        Assert.Equal(["Server=.;Integrated Security=true"], settings.SqlServerConnectionStrings);
    }

    [Fact]
    public void Normalize_cleans_request_tracking_settings()
    {
        var defaults = new MonitorSettings().Normalize();
        Assert.Empty(defaults.RequestTrackingPools);
        Assert.Equal(3, defaults.RequestLogRetentionDays);

        var settings = new MonitorSettings
        {
            RequestTrackingPools = ["  ShopPool ", "", "shoppool", "ApiPool", null!, "   "],
            RequestLogRetentionDays = 0,
        }.Normalize();
        Assert.Equal(["ApiPool", "ShopPool"], settings.RequestTrackingPools);
        Assert.Equal(1, settings.RequestLogRetentionDays);

        var clamped = new MonitorSettings { RequestTrackingPools = null!, RequestLogRetentionDays = 400 }.Normalize();
        Assert.Empty(clamped.RequestTrackingPools);
        Assert.Equal(MonitorSettings.MaxRequestLogRetentionDays, clamped.RequestLogRetentionDays);
    }

    [Fact]
    public void Settings_saved_before_request_tracking_load_with_its_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "iismonitor-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "sampleIntervalMs": 2000, "enableResponseTimeTracing": true }""");
            var loaded = new SettingsStore(path).Load();
            Assert.Equal(2000, loaded.SampleIntervalMs);
            Assert.Empty(loaded.RequestTrackingPools);
            Assert.Equal(3, loaded.RequestLogRetentionDays);

            new SettingsStore(path).Save(new MonitorSettings { RequestTrackingPools = ["ShopPool"], RequestLogRetentionDays = 7 });
            var saved = new SettingsStore(path).Load();
            Assert.Equal(["ShopPool"], saved.RequestTrackingPools);
            Assert.Equal(7, saved.RequestLogRetentionDays);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_interval_is_never_finer_than_the_sample_interval()
    {
        var settings = new MonitorSettings { SampleIntervalMs = 30_000, HistoryIntervalSeconds = 10 }.Normalize();
        Assert.Equal(30, settings.HistoryIntervalSeconds);
    }

    [Fact]
    public void Settings_store_round_trips_and_survives_corrupt_files()
    {
        var path = Path.Combine(Path.GetTempPath(), "iismonitor-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal(1000, store.Load().SampleIntervalMs);

            store.Save(new MonitorSettings { SampleIntervalMs = 2000, SqlServerPorts = [1433, 1500] });
            var loaded = store.Load();
            Assert.Equal(2000, loaded.SampleIntervalMs);
            Assert.Equal([1433, 1500], loaded.SqlServerPorts);

            File.WriteAllText(path, "{ not json");
            Assert.Equal(1000, store.Load().SampleIntervalMs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(MetricUnit.Bytes, 512d, "512 B")]
    [InlineData(MetricUnit.Bytes, 1536d, "1.5 KB")]
    [InlineData(MetricUnit.BytesPerSecond, 250d * 1024 * 1024, "250 MB/s")]
    [InlineData(MetricUnit.Milliseconds, 42.4, "42 ms")]
    [InlineData(MetricUnit.Milliseconds, 12_500d, "12.5 s")]
    [InlineData(MetricUnit.Percent, 3.14159, "3.1 %")]
    [InlineData(MetricUnit.Count, 7d, "7")]
    [InlineData(MetricUnit.PerSecond, 2.25, "2.3/s")]
    public void Formats_values(MetricUnit unit, double value, string expected)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, MetricFormatter.Format(unit, value));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Formats_missing_values_as_a_dash() =>
        Assert.Equal(MetricFormatter.Missing, MetricFormatter.Format(MetricUnit.Count, null));

    [Fact]
    public void Every_chart_metric_exists_in_the_catalog()
    {
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            var keys = MetricCatalog.Metrics(kind).Select(m => m.Key).ToHashSet();
            Assert.Equal(keys.Count, MetricCatalog.Metrics(kind).Count);
            foreach (var chart in MetricCatalog.Charts(kind))
                Assert.All(chart.MetricKeys, key => Assert.Contains(key, keys));
        }
    }
}
