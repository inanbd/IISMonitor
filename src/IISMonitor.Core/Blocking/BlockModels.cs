namespace IISMonitor.Core.Blocking;

/// <summary>Where a block is enforced.</summary>
public enum BlockMethod
{
    /// <summary>A Windows Firewall rule: all traffic from the address is dropped, on every port.</summary>
    Firewall,

    /// <summary>An IIS IP restriction (proxy mode): for visitors that arrive through a trusted proxy.</summary>
    Iis,
}

public enum BlockState
{
    /// <summary>Decided, not yet written to the firewall or IIS (IIS changes are batched).</summary>
    Pending,

    Active,

    /// <summary>Watch mode: the IP tripped a rule, but nothing was blocked.</summary>
    Watch,

    /// <summary>The firewall or IIS refused or couldn't be reached; see the message. Retried later.</summary>
    Failed,

    /// <summary>Lifted by hand.</summary>
    Lifted,

    Expired,
}

/// <summary>One block (or watch-mode match), as stored and shown on the Blocked IPs list.</summary>
public sealed class BlockEntry
{
    public long Id { get; set; }

    public string Ip { get; set; } = "";

    public BlockMethod Method { get; set; }

    public BlockState State { get; set; }

    /// <summary>The rule that tripped, or null for a block made by hand.</summary>
    public string? RuleId { get; set; }

    /// <summary>The rule's name, or the note given with a manual block.</summary>
    public string Reason { get; set; } = "";

    public bool Manual { get; set; }

    public string? Site { get; set; }

    /// <summary>The URL of the request that tripped the rule.</summary>
    public string? Path { get; set; }

    public int Hits { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Null for a permanent block.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>When it was lifted or expired.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Why it isn't active (error), or a remark such as "already denied by IIS configuration".</summary>
    public string? Message { get; set; }

    public bool IsCurrent => State is BlockState.Pending or BlockState.Active or BlockState.Watch or BlockState.Failed;
}

/// <summary>An IP that tripped a rule, from <see cref="BlacklistEngine"/>.</summary>
public sealed record BlockDecision(
    string Ip,
    bool ViaProxy,
    string RuleId,
    string RuleName,
    int Hits,
    string? Site,
    string Path,
    DateTime TimeUtc,
    DateTime? ExpiresUtc,
    bool WatchOnly);

public sealed class BlockListQuery
{
    /// <summary>Also return lifted and expired blocks (most recent first).</summary>
    public bool IncludeEnded { get; set; }

    public int Limit { get; set; } = 1000;
}

public sealed class BlockList
{
    public List<BlockEntry> Entries { get; set; } = [];

    public BlockingStatus Status { get; set; } = new();
}

/// <summary>What the blacklist is doing and what stands in its way, for the dashboard.</summary>
public sealed class BlockingStatus
{
    public BlockingMode Mode { get; set; }

    /// <summary>This collector applies blocks (the Windows service does; a standalone dashboard doesn't).</summary>
    public bool Enforcing { get; set; }

    public int ActiveFirewall { get; set; }

    public int ActiveIis { get; set; }

    public int Watching { get; set; }

    public int Pending { get; set; }

    public int Failed { get; set; }

    /// <summary>Why firewall blocks may not take effect (firewall off, Group Policy...), or null.</summary>
    public string? FirewallProblem { get; set; }

    /// <summary>Why IIS blocks may not take effect (role service missing, proxy mode off at a site...), or null.</summary>
    public string? IisProblem { get; set; }

    public DateTime? NextIisApplyUtc { get; set; }

    public string? LastError { get; set; }
}

public sealed class ManualBlockRequest
{
    public string Ip { get; set; } = "";

    /// <summary>0 for a permanent block.</summary>
    public double Hours { get; set; } = 24;

    /// <summary>Null: the firewall, unless the IP was last seen through a trusted proxy.</summary>
    public BlockMethod? Method { get; set; }

    public string? Note { get; set; }
}

public sealed class UnblockRequest
{
    public string Ip { get; set; } = "";
}
