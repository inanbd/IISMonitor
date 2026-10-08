using System.Globalization;
using IISMonitor.Core.Presentation;
using IISMonitor.Core.RequestLog;

namespace IISMonitor.Core.Tests;

public class TrafficPresentationTests
{
    [Theory]
    [InlineData(1, "Last 24 hours", 4)]
    [InlineData(0, "Last 24 hours", 4)]
    [InlineData(3, "Last 3 days", 5)]
    [InlineData(5, "Last 5 days", 6)]
    [InlineData(7, "Last 7 days", 6)]
    [InlineData(31, "Last 31 days", 7)]
    [InlineData(400, "Last 31 days", 7)]
    public void Offers_only_ranges_within_the_retention(int retentionDays, string last, int count)
    {
        var ranges = TrafficPresentation.RangesWithin(retentionDays);
        Assert.Equal(count, ranges.Count);
        Assert.Equal("Last 15 minutes", ranges[0].Label);
        Assert.Equal(last, ranges[^1].Label);
        Assert.True(ranges.Zip(ranges.Skip(1)).All(pair => pair.First.Range < pair.Second.Range));
    }

    [Fact]
    public void Adds_the_retention_itself_when_it_is_not_a_standard_range()
    {
        var ranges = TrafficPresentation.RangesWithin(5);
        Assert.Equal(TimeSpan.FromDays(5), ranges[^1].Range);
        Assert.Equal("Last 3 days", ranges[^2].Label);
    }

    [Theory]
    [InlineData(404, 0, "404")]
    [InlineData(401, 1, "401.1")]
    [InlineData(200, 0, "200")]
    [InlineData(500, 19, "500.19")]
    public void Shows_the_substatus_only_when_there_is_one(int status, int subStatus, string expected) =>
        Assert.Equal(expected, TrafficPresentation.StatusText(status, subStatus));

    [Fact]
    public void Status_sort_key_puts_substatuses_between_their_status_and_the_next()
    {
        var keys = new[] { (401, 0), (401, 1), (401, 2), (402, 0), (500, 19) }
            .Select(s => TrafficPresentation.StatusSortKey(s.Item1, s.Item2))
            .ToList();
        Assert.Equal(keys.Order(), keys);
        Assert.Equal(5, keys.Distinct().Count());
    }

    [Fact]
    public void Times_show_the_date_only_when_it_is_not_today() => WithInvariantCulture(() =>
    {
        var now = new DateTime(2026, 10, 8, 18, 0, 0);
        Assert.Equal("14:05:09", TrafficPresentation.FormatTime(new DateTime(2026, 10, 8, 14, 5, 9), now, seconds: true));
        Assert.Equal("14:05", TrafficPresentation.FormatTime(new DateTime(2026, 10, 8, 14, 5, 9), now, seconds: false));
        Assert.Equal("10/07/2026 23:59:59", TrafficPresentation.FormatTime(new DateTime(2026, 10, 7, 23, 59, 59), now, seconds: true));
        Assert.Equal("10/07/2026 23:59", TrafficPresentation.FormatTime(new DateTime(2026, 10, 7, 23, 59, 59), now, seconds: false));
        Assert.Equal("—", TrafficPresentation.FormatTime(0, seconds: true));
    });

    [Fact]
    public void Summary_status_counts_rows_requests_and_database_size() => WithInvariantCulture(() =>
    {
        var report = new RequestLogReport
        {
            View = RequestLogView.Clients,
            TotalRows = 1234,
            TotalHits = 56_789,
            Truncated = true,
            Summaries = Enumerable.Range(0, 1000).Select(_ => new RequestLogSummaryRow()).ToList(),
            DatabaseBytes = 120L * 1024 * 1024,
        };
        Assert.Equal("1,234 IP addresses · 56,789 requests · showing the busiest 1,000 · requests.db 120 MB",
            TrafficPresentation.SummaryStatus(report, null));

        var single = new RequestLogReport { View = RequestLogView.Urls, TotalRows = 1, TotalHits = 1, Summaries = [new()] };
        Assert.Equal("1 URL · 1 request", TrafficPresentation.SummaryStatus(single, ""));
    });

