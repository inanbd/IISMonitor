using System.Net;
using IISMonitor.Core.Collection;
using IISMonitor.Core.Requests;

namespace IISMonitor.Core.Tests.Requests;

public class RequestDataTests
{
    private static readonly IReadOnlyList<string> OurNames = HeaderFields.All.Select(f => f.LogFieldName).ToList();

    private static CustomFieldLayout Layout(IReadOnlyList<string>? names = null, int maxLength = 4096)
    {
        names ??= OurNames;
        var known = HeaderFields.All.Where(f => names.Contains(f.LogFieldName)).ToDictionary(f => f.Field, f => f.LogFieldName);
        return new CustomFieldLayout(names, known, maxLength);
    }

    private static string Raw(string requestedWith = "-", string dest = "-", string mode = "-", string type = "-", string disposition = "-", string xff = "-") =>
        $"IISMon-X-Requested-With {requestedWith} IISMon-Sec-Fetch-Dest {dest} IISMon-Sec-Fetch-Mode {mode} " +
        $"IISMon-Resp-Content-Type {type} IISMon-Resp-Content-Disposition {disposition} IISMon-X-Forwarded-For {xff} ";

    [Fact]
    public void Custom_fields_parse_in_order_with_dash_for_missing()
    {
        var values = CustomFieldParser.Parse(Raw(dest: "empty", mode: "cors", type: "application/json;+charset=utf-8", xff: "198.51.100.7"), Layout());
        Assert.Null(values.RequestedWith);
        Assert.Equal("empty", values.FetchDest);
        Assert.Equal("cors", values.FetchMode);
        Assert.Equal("application/json;+charset=utf-8", values.ContentType);
        Assert.Null(values.ContentDisposition);
        Assert.Equal("198.51.100.7", values.ForwardedFor);
        Assert.True(values.ForwardedForReliable);
    }

