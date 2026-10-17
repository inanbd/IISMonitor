using System.Net;
using IISMonitor.Core.Blocking;
using IISMonitor.Core.Collection;
using IISMonitor.Core.Presentation;
using IISMonitor.Core.Requests;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Tests.Requests;

public sealed class BlacklistTests : IDisposable
{
    // Recent, on a whole minute: the Top IPs lookup the pipeline does is relative to the clock.
    private static readonly DateTime T0 = WholeMinute(DateTime.UtcNow);
    private static readonly IPAddress Visitor = IPAddress.Parse("203.0.113.9");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iismonitor-blocks-" + Guid.NewGuid().ToString("N"));

    private static DateTime WholeMinute(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private static BlacklistEngine Engine(BlockingMode mode = BlockingMode.Block, params BlockRule[] rules)
    {
        var engine = new BlacklistEngine();
        engine.Configure(new BlockingSettings
        {
            Mode = mode,
            Rules = rules.Length > 0 ? [.. rules] : [new BlockRule { Id = "wp", Name = "WordPress login", Pattern = "/wp-login.php", Hits = 3, WithinMinutes = 10, BlockHours = 24 }],
            AllowList = ["198.51.100.0/24"],
            TrustedProxies = ["10.0.0.0/8"],
        }.Normalize(), [IPAddress.Parse("192.0.2.1")]);
        return engine;
    }

    [Theory]
    [InlineData("/wp-login.php", "/WP-Login.PHP", true)]
    [InlineData("/wp-login.php", "/blog/wp-login.php", false)]
    [InlineData("*/wp-login.php", "/blog/wp-login.php", true)]
    [InlineData("/.git/*", "/.git/config", true)]
    [InlineData("/.git/*", "/.github/x", false)]
    [InlineData("*.env", "/app/.env", true)]
    [InlineData("/admin?.php", "/admin1.php", true)]
    [InlineData("/a*b*c", "/aXXbYYc", true)]
    [InlineData("/a*b*c", "/aXXbYY", false)]
    public void Url_patterns_are_case_insensitive_globs(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new UrlPattern(pattern).IsMatch(path));

    [Fact]
    public void A_rule_can_list_several_patterns()
    {
        var pattern = new UrlPattern("wp-login.php; /.git/* ;*.env");
        Assert.True(pattern.IsMatch("/WP-LOGIN.PHP"));
        Assert.True(pattern.IsMatch("/.git/HEAD"));
        Assert.True(pattern.IsMatch("/api/.env"));
        Assert.False(pattern.IsMatch("/"));
        Assert.Equal("/wp-login.php; /.git/*; *.env", new BlockRule { Pattern = "wp-login.php; /.git/* ;*.env" }.Normalize().Pattern);
        Assert.Equal(["/*"], UrlPattern.TooBroad("/.env; /*"));
        Assert.False(new BlockRule { Pattern = "/.env; /*" }.Normalize().Enabled);

        var scanner = BlockingSettings.ScannerRule().Normalize();
        Assert.True(scanner.Enabled);
        Assert.True(new UrlPattern(scanner.Pattern).IsMatch("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php"));
        Assert.False(new UrlPattern(scanner.Pattern).IsMatch("/products/42"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("/*")]
    [InlineData("/*/*")]
    [InlineData("/i*")]
    [InlineData("*.html")]
    public void Patterns_that_would_catch_ordinary_visitors_are_refused(string pattern)
    {
        Assert.True(UrlPattern.IsTooBroad(pattern));
        Assert.False(new BlockRule { Pattern = pattern }.Normalize().Enabled);
    }

    [Fact]
    public void Rule_trips_after_the_hit_count_within_the_window()
    {
        var engine = Engine();
        Assert.Null(engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0));
        Assert.Null(engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0.AddMinutes(4)));
        Assert.Null(engine.Observe(Visitor, false, "/index.html", "Shop", 200, 0, T0.AddMinutes(5)));

        // The first hit has left the 10-minute window by now: still only two.
        Assert.Null(engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0.AddMinutes(11)));
        var decision = engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0.AddMinutes(12));

