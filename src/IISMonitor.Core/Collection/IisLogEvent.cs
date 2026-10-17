using System.Globalization;

namespace IISMonitor.Core.Collection;

/// <summary>
/// One request from IIS's log stream: the fields needed for response times, plus the request
/// details (client, method, size, user agent, custom header fields) when the site logs them.
/// </summary>
public readonly record struct IisLogEvent(long? SiteId, string? SiteName, string UriStem, int StatusCode, double TimeTakenMs)
{
    /// <summary>c-ip: the address that connected (a proxy, when there is one in front of IIS).</summary>
    public string? ClientIp { get; init; }

    public string? Method { get; init; }

    public int SubStatus { get; init; }

    public long? BytesSent { get; init; }

    /// <summary>As IIS logs it, with "+" for spaces.</summary>
    public string? UserAgent { get; init; }

    /// <summary>The raw CustomFields string ("Name value Name value ..."); parsed per site later.</summary>
    public string? CustomFields { get; init; }

    /// <summary>When the event was raised (IIS raises it as the request finishes); default when unknown.</summary>
    public DateTime TimeUtc { get; init; }
}

/// <summary>
/// Parses the payload of the Microsoft-Windows-IIS-Logging ETW event. Field names in the event
/// manifest use the W3C names ("s-sitename", "time-taken", ...); matching ignores case, dashes
/// and underscores so small differences between IIS versions don't matter.
/// </summary>
public static class IisLogEventParser
{
    public static bool TryParse(IEnumerable<KeyValuePair<string, object?>> payload, out IisLogEvent result)
    {
        string? siteField = null, uriStem = null, clientIp = null, method = null, userAgent = null, customFields = null;
        int? status = null, subStatus = null;
        double? timeTaken = null, bytesSent = null;

        foreach (var (rawName, value) in payload)
        {
            switch (Normalize(rawName))
            {
                case "ssitename":
                    siteField = AsString(value);
                    break;
                case "csuristem":
                    uriStem = AsString(value);
                    break;
                case "scstatus":
                    status = AsInt(value);
                    break;
                case "timetaken":
                    timeTaken = AsDouble(value);
                    break;
                case "cip":
                    clientIp = Present(AsString(value));
                    break;
                case "csmethod":
                    method = Present(AsString(value));
                    break;
                case "scsubstatus":
                    subStatus = AsInt(value);
                    break;
                case "scbytes":
                    bytesSent = AsDouble(value);
                    break;
                case "csuseragent":
                    userAgent = Present(AsString(value));
                    break;
                case "customfields":
                    customFields = Present(AsString(value));
                    break;
            }
        }

        if (timeTaken is null || string.IsNullOrWhiteSpace(siteField))
        {
            result = default;
            return false;
        }

        long? siteId = TryParseServiceInstance(siteField, out var id) ? id : null;
        result = new IisLogEvent(
            siteId,
            siteId is null ? siteField : null,
            string.IsNullOrEmpty(uriStem) ? "/" : uriStem,
            status ?? 0,
            timeTaken.Value)
        {
            ClientIp = clientIp,
            Method = method,
            SubStatus = subStatus ?? 0,
            BytesSent = bytesSent is { } bytes and >= 0 ? (long)bytes : null,
            UserAgent = userAgent,
            CustomFields = customFields,
        };
        return true;
    }

    /// <summary>W3C logs identify a site as "W3SVC" followed by its numeric site ID.</summary>
    public static bool TryParseServiceInstance(string value, out long siteId)
    {
        siteId = 0;
        const string prefix = "W3SVC";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out siteId);
    }

    private static string Normalize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (c is '-' or '_' or ' ' or '(' or ')')
                continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    /// <summary>IIS writes "-" for a field with no value.</summary>
    private static string? Present(string? value) => string.IsNullOrEmpty(value) || value == "-" ? null : value;

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s.Trim(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static int? AsInt(object? value) => value switch
    {
        null => null,
        string s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
        IConvertible c => TryConvert(() => c.ToInt32(CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static double? AsDouble(object? value) => value switch
    {
        null => null,
        string s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c => TryConvert(() => c.ToDouble(CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static T? TryConvert<T>(Func<T> convert) where T : struct
    {
        try
        {
            return convert();
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
