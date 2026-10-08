using System.Globalization;
using System.Net;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Presentation;

public sealed record TrafficRange(TimeSpan Range, string Label);

/// <summary>Choices and text for the IPs &amp; URLs tab.</summary>
public static class TrafficPresentation
{
    public static readonly IReadOnlyList<TrafficRange> StandardRanges =
    [
        new(TimeSpan.FromMinutes(15), "Last 15 minutes"), new(TimeSpan.FromHours(1), "Last hour"),
        new(TimeSpan.FromHours(6), "Last 6 hours"), new(TimeSpan.FromHours(24), "Last 24 hours"),
        new(TimeSpan.FromDays(3), "Last 3 days"), new(TimeSpan.FromDays(7), "Last 7 days"),
        new(TimeSpan.FromDays(31), "Last 31 days"),
    ];

    /// <summary>
    /// The standard ranges that fit in the retention (always at least the first), plus the retention
    /// itself ("Last 5 days") when it isn't one of them.
    /// </summary>
    public static List<TrafficRange> RangesWithin(int retentionDays)
    {
        var days = Math.Clamp(retentionDays, 1, MonitorSettings.MaxRequestLogRetentionDays);
        var retention = TimeSpan.FromDays(days);
        var ranges = StandardRanges.Where(r => r.Range <= retention).ToList();
        if (ranges.Count == 0)
            ranges.Add(StandardRanges[0]);
        if (ranges[^1].Range < retention)
            ranges.Add(new TrafficRange(retention, $"Last {days} days"));
        return ranges;
    }

    /// <summary>"404", or "401.1" when IIS logged a substatus.</summary>
    public static string StatusText(int status, int subStatus) =>
        subStatus == 0
            ? status.ToString(CultureInfo.InvariantCulture)
            : status.ToString(CultureInfo.InvariantCulture) + "." + subStatus.ToString(CultureInfo.InvariantCulture);

    /// <summary>Sorts 401.1 after 401 and before 402.</summary>
    public static long StatusSortKey(int status, int subStatus) => status * 100_000L + Math.Clamp(subStatus, 0, 99_999);

    /// <summary>A Unix-milliseconds timestamp in local time: just the time for today, else date and time.</summary>
    public static string FormatTime(long unixMs, bool seconds) =>
        unixMs <= 0
            ? MetricFormatter.Missing
            : FormatTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime, DateTime.Now, seconds);

    /// <summary>Just the time when <paramref name="local"/> is on the same day as <paramref name="now"/>, else date and time.</summary>
    public static string FormatTime(DateTime local, DateTime now, bool seconds)
    {
        var format = local.Date == now.Date ? (seconds ? "T" : "t") : (seconds ? "G" : "g");
        return local.ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>"1,234 IP addresses · 56,789 requests · showing the busiest 1,000 · requests.db 120 MB".</summary>
    public static string SummaryStatus(RequestLogReport report, string? filter)
    {
        var (one, many) = report.View == RequestLogView.Clients ? ("IP address", "IP addresses") : ("URL", "URLs");
        var parts = new List<string>();
        if (report.TotalRows == 0)
        {
            parts.Add(string.IsNullOrWhiteSpace(filter)
                ? "No requests recorded in this period"
                : $"No {many} containing \"{filter.Trim()}\" in this period");
        }
        else
        {
            parts.Add(Count(report.TotalRows, one, many));
            parts.Add(Count(report.TotalHits, "request", "requests"));
            if (report.Truncated)
                parts.Add("showing the busiest " + report.Summaries.Count.ToString("N0", CultureInfo.CurrentCulture));
        }

        if (report.DatabaseBytes > 0)
            parts.Add("requests.db " + MetricFormatter.Bytes(report.DatabaseBytes));
        return string.Join(" · ", parts);
    }

    /// <summary>"120 rows · 4,567 requests · showing the latest 1,000" for the detail grid.</summary>
    public static string DetailStatus(RequestLogReport report, bool perMinute)
    {
        if (report.TotalRows == 0)
            return "No requests in this period";

        var text = Count(report.TotalRows, "row", "rows") + " · " + Count(report.TotalHits, "request", "requests");
        if (report.Truncated)
            text += $" · showing the {(perMinute ? "latest" : "busiest")} {report.Details.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        return text;
    }

    /// <summary>Title of the detail grid for a client IP (Clients view) or a URL (Urls view).</summary>
    public static string DetailHeader(RequestLogView view, string key) =>
        view == RequestLogView.Clients ? "URLs visited by " + key : "IP addresses that visited " + key;

    /// <summary>Whether the text is an IPv4 or IPv6 address (not "(unknown)" or other text).</summary>
    public static bool IsIpAddress(string? text) => TryParse(text, out _);

    /// <summary>Whether a deny entry covers the address: the same address, or within its subnet mask or prefix length.</summary>
    public static bool Covers(BlockedIp entry, string ipAddress)
    {
        if (!TryParse(entry.IpAddress, out var blocked) || !TryParse(ipAddress, out var address))
        {
            return string.IsNullOrWhiteSpace(entry.SubnetMask)
                && string.Equals(entry.IpAddress.Trim(), ipAddress.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (blocked.AddressFamily != address.AddressFamily)
            return false;

        var blockedBytes = blocked.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (string.IsNullOrWhiteSpace(entry.SubnetMask))
            return blockedBytes.AsSpan().SequenceEqual(addressBytes);

        if (!TryMask(entry.SubnetMask.Trim(), blockedBytes.Length, out var mask))
            return false;

        for (var i = 0; i < mask.Length; i++)
        {
            if ((blockedBytes[i] & mask[i]) != (addressBytes[i] & mask[i]))
                return false;
        }

        return true;
    }

    /// <summary>Where IIS refuses the address ("All sites on this server", "Shop, Shop/api"), or "" when it doesn't.</summary>
    public static string BlockedText(IEnumerable<BlockedIp> blocked, string ipAddress) =>
        string.Join(", ", blocked
            .Where(entry => Covers(entry, ipAddress))
            .Select(entry => IpAddressRules.DescribeLocation(entry.Location))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Count(long count, string one, string many) =>
        count.ToString("N0", CultureInfo.CurrentCulture) + " " + (count == 1 ? one : many);

    private static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !IPAddress.TryParse(trimmed, out var parsed))
            return false;
        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    /// <summary>A mask written as an address ("255.255.255.0") or as a prefix length ("24").</summary>
    private static bool TryMask(string text, int length, out byte[] mask)
    {
        mask = [];
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            if (prefix > length * 8)
                return false;
            mask = new byte[length];
            for (var i = 0; i < length; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                mask[i] = (byte)(0xFF << (8 - bits));
            }

            return true;
        }

        if (!IPAddress.TryParse(text, out var parsed) || parsed.GetAddressBytes() is not { } bytes || bytes.Length != length)
            return false;
        mask = bytes;
        return true;
    }
}
