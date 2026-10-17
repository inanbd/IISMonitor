using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.Administration;
using Microsoft.Win32;

namespace IISMonitor.Collectors.Blocking;

/// <summary>
/// An address or network as IIS ipSecurity stores it: base address plus prefix length. Equality is by
/// value, so "203.0.113.5" with no mask, with mask "255.255.255.255" and with mask "32" are the same key.
/// </summary>
internal readonly record struct IpBlockKey
{
    public IpBlockKey(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address);
        var max = MaxPrefix(address);
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, max);
        Address = MaskOff(address, prefixLength);
        PrefixLength = prefixLength;
    }

    public IPAddress Address { get; }
    public int PrefixLength { get; }

    public bool IsSingleAddress => PrefixLength == MaxPrefix(Address);

    public static IpBlockKey Single(IPAddress address)
    {
        var normalized = Normalize(address);
        return new IpBlockKey(normalized, MaxPrefix(normalized));
    }

    public bool Overlaps(IpBlockKey other)
    {
        if (Address.AddressFamily != other.Address.AddressFamily)
            return false;
        var shorter = Math.Min(PrefixLength, other.PrefixLength);
        return MaskOff(Address, shorter).Equals(MaskOff(other.Address, shorter));
    }

    public override string ToString() => $"{Address}/{PrefixLength}";

    public static bool TryParse(string? text, out IpBlockKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
            return false;
        address = Normalize(address);
        var prefix = MaxPrefix(address);
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix > MaxPrefix(address)))
            return false;
        key = new IpBlockKey(address, prefix);
        return true;
    }

    /// <summary>
    /// The subnetMask value to write, or null to leave the schema default (255.255.255.255), which is what
    /// IIS Manager writes for one IPv4 address. IPv6 gets an explicit prefix length: IIS Manager accepts
    /// 0-128 there, but whether the module treats the IPv4-looking default as /128 is not documented.
    /// </summary>
    internal string? ConfigMask => Address.AddressFamily == AddressFamily.InterNetwork
        ? (PrefixLength == 32 ? null : PrefixToIPv4Mask(PrefixLength))
        : PrefixLength.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads an entry as IIS stores it. Accepts dotted masks, IPv6-style masks and prefix lengths.</summary>
    internal static bool TryFromConfig(string? ipAddress, string? subnetMask, out IpBlockKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var address))
            return false;
        address = Normalize(address);
        var max = MaxPrefix(address);
        var mask = subnetMask?.Trim();
        int prefix;
        if (string.IsNullOrEmpty(mask) || mask == "255.255.255.255")
            prefix = max;   // the schema default; taken to mean "this one address" for IPv6 too
        else if (int.TryParse(mask, NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
        {
            if (prefix > max)
                return false;
        }
        else if (!IPAddress.TryParse(mask, out var maskAddress) || !TryCountPrefix(maskAddress.GetAddressBytes(), out prefix)
            || prefix > max)
            return false;
        key = new IpBlockKey(address, prefix);
        return true;
    }

    internal static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());   // drop %zone
        return address;
    }

    private static int MaxPrefix(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

    private static IPAddress MaskOff(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsLeft = prefixLength - (i * 8);
            bytes[i] &= bitsLeft >= 8 ? (byte)0xFF : bitsLeft <= 0 ? (byte)0 : (byte)(0xFF << (8 - bitsLeft));
        }
        return new IPAddress(bytes);
    }

    private static string PrefixToIPv4Mask(int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return new IPAddress([(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask]).ToString();
    }

    private static bool TryCountPrefix(byte[] mask, out int prefix)
    {
        prefix = 0;
        var seenZero = false;
        foreach (var b in mask)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var one = (b & (1 << bit)) != 0;
                if (one && seenZero)
                    return false;   // not a contiguous mask
                if (one)
                    prefix++;
                else
                    seenZero = true;
            }
        }
        return true;
    }
}

/// <summary>One entry of the server-level ipSecurity list, in list order.</summary>
internal sealed record IpSecurityEntry(int Index, string IpAddress, string SubnetMask, string DomainName, bool Allowed, IpBlockKey? Key, bool AddedByUs);

