using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Protocol;

/// <summary>Runs the collector inside the dashboard process (standalone mode).</summary>
public sealed class InProcessBackend : IMonitorBackend
{
    private readonly IMonitorHost _host;
    private readonly IAsyncDisposable? _ownedHost;

    public InProcessBackend(IMonitorHost host, IAsyncDisposable? ownedHost = null)
    {
        _host = host;
        _ownedHost = ownedHost;
        _host.SnapshotProduced += OnSnapshot;
        _host.SettingsChanged += OnSettings;
    }

    public string Description => "Standalone";

    public bool IsStandalone => true;

    public MonitorSettings Settings => _host.Settings;

    public event EventHandler<MonitorSnapshot>? SnapshotReceived;

    public event EventHandler<MonitorSettings>? SettingsChanged;

    // The engine lives in this process, so the connection can't drop.
    public event EventHandler? Disconnected { add { } remove { } }

    public Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken = default) =>
        _host.UpdateSettingsAsync(settings, cancellationToken);

    public Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
        _host.QueryHistoryAsync(query, cancellationToken);

    public Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken = default) =>
        _host.ListHistoryEntitiesAsync(kind, cancellationToken);

    public Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken = default) =>
        _host.EnableIisEtwLoggingAsync(cancellationToken);

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot) => SnapshotReceived?.Invoke(this, snapshot);

    private void OnSettings(object? sender, MonitorSettings settings) => SettingsChanged?.Invoke(this, settings);

    public async ValueTask DisposeAsync()
    {
        _host.SnapshotProduced -= OnSnapshot;
        _host.SettingsChanged -= OnSettings;
        if (_ownedHost is not null)
            await _ownedHost.DisposeAsync().ConfigureAwait(false);
    }
}
