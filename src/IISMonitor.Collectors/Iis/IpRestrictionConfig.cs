using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IISMonitor.Core.RequestLog;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads and changes IIS "IP and Domain Restrictions" (system.webServer/security/ipSecurity) in
/// applicationHost.config: deny entries for single IP addresses, for the whole server or for one
/// location ("Site" or "Site/path").
/// </summary>
internal static class IpRestrictionConfig
{
    internal const string NotInstalledMessage =
        "IIS can't block addresses because its 'IP and Domain Restrictions' feature isn't installed. " +
        "Install it (PowerShell: Install-WindowsFeature Web-IP-Security, or Server Manager: Web Server > Security > IP and Domain Restrictions), then try again.";

    private const string SectionPath = "system.webServer/security/ipSecurity";
    private const string ModuleName = "IpRestrictionModule";
    private const string NotInstalledIis = "IIS is not installed (applicationHost.config not found).";

    private const int AccessDenied = unchecked((int)0x80070005);
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>Every deny entry for an IP address or range, at the server level and at each location that has its own.</summary>
    public static List<BlockedIp> ListBlocked()
    {
        EnsureIisInstalled();
        using var manager = new ServerManager(readOnly: true, applicationHostConfigurationPath: null);
        var config = manager.GetApplicationHostConfiguration();

        var blocked = new List<BlockedIp>();
        foreach (var location in Locations(config))
        {
            // A location's section also holds the entries it inherits; each entry is listed where it is stored.
            foreach (var entry in Section(config, location).GetCollection())
            {
                if (!entry.IsLocallyStored || !IsDeny(entry))
                    continue;

                var address = Text(entry, "ipAddress");
                if (address.Length == 0)
                    continue; // A domain name entry, not an address.

                var mask = Text(entry, "subnetMask");
                var family = TryParseAddress(address, out var parsed) ? parsed.AddressFamily : AddressFamily.InterNetwork;
                blocked.Add(new BlockedIp
                {
                    IpAddress = address,
                    SubnetMask = IsSingleAddressMask(mask, family) ? null : mask,
                    Location = location,
                });
            }
        }

        return blocked;
    }

    /// <summary>
    /// Adds a deny entry for <paramref name="ipAddress"/> at each location ("" = the whole server),
    /// skipping locations where it is already denied. Returns what was done, in plain words.
    /// </summary>
    /// <remarks>
    /// An entry is inherited by every location below its own, and IIS refuses to serve a location that
    /// has its own entry with the same address as an inherited one (error 500.19, duplicate entry). So
    /// a location below another one in the list gets no entry of its own, and the deny entries below a
    /// new one are removed (it replaces them).
    /// </remarks>
    public static string Block(string ipAddress, IReadOnlyList<string> locations)
    {
        if (!TryParseAddress(ipAddress, out var address))
            throw new ArgumentException($"'{ipAddress}' is not an IP address.");
        if (locations.Count == 0)
            throw new ArgumentException("There is no site or application to block the address on.");

        EnsureIisInstalled();
        using var manager = new ServerManager();
        var config = manager.GetApplicationHostConfiguration();
        if (!HasRestrictionModule(config))
            throw new InvalidOperationException(NotInstalledMessage);

        var allLocations = Locations(config);
        var blocked = new List<string>();
        var already = new List<string>();
        var replaced = new List<string>();
        var effects = new HashSet<string>();
        foreach (var location in WithoutCovered(locations))
        {
            var section = Section(config, location);
            var collection = section.GetCollection();

            // Entries for this address here, including ones inherited from the server or a parent location.
            var existing = collection.Where(e => IsSingleAddressEntryFor(e, address)).ToList();
            if (existing.Any(IsDeny))
            {
                already.Add(location);
                continue;
            }

            var allowEntry = existing.FirstOrDefault(e => e.IsLocallyStored);
            if (allowEntry is null && existing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{ipAddress} is explicitly allowed for {DescribeInSentence(location)} by an entry made at a higher level. " +
                    "Remove that entry in IIS Manager (IP Address and Domain Restrictions), then try again.");
            }

            // Below this location first, while their sections still read as they are on disk.
            foreach (var below in allLocations.Where(l => IsBelow(l, location)))
            {
                var belowEntries = Section(config, below).GetCollection();
                foreach (var entry in belowEntries.Where(e => e.IsLocallyStored && IsSingleAddressEntryFor(e, address)).ToList())
                {
                    if (!IsDeny(entry))
                    {
                        throw new InvalidOperationException(
                            $"{ipAddress} is explicitly allowed on {below}, so it can't also be blocked for {DescribeInSentence(location)}. " +
                            "Remove that entry in IIS Manager (IP Address and Domain Restrictions), or block the address on fewer sites.");
                    }

                    belowEntries.Remove(entry);
                    if (!replaced.Contains(below, StringComparer.OrdinalIgnoreCase))
                        replaced.Add(below);
                }
            }

            if (allowEntry is not null)
            {
                // The address is explicitly allowed here: turn that entry into a deny entry.
                allowEntry.SetAttributeValue("allowed", false);
            }
            else
            {
                var entry = collection.CreateElement("add");
                entry.SetAttributeValue("ipAddress", ipAddress);
                entry.SetAttributeValue("allowed", false);
                collection.Add(entry);
            }

            blocked.Add(location);
            effects.Add(DescribeDenyAction(DenyAction(section)));
        }