/// <summary>Whether IIS can enforce our deny entries, and what stands in the way.</summary>
internal sealed record IisIpSecurityStatus(
    bool ModuleDllPresent,
    bool ComponentRegistered,
    bool ModuleInGlobalModules,
    bool ModuleInServerModules,
    bool ProxyModeEnabled,
    bool AllowUnlisted,
    string DenyAction,
    int ServerEntryCount,
    int OwnedEntryCount,
    IReadOnlyList<string> Problems)
{
    /// <summary>The module is loaded server-wide and reads X-Forwarded-For. Per-site problems are in <see cref="Problems"/>.</summary>
    public bool CanBlockProxiedVisitors => ModuleDllPresent && ModuleInGlobalModules && ModuleInServerModules && ProxyModeEnabled;
}

internal sealed class IisBlockChangeResult
{
    public List<IpBlockKey> Added { get; } = [];
    public List<IpBlockKey> Removed { get; } = [];
    /// <summary>A deny entry for this key already exists and was not added by us: the visitor is blocked, but not by us.</summary>
    public List<IpBlockKey> AlreadyDeniedByAdmin { get; } = [];
    /// <summary>An administrator's allow entry covers this key; we never override it.</summary>
    public List<IpBlockKey> AllowedByAdmin { get; } = [];
    /// <summary>Loopback, the server's own addresses, the allow-list, trusted proxies, or over the entry limit.</summary>
    public List<IpBlockKey> Refused { get; } = [];
    /// <summary>Unblock asked for an entry we did not add; it is left alone.</summary>
    public List<IpBlockKey> NotOurs { get; } = [];
    public bool Committed { get; internal set; }
}

/// <summary>
/// Blocks visitors that arrive through a trusted reverse proxy or CDN by adding deny entries to the
/// server-level system.webServer/security/ipSecurity list (root of applicationHost.config), with proxy
/// mode on so IIS also checks X-Forwarded-For. Server-level entries are inherited by every site unless
/// a site clears them; <see cref="GetStatus"/> reports sites where that happened.
/// The schema has no description attribute and rejects unknown ones, so the entries we added are
/// tracked in a ledger file; entries an administrator added are never removed or claimed.
/// </summary>
internal sealed class IisIpBlocker
{
    private const string SectionPath = "system.webServer/security/ipSecurity";
    private const string ModuleName = "IpRestrictionModule";
    private const int MaxAttempts = 3;
    private static readonly string[] DenyActions = ["AbortRequest", "Unauthorized", "Forbidden", "NotFound"];

    private readonly object _gate = new();
    private readonly OwnedEntryLedger _ledger;
    private readonly Func<IReadOnlyCollection<IpBlockKey>> _protectedNetworks;
    private readonly int _maxOwnedEntries;

    /// <param name="ledgerPath">JSON file in the secured data directory (create it with SecureDataDirectory.Ensure).</param>
    /// <param name="protectedNetworks">Loopback, the server's own addresses, the allow-list and the trusted proxies. Never denied.</param>
    /// <param name="maxOwnedEntries">Upper bound on entries we add; the per-request cost of a long list is not documented.</param>
    public IisIpBlocker(string ledgerPath, Func<IReadOnlyCollection<IpBlockKey>> protectedNetworks, int maxOwnedEntries = 5000)
    {
        _ledger = new OwnedEntryLedger(ledgerPath);
        _protectedNetworks = protectedNetworks;
        _maxOwnedEntries = maxOwnedEntries;
    }