        Assert.NotNull(decision);
        Assert.Equal(("203.0.113.9", "WordPress login", 3, false, false), (decision.Ip, decision.RuleName, decision.Hits, decision.ViaProxy, decision.WatchOnly));
        Assert.Equal(T0.AddMinutes(12).AddHours(24), decision.ExpiresUtc);

        // Blocked now: further hits don't decide again until it is unblocked.
        Assert.Null(engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0.AddMinutes(13)));
        engine.MarkUnblocked(decision.Ip);
        Assert.Null(engine.Observe(Visitor, false, "/wp-login.php", "Shop", 404, 0, T0.AddMinutes(14)));
    }

    [Fact]
    public void Protected_addresses_and_iis_denials_never_count()
    {
        var engine = Engine(rules: new BlockRule { Id = "env", Pattern = "/.env", Hits = 1, BlockHours = 0 });
        foreach (var ip in new[] { "127.0.0.1", "::1", "198.51.100.40", "10.1.2.3", "192.0.2.1", "fe80::1" })
            Assert.Null(engine.Observe(IPAddress.Parse(ip), false, "/.env", null, 404, 0, T0));
        Assert.Null(engine.Observe(Visitor, false, "/.env", null, 403, 503, T0));
        Assert.Null(engine.Observe(null, true, "/.env", null, 404, 0, T0));

        var decision = engine.Observe(Visitor, true, "/.env", null, 404, 0, T0);
        Assert.NotNull(decision);
        Assert.True(decision.ViaProxy);
        Assert.Null(decision.ExpiresUtc);
    }

    [Fact]
    public void Private_addresses_behind_a_proxy_are_internal_hops_and_never_blocked()
    {
        var engine = Engine(rules: new BlockRule { Id = "env", Pattern = "/.env", Hits = 1 });
        Assert.Null(engine.Observe(IPAddress.Parse("192.168.1.20"), true, "/.env", null, 404, 0, T0));
        Assert.Null(engine.Observe(IPAddress.Parse("fd00::5"), true, "/.env", null, 404, 0, T0));
        Assert.NotNull(engine.Observe(IPAddress.Parse("192.168.1.20"), false, "/.env", null, 404, 0, T0));
        Assert.True(IpText.IsPrivate(IPAddress.Parse("100.64.0.1")));
        Assert.False(IpText.IsPrivate(IPAddress.Parse("172.32.0.1")));
    }

    [Fact]
    public void Site_filter_watch_mode_and_off()
    {
        var rule = new BlockRule { Id = "x", Pattern = "/xmlrpc.php", Hits = 1, Sites = ["Blog"] };
        var engine = Engine(BlockingMode.Watch, rule);
        Assert.Null(engine.Observe(Visitor, false, "/xmlrpc.php", "Shop", 404, 0, T0));
        Assert.True(engine.Observe(Visitor, false, "/xmlrpc.php", "blog", 404, 0, T0)!.WatchOnly);

        var off = Engine(BlockingMode.Off, rule);
        Assert.Null(off.MatchRule("/xmlrpc.php", "Blog"));
        Assert.Null(off.Observe(Visitor, false, "/xmlrpc.php", "Blog", 404, 0, T0));
    }

    [Fact]
    public void Pipeline_logs_counts_and_blocks_from_log_events()
    {
        var decisions = new List<BlockDecision>();
        var responses = new ResponseTimeAggregator();
        var pipeline = new RequestPipeline(responses, decisions.Add);
        pipeline.Blacklist.Configure(new BlockingSettings
        {
            Mode = BlockingMode.Block,
            Rules = [new BlockRule { Id = "env", Name = "dotenv", Pattern = "/.env", Hits = 2 }],
            TrustedProxies = ["10.0.0.0/8"],
        }.Normalize(), []);
        pipeline.SetTrustedProxies(IpSet.Parse(["10.0.0.0/8"]));
        var names = HeaderFields.All.Select(f => f.LogFieldName).ToList();
        pipeline.UpdateSites([new SiteInfo
        {
            Id = 2, Name = "Shop", Applications = [new ApplicationInfo("/", "ShopPool")],
            Layout = new CustomFieldLayout(names, HeaderFields.All.ToDictionary(f => f.Field, f => f.LogFieldName), 4096),
        }]);

        IisLogEvent Event(string ip, string path, int status, string? xff = null, string dest = "-") =>
            new(2, null, path, status, 12)
            {
                ClientIp = ip, Method = "GET", UserAgent = "Mozilla/5.0+(X11)", TimeUtc = T0,
                CustomFields = $"IISMon-X-Requested-With - IISMon-Sec-Fetch-Dest {dest} IISMon-Sec-Fetch-Mode - IISMon-Resp-Content-Type - IISMon-Resp-Content-Disposition - IISMon-X-Forwarded-For {xff ?? "-"}",
            };

        pipeline.Process(Event("203.0.113.9", "/", 200, dest: "document"));
        pipeline.Process(Event("10.0.0.5", "/.env", 404, xff: "198.51.100.77"));
        pipeline.Process(Event("10.0.0.5", "/.env", 404, xff: "198.51.100.77"));
        pipeline.Process(Event("10.0.0.5", "/.env", 404));   // proxy without X-Forwarded-For: nobody to count

        var decision = Assert.Single(decisions);
        Assert.Equal(("198.51.100.77", true, "dotenv"), (decision.Ip, decision.ViaProxy, decision.RuleName));

        var page = pipeline.Log.Query(new RequestQuery());
        Assert.Equal(4, page.Requests.Count);
        var first = page.Requests[^1];
        Assert.Equal(("203.0.113.9", RequestKind.PageView, "Shop", "ShopPool", "Mozilla/5.0 (X11)"), (first.ClientIp, first.Kind, first.Site, first.AppPool, first.UserAgent));
        var proxied = page.Requests[1];
        Assert.Equal(("198.51.100.77", "10.0.0.5", true, "dotenv"), (proxied.ClientIp, proxied.PeerIp, proxied.ViaProxy, proxied.Rule));
        Assert.Null(page.Requests[0].ClientIp);

        var activity = pipeline.DrainActivity(2);
        Assert.Equal(1, activity.Unattributed);
        Assert.Equal(0.5, activity.PerSecondByKind[RequestKind.PageView]);
        Assert.Equal(2, activity.PerSecondByMethod[MethodGroup.Get]);
        Assert.Equal(4, responses.DrainLive().BySite[2].RequestCount);
        Assert.True(pipeline.WasSeenViaProxy("198.51.100.77"));
    }

    [Fact]
    public void Block_store_keeps_current_blocks_and_purges_ended_ones()
    {
        Directory.CreateDirectory(_directory);
        var store = new BlockStore(Path.Combine(_directory, "history.db"));
        store.Initialize();
        var active = new BlockEntry { Ip = "203.0.113.9", Method = BlockMethod.Firewall, State = BlockState.Active, Reason = "dotenv", Hits = 2, CreatedUtc = T0, ExpiresUtc = T0.AddHours(24) };
        var ended = new BlockEntry { Ip = "198.51.100.7", Method = BlockMethod.Iis, State = BlockState.Pending, Reason = "manual", Manual = true, CreatedUtc = T0 };
        store.Insert(active);
        store.Insert(ended);
        ended.State = BlockState.Lifted;
        ended.EndedUtc = T0.AddHours(1);
        ended.Message = "by hand";
        store.Update(ended);

        var current = Assert.Single(store.Current());
        Assert.Equal(("203.0.113.9", BlockState.Active, T0.AddHours(24)), (current.Ip, current.State, current.ExpiresUtc!.Value));
        var all = store.List(new BlockListQuery { IncludeEnded = true });
        Assert.Equal(["203.0.113.9", "198.51.100.7"], all.Select(e => e.Ip));
        Assert.Equal(("by hand", true), (all[1].Message, all[1].Manual));

        Assert.Equal(1, store.Purge(T0.AddDays(2)));
        Assert.Single(store.List(new BlockListQuery { IncludeEnded = true }));
    }

    [Fact]
    public void Blocking_settings_are_cleaned_and_survive_a_round_trip()
    {
        var settings = new MonitorSettings
        {
            Blocking = new BlockingSettings
            {
                Mode = BlockingMode.Block,
                Rules = [new BlockRule { Pattern = "wp-login.php", Hits = 0, WithinMinutes = double.NaN, BlockHours = -1 }],
                AllowList = ["198.51.100.0/24", "garbage", "203.0.113.5"],
                TrustedProxies = ["10.0.0.0/255.0.0.0"],
                IisApplyIntervalSeconds = 1,
            },
        }.Normalize();

        var blocking = settings.Clone().Blocking!;
        var rule = Assert.Single(blocking.Rules);
        Assert.Equal(("/wp-login.php", "/wp-login.php", 1, 10.0, 0.0, true), (rule.Pattern, rule.Name, rule.Hits, rule.WithinMinutes, rule.BlockHours, rule.Enabled));
        Assert.Equal(["198.51.100.0/24", "203.0.113.5"], blocking.AllowList);
        Assert.Equal(["10.0.0.0/8"], blocking.TrustedProxies);
        Assert.Equal(30, blocking.IisApplyIntervalSeconds);
        Assert.Equal(["garbage"], BlockingSettings.InvalidNetworks(["garbage", "10.0.0.1"]));

        // Settings without a blacklist (an older dashboard) normalize to "off", not to an error.
        Assert.Equal(BlockingMode.Off, new MonitorSettings { Blocking = null }.Normalize().Blocking!.Mode);
    }
}

