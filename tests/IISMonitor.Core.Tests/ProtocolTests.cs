using System.IO.Pipes;
using IISMonitor.Core.Blocking;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.Requests;
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

        public Task<SlowQueryReport> QuerySlowQueriesAsync(SlowQueryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SlowQueryReport
            {
                Groups = [new SlowQueryGroup { AppPool = request.AppPool ?? "", Statement = "SELECT ?", Count = 3, MaxMs = 2500 }],
            });

        public Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok("done"));

        public IisLoggingRequest? LastLoggingRequest { get; private set; }

        public Task<CommandResult> ConfigureIisLoggingAsync(IisLoggingRequest request, CancellationToken cancellationToken)
        {
            LastLoggingRequest = request;
            return Task.FromResult(CommandResult.Ok("configured"));
        }

        public Task<RequestPage> QueryRequestsAsync(RequestQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new RequestPage
            {
                LatestId = 7,
                Matched = 1,
                Requests =
                [
                    new RequestRecord
                    {
                        Id = 7, ClientIp = query.Filter.Ip, Method = "POST", MethodGroup = MethodGroup.Post,
                        Kind = query.Filter.Kinds.FirstOrDefault(), Path = "/wp-login.php", Status = 404, Rule = "WordPress",
                    },
                ],
            });

        public Task<TopIpReport> QueryTopIpsAsync(TopIpQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new TopIpReport { Visitors = 1, Rows = [new TopIpRow { Ip = "203.0.113.5", Total = 9, Api = 9, Blocked = "Firewall (Active)" }] });

        public Task<BlockList> ListBlocksAsync(BlockListQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new BlockList
            {
                Entries = [new BlockEntry { Ip = "203.0.113.5", Method = BlockMethod.Iis, State = BlockState.Active, ExpiresUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc) }],
                Status = new BlockingStatus { Mode = BlockingMode.Block, Enforcing = true, ActiveIis = 1 },
            });

        public Task<CommandResult> BlockIpAsync(ManualBlockRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok($"blocked {request.Ip} via {request.Method}"));

        public Task<CommandResult> UnblockIpAsync(UnblockRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.Ok($"unblocked {request.Ip}"));
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
        var slow = await client.QuerySlowQueriesAsync(new SlowQueryRequest { AppPool = "Shop" }, cts.Token);
        Assert.Equal("Shop", Assert.Single(slow.Groups).AppPool);
        Assert.True((await client.EnableIisEtwLoggingAsync(cts.Token)).Success);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.QueryHistoryAsync(new HistoryQuery { Name = "boom" }, cts.Token));
        Assert.Equal("history unavailable", error.Message);

        // Requests and blacklist messages.
        Assert.Contains(PipeProtocol.Feature.Requests, client.Features);
        Assert.Contains(PipeProtocol.Feature.Blocking, client.Features);
        var page = await client.QueryRequestsAsync(new RequestQuery
        {
            Filter = new RequestFilter { Ip = "203.0.113.5", Kinds = [RequestKind.XhrApi], Methods = [MethodGroup.Post] },
        }, cts.Token);
        var request = Assert.Single(page.Requests);
        Assert.Equal(("203.0.113.5", RequestKind.XhrApi, MethodGroup.Post, "WordPress"), (request.ClientIp, request.Kind, request.MethodGroup, request.Rule));
        var top = await client.QueryTopIpsAsync(new TopIpQuery { Window = TopIpWindow.OneDay }, cts.Token);
        Assert.Equal("Firewall (Active)", Assert.Single(top.Rows).Blocked);
        var blocks = await client.ListBlocksAsync(new BlockListQuery(), cts.Token);
        Assert.Equal((BlockMethod.Iis, BlockState.Active), (blocks.Entries[0].Method, blocks.Entries[0].State));
        Assert.Equal(DateTimeKind.Utc, blocks.Entries[0].ExpiresUtc!.Value.Kind);
        Assert.Equal(BlockingMode.Block, blocks.Status.Mode);
        Assert.Equal("blocked 198.51.100.7 via Iis", (await client.BlockIpAsync(new ManualBlockRequest { Ip = "198.51.100.7", Method = BlockMethod.Iis }, cts.Token)).Message);
        Assert.Equal("unblocked 198.51.100.7", (await client.UnblockIpAsync(new UnblockRequest { Ip = "198.51.100.7" }, cts.Token)).Message);
        Assert.True((await client.ConfigureIisLoggingAsync(new IisLoggingRequest { HeaderFieldSites = [2, 3] }, cts.Token)).Success);
        Assert.Equal([2L, 3L], host.LastLoggingRequest!.HeaderFieldSites);

        // A snapshot carries request rates and the blacklist status.
        host.Publish(new MonitorSnapshot
        {
            Requests = new RequestActivity { LatestId = 42, PerSecondByKind = { [RequestKind.Download] = 1.5 } },
            Blocking = new BlockingStatus { Mode = BlockingMode.Watch, Watching = 2 },
        });
        var withRequests = await Next(received, cts.Token);
        Assert.Equal(1.5, withRequests.Requests!.PerSecondByKind[RequestKind.Download]);
        Assert.Equal(2, withRequests.Blocking!.Watching);

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
