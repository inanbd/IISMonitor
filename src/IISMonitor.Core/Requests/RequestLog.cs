using System.Net;

namespace IISMonitor.Core.Requests;

/// <summary>
/// The most recent requests, newest first on query. A fixed-size ring: once full, each new request
/// replaces the oldest. Thread-safe: the IIS log thread adds, pipe requests query.
/// </summary>
public sealed class RequestLog(int capacity = RequestLog.DefaultCapacity)
{
    public const int DefaultCapacity = 20_000;
    public const int MaxPage = 2_000;

    private readonly object _gate = new();
    private readonly RequestRecord?[] _ring = new RequestRecord?[Math.Max(1, capacity)];
    private long _nextId = 1;
    private int _next;
    private int _count;

    public int Capacity => _ring.Length;

    public long LatestId
    {
        get
        {
            lock (_gate)
                return _nextId - 1;
        }
    }

    /// <summary>Stores a request, giving it the next id. Returns the stored record.</summary>
    public RequestRecord Add(Func<long, RequestRecord> create)
    {
        lock (_gate)
        {
            var record = create(_nextId++);
            _ring[_next] = record;
            _next = (_next + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
            return record;
        }
    }

    public RequestPage Query(RequestQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxPage);
        var match = RequestMatcher.Create(query.Filter);
        var page = new RequestPage { Capacity = _ring.Length };
        lock (_gate)
        {
            page.LatestId = _nextId - 1;
            for (var i = 0; i < _count; i++)
            {
                var index = (_next - 1 - i + _ring.Length * 2) % _ring.Length;
                var record = _ring[index]!;
                page.OldestUtc = record.TimeUtc;
                if (!match(record))
                    continue;
                page.Matched++;
                if (page.Requests.Count < limit)
                    page.Requests.Add(record);
            }
        }

        return page;
    }
}

/// <summary>Builds a predicate from a <see cref="RequestFilter"/>.</summary>
public static class RequestMatcher
{
    public static Func<RequestRecord, bool> Create(RequestFilter? filter)
    {
        filter ??= new RequestFilter();
        var methods = filter.Methods.ToHashSet();
        var kinds = filter.Kinds.ToHashSet();
        var ip = IpMatcher(filter.Ip);
        var path = string.IsNullOrWhiteSpace(filter.PathContains) ? null : filter.PathContains.Trim();
        var site = filter.SiteId;
        return r => (methods.Count == 0 || methods.Contains(r.MethodGroup))
                    && (kinds.Count == 0 || kinds.Contains(r.Kind))
                    && (site is null || r.SiteId == site)
                    && (ip is null || ip(r.ClientIp) || ip(r.PeerIp))
                    && (path is null || r.Path.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Null for "any". An address or network ("203.0.113.0/24") matches by containment; anything
    /// else matches addresses that start with it ("203.0.").
    /// </summary>
    public static Func<string?, bool>? IpMatcher(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (IpNetwork.TryParse(text, out var network))
            return ip => ip is not null && IPAddress.TryParse(ip, out var address) && network.Contains(address);
        return ip => ip is not null && ip.StartsWith(text, StringComparison.OrdinalIgnoreCase);
    }
}
