using IISMonitor.Core.Blocking;
using IISMonitor.Core.Collection;

namespace IISMonitor.Core.Requests;

/// <summary>
/// Everything that happens to a request from IIS's log stream: who sent it (behind trusted
/// proxies too), what kind it is, response-time statistics, the live request log, per-IP counts
/// and the blacklist rules. Called on the IIS log thread, so it never blocks on I/O: block
/// decisions go to a callback that queues them. Outlives the ETW session, which is restarted on
/// failures.
/// </summary>
public sealed class RequestPipeline
{
    private const int MaxPathLength = 1024;
    private const int MaxUserAgentLength = 512;

    private readonly ResponseTimeAggregator _responses;
    private readonly Action<BlockDecision> _onDecision;
    private readonly object _countsGate = new();
    private readonly long[] _byKind = new long[5];
    private readonly long[] _byMethod = new long[3];
    private readonly StringPool _userAgents = new(4096);
    private volatile SiteMap _sites = new([]);
    private volatile IpSet _trustedProxies = IpSet.Empty;
    private long _unattributed;

    public RequestPipeline(ResponseTimeAggregator responses, Action<BlockDecision> onDecision,
        RequestLog? log = null, IpActivityTracker? tracker = null, BlacklistEngine? blacklist = null)
    {
        _responses = responses;
        _onDecision = onDecision;
        Log = log ?? new RequestLog();
        Tracker = tracker ?? new IpActivityTracker();
        Blacklist = blacklist ?? new BlacklistEngine();
    }

    public RequestLog Log { get; }

    public IpActivityTracker Tracker { get; }

    public BlacklistEngine Blacklist { get; }

    public void UpdateSites(IEnumerable<SiteInfo> sites) => _sites = new SiteMap(sites.ToList());

    public void SetTrustedProxies(IpSet trustedProxies) => _trustedProxies = trustedProxies;

    public void Process(IisLogEvent e)
    {
        var sites = _sites;
        var siteId = sites.Matcher.ResolveSiteId(e);
        var site = siteId is { } id ? sites.Get(id) : null;
        var appPool = siteId is { } matched ? sites.Matcher.ResolveAppPool(matched, e.UriStem) : null;
        var time = e.TimeUtc == default ? DateTime.UtcNow : e.TimeUtc;

        // WebSockets and other upgrades last as long as the connection: not a response time.
        if (siteId is { } responseSite && e.StatusCode != 101)
            _responses.Record(responseSite, appPool, e.TimeTakenMs, e.StatusCode);

        var headers = CustomFieldParser.Parse(e.CustomFields, site?.Layout ?? CustomFieldLayout.None);
        var who = ClientIpResolver.Resolve(e.ClientIp, headers, _trustedProxies);
        var methodGroup = RequestClassifier.GroupMethod(e.Method);
        var kind = RequestClassifier.Classify(new RequestSignals(e.Method, e.UriStem, e.StatusCode)
        {
            SecFetchDest = headers.FetchDest,
            SecFetchMode = headers.FetchMode,
            XRequestedWith = headers.RequestedWith,
            ContentType = headers.ContentType,
            ContentDisposition = headers.ContentDisposition,
        });

        var path = e.UriStem.Length > MaxPathLength ? e.UriStem[..MaxPathLength] : e.UriStem;
        var siteName = site?.Name ?? e.SiteName ?? (e.SiteId is { } rawId ? $"W3SVC{rawId}" : "");
        var rule = Blacklist.MatchRule(path, site?.Name);
        var client = who.Client?.ToString();
        var userAgent = UserAgent(e.UserAgent);

        Log.Add(nextId => new RequestRecord
        {
            Id = nextId,
            TimeUtc = time,
            SiteId = siteId ?? e.SiteId,
            Site = siteName,
            AppPool = appPool,
            ClientIp = client,
            PeerIp = who.ViaProxy ? who.Peer?.ToString() : null,
            ViaProxy = who.ViaProxy,
            Method = e.Method ?? "",
            MethodGroup = methodGroup,
            Kind = kind,
            Path = path,
            Status = e.StatusCode,
            SubStatus = e.SubStatus,
            TimeTakenMs = e.TimeTakenMs,
            BytesSent = e.BytesSent,
            UserAgent = userAgent,
            Rule = rule?.Name,
        });

        lock (_countsGate)
        {
            _byKind[(int)kind]++;
            _byMethod[(int)methodGroup]++;
            if (who.ViaProxy && who.Client is null)
                _unattributed++;
        }

        if (client is not null)
        {
            Tracker.Record(client, who.ViaProxy, methodGroup, kind, e.StatusCode, rule is not null, time, path, userAgent);
            if (rule is not null && Blacklist.Observe(who.Client, who.ViaProxy, path, site?.Name, e.StatusCode, e.SubStatus, time) is { } decision)
                _onDecision(decision);
        }
    }

    /// <summary>Requests per second by kind and method since the previous call.</summary>
    public RequestActivity DrainActivity(double intervalSeconds)
    {
        var activity = new RequestActivity { LatestId = Log.LatestId, Visitors24h = Tracker.Count };
        if (intervalSeconds <= 0)
            return activity;

        lock (_countsGate)
        {
            for (var i = 0; i < _byKind.Length; i++)
            {
                activity.PerSecondByKind[(RequestKind)i] = _byKind[i] / intervalSeconds;
                _byKind[i] = 0;
            }

            for (var i = 0; i < _byMethod.Length; i++)
            {
                activity.PerSecondByMethod[(MethodGroup)i] = _byMethod[i] / intervalSeconds;
                _byMethod[i] = 0;
            }

            activity.Unattributed = (int)Math.Min(int.MaxValue, _unattributed);
            _unattributed = 0;
        }

        return activity;
    }

    /// <summary>The visitor's address as last seen: whether it came through a trusted proxy.</summary>
    public bool? WasSeenViaProxy(string ip)
    {
        var row = Tracker.Query(new TopIpQuery { Window = TopIpWindow.OneDay, Filter = new RequestFilter { Ip = ip }, Limit = 1 }, DateTime.UtcNow)
            .Rows.FirstOrDefault(r => r.Ip == ip);
        return row?.ViaProxy;
    }

    private string? UserAgent(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        // IIS writes "+" for spaces in the user agent.
        var text = raw.Length > MaxUserAgentLength ? raw[..MaxUserAgentLength] : raw;
        return _userAgents.Get(text.Replace('+', ' '));
    }

    private sealed class SiteMap
    {
        private readonly Dictionary<long, SiteInfo> _byId;

        public SiteMap(List<SiteInfo> sites)
        {
            Matcher = new ApplicationMatcher(sites);
            _byId = sites.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        }

        public ApplicationMatcher Matcher { get; }

        public SiteInfo? Get(long id) => _byId.GetValueOrDefault(id);
    }

    /// <summary>Shares identical strings (user agents repeat a lot), up to a size; then starts over.</summary>
    private sealed class StringPool(int limit)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        public string Get(string value)
        {
            lock (_gate)
            {
                if (_strings.TryGetValue(value, out var shared))
                    return shared;
                if (_strings.Count >= limit)
                    _strings.Clear();
                _strings[value] = value;
                return value;
            }
        }
    }
}
