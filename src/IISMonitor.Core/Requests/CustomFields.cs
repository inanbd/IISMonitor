using System.Net;

namespace IISMonitor.Core.Requests;

/// <summary>The request and response headers IIS Monitor asks IIS to log as custom fields.</summary>
public enum HeaderField
{
    RequestedWith,
    FetchDest,
    FetchMode,
    ContentType,
    ContentDisposition,

    /// <summary>Logged last on purpose: see <see cref="CustomFieldParser"/>.</summary>
    ForwardedFor,
}

/// <summary>How IIS logs one header: its source, and the log field name IIS Monitor uses when it adds it.</summary>
public sealed record HeaderFieldSpec(HeaderField Field, string LogFieldName, string SourceName, bool IsResponseHeader);

public static class HeaderFields
{
    /// <summary>In the order they are added to a site, X-Forwarded-For last.</summary>
    public static readonly IReadOnlyList<HeaderFieldSpec> All =
    [
        new(HeaderField.RequestedWith, "IISMon-X-Requested-With", "X-Requested-With", false),
        new(HeaderField.FetchDest, "IISMon-Sec-Fetch-Dest", "Sec-Fetch-Dest", false),
        new(HeaderField.FetchMode, "IISMon-Sec-Fetch-Mode", "Sec-Fetch-Mode", false),
        new(HeaderField.ContentType, "IISMon-Resp-Content-Type", "Content-Type", true),
        new(HeaderField.ContentDisposition, "IISMon-Resp-Content-Disposition", "Content-Disposition", true),
        new(HeaderField.ForwardedFor, "IISMon-X-Forwarded-For", "X-Forwarded-For", false),
    ];
}

/// <summary>
/// The custom log fields configured for one site, in configuration order (which is the order IIS
/// writes them), with the ones IIS Monitor understands. An administrator's own fields are included
/// in <see cref="Names"/> so they can be skipped while parsing.
/// </summary>
public sealed class CustomFieldLayout
{
    public static readonly CustomFieldLayout None = new([], new Dictionary<HeaderField, string>(), 4096);

