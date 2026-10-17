using System.Net;
using IISMonitor.Core.Requests;

namespace IISMonitor.Core.Blocking;

/// <summary>
/// Counts, per rule and visitor IP, requests to the rule's URLs within its time window, and decides
/// to block an IP when it reaches the rule's hit count. Pure logic: enforcement is someone else's
/// job (decisions go to a callback). Thread-safe; the IIS log thread calls <see cref="Observe"/>.
/// </summary>
public sealed class BlacklistEngine
{
    /// <summary>Upper bound on (rule, IP) counters, so a flood of addresses can't use unbounded memory.</summary>
    public const int MaxCounters = 200_000;

    private readonly object _gate = new();
    private readonly Dictionary<(string RuleId, string Ip), Queue<long>> _hits = [];
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private CompiledRule[] _rules = [];
    private IpSet _protected = IpSet.Empty;
    private BlockingMode _mode;
    private long _lastPruneTicks;

    public BlockingMode Mode
    {
        get
        {
            lock (_gate)
                return _mode;
        }
    }

    /// <param name="serverAddresses">This machine's own addresses: never blocked.</param>
    public void Configure(BlockingSettings settings, IEnumerable<IPAddress> serverAddresses)
    {
        var rules = settings.Rules
            .Where(r => r.Enabled && !UrlPattern.IsTooBroad(r.Pattern))
            .Select(r => new CompiledRule(r, new UrlPattern(r.Pattern),
                r.Sites.Count == 0 ? null : r.Sites.ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var protectedSet = IpSet.Parse(settings.AllowList.Concat(settings.TrustedProxies), serverAddresses);

        lock (_gate)
        {
            _rules = rules;
            _protected = protectedSet;
            _mode = settings.Mode;
            var ids = rules.Select(r => r.Rule.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _hits.Keys.Where(k => !ids.Contains(k.RuleId)).ToList())
                _hits.Remove(key);
        }
    }

    /// <summary>Loopback, link-local, this server, the allow-list and the trusted proxies.</summary>
    public bool IsProtected(IPAddress address)
    {
        if (IpText.IsAlwaysProtected(address))
            return true;
        lock (_gate)
            return _protected.Contains(address);
    }

    /// <summary>IPs with a current block or watch entry: their requests no longer count.</summary>
    public void SetBlocked(IEnumerable<string> ips)
    {
        lock (_gate)
        {
            _blocked.Clear();
            _blocked.UnionWith(ips);
        }
    }

    public void MarkBlocked(string ip)
    {
        lock (_gate)
            _blocked.Add(ip);
    }

    public void MarkUnblocked(string ip)
    {
        lock (_gate)
            _blocked.Remove(ip);
    }

    public bool IsBlocked(string ip)
    {
        lock (_gate)
            return _blocked.Contains(ip);
    }

    /// <summary>
    /// The first enabled rule whose URL pattern (and sites) match the request, or null; nothing
    /// matches while the blacklist is off.
    /// </summary>
    public BlockRule? MatchRule(string path, string? site)
    {
        lock (_gate)
            return _mode == BlockingMode.Off ? null : FindRule(path, site)?.Rule;
    }

    /// <summary>
    /// Counts the request against every rule it matches; returns a decision when the IP has
    /// reached a rule's hit count, and from then on ignores the IP until it is unblocked.
    /// </summary>
    /// <param name="client">The visitor; requests without one (unattributed proxy traffic) never count.</param>
    public BlockDecision? Observe(IPAddress? client, bool viaProxy, string path, string? site, int status, int subStatus, DateTime timeUtc)
    {
        if (client is null || RequestRecord.IsIisDenial(status, subStatus))
            return null;

        var ip = client.ToString();
        lock (_gate)
        {
            if (_mode == BlockingMode.Off || _rules.Length == 0 || _blocked.Contains(ip))
                return null;
            if (IpText.IsAlwaysProtected(client) || _protected.Contains(client))
                return null;

            // Behind a proxy, a private address is an internal hop, and blocking it in IIS would turn
            // away everyone whose X-Forwarded-For passes through it.
            if (viaProxy && IpText.IsPrivate(client))
                return null;

            var now = timeUtc.Ticks;
            PruneIfDue(now);
            foreach (var compiled in _rules)
            {
                if (!compiled.Applies(path, site))
                    continue;

                var rule = compiled.Rule;
                var key = (rule.Id, ip);
                if (!_hits.TryGetValue(key, out var times))
                {
                    if (_hits.Count >= MaxCounters)
                        Prune(now, aggressive: true);
                    _hits[key] = times = new Queue<long>();
                }

                var window = TimeSpan.FromMinutes(rule.WithinMinutes).Ticks;
                times.Enqueue(now);
                while (times.Count > 0 && times.Peek() <= now - window)
                    times.Dequeue();
                while (times.Count > rule.Hits)
                    times.Dequeue();

                if (times.Count < rule.Hits)
                    continue;

                // Tripped: stop counting this IP anywhere until it is unblocked.
                _blocked.Add(ip);
                foreach (var stale in _hits.Keys.Where(k => k.Ip == ip).ToList())
                    _hits.Remove(stale);
                return new BlockDecision(
                    ip, viaProxy, rule.Id, rule.Name, rule.Hits, site, path, timeUtc,
                    rule.BlockHours > 0 ? timeUtc.AddHours(rule.BlockHours) : null,
                    WatchOnly: _mode == BlockingMode.Watch);
            }

            return null;
        }
    }

    private CompiledRule? FindRule(string path, string? site)
    {
        foreach (var compiled in _rules)
        {
            if (compiled.Applies(path, site))
                return compiled;
        }

        return null;
    }

    private void PruneIfDue(long now)
    {
        if (now - _lastPruneTicks < TimeSpan.TicksPerMinute)
            return;
        _lastPruneTicks = now;
        Prune(now, aggressive: false);
    }

    /// <summary>Drops counters whose newest hit has left the rule's window; when still too many, drops them all.</summary>
    private void Prune(long now, bool aggressive)
    {
        var windows = _rules.ToDictionary(r => r.Rule.Id, r => TimeSpan.FromMinutes(r.Rule.WithinMinutes).Ticks, StringComparer.Ordinal);
        foreach (var (key, times) in _hits.ToList())
        {
            if (times.Count == 0 || !windows.TryGetValue(key.RuleId, out var window) || times.Last() <= now - window)
                _hits.Remove(key);
        }

        if (aggressive && _hits.Count >= MaxCounters)
            _hits.Clear();
    }

    private sealed record CompiledRule(BlockRule Rule, UrlPattern Pattern, HashSet<string>? Sites)
    {
        public bool Applies(string path, string? site) =>
            (Sites is null || (site is not null && Sites.Contains(site))) && Pattern.IsMatch(path);
    }
}
