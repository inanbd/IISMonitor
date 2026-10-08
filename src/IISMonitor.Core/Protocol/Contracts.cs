using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Protocol;

public sealed class CommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public static CommandResult Ok(string message) => new() { Success = true, Message = message };

    public static CommandResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>What the collector (the Windows service, or the engine inside the dashboard) offers.</summary>
public interface IMonitorHost
{
    MonitorSettings Settings { get; }

    MonitorSnapshot? LatestSnapshot { get; }

    event EventHandler<MonitorSnapshot>? SnapshotProduced;

    event EventHandler<MonitorSettings>? SettingsChanged;

    Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken);

    Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);

    Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken);

    Task<SlowQueryReport> QuerySlowQueriesAsync(SlowQueryRequest request, CancellationToken cancellationToken);

    /// <summary>Turns on IIS's ETW log target (and the log fields response-time tracking needs) for all sites.</summary>
    Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken);

    /// <summary>Tracked requests of one app pool: client IPs, URLs, or the details of one of them.</summary>
    Task<RequestLogReport> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken);

    /// <summary>IP addresses IIS currently refuses (IP and Domain Restrictions).</summary>
    Task<List<BlockedIp>> ListBlockedIpsAsync(CancellationToken cancellationToken);

    /// <summary>Makes IIS refuse an IP address, for one app pool's applications or the whole server.</summary>
    Task<CommandResult> BlockIpAsync(IpBlockRequest request, CancellationToken cancellationToken);

    /// <summary>Removes a block added by <see cref="BlockIpAsync"/> (or by hand) at one location.</summary>
    Task<CommandResult> UnblockIpAsync(IpBlockRequest request, CancellationToken cancellationToken);
}

/// <summary>The dashboard's view of a collector, local or over the named pipe.</summary>
public interface IMonitorBackend : IAsyncDisposable
{
    /// <summary>Where the data comes from, for display ("Windows service", "Standalone").</summary>
    string Description { get; }

    bool IsStandalone { get; }

    MonitorSettings Settings { get; }

    event EventHandler<MonitorSnapshot>? SnapshotReceived;

    event EventHandler<MonitorSettings>? SettingsChanged;

    /// <summary>Raised once when the connection to the collector is lost.</summary>
    event EventHandler? Disconnected;

    /// <summary>
    /// Starts raising <see cref="SnapshotReceived"/> and <see cref="SettingsChanged"/>, beginning with the
    /// collector's latest snapshot. Subscribe first, then call this, so the first snapshot isn't missed.
    /// Calling it again does nothing.
    /// </summary>
    void Start();

    Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken = default);

    Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken = default);

    Task<SlowQueryReport> QuerySlowQueriesAsync(SlowQueryRequest request, CancellationToken cancellationToken = default);

    Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked requests of one app pool: client IPs, URLs, or the details of one of them.</summary>
    Task<RequestLogReport> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>IP addresses IIS currently refuses (IP and Domain Restrictions).</summary>
    Task<List<BlockedIp>> ListBlockedIpsAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes IIS refuse an IP address, for one app pool's applications or the whole server.</summary>
    Task<CommandResult> BlockIpAsync(IpBlockRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a block added by <see cref="BlockIpAsync"/> (or by hand) at one location.</summary>
    Task<CommandResult> UnblockIpAsync(IpBlockRequest request, CancellationToken cancellationToken = default);
}
