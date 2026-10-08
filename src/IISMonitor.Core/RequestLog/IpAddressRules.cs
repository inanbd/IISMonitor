using System.Net;
using System.Net.Sockets;
using IISMonitor.Core.Collection;

namespace IISMonitor.Core.RequestLog;

/// <summary>Which client addresses can be blocked, which look internal, and where IIS keeps a pool's blocks.</summary>
public static class IpAddressRules
{
    /// <summary>Parses an address to block; rejects empty, invalid, loopback, unspecified, multicast and broadcast. IPv4-mapped IPv6 becomes IPv4; an IPv6 zone is dropped.</summary>
    public static bool TryNormalizeForBlocking(string? text, out string canonical, out string error)
    {
        canonical = "";
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter an IP address.";
            return false;
        }

        if (!TryParse(text, out var address))
        {
            error = $"'{text.Trim()}' is not an IP address.";
            return false;
        }

        var shown = address.ToString();
        if (IPAddress.IsLoopback(address))
        {
            error = $"{shown} is a loopback address (this server itself) and can't be blocked.";
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            error = $"{shown} is the unspecified address and can't be blocked.";
            return false;
        }

        if (address.IsIPv6Multicast || (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239))
        {
            error = $"{shown} is a multicast address and can't be blocked.";
            return false;
        }

        if (address.Equals(IPAddress.Broadcast))
        {
            error = $"{shown} is the broadcast address and can't be blocked.";
            return false;
        }

        canonical = shown;
        return true;
    }

    /// <summary>RFC 1918, CGNAT 100.64/10, link-local, IPv6 ULA fc00::/7 and fe80::/10: probably a proxy, load balancer or internal client.</summary>
    public static bool IsPrivate(string ipAddress)
    {
        if (!TryParse(ipAddress, out var address))
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        return (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
    }

    /// <summary>IIS configuration locations of every application the pool runs: "Site" for a site's root application, "Site/path" otherwise.</summary>
    public static List<string> LocationsForPool(IisTopology topology, string appPool)
    {
        var locations = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in topology.Sites)
        {
            foreach (var application in site.Applications)
            {
                if (!string.Equals(application.AppPool, appPool, StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = application.Path.Trim('/');
                var location = path.Length == 0 ? site.Name : site.Name + "/" + path;
                if (seen.Add(location))
                    locations.Add(location);
            }
        }

        return locations;
    }

    /// <summary>"All sites on this server" for "", else the location.</summary>
    public static string DescribeLocation(string location) =>
        string.IsNullOrEmpty(location) ? "All sites on this server" : location;

    /// <summary>
    /// Parses an IPv4 or IPv6 address as IIS logs it. IPv4 must be written as four decimal numbers
    /// (no "10.1" or "0x7f.1" shorthands); IPv4-mapped IPv6 becomes IPv4; an IPv6 zone is dropped.
    /// </summary>
    private static bool TryParse(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var zone = trimmed.IndexOf('%');
        if (zone >= 0 && trimmed.Contains(':'))
            trimmed = trimmed[..zone];

        if (!IPAddress.TryParse(trimmed, out var parsed))
            return false;

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPAddress.TryParse also accepts "1", "10.1" and octal or hex parts; IIS never logs those.
            if (parsed.ToString() != trimmed)
                return false;
        }
        else if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            parsed = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : new IPAddress(parsed.GetAddressBytes());
        }
        else
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
