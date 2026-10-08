using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Protocol;

/// <summary>
/// Serves an <see cref="IMonitorHost"/> to dashboards over a named pipe: pushes every snapshot and
/// settings change, and answers settings, history and command requests.
/// </summary>
public sealed class PipeServer(IMonitorHost host, Func<NamedPipeServerStream> createPipe, Action<string, Exception?>? log = null)
{
    private int _clientCount;

    public int ClientCount => Volatile.Read(ref _clientCount);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clients = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = createPipe();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("Could not create the pipe; retrying.", e);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException e)
            {
                log?.Invoke("A pipe connection failed.", e);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(Task.Run(() => ServeClientAsync(pipe, cancellationToken), CancellationToken.None));
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        Interlocked.Increment(ref _clientCount);
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var token = clientCts.Token;
        var connection = new MessageConnection(pipe);

        // Snapshots for a slow client are dropped rather than queued without bound.
        var outbox = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        void OnSnapshot(object? sender, MonitorSnapshot snapshot) =>
            outbox.Writer.TryWrite(Envelope.Create(PipeProtocol.Snapshot, snapshot));
        void OnSettings(object? sender, MonitorSettings settings) =>
            outbox.Writer.TryWrite(Envelope.Create(PipeProtocol.Settings, settings));

        try
        {
            await connection.SendAsync(Envelope.Create(PipeProtocol.Hello, new HelloPayload
            {
                ProtocolVersion = PipeProtocol.Version,
                MachineName = Environment.MachineName,
                Settings = host.Settings,
            }), token).ConfigureAwait(false);

            if (host.LatestSnapshot is { } latest)
                outbox.Writer.TryWrite(Envelope.Create(PipeProtocol.Snapshot, latest));

            host.SnapshotProduced += OnSnapshot;
            host.SettingsChanged += OnSettings;

            var sender = Task.Run(async () =>
            {
                await foreach (var envelope in outbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    await connection.SendAsync(envelope, token).ConfigureAwait(false);
            }, token);

            while (!token.IsCancellationRequested)
            {
                var request = await connection.ReceiveAsync(token).ConfigureAwait(false);
                if (request is null)
                    break;
                _ = Task.Run(() => HandleRequestAsync(connection, request, token), token);
            }

            clientCts.Cancel();
            await sender.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            // Client went away or sent something unreadable; drop the connection.
        }
        finally
        {
            host.SnapshotProduced -= OnSnapshot;
            host.SettingsChanged -= OnSettings;
            outbox.Writer.TryComplete();
            clientCts.Cancel();
            await connection.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _clientCount);
        }
    }

    private async Task HandleRequestAsync(MessageConnection connection, Envelope request, CancellationToken token)
    {
        Envelope reply;
        try
        {
            reply = request.Type switch
            {
                PipeProtocol.UpdateSettings => Envelope.Create(PipeProtocol.Reply,
                    await host.UpdateSettingsAsync(request.Read<MonitorSettings>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.QueryHistory => Envelope.Create(PipeProtocol.Reply,
                    await host.QueryHistoryAsync(request.Read<HistoryQuery>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.ListEntities => Envelope.Create(PipeProtocol.Reply,
                    await host.ListHistoryEntitiesAsync(request.Read<EntityKind>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.QuerySlowQueries => Envelope.Create(PipeProtocol.Reply,
                    await host.QuerySlowQueriesAsync(request.Read<SlowQueryRequest>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.EnableIisEtwLogging => Envelope.Create(PipeProtocol.Reply,
                    await host.EnableIisEtwLoggingAsync(token).ConfigureAwait(false), request.Id),
                PipeProtocol.QueryRequestLog => Envelope.Create(PipeProtocol.Reply,
                    await host.QueryRequestLogAsync(request.Read<RequestLogQuery>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.ListBlockedIps => Envelope.Create(PipeProtocol.Reply,
                    await host.ListBlockedIpsAsync(token).ConfigureAwait(false), request.Id),
                PipeProtocol.BlockIp => Envelope.Create(PipeProtocol.Reply,
                    await host.BlockIpAsync(request.Read<IpBlockRequest>(), token).ConfigureAwait(false), request.Id),
                PipeProtocol.UnblockIp => Envelope.Create(PipeProtocol.Reply,
                    await host.UnblockIpAsync(request.Read<IpBlockRequest>(), token).ConfigureAwait(false), request.Id),
                _ => Envelope.Create(PipeProtocol.Error, new ErrorPayload { Message = $"Unknown request '{request.Type}'." }, request.Id),
            };
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            log?.Invoke($"Request '{request.Type}' failed.", e);
            reply = Envelope.Create(PipeProtocol.Error, new ErrorPayload { Message = e.Message }, request.Id);
        }

        try
        {
            await connection.SendAsync(reply, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
