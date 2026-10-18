namespace IISMonitor.Website.Site;

/// <summary>
/// One page of the site. The single place for its URL, titles and description: the layout,
/// navigation, previous/next links, structured data and sitemap.xml all read from here.
/// </summary>
/// <param name="Path">URL path, lowercase, no trailing slash (matches the page's @page route).</param>
/// <param name="NavTitle">Short name for menus and cards.</param>
/// <param name="Title">The &lt;title&gt; (" · IIS Monitor" is appended), ideally under 50 characters.</param>
/// <param name="Heading">The page's h1.</param>
/// <param name="Description">Meta description, about 140–160 characters.</param>
/// <param name="Section">Navigation group: <see cref="SiteMap.Features"/>, <see cref="SiteMap.Guides"/> or empty.</param>
/// <param name="Icon">Icon name from <see cref="Icons"/>.</param>
/// <param name="Summary">One or two sentences for cards on the home and features pages.</param>
/// <param name="Screenshot">Main screenshot (file name in wwwroot/images/screenshots, extension optional); also the page's social image.</param>
/// <param name="ScreenshotAlt">Alt text for the main screenshot.</param>
public sealed record SitePage(
    string Path,
    string NavTitle,
    string Title,
    string Heading,
    string Description,
    string Section = "",
    string Icon = "",
    string Summary = "",
    string? Screenshot = null,
    string? ScreenshotAlt = null);

public static class SiteMap
{
    public const string Features = "Features";
    public const string Guides = "Guides";

    public static readonly SitePage Home = new(
        "/", "Home",
        "IIS Monitor — Live IIS, App Pool & SQL Server Monitoring",
        "Real-time IIS monitoring, down to every app pool",
        "See every IIS site and app pool in real time: CPU, memory, disk, network, response times, every request and SQL Server load, with 7 days of history.",
        Screenshot: "overview",
        ScreenshotAlt: "IIS Monitor's Overview tab: CPU, RAM, requests per second and network charts with one line per application pool");

    public static readonly SitePage FeatureIndex = new(
        "/features", "Features",
        "Features",
        "Everything IIS Monitor shows you",
        "A tour of every IIS Monitor screen: live app pool charts, sites, every request and IP, the auto blacklist, SQL Server load, server health, history and settings.",
        Icon: "grid",
        Screenshot: "overview",
        ScreenshotAlt: "IIS Monitor dashboard with live charts for every application pool");

    public static readonly IReadOnlyList<SitePage> FeaturePages =
    [
        new("/features/overview", "Overview",
            "Overview: compare every app pool live",
            "Every app pool on four live charts",
            "Compare CPU, RAM, requests per second and network across all IIS application pools on four live charts, one line per pool, updated up to four times a second.",
            Features, "overview",
            "CPU, RAM, requests/sec and network for all app pools, one line each, so the busy one stands out at a glance.",
            "overview",
            "Overview tab with four live charts, CPU, RAM, requests per second and network, one line per application pool, and the hover read-out listing each pool's CPU"),
        new("/features/app-pools", "App pools",
            "App pools: CPU, memory, disk and processes",
            "Everything an app pool uses, down to each process",
            "Per IIS application pool: CPU, memory, disk and network I/O, SQL Server connections, requests, queue and response times, plus every w3wp.exe and child process.",
            Features, "layers",
            "One row per pool with CPU, memory, disk, network, database and request metrics, its processes and twelve live charts.",
            "app-pools",
            "App pools tab: a grid of application pools with CPU, memory, disk, network and database columns, the selected pool's processes and its live charts"),
        new("/features/sites", "Sites",
            "Sites: requests, bandwidth and response times",
            "Requests, bandwidth and response times for every site",
            "Per IIS website: connections, requests per second, bandwidth, average, p95 and maximum response time, and 4xx/5xx errors, measured live from IIS's own log stream.",
            Features, "globe",
            "Connections, requests/sec, bandwidth, response time (average, p95, max) and 4xx/5xx errors for each site.",
            "sites",
            "Sites tab: a grid of IIS websites with connections, requests per second, bandwidth, response times and errors, and live charts for the selected site"),
        new("/features/requests", "Requests",
            "Requests: every request and the IPs behind it",
            "Every request, from every IP, as it happens",
            "See each request IIS finishes with its client IP, method, kind, URL, status, time taken and user agent. Filter by kind, IP or URL and find the IPs sending the most.",
            Features, "list",
            "A live log of every request with client IP, kind and timing, quick filters, and the IPs sending the most traffic.",
            "requests",
            "Requests tab: requests per second by kind, method and kind filters, and the live request list with client IP, site, status, time taken and URL"),
        new("/features/auto-blacklist", "Auto blacklist",
            "Auto blacklist: block scanners and abusive IPs",
            "Block scanners and abusive IPs automatically",
            "Rules that block an IP after it requests matching URLs X times within N minutes, in Windows Firewall or IIS IP restrictions, with an allow-list and watch mode.",
            Features, "shield",
            "Rules that block IPs probing for /wp-login.php, /.env or your login page, in Windows Firewall or in IIS.",
            "blacklist-rules",
            "Blacklist rules editor with URL pattern rules, hit counts, time windows and block lengths, next to the allow-list and trusted proxies"),
        new("/features/database", "Database",
            "Database: SQL Server load per app pool",
            "Which app pool keeps SQL Server busy, and with what",
            "Database load, blocking and slow queries per IIS application pool, matched by client process. Nothing to install on SQL Server: it only needs VIEW SERVER STATE.",
            Features, "database",
            "SQL Server load, blocking, the queries running now and slow queries, each traced to the app pool that sent it.",
            "database",
            "Database tab: database load per application pool, load and blocking charts, and the queries running now with what each one is waiting for"),
        new("/features/server", "Server",
            "Server: CPU, memory and request mix",
            "The whole server at a glance",
            "Whole-server CPU and memory next to the local SQL Server process, requests per second by kind, and summary tiles that stay visible on every tab.",
            Features, "server",
            "Server CPU and memory next to SQL Server's own share, and the request mix by kind.",
            "server",
            "Server tab: whole-server CPU and memory charts with the local SQL Server process, and requests per second by kind"),
        new("/features/history", "History",
            "History: 7 days of IIS metrics, no gaps",
            "Seven days of history, with no gaps",
            "IIS Monitor's Windows service records every app pool, site and the server every 10 seconds and keeps 7 days, so you can see what happened overnight or last week.",
            Features, "clock",
            "Every pool, site and the server recorded every 10 seconds and kept for 7 days, even when nobody is watching.",
            "history",
            "History tab showing seven days of CPU, memory, disk, network and database charts for one application pool"),
        new("/features/collectors", "Collectors",
            "Collectors: where every number comes from",
            "Know where every number comes from",
            "The Collectors tab lists each data source IIS Monitor reads, from IIS configuration to ETW traces and SQL Server, and whether it works.",
            Features, "pulse",
            "Each data source and whether it is working, so an empty column or chart always comes with a reason.",
            "collectors",
            "Collectors tab: each data source with a check mark or a warning and a plain-language status message"),
        new("/features/settings", "Settings",
            "Settings: sampling, history, SQL Server",
            "Tune sampling, history, SQL Server and tracing",
            "Set IIS Monitor's update interval, history resolution and retention, SQL Server ports and connection strings, the slow-query threshold and the ETW tracing switches.",
            Features, "sliders",
            "Update interval, history resolution and retention, SQL Server connections and the tracing switches.",
            "settings",
            "IIS Monitor settings window with sampling and history, SQL Server connections and tracing sections"),
    ];

