namespace IISMonitor.Core.Requests;

/// <summary>One finished request, as kept in the live request log.</summary>
public sealed class RequestRecord
{
    /// <summary>Increases by one per request; lets the dashboard tell new requests from ones it has seen.</summary>
    public long Id { get; init; }

    /// <summary>When IIS reported the request (it reports a request when it finishes).</summary>
    public DateTime TimeUtc { get; init; }

    public long? SiteId { get; init; }

    public string Site { get; init; } = "";

    public string? AppPool { get; init; }

    /// <summary>The visitor; null when it came through a trusted proxy that didn't say who it was.</summary>
    public string? ClientIp { get; init; }

    /// <summary>The address that connected to IIS (c-ip); differs from <see cref="ClientIp"/> behind a proxy.</summary>
    public string? PeerIp { get; init; }

    public bool ViaProxy { get; init; }

    public string Method { get; init; } = "";

    public MethodGroup MethodGroup { get; init; }

    public RequestKind Kind { get; init; }

    /// <summary>URL path without the query string (cs-uri-stem).</summary>
    public string Path { get; init; } = "";

    public int Status { get; init; }

    public int SubStatus { get; init; }

    public double TimeTakenMs { get; init; }

    public long? BytesSent { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Name of the blacklist rule whose URL pattern the request matched, if any.</summary>
    public string? Rule { get; init; }

    /// <summary>IIS IP restrictions turned it away (401/403/404 with substatus 501-504, or 403.6).</summary>
    public bool DeniedByIis => IsIisDenial(Status, SubStatus);

    public static bool IsIisDenial(int status, int subStatus) =>
        (status is 401 or 403 or 404 && subStatus is >= 501 and <= 504) || (status == 403 && subStatus == 6);
}

/// <summary>Filters for the request log and the Top IPs table. Empty lists mean "all".</summary>
public sealed class RequestFilter
{
    public List<MethodGroup> Methods { get; set; } = [];

    public List<RequestKind> Kinds { get; set; } = [];

    /// <summary>An address ("203.0.113.5"), a network ("203.0.113.0/24") or the start of one ("203.0.").</summary>
    public string? Ip { get; set; }

    public long? SiteId { get; set; }

    /// <summary>Part of the URL path, case-insensitive.</summary>
    public string? PathContains { get; set; }

    public bool Matches(MethodGroup method, RequestKind kind) =>
        (Methods.Count == 0 || Methods.Contains(method)) && (Kinds.Count == 0 || Kinds.Contains(kind));
}

public sealed class RequestQuery
{
    public RequestFilter Filter { get; set; } = new();

    /// <summary>Newest first, at most this many (1–2000).</summary>
    public int Limit { get; set; } = 500;
}

public sealed class RequestPage
{
    public List<RequestRecord> Requests { get; set; } = [];

    /// <summary>Id of the newest request in the log, matching or not.</summary>
    public long LatestId { get; set; }

    /// <summary>How many requests in the log match the filter (the list may show fewer).</summary>
    public int Matched { get; set; }

    /// <summary>How many requests the log keeps; older ones drop out.</summary>
    public int Capacity { get; set; }

    public DateTime? OldestUtc { get; set; }
}

public enum TopIpWindow
{
    FiveMinutes,
    OneHour,
    OneDay,
}

public sealed class TopIpQuery
{
    public TopIpWindow Window { get; set; } = TopIpWindow.OneHour;

    /// <summary>Method and kind filters decide what <see cref="TopIpRow.Matching"/> counts; IP and path filters pick rows.</summary>
    public RequestFilter Filter { get; set; } = new();

    public int Limit { get; set; } = 200;
}

/// <summary>One visitor's requests in a time window, by kind and method.</summary>
public sealed class TopIpRow
{
    public string Ip { get; set; } = "";

    /// <summary>Seen through a trusted proxy (blocking it needs IIS rather than the firewall).</summary>
    public bool ViaProxy { get; set; }

    /// <summary>Requests that match the filter; the list is sorted by this.</summary>
    public int Matching { get; set; }

    public int Total { get; set; }
    public int Pages { get; set; }
    public int Api { get; set; }
    public int Static { get; set; }
    public int Downloads { get; set; }
    public int OtherKinds { get; set; }
    public int Get { get; set; }
    public int Post { get; set; }
    public int OtherMethods { get; set; }
    public int ClientErrors { get; set; }
    public int ServerErrors { get; set; }

    /// <summary>Requests to a blacklist rule's URLs.</summary>
    public int RuleHits { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public string? LastPath { get; set; }

    public string? LastUserAgent { get; set; }

    /// <summary>How the IP is blocked now ("Firewall (Active)", "Iis (Pending)", "Watch"), or null.</summary>
    public string? Blocked { get; set; }
}

public sealed class TopIpReport
{
    public List<TopIpRow> Rows { get; set; } = [];

    /// <summary>Distinct visitors in the window (the list may show fewer).</summary>
    public int Visitors { get; set; }
}

/// <summary>Request totals for one update, for the snapshot.</summary>
public sealed class RequestActivity
{
    public long LatestId { get; set; }

    /// <summary>Requests per second from IIS's log stream, by kind and method.</summary>
    public Dictionary<RequestKind, double> PerSecondByKind { get; set; } = [];

    public Dictionary<MethodGroup, double> PerSecondByMethod { get; set; } = [];

    /// <summary>Requests through a trusted proxy whose visitor couldn't be read from X-Forwarded-For, since the last update.</summary>
    public int Unattributed { get; set; }

    /// <summary>Events the ETW session dropped since it started (the log and the blacklist miss those).</summary>
    public long EventsLost { get; set; }

    public int Visitors24h { get; set; }
}

/// <summary>What "Set up IIS logging" should change.</summary>
public sealed class IisLoggingRequest
{
    /// <summary>Send log events to ETW and add the client IP, method, bytes sent, user agent and substatus fields (all sites).</summary>
    public bool RequestFields { get; set; } = true;

    /// <summary>
    /// Sites that should also log request and response headers as custom fields (X-Forwarded-For,
    /// Sec-Fetch-*, X-Requested-With, Content-Type, Content-Disposition). IIS turns off kernel-mode
    /// caching on a site that logs custom fields.
    /// </summary>
    public List<long> HeaderFieldSites { get; set; } = [];

    /// <summary>Sites whose header fields IIS Monitor added should be removed again.</summary>
    public List<long> RemoveHeaderFieldSites { get; set; } = [];
}
