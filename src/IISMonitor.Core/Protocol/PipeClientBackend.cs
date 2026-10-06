using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Settings;

namespace IISMonitor.Core.Protocol;

/// <summary>Dashboard side of the named pipe: connects to the collector service.</summary>
public sealed class PipeClientBackend : IMonitorBackend
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly MessageConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private long _nextId;
    private int _disconnected;
    private Task? _readLoop;

    private PipeClientBackend(MessageConnection connection, HelloPayload hello)
    {
        _connection = connection;
        Settings = hello.Settings;
        MachineName = hello.MachineName;
    }

    public string Description => "Windows service";

    public bool IsStandalone => false;

    public string MachineName { get; }

    public MonitorSettings Settings { get; private set; }

    public event EventHandler<MonitorSnapshot>? SnapshotReceived;

    public event EventHandler<MonitorSettings>? SettingsChanged;

    public event EventHandler? Disconnected;

    /// <summary>Connects to the service, or returns null if it isn't reachable within the timeout.</summary>
    public static async Task<PipeClientBackend?> TryConnectAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default, string pipeName = PipeProtocol.PipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connection = new MessageConnection(pipe);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            var hello = await connection.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            if (hello is null || hello.Type != PipeProtocol.Hello)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            var payload = hello.Read<HelloPayload>();
            if (payload.ProtocolVersion != PipeProtocol.Version)
            {
                throw new InvalidOperationException(
                    $"The IISMonitor service speaks protocol version {payload.ProtocolVersion}, this dashboard needs {PipeProtocol.Version}. Install matching versions.");
            }

            // Reading starts in Start(), once the caller has subscribed; until then messages wait in the pipe.
            return new PipeClientBackend(connection, payload);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested
                                  && e is OperationCanceledException or TimeoutException or IOException)
        {
            // Service not running or not answering.
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Start()
    {
        lock (_startGate)
            _readLoop ??= Task.Run(ReadLoopAsync);
    }

    public async Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync<MonitorSettings>(PipeProtocol.UpdateSettings, settings, cancellationToken).ConfigureAwait(false);
        Settings = result;
        return result;
    }

    public Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default) =>
        RequestAsync<HistoryResult>(PipeProtocol.QueryHistory, query, cancellationToken);

    public Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken = default) =>
        RequestAsync<List<string>>(PipeProtocol.ListEntities, kind, cancellationToken);

    public Task<SlowQueryReport> QuerySlowQueriesAsync(SlowQueryRequest request, CancellationToken cancellationToken = default) =>
        RequestAsync<SlowQueryReport>(PipeProtocol.QuerySlowQueries, request, cancellationToken);

    public Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<CommandResult>(PipeProtocol.EnableIisEtwLogging, new { }, cancellationToken);

    private async Task<T> RequestAsync<T>(string type, object payload, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disconnected) != 0)
            throw new IOException("Not connected to the IISMonitor service.");

        // Replies arrive through the read loop.
        Start();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _connection.SendAsync(Envelope.Create(type, payload, id), cancellationToken).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            return result is { } element
                ? element.Deserialize<T>(PipeProtocol.Json) ?? throw new InvalidDataException($"Empty reply to '{type}'.")
                : throw new InvalidDataException($"Empty reply to '{type}'.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var envelope = await _connection.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (envelope is null)
                    break;

                switch (envelope.Type)
                {
                    case PipeProtocol.Snapshot:
                        SnapshotReceived?.Invoke(this, envelope.Read<MonitorSnapshot>());
                        break;
                    case PipeProtocol.Settings:
                        Settings = envelope.Read<MonitorSettings>();
                        SettingsChanged?.Invoke(this, Settings);
                        break;
                    case PipeProtocol.Reply:
                        if (_pending.TryGetValue(envelope.Id, out var reply))
                            reply.TrySetResult(envelope.Payload);
                        break;
                    case PipeProtocol.Error:
                        if (_pending.TryGetValue(envelope.Id, out var failed))
                        {
                            var message = envelope.Payload is { } p ? p.Deserialize<ErrorPayload>(PipeProtocol.Json)?.Message : null;
                            failed.TrySetException(new InvalidOperationException(message ?? "The service reported an error."));
                        }

                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
        }
        finally
        {
            MarkDisconnected();
        }
    }

    private void MarkDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            return;

        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("The connection to the IISMonitor service was lost."));
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disconnected, 1);
        _cts.Cancel();
        await _connection.DisposeAsync().ConfigureAwait(false);
        Task? readLoop;
        lock (_startGate)
            readLoop = _readLoop;
        if (readLoop is not null)
            await readLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }
}