    public static readonly IReadOnlyList<SitePage> GuidePages =
    [
        new("/docs/getting-started", "Getting started",
            "Install IIS Monitor on Windows Server",
            "Install IIS Monitor",
            "Requirements, building from source, installing the IIS Monitor Windows service and dashboard, first-run setup, updating and uninstalling.",
            Guides, "download",
            "Requirements, install, first-run setup, updates and removal."),
        new("/docs/how-it-works", "How it works",
            "How IIS Monitor works: architecture and data",
            "How IIS Monitor works",
            "The collector service, the dashboard and where each metric comes from: Win32 process APIs, IIS performance counters, kernel and IIS ETW traces and SQL Server DMVs.",
            Guides, "flow",
            "The service, the dashboard, and the source behind every metric."),
        new("/docs/security", "Data & security",
            "Data and security: what IIS Monitor stores",
            "Your data stays on the server",
            "Where IIS Monitor keeps settings and history, who can read them, what it never stores (query strings, SQL literal values) and why nothing listens on the network.",
            Guides, "lock",
            "Where data is kept, who can read it and what is never stored."),
        new("/docs/troubleshooting", "Troubleshooting",
            "Troubleshooting IIS Monitor",
            "Troubleshooting",
            "Fixes for missing response times, low network numbers, zero database connections, standalone mode, empty counters, missing client IPs and failed IP blocks.",
            Guides, "wrench",
            "What to check when a number is missing or a block fails."),
        new("/faq", "FAQ",
            "Frequently asked questions",
            "Frequently asked questions",
            "Answers about IIS Monitor's requirements, what it changes in IIS, SQL Server permissions, disk usage, privacy, IP blocking and running it without the service.",
            Guides, "help",
            "Short answers to the questions people ask first."),
    ];

    public static readonly SitePage Contact = new(
        "/contact", "Contact",
        "Contact",
        "Get in touch",
        "Questions about IIS Monitor, a feature request or a problem on your server? Send me a message and I'll reply by email.",
        Icon: "mail");

    public static readonly IReadOnlyList<SitePage> All = [Home, FeatureIndex, .. FeaturePages, .. GuidePages, Contact];

    private static readonly Dictionary<string, SitePage> ByPath = All.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);

    public static SitePage? Find(string? path) =>
        ByPath.GetValueOrDefault(string.IsNullOrEmpty(path) || path == "/" ? "/" : path.TrimEnd('/'));

    public static SitePage Get(string path) =>
        Find(path) ?? throw new InvalidOperationException($"No page is registered for {path} in {nameof(SiteMap)}.");

    /// <summary>The pages in the same navigation group, in menu order.</summary>
    public static IReadOnlyList<SitePage> SectionOf(SitePage page) => page.Section switch
    {
        Features => FeaturePages,
        Guides => GuidePages,
        _ => [],
    };

    public static (SitePage? Previous, SitePage? Next) Neighbours(SitePage page)
    {
        var pages = SectionOf(page);
        var index = pages.ToList().IndexOf(page);
        if (index < 0)
            return (null, null);
        return (index > 0 ? pages[index - 1] : null, index < pages.Count - 1 ? pages[index + 1] : null);
    }
}
