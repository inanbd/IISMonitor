using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using IISMonitor.Collectors.Blocking.Interop;
using Microsoft.Win32;

namespace IISMonitor.Collectors.Blocking;

/// <summary>What the firewall will do with our rules right now.</summary>
internal sealed record FirewallStatus(
    bool Reachable,
    string ActiveProfiles,
    bool EnabledOnAllActiveProfiles,
    bool BlockAllInboundOnActiveProfile,
    string LocalPolicyModifyState,
    bool LocalRulesIgnoredByGroupPolicy,
    string? Problem)
{
    /// <summary>True when an inbound block rule we add will actually be enforced.</summary>
    public bool BlocksTakeEffect => Reachable && EnabledOnAllActiveProfiles && !LocalRulesIgnoredByGroupPolicy
        && LocalPolicyModifyState == nameof(NetFwModifyState.Ok);
}

internal sealed class FirewallBlockerException(string message, int hResult, Exception inner) : Exception(message, inner)
{
    public int ErrorCode { get; } = hResult;
}

/// <summary>
/// Blocks remote addresses with inbound Windows Defender Firewall rules that this app owns. Addresses are
/// packed into rules named "<see cref="RuleNamePrefix"/>NNN" (at most <see cref="MaxAddressesPerRule"/> each),
/// all in rule group <see cref="RuleGroup"/>, so they can be found and removed without touching anyone else's rules.
/// Requires administrator rights (the service runs as LocalSystem). Thread-safe; calls are serialized.
/// </summary>
internal sealed class FirewallBlocker
{
    public const string RuleNamePrefix = "IIS Monitor - blocked addresses #";
    public const string RuleGroup = "IIS Monitor";
    public const string DescriptionMarker = "[iismonitor:auto-blacklist]";
    public const int DefaultMaxAddressesPerRule = 1000;

    private const int HResultNotFound = unchecked((int)0x80070002);      // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
    private const int HResultAccessDenied = unchecked((int)0x80070005);  // E_ACCESSDENIED
    private const int HResultInvalidArg = unchecked((int)0x80070057);    // E_INVALIDARG
    private const int HResultNoEndpoints = unchecked((int)0x800706D9);   // EPT_S_NOT_REGISTERED: MpsSvc not running
    private const int HResultServiceDisabled = unchecked((int)0x80070422); // ERROR_SERVICE_DISABLED
    private const int HResultRpcUnavailable = unchecked((int)0x800706BA);  // RPC_S_SERVER_UNAVAILABLE

    // Narrowest networks we accept in our rules. Anything wider (0.0.0.0/0, a /8...) would block
    // huge numbers of people, so a hand-edited rule holding one is repaired, never written back.
    private const int MinIpv4Prefix = 16;
    private const int MinIpv6Prefix = 32;

    private static readonly NetFwProfileType2[] SingleProfiles =
        [NetFwProfileType2.Domain, NetFwProfileType2.Private, NetFwProfileType2.Public];

    private readonly Lock _gate = new();
    private readonly Func<IPAddress, bool> _isProtected;

    // Rule index -> addresses in that rule, as last read or written by us. Null until first loaded.
    private SortedDictionary<int, SortedSet<string>>? _rules;

