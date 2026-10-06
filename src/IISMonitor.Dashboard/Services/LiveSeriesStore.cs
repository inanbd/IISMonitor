using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;

namespace IISMonitor.Dashboard.Services;

/// <summary>
/// Keeps the most recent samples of every entity in fixed-size ring buffers, so charts can show
/// any pool or site immediately when it is selected. Thread-safe.
/// </summary>
public sealed class LiveSeriesStore
{
    public const int Capacity = 3600;
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<(EntityKind Kind, string Name), EntitySeries> _series = [];

    public void Add(MonitorSnapshot snapshot)
    {
        var time = snapshot.TimestampUtc.ToLocalTime().ToOADate();
        lock (_gate)
        {
            foreach (var pool in snapshot.AppPools)
                Get(EntityKind.AppPool, pool.Name).Add(time, MetricCatalog.Extract(pool, snapshot.IntervalSeconds));
            foreach (var site in snapshot.Sites)
                Get(EntityKind.Site, site.Name).Add(time, MetricCatalog.Extract(site, snapshot.IntervalSeconds));
            Get(EntityKind.Server, "").Add(time, MetricCatalog.Extract(snapshot.Server));

            var cutoff = snapshot.TimestampUtc.ToLocalTime().Subtract(ForgetAfter).ToOADate();
            foreach (var stale in _series.Where(kv => kv.Value.LastTime < cutoff).Select(kv => kv.Key).ToList())
                _series.Remove(stale);
        }
    }

    /// <summary>Points (OLE automation dates, values) of one metric since <paramref name="fromOADate"/>, skipping gaps.</summary>
    public (double[] Xs, double[] Ys) Get(EntityKind kind, string name, string key, double fromOADate)
    {
        lock (_gate)
        {
            if (kind == EntityKind.Server)
                name = "";
            return _series.TryGetValue((kind, name), out var series) ? series.Read(key, fromOADate) : ([], []);
        }
    }

    private EntitySeries Get(EntityKind kind, string name)
    {
        if (!_series.TryGetValue((kind, name), out var series))
            _series[(kind, name)] = series = new EntitySeries();
        return series;
    }

    private sealed class EntitySeries
    {
        private readonly double[] _times = new double[Capacity];
        private readonly Dictionary<string, double[]> _values = [];
        private int _start;
        private int _count;

        public double LastTime { get; private set; }

        public void Add(double time, Dictionary<string, double?> values)
        {
            int index;
            if (_count < Capacity)
            {
                index = (_start + _count) % Capacity;
                _count++;
            }
            else
            {
                index = _start;
                _start = (_start + 1) % Capacity;
            }

            _times[index] = time;
            LastTime = time;

            foreach (var (key, value) in values)
            {
                if (!_values.ContainsKey(key))
                    _values[key] = CreateEmpty();
            }

            foreach (var (key, buffer) in _values)
                buffer[index] = values.TryGetValue(key, out var v) && v is { } d ? d : double.NaN;
        }

        public (double[], double[]) Read(string key, double from)
        {
            if (!_values.TryGetValue(key, out var buffer))
                return ([], []);

            var xs = new List<double>(_count);
            var ys = new List<double>(_count);
            for (var i = 0; i < _count; i++)
            {
                var index = (_start + i) % Capacity;
                if (_times[index] < from || double.IsNaN(buffer[index]))
                    continue;
                xs.Add(_times[index]);
                ys.Add(buffer[index]);
            }

            return (xs.ToArray(), ys.ToArray());
        }

        private static double[] CreateEmpty()
        {
            var buffer = new double[Capacity];
            Array.Fill(buffer, double.NaN);
            return buffer;
        }
    }
}
