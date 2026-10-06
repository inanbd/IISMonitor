using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;

namespace IISMonitor.Core.History;

/// <summary>
/// Accumulates live snapshots and, when flushed, produces one <see cref="HistoryRow"/> per entity
/// using each metric's <see cref="WindowAggregation"/>. Not thread-safe.
/// </summary>
public sealed class HistoryAggregator
{
    private readonly Dictionary<(EntityKind Kind, string Name), Dictionary<string, Accumulator>> _entities = [];
    private DateTime? _windowStartUtc;

    public DateTime? WindowStartUtc => _windowStartUtc;

    public void Add(MonitorSnapshot snapshot)
    {
        _windowStartUtc ??= snapshot.TimestampUtc;

        foreach (var pool in snapshot.AppPools)
            AddValues(EntityKind.AppPool, pool.Name, MetricCatalog.Extract(pool, snapshot.IntervalSeconds));
        foreach (var site in snapshot.Sites)
            AddValues(EntityKind.Site, site.Name, MetricCatalog.Extract(site, snapshot.IntervalSeconds));
        AddValues(EntityKind.Server, snapshot.Server.MachineName, MetricCatalog.Extract(snapshot.Server));
    }

    /// <summary>
    /// Produces the rows for the current window and starts a new one.
    /// </summary>
    /// <param name="responseBySite">Response stats for the whole window, by site name; null when response times aren't traced.</param>
    /// <param name="responseByPool">Response stats for the whole window, by app pool name.</param>
    /// <param name="loggedSites">Sites whose response time is traced; others get no response values. Null means all.</param>
    /// <param name="loggedPools">App pools serving a traced site; others get no response values. Null means all.</param>
    public List<HistoryRow> Flush(
        DateTime nowUtc,
        IReadOnlyDictionary<string, ResponseStats>? responseBySite,
        IReadOnlyDictionary<string, ResponseStats>? responseByPool,
        ISet<string>? loggedSites = null,
        ISet<string>? loggedPools = null)
    {
        var rows = new List<HistoryRow>();
        if (_windowStartUtc is not { } start)
            return rows;

        var seconds = Math.Max(1, (nowUtc - start).TotalSeconds);
        foreach (var ((kind, name), accumulators) in _entities)
        {
            var values = new Dictionary<string, double?>();
            foreach (var metric in MetricCatalog.Metrics(kind))
            {
                if (metric.Aggregation == WindowAggregation.ResponseWindow)
                    continue;
                values[metric.Key] = accumulators.TryGetValue(metric.SourceKey, out var acc)
                    ? metric.Aggregation switch
                    {
                        WindowAggregation.Max => acc.Max,
                        WindowAggregation.Sum => acc.Sum,
                        _ => acc.Average,
                    }
                    : null;
            }

            if (kind != EntityKind.Server)
            {
                var (source, logged) = kind == EntityKind.Site ? (responseBySite, loggedSites) : (responseByPool, loggedPools);
                ResponseStats? stats = null;
                if (source is not null && (logged is null || logged.Contains(name)))
                    stats = source.GetValueOrDefault(name) ?? new ResponseStats();
                MetricCatalog.AddResponse(values, stats, seconds);
            }

            rows.Add(new HistoryRow(kind, name, start, values));
        }

        _entities.Clear();
        _windowStartUtc = null;
        return rows;
    }

    private void AddValues(EntityKind kind, string name, Dictionary<string, double?> values)
    {
        if (!_entities.TryGetValue((kind, name), out var accumulators))
            _entities[(kind, name)] = accumulators = [];

        foreach (var (key, value) in values)
        {
            if (value is not { } v || double.IsNaN(v))
                continue;
            if (!accumulators.TryGetValue(key, out var acc))
                accumulators[key] = acc = new Accumulator();
            acc.Add(v);
        }
    }

    private sealed class Accumulator
    {
        private double _sum;
        private int _count;

        public double? Max { get; private set; }

        public double? Average => _count == 0 ? null : _sum / _count;

        public double? Sum => _count == 0 ? null : _sum;

        public void Add(double value)
        {
            _sum += value;
            _count++;
            if (Max is null || value > Max)
                Max = value;
        }
    }
}