    public IisIpSecurityStatus GetStatus(bool checkEverySite = true)
    {
        lock (_gate)
        {
            var problems = new List<string>();
            var dll = File.Exists(Path.Combine(Environment.SystemDirectory, "inetsrv", "iprestr.dll"));
            var registered = ComponentRegistered("IPSecurity");
            if (!dll)
                problems.Add("IP and Domain Restrictions is not installed (Install-WindowsFeature Web-IP-Security). Without it ipSecurity entries are saved but not enforced.");

            using var manager = new ServerManager();
            var config = manager.GetApplicationHostConfiguration();
            var inGlobal = ContainsNamed(config.GetSection("system.webServer/globalModules").GetCollection(), ModuleName);
            var inModules = ContainsNamed(config.GetSection("system.webServer/modules").GetCollection(), ModuleName);
            if (dll && !(inGlobal && inModules))
                problems.Add("IpRestrictionModule is not enabled at server level (globalModules/modules).");

            ConfigurationSection section;
            try
            {
                section = config.GetSection(SectionPath);
            }
            catch (COMException e)
            {
                problems.Add("The ipSecurity section can't be read: " + e.Message);
                return new IisIpSecurityStatus(dll, registered, inGlobal, inModules, false, true, "Forbidden", 0, 0, problems);
            }

            var proxyMode = AsBool(section.GetAttributeValue("enableProxyMode"));
            var allowUnlisted = AsBool(section.GetAttributeValue("allowUnlisted"));
            var denyAction = DenyActionName(section.GetAttributeValue("denyAction"));
            if (!proxyMode)
                problems.Add("Proxy mode is off at server level: X-Forwarded-For is not checked.");
            if (!allowUnlisted)
                problems.Add("The server denies unlisted clients (allowUnlisted=false); deny entries change nothing there.");
            if (denyAction == "NotFound")
                problems.Add("denyAction=NotFound: a CDN may cache the 404 and serve it to other visitors.");

            var entries = ReadEntries(section.GetCollection(), _ledger.Keys);
            var owned = entries.Where(e => e.AddedByUs && !e.Allowed && e.Key is not null).Select(e => e.Key!.Value).ToHashSet();
            if (checkEverySite)
                FindUncoveredPaths(manager, owned, problems);

            return new IisIpSecurityStatus(dll, registered, inGlobal, inModules, proxyMode, allowUnlisted, denyAction,
                entries.Count, owned.Count, problems);
        }
    }

    /// <summary>Turns proxy mode on at server level and, if asked, sets the deny action. Returns whether anything changed.</summary>
    public bool EnsureServerSettings(string? denyAction = null)
    {
        if (denyAction is not null && !DenyActions.Contains(denyAction, StringComparer.Ordinal))
            throw new ArgumentException("Unknown deny action.", nameof(denyAction));

        lock (_gate)
        {
            using var manager = new ServerManager();
            var section = manager.GetApplicationHostConfiguration().GetSection(SectionPath);
            var changed = false;
            if (!AsBool(section.GetAttributeValue("enableProxyMode")))
            {
                section["enableProxyMode"] = true;
                changed = true;
            }
            if (denyAction is not null && DenyActionName(section.GetAttributeValue("denyAction")) != denyAction)
            {
                section["denyAction"] = denyAction;   // enum attributes accept the name; they read back as numbers
                changed = true;
            }
            if (changed)
                manager.CommitChanges();
            return changed;
        }
    }

    /// <summary>The server-level list in IIS order, marking the entries we added.</summary>
    public IReadOnlyList<IpSecurityEntry> ListServerEntries()
    {
        lock (_gate)
        {
            using var manager = new ServerManager();
            var section = manager.GetApplicationHostConfiguration().GetSection(SectionPath);
            return ReadEntries(section.GetCollection(), _ledger.Keys);
        }
    }

