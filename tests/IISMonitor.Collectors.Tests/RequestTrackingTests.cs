using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IISMonitor.Collectors.Iis;
using IISMonitor.Core.Collection;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Collectors.Tests;

public class RequestTrackingTests
{
    // IIS's default logExtFileFlags: everything but SiteName, ComputerName, BytesSent/Recv, Cookie, ProtocolVersion and Host.
    private const string IisDefaultFields =
        "Date, Time, ClientIP, UserName, ServerIP, Method, UriStem, UriQuery, HttpStatus, Win32Status, TimeTaken, ServerPort, UserAgent, Referer, HttpSubStatus";

    [Fact]
    public void Tracking_fields_match_the_iis_schema_names()
    {
        Assert.Equal(4, IisLoggingConfig.FlagClientIp);
        Assert.Equal(128, IisLoggingConfig.FlagMethod);
        Assert.Equal(2097152, IisLoggingConfig.FlagHttpSubStatus);
        Assert.Equal(
            IisLoggingConfig.TrackingFields,
            IisLoggingConfig.Flags("ClientIP,Method,HttpSubStatus", IisLoggingConfig.FieldNames));
        Assert.Equal(
            IisLoggingConfig.RequiredFields,
            IisLoggingConfig.Flags("SiteName, UriStem, HttpStatus, TimeTaken", IisLoggingConfig.FieldNames));
    }

    [Theory]
    [InlineData(IisDefaultFields)]
    [InlineData("2478031")]
    [InlineData(2478031)]
    public void Iis_default_fields_include_the_tracking_fields_but_not_the_site_name(object value)
    {
        var fields = IisLoggingConfig.Flags(value, IisLoggingConfig.FieldNames);

        Assert.Equal(IisLoggingConfig.TrackingFields, fields & IisLoggingConfig.TrackingFields);
        Assert.Equal(IisLoggingConfig.FlagSiteName, IisLoggingConfig.MissingFields(fields));
    }

    [Fact]
    public void Nothing_is_missing_once_required_and_tracking_fields_are_logged()
    {
        Assert.Equal(0, IisLoggingConfig.MissingFields(IisLoggingConfig.RequiredFields | IisLoggingConfig.TrackingFields | 1 | 2));
        Assert.Equal(IisLoggingConfig.TrackingFields, IisLoggingConfig.MissingFields(IisLoggingConfig.RequiredFields));
    }

