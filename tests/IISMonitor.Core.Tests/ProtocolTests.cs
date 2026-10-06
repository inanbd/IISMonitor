using System.IO.Pipes;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Tests;

public class ProtocolTests
{
    private sealed class FakeHost : IMonitorHost
    {
        public MonitorSettings Settings { get; private set; } = new MonitorSettings().Normalize();

        public MonitorSnapshot? LatestSnapshot { get; set; }

        public event EventHandler<MonitorSnapshot>? SnapshotProduced;

        public event EventHandler<MonitorSettings>? SettingsChanged;

        public void Publish(MonitorSnapshot snapshot)
        {
            LatestSnapshot = snapshot;
            SnapshotProduced?.Invoke(this, snapshot);
        }

        public Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings.Normalize();
            SettingsChanged?.Invoke(this, Settings);
            return Task.FromResult(Settings);
        }

        public Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            query.Name == "boom"
                ? throw new InvalidOperationException("history unavailable")
                : Task.FromResult(new HistoryResult { Kind = query.Kind, Name = query.Name, Timestamps = [1, 2], Series = { ["cpu"] = [1.5, null] } });

        public Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(new List<string> { kind.ToString() });

        public Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok("done"));
    }

    [Fact]
    public async Task Client_receives_snapshots_and_gets_replies()
    {
        var pipeName = "iismonitor-test-" + Guid.NewGuid().ToString("N")[..8];
        var host = new FakeHost { LatestSnapshot = new MonitorSnapshot { Server = new ServerMetrics { MachineName = "first" } } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new PipeServer(host, () => new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await PipeClientBackend.TryConnectAsync(TimeSpan.FromSeconds(10), cts.Token, pipeName);
        Assert.NotNull(client);
        Assert.Equal(1000, client.Settings.SampleIntervalMs);

        var received = Channel<MonitorSnapshot>();
        client.SnapshotReceived += (_, s) => received.TryAdd(s);
        client.Start();

        Assert.Equal("first", (await Next(received, cts.Token)).Server.MachineName);

        await WaitFor(() => server.ClientCount == 1, cts.Token);
        host.Publish(new MonitorSnapshot
        {
            AppPools = [new AppPoolMetrics { Name = "Pool", CpuPercent = 12.5, DiskReadBytesPerSec = double.NaN }],
        });
        var snapshot = await Next(received, cts.Token);
        Assert.Equal(12.5, snapshot.AppPools[0].CpuPercent);
        Assert.True(double.IsNaN(snapshot.AppPools[0].DiskReadBytesPerSec!.Value));

        var updated = await client.UpdateSettingsAsync(new MonitorSettings { SampleIntervalMs = 5 }, cts.Token);
        Assert.Equal(MonitorSettings.MinSampleIntervalMs, updated.SampleIntervalMs);

        var history = await client.QueryHistoryAsync(new HistoryQuery { Kind = EntityKind.Site, Name = "Shop" }, cts.Token);
        Assert.Equal([1L, 2L], history.Timestamps);
        Assert.Equal([1.5, null], history.Series["cpu"]);

        Assert.Equal(["AppPool"], await client.ListHistoryEntitiesAsync(EntityKind.AppPool, cts.Token));
        Assert.True((await client.EnableIisEtwLoggingAsync(cts.Token)).Success);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.QueryHistoryAsync(new HistoryQuery { Name = "boom" }, cts.Token));
        Assert.Equal("history unavailable", error.Message);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task First_snapshot_is_not_lost_when_the_subscriber_attaches_late()
    {
        var pipeName = "iismonitor-test-" + Guid.NewGuid().ToString("N")[..8];
        var host = new FakeHost { LatestSnapshot = new MonitorSnapshot { Server = new ServerMetrics { MachineName = "first" } } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new PipeServer(host, () => new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous));
        var serverTask = server.RunAsync(cts.Token);

        await using (var client = await PipeClientBackend.TryConnectAsync(TimeSpan.FromSeconds(10), cts.Token, pipeName))
        {
            Assert.NotNull(client);

            // By now the server has long since sent the first snapshot.
            await Task.Delay(500, cts.Token);
            var received = Channel<MonitorSnapshot>();
            client.SnapshotReceived += (_, s) => received.TryAdd(s);
            client.Start();

            Assert.Equal("first", (await Next(received, cts.Token)).Server.MachineName);
        }

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public void In_process_backend_replays_the_latest_snapshot_on_start()
    {
        var host = new FakeHost { LatestSnapshot = new MonitorSnapshot { Server = new ServerMetrics { MachineName = "latest" } } };
        var backend = new InProcessBackend(host);
        var received = new List<MonitorSnapshot>();
        backend.SnapshotReceived += (_, s) => received.Add(s);

        host.Publish(new MonitorSnapshot { Server = new ServerMetrics { MachineName = "before start" } });
        Assert.Empty(received);

        backend.Start();
        backend.Start();
        host.Publish(new MonitorSnapshot { Server = new ServerMetrics { MachineName = "after start" } });

        Assert.Equal(["before start", "after start"], received.Select(s => s.Server.MachineName));
    }

    [Fact]
    public async Task Returns_null_when_no_service_is_listening()
    {
        var client = await PipeClientBackend.TryConnectAsync(TimeSpan.FromMilliseconds(300), default, "iismonitor-missing-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Null(client);
    }

    private static System.Collections.Concurrent.BlockingCollection<T> Channel<T>() => new();

    private static async Task<T> Next<T>(System.Collections.Concurrent.BlockingCollection<T> items, CancellationToken token) =>
        await Task.Run(() => items.Take(token), token);

    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(20, token);
    }
}
