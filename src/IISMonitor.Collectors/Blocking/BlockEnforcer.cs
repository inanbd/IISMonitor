using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using IISMonitor.Core.Blocking;
using IISMonitor.Core.Protocol;
using IISMonitor.Core.Requests;

namespace IISMonitor.Collectors.Blocking;

/// <summary>
/// Applies the blacklist: records each block in the <see cref="BlockStore"/>, blocks direct
/// visitors with a Windows Firewall rule and proxied visitors with an IIS IP restriction, lifts
/// blocks when they expire or are lifted by hand, and retries what failed. One background loop
/// does all of it in order, so the firewall and IIS are never changed from two threads; IIS
/// changes are batched because each one rewrites applicationHost.config. Without
/// <c>enforce</c> (standalone dashboard) it only shows the stored blocks.
/// </summary>
internal sealed class BlockEnforcer : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SiteCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AddressRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly BlockStore _store;
    private readonly BlacklistEngine _engine;
    private readonly Func<string, bool?> _seenViaProxy;
    private readonly bool _enforce;
    private readonly Action<string, Exception?> _log;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly List<BlockEntry> _current = [];
    private readonly HashSet<string> _iisToAdd = new(StringComparer.Ordinal);
    private readonly HashSet<string> _iisToRemove = new(StringComparer.Ordinal);
    private readonly FirewallBlocker? _firewall;
    private readonly IisIpBlocker? _iis;
    private BlockingSettings _settings = new BlockingSettings().Normalize();
    private IPAddress[] _serverAddresses = [];
    private DateTime _addressesReadUtc = DateTime.MinValue;
    private DateTime _statusReadUtc = DateTime.MinValue;
    private DateTime _sitesCheckedUtc = DateTime.MinValue;
    private DateTime _lastRetryUtc = DateTime.MinValue;
    private DateTime _lastIisApplyUtc = DateTime.MinValue;
    private bool _proxyModeEnsured;
    private string? _firewallProblem;
    private string? _iisProblem;
    private string? _iisSiteProblems;
    private bool _iisAvailable = true;
    private string? _lastError;
    private volatile BlockingStatus _status = new();
    private Task? _loop;

    public BlockEnforcer(BlockStore store, BlacklistEngine engine, Func<string, bool?> seenViaProxy, bool enforce, string dataDirectory,
        Action<string, Exception?> log)
    {
        _store = store;
        _engine = engine;
        _seenViaProxy = seenViaProxy;
        _enforce = enforce;
        _log = log;
        if (enforce)
        {
            _firewall = new FirewallBlocker(IsProtected);
            _iis = new IisIpBlocker(Path.Combine(dataDirectory, "iis-blocks.json"), ProtectedNetworks);
        }
    }

    public BlockingStatus Status => _status;

    public bool Enforcing => _enforce;

    public void Start(BlockingSettings settings)
    {
        _settings = settings;
        Configure(settings);
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>From the IIS log thread: never blocks.</summary>
    public void Enqueue(BlockDecision decision) => _commands.Writer.TryWrite(new DecisionCommand(decision));

    public void ApplySettings(BlockingSettings settings) => _commands.Writer.TryWrite(new SettingsCommand(settings));

    public Task<CommandResult> BlockAsync(ManualBlockRequest request)
    {
        var command = new ManualCommand(request);
        return Post(command, command.Done);
    }

    public Task<CommandResult> UnblockAsync(UnblockRequest request)
    {
        var command = new UnblockCommand(request.Ip);
        return Post(command, command.Done);
    }

    /// <summary>The block or watch entry for each IP that has one.</summary>
    public IReadOnlyDictionary<string, BlockEntry> CurrentByIp()
    {
        lock (_current)
            return _current.GroupBy(e => e.Ip).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public BlockList List(BlockListQuery query) => new() { Entries = _store.List(query), Status = _status };

    public int Purge(DateTime cutoffUtc) => _store.Purge(cutoffUtc);

    private Task<CommandResult> Post(Command command, TaskCompletionSource<CommandResult> done)
    {
        if (!_enforce)
            return Task.FromResult(CommandResult.Fail("Blocking runs in the IIS Monitor Windows service. Start the service to block or unblock addresses."));
        if (!_commands.Writer.TryWrite(command))
            return Task.FromResult(CommandResult.Fail("The blacklist is shutting down."));
        return done.Task;
    }

    private async Task LoopAsync()
    {
        try
        {
            Initialize();
        }
        catch (Exception e)
        {
            _lastError = "Could not load the block list: " + e.Message;
            _log(_lastError, e);
        }

        PublishStatus();
        var token = _cts.Token;
        using var timer = new PeriodicTimer(TickInterval);
        var tick = timer.WaitForNextTickAsync(token).AsTask();
        while (!token.IsCancellationRequested)
        {
            var read = _commands.Reader.WaitToReadAsync(token).AsTask();
            Task finished;
            try
            {
                finished = await Task.WhenAny(read, tick).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (finished == tick)
                {
                    if (!await tick.ConfigureAwait(false))
                        break;
                    tick = timer.WaitForNextTickAsync(token).AsTask();
                    Maintain(DateTime.UtcNow);
                }
                else if (!await read.ConfigureAwait(false))
                {
                    break;
                }

                ProcessCommands();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _log("The blacklist worker failed a step.", e);
            }

            PublishStatus();
        }
    }

    /// <summary>Loads current blocks, expires those that ran out while stopped, and makes the firewall and IIS match.</summary>
    private void Initialize()
    {
        _store.Initialize();
        var now = DateTime.UtcNow;
        lock (_current)
        {
            _current.Clear();
            _current.AddRange(_store.Current());
        }

        foreach (var entry in Snapshot().Where(e => e.ExpiresUtc is { } expires && expires <= now))
            End(entry, BlockState.Expired, now, null);
        _engine.SetBlocked(Snapshot().Select(e => e.Ip));

        if (!_enforce)
            return;

        var firewall = Snapshot().Where(e => e.Method == BlockMethod.Firewall && e.State is BlockState.Active or BlockState.Pending or BlockState.Failed).ToList();
        try
        {
            _firewall!.Sync(firewall.Select(e => IPAddress.Parse(e.Ip)));
            foreach (var entry in firewall)
                SetState(entry, BlockState.Active, null);
        }
        catch (Exception e)
        {
            foreach (var entry in firewall)
                SetState(entry, BlockState.Failed, e.Message);
            _log("Could not restore firewall blocks.", e);
        }

        // IIS: the ledger says which entries are ours; the store says which should exist.
        try
        {
            _iis!.Reconcile();
            var wanted = Snapshot().Where(e => e.Method == BlockMethod.Iis && e.State is BlockState.Active or BlockState.Pending or BlockState.Failed)
                .Select(e => e.Ip).ToHashSet(StringComparer.Ordinal);
            var owned = _iis.ListServerEntries().Where(x => x.AddedByUs && x.Key is { IsSingleAddress: true }).Select(x => x.Key!.Value.Address.ToString()).ToHashSet();
            _iisToAdd.UnionWith(wanted.Where(ip => !owned.Contains(ip)));
            _iisToRemove.UnionWith(owned.Where(ip => !wanted.Contains(ip)));
            foreach (var entry in Snapshot().Where(e => e.Method == BlockMethod.Iis && owned.Contains(e.Ip)))
                SetState(entry, BlockState.Active, null);
        }
        catch (Exception e)
        {
            _iisProblem = "Could not read the IIS IP restrictions: " + e.Message;
            _log(_iisProblem, e);
        }

        Maintain(now);
    }

    private void ProcessCommands()
    {
        var firewallAdds = new List<BlockEntry>();
        while (_commands.Reader.TryRead(out var command))
        {
            switch (command)
            {
                case DecisionCommand decision:
                    if (Record(decision.Decision) is { Method: BlockMethod.Firewall, State: BlockState.Pending } entry)
                        firewallAdds.Add(entry);
                    break;
                case ManualCommand manual:
                    var (result, added) = RecordManual(manual.Request);
                    if (added is { Method: BlockMethod.Firewall, State: BlockState.Pending })
                        firewallAdds.Add(added);
                    manual.Done.TrySetResult(result);
                    break;
                case UnblockCommand unblock:
                    unblock.Done.TrySetResult(Unblock(unblock.Ip, DateTime.UtcNow));
                    break;
                case SettingsCommand settings:
                    _settings = settings.Settings;
                    Configure(settings.Settings);
                    LiftNowProtected(DateTime.UtcNow);
                    break;
            }
        }

        if (firewallAdds.Count > 0)
            ApplyFirewall(firewallAdds);
        ApplyIisIfDue(DateTime.UtcNow, force: false);
    }

    private BlockEntry? Record(BlockDecision decision)
    {
        if (!_enforce || CurrentFor(decision.Ip) is not null)
            return null;
        var entry = new BlockEntry
        {
            Ip = decision.Ip,
            Method = decision.ViaProxy ? BlockMethod.Iis : BlockMethod.Firewall,
            State = decision.WatchOnly ? BlockState.Watch : BlockState.Pending,
            RuleId = decision.RuleId,
            Reason = decision.RuleName,
            Site = decision.Site,
            Path = decision.Path,
            Hits = decision.Hits,
            CreatedUtc = decision.TimeUtc,
            ExpiresUtc = decision.ExpiresUtc,
            Message = decision.WatchOnly ? "Watch mode: would have been blocked." : null,
        };
        Add(entry);
        if (entry is { State: BlockState.Pending, Method: BlockMethod.Iis })
            QueueIis(entry.Ip, add: true);
        return entry;
    }

    private (CommandResult Result, BlockEntry? Entry) RecordManual(ManualBlockRequest request)
    {
        if (!IpText.TryParse(request.Ip, out var address))
            return (CommandResult.Fail($"\"{request.Ip}\" is not an IP address."), null);
        var ip = address.ToString();
        if (_engine.IsProtected(address))
            return (CommandResult.Fail($"{ip} is protected (loopback, this server, the allow-list or a trusted proxy) and can't be blocked."), null);
        if (CurrentFor(ip) is { } existing && existing.State != BlockState.Watch)
            return (CommandResult.Fail($"{ip} is already blocked ({existing.State})."), null);
        if (CurrentFor(ip) is { } watch)
            End(watch, BlockState.Lifted, DateTime.UtcNow, "Replaced by a block made by hand.");

        var now = DateTime.UtcNow;
        var method = request.Method ?? (_seenViaProxy(ip) == true ? BlockMethod.Iis : BlockMethod.Firewall);
        if (method == BlockMethod.Iis && IpText.IsPrivate(address))
            return (CommandResult.Fail($"{ip} is a private address. In IIS proxy mode, blocking it would also turn away other visitors whose X-Forwarded-For passes through it. Block it in the firewall instead."), null);
        var hours = double.IsFinite(request.Hours) ? Math.Clamp(request.Hours, 0, 24 * 365) : 24;
        var entry = new BlockEntry
        {
            Ip = ip,
            Method = method,
            State = BlockState.Pending,
            Reason = string.IsNullOrWhiteSpace(request.Note) ? "Blocked by hand" : request.Note.Trim(),
            Manual = true,
            CreatedUtc = now,
            ExpiresUtc = hours > 0 ? now.AddHours(hours) : null,
        };
        Add(entry);
        _engine.MarkBlocked(ip);
        if (method == BlockMethod.Iis)
            QueueIis(ip, add: true);
        var how = method == BlockMethod.Firewall ? "in the Windows Firewall" : "in IIS (applied with the next batch of IIS changes)";
        return (CommandResult.Ok($"{ip} is blocked {how}{(entry.ExpiresUtc is { } until ? $" until {until.ToLocalTime():g}" : " until you unblock it")}."), entry);
    }

    private CommandResult Unblock(string text, DateTime now)
    {
        if (!IpText.TryParse(text, out var address))
            return CommandResult.Fail($"\"{text}\" is not an IP address.");
        var ip = address.ToString();
        var entries = Snapshot().Where(e => e.Ip == ip).ToList();
        if (entries.Count == 0)
            return CommandResult.Fail($"{ip} isn't blocked.");
        foreach (var entry in entries)
            End(entry, BlockState.Lifted, now, null);
        return CommandResult.Ok($"{ip} is unblocked.");
    }

    private void End(BlockEntry entry, BlockState state, DateTime now, string? message)
    {
        var wasEnforced = entry.State is BlockState.Active or BlockState.Pending or BlockState.Failed;
        entry.State = state;
        entry.EndedUtc = now;
        if (message is not null)
            entry.Message = message;
        Save(entry);
        lock (_current)
            _current.Remove(entry);
        _engine.MarkUnblocked(entry.Ip);

        if (!_enforce || !wasEnforced)
            return;
        if (entry.Method == BlockMethod.Firewall)
        {
            try
            {
                _firewall!.Unblock([IPAddress.Parse(entry.Ip)]);
            }
            catch (Exception e)
            {
                _lastError = $"Could not remove {entry.Ip} from the firewall: {e.Message}";
                _log(_lastError, e);
            }
        }
        else
        {
            QueueIis(entry.Ip, add: false);
        }
    }

    private void ApplyFirewall(List<BlockEntry> entries)
    {
        try
        {
            _firewall!.Block(entries.Select(e => IPAddress.Parse(e.Ip)));
            foreach (var entry in entries)
                SetState(entry, BlockState.Active, null);
        }
        catch (Exception e)
        {
            foreach (var entry in entries)
                SetState(entry, BlockState.Failed, e.Message);
            _lastError = "Firewall: " + e.Message;
            _log("Could not add firewall blocks.", e);
            return;
        }

        if (!_settings.DropOpenConnections)
            return;
        foreach (var entry in entries)
        {
            try
            {
                // Keep-alive connections opened before the block would otherwise live on for minutes. The
                // firewall rule covers every port, so close every connection from the address.
                TcpConnectionDropper.Drop(IPAddress.Parse(entry.Ip));
            }
            catch (Exception e)
            {
                _log($"Could not close open connections from {entry.Ip}.", e);
            }
        }
    }

    private void QueueIis(string ip, bool add)
    {
        if (add)
        {
            _iisToRemove.Remove(ip);
            _iisToAdd.Add(ip);
        }
        else
        {
            _iisToAdd.Remove(ip);
            _iisToRemove.Add(ip);
        }
    }

    /// <summary>Writes queued IIS changes in one commit, at most once per IisApplyIntervalSeconds.</summary>
    private void ApplyIisIfDue(DateTime now, bool force)
    {
        if (!_enforce || (_iisToAdd.Count == 0 && _iisToRemove.Count == 0))
            return;
        if (!force && now - _lastIisApplyUtc < TimeSpan.FromSeconds(_settings.IisApplyIntervalSeconds))
            return;

        var pending = Snapshot().Where(e => e.Method == BlockMethod.Iis && _iisToAdd.Contains(e.Ip)).ToList();
        if (_iisToAdd.Count > 0 && !_iisAvailable)
        {
            foreach (var entry in pending)
                SetState(entry, BlockState.Failed, _iisProblem ?? "IIS IP restrictions are not available.");
            _iisToAdd.Clear();
            if (_iisToRemove.Count == 0)
                return;
        }

        _lastIisApplyUtc = now;
        try
        {
            if (_iisToAdd.Count > 0 && !_proxyModeEnsured)
            {
                // Proxy mode makes IIS also check X-Forwarded-For against the deny list.
                _iis!.EnsureServerSettings();
                _proxyModeEnsured = true;
            }

            var add = _iisToAdd.Select(ip => IpBlockKey.Single(IPAddress.Parse(ip))).ToList();
            var remove = _iisToRemove.Select(ip => IpBlockKey.Single(IPAddress.Parse(ip))).ToList();
            var result = _iis!.Apply(add, remove);
            _iisToAdd.Clear();
            _iisToRemove.Clear();

            string? Outcome(string ip)
            {
                var key = IpBlockKey.Single(IPAddress.Parse(ip));
                if (result.AlreadyDeniedByAdmin.Contains(key))
                    return "Already denied in the IIS configuration (not by IIS Monitor).";
                if (result.AllowedByAdmin.Contains(key))
                    return "An allow entry in the IIS configuration covers this address.";
                if (result.Refused.Contains(key))
                    return "Refused: protected address, or IIS Monitor's limit of IIS entries reached.";
                return null;
            }

            foreach (var entry in pending)
            {
                var outcome = Outcome(entry.Ip);
                var refused = outcome is not null && !outcome.StartsWith("Already denied", StringComparison.Ordinal);
                SetState(entry, refused ? BlockState.Failed : BlockState.Active, outcome);
            }
        }
        catch (Exception e)
        {
            foreach (var entry in pending)
                SetState(entry, BlockState.Failed, "IIS: " + e.Message);
            _lastError = "IIS: " + e.Message;
            _log("Could not update the IIS IP restrictions.", e);
        }
    }

    /// <summary>Every few seconds: expiry, retries, batched IIS writes and status checks.</summary>
    private void Maintain(DateTime now)
    {
        foreach (var entry in Snapshot().Where(e => e.ExpiresUtc is { } expires && expires <= now))
            End(entry, BlockState.Expired, now, null);

        if (now - _addressesReadUtc >= AddressRefreshInterval)
        {
            _addressesReadUtc = now;
            _serverAddresses = ServerAddresses();
            Configure(_settings);
        }

        if (!_enforce)
            return;

        // Status first: whether IIS can take restrictions decides what the next batch does.
        if (now - _statusReadUtc >= StatusInterval)
        {
            _statusReadUtc = now;
            RefreshStatus(now);
        }

        if (now - _lastRetryUtc >= RetryInterval)
        {
            _lastRetryUtc = now;
            var failed = Snapshot().Where(e => e.State == BlockState.Failed).ToList();
            var firewall = failed.Where(e => e.Method == BlockMethod.Firewall).ToList();
            if (firewall.Count > 0)
                ApplyFirewall(firewall);
            foreach (var entry in failed.Where(e => e.Method == BlockMethod.Iis && _iisAvailable))
                QueueIis(entry.Ip, add: true);
        }

        ApplyIisIfDue(now, force: false);
    }

    private void RefreshStatus(DateTime now)
    {
        try
        {
            var firewall = _firewall!.GetStatus();
            _firewallProblem = firewall.BlocksTakeEffect ? null : firewall.Problem ?? "Firewall rules may not take effect.";
        }
        catch (Exception e)
        {
            _firewallProblem = e.Message;
        }

        try
        {
            var checkSites = now - _sitesCheckedUtc >= SiteCheckInterval && Snapshot().Any(e => e.Method == BlockMethod.Iis);
            var iis = _iis!.GetStatus(checkSites);
            _iisAvailable = iis.ModuleDllPresent && iis.ModuleInGlobalModules && !iis.SharedConfiguration;
            if (checkSites)
            {
                _sitesCheckedUtc = now;
                _iisSiteProblems = iis.Problems.Count == 0 ? null : string.Join(" ", iis.Problems.Where(p => p.Contains(':')).Take(5));
            }

            // Proxy mode being off is only a problem once there is something to enforce; it's turned on then.
            var problems = iis.Problems.Where(p => !p.StartsWith("Proxy mode is off", StringComparison.Ordinal) || Snapshot().Any(e => e.Method == BlockMethod.Iis))
                .Where(p => !p.Contains(':') || p.StartsWith("IP and Domain", StringComparison.Ordinal) || p.StartsWith("IIS Shared", StringComparison.Ordinal))
                .Where(p => !p.StartsWith("The server only lets listed clients", StringComparison.Ordinal))
                .ToList();
            if (IisIpBlocker.LedgerProblem is { } ledger)
                problems.Add(ledger);
            if (_iisSiteProblems is not null)
                problems.Add(_iisSiteProblems);
            _iisProblem = problems.Count == 0 ? null : string.Join(" ", problems);
        }
        catch (Exception e)
        {
            _iisProblem = "Could not read the IIS IP restrictions: " + e.Message;
        }
    }

    private void Configure(BlockingSettings settings)
    {
        // A standalone dashboard shows requests but never decides blocks.
        var effective = _enforce ? settings : new BlockingSettings { Mode = BlockingMode.Off, AllowList = settings.AllowList, TrustedProxies = settings.TrustedProxies };
        _engine.Configure(effective, _serverAddresses);
    }

    /// <summary>After the allow-list or trusted proxies change, lift blocks on addresses that are now protected.</summary>
    private void LiftNowProtected(DateTime now)
    {
        foreach (var entry in Snapshot())
        {
            if (IPAddress.TryParse(entry.Ip, out var address) && _engine.IsProtected(address))
                End(entry, BlockState.Lifted, now, "Lifted: the address is now on the allow-list or a trusted proxy.");
        }
    }

    private bool IsProtected(IPAddress address) => _engine.IsProtected(address);

    private IReadOnlyCollection<IpBlockKey> ProtectedNetworks()
    {
        var keys = new List<IpBlockKey>();
        foreach (var text in _settings.AllowList.Concat(_settings.TrustedProxies))
        {
            if (IpBlockKey.TryParse(text, out var key))
                keys.Add(key);
        }

        keys.AddRange(_serverAddresses.Select(IpBlockKey.Single));
        keys.Add(IpBlockKey.Single(IPAddress.Loopback));
        keys.Add(IpBlockKey.Single(IPAddress.IPv6Loopback));
        return keys;
    }

    private static IPAddress[] ServerAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => IpText.Normalize(a.Address))
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private BlockEntry? CurrentFor(string ip)
    {
        lock (_current)
            return _current.FirstOrDefault(e => e.Ip == ip);
    }

    private List<BlockEntry> Snapshot()
    {
        lock (_current)
            return _current.ToList();
    }

    private void Add(BlockEntry entry)
    {
        _store.Insert(entry);
        lock (_current)
            _current.Add(entry);
    }

    private void SetState(BlockEntry entry, BlockState state, string? message)
    {
        if (entry.State == state && entry.Message == message)
            return;
        entry.State = state;
        entry.Message = message;
        Save(entry);
    }

    private void Save(BlockEntry entry)
    {
        try
        {
            _store.Update(entry);
        }
        catch (Exception e)
        {
            _lastError = "Could not save the block list: " + e.Message;
            _log(_lastError, e);
        }
    }

    private void PublishStatus()
    {
        var current = Snapshot();
        _status = new BlockingStatus
        {
            Mode = _settings.Mode,
            Enforcing = _enforce,
            ActiveFirewall = current.Count(e => e is { Method: BlockMethod.Firewall, State: BlockState.Active }),
            ActiveIis = current.Count(e => e is { Method: BlockMethod.Iis, State: BlockState.Active }),
            Watching = current.Count(e => e.State == BlockState.Watch),
            Pending = current.Count(e => e.State == BlockState.Pending),
            Failed = current.Count(e => e.State == BlockState.Failed),
            FirewallProblem = _firewallProblem,
            IisProblem = _iisProblem,
            NextIisApplyUtc = _iisToAdd.Count + _iisToRemove.Count > 0 ? _lastIisApplyUtc.AddSeconds(_settings.IisApplyIntervalSeconds) : null,
            LastError = _lastError,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        _cts.Cancel();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }

    private abstract record Command;

    private sealed record DecisionCommand(BlockDecision Decision) : Command;

    private sealed record SettingsCommand(BlockingSettings Settings) : Command;

    private sealed record ManualCommand(ManualBlockRequest Request) : Command
    {
        public TaskCompletionSource<CommandResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record UnblockCommand(string Ip) : Command
    {
        public TaskCompletionSource<CommandResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