public class RequestsPresentationTests
{
    [Fact]
    public void Block_entries_read_well()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var entry = new BlockEntry { Ip = "203.0.113.9", Method = BlockMethod.Iis, State = BlockState.Pending, ExpiresUtc = now.AddMinutes(30) };
        Assert.Equal("Waiting for the next IIS update", RequestsPresentation.State(entry));
        Assert.Equal("in 30 min", RequestsPresentation.Expires(entry, now));
        entry.ExpiresUtc = now.AddHours(23.6);
        Assert.Equal("in 24 h", RequestsPresentation.Expires(entry, now));
        entry.ExpiresUtc = null;
        Assert.Equal("never (until lifted)", RequestsPresentation.Expires(entry, now));
        entry.State = BlockState.Failed;
        entry.Message = "IIS: locked";
        Assert.Equal("Not blocked: IIS: locked", RequestsPresentation.State(entry));
        Assert.Equal("198.51.100.7 (via 10.0.0.5)", RequestsPresentation.Client(new RequestRecord { ClientIp = "198.51.100.7", PeerIp = "10.0.0.5", ViaProxy = true }));
        Assert.Equal("unknown (via 10.0.0.5)", RequestsPresentation.Client(new RequestRecord { PeerIp = "10.0.0.5", ViaProxy = true }));
    }

    [Fact]
    public void Drafts_with_bad_networks_or_broad_patterns_are_refused()
    {
        var (errors, warnings) = RequestsPresentation.Validate(new BlockingSettings
        {
            Mode = BlockingMode.Block,
            AllowList = ["office"],
            TrustedProxies = ["10.0.0.0/8"],
            Rules = [new BlockRule { Name = "All", Pattern = "/*", Hits = 1 }, new BlockRule { Name = "Env", Pattern = "/.env", Hits = 1 }],
        });
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("\"office\""));
        Assert.Contains(errors, e => e.StartsWith("All:"));
        Assert.Contains(warnings, w => w.StartsWith("Env:"));

        var ok = RequestsPresentation.Validate(new BlockingSettings { Mode = BlockingMode.Watch, Rules = [BlockingSettings.ScannerRule()] });
        Assert.Empty(ok.Errors);
    }
}
