using System.Diagnostics;
using IISMonitor.Collectors.Database;
using IISMonitor.Collectors.Etw;
using IISMonitor.Collectors.Iis;
using IISMonitor.Collectors.Processes;
using IISMonitor.Core;
using IISMonitor.Core.Collection;
using IISMonitor.Core.History;
using IISMonitor.Core.Metrics;
using IISMonitor.Core.Models;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.RequestLog;
using IISMonitor.Core.Settings;

namespace IISMonitor.Collectors;

public sealed class MonitorEngineOptions
{
    public string DataDirectory { get; init; } = DataPaths.DataDirectory;

    /// <summary>Prefix for the ETW session names, so the service and a standalone dashboard don't stop each other's sessions.</summary>
    public string EtwSessionPrefix { get; init; } = "IISMonitor";

    public Action<string, Exception?>? Log { get; init; }
}

/// <summary>
/// Drives all collectors on a timer, composes snapshots, records history and serves the
/// <see cref="IMonitorHost"/> contract (used by the Windows service and by standalone mode).
/// </summary>
public sealed class MonitorEngine : IMonitorHost, IAsyncDisposable
{
    private static readonly TimeSpan CollectorRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly MonitorEngineOptions _options;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _history;
    private readonly SnapshotComposer _composer = new(Environment.MachineName, Environment.ProcessorCount);
    private readonly IisConfigReader _config = new();
    private readonly IisCounterReader _counters = new();
    private readonly WorkerProcessResolver _workers = new();
    private readonly ProcessSampler _sampler = new();
    private readonly ResponseTimeAggregator _responses = new();
    private readonly HistoryAggregator _historyAggregator = new();
    private readonly RequestLogRecorder _requestLog;
    private readonly CancellationTokenSource _cts = new();
    // Serializes the commands that change applicationHost.config, so one doesn't overwrite another's change.
    private readonly object _iisConfigWriteGate = new();
    private readonly bool _elevated;

    private MonitorSettings _settings;
    private MonitorSnapshot? _latest;
    private KernelIoMonitor? _kernel;
    private IisLogMonitor? _iisLog;
    private SqlActivitySampler? _sql;
    private string? _kernelError;
    private string? _iisLogError;
    private DateTime _kernelAttemptUtc;
    private DateTime _iisLogAttemptUtc;
    private string? _historyError;
    private DateTime _lastPurgeUtc = DateTime.MinValue;
    private IisTopology? _topology;
    private PeriodicTimer? _timer;
    private Task? _loop;
    private string? _blockedListError;

    public MonitorEngine(MonitorEngineOptions? options = null)
    {
        _options = options ?? new MonitorEngineOptions();
        _settingsStore = new SettingsStore(Path.Combine(_options.DataDirectory, "settings.json"));
        _history = new HistoryStore(Path.Combine(_options.DataDirectory, "history.db"));
        _requestLog = new RequestLogRecorder(new RequestLogStore(Path.Combine(_options.DataDirectory, "requests.db")), Log);
        _settings = _settingsStore.Load();
        _elevated = SecureDataDirectory.IsElevated();
    }

    public MonitorSettings Settings => Volatile.Read(ref _settings);

    public MonitorSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    public event EventHandler<MonitorSnapshot>? SnapshotProduced;

    public event EventHandler<MonitorSettings>? SettingsChanged;

