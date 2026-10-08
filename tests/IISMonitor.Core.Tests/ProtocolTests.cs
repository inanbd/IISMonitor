using System.IO.Pipes;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.RequestLog;
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

        public System.Collections.Concurrent.ConcurrentQueue<IpBlockRequest> Unblocked { get; } = new();

        public Task<RequestLogReport> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new RequestLogReport
            {
                View = query.View,
                Details =
                [
                    new RequestLogDetailRow
                    {
                        MinuteUnixMs = HistoryStore.ToUnixMs(query.FromUtc),
                        ClientIp = query.Key ?? "",
                        Url = query.AppPool + "/login",
                        Method = "POST",
                        Status = 401,
                        SubStatus = 1,
                        Hits = query.PerMinute ? 7 : 70,
                        AverageTimeMs = 12.5,
                    },
                ],
                TotalRows = 1,
                TotalHits = 7,
                Truncated = true,
                Warnings = ["Tracking is off for this app pool."],
                LoggingFieldsMissing = true,
                DatabaseBytes = 4096,
            });

        public Task<List<BlockedIp>> ListBlockedIpsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new List<BlockedIp>
            {
                new() { IpAddress = "203.0.113.7", Location = "Shop" },
                new() { IpAddress = "198.51.100.0", SubnetMask = "255.255.255.0" },
            });

        public Task<CommandResult> BlockIpAsync(IpBlockRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.AppPool == "ShopPool"
                ? CommandResult.Ok($"Blocked {request.IpAddress} for {request.AppPool}.")
                : CommandResult.Fail($"No app pool '{request.AppPool}'."));

        public Task<CommandResult> UnblockIpAsync(IpBlockRequest request, CancellationToken cancellationToken)
        {
            Unblocked.Enqueue(request);
            return Task.FromResult(CommandResult.Ok($"Unblocked {request.IpAddress}."));
        }
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

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task Request_log_and_ip_blocking_round_trip_over_the_pipe()
    {
        var pipeName = "iismonitor-test-" + Guid.NewGuid().ToString("N")[..8];
        var host = new FakeHost();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new PipeServer(host, () => new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous));
        var serverTask = server.RunAsync(cts.Token);

        await using (var client = await PipeClientBackend.TryConnectAsync(TimeSpan.FromSeconds(10), cts.Token, pipeName))
        {
            Assert.NotNull(client);
            client.Start();
            Assert.Equal(3, client.Settings.RequestLogRetentionDays);

            // The new settings survive a save through the pipe (the reason for protocol version 2).
            var settings = await client.UpdateSettingsAsync(
                new MonitorSettings { RequestTrackingPools = ["ShopPool", " ApiPool "], RequestLogRetentionDays = 10 }, cts.Token);
            Assert.Equal(["ApiPool", "ShopPool"], settings.RequestTrackingPools);
            Assert.Equal(10, settings.RequestLogRetentionDays);

            var from = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            var report = await client.QueryRequestLogAsync(new RequestLogQuery
            {
                AppPool = "ShopPool",
                FromUtc = from,
                ToUtc = from.AddHours(1),
                View = RequestLogView.ClientDetail,
                Key = "203.0.113.7",
                PerMinute = false,
            }, cts.Token);
            Assert.Equal(RequestLogView.ClientDetail, report.View);
            var row = Assert.Single(report.Details);
            Assert.Equal(HistoryStore.ToUnixMs(from), row.MinuteUnixMs);
            Assert.Equal("203.0.113.7", row.ClientIp);
            Assert.Equal("ShopPool/login", row.Url);
            Assert.Equal(("POST", 401, 1, 70L, 12.5), (row.Method, row.Status, row.SubStatus, row.Hits, row.AverageTimeMs));
            Assert.Equal((1L, 7L, true, true, 4096L), (report.TotalRows, report.TotalHits, report.Truncated, report.LoggingFieldsMissing, report.DatabaseBytes));
            Assert.Equal(["Tracking is off for this app pool."], report.Warnings);
            Assert.Empty(report.Summaries);

            var blocked = await client.ListBlockedIpsAsync(cts.Token);
            Assert.Equal(2, blocked.Count);
            Assert.Equal(("203.0.113.7", null, "Shop"), (blocked[0].IpAddress, blocked[0].SubnetMask, blocked[0].Location));
            Assert.Equal(("198.51.100.0", "255.255.255.0", ""), (blocked[1].IpAddress, blocked[1].SubnetMask, blocked[1].Location));

            var ok = await client.BlockIpAsync(new IpBlockRequest { IpAddress = "203.0.113.7", AppPool = "ShopPool" }, cts.Token);
            Assert.True(ok.Success);
            Assert.Equal("Blocked 203.0.113.7 for ShopPool.", ok.Message);
            var failed = await client.BlockIpAsync(new IpBlockRequest { IpAddress = "203.0.113.7", AppPool = "Nope" }, cts.Token);
            Assert.False(failed.Success);
            Assert.Equal("No app pool 'Nope'.", failed.Message);

            Assert.True((await client.UnblockIpAsync(new IpBlockRequest { IpAddress = "203.0.113.7", Location = "" }, cts.Token)).Success);
            var unblocked = Assert.Single(host.Unblocked);
            Assert.Equal(("203.0.113.7", "", null), (unblocked.IpAddress, unblocked.Location, unblocked.AppPool));
        }

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task In_process_backend_forwards_request_log_and_blocking_calls()
    {
        var host = new FakeHost();
        await using var backend = new InProcessBackend(host);

        var report = await backend.QueryRequestLogAsync(new RequestLogQuery { AppPool = "ShopPool", View = RequestLogView.UrlDetail, Key = "/x" });
        Assert.Equal(RequestLogView.UrlDetail, report.View);
        Assert.Equal("/x", Assert.Single(report.Details).ClientIp);
        Assert.Equal(2, (await backend.ListBlockedIpsAsync()).Count);
        Assert.True((await backend.BlockIpAsync(new IpBlockRequest { IpAddress = "203.0.113.7", AppPool = "ShopPool" })).Success);
        Assert.True((await backend.UnblockIpAsync(new IpBlockRequest { IpAddress = "203.0.113.7", Location = "Shop" })).Success);
        Assert.Equal("Shop", Assert.Single(host.Unblocked).Location);
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
