using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace IISMonitor.Core.Requests;

/// <summary>Helpers for IP addresses as they appear in IIS logs and X-Forwarded-For headers.</summary>
public static class IpText
{
    /// <summary>IPv4-mapped IPv6 becomes IPv4 and an IPv6 zone ("%12") is dropped, so one client has one form.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());
        return address;
    }

    /// <summary>
    /// Parses a plain address ("203.0.113.5", "2001:db8::1", "::ffff:1.2.3.4"). IPv4 needs all four
    /// parts: IPAddress alone would read "10" as 0.0.0.10 and "10.1" as 10.0.0.1.
    /// </summary>
    public static bool TryParse(string? text, out IPAddress address) => TryParse(text.AsSpan(), out address);

    public static bool TryParse(ReadOnlySpan<char> text, out IPAddress address)
    {
        address = IPAddress.None;
        text = text.Trim();
        if (text.IsEmpty || (!text.Contains(':') && text.Count('.') != 3))
            return false;
        if (!IPAddress.TryParse(text, out var parsed))
            return false;
        address = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Parses one X-Forwarded-For entry, which may carry a port ("1.2.3.4:5678", "[2001:db8::1]:443"),
    /// quotes, or IIS's "+" for a space. Returns false for "unknown", obfuscated names and garbage.
    /// </summary>
    public static bool TryParseForwarded(ReadOnlySpan<char> entry, out IPAddress address)
    {
        address = IPAddress.None;
        entry = entry.Trim(" \t+\"'");
        if (entry.IsEmpty || entry.Length > 64)
            return false;

        if (entry[0] == '[')
        {
            var close = entry.IndexOf(']');
            if (close < 0)
                return false;
            entry = entry[1..close];
        }
        else
        {
            // "1.2.3.4:5678": exactly one colon means IPv4 with a port; IPv6 has two or more.
            var first = entry.IndexOf(':');
            if (first >= 0 && entry.LastIndexOf(':') == first)
                entry = entry[..first];
        }

        return TryParse(entry, out address);
    }

    /// <summary>
    /// Private and shared ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7). Such an address in
    /// X-Forwarded-For is an internal hop, which many visitors' requests can pass through.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        address = Normalize(address);
        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out var written);
        if (written == 4)
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64);
        return (b[0] & 0xFE) == 0xFC;
    }

    public static bool IsAlwaysProtected(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6Multicast || address.IsIPv6LinkLocal;
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return b[0] is >= 224 and <= 239 || b[0] == 0 || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>An address or a network in CIDR form ("10.0.0.0/8", "2001:db8::/32", "203.0.113.5").</summary>
public readonly record struct IpNetwork
{
    private IpNetwork(IPAddress network, int prefixLength)
    {
        Network = network;
        PrefixLength = prefixLength;
    }

    public IPAddress Network { get; }

    public int PrefixLength { get; }

    public bool IsSingleAddress => PrefixLength == MaxPrefix(Network);

    public static IpNetwork Single(IPAddress address)
    {
        var normalized = IpText.Normalize(address);
        return new IpNetwork(normalized, MaxPrefix(normalized));
    }

    public static bool TryParse(string? text, out IpNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        var slash = text.IndexOf('/');
        if (!IpText.TryParse(slash < 0 ? text : text[..slash], out var address))
            return false;
        var max = MaxPrefix(address);
        var prefix = max;
        if (slash >= 0)
        {
            var maskText = text[(slash + 1)..].Trim();
            if (!int.TryParse(maskText, NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.TryParse(maskText, out var mask)
                    || !TryMaskToPrefix(mask, out prefix))
                    return false;
            }

            if (prefix < 0 || prefix > max)
                return false;
        }

        network = new IpNetwork(MaskOff(address, prefix), prefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        address = IpText.Normalize(address);
        if (address.AddressFamily != Network.AddressFamily)
            return false;
        return MaskOff(address, PrefixLength).Equals(Network);
    }

    public bool Overlaps(IpNetwork other)
    {
        if (Network.AddressFamily != other.Network.AddressFamily)
            return false;
        var shorter = Math.Min(PrefixLength, other.PrefixLength);
        return MaskOff(Network, shorter).Equals(MaskOff(other.Network, shorter));
    }

    public override string ToString() => IsSingleAddress ? Network.ToString() : $"{Network}/{PrefixLength}";

    private static int MaxPrefix(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

    private static IPAddress MaskOff(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsLeft = prefixLength - i * 8;
            bytes[i] &= bitsLeft >= 8 ? (byte)0xFF : bitsLeft <= 0 ? (byte)0 : (byte)(0xFF << (8 - bitsLeft));
        }

        return new IPAddress(bytes);
    }

    private static bool TryMaskToPrefix(IPAddress mask, out int prefix)
    {
        prefix = 0;
        var seenZero = false;
        foreach (var b in mask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var one = (b & (1 << bit)) != 0;
                if (one && seenZero)
                    return false;
                if (one)
                    prefix++;
                else
                    seenZero = true;
            }
        }

        return true;
    }
}

/// <summary>A set of addresses and networks, for allow-lists and trusted proxies. Immutable; thread-safe.</summary>
public sealed class IpSet
{
    private readonly HashSet<IPAddress> _addresses = [];
    private readonly List<IpNetwork> _networks = [];

    public static readonly IpSet Empty = new([]);

    public IpSet(IEnumerable<IpNetwork> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSingleAddress)
                _addresses.Add(entry.Network);
            else
                _networks.Add(entry);
        }
    }

    /// <summary>Parses entries, skipping invalid ones (settings validation reports those).</summary>
    public static IpSet Parse(IEnumerable<string>? entries, IEnumerable<IPAddress>? extra = null)
    {
        var list = new List<IpNetwork>();
        foreach (var text in entries ?? [])
        {
            if (IpNetwork.TryParse(text, out var network))
                list.Add(network);
        }

        foreach (var address in extra ?? [])
            list.Add(IpNetwork.Single(address));
        return new IpSet(list);
    }

    public bool IsEmpty => _addresses.Count == 0 && _networks.Count == 0;

    public IEnumerable<IpNetwork> Entries => _addresses.Select(IpNetwork.Single).Concat(_networks);

    public bool Contains(IPAddress address)
    {
        address = IpText.Normalize(address);
        if (_addresses.Contains(address))
            return true;
        foreach (var network in _networks)
        {
            if (network.Contains(address))
                return true;
        }

        return false;
    }
}