    public CustomFieldLayout(IReadOnlyList<string> names, IReadOnlyDictionary<HeaderField, string> known, int maxFieldLength)
    {
        Names = names;
        Known = known;
        MaxFieldLength = maxFieldLength;
        ForwardedForIsLast = known.TryGetValue(HeaderField.ForwardedFor, out var xff) && names.Count > 0
                             && string.Equals(names[^1], xff, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every logFieldName configured for the site, in order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The logFieldName that carries each header IIS Monitor reads (an administrator's existing field is reused).</summary>
    public IReadOnlyDictionary<HeaderField, string> Known { get; }

    /// <summary>IIS truncates each custom field at this many characters (maxCustomFieldLength).</summary>
    public int MaxFieldLength { get; }

    /// <summary>X-Forwarded-For is the last field, which lets it be read safely even when other values hold spaces.</summary>
    public bool ForwardedForIsLast { get; }

    public bool Has(HeaderField field) => Known.ContainsKey(field);
}

/// <summary>Header values read from one request's custom fields; null when not logged or absent ("-").</summary>
public readonly record struct HeaderValues
{
    public string? RequestedWith { get; init; }
    public string? FetchDest { get; init; }
    public string? FetchMode { get; init; }
    public string? ContentType { get; init; }
    public string? ContentDisposition { get; init; }
    public string? ForwardedFor { get; init; }

    /// <summary>
    /// <see cref="ForwardedFor"/> was read unambiguously and wasn't cut off, so its right-most
    /// entries (the ones a trusted proxy added) can be relied on.
    /// </summary>
    public bool ForwardedForReliable { get; init; }
}

/// <summary>
/// Splits the CustomFields string of an IIS log ETW event ("Name1 value1 Name2 value2 ...", "-" for an
/// absent header) into values.
/// </summary>
/// <remarks>
/// Header values come from the client, so a value can contain another field's name. The parser
/// first expects exactly the configured names at their positions. If that fails (a value with a
/// space, or a missing field), other headers take the single token after the first occurrence of
/// their name, which can only misclassify the sender's own request. X-Forwarded-For is trusted only
/// when it is the last configured field, and then everything after its last occurrence is its
/// value: its right-most entries are added by the trusted proxy after anything the client sent, so
/// the client can't hide or forge them. A value at the truncation limit is never trusted, since the
/// proxy's entries may have been cut off.
/// </remarks>
public static class CustomFieldParser
{
    /// <summary>All custom data of a request is cut off at 64 KB.</summary>
    public const int TotalLimit = 65_536;

    public static HeaderValues Parse(string? raw, CustomFieldLayout layout)
    {
        if (string.IsNullOrWhiteSpace(raw) || layout.Names.Count == 0 || layout.Known.Count == 0)
            return default;

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = layout.Names;
        var strict = tokens.Length == names.Count * 2;
        for (var i = 0; strict && i < names.Count; i++)
            strict = string.Equals(tokens[i * 2], names[i], StringComparison.OrdinalIgnoreCase);

        string? Value(HeaderField field)
        {
            if (!layout.Known.TryGetValue(field, out var name))
                return null;
            if (strict)
            {
                for (var i = 0; i < names.Count; i++)
                {
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                        return Clean(tokens[i * 2 + 1]);
                }

                return null;
            }

            var at = Array.FindIndex(tokens, t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < tokens.Length ? Clean(tokens[at + 1]) : null;
        }

        // X-Forwarded-For is relied on only as the last field: then, however the values before it are
        // shaped, the string ends with the entries the proxy appended.
        string? forwardedFor = null;
        var reliable = false;
        if (layout.Known.TryGetValue(HeaderField.ForwardedFor, out var xffName))
        {
            if (layout.ForwardedForIsLast)
            {
                var at = Array.FindLastIndex(tokens, t => string.Equals(t, xffName, StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && at + 1 < tokens.Length)
                {
                    forwardedFor = Clean(string.Join(' ', tokens, at + 1, tokens.Length - at - 1));
                    reliable = true;
                }
            }
            else
            {
                forwardedFor = Value(HeaderField.ForwardedFor);
            }

            // Cut off by IIS: the entries the proxy appended at the end may be missing.
            if (raw.Length >= TotalLimit - 16 || (forwardedFor?.Length ?? 0) >= layout.MaxFieldLength - 1)
                reliable = false;
        }

        return new HeaderValues
        {
            RequestedWith = Value(HeaderField.RequestedWith),
            FetchDest = Value(HeaderField.FetchDest),
            FetchMode = Value(HeaderField.FetchMode),
            ContentType = Value(HeaderField.ContentType),
            ContentDisposition = Value(HeaderField.ContentDisposition),
            ForwardedFor = forwardedFor,
            ForwardedForReliable = reliable,
        };
    }

    private static string? Clean(string value) => value is "-" or "" ? null : value;
}

/// <summary>Who sent a request: the connecting address, and the visitor behind a trusted proxy.</summary>
/// <param name="Client">The visitor; null when it can't be known (a trusted proxy sent no usable X-Forwarded-For).</param>
/// <param name="Peer">The address that connected to IIS (c-ip).</param>
/// <param name="ViaProxy">The request came through a trusted proxy; blocking it needs IIS, not the firewall.</param>
public readonly record struct ClientIdentity(IPAddress? Client, IPAddress? Peer, bool ViaProxy);

public static class ClientIpResolver
{
    private const int MaxEntries = 20;

    /// <summary>
    /// The visitor's address. X-Forwarded-For is honoured only when the connecting address is a
    /// trusted proxy, and is read from the right, skipping other trusted proxies, so a client can't
    /// put someone else's address in it.
    /// </summary>
    public static ClientIdentity Resolve(string? clientIp, in HeaderValues headers, IpSet trustedProxies)
    {
        if (!IpText.TryParse(clientIp, out var peer))
            return default;
        if (trustedProxies.IsEmpty || !trustedProxies.Contains(peer))
            return new ClientIdentity(peer, peer, false);

        if (!headers.ForwardedForReliable || string.IsNullOrEmpty(headers.ForwardedFor))
            return new ClientIdentity(null, peer, true);

        var entries = headers.ForwardedFor.Split(',');
        IPAddress? last = null;
        for (var i = entries.Length - 1; i >= 0 && entries.Length - i <= MaxEntries; i--)
        {
            if (!IpText.TryParseForwarded(entries[i], out var hop))
                return new ClientIdentity(null, peer, true);
            if (!trustedProxies.Contains(hop))
                return new ClientIdentity(hop, peer, true);
            last = hop;
        }

        // Every hop is a trusted proxy: the request came from the proxy itself (a health probe).
        return new ClientIdentity(last, peer, true);
    }
}
