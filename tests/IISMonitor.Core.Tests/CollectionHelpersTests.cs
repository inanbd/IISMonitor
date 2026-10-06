using IISMonitor.Core.Collection;

namespace IISMonitor.Core.Tests;

public class CollectionHelpersTests
{
    [Fact]
    public void Process_tree_ignores_children_older_than_their_parent()
    {
        ProcessEntry[] processes =
        [
            new(10, 1, "w3wp.exe", 1),
            new(11, 10, "php-cgi.exe", 1),
            new(12, 10, "stale.exe", 1),
            new(13, 11, "grandchild.exe", 1),
        ];
        var created = new Dictionary<int, long> { [10] = 100, [11] = 200, [12] = 50, [13] = 300 };

        var members = ProcessTree.ResolvePoolMembers(
            processes, new Dictionary<int, string> { [10] = "Pool" }, pid => created.TryGetValue(pid, out var t) ? t : null);

        Assert.Equal([10, 11, 13], members.Keys.Order());
        Assert.True(members[10].IsWorkerProcess);
        Assert.False(members[13].IsWorkerProcess);
    }

    [Fact]
    public void Process_tree_skips_dead_worker_processes()
    {
        var members = ProcessTree.ResolvePoolMembers([new ProcessEntry(5, 1, "x.exe", 1)], new Dictionary<int, string> { [99] = "Pool" });
        Assert.Empty(members);
    }

    [Theory]
    [InlineData("Shop (Live)", "Shop [Live]")]
    [InlineData("A#1/B\\C", "A_1_B_C")]
    [InlineData("Plain", "Plain")]
    public void Mangles_instance_names_like_perflib(string name, string expected) =>
        Assert.Equal(expected, PerfInstanceName.Mangle(name));

    [Theory]
    [InlineData("1234_DefaultAppPool", 1234, "DefaultAppPool")]
    [InlineData("42_My_Pool", 42, "My_Pool")]
    public void Parses_worker_instance_names(string instance, int pid, string pool)
    {
        Assert.True(PerfInstanceName.TryParseWorkerInstance(instance, out var parsedPid, out var parsedPool));
        Assert.Equal(pid, parsedPid);
        Assert.Equal(pool, parsedPool);
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("abc_Pool")]
    [InlineData("12_")]
    public void Rejects_non_worker_instance_names(string instance) =>
        Assert.False(PerfInstanceName.TryParseWorkerInstance(instance, out _, out _));

    [Fact]
    public void Resolves_mangled_pool_names()
    {
        string[] pools = ["Shop (Live)", "Other"];
        Assert.Equal("Shop (Live)", PerfInstanceName.ResolvePoolName("Shop [Live]", pools));
        Assert.Equal("Other", PerfInstanceName.ResolvePoolName("other", pools));
        Assert.Null(PerfInstanceName.ResolvePoolName("Missing", pools));
    }

    [Fact]
    public void Matches_requests_to_the_longest_application_path()
    {
        var matcher = new ApplicationMatcher(
        [
            new SiteInfo
            {
                Id = 3, Name = "Shop",
                Applications = [new("/", "Root"), new("/api", "Api"), new("/api/v2", "ApiV2")],
            },
        ]);

        Assert.Equal("ApiV2", matcher.ResolveAppPool(3, "/api/v2/orders"));
        Assert.Equal("Api", matcher.ResolveAppPool(3, "/API/v1"));
        Assert.Equal("Api", matcher.ResolveAppPool(3, "/api"));
        Assert.Equal("Root", matcher.ResolveAppPool(3, "/apis"));
        Assert.Equal("Root", matcher.ResolveAppPool(3, "/"));
        Assert.Null(matcher.ResolveAppPool(4, "/"));
        Assert.Equal(3, matcher.ResolveSiteId(new IisLogEvent(null, "shop", "/", 200, 1)));
        Assert.Null(matcher.ResolveSiteId(new IisLogEvent(9, null, "/", 200, 1)));
    }

    [Fact]
    public void Parses_iis_log_event_payloads()
    {
        var payload = new Dictionary<string, object?>
        {
            ["date"] = "2026-01-01",
            ["s-sitename"] = "W3SVC12",
            ["cs-uri-stem"] = "/api/orders",
            ["sc-status"] = "503",
            ["time-taken"] = "250",
        };

        Assert.True(IisLogEventParser.TryParse(payload, out var e));
        Assert.Equal(12, e.SiteId);
        Assert.Equal("/api/orders", e.UriStem);
        Assert.Equal(503, e.StatusCode);
        Assert.Equal(250, e.TimeTakenMs);
    }

    [Fact]
    public void Parses_numeric_payload_values_and_odd_field_names()
    {
        var payload = new Dictionary<string, object?>
        {
            ["S_SiteName"] = "Default Web Site",
            ["sc_status"] = (ushort)200,
            ["time_taken"] = 15u,
        };

        Assert.True(IisLogEventParser.TryParse(payload, out var e));
        Assert.Null(e.SiteId);
        Assert.Equal("Default Web Site", e.SiteName);
        Assert.Equal("/", e.UriStem);
        Assert.Equal(15, e.TimeTakenMs);
    }

    [Fact]
    public void Rejects_events_without_site_or_time()
    {
        Assert.False(IisLogEventParser.TryParse(new Dictionary<string, object?> { ["time-taken"] = "5" }, out _));
        Assert.False(IisLogEventParser.TryParse(new Dictionary<string, object?> { ["s-sitename"] = "W3SVC1", ["time-taken"] = "" }, out _));
    }

    [Fact]
    public void Duration_accumulator_reports_exact_stats_below_reservoir_size()
    {
        var acc = new DurationAccumulator(reservoirSize: 1000);
        for (var i = 1; i <= 100; i++)
            acc.Add(i, i % 10 == 0 ? 500 : i % 7 == 0 ? 404 : 200);

        var stats = acc.ToStats();
        Assert.Equal(100, stats.RequestCount);
        Assert.Equal(50.5, stats.AverageMs, 6);
        Assert.Equal(95, stats.P95Ms);
        Assert.Equal(100, stats.MaxMs);
        Assert.Equal(10, stats.ServerErrors);
        Assert.Equal(13, stats.ClientErrors);
    }

    [Fact]
    public void Duration_accumulator_estimates_percentile_beyond_reservoir()
    {
        var acc = new DurationAccumulator(reservoirSize: 2000, random: new Random(1));
        for (var i = 0; i < 100_000; i++)
            acc.Add(i % 1000, 200);

        var stats = acc.ToStats();
        Assert.Equal(100_000, stats.RequestCount);
        Assert.Equal(999, stats.MaxMs);
        Assert.InRange(stats.P95Ms, 930, 970);
    }

    [Fact]
    public void Response_aggregator_keeps_live_and_history_windows_separate()
    {
        var agg = new ResponseTimeAggregator();
        agg.Record(1, "Pool", 10, 200);
        agg.Record(1, "Pool", 30, 500);

        var live = agg.DrainLive();
        Assert.Equal(2, live.BySite[1].RequestCount);
        Assert.Equal(20, live.ByPool["pool"].AverageMs);
        Assert.Empty(agg.DrainLive().BySite);

        agg.Record(2, null, 5, 200);
        var history = agg.DrainHistory();
        Assert.Equal(2, history.BySite[1].RequestCount);
        Assert.Equal(1, history.BySite[2].RequestCount);
        Assert.Equal(1, history.ByPool["Pool"].ServerErrors);
    }
}