    public void Start()
    {
        try
        {
            SecureDataDirectory.Ensure(_options.DataDirectory);
        }
        catch (Exception e)
        {
            Log($"Could not create {_options.DataDirectory}.", e);
        }

        try
        {
            // Lets OpenProcess read worker processes that run under other identities.
            Process.EnterDebugMode();
        }
        catch (Exception e)
        {
            Log("Could not enable SeDebugPrivilege; some processes may be unreadable.", e);
        }

        try
        {
            _history.Initialize();
        }
        catch (Exception e)
        {
            _historyError = e.Message;
            Log("Could not open the history database.", e);
        }

        // Opens requests.db (a failure shows on the "IP and URL tracking" health row) and starts its write loop.
        _requestLog.RetentionDays = _settings.RequestLogRetentionDays;
        _requestLog.Start();

        lock (_gate)
            ApplyCollectors(_settings, previous: null);

        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.SampleIntervalMs));
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            do
            {
                MonitorSnapshot? snapshot = null;
                lock (_gate)
                {
                    try
                    {
                        snapshot = Tick();
                    }
                    catch (Exception e)
                    {
                        Log("A collection tick failed.", e);
                    }
                }

                if (snapshot is not null)
                {
                    Volatile.Write(ref _latest, snapshot);
                    Raise(SnapshotProduced, snapshot);
                }
            }
            while (await _timer!.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private MonitorSnapshot Tick()
    {
        var now = DateTime.UtcNow;
        var settings = _settings;
        var health = new List<CollectorHealth>();

        if (!_elevated)
            health.Add(Fail("Permissions", "Not running as administrator: process, ETW and IIS data will be incomplete."));

        // IIS sites, applications and app pools.
        try
        {
            var topology = _config.Read();
            if (!ReferenceEquals(topology, _topology))
            {
                _topology = topology;
                _iisLog?.UpdateSites(topology.Sites);
            }

            health.Add(Ok("IIS configuration", $"{topology.Sites.Count} sites, {topology.AppPools.Count} app pools."));
        }
        catch (Exception e)
        {
            _topology = null;
            health.Add(Fail("IIS configuration", e.Message));
        }

        var topologyNow = _topology;

        List<ProcessEntry> processes = [];
        try
        {
            processes = ProcessTableReader.Read();
        }
        catch (Exception e)
        {
            health.Add(Fail("Processes", e.Message));
        }

        var counterProblems = new List<string>();
        IisCounterValues? counters = _counters.Read(counterProblems);
        if (counterProblems.Count >= 3)
            counters = null;
        health.Add(counterProblems.Count == 0
            ? Ok("IIS performance counters", "Reading Web Service, W3SVC_W3WP and HTTP request queue counters.")
            : Fail("IIS performance counters", string.Join(" ", counterProblems)));

        var poolNames = topologyNow?.AppPools.Select(p => p.Name).ToList() ?? [];
        var workerPools = _workers.Resolve(processes, counters, poolNames);
        var members = ProcessTree.ResolvePoolMembers(processes, workerPools);
        // The local SQL Server process is sampled too, for SQL Server CPU on the Server and Database views.
        var sqlServerPids = processes
            .Where(p => p.Name.Equals("sqlservr.exe", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToList();
        var samples = _sampler.Sample(members.Keys.Concat(sqlServerPids));
        var unreadable = members.Count - samples.Count;
        health.Add(unreadable == 0
            ? Ok("Processes", $"{workerPools.Count} worker processes; {members.Count} processes in app pools.")
            : Fail("Processes", $"{unreadable} of {members.Count} app pool processes could not be read (access denied or exited)."));

        // Disk and network bytes from kernel ETW.
        RetryCollectorsIfNeeded(settings, now);
        IReadOnlyDictionary<int, IoTotals>? etwIo = null;
        if (_kernel is { Running: true } kernel)
        {
            etwIo = kernel.Read(members.Keys, processes.Select(p => p.Pid).ToHashSet());
            health.Add(Ok("Disk & network (kernel ETW)", kernel.LostEvents > 0
                ? $"Tracing file and TCP/UDP I/O per process ({kernel.LostEvents:N0} events lost under load)."
                : "Tracing file and TCP/UDP I/O per process."));
        }
        else
        {
            const string fallback = " Disk falls back to process I/O counters; pool network shows website traffic only (outbound traffic unavailable).";
            health.Add(settings.EnableKernelTracing
                ? Fail("Disk & network (kernel ETW)", (_kernel?.Error ?? _kernelError ?? "Not running.") + fallback)
                : Ok("Disk & network (kernel ETW)", "Off in settings." + fallback));
        }

        // SQL Server connections.
        Dictionary<int, int>? dbConnections = null;
        try
        {
            var tcp = TcpTableReader.ReadAll();
            _kernel?.SetExcludedConnections(LoopbackPairs.WithinPools(tcp, members));
            dbConnections = DbConnectionCounter.Count(tcp, members.Keys, processes, settings.SqlServerPorts, settings.DetectLocalSqlServerPorts, out var sqlPorts);
            health.Add(Ok("SQL Server connections (TCP)", sqlPorts.Count == 0
                ? "No SQL Server ports configured."
                : $"Counting established connections to port(s) {string.Join(", ", sqlPorts.Order())}."));
        }
        catch (Exception e)
        {
            health.Add(Fail("SQL Server connections (TCP)", e.Message));
        }

        DbActivityInput? dbActivity = null;
        if (_sql is { } sql)
        {
            dbActivity = sql.Drain();
            var (ok, message) = sql.Status;
            health.Add(ok ? Ok("SQL Server activity", message) : Fail("SQL Server activity", message));
        }
        else
        {
            health.Add(Ok("SQL Server activity", "Not set up. Add a SQL Server connection string in Settings to see database load and slow queries per app pool."));
        }

        // Response times from the IIS ETW log stream.
        IReadOnlyDictionary<long, ResponseStats>? responseBySite = null;
        IReadOnlyDictionary<string, ResponseStats>? responseByPool = null;
        if (_iisLog is { Running: true } iisLog)
        {
            (responseBySite, responseByPool) = _responses.DrainLive();
            var traced = topologyNow?.Sites.Count(s => s.EtwLoggingEnabled) ?? 0;
            var total = topologyNow?.Sites.Count ?? 0;
            health.Add(traced == 0
                ? Fail("Response times (IIS ETW log)", "No site sends its log to ETW yet. Use \"Enable response times\" to turn it on.")
                : Ok("Response times (IIS ETW log)", $"{traced} of {total} sites traced; {iisLog.EventCount:N0} requests seen."));
        }
        else
        {
            health.Add(settings.EnableResponseTimeTracing
                ? Fail("Response times (IIS ETW log)", _iisLog?.Error ?? _iisLogError ?? "Not running.")
                : Ok("Response times (IIS ETW log)", "Off in settings."));
        }

        // Client IPs and URLs of the tracked app pools, recorded from the same log stream.
        var tracking = settings.RequestTrackingPools.Count > 0;
        health.Add(RequestTrackingStatus.Health(
            settings,
            topologyNow,
            _iisLog is { Running: true },
            _requestLog.Error,
            _requestLog.Aggregator.RecordedRequests,
            _requestLog.Aggregator.DroppedRequests,
            tracking ? _requestLog.Store.SizeBytes() : 0));

        SystemSample? system = null;
        try
        {
            system = SystemSampler.Read();
        }
        catch (Exception e)
        {
            health.Add(Fail("Server CPU & memory", e.Message));
        }

        var snapshot = _composer.Compose(new CollectionInput
        {
            TimestampUtc = now,
            Topology = topologyNow,
            Processes = processes,
            WorkerProcessPools = workerPools,
            ProcessSamples = samples,
            EtwIo = etwIo,
            Counters = counters,
            DbConnectionsByPid = dbConnections,
            DbActivity = dbActivity,
            SqlServerPids = sqlServerPids,
            ResponseBySite = responseBySite,
            ResponseByPool = responseByPool,
            System = system,
            Health = health,
        });

        RecordHistory(snapshot, settings, now, topologyNow);
        health.Add(_historyError is null
            ? Ok("History", $"Keeping {settings.HistoryRetentionDays} days at {settings.HistoryIntervalSeconds}-second resolution.")
            : Fail("History", _historyError));

        return snapshot;
    }

    private void RecordHistory(MonitorSnapshot snapshot, MonitorSettings settings, DateTime now, IisTopology? topology)
    {
        _historyAggregator.Add(snapshot);
        if (snapshot.Database is { CompletedSlow.Count: > 0 } database)
        {
            try
            {
                _history.WriteSlowQueries(database.CompletedSlow);
            }
            catch (Exception e)
            {
                _historyError = "Writing slow queries failed: " + e.Message;
                Log("Writing slow queries failed.", e);
            }
        }

        if (_historyAggregator.WindowStartUtc is { } start && now - start >= TimeSpan.FromSeconds(settings.HistoryIntervalSeconds) - TimeSpan.FromMilliseconds(50))
            FlushHistory(now, topology);

        if (now - _lastPurgeUtc >= PurgeInterval)
        {
            _lastPurgeUtc = now;
            try
            {
                _history.Purge(now.AddDays(-settings.HistoryRetentionDays));
            }
            catch (Exception e)
            {
                Log("Purging old history failed.", e);
            }
        }
    }

    private void FlushHistory(DateTime now, IisTopology? topology)
    {
        Dictionary<string, ResponseStats>? bySite = null;
        Dictionary<string, ResponseStats>? byPool = null;
        HashSet<string>? loggedSites = null;
        HashSet<string>? loggedPools = null;

        if (_iisLog is { Running: true })
        {
            var (siteStats, poolStats) = _responses.DrainHistory();
            var traced = topology?.Sites.Where(s => s.EtwLoggingEnabled).ToList() ?? [];
            var names = topology?.Sites.ToDictionary(s => s.Id, s => s.Name) ?? [];
            bySite = siteStats
                .Where(kv => names.ContainsKey(kv.Key))
                .ToDictionary(kv => names[kv.Key], kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            byPool = poolStats;
            loggedSites = traced.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            loggedPools = traced.SelectMany(s => s.Applications.Select(a => a.AppPool)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var rows = _historyAggregator.Flush(now, bySite, byPool, loggedSites, loggedPools);
        try
        {
            _history.Write(rows);
            _historyError = null;
        }
        catch (Exception e)
        {
            _historyError = "Writing history failed: " + e.Message;
            Log("Writing history failed.", e);
        }
    }

    private void ApplyCollectors(MonitorSettings settings, MonitorSettings? previous)
    {
        if (settings.EnableKernelTracing && _kernel is null)
            StartKernel();
        else if (!settings.EnableKernelTracing && _kernel is not null)
            StopKernel();

        if (settings.EnableResponseTimeTracing && _iisLog is null)
            StartIisLog();
        else if (!settings.EnableResponseTimeTracing && _iisLog is not null)
            StopIisLog();

        var sqlChanged = previous is null
            || !previous.SqlServerConnectionStrings.SequenceEqual(settings.SqlServerConnectionStrings)
            || previous.SqlActivityIntervalSeconds != settings.SqlActivityIntervalSeconds
            || !previous.SlowQueryThresholdSeconds.Equals(settings.SlowQueryThresholdSeconds);
        if (sqlChanged)
        {
            if (_sql is { } old)
            {
                _sql = null;
                _ = old.DisposeAsync().AsTask();
            }

            if (settings.SqlServerConnectionStrings.Count > 0)
                _sql = new SqlActivitySampler(
                    settings.SqlServerConnectionStrings,
                    TimeSpan.FromSeconds(settings.SqlActivityIntervalSeconds),
                    settings.SlowQueryThresholdSeconds * 1000);
        }

        _iisLog?.UpdateTrackedPools(settings.RequestTrackingPools);
        _requestLog.RetentionDays = settings.RequestLogRetentionDays;
    }

    /// <summary>Restarts an ETW session that failed to start or stopped, at most once a minute.</summary>
    private void RetryCollectorsIfNeeded(MonitorSettings settings, DateTime now)
    {
        if (settings.EnableKernelTracing && _kernel is not { Running: true } && now - _kernelAttemptUtc >= CollectorRetryInterval)
        {
            StopKernel();
            StartKernel();
        }

        if (settings.EnableResponseTimeTracing && _iisLog is not { Running: true } && now - _iisLogAttemptUtc >= CollectorRetryInterval)
        {
            StopIisLog();
            StartIisLog();
        }
    }

    private void StartKernel()
    {
        _kernelAttemptUtc = DateTime.UtcNow;
        var monitor = new KernelIoMonitor(_options.EtwSessionPrefix + "-KernelIO");
        try
        {
            monitor.Start();
            _kernel = monitor;
            _kernelError = null;
        }
        catch (Exception e)
        {
            monitor.Dispose();
            var error = "Could not start kernel tracing: " + e.Message;
            if (error != _kernelError)
                Log(error, e);
            _kernelError = error;
        }
    }

    private void StopKernel()
    {
        _kernel?.Dispose();
        _kernel = null;
    }

    private void StartIisLog()
    {
        _iisLogAttemptUtc = DateTime.UtcNow;
        var monitor = new IisLogMonitor(_options.EtwSessionPrefix + "-IISLog", _responses, _requestLog.Aggregator);
        try
        {
            if (_topology is { } topology)
                monitor.UpdateSites(topology.Sites);
            monitor.UpdateTrackedPools(_settings.RequestTrackingPools);
            monitor.Start();
            _iisLog = monitor;
            _iisLogError = null;
        }
        catch (Exception e)
        {
            monitor.Dispose();
            var error = "Could not start IIS log tracing: " + e.Message;
            if (error != _iisLogError)
                Log(error, e);
            _iisLogError = error;
        }
    }

    private void StopIisLog()
    {
        _iisLog?.Dispose();
        _iisLog = null;
    }

    public Task<MonitorSettings> UpdateSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken)
    {
        MonitorSettings normalized;
        lock (_gate)
        {
            normalized = settings.Normalize();
            _settingsStore.Save(normalized);
            var previous = _settings;
            Volatile.Write(ref _settings, normalized);
            ApplyCollectors(normalized, previous);
            if (_timer is not null)
                _timer.Period = TimeSpan.FromMilliseconds(normalized.SampleIntervalMs);
        }

        Raise(SettingsChanged, normalized);
        return Task.FromResult(normalized);
    }

    public Task<HistoryResult> QueryHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        Task.Run(() => _history.Query(query), cancellationToken);

    public Task<SlowQueryReport> QuerySlowQueriesAsync(SlowQueryRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => _history.QuerySlowQueries(request), cancellationToken);

    public Task<List<string>> ListHistoryEntitiesAsync(EntityKind kind, CancellationToken cancellationToken) =>
        Task.Run(() => _history.ListEntities(kind, DateTime.UtcNow.AddDays(-Settings.HistoryRetentionDays)), cancellationToken);

    public Task<CommandResult> EnableIisEtwLoggingAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                string message;
                lock (_iisConfigWriteGate)
                    message = IisLoggingConfig.EnableEtwLogging();
                lock (_gate)
                {
                    try
                    {
                        _topology = _config.Read(force: true);
                        _iisLog?.UpdateSites(_topology.Sites);
                    }
                    catch (Exception e)
                    {
                        Log("Re-reading IIS configuration failed.", e);
                    }
                }

                return CommandResult.Ok(message);
            }
            catch (Exception e)
            {
                Log("Enabling IIS ETW logging failed.", e);
                return CommandResult.Fail(e.Message);
            }
        }, cancellationToken);

    public async Task<RequestLogReport> QueryRequestLogAsync(RequestLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Writes what is still pending first, so the report is up to the second.
        var report = await _requestLog.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        IisTopology? topology;
        bool iisLogRunning;
        lock (_gate)
        {
            topology = _topology;
            iisLogRunning = _iisLog is { Running: true };
        }

        RequestTrackingStatus.AddWarnings(
            report, query.AppPool, Settings, topology, iisLogRunning, _requestLog.Error, _requestLog.Aggregator.DroppedRequests);
        report.DatabaseBytes = _requestLog.Store.SizeBytes();
        return report;
    }

    public Task<List<BlockedIp>> ListBlockedIpsAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                var blocked = IpRestrictionConfig.ListBlocked();
                Volatile.Write(ref _blockedListError, null);
                return blocked;
            }
            catch (Exception e)
            {
                // The tab asks every time it refreshes; log each distinct failure once.
                var message = IpRestrictionConfig.ErrorMessage(e);
                if (Interlocked.Exchange(ref _blockedListError, message) != message)
                    Log("Reading blocked IP addresses from IIS failed.", e);
                throw new InvalidOperationException(message, e);
            }
        }, cancellationToken);

    public Task<CommandResult> BlockIpAsync(IpBlockRequest request, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (!IpAddressRules.TryNormalizeForBlocking(request?.IpAddress, out var address, out var error))
                return CommandResult.Fail(error);

            try
            {
                List<string> locations = [""];
                var pool = request!.AppPool?.Trim();
                if (!string.IsNullOrEmpty(pool))
                {
                    locations = IpAddressRules.LocationsForPool(CurrentTopology(), pool);
                    if (locations.Count == 0)
                        return CommandResult.Fail($"App pool {pool} runs no sites or applications in IIS, so there is nothing to block {address} on.");
                }

                string message;
                lock (_iisConfigWriteGate)
                    message = IpRestrictionConfig.Block(address, locations);
                Log(message);
                return CommandResult.Ok(message);
            }
            catch (Exception e)
            {
                Log($"Blocking {address} failed.", e);
                return CommandResult.Fail(IpRestrictionConfig.ErrorMessage(e));
            }
        }, cancellationToken);

    public Task<CommandResult> UnblockIpAsync(IpBlockRequest request, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // Any deny entry can be removed, also one this app wouldn't add (say, a loopback address added by hand).
            var address = request?.IpAddress?.Trim() ?? "";
            if (!IpRestrictionConfig.TryParseAddress(address, out _))
                return CommandResult.Fail(address.Length == 0 ? "Enter an IP address." : $"'{address}' is not an IP address.");

            try
            {
                string message;
                lock (_iisConfigWriteGate)
                    message = IpRestrictionConfig.Unblock(address, request!.Location ?? "");
                Log(message);
                return CommandResult.Ok(message);
            }
            catch (Exception e)
            {
                Log($"Unblocking {address} failed.", e);
                return CommandResult.Fail(IpRestrictionConfig.ErrorMessage(e));
            }
        }, cancellationToken);

    /// <summary>The IIS topology of the last tick, or read now when there is none (without holding the collection lock while reading).</summary>
    private IisTopology CurrentTopology()
    {
        lock (_gate)
        {
            if (_topology is { } topology)
                return topology;
        }

        return new IisConfigReader().Read();
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                subscriber(this, value);
            }
            catch (Exception e)
            {
                Log("A subscriber failed.", e);
            }
        }
    }

    private void Log(string message, Exception? exception = null) => _options.Log?.Invoke(message, exception);

    private static CollectorHealth Ok(string name, string message) => new() { Name = name, Ok = true, Message = message };

    private static CollectorHealth Fail(string name, string message) => new() { Name = name, Ok = false, Message = message };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _timer?.Dispose();

        lock (_gate)
        {
            FlushHistory(DateTime.UtcNow, _topology);
            StopKernel();
            StopIisLog();
            _sampler.Dispose();
        }

        // After the IIS log session has stopped, so the final write includes its last requests.
        await _requestLog.DisposeAsync().ConfigureAwait(false);

        if (_sql is { } sql)
            await sql.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
