namespace IISMonitor.Website.Site;

/// <param name="AnswerHtml">Trusted, hand-written HTML: shown on the page and in the FAQPage structured data.</param>
public sealed record FaqItem(string Id, string Question, string AnswerHtml);

public static class Faq
{
    public static readonly IReadOnlyList<FaqItem> Items =
    [
        new("free", "Is IIS Monitor free?",
            "<p>Yes. IIS Monitor is free and open source under the <a href=\"https://github.com/inanbd/IISMonitor/blob/HEAD/LICENSE\">MIT License</a>: you can use it on as many servers as you like, including at work, and change or share the code. The code is <a href=\"https://github.com/inanbd/IISMonitor\">on GitHub</a>.</p>"),
        new("requirements", "Which Windows and IIS versions does IIS Monitor support?",
            "<p>Windows Server 2022 and Windows Server 2025 with IIS 10, on x64. The published build is self-contained, so the server doesn't need a .NET runtime installed.</p>"),
        new("what-it-measures", "What does IIS Monitor measure?",
            "<p>Per application pool: CPU, private memory, working set, threads, handles, processes, disk and network bytes per second, SQL Server connections and sessions, database load, requests per second, active requests, queue length, response times and errors. Per site: connections, requests per second, bandwidth, response time (average, p95 and max) and 4xx/5xx errors. For the server: CPU, memory, the local SQL Server process and requests by kind. It also lists every request with its client IP.</p>"),
        new("service", "Do I have to install the Windows service?",
            "<p>No, but it is recommended. Without it, the dashboard runs the same collector itself in <em>standalone mode</em>: the live view works, but history is only recorded while the window is open and the auto blacklist doesn't block anything. With the service installed, data is collected around the clock and history has no gaps.</p>"),
        new("remote", "Can I watch a server from my own PC?",
            "<p>The dashboard talks to the service over a local named pipe that only Administrators and SYSTEM can open, and nothing listens on the network. Run the dashboard on the IIS server itself, for example over Remote Desktop.</p>"),
        new("iis-changes", "Does IIS Monitor change my IIS configuration?",
            "<p>Only when you ask it to, and it asks before each change. <strong>Enable response times</strong> sends IIS's W3C log to ETW as well as to files, and turns on the four log fields it needs if they are off. <strong>Set up IIS logging</strong> adds the client IP, method, bytes sent, user agent and substatus fields, and optional header fields per site. Blocking a visitor that comes through a trusted proxy adds an IIS IP restriction. Your log files keep being written as before.</p>"),
        new("sql-server", "Does it need anything installed on SQL Server?",
            "<p>No. Database connections are counted from the Windows TCP table on the IIS server. For the Database tab, add a connection string whose login has <code>VIEW SERVER STATE</code> (or <code>VIEW SERVER PERFORMANCE STATE</code> on SQL Server 2022 and later). IIS Monitor then reads SQL Server's dynamic management views; it doesn't create objects or start traces.</p>"),
        new("per-site-cpu", "Why can't I see CPU and memory per site?",
            "<p>IIS runs each application pool in its own processes, so CPU, memory, disk and database connections belong to a pool. When several sites share a pool, those numbers can't be split between them. Requests, bandwidth and response times are still measured per site. One pool per site gives the clearest picture.</p>"),
        new("disk-usage", "How much disk space does the history use?",
            "<p>About 60,000 rows per app pool or site per week at the default 10-second resolution. Ten pools and ten sites with every metric populated take about 260 MB for 7 days. A 30-second resolution cuts that to a third. Old rows are deleted every hour.</p>"),
        new("privacy", "Does IIS Monitor store customer data?",
            "<p>As little as possible. URL query strings are not logged. SQL statements are stored with literal values replaced by <code>?</code>, so <code>WHERE Email = 'x@y.com'</code> becomes <code>WHERE Email = ?</code>. The request log and per-IP counts live in the service's memory only. Settings and history stay on the server, in a folder only Administrators and SYSTEM can read.</p>"),
        new("blocking-safety", "Could the auto blacklist block my real visitors?",
            "<p>It is built to avoid that. Loopback, the server's own addresses, your allow-list and your trusted proxies are never blocked, patterns that would match ordinary pages (such as <code>/*</code> or the home page) are refused, and private addresses found in <code>X-Forwarded-For</code> are never blocked in IIS. Start new rules in <em>Watch only</em> mode to see which IPs they would block before switching to <em>Block</em>.</p>"),
        new("overhead", "How much load does IIS Monitor add?",
            "<p>It reads data Windows and IIS already produce: process counters, IIS performance counters and ETW events, the same sources Task Manager and Resource Monitor use. The SQL Server sample is one light query per second. You choose the update interval (0.25 seconds to 1 minute) and can switch either ETW trace off in Settings.</p>"),
        new("load-balancer", "Does it work behind a load balancer, reverse proxy or CDN?",
            "<p>Yes. Add the proxies' addresses to <strong>Trusted proxies</strong> and log the header fields on those sites. IIS Monitor then reads the visitor from <code>X-Forwarded-For</code>, taking the right-most address that isn't a trusted proxy so a client can't pretend to be someone else, and blocks those visitors with IIS IP restrictions instead of the firewall.</p>"),
    ];
}
