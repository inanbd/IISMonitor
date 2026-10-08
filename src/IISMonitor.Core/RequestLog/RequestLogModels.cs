namespace IISMonitor.Core.RequestLog;

/// <summary>Requests from one client IP for one URL with one status, within one minute, for one app pool.</summary>
public sealed class RequestLogEntry
{
    public string AppPool { get; init; } = "";

    /// <summary>Start of the UTC minute, Unix milliseconds.</summary>
    public long MinuteUnixMs { get; init; }

    public string ClientIp { get; init; } = "";
    public string Url { get; init; } = "";
    public string Method { get; init; } = "";
    public int Status { get; init; }
    public int SubStatus { get; init; }
    public long Hits { get; set; }
    public long FirstUnixMs { get; set; }
    public long LastUnixMs { get; set; }
    public double TotalTimeMs { get; set; }
}

public enum RequestLogView
{
    /// <summary>One row per client IP.</summary>
    Clients,

    /// <summary>One row per URL.</summary>
    Urls,

    /// <summary>What one client IP requested (Key = the IP).</summary>
    ClientDetail,

    /// <summary>Who requested one URL (Key = the URL).</summary>
    UrlDetail,
}

public sealed class RequestLogQuery
{
    public const int MaxLimit = 5000;

    public string AppPool { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public RequestLogView View { get; set; }

    /// <summary>ClientDetail: the client IP. UrlDetail: the URL.</summary>
    public string? Key { get; set; }

    /// <summary>Clients and Urls: only IPs or URLs containing this text (ignoring case).</summary>
    public string? Filter { get; set; }

    /// <summary>Details: one row per minute (true) or one row per URL/IP, method and status over the whole range (false).</summary>
    public bool PerMinute { get; set; } = true;

    /// <summary>Most rows returned (clamped to 1..MaxLimit); the busiest come first.</summary>
    public int Limit { get; set; } = 1000;
}

/// <summary>A client IP (Clients view) or a URL (Urls view) over the queried range.</summary>
public sealed class RequestLogSummaryRow
{
    public string Key { get; set; } = "";
    public long Hits { get; set; }

    /// <summary>Distinct URLs (Clients view) or distinct client IPs (Urls view).</summary>
    public long Distinct { get; set; }

    public long Status2xx { get; set; }
    public long Status3xx { get; set; }
    public long Status4xx { get; set; }
    public long Status5xx { get; set; }
    public double AverageTimeMs { get; set; }
    public long FirstUnixMs { get; set; }
    public long LastUnixMs { get; set; }
}

public sealed class RequestLogDetailRow
{
    /// <summary>Start of the minute (PerMinute), else 0.</summary>
    public long MinuteUnixMs { get; set; }

    public string ClientIp { get; set; } = "";
    public string Url { get; set; } = "";
    public string Method { get; set; } = "";
    public int Status { get; set; }
    public int SubStatus { get; set; }
    public long Hits { get; set; }
    public double AverageTimeMs { get; set; }
    public long FirstUnixMs { get; set; }
    public long LastUnixMs { get; set; }
}

public sealed class RequestLogReport
{
    public RequestLogView View { get; set; }

    /// <summary>Clients and Urls views.</summary>
    public List<RequestLogSummaryRow> Summaries { get; set; } = [];

    /// <summary>ClientDetail and UrlDetail views.</summary>
    public List<RequestLogDetailRow> Details { get; set; } = [];

    /// <summary>Rows that matched (before Limit).</summary>
    public long TotalRows { get; set; }

    /// <summary>Requests in all matching rows (before Limit).</summary>
    public long TotalHits { get; set; }

    /// <summary>More rows matched than were returned.</summary>
    public bool Truncated { get; set; }

    /// <summary>Things the user should know (filled by the engine): missing IIS log fields, dropped requests, tracking off…</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Some site of this app pool doesn't log the client IP to ETW yet; the dashboard offers to turn it on.</summary>
    public bool LoggingFieldsMissing { get; set; }

    /// <summary>Size of requests.db including its WAL file.</summary>
    public long DatabaseBytes { get; set; }
}

/// <summary>An IP address IIS refuses (an ipSecurity entry with allowed="false").</summary>
public sealed class BlockedIp
{
    public string IpAddress { get; set; } = "";

    /// <summary>Subnet mask or prefix when the entry covers a range; null for one address.</summary>
    public string? SubnetMask { get; set; }

    /// <summary>IIS configuration location: "" for the whole server, else "Site" or "Site/path".</summary>
    public string Location { get; set; } = "";
}

public sealed class IpBlockRequest
{
    public string IpAddress { get; set; } = "";

    /// <summary>Block: the app pool whose applications refuse the address; null or empty for every site on the server.</summary>
    public string? AppPool { get; set; }

    /// <summary>Unblock: the location the entry is removed from ("" for the whole server).</summary>
    public string? Location { get; set; }
}