    /// <summary>
    /// Adds and removes deny entries in one commit (each commit rewrites applicationHost.config and can
    /// restart ASP.NET apps, so batch). New entries go to the top of the list, ahead of any allow entries.
    /// </summary>
    public IisBlockChangeResult Apply(IReadOnlyCollection<IpBlockKey> block, IReadOnlyCollection<IpBlockKey> unblock)
    {
        lock (_gate)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return ApplyOnce(block, unblock);
                }
                catch (COMException) when (attempt < MaxAttempts)
                {
                    // Usually another writer (IIS Manager, appcmd) changed or locked the file, or added the same
                    // entry (0x800700B7, duplicate combined key) between our read and commit: reload and retry.
                    Thread.Sleep(250 * attempt);
                }
            }
        }
    }

    /// <summary>Forgets ledger keys whose entries an administrator removed by hand. Returns how many were dropped.</summary>
    public int Reconcile()
    {
        lock (_gate)
        {
            using var manager = new ServerManager();
            var section = manager.GetApplicationHostConfiguration().GetSection(SectionPath);
            var present = new HashSet<IpBlockKey>();
            foreach (var element in section.GetCollection())
            {
                if (TryReadKey(element, out var key, out var allowed) && !allowed)
                    present.Add(key);
            }
            var keep = _ledger.Keys.Where(present.Contains).ToList();
            var dropped = _ledger.Keys.Count - keep.Count;
            if (dropped > 0)
                _ledger.Save(keep);
            return dropped;
        }
    }

    private IisBlockChangeResult ApplyOnce(IReadOnlyCollection<IpBlockKey> block, IReadOnlyCollection<IpBlockKey> unblock)
    {
        var result = new IisBlockChangeResult();
        using var manager = new ServerManager();
        var section = manager.GetApplicationHostConfiguration().GetSection(SectionPath);
        var collection = section.GetCollection();

        var before = _ledger.Keys.ToList();
        var owned = before.ToHashSet();
        var existing = new Dictionary<IpBlockKey, List<(ConfigurationElement Element, bool Allowed)>>();
        foreach (var element in collection)
        {
            if (!TryReadKey(element, out var key, out var allowed))
                continue;
            if (!existing.TryGetValue(key, out var list))
                existing[key] = list = [];
            list.Add((element, allowed));
        }

        foreach (var key in unblock.Distinct())
        {
            if (!owned.Remove(key))
            {
                result.NotOurs.Add(key);
                continue;
            }
            if (existing.Remove(key, out var list))
            {
                foreach (var (element, allowed) in list)
                {
                    if (!allowed)
                        collection.Remove(element);
                }
            }
            result.Removed.Add(key);
        }

        var protectedNetworks = _protectedNetworks();
        var insertAt = 0;
        foreach (var key in block.Distinct())
        {
            if (protectedNetworks.Any(key.Overlaps) || owned.Count >= _maxOwnedEntries)
            {
                result.Refused.Add(key);
                continue;
            }
            if (existing.TryGetValue(key, out var list) && list.Count > 0)
            {
                if (list.Any(e => e.Allowed))
                    result.AllowedByAdmin.Add(key);
                else if (!owned.Contains(key))
                    result.AlreadyDeniedByAdmin.Add(key);
                continue;
            }

            var element = collection.CreateElement("add");
            element["ipAddress"] = key.Address.ToString();
            if (key.ConfigMask is { } mask)
                element["subnetMask"] = mask;
            element["allowed"] = false;
            collection.AddAt(insertAt++, element);   // deny entries first: IIS evaluates the list top to bottom
            owned.Add(key);
            result.Added.Add(key);
        }

        if (result.Added.Count == 0 && result.Removed.Count == 0)
            return result;

        // Record intent before committing, so a crash leaves at most a ledger key without an entry,
        // which Reconcile drops, never an entry of ours that we no longer recognise.
        _ledger.Save(owned);
        try
        {
            manager.CommitChanges();
        }
        catch
        {
            _ledger.Save(before);
            throw;
        }
        result.Committed = true;
        return result;
    }

    /// <summary>Reports sites and applications where our entries, proxy mode or the module don't apply.</summary>
    private static void FindUncoveredPaths(ServerManager manager, HashSet<IpBlockKey> owned, List<string> problems)
    {
        foreach (var site in manager.Sites)
        {
            foreach (var app in site.Applications)
            {
                var label = app.Path == "/" ? site.Name : site.Name + app.Path;
                try
                {
                    // GetWebConfiguration merges applicationHost.config <location> tags and web.config files.
                    var config = manager.GetWebConfiguration(site.Name, app.Path);
                    var section = config.GetSection(SectionPath);
                    if (!AsBool(section.GetAttributeValue("enableProxyMode")))
                        problems.Add($"{label}: proxy mode is turned off here.");
                    if (!ContainsNamed(config.GetSection("system.webServer/modules").GetCollection(), ModuleName))
                        problems.Add($"{label}: IpRestrictionModule is removed here.");
                    if (owned.Count == 0)
                        continue;
                    var effective = new HashSet<IpBlockKey>();
                    foreach (var element in section.GetCollection())
                    {
                        if (TryReadKey(element, out var key, out var allowed) && !allowed)
                            effective.Add(key);
                    }
                    var missing = owned.Count(k => !effective.Contains(k));
                    if (missing > 0)
                        problems.Add($"{label}: {missing} of our deny entries don't reach it (<clear/> or <remove/> below server level).");
                }
                catch (COMException e)
                {
                    problems.Add($"{label}: can't read its configuration ({e.Message}).");
                }
                catch (FileNotFoundException e)
                {
                    problems.Add($"{label}: can't read its configuration ({e.Message}).");
                }
            }
        }
    }

    private static List<IpSecurityEntry> ReadEntries(ConfigurationElementCollection collection, IReadOnlyCollection<IpBlockKey> ledger)
    {
        var owned = ledger as ISet<IpBlockKey> ?? ledger.ToHashSet();
        var entries = new List<IpSecurityEntry>(collection.Count);
        var index = 0;
        foreach (var element in collection)
        {
            var ip = Convert.ToString(element.GetAttributeValue("ipAddress"), CultureInfo.InvariantCulture) ?? "";
            var mask = Convert.ToString(element.GetAttributeValue("subnetMask"), CultureInfo.InvariantCulture) ?? "";
            var domain = Convert.ToString(element.GetAttributeValue("domainName"), CultureInfo.InvariantCulture) ?? "";
            var allowed = AsBool(element.GetAttributeValue("allowed"));
            IpBlockKey? key = domain.Length == 0 && IpBlockKey.TryFromConfig(ip, mask, out var k) ? k : null;
            entries.Add(new IpSecurityEntry(index++, ip, mask, domain, allowed, key, key is { } kk && !allowed && owned.Contains(kk)));
        }
        return entries;
    }

    private static bool TryReadKey(ConfigurationElement element, out IpBlockKey key, out bool allowed)
    {
        allowed = AsBool(element.GetAttributeValue("allowed"));
        key = default;
        var domain = Convert.ToString(element.GetAttributeValue("domainName"), CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(domain)
            && IpBlockKey.TryFromConfig(
                Convert.ToString(element.GetAttributeValue("ipAddress"), CultureInfo.InvariantCulture),
                Convert.ToString(element.GetAttributeValue("subnetMask"), CultureInfo.InvariantCulture),
                out key);
    }

    private static bool ContainsNamed(ConfigurationElementCollection collection, string name) =>
        collection.Any(e => string.Equals(Convert.ToString(e.GetAttributeValue("name"), CultureInfo.InvariantCulture), name, StringComparison.OrdinalIgnoreCase));

    private static bool AsBool(object? value) => value is bool b ? b : Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static string DenyActionName(object? value) => value switch
    {
        string s when DenyActions.Contains(s, StringComparer.OrdinalIgnoreCase) => s,
        IConvertible c => c.ToInt32(CultureInfo.InvariantCulture) switch
        {
            0 => "AbortRequest",
            401 => "Unauthorized",
            404 => "NotFound",
            _ => "Forbidden",
        },
        _ => "Forbidden",
    };

    private static bool ComponentRegistered(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp\Components");
            return key?.GetValue(name) is int value && value == 1;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The keys of the entries we added, kept next to the other service data.</summary>
    private sealed class OwnedEntryLedger(string path)
    {
        private HashSet<IpBlockKey> _keys = Load(path);

        public IReadOnlyCollection<IpBlockKey> Keys => _keys;

        public void Save(IEnumerable<IpBlockKey> keys)
        {
            var set = keys.ToHashSet();
            var json = JsonSerializer.Serialize(set.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToArray());
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            _keys = set;
        }

        private static HashSet<IpBlockKey> Load(string path)
        {
            if (!File.Exists(path))
                return [];
            var keys = new HashSet<IpBlockKey>();
            foreach (var text in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
            {
                if (IpBlockKey.TryParse(text, out var key))
                    keys.Add(key);
            }
            return keys;
        }
    }
}
