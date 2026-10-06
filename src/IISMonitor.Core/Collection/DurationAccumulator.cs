using IISMonitor.Core.Models;

namespace IISMonitor.Core.Collection;

/// <summary>
/// Collects request durations for one interval. Count, average and max are exact; the 95th
/// percentile comes from a fixed-size uniform reservoir, so memory stays bounded under heavy load.
/// Not thread-safe.
/// </summary>
public sealed class DurationAccumulator
{
    public const int DefaultReservoirSize = 8192;

    private readonly double[] _reservoir;
    private readonly Random _random;
    private int _count;
    private double _sum;
    private double _max;
    private int _clientErrors;
    private int _serverErrors;

    public DurationAccumulator(int reservoirSize = DefaultReservoirSize, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reservoirSize, 1);
        _reservoir = new double[reservoirSize];
        _random = random ?? Random.Shared;
    }

    public int Count => _count;

    public void Add(double durationMs, int statusCode)
    {
        if (durationMs < 0 || double.IsNaN(durationMs))
            durationMs = 0;

        if (_count < _reservoir.Length)
        {
            _reservoir[_count] = durationMs;
        }
        else
        {
            // Algorithm R: keep each of the first n items with equal probability.
            var slot = _random.NextInt64(_count + 1L);
            if (slot < _reservoir.Length)
                _reservoir[slot] = durationMs;
        }

        _count++;
        _sum += durationMs;
        if (durationMs > _max)
            _max = durationMs;
        if (statusCode is >= 400 and < 500)
            _clientErrors++;
        else if (statusCode >= 500)
            _serverErrors++;
    }

    public ResponseStats ToStats()
    {
        if (_count == 0)
            return new ResponseStats();

        var kept = Math.Min(_count, _reservoir.Length);
        var sorted = new double[kept];
        Array.Copy(_reservoir, sorted, kept);
        Array.Sort(sorted);

        return new ResponseStats
        {
            RequestCount = _count,
            AverageMs = _sum / _count,
            P95Ms = Percentile(sorted, 0.95),
            MaxMs = _max,
            ClientErrors = _clientErrors,
            ServerErrors = _serverErrors,
        };
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
            return 0;
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
