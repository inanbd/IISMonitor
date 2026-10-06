using System.ComponentModel;
using System.Diagnostics;
using IISMonitor.Core.Collection;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads the IIS performance counter categories in one call each and turns raw samples into
/// values (rates need the previous sample, which is kept between ticks).
/// </summary>
internal sealed class IisCounterReader
{
    private readonly CategorySampler _webService = new("Web Service", "Current Connections", "Total Method Requests/sec", "Bytes Sent/sec", "Bytes Received/sec");
    private readonly CategorySampler _workers = new("W3SVC_W3WP", "Active Requests", "Requests / Sec");
    private readonly CategorySampler _queues = new("HTTP Service Request Queues", "CurrentQueueSize");

    public IisCounterValues Read(List<string> problems)
    {
        var result = new IisCounterValues();

        var sites = TrySample(_webService, problems);
        result.SitesAvailable = sites is not null;
        if (sites is not null)
        {
            foreach (var (instance, values) in sites)
            {
                if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Sites[instance] = new SiteCounterValues(
                    ToInt(values.GetValueOrDefault("Current Connections")),
                    values.GetValueOrDefault("Total Method Requests/sec"),
                    values.GetValueOrDefault("Bytes Sent/sec"),
                    values.GetValueOrDefault("Bytes Received/sec"));
            }
        }

        var workers = TrySample(_workers, problems);
        result.WorkersAvailable = workers is not null;
        if (workers is not null)
        {
            foreach (var (instance, values) in workers)
            {
                if (!PerfInstanceName.TryParseWorkerInstance(instance, out var pid, out _))
                    continue;
                result.Workers[pid] = new WorkerCounterValues(
                    instance,
                    ToInt(values.GetValueOrDefault("Active Requests")),
                    values.GetValueOrDefault("Requests / Sec"));
            }
        }

        var queues = TrySample(_queues, problems);
        result.QueuesAvailable = queues is not null;
        if (queues is not null)
        {
            foreach (var (instance, values) in queues)
            {
                if (values.GetValueOrDefault("CurrentQueueSize") is { } size)
                    result.QueueLengths[instance] = (int)size;
            }
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, double?>>? TrySample(CategorySampler sampler, List<string> problems)
    {
        try
        {
            return sampler.Sample();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            problems.Add($"\"{sampler.Category}\": {e.Message}");
            return null;
        }
    }

    private static int? ToInt(double? value) => value is { } v ? (int)Math.Round(v) : null;

    private sealed class CategorySampler(string category, params string[] counters)
    {
        private readonly HashSet<string> _counters = new(counters, StringComparer.OrdinalIgnoreCase);
        private Dictionary<(string Instance, string Counter), CounterSample> _previous = [];

        public string Category => category;

        /// <summary>Instance name → counter name → value.</summary>
        public Dictionary<string, Dictionary<string, double?>> Sample()
        {
            var data = new PerformanceCounterCategory(category).ReadCategory();
            var result = new Dictionary<string, Dictionary<string, double?>>(StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<(string, string), CounterSample>();

            foreach (InstanceDataCollection counter in data.Values)
            {
                var counterName = _counters.FirstOrDefault(c => c.Equals(counter.CounterName, StringComparison.OrdinalIgnoreCase));
                if (counterName is null)
                    continue;

                foreach (InstanceData instance in counter.Values)
                {
                    var key = (instance.InstanceName, counterName);
                    var sample = instance.Sample;
                    current[key] = sample;

                    double? value;
                    try
                    {
                        value = _previous.TryGetValue(key, out var previous)
                            ? CounterSample.Calculate(previous, sample)
                            : CounterSample.Calculate(sample);
                    }
                    catch (InvalidOperationException)
                    {
                        value = null;
                    }

                    if (value is { } v && (double.IsNaN(v) || double.IsInfinity(v) || v < 0))
                        value = null;

                    if (!result.TryGetValue(instance.InstanceName, out var values))
                        result[instance.InstanceName] = values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                    values[counterName] = value;
                }
            }

            _previous = current;
            return result;
        }
    }
}