    [Theory]
    [InlineData(IisLoggingConfig.TrackingFields, "added the client IP, method and substatus log fields")]
    [InlineData(IisLoggingConfig.FlagHttpSubStatus, "added the substatus log field")]
    [InlineData(IisLoggingConfig.FlagSiteName | IisLoggingConfig.FlagClientIp, "added the site name and client IP log fields")]
    [InlineData(IisLoggingConfig.RequiredFields, "added the site name, URL, status and time taken log fields")]
    public void Names_the_log_fields_it_adds(long added, string expected)
    {
        Assert.Equal(expected, IisLoggingConfig.AddedFieldsMessage(added));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData(" ::ffff:203.0.113.7 ", "203.0.113.7")]
    [InlineData("fe80::1%12", "fe80::1")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:db8:0:0:0:0:0:1", "2001:db8::1")]
    public void Parses_equal_addresses_as_equal(string text, string expected)
    {
        Assert.True(IpRestrictionConfig.TryParseAddress(text, out var address));
        Assert.Equal(IPAddress.Parse(expected), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("203.0.113.300")]
    public void Rejects_text_that_is_not_an_address(string? text)
    {
        Assert.False(IpRestrictionConfig.TryParseAddress(text, out _));
    }

    [Theory]
    [InlineData(null, AddressFamily.InterNetwork, true)]
    [InlineData("", AddressFamily.InterNetwork, true)]
    [InlineData("255.255.255.255", AddressFamily.InterNetwork, true)]
    [InlineData("255.255.255.255", AddressFamily.InterNetworkV6, true)]
    [InlineData("32", AddressFamily.InterNetwork, true)]
    [InlineData("128", AddressFamily.InterNetworkV6, true)]
    [InlineData("/128", AddressFamily.InterNetworkV6, true)]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", AddressFamily.InterNetworkV6, true)]
    [InlineData("255.255.255.0", AddressFamily.InterNetwork, false)]
    [InlineData("24", AddressFamily.InterNetwork, false)]
    [InlineData("64", AddressFamily.InterNetworkV6, false)]
    [InlineData("ffff:ffff:ffff:ffff::", AddressFamily.InterNetworkV6, false)]
    public void Tells_single_address_masks_from_ranges(string? mask, AddressFamily family, bool single)
    {
        Assert.Equal(single, IpRestrictionConfig.IsSingleAddressMask(mask, family));
    }

    [Theory]
    [InlineData(403, "IIS now answers its requests with 403 Forbidden.")]
    [InlineData("Forbidden", "IIS now answers its requests with 403 Forbidden.")]
    [InlineData(401, "IIS now answers its requests with 401 Unauthorized.")]
    [InlineData("NotFound", "IIS now answers its requests with 404 Not Found.")]
    [InlineData(0, "IIS now drops its connections without an answer.")]
    [InlineData(null, "IIS now refuses its requests.")]
    public void Describes_what_iis_does_with_a_blocked_request(object? denyAction, string expected)
    {
        Assert.Equal(expected, IpRestrictionConfig.DescribeDenyAction(denyAction));
    }

    [Fact]
    public void Block_messages_name_the_locations()
    {
        const string forbidden = "IIS now answers its requests with 403 Forbidden.";

        Assert.Equal(
            "Blocked 203.0.113.7 on Default Web Site and Default Web Site/api. IIS now answers its requests with 403 Forbidden.",
            IpRestrictionConfig.BlockMessage("203.0.113.7", ["Default Web Site", "Default Web Site/api"], [], [], forbidden));
        Assert.Equal(
            "203.0.113.7 was already blocked on Shop.",
            IpRestrictionConfig.BlockMessage("203.0.113.7", [], ["Shop"], [], forbidden));
        Assert.Equal(
            "Blocked 203.0.113.7 on A (it was already blocked on B and C). IIS now answers its requests with 403 Forbidden.",
            IpRestrictionConfig.BlockMessage("203.0.113.7", ["A"], ["B", "C"], [], forbidden));
        Assert.Equal(
            "Blocked 203.0.113.7 on all sites on this server, which replaces its own blocks on Shop. IIS now answers its requests with 403 Forbidden.",
            IpRestrictionConfig.BlockMessage("203.0.113.7", [""], [], ["Shop"], forbidden));
    }

    [Fact]
    public void Joins_locations_in_plain_words()
    {
        Assert.Equal("all sites on this server", IpRestrictionConfig.JoinLocations([""]));
        Assert.Equal("A and B", IpRestrictionConfig.JoinLocations(["A", "B"]));
        Assert.Equal("A, B and C", IpRestrictionConfig.JoinLocations(["A", "B", "C"]));
    }

    [Theory]
    [InlineData("Default Web Site/api", "Default Web Site", true)]
    [InlineData("Default Web Site/api/v2", "Default Web Site/api", true)]
    [InlineData("default web site/API", "Default Web Site", true)]
    [InlineData("Shop", "", true)]
    [InlineData("", "", false)]
    [InlineData("Default Web Site", "Default Web Site", false)]
    [InlineData("Default Web Site2", "Default Web Site", false)]
    [InlineData("Default Web Site/apiv2", "Default Web Site/api", false)]
    [InlineData("Default Web Site", "Default Web Site/api", false)]
    public void Knows_which_locations_inherit_from_which(string location, string ancestor, bool below)
    {
        Assert.Equal(below, IpRestrictionConfig.IsBelow(location, ancestor));
    }

    [Fact]
    public void Leaves_out_locations_that_inherit_from_another_one_in_the_list()
    {
        Assert.Equal(
            ["Default Web Site", "Shop", "Default Web Site2"],
            IpRestrictionConfig.WithoutCovered(["Default Web Site", "Default Web Site/api", "Shop", "shop", "Default Web Site2"]));
        Assert.Equal([""], IpRestrictionConfig.WithoutCovered(["Shop", "", "Shop/api"]));
        Assert.Equal([""], IpRestrictionConfig.WithoutCovered([" ", "."]));
    }

    [Fact]
    public void Turns_iis_configuration_errors_into_one_line()
    {
        Assert.Equal(
            "Access to the IIS configuration was denied. The collector must run as administrator.",
            IpRestrictionConfig.ErrorMessage(new UnauthorizedAccessException("Filename: redirection.config\r\nError: Cannot read configuration file due to insufficient permissions\r\n\r\n")));
        Assert.Equal(
            "Access to the IIS configuration was denied. The collector must run as administrator.",
            IpRestrictionConfig.ErrorMessage(new COMException("Access is denied.", unchecked((int)0x80070005))));
        Assert.Equal(
            "IIS is not installed (applicationHost.config not found).",
            IpRestrictionConfig.ErrorMessage(new COMException("Class not registered", unchecked((int)0x80040154))));
        Assert.Equal(
            "IIS configuration error: Filename: \\\\?\\C:\\Windows\\system32\\inetsrv\\config\\applicationHost.config Line number: 12 Error: Configuration file is not well-formed XML",
            IpRestrictionConfig.ErrorMessage(new COMException(
                "Filename: \\\\?\\C:\\Windows\\system32\\inetsrv\\config\\applicationHost.config\r\nLine number: 12\r\nError: Configuration file is not well-formed XML\r\n\r\n",
                unchecked((int)0x8007000D))));
        Assert.Equal(
            IpRestrictionConfig.NotInstalledMessage,
            IpRestrictionConfig.ErrorMessage(new InvalidOperationException(IpRestrictionConfig.NotInstalledMessage)));
    }

    [Fact]
    public void Says_how_to_install_ip_restrictions()
    {
        Assert.Equal(
            "IIS can't block addresses because its 'IP and Domain Restrictions' feature isn't installed. Install it " +
            "(PowerShell: Install-WindowsFeature Web-IP-Security, or Server Manager: Web Server > Security > IP and Domain Restrictions), then try again.",
            IpRestrictionConfig.NotInstalledMessage);
    }

    [Fact]
    public void Health_is_ok_and_off_without_tracked_pools()
    {
        var health = RequestTrackingStatus.Health(Settings(), null, iisLogRunning: false, "ignored", 0, 0, 0);

        Assert.Equal("IP and URL tracking", health.Name);
        Assert.True(health.Ok);
        Assert.Equal("Off. Turn it on per app pool in Settings.", health.Message);
    }

    [Fact]
    public void Health_fails_when_the_log_stream_is_off_or_down_or_writing_fails()
    {
        var tracingOff = RequestTrackingStatus.Health(Settings(["Shop"], tracing: false), Topology(), true, null, 0, 0, 0);
        Assert.False(tracingOff.Ok);
        Assert.Equal("Needs the IIS ETW log stream: turn on Live response times in Settings.", tracingOff.Message);

        var writeFailed = RequestTrackingStatus.Health(Settings(["Shop"]), Topology(), true, "Writing recorded requests failed.", 0, 0, 0);
        Assert.False(writeFailed.Ok);
        Assert.Equal("Writing recorded requests failed.", writeFailed.Message);

        var notRunning = RequestTrackingStatus.Health(Settings(["Shop"]), Topology(), false, null, 0, 0, 0);
        Assert.False(notRunning.Ok);
        Assert.Contains("isn't running", notRunning.Message);
    }

    [Fact]
    public void Health_names_sites_of_tracked_pools_that_dont_log_the_client_ip()
    {
        var topology = Topology(
            Site("Shop", "Shop", fieldsLogged: false),
            Site("Blog", "Blog", fieldsLogged: false),
            Site("Api", "Api", fieldsLogged: true));

        var health = RequestTrackingStatus.Health(Settings(["shop", "Api"]), topology, true, null, 0, 0, 0);

        Assert.False(health.Ok);
        Assert.StartsWith("Shop doesn't send the client IP, method and substatus", health.Message);
        Assert.Contains("IPs & URLs tab", health.Message);
        Assert.DoesNotContain("Blog", health.Message);
    }

    [Fact]
    public void Health_reports_what_is_recorded()
    {
        var settings = Settings(["Shop", "Api"]);
        var topology = Topology(Site("Shop", "Shop", fieldsLogged: true), Site("Api", "Api", fieldsLogged: true));

        var health = RequestTrackingStatus.Health(settings, topology, true, null, 12, 0, 5 * 1024 * 1024);

        Assert.True(health.Ok);
        Assert.Equal(
            $"Recording 2 app pools: Api and Shop. 12 requests since start; {MetricFormatter.Bytes(5 * 1024 * 1024)} on disk; kept 3 days.",
            health.Message);

        var dropping = RequestTrackingStatus.Health(settings, topology, true, null, 900, 7, 0);
        Assert.True(dropping.Ok);
        Assert.EndsWith(
            $" · 7 requests not itemised (more than {RequestLogAggregator.DefaultMaxPendingRows:N0} different IP/URL combinations within a minute)",
            dropping.Message);
    }

    [Fact]
    public void Warns_when_the_pool_is_not_tracked_now()
    {
        var report = new RequestLogReport();
        var topology = Topology(Site("Shop", "Shop", fieldsLogged: false));

        RequestTrackingStatus.AddWarnings(report, "Shop", Settings(["Api"]), topology, true, null, 0);

        Assert.Equal(["Tracking is off for this app pool; showing what was recorded earlier."], report.Warnings);
        Assert.False(report.LoggingFieldsMissing);
    }

    [Fact]
    public void Warns_about_sites_that_dont_log_the_client_ip_and_offers_the_fix()
    {
        var report = new RequestLogReport();
        var topology = Topology(
            Site("Shop", "Shop", fieldsLogged: false),
            new SiteInfo { Id = 9, Name = "Main", Applications = [new("/", "Other"), new("/shop", "Shop")], RequestFieldsLogged = false },
            Site("Api", "Api", fieldsLogged: false));

        RequestTrackingStatus.AddWarnings(report, "shop", Settings(["Shop"]), topology, true, null, 0);

        Assert.True(report.LoggingFieldsMissing);
        var warning = Assert.Single(report.Warnings);
        Assert.StartsWith("Shop and Main don't send the client IP, method and substatus", warning);
    }

    [Fact]
    public void Warns_when_nothing_new_is_recorded_and_about_failures_and_dropped_requests()
    {
        var topology = Topology(Site("Shop", "Shop", fieldsLogged: true));

        var tracingOff = new RequestLogReport();
        RequestTrackingStatus.AddWarnings(tracingOff, "Shop", Settings(["Shop"], tracing: false), topology, false, null, 0);
        Assert.Contains("Live response times", Assert.Single(tracingOff.Warnings));

        var notRunning = new RequestLogReport();
        RequestTrackingStatus.AddWarnings(notRunning, "Shop", Settings(["Shop"]), topology, false, "Disk full.", 3);
        Assert.Equal(3, notRunning.Warnings.Count);
        Assert.Contains("isn't running", notRunning.Warnings[0]);
        Assert.Equal("Disk full.", notRunning.Warnings[1]);
        Assert.StartsWith("3 requests since the collector started were counted but not itemised", notRunning.Warnings[2]);

        var fine = new RequestLogReport();
        RequestTrackingStatus.AddWarnings(fine, "Shop", Settings(["Shop"]), topology, true, null, 0);
        Assert.Empty(fine.Warnings);
        Assert.False(fine.LoggingFieldsMissing);
    }

    [Fact]
    public void Joins_names_and_shortens_long_lists()
    {
        Assert.Equal("A", RequestTrackingStatus.JoinNames(["A"]));
        Assert.Equal("A and B", RequestTrackingStatus.JoinNames(["A", "B"]));
        Assert.Equal("A, B and C", RequestTrackingStatus.JoinNames(["A", "B", "C"]));
        Assert.Equal("A, B, C, D, E and 2 more", RequestTrackingStatus.JoinNames(["A", "B", "C", "D", "E", "F", "G"]));
    }

    private static MonitorSettings Settings(List<string>? pools = null, bool tracing = true) =>
        new MonitorSettings { RequestTrackingPools = pools ?? [], EnableResponseTimeTracing = tracing }.Normalize();

    private static IisTopology Topology(params SiteInfo[] sites) => new() { Sites = [.. sites] };

    private static SiteInfo Site(string name, string pool, bool fieldsLogged) => new()
    {
        Id = 1,
        Name = name,
        Applications = [new("/", pool)],
        RequestFieldsLogged = fieldsLogged,
        EtwLoggingEnabled = fieldsLogged,
    };
}
