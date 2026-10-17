using IISMonitor.Core.Blocking;
using IISMonitor.Core.Requests;

namespace IISMonitor.Core.Presentation;

/// <summary>Wording for the Requests tab, kept here so it can be tested without WPF.</summary>
public static class RequestsPresentation
{
    public static string Label(RequestKind kind) => kind switch
    {
        RequestKind.PageView => "Page view",
        RequestKind.XhrApi => "XHR / API",
        RequestKind.Static => "Static content",
        RequestKind.Download => "Download",
        _ => "Other",
    };

    public static string Label(MethodGroup method) => method switch
    {
        MethodGroup.Get => "GET",
        MethodGroup.Post => "POST",
        _ => "Other methods",
    };

    public static string Label(BlockingMode mode) => mode switch
    {
        BlockingMode.Watch => "Watch only (list IPs, don't block)",
        BlockingMode.Block => "Block",
        _ => "Off",
    };

    public static string Label(TopIpWindow window) => window switch
    {
        TopIpWindow.FiveMinutes => "Last 5 minutes",
        TopIpWindow.OneHour => "Last hour",
        _ => "Last 24 hours",
    };

    public static string State(BlockEntry entry) => entry.State switch
    {
        BlockState.Active => entry.Method == BlockMethod.Firewall ? "Blocked (firewall)" : "Blocked (IIS)",
        BlockState.Pending => entry.Method == BlockMethod.Firewall ? "Blocking…" : "Waiting for the next IIS update",
        BlockState.Watch => "Watch: would be blocked",
        BlockState.Failed => "Not blocked: " + (entry.Message ?? "failed"),
        BlockState.Lifted => "Lifted",
        _ => "Expired",
    };

    public static string Expires(BlockEntry entry, DateTime nowUtc)
    {
        if (!entry.IsCurrent)
            return entry.EndedUtc is { } ended ? ended.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "";
        if (entry.ExpiresUtc is not { } expires)
            return "never (until lifted)";
        var left = expires - nowUtc;
        return left <= TimeSpan.Zero ? "now"
            : left < TimeSpan.FromHours(1) ? $"in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min"
            : left < TimeSpan.FromDays(2) ? $"in {(int)Math.Round(left.TotalHours)} h"
            : $"in {(int)Math.Round(left.TotalDays)} days";
    }

    /// <summary>The visitor, with the proxy it came through.</summary>
    public static string Client(RequestRecord record) =>
        record.ViaProxy
            ? (record.ClientIp ?? "unknown") + $" (via {record.PeerIp})"
            : record.ClientIp ?? "";

    /// <summary>Problems that stop a blacklist draft from being saved, and warnings worth showing.</summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Validate(BlockingSettings draft)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var bad in BlockingSettings.InvalidNetworks(draft.AllowList))
            errors.Add($"Allow-list: \"{bad}\" is not an address or a network (e.g. 203.0.113.5 or 203.0.113.0/24).");
        foreach (var bad in BlockingSettings.InvalidNetworks(draft.TrustedProxies))
            errors.Add($"Trusted proxies: \"{bad}\" is not an address or a network.");

        var index = 0;
        foreach (var rule in draft.Rules)
        {
            index++;
            var name = string.IsNullOrWhiteSpace(rule.Name) ? $"Rule {index}" : rule.Name.Trim();
            if (UrlPattern.Split(rule.Pattern).Any() == false)
                errors.Add($"{name}: enter at least one URL pattern.");
            foreach (var broad in UrlPattern.TooBroad(rule.Pattern))
                errors.Add($"{name}: \"{broad}\" would match ordinary visitors' requests. Use a more specific path.");
            if (rule.Hits < 1)
                errors.Add($"{name}: the hit count must be at least 1.");
            if (!double.IsFinite(rule.WithinMinutes) || rule.WithinMinutes <= 0)
                errors.Add($"{name}: the time window must be more than 0 minutes.");
            if (!double.IsFinite(rule.BlockHours) || rule.BlockHours < 0)
                errors.Add($"{name}: the block length can't be negative (0 means until lifted).");
            if (rule.Hits == 1 && rule.Enabled)
                warnings.Add($"{name}: one request is enough to block. A typo or an old bookmark from a real visitor would be blocked too.");
        }

        if (draft.Mode == BlockingMode.Block && draft.Rules.Count(r => r.Enabled) == 0)
            warnings.Add("Blocking is on, but no rule is enabled.");
        return (errors, warnings);
    }
}
