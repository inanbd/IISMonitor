using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>
/// Receives completed requests (from the IIS ETW log stream) on any thread and keeps two
/// independent windows: one drained on every live sample, one drained when history is written.
/// </summary>
public sealed class ResponseTimeAggregator
{
    private readonly object _gate = new();
    private Window _live = new();
    private Window _history = new();

    public void Record(long siteId, string? appPool, double durationMs, int statusCode)
    {
        lock (_gate)
        {
            _live.Add(siteId, appPool, durationMs, statusCode);
            _history.Add(siteId, appPool, durationMs, statusCode);
        }
    }

    public (Dictionary<long, ResponseStats> BySite, Dictionary<string, ResponseStats> ByPool) DrainLive()
    {
        Window drained;
        lock (_gate)
        {
            drained = _live;
            _live = new Window();
        }

        return drained.ToStats();
    }

    public (Dictionary<long, ResponseStats> BySite, Dictionary<string, ResponseStats> ByPool) DrainHistory()
    {
        Window drained;
        lock (_gate)
        {
            drained = _history;
            _history = new Window();
        }

        return drained.ToStats();
    }

    private sealed class Window
    {
        private readonly Dictionary<long, DurationAccumulator> _sites = [];
        private readonly Dictionary<string, DurationAccumulator> _pools = new(StringComparer.OrdinalIgnoreCase);

        public void Add(long siteId, string? appPool, double durationMs, int statusCode)
        {
            if (!_sites.TryGetValue(siteId, out var site))
                _sites[siteId] = site = new DurationAccumulator();
            site.Add(durationMs, statusCode);

            if (!string.IsNullOrEmpty(appPool))
            {
                if (!_pools.TryGetValue(appPool, out var pool))
                    _pools[appPool] = pool = new DurationAccumulator();
                pool.Add(durationMs, statusCode);
            }
        }

        public (Dictionary<long, ResponseStats>, Dictionary<string, ResponseStats>) ToStats() =>
            (_sites.ToDictionary(kv => kv.Key, kv => kv.Value.ToStats()),
             _pools.ToDictionary(kv => kv.Key, kv => kv.Value.ToStats(), StringComparer.OrdinalIgnoreCase));
    }
}
