using IISMonitor.Core.Settings;

namespace IISMonitor.Core.RequestLog;

/// <summary>
/// Owns the aggregator and the store: writes pending rows once a minute and before every query,
/// and deletes rows older than the retention once an hour, all off the collection thread.
/// </summary>
public sealed class RequestLogRecorder : IAsyncDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly Action<string, Exception?>? _log;

    // Serializes everything that writes (flush, purge chunk, initialize). Queries only hold it for their flush.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startGate = new();
    private Task? _loop;
    private int _disposed;
    private int _retentionDays = new MonitorSettings().RequestLogRetentionDays;
    private string? _error;
    private long _rowsWritten;

    // Only touched while holding _writeLock.
    private bool _initialized;
    private long _lostRequests;
    private string? _lastLoggedFailure;
    private Operation _failedOperation;

    public RequestLogRecorder(RequestLogStore store, Action<string, Exception?>? log = null)
    {
        Store = store;
        _log = log;
    }

    public RequestLogAggregator Aggregator { get; } = new();

    public RequestLogStore Store { get; }

    /// <summary>Retention used by the next purge.</summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set => Volatile.Write(ref _retentionDays, Math.Clamp(value, 1, MonitorSettings.MaxRequestLogRetentionDays));
    }

    /// <summary>Last initialize/write/purge error, or null.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>Rows written since start.</summary>
    public long RowsWritten => Interlocked.Read(ref _rowsWritten);

    /// <summary>Opens the database and starts the background loop (first purge right away).</summary>
    public void Start()
    {
        lock (_startGate)
        {
            if (_loop is not null || Volatile.Read(ref _disposed) != 0)
                return;

            _writeLock.Wait();
            try
            {
                EnsureInitialized();
            }
            finally
            {
                _writeLock.Release();
            }

            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Writes pending rows now.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WritePending();
            }
            finally
            {
                _writeLock.Release();
            }
        }, cancellationToken);

    /// <summary>Writes pending rows, then queries.</summary>
    public Task<RequestLogReport> QueryAsync(RequestLogQuery query, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            bool ready;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // One attempt to open the database per call; pending rows wait for the next flush if it fails.
                ready = EnsureInitialized();
                if (ready)
                    WritePending();
            }
            finally
            {
                _writeLock.Release();
            }

            if (!ready)
                throw new InvalidOperationException(Error ?? "The request database is not available.");

            // WAL lets this read while a later flush writes.
            cancellationToken.ThrowIfCancellationRequested();
            return Store.Query(query);
        }, cancellationToken);

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        var nextPurge = Environment.TickCount64;
        var purge = Task.CompletedTask;
        try
        {
            do
            {
                await _writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    WritePending();
                }
                catch (Exception e)
                {
                    // WritePending reports its own failures; this only keeps the loop alive.
                    Log("Recording requests failed.", e);
                }
                finally
                {
                    _writeLock.Release();
                }

                // The purge runs beside the flushes: a large one (say, after the retention was shortened)
                // must not hold up flushes and queries until it is done.
                if (purge.IsCompleted && Environment.TickCount64 >= nextPurge)
                {
                    nextPurge = Environment.TickCount64 + (long)PurgeInterval.TotalMilliseconds;
                    purge = Task.Run(() => PurgeAsync(token));
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log("Recording requests stopped.", e);
        }
        finally
        {
            // A running purge stops after its current chunk.
            await purge.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Caller holds <see cref="_writeLock"/>. Opens the database if that hasn't worked yet.</summary>
    private bool EnsureInitialized()
    {
        if (TryInitialize() is not { } failure)
            return true;

        ReportFailure(Operation.Open, $"Could not open {Store.DatabasePath}: {WithPeriod(failure.Message)}", failure);
        return false;
    }

    /// <summary>Caller holds <see cref="_writeLock"/>. Returns the failure, or null when the database is ready.</summary>
    private Exception? TryInitialize()
    {
        if (_initialized)
            return null;

        try
        {
            Store.Initialize();
            _initialized = true;
            ClearError(Operation.Open);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>Caller holds <see cref="_writeLock"/>. A batch that can't be written is dropped and counted.</summary>
    private void WritePending()
    {
        var entries = Aggregator.Drain();
        if (entries.Count == 0)
            return;

        Operation operation;
        string message;
        var failure = TryInitialize();
        if (failure is not null)
        {
            operation = Operation.Open;
            message = $"Could not open {Store.DatabasePath}: {WithPeriod(failure.Message)}";
        }
        else
        {
            try
            {
                Store.Write(entries);
                Interlocked.Add(ref _rowsWritten, entries.Count);
                ClearError(Operation.Write);
                return;
            }
            catch (Exception e)
            {
                // Check the schema again next time, in case the database was replaced or damaged.
                _initialized = false;
                operation = Operation.Write;
                failure = e;
                message = $"Writing recorded requests to {Store.DatabasePath} failed: {WithPeriod(e.Message)}";
            }
        }

        foreach (var entry in entries)
            _lostRequests += entry.Hits;
        ReportFailure(operation, message, failure);
    }

    /// <summary>Deletes rows older than the retention, holding the write lock for one chunk at a time.</summary>
    private async Task PurgeAsync(CancellationToken token)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        while (true)
        {
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!EnsureInitialized())
                    return;
                if (Store.PurgeChunk(cutoff) == 0)
                {
                    ClearError(Operation.Purge);
                    return;
                }
            }
            catch (Exception e)
            {
                ReportFailure(Operation.Purge, $"Deleting old recorded requests from {Store.DatabasePath} failed: {WithPeriod(e.Message)}", e);
                return;
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    /// <summary>Caller holds <see cref="_writeLock"/>.</summary>
    private void ReportFailure(Operation operation, string message, Exception e)
    {
        if (_lostRequests > 0)
            message += $" Requests lost so far: {_lostRequests:N0}.";
        _failedOperation = operation;
        Volatile.Write(ref _error, message);

        // Log each distinct failure once, not once a minute while it lasts.
        var failure = e.GetType().FullName + ": " + e.Message;
        if (failure == _lastLoggedFailure)
            return;
        _lastLoggedFailure = failure;
        Log(message, e);
    }

    private void Log(string message, Exception? exception)
    {
        try
        {
            _log?.Invoke(message, exception);
        }
        catch
        {
            // A failing logger must not stop recording or fail a query.
        }
    }

    /// <summary>Caller holds <see cref="_writeLock"/>. A success only clears an error of the same kind.</summary>
    private void ClearError(Operation operation)
    {
        if (_error is null || _failedOperation != operation)
            return;
        Volatile.Write(ref _error, null);
        _lastLoggedFailure = null;
    }

    private static string WithPeriod(string text)
    {
        text = text.Trim();
        return text.Length == 0 || text[^1] is '.' or '!' or '?' ? text : text + ".";
    }

    /// <summary>Stops the loop and writes what is still pending.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        Task? loop;
        lock (_startGate)
            loop = _loop;
        if (loop is not null)
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private enum Operation
    {
        Open,
        Write,
        Purge,
    }
}
