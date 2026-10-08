using System.Globalization;
using IISMonitor.Core.Collection;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Collectors;

/// <summary>What the "IP and URL tracking" health row and the warnings on the IPs &amp; URLs tab say.</summary>
internal static class RequestTrackingStatus
{
    public const string HealthName = "IP and URL tracking";

    private const int MaxNamesShown = 5;

    public static CollectorHealth Health(
        MonitorSettings settings, IisTopology? topology, bool iisLogRunning, string? recorderError,
        long recordedRequests, long droppedRequests, long databaseBytes)
    {
        var pools = settings.RequestTrackingPools;
        if (pools.Count == 0)
            return Ok("Off. Turn it on per app pool in Settings.");

        if (!settings.EnableResponseTimeTracing)
            return Fail("Needs the IIS ETW log stream: turn on Live response times in Settings.");

        if (recorderError is not null)
            return Fail(recorderError);

        if (!iisLogRunning)
            return Fail("Not recording: the IIS ETW log stream isn't running (see Response times).");

        var missing = SitesMissingFields(topology, pools);
        if (missing.Count > 0)
        {
            return Fail($"{JoinNames(missing)} {(missing.Count == 1 ? "doesn't" : "don't")} send the client IP, method and substatus to the IIS ETW log yet. " +
                        "Turn them on with the button on the IPs & URLs tab.");
        }

        var message = $"Recording {Count(pools.Count, "app pool")}: {JoinNames(pools)}. " +
                      $"{recordedRequests.ToString("N0", CultureInfo.CurrentCulture)} requests since start; " +
                      $"{MetricFormatter.Bytes(databaseBytes)} on disk; kept {Count(settings.RequestLogRetentionDays, "day")}.";
        if (droppedRequests > 0)
        {
            message += $" · {droppedRequests.ToString("N0", CultureInfo.CurrentCulture)} requests not itemised " +
                       $"(more than {RequestLogAggregator.DefaultMaxPendingRows.ToString("N0", CultureInfo.CurrentCulture)} different IP/URL combinations within a minute)";
        }

        return Ok(message);
    }

    /// <summary>Adds what the user should know about <paramref name="report"/>'s app pool; sets <see cref="RequestLogReport.LoggingFieldsMissing"/>.</summary>
    public static void AddWarnings(
        RequestLogReport report, string appPool, MonitorSettings settings, IisTopology? topology,
        bool iisLogRunning, string? recorderError, long droppedRequests)
    {
        var tracked = settings.RequestTrackingPools.Contains(appPool, StringComparer.OrdinalIgnoreCase);
        if (!tracked)
        {
            report.Warnings.Add("Tracking is off for this app pool; showing what was recorded earlier.");
        }
        else if (!settings.EnableResponseTimeTracing)
        {
            report.Warnings.Add("Nothing new is recorded: IP and URL tracking needs Live response times, which is off in Settings.");
        }
        else if (!iisLogRunning)
        {
            report.Warnings.Add("Nothing new is recorded right now: the IIS ETW log stream isn't running. See the Collectors tab.");
        }

        if (tracked)
        {
            var missing = SitesMissingFields(topology, [appPool]);
            if (missing.Count > 0)
            {
                report.LoggingFieldsMissing = true;
                report.Warnings.Add($"{JoinNames(missing)} {(missing.Count == 1 ? "doesn't" : "don't")} send the client IP, method and substatus to the IIS ETW log yet, " +
                                    "so requests may be missing or show the client as (unknown).");
            }
        }

        if (recorderError is not null)
            report.Warnings.Add(recorderError);

        if (droppedRequests > 0)
        {
            report.Warnings.Add($"{droppedRequests.ToString("N0", CultureInfo.CurrentCulture)} requests since the collector started were counted but not itemised: " +
                                $"more than {RequestLogAggregator.DefaultMaxPendingRows.ToString("N0", CultureInfo.CurrentCulture)} different IP/URL combinations arrived within a minute.");
        }
    }

    /// <summary>Sites that run an application of one of <paramref name="pools"/> but don't log the fields tracking needs.</summary>
    internal static List<string> SitesMissingFields(IisTopology? topology, IReadOnlyCollection<string> pools)
    {
        if (topology is null)
            return [];

        var wanted = new HashSet<string>(pools, StringComparer.OrdinalIgnoreCase);
        return topology.Sites
            .Where(s => !s.RequestFieldsLogged && s.Applications.Any(a => wanted.Contains(a.AppPool)))
            .Select(s => s.Name)
            .ToList();
    }

    /// <summary>"A", "A and B", "A, B and C", or "A, B, C, D, E and 3 more".</summary>
    internal static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return "";
        if (names.Count == 1)
            return names[0];
        if (names.Count > MaxNamesShown)
            return string.Join(", ", names.Take(MaxNamesShown)) + $" and {names.Count - MaxNamesShown} more";
        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    private static string Count(int count, string noun) =>
        count.ToString("N0", CultureInfo.CurrentCulture) + " " + noun + (count == 1 ? "" : "s");

    private static CollectorHealth Ok(string message) => new() { Name = HealthName, Ok = true, Message = message };

    private static CollectorHealth Fail(string message) => new() { Name = HealthName, Ok = false, Message = message };
}