    [Fact]
    public void Summary_status_explains_an_empty_result() => WithInvariantCulture(() =>
    {
        var empty = new RequestLogReport { View = RequestLogView.Urls, DatabaseBytes = 2048 };
        Assert.Equal("No URLs containing \"login\" in this period · requests.db 2.0 KB", TrafficPresentation.SummaryStatus(empty, " login "));
        Assert.Equal("No requests recorded in this period", TrafficPresentation.SummaryStatus(new RequestLogReport(), null));
    });

    [Fact]
    public void Detail_status_says_which_rows_are_shown() => WithInvariantCulture(() =>
    {
        var report = new RequestLogReport
        {
            View = RequestLogView.ClientDetail,
            TotalRows = 2500,
            TotalHits = 9000,
            Truncated = true,
            Details = Enumerable.Range(0, 1000).Select(_ => new RequestLogDetailRow()).ToList(),
        };
        Assert.Equal("2,500 rows · 9,000 requests · showing the latest 1,000", TrafficPresentation.DetailStatus(report, perMinute: true));
        Assert.Equal("2,500 rows · 9,000 requests · showing the busiest 1,000", TrafficPresentation.DetailStatus(report, perMinute: false));
        Assert.Equal("No requests in this period", TrafficPresentation.DetailStatus(new RequestLogReport(), perMinute: true));
    });

    [Fact]
    public void Detail_header_names_the_selected_ip_or_url()
    {
        Assert.Equal("URLs visited by 203.0.113.7", TrafficPresentation.DetailHeader(RequestLogView.Clients, "203.0.113.7"));
        Assert.Equal("IP addresses that visited /login", TrafficPresentation.DetailHeader(RequestLogView.Urls, "/login"));
    }

    [Theory]
    [InlineData("203.0.113.7", null, "203.0.113.7", true)]
    [InlineData("203.0.113.7", null, "203.0.113.8", false)]
    [InlineData("203.0.113.0", "255.255.255.0", "203.0.113.200", true)]
    [InlineData("203.0.113.0", "255.255.255.0", "203.0.114.1", false)]
    [InlineData("203.0.113.0", "24", "203.0.113.99", true)]
    [InlineData("203.0.112.0", "23", "203.0.113.99", true)]
    [InlineData("203.0.112.0", "23", "203.0.114.1", false)]
    [InlineData("2001:db8::", "64", "2001:db8::1234", true)]
    [InlineData("2001:db8::", "64", "2001:db8:0:1::1", false)]
    [InlineData("2001:db8::1", null, "2001:0db8:0000::0001", true)]
    [InlineData("203.0.113.7", null, "::ffff:203.0.113.7", true)]
    [InlineData("203.0.113.7", null, "2001:db8::1", false)]
    [InlineData("203.0.113.0", "not a mask", "203.0.113.7", false)]
    [InlineData("203.0.113.0", "33", "203.0.113.7", false)]
    [InlineData("203.0.113.7", null, "(unknown)", false)]
    public void Finds_whether_a_deny_entry_covers_an_address(string blocked, string? mask, string address, bool expected) =>
        Assert.Equal(expected, TrafficPresentation.Covers(new BlockedIp { IpAddress = blocked, SubnetMask = mask }, address));

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("(unknown)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_ip_addresses(string? text, bool expected) =>
        Assert.Equal(expected, TrafficPresentation.IsIpAddress(text));

    [Fact]
    public void Blocked_text_lists_each_place_the_address_is_refused()
    {
        List<BlockedIp> blocked =
        [
            new() { IpAddress = "203.0.113.7", Location = "" },
            new() { IpAddress = "203.0.113.0", SubnetMask = "255.255.255.0", Location = "Shop" },
            new() { IpAddress = "203.0.113.7", Location = "Shop" },
            new() { IpAddress = "198.51.100.1", Location = "Shop/api" },
        ];

        Assert.Equal("All sites on this server, Shop", TrafficPresentation.BlockedText(blocked, "203.0.113.7"));
        Assert.Equal("Shop", TrafficPresentation.BlockedText(blocked, "203.0.113.8"));
        Assert.Equal("", TrafficPresentation.BlockedText(blocked, "192.0.2.1"));
    }

    private static void WithInvariantCulture(Action test)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            test();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
