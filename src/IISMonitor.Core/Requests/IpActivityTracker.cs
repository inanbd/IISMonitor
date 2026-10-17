namespace IISMonitor.Core.Requests;

/// <summary>
/// Request counts per visitor IP over the last day, by kind and method, for the Top IPs table:
/// per-minute buckets for the last hour and per-hour buckets for the last day, stored only for
/// minutes and hours in which the visitor sent something. Bounded: past <c>maxVisitors</c>, the
/// visitors seen longest ago are dropped. Thread-safe.
/// </summary>
public sealed class IpActivityTracker(int maxVisitors = IpActivityTracker.DefaultMaxVisitors)
{
    public const int DefaultMaxVisitors = 50_000;

    private const int KindCount = 5;
    private const int MethodCount = 3;
    private const int ClientErrorIndex = KindCount * MethodCount;
    private const int ServerErrorIndex = ClientErrorIndex + 1;
    private const int RuleHitIndex = ServerErrorIndex + 1;
    private const int CounterCount = RuleHitIndex + 1;
    private const int MinutesKept = 60;
    private const int HoursKept = 24;

    private readonly object _gate = new();
    private readonly Dictionary<string, Visitor> _visitors = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
                return _visitors.Count;
        }
    }

    public void Record(string ip, bool viaProxy, MethodGroup method, RequestKind kind, int status, bool ruleHit,
        DateTime timeUtc, string path, string? userAgent)
    {
        var minute = timeUtc.Ticks / TimeSpan.TicksPerMinute;
        var hour = timeUtc.Ticks / TimeSpan.TicksPerHour;
        lock (_gate)
        {
            if (!_visitors.TryGetValue(ip, out var visitor))
            {
                if (_visitors.Count >= maxVisitors)
                    EvictOldest();
                _visitors[ip] = visitor = new Visitor(ip);
            }

            visitor.ViaProxy = viaProxy;
            if (timeUtc >= visitor.LastSeenUtc)
            {
                visitor.LastSeenUtc = timeUtc;
                visitor.LastPath = path;
                visitor.LastUserAgent = userAgent;
            }

            Tally(Slot(visitor.Minutes, minute, MinutesKept), method, kind, status, ruleHit);
            Tally(Slot(visitor.Hours, hour, HoursKept), method, kind, status, ruleHit);
        }
    }

    public TopIpReport Query(TopIpQuery query, DateTime nowUtc)
    {
        var filter = query.Filter ?? new RequestFilter();
        var ipMatch = RequestMatcher.IpMatcher(filter.Ip);
        var filtered = filter.Methods.Count > 0 || filter.Kinds.Count > 0;
        var limit = Math.Clamp(query.Limit, 1, 5000);
        var useHours = query.Window == TopIpWindow.OneDay;
        var from = query.Window switch
        {
            TopIpWindow.FiveMinutes => nowUtc.Ticks / TimeSpan.TicksPerMinute - 4,
            TopIpWindow.OneHour => nowUtc.Ticks / TimeSpan.TicksPerMinute - (MinutesKept - 1),
            _ => nowUtc.Ticks / TimeSpan.TicksPerHour - (HoursKept - 1),
        };

        var report = new TopIpReport();
        var rows = new List<TopIpRow>();
        Span<int> sums = stackalloc int[CounterCount];
        lock (_gate)
        {
            foreach (var visitor in _visitors.Values)
            {
                sums.Clear();
                var any = false;
                foreach (var slot in useHours ? visitor.Hours : visitor.Minutes)
                {
                    if (slot.Bucket < from)
                        continue;
                    any = true;
                    for (var i = 0; i < CounterCount; i++)
                        sums[i] += slot.Counts[i];
                }

                if (!any)
                    continue;
                report.Visitors++;
                if (ipMatch is not null && !ipMatch(visitor.Ip))
                    continue;

                var row = ToRow(visitor, sums, filter);
                if (filtered && row.Matching == 0)
                    continue;
                rows.Add(row);
            }
        }

        report.Rows = rows
            .OrderByDescending(r => r.Matching)
            .ThenByDescending(r => r.Total)
            .ThenBy(r => r.Ip, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return report;
    }

    /// <summary>Forgets visitors not seen for a day and buckets that have aged out.</summary>
    public void Prune(DateTime nowUtc)
    {
        var minute = nowUtc.Ticks / TimeSpan.TicksPerMinute;
        var hour = nowUtc.Ticks / TimeSpan.TicksPerHour;
        lock (_gate)
        {
            foreach (var (ip, visitor) in _visitors.ToList())
            {
                visitor.Minutes.RemoveAll(s => s.Bucket <= minute - MinutesKept);
                visitor.Hours.RemoveAll(s => s.Bucket <= hour - HoursKept);
                if (visitor.Minutes.Count == 0 && visitor.Hours.Count == 0)
                    _visitors.Remove(ip);
            }
        }
    }

    private static TopIpRow ToRow(Visitor visitor, ReadOnlySpan<int> sums, RequestFilter filter)
    {
        var row = new TopIpRow
        {
            Ip = visitor.Ip,
            ViaProxy = visitor.ViaProxy,
            ClientErrors = sums[ClientErrorIndex],
            ServerErrors = sums[ServerErrorIndex],
            RuleHits = sums[RuleHitIndex],
            LastSeenUtc = visitor.LastSeenUtc,
            LastPath = visitor.LastPath,
            LastUserAgent = visitor.LastUserAgent,
        };

        for (var kind = 0; kind < KindCount; kind++)
        {
            for (var method = 0; method < MethodCount; method++)
            {
                var n = sums[kind * MethodCount + method];
                if (n == 0)
                    continue;
                row.Total += n;
                if (filter.Matches((MethodGroup)method, (RequestKind)kind))
                    row.Matching += n;
                switch ((RequestKind)kind)
                {
                    case RequestKind.PageView: row.Pages += n; break;
                    case RequestKind.XhrApi: row.Api += n; break;
                    case RequestKind.Static: row.Static += n; break;
                    case RequestKind.Download: row.Downloads += n; break;
                    default: row.OtherKinds += n; break;
                }

                switch ((MethodGroup)method)
                {
                    case MethodGroup.Get: row.Get += n; break;
                    case MethodGroup.Post: row.Post += n; break;
                    default: row.OtherMethods += n; break;
                }
            }
        }

        return row;
    }

    private static int[] Slot(List<TimeSlot> slots, long bucket, int kept)
    {
        // Events arrive almost in time order; look from the end.
        for (var i = slots.Count - 1; i >= 0 && i >= slots.Count - 3; i--)
        {
            if (slots[i].Bucket == bucket)
                return slots[i].Counts;
        }

        var counts = new int[CounterCount];
        var at = slots.Count;
        while (at > 0 && slots[at - 1].Bucket > bucket)
            at--;
        slots.Insert(at, new TimeSlot(bucket, counts));
        while (slots.Count > 0 && slots[0].Bucket <= slots[^1].Bucket - kept)
            slots.RemoveAt(0);
        return counts;
    }

    private static void Tally(int[] counts, MethodGroup method, RequestKind kind, int status, bool ruleHit)
    {
        counts[(int)kind * MethodCount + (int)method]++;
        if (status is >= 400 and < 500)
            counts[ClientErrorIndex]++;
        else if (status >= 500)
            counts[ServerErrorIndex]++;
        if (ruleHit)
            counts[RuleHitIndex]++;
    }

    private void EvictOldest()
    {
        // Drop the tenth seen longest ago in one go, so a flood of new addresses costs one scan per many inserts.
        var drop = Math.Max(1, _visitors.Count / 10);
        foreach (var ip in _visitors.Values.OrderBy(v => v.LastSeenUtc).Take(drop).Select(v => v.Ip).ToList())
            _visitors.Remove(ip);
    }

    private sealed class Visitor(string ip)
    {
        public string Ip { get; } = ip;
        public bool ViaProxy { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? LastPath { get; set; }
        public string? LastUserAgent { get; set; }
        public List<TimeSlot> Minutes { get; } = [];
        public List<TimeSlot> Hours { get; } = [];
    }

    private readonly record struct TimeSlot(long Bucket, int[] Counts);
}