    /// <param name="isProtected">Extra never-block test (allow-list, trusted proxies). Loopback, unspecified,
    /// multicast and this machine's own addresses are always refused.</param>
    /// <param name="maxAddressesPerRule">Addresses per rule. 1000 is what IPBan uses in production.</param>
    public FirewallBlocker(Func<IPAddress, bool>? isProtected = null, int maxAddressesPerRule = DefaultMaxAddressesPerRule)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAddressesPerRule, 1);
        _isProtected = isProtected ?? (_ => false);
        MaxAddressesPerRule = maxAddressesPerRule;
    }

    public int MaxAddressesPerRule { get; }

    // ---------------------------------------------------------------- status

    public FirewallStatus GetStatus()
    {
        lock (_gate)
        {
            INetFwPolicy2? policy = null;
            try
            {
                policy = CreatePolicy();
                var current = policy.CurrentProfileTypes;
                var names = new List<string>();
                var enabled = true;
                var blockAll = false;
                foreach (var profile in SingleProfiles)
                {
                    if ((current & (int)profile) == 0)
                        continue;
                    names.Add(profile.ToString());
                    enabled &= policy.get_FirewallEnabled(profile);
                    blockAll |= policy.get_BlockAllInboundTraffic(profile);
                }

                var modify = policy.LocalPolicyModifyState;
                var mergeOff = LocalRulesIgnoredByGroupPolicy(current);
                string? problem = null;
                if (!enabled)
                    problem = $"Windows Defender Firewall is turned off for the active profile ({string.Join(", ", names)}), so block rules are not enforced.";
                else if (mergeOff)
                    problem = "Group Policy sets 'Apply local firewall rules' to No for the active profile, so locally created block rules are ignored.";
                else if (modify == NetFwModifyState.GroupPolicyOverride)
                    problem = "The active firewall profile is controlled by Group Policy; locally created rules may not take effect.";
                else if (modify == NetFwModifyState.InboundBlocked || blockAll)
                    problem = "The active profile is set to block all incoming connections, which ignores inbound rules: check that this is intended.";

                return new FirewallStatus(true, string.Join(", ", names), enabled, blockAll, modify.ToString(), mergeOff, problem);
            }
            catch (Exception ex) when (IsComFailure(ex))
            {
                return new FirewallStatus(false, "", false, false, "Unknown", false, Describe(ex));
            }
            finally
            {
                Release(policy);
            }
        }
    }

    /// <summary>
    /// Group Policy "Apply local firewall rules: No" is stored as AllowLocalPolicyMerge = 0 under the policy key of
    /// each profile. Not visible through INetFwPolicy2, so read the registry. (MDM/Intune uses a different store.)
    /// </summary>
    private static bool LocalRulesIgnoredByGroupPolicy(int currentProfiles)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var (profile, key) in new[]
                     {
                         (NetFwProfileType2.Domain, "DomainProfile"),
                         (NetFwProfileType2.Private, "PrivateProfile"),
                         (NetFwProfileType2.Public, "PublicProfile"),
                     })
            {
                if ((currentProfiles & (int)profile) == 0)
                    continue;
                using var sub = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + key);
                if (sub?.GetValue("AllowLocalPolicyMerge") is int value && value == 0)
                    return true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Can't tell; report as not ignored.
        }

        return false;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Every address (or subnet/range) currently in our rules, read fresh from the firewall.</summary>
    public IReadOnlyList<string> ListBlocked()
    {
        lock (_gate)
        {
            return Run(rules => Load(rules, force: true).Values.SelectMany(s => s).Distinct().Order(StringComparer.Ordinal).ToList());
        }
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Adds addresses to our rules. Returns the canonical addresses that were actually added.</summary>
    public IReadOnlyList<string> Block(IEnumerable<IPAddress> addresses)
    {
        lock (_gate)
        {
            var own = OwnAddresses();
            var wanted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var address in addresses)
            {
                var ip = Normalize(address);
                if (IsAlwaysProtected(ip, own) || _isProtected(ip))
                    continue;
                wanted.Add(ip.ToString());
            }

            if (wanted.Count == 0)
                return [];

            return Run(rules =>
            {
                var map = Load(rules, force: false);
                wanted.ExceptWith(map.Values.SelectMany(s => s));
                var added = wanted.ToList();
                var pending = new Queue<string>(added);

                // Top up existing rules first (each RemoteAddresses write is one commit), then create new ones.
                foreach (var index in map.Where(r => r.Value.Count < MaxAddressesPerRule).Select(r => r.Key).ToList())
                {
                    if (pending.Count == 0)
                        break;
                    var name = RuleName(index);
                    var live = ReadLive(rules, name);
                    if (live is null)
                    {
                        map.Remove(index); // deleted by hand
                        continue;
                    }

                    var room = MaxAddressesPerRule - live.Count;
                    if (room <= 0)
                    {
                        map[index] = live;
                        continue;
                    }

                    for (var i = 0; i < room && pending.Count > 0; i++)
                        live.Add(pending.Dequeue());
                    WriteAddresses(rules, name, live);
                    map[index] = live;
                }

                var next = map.Count == 0 ? 1 : map.Keys.Max() + 1;
                while (pending.Count > 0)
                {
                    var chunk = new SortedSet<string>(StringComparer.Ordinal);
                    while (chunk.Count < MaxAddressesPerRule && pending.Count > 0)
                        chunk.Add(pending.Dequeue());

                    var name = RuleName(next);
                    RemoveAllNamed(rules, name); // never leave a duplicate name behind
                    AddRule(rules, name, chunk);
                    map[next] = chunk;
                    next++;
                }

                return added;
            });
        }
    }

    /// <summary>Removes addresses from our rules; deletes a rule when it becomes empty. Returns how many were removed.</summary>
    public int Unblock(IEnumerable<IPAddress> addresses)
    {
        lock (_gate)
        {
            var remove = new HashSet<string>(addresses.Select(a => Normalize(a).ToString()), StringComparer.Ordinal);
            if (remove.Count == 0)
                return 0;

            return Run(rules =>
            {
                var map = Load(rules, force: false);
                var removed = 0;
                foreach (var (index, cached) in map.ToList())
                {
                    if (!cached.Overlaps(remove))
                        continue;
                    var name = RuleName(index);
                    var live = ReadLive(rules, name);
                    if (live is null)
                    {
                        map.Remove(index);
                        continue;
                    }

                    var before = live.Count;
                    live.ExceptWith(remove);
                    removed += before - live.Count;
                    if (live.Count == 0)
                    {
                        // An empty RemoteAddresses means "*" (every address): delete the rule instead.
                        RemoveAllNamed(rules, name);
                        map.Remove(index);
                    }
                    else if (live.Count != before)
                    {
                        WriteAddresses(rules, name, live);
                        map[index] = live;
                    }
                }

                return removed;
            });
        }
    }

    /// <summary>
    /// Makes our rules contain exactly <paramref name="desired"/> (call at startup with the persisted block list,
    /// after dropping expired blocks). Repacks rules densely. Returns the number of rules written or removed.
    /// </summary>
    public int Sync(IEnumerable<IPAddress> desired)
    {
        lock (_gate)
        {
            var own = OwnAddresses();
            var target = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var address in desired)
            {
                var ip = Normalize(address);
                if (!IsAlwaysProtected(ip, own) && !_isProtected(ip))
                    target.Add(ip.ToString());
            }

            return Run(rules =>
            {
                var current = Load(rules, force: true);
                var chunks = target.Chunk(MaxAddressesPerRule).Select((c, i) => (Index: i + 1, Set: new SortedSet<string>(c, StringComparer.Ordinal))).ToList();
                var changes = 0;
                foreach (var (index, set) in chunks)
                {
                    var name = RuleName(index);
                    if (current.TryGetValue(index, out var existing) && existing.SetEquals(set))
                        continue;
                    if (current.ContainsKey(index))
                        WriteAddresses(rules, name, set);
                    else
                        AddRule(rules, name, set);
                    changes++;
                }

                foreach (var index in current.Keys.Where(i => i > chunks.Count))
                {
                    RemoveAllNamed(rules, RuleName(index));
                    changes++;
                }

                _rules = new SortedDictionary<int, SortedSet<string>>(chunks.ToDictionary(c => c.Index, c => c.Set));
                return changes;
            });
        }
    }

    /// <summary>Deletes every rule we created (uninstall, or "unblock all"). Returns the number of rules removed.</summary>
    public int RemoveAllRules()
    {
        lock (_gate)
        {
            return Run(rules =>
            {
                var names = EnumerateOurRules(rules).Select(r => r.Name).Distinct(StringComparer.Ordinal).ToList();
                foreach (var name in names)
                    RemoveAllNamed(rules, name);
                _rules = new SortedDictionary<int, SortedSet<string>>();
                return names.Count;
            });
        }
    }

    // ---------------------------------------------------------------- address helpers

    /// <summary>IPv4-mapped IPv6 becomes IPv4; IPv6 zone ids are dropped (the firewall does not take them).</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());
        return address;
    }

    private static bool IsAlwaysProtected(IPAddress ip, HashSet<IPAddress> own)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6Multicast)
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            ip.TryWriteBytes(b, out _);
            if (b[0] is >= 224 and <= 239)
                return true;
        }

        return own.Contains(ip);
    }

    private static HashSet<IPAddress> OwnAddresses()
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    set.Add(Normalize(unicast.Address));
        }
        catch (NetworkInformationException)
        {
            // Fall back to the static checks only.
        }

        return set;
    }

    /// <summary>
    /// Turns a token read back from RemoteAddresses into our canonical form. The firewall may return single hosts
    /// with a mask ("1.2.3.4/255.255.255.255"); those become "1.2.3.4". Subnets become "addr/prefix"; ranges are
    /// kept as written. Returns false for keywords and "*".
    /// </summary>
    internal static bool TryCanonicalize(string token, out string canonical)
    {
        canonical = "";
        token = token.Trim();
        if (token.Length == 0 || token == "*")
            return false;

        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            // IPv6 text never contains '-', so this is "start-end".
            if (!IPAddress.TryParse(token[..dash], out var start) || !IPAddress.TryParse(token[(dash + 1)..], out var end))
                return false;
            start = Normalize(start);
            end = Normalize(end);
            if (start.Equals(end))
            {
                canonical = start.ToString();
                return true;
            }

            if (start.AddressFamily != end.AddressFamily || RangeTooWide(start, end))
                return false;
            canonical = $"{start}-{end}";
            return true;
        }

        var slash = token.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? token : token[..slash], out var ip))
            return false; // LocalSubnet, DNS, DHCP, WINS, DefaultGateway, ...
        ip = Normalize(ip);
        var maxBits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxBits;
        if (slash >= 0)
        {
            var mask = token[(slash + 1)..];
            if (int.TryParse(mask, NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
                prefix = bits;
            else if (!(IPAddress.TryParse(mask, out var dotted) && TryMaskToPrefix(dotted, out prefix)))
                return false;
            if (prefix < 0 || prefix > maxBits)
                return false;
        }

        if (prefix < (ip.AddressFamily == AddressFamily.InterNetwork ? MinIpv4Prefix : MinIpv6Prefix))
            return false;
        canonical = prefix == maxBits ? ip.ToString() : $"{ip}/{prefix}";
        return true;
    }

    /// <summary>A range covering more than a /16 (IPv4) or /32 (IPv6).</summary>
    private static bool RangeTooWide(IPAddress start, IPAddress end)
    {
        var a = start.GetAddressBytes();
        var b = end.GetAddressBytes();
        var fixedBytes = start.AddressFamily == AddressFamily.InterNetwork ? MinIpv4Prefix / 8 : MinIpv6Prefix / 8;
        for (var i = 0; i < fixedBytes; i++)
        {
            if (a[i] != b[i])
                return true;
        }

        return false;
    }

    private static bool TryMaskToPrefix(IPAddress mask, out int prefix)
    {
        prefix = 0;
        if (mask.AddressFamily != AddressFamily.InterNetwork)
            return false;
        Span<byte> b = stackalloc byte[4];
        mask.TryWriteBytes(b, out _);
        var value = BinaryPrimitives.ReadUInt32BigEndian(b);
        prefix = BitOperations.PopCount(value);
        return value == (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix));
    }

    // ---------------------------------------------------------------- COM plumbing

    private static string RuleName(int index) => RuleNamePrefix + index.ToString("D3", CultureInfo.InvariantCulture);

    private static bool TryParseIndex(string name, out int index)
    {
        index = 0;
        return name.StartsWith(RuleNamePrefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(RuleNamePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index > 0;
    }

    private T Run<T>(Func<INetFwRules, T> action)
    {
        INetFwPolicy2? policy = null;
        INetFwRules? rules = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            return action(rules);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            _rules = null; // state unknown; reload next time
            throw new FirewallBlockerException(Describe(ex), ex.HResult, ex);
        }
        finally
        {
            Release(rules);
            Release(policy);
        }
    }

    private SortedDictionary<int, SortedSet<string>> Load(INetFwRules rules, bool force)
    {
        if (_rules is not null && !force)
            return _rules;

        var found = new SortedDictionary<int, List<string>>();
        foreach (var (name, remote) in EnumerateOurRules(rules))
        {
            if (!TryParseIndex(name, out var index))
                continue;
            if (!found.TryGetValue(index, out var list))
                found[index] = list = [];
            list.Add(remote);
        }

        var map = new SortedDictionary<int, SortedSet<string>>();
        foreach (var (index, remotes) in found)
        {
            var union = new SortedSet<string>(StringComparer.Ordinal);
            var clean = remotes.Count == 1;
            foreach (var remote in remotes)
            {
                union.UnionWith(ParseAddresses(remote, out var ok));
                clean &= ok;
            }

            if (!clean)
            {
                // A duplicate name, or a hand-edited list holding "*" (which blocks everyone) or keywords:
                // replace it with one rule holding only the valid addresses.
                var name = RuleName(index);
                RemoveAllNamed(rules, name);
                if (union.Count == 0)
                    continue;
                AddRule(rules, name, union);
            }

            map[index] = union;
        }

        _rules = map;
        return map;
    }

    /// <summary>Re-reads one of our rules by name (repairing it if it was edited); null if it no longer exists.</summary>
    private static SortedSet<string>? ReadLive(INetFwRules rules, string name)
    {
        var rule = TryGetRule(rules, name);
        if (rule is null)
            return null;
        SortedSet<string> set;
        bool clean;
        try
        {
            set = ParseAddresses(rule.RemoteAddresses, out clean);
        }
        finally
        {
            Release(rule);
        }

        if (clean)
            return set;
        if (set.Count == 0)
        {
            RemoveAllNamed(rules, name);
            return null;
        }

        WriteAddresses(rules, name, set);
        return set;
    }

    /// <summary>Canonical addresses in a RemoteAddresses string; <paramref name="clean"/> is false if any token
    /// was "*", a keyword or unparsable, or the string was empty (which the firewall treats as "*").</summary>
    private static SortedSet<string> ParseAddresses(string? remoteAddresses, out bool clean)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        clean = !string.IsNullOrWhiteSpace(remoteAddresses);
        if (!clean)
            return set;
        foreach (var token in remoteAddresses!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryCanonicalize(token, out var canonical))
                set.Add(canonical);
            else
                clean = false;
        }

        return set;
    }

    private static List<(string Name, string RemoteAddresses)> EnumerateOurRules(INetFwRules rules)
    {
        var result = new List<(string, string)>();
        var enumerator = (IEnumVARIANT)rules.NewEnum;
        var batch = new object?[64];
        var fetched = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                Marshal.WriteInt32(fetched, 0);
                var hr = enumerator.Next(batch.Length, batch, fetched);
                var count = Marshal.ReadInt32(fetched);
                for (var i = 0; i < count; i++)
                {
                    var item = batch[i];
                    batch[i] = null;
                    try
                    {
                        if (item is INetFwRule rule
                            && string.Equals(rule.Grouping, RuleGroup, StringComparison.Ordinal)
                            && rule.Name is { } name
                            && name.StartsWith(RuleNamePrefix, StringComparison.Ordinal))
                        {
                            result.Add((name, rule.RemoteAddresses ?? ""));
                        }
                    }
                    finally
                    {
                        Release(item);
                    }
                }

                if (hr != 0 || count == 0) // S_FALSE: fewer than requested, i.e. the end
                    break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fetched);
            Release(enumerator);
        }

        return result;
    }

    private static void AddRule(INetFwRules rules, string name, IReadOnlyCollection<string> addresses)
    {
        var text = JoinChecked(addresses);
        var rule = CreateCom<INetFwRule>(NetFwClsid.Rule);
        try
        {
            // Every property set is validated; Protocol must be set before any port property.
            rule.Name = name;
            rule.Description = DescriptionMarker + " Addresses blocked automatically by IIS Monitor. Unblock them from the IIS Monitor dashboard.";
            rule.Grouping = RuleGroup;
            rule.Direction = NetFwRuleDirection.In;
            rule.Action = NetFwAction.Block;
            rule.Protocol = NetFwIpProtocol.Any;        // TCP and UDP (HTTP/3) and everything else
            rule.Profiles = (int)NetFwProfileType2.All; // 0x7FFFFFFF
            rule.InterfaceTypes = "All";
            rule.EdgeTraversal = false;
            rule.RemoteAddresses = text;
            rule.Enabled = true;                        // defaults to false
            rules.Add(rule);
        }
        finally
        {
            Release(rule);
        }
    }

    private static void WriteAddresses(INetFwRules rules, string name, IReadOnlyCollection<string> addresses)
    {
        var text = JoinChecked(addresses);
        var rule = rules.Item(name);
        try
        {
            rule.RemoteAddresses = text; // committed immediately
        }
        finally
        {
            Release(rule);
        }
    }

    /// <summary>An empty or "*" RemoteAddresses on a block rule blocks every address. Never write one.</summary>
    private static string JoinChecked(IReadOnlyCollection<string> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(a => a.Length == 0 || a == "*"))
            throw new InvalidOperationException("Refusing to write an empty or wildcard address list to a block rule.");
        return string.Join(',', addresses);
    }

    private static INetFwRule? TryGetRule(INetFwRules rules, string name)
    {
        try
        {
            return rules.Item(name);
        }
        catch (Exception ex) when (ex.HResult == HResultNotFound)
        {
            return null;
        }
    }

    /// <summary>Removes the rule and any duplicates with the same name (rule names are not unique).</summary>
    private static void RemoveAllNamed(INetFwRules rules, string name)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var rule = TryGetRule(rules, name);
            if (rule is null)
                return;
            Release(rule);
            rules.Remove(name);
        }
    }

    private static INetFwPolicy2 CreatePolicy() => CreateCom<INetFwPolicy2>(NetFwClsid.Policy2);

    private static T CreateCom<T>(Guid clsid) where T : class
    {
        var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (T)Activator.CreateInstance(type)!;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException or InvalidCastException
            || ex.HResult is HResultAccessDenied or HResultNoEndpoints or HResultInvalidArg or HResultServiceDisabled or HResultRpcUnavailable;

    private static string Describe(Exception ex) => ex.HResult switch
    {
        HResultAccessDenied => "Access denied changing the firewall. The service must run as LocalSystem or an administrator.",
        HResultNoEndpoints or HResultRpcUnavailable => "The Windows Defender Firewall service (MpsSvc) is not running.",
        HResultServiceDisabled => "The Windows Defender Firewall service (MpsSvc) or the Base Filtering Engine is disabled.",
        HResultInvalidArg => "The firewall rejected a rule value (invalid address list?): " + ex.Message,
        _ => $"Windows Firewall call failed (0x{ex.HResult:X8}): {ex.Message}",
    };
}
