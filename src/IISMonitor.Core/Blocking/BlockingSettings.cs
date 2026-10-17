using IISMonitor.Core.Requests;

namespace IISMonitor.Core.Blocking;

public enum BlockingMode
{
    /// <summary>Rules are not checked.</summary>
    Off,

    /// <summary>Rules are checked and IPs that trip one are listed, but nothing is blocked. For trying rules out.</summary>
    Watch,

    /// <summary>IPs that trip a rule are blocked.</summary>
    Block,
}

/// <summary>"If an IP requests a URL matching <see cref="Pattern"/> <see cref="Hits"/> times within <see cref="WithinMinutes"/>, block it."</summary>
public sealed class BlockRule
{
    /// <summary>Stable identity, so hit counts survive edits to other rules.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// URL path patterns, separated by ";", case-insensitive: "*" matches anything (including "/"),
    /// "?" one character. A request to any of them counts as a hit.
    /// </summary>
    public string Pattern { get; set; } = "";

    public int Hits { get; set; } = 3;

    public double WithinMinutes { get; set; } = 10;

    /// <summary>How long the block lasts; 0 means until it is lifted by hand.</summary>
    public double BlockHours { get; set; } = 24;

    /// <summary>Site names the rule applies to; empty for all sites.</summary>
    public List<string> Sites { get; set; } = [];

    public BlockRule Normalize()
    {
        var pattern = UrlPattern.Join(UrlPattern.Split(Pattern));
        return new BlockRule
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? pattern : Name.Trim(),
            // A pattern that would catch ordinary visitors is kept for editing but never enforced.
            Enabled = Enabled && !UrlPattern.IsTooBroad(pattern),
            Pattern = pattern.Length > 4000 ? pattern[..4000] : pattern,
            Hits = Math.Clamp(Hits, 1, 100_000),
            WithinMinutes = double.IsFinite(WithinMinutes) ? Math.Clamp(WithinMinutes, 0.1, 7 * 24 * 60) : 10,
            BlockHours = double.IsFinite(BlockHours) ? Math.Clamp(BlockHours, 0, 24 * 365) : 24,
            Sites = (Sites ?? []).Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }
}

/// <summary>Auto blacklist settings. A separate object so a dashboard that doesn't know them can't wipe them.</summary>
public sealed class BlockingSettings
{
    public const int MaxRules = 200;

    /// <summary>
    /// A starting point for sites that don't use PHP, WordPress or these tools: paths that only
    /// vulnerability scanners ask for. Review before saving.
    /// </summary>
    public static BlockRule ScannerRule() => new()
    {
        Name = "Common scanner paths",
        Pattern = UrlPattern.Join([
            "/wp-login.php", "/xmlrpc.php", "/wp-admin/*", "/wp-includes/*", "/wp-content/plugins/*", "/.env", "*/.env",
            "/.git/*", "/.aws/*", "/phpmyadmin/*", "/pma/*", "/cgi-bin/*", "/vendor/phpunit/*", "/actuator/*", "/boaform/*",
            "/HNAP1*",
        ]),
        Hits = 3,
        WithinMinutes = 10,
        BlockHours = 24,
    };

    public BlockingMode Mode { get; set; } = BlockingMode.Off;

    public List<BlockRule> Rules { get; set; } = [];

    /// <summary>Addresses and networks never blocked (CIDR allowed): your office, monitoring, partners.</summary>
    public List<string> AllowList { get; set; } = [];

    /// <summary>
    /// Reverse proxies, load balancers and CDNs in front of IIS (CIDR allowed). For requests from
    /// them, the visitor is read from X-Forwarded-For and blocked in IIS instead of the firewall.
    /// They are never blocked themselves.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// IIS IP restriction changes are written at most this often. Each change rewrites
    /// applicationHost.config, which restarts ASP.NET Core apps on the server.
    /// </summary>
    public int IisApplyIntervalSeconds { get; set; } = 60;

    /// <summary>After a firewall block, close TCP connections the IP still has open (IPv4).</summary>
    public bool DropOpenConnections { get; set; } = true;

    public BlockingSettings Normalize()
    {
        return new BlockingSettings
        {
            Mode = Enum.IsDefined(Mode) ? Mode : BlockingMode.Off,
            Rules = (Rules ?? [])
                .Where(r => r is not null)
                .Select(r => r.Normalize())
                .Where(r => r.Pattern.Length > 0)
                .DistinctBy(r => r.Id)
                .Take(MaxRules)
                .ToList(),
            AllowList = CleanNetworks(AllowList),
            TrustedProxies = CleanNetworks(TrustedProxies),
            IisApplyIntervalSeconds = Math.Clamp(IisApplyIntervalSeconds, 10, 3600),
            DropOpenConnections = DropOpenConnections,
        };
    }

    /// <summary>Entries that aren't an address or a CIDR network, for the editor to point out.</summary>
    public static IReadOnlyList<string> InvalidNetworks(IEnumerable<string>? entries) =>
        (entries ?? []).Select(e => e?.Trim() ?? "").Where(e => e.Length > 0 && !IpNetwork.TryParse(e, out _)).ToList();

    private static List<string> CleanNetworks(IEnumerable<string>? entries) =>
        (entries ?? [])
            .Select(e => IpNetwork.TryParse(e, out var network) ? network.ToString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