    [Fact]
    public void A_header_value_naming_the_xff_field_cannot_hide_or_forge_the_visitor()
    {
        // The client sends "X-Requested-With: IISMon-X-Forwarded-For" and a space-laden value with a fake address.
        var raw = Raw(requestedWith: "IISMon-X-Forwarded-For", dest: "x IISMon-X-Forwarded-For 192.0.2.66", xff: "203.0.113.9");
        var values = CustomFieldParser.Parse(raw, Layout());
        Assert.True(values.ForwardedForReliable);
        Assert.Equal("203.0.113.9", values.ForwardedFor);

        // The client's own X-Forwarded-For can hold anything; the proxy's entry is still the right-most one.
        var spoofed = CustomFieldParser.Parse(Raw(xff: "192.0.2.66 IISMon-X-Forwarded-For 192.0.2.67,+203.0.113.9"), Layout());
        var proxies = IpSet.Parse(["10.0.0.0/8"]);
        var who = ClientIpResolver.Resolve("10.1.2.3", spoofed, proxies);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), who.Client);
    }

    [Fact]
    public void Xff_is_not_trusted_unless_it_is_the_last_field_or_when_cut_off()
    {
        var names = OurNames.Append("Admin-Field").ToList();
        var notLast = CustomFieldParser.Parse(Raw(xff: "203.0.113.9") + "Admin-Field value", Layout(names));
        Assert.Equal("203.0.113.9", notLast.ForwardedFor);
        Assert.False(notLast.ForwardedForReliable);

        var longValue = string.Join(",+", Enumerable.Range(0, 400).Select(i => $"192.0.2.{i % 250}"));
        var truncated = CustomFieldParser.Parse(Raw(xff: longValue[..4095]), Layout());
        Assert.False(truncated.ForwardedForReliable);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("192.0.2.66,+203.0.113.9", "203.0.113.9")]              // client-supplied entry on the left
    [InlineData("203.0.113.9,+10.0.0.5", "203.0.113.9")]                // a second trusted proxy hop
    [InlineData("203.0.113.9:51234", "203.0.113.9")]                    // ARR adds the port
    [InlineData("[2001:db8::7]:443", "2001:db8::7")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("unknown", null)]
    [InlineData("-", null)]
    public void Visitor_behind_a_trusted_proxy_comes_from_the_right_of_xff(string xff, string? expected)
    {
        var headers = CustomFieldParser.Parse(Raw(xff: xff), Layout());
        var who = ClientIpResolver.Resolve("10.0.0.4", headers, IpSet.Parse(["10.0.0.0/24"]));
        Assert.True(who.ViaProxy);
        Assert.Equal(expected, who.Client?.ToString());
        Assert.Equal("10.0.0.4", who.Peer!.ToString());
    }

    [Fact]
    public void Xff_from_a_direct_client_is_ignored()
    {
        var headers = CustomFieldParser.Parse(Raw(xff: "192.0.2.66"), Layout());
        var who = ClientIpResolver.Resolve("203.0.113.9", headers, IpSet.Parse(["10.0.0.0/8"]));
        Assert.False(who.ViaProxy);
        Assert.Equal("203.0.113.9", who.Client!.ToString());
        Assert.Equal(default, ClientIpResolver.Resolve("-", headers, IpSet.Empty));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.200.3.4", true)]
    [InlineData("10.0.0.0/255.0.0.0", "10.200.3.4", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("203.0.113.5", "::ffff:203.0.113.5", true)]
    [InlineData("203.0.113.5", "203.0.113.6", false)]
    public void Networks_contain_addresses(string network, string address, bool expected)
    {
        Assert.True(IpNetwork.TryParse(network, out var parsed));
        Assert.Equal(expected, parsed.Contains(IPAddress.Parse(address)));
        Assert.Equal(expected, IpSet.Parse([network]).Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("not an ip")]
    [InlineData("")]
    public void Partial_or_bad_addresses_are_rejected(string text) => Assert.False(IpNetwork.TryParse(text, out _));

    [Fact]
    public void Log_event_parser_reads_the_request_fields_under_traceevent_names()
    {
        KeyValuePair<string, object?>[] payload =
        [
            new("EnabledFieldsFlags", 2149961727u), new("cip", "203.0.113.9"), new("ssitename", "W3SVC2"), new("csmethod", "POST"),
            new("csuristem", "/api/orders"), new("scstatus", (ushort)201), new("scsubstatus", (ushort)0), new("scbytes", 512UL),
            new("timetaken", 37UL), new("csUserAgent", "Mozilla/5.0+(Windows+NT+10.0)"), new("CustomFields", "IISMon-X-Forwarded-For -"),
        ];
        Assert.True(IisLogEventParser.TryParse(payload, out var e));
        Assert.Equal((2L, "/api/orders", 201, 37.0), (e.SiteId!.Value, e.UriStem, e.StatusCode, e.TimeTakenMs));
        Assert.Equal(("203.0.113.9", "POST", 512L), (e.ClientIp, e.Method, e.BytesSent!.Value));
        Assert.Equal("Mozilla/5.0+(Windows+NT+10.0)", e.UserAgent);
        Assert.Equal("IISMon-X-Forwarded-For -", e.CustomFields);

        // Dashed manifest names and "-" for an empty field work too.
        KeyValuePair<string, object?>[] dashed = [new("s-sitename", "W3SVC1"), new("time-taken", 5), new("c-ip", "-"), new("cs(User-Agent)", "curl/8")];
        Assert.True(IisLogEventParser.TryParse(dashed, out var d));
        Assert.Null(d.ClientIp);
        Assert.Equal("curl/8", d.UserAgent);
    }

    [Fact]
    public void Request_log_keeps_the_newest_and_filters()
    {
        var log = new RequestLog(capacity: 3);
        RequestRecord Make(long id, string ip, RequestKind kind, MethodGroup method, string path) =>
            new() { Id = id, ClientIp = ip, Kind = kind, MethodGroup = method, Path = path, SiteId = 1, TimeUtc = DateTime.UtcNow };

        log.Add(id => Make(id, "203.0.113.1", RequestKind.PageView, MethodGroup.Get, "/"));
        log.Add(id => Make(id, "203.0.113.2", RequestKind.XhrApi, MethodGroup.Post, "/api/cart"));
        log.Add(id => Make(id, "198.51.100.3", RequestKind.Static, MethodGroup.Get, "/site.css"));
        log.Add(id => Make(id, "203.0.113.4", RequestKind.XhrApi, MethodGroup.Get, "/api/stock"));

        var all = log.Query(new RequestQuery());
        Assert.Equal([4L, 3L, 2L], all.Requests.Select(r => r.Id));
        Assert.Equal(4, all.LatestId);

        Query(new RequestFilter { Kinds = [RequestKind.XhrApi] }, 4, 2);
        Query(new RequestFilter { Methods = [MethodGroup.Post] }, 2);
        Query(new RequestFilter { Ip = "203.0.113.0/24" }, 4, 2);
        Query(new RequestFilter { Ip = "198.51." }, 3);
        Query(new RequestFilter { PathContains = "STOCK" }, 4);
        Query(new RequestFilter { SiteId = 9 });

        void Query(RequestFilter filter, params long[] expected) =>
            Assert.Equal(expected, log.Query(new RequestQuery { Filter = filter }).Requests.Select(r => r.Id));
    }

    [Fact]
    public void Top_ips_count_by_kind_and_method_in_the_chosen_window()
    {
        var tracker = new IpActivityTracker();
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        tracker.Record("203.0.113.9", false, MethodGroup.Get, RequestKind.PageView, 200, false, now.AddMinutes(-2), "/", "ua");
        tracker.Record("203.0.113.9", false, MethodGroup.Post, RequestKind.XhrApi, 500, false, now.AddMinutes(-1), "/api", "ua");
        tracker.Record("203.0.113.9", false, MethodGroup.Get, RequestKind.Static, 404, true, now.AddMinutes(-30), "/.env", "ua");
        tracker.Record("198.51.100.1", true, MethodGroup.Get, RequestKind.Download, 200, false, now.AddHours(-5), "/a.zip", "bot");

        var five = tracker.Query(new TopIpQuery { Window = TopIpWindow.FiveMinutes }, now);
        var row = Assert.Single(five.Rows);
        Assert.Equal((2, 1, 1, 1, 1, 1), (row.Total, row.Pages, row.Api, row.Get, row.Post, row.ServerErrors));

        var hour = tracker.Query(new TopIpQuery { Window = TopIpWindow.OneHour }, now).Rows.Single();
        Assert.Equal((3, 1, 1), (hour.Total, hour.RuleHits, hour.ClientErrors));

        var day = tracker.Query(new TopIpQuery { Window = TopIpWindow.OneDay }, now);
        Assert.Equal(2, day.Visitors);
        Assert.Equal(["203.0.113.9", "198.51.100.1"], day.Rows.Select(r => r.Ip));
        Assert.True(day.Rows[1].ViaProxy);

        var downloads = tracker.Query(new TopIpQuery { Window = TopIpWindow.OneDay, Filter = new RequestFilter { Kinds = [RequestKind.Download] } }, now);
        Assert.Equal(("198.51.100.1", 1), (Assert.Single(downloads.Rows).Ip, downloads.Rows[0].Matching));

        tracker.Prune(now.AddHours(25));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Tracker_drops_the_longest_unseen_visitors_when_full()
    {
        var tracker = new IpActivityTracker(maxVisitors: 10);
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
            tracker.Record($"192.0.2.{i}", false, MethodGroup.Get, RequestKind.PageView, 200, false, now.AddSeconds(i), "/", null);
        Assert.InRange(tracker.Count, 1, 10);
        Assert.Contains(tracker.Query(new TopIpQuery { Window = TopIpWindow.OneHour }, now.AddMinutes(1)).Rows, r => r.Ip == "192.0.2.24");
    }
}