        if (blocked.Count > 0)
            manager.CommitChanges();

        return BlockMessage(ipAddress, blocked, already, replaced, effects.Count == 1 ? effects.Single() : DescribeDenyAction(null));
    }

    /// <summary>Removes the deny entries for <paramref name="ipAddress"/> stored at <paramref name="location"/> ("" = server level).</summary>
    public static string Unblock(string ipAddress, string location)
    {
        if (!TryParseAddress(ipAddress, out var address))
            throw new ArgumentException($"'{ipAddress}' is not an IP address.");

        location = NormalizeLocation(location);
        EnsureIisInstalled();
        using var manager = new ServerManager();
        var config = manager.GetApplicationHostConfiguration();
        var collection = Section(config, location).GetCollection();

        var denies = collection.Where(e => IsDeny(e) && SameAddress(e, address)).ToList();
        var local = denies.Where(e => e.IsLocallyStored).ToList();
        if (local.Count == 0)
        {
            return denies.Count > 0
                ? $"{ipAddress} isn't blocked on {DescribeInSentence(location)} itself: the block is inherited from the server or a parent location. Unblock it there."
                : $"{ipAddress} wasn't blocked on {DescribeInSentence(location)}; nothing changed.";
        }

        foreach (var entry in local)
            collection.Remove(entry);
        manager.CommitChanges();

        return denies.Count > local.Count
            ? $"Unblocked {ipAddress} on {DescribeInSentence(location)}, but it is still blocked there by an entry made for the whole server or a parent site."
            : $"Unblocked {ipAddress} on {DescribeInSentence(location)}.";
    }

    /// <summary>A one-line message for a failure reading or changing the IIS configuration.</summary>
    internal static string ErrorMessage(Exception e) => e switch
    {
        UnauthorizedAccessException or COMException { HResult: AccessDenied } =>
            "Access to the IIS configuration was denied. The collector must run as administrator.",
        COMException { HResult: ClassNotRegistered } => NotInstalledIis,
        FileNotFoundException or DirectoryNotFoundException or COMException { HResult: FileNotFound or PathNotFound } =>
            "An IIS configuration file is missing. " + OneLine(e.Message),
        COMException => "IIS configuration error: " + OneLine(e.Message),
        _ => OneLine(e.Message),
    };

    /// <summary>
    /// Parses an IPv4 or IPv6 address from the configuration or a request; IPv4-mapped IPv6 becomes
    /// IPv4 and an IPv6 zone is dropped, so equal addresses compare equal.
    /// </summary>
    internal static bool TryParseAddress(string? text, out IPAddress address)
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

        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
            parsed = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : new IPAddress(parsed.GetAddressBytes());
        else if (parsed.AddressFamily != AddressFamily.InterNetwork)
            return false;

        address = parsed;
        return true;
    }

    /// <summary>Whether an entry's subnet mask (or prefix length) covers exactly one address. IIS's default mask is 255.255.255.255.</summary>
    internal static bool IsSingleAddressMask(string? mask, AddressFamily family)
    {
        mask = mask?.Trim();
        if (string.IsNullOrEmpty(mask) || mask == "255.255.255.255")
            return true;

        var bits = family == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (int.TryParse(mask.TrimStart('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            return prefix == bits;

        return IPAddress.TryParse(mask, out var parsed)
            && parsed.AddressFamily == family
            && parsed.GetAddressBytes().All(b => b == 0xFF);
    }

    /// <summary>What IIS does with a refused request, from the section's denyAction (IIS 8 and later).</summary>
    internal static string DescribeDenyAction(object? denyAction) => Convert.ToString(denyAction, CultureInfo.InvariantCulture) switch
    {
        "403" or "Forbidden" => "IIS now answers its requests with 403 Forbidden.",
        "401" or "Unauthorized" => "IIS now answers its requests with 401 Unauthorized.",
        "404" or "NotFound" => "IIS now answers its requests with 404 Not Found.",
        "0" or "AbortRequest" => "IIS now drops its connections without an answer.",
        _ => "IIS now refuses its requests.",
    };

    internal static string BlockMessage(
        string ipAddress, IReadOnlyList<string> blocked, IReadOnlyList<string> already, IReadOnlyList<string> replaced, string effect)
    {
        if (blocked.Count == 0)
            return $"{ipAddress} was already blocked on {JoinLocations(already)}.";

        var message = $"Blocked {ipAddress} on {JoinLocations(blocked)}";
        if (already.Count > 0)
            message += $" (it was already blocked on {JoinLocations(already)})";
        if (replaced.Count > 0)
            message += $", which replaces its own blocks on {JoinLocations(replaced)}";
        return message + ". " + effect;
    }

    /// <summary>
    /// The distinct locations, leaving out any that lies below another one in the list (it inherits that
    /// one's entries); "" (the whole server) covers every other location.
    /// </summary>
    internal static List<string> WithoutCovered(IEnumerable<string> locations)
    {
        var distinct = locations.Select(NormalizeLocation).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Where(l => !distinct.Any(other => IsBelow(l, other))).ToList();
    }

    /// <summary>Whether <paramref name="location"/> lies below <paramref name="ancestor"/> ("Site/app" below "Site"; everything below "").</summary>
    internal static bool IsBelow(string location, string ancestor)
    {
        if (location.Length == 0)
            return false;
        if (ancestor.Length == 0)
            return true;

        var parent = ancestor.TrimEnd('/');
        return location.Length > parent.Length + 1
            && location.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && location[parent.Length] == '/';
    }

    /// <summary>"A", "A and B", "A, B and C", with "" read as all sites on this server.</summary>
    internal static string JoinLocations(IReadOnlyList<string> locations)
    {
        var names = locations.Select(DescribeInSentence).ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    internal static string DescribeInSentence(string location) =>
        location.Length == 0 ? "all sites on this server" : IpAddressRules.DescribeLocation(location);

    /// <summary>"" for the server level ("", "." or nothing), else the location path as IIS stores it.</summary>
    internal static string NormalizeLocation(string? location) =>
        string.IsNullOrWhiteSpace(location) || location.Trim() == "." ? "" : location;

    private static void EnsureIisInstalled()
    {
        if (!File.Exists(IisConfigReader.ConfigPath))
            throw new InvalidOperationException(NotInstalledIis);
    }

    /// <summary>The server level ("") and every location path in applicationHost.config.</summary>
    private static List<string> Locations(Configuration config)
    {
        var locations = new List<string> { "" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var path in config.GetLocationPaths() ?? [])
        {
            var location = NormalizeLocation(path);
            if (seen.Add(location))
                locations.Add(location);
        }

        return locations;
    }

    private static ConfigurationSection Section(Configuration config, string location) =>
        location.Length == 0 ? config.GetSection(SectionPath) : config.GetSection(SectionPath, location);

    private static bool HasRestrictionModule(Configuration config) =>
        config.GetSection("system.webServer/globalModules").GetCollection()
            .Any(m => string.Equals(Text(m, "name"), ModuleName, StringComparison.OrdinalIgnoreCase));

    private static object? DenyAction(ConfigurationSection section)
    {
        try
        {
            return section.GetAttributeValue("denyAction");
        }
        catch (Exception e) when (e is COMException or ArgumentException)
        {
            // IIS 7.x has no denyAction: it always answers 403.
            return null;
        }
    }

    private static bool IsDeny(ConfigurationElement entry) => entry.GetAttributeValue("allowed") is false;

    private static bool SameAddress(ConfigurationElement entry, IPAddress address) =>
        TryParseAddress(Text(entry, "ipAddress"), out var other) && other.Equals(address);

    /// <summary>An entry for exactly this one address (not a range, not a domain name).</summary>
    private static bool IsSingleAddressEntryFor(ConfigurationElement entry, IPAddress address) =>
        SameAddress(entry, address)
        && IsSingleAddressMask(Text(entry, "subnetMask"), address.AddressFamily)
        && Text(entry, "domainName").Length == 0;

    private static string Text(ConfigurationElement element, string attribute) =>
        Convert.ToString(element.GetAttributeValue(attribute), CultureInfo.InvariantCulture)?.Trim() ?? "";

    /// <summary>IIS configuration errors span several lines ("Filename: …", "Error: …"); joins them into one.</summary>
    private static string OneLine(string message) =>
        string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
