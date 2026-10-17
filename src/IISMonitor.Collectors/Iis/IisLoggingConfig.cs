using System.Globalization;
using System.Runtime.InteropServices;
using IISMonitor.Core.Requests;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads and changes the IIS log settings that IIS Monitor depends on. Three levels:
/// <list type="bullet">
/// <item>response times: the W3C log target must include ETW (IIS 8.5+), with site name, URL, status and time taken;</item>
/// <item>request details (Requests tab, blacklist): also client IP, method, bytes sent, user agent and substatus;</item>
/// <item>header fields (per site): request and response headers as custom fields, so request kinds are
/// more accurate and visitors behind a trusted proxy are known (X-Forwarded-For).</item>
/// </list>
/// </summary>
internal static class IisLoggingConfig
{
    private const int TargetFile = 1;
    private const int TargetEtw = 2;

    // logExtFileFlags values from the IIS configuration schema.
    private const long FlagClientIp = 4;
    private const long FlagSiteName = 16;
    private const long FlagMethod = 128;
    private const long FlagUriStem = 256;
    private const long FlagHttpStatus = 1024;
    private const long FlagBytesSent = 4096;
    private const long FlagTimeTaken = 16384;
    private const long FlagUserAgent = 65536;
    private const long FlagHttpSubStatus = 2097152;

    /// <summary>What response times need. Unchanged, so existing installs keep working.</summary>
    private const long RequiredFields = FlagSiteName | FlagUriStem | FlagHttpStatus | FlagTimeTaken;

    /// <summary>What the Requests tab and the blacklist need on top.</summary>
    private const long RequestFields = FlagClientIp | FlagMethod | FlagBytesSent | FlagUserAgent | FlagHttpSubStatus;

    /// <summary>Custom fields IIS Monitor adds all start with this, so they can be told from an administrator's.</summary>
    public const string OwnFieldPrefix = "IISMon-";

    private static readonly Dictionary<string, long> FieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Date"] = 1, ["Time"] = 2, ["ClientIP"] = 4, ["UserName"] = 8, ["SiteName"] = 16, ["ComputerName"] = 32,
        ["ServerIP"] = 64, ["Method"] = 128, ["UriStem"] = 256, ["UriQuery"] = 512, ["HttpStatus"] = 1024,
        ["Win32Status"] = 2048, ["BytesSent"] = 4096, ["BytesRecv"] = 8192, ["TimeTaken"] = 16384,
        ["ServerPort"] = 32768, ["UserAgent"] = 65536, ["Cookie"] = 131072, ["Referer"] = 262144,
        ["ProtocolVersion"] = 524288, ["Host"] = 1048576, ["HttpSubStatus"] = 2097152,
    };

    private static readonly Dictionary<string, long> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["File"] = TargetFile,
        ["ETW"] = TargetEtw,
    };

    /// <summary>Whether IIS sends usable log events for <paramref name="site"/> to ETW.</summary>
    public static bool IsEtwReady(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C) =>
        HasFields(site, siteDefaultsLogFile, centralW3C, RequiredFields);

    /// <summary>Whether the events also carry what the Requests tab and the blacklist need.</summary>
    public static bool AreRequestFieldsReady(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C) =>
        HasFields(site, siteDefaultsLogFile, centralW3C, RequiredFields | RequestFields);

    private static bool HasFields(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C, long required)
    {
        try
        {
            var logFile = site.GetChildElement("logFile");
            var enabled = Effective(logFile, siteDefaultsLogFile, "enabled");
            if (enabled is bool on && !on)
                return false;

            // The central W3C log has its own field list but no log target attribute; the target
            // comes from the site (or the site defaults) either way.
            var fields = centralW3C is not null
                ? Flags(centralW3C.GetAttributeValue("logExtFileFlags"), FieldNames)
                : Flags(Effective(logFile, siteDefaultsLogFile, "logExtFileFlags"), FieldNames);
            return (Flags(Effective(logFile, siteDefaultsLogFile, "logTargetW3C"), TargetNames) & TargetEtw) != 0
                && (fields & required) == required;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The site's custom log fields, in order, and which of them carry the headers IIS Monitor reads.</summary>
    public static CustomFieldLayout ReadLayout(Site site)
    {
        try
        {
            var customFields = site.GetChildElement("logFile").GetChildElement("customFields");
            var maxLength = Convert.ToInt32(customFields.GetAttributeValue("maxCustomFieldLength") ?? 4096, CultureInfo.InvariantCulture);
            var names = new List<string>();
            var known = new Dictionary<HeaderField, string>();
            foreach (var field in ReadFields(customFields.GetCollection()))
            {
                names.Add(field.LogFieldName);
                foreach (var spec in HeaderFields.All)
                {
                    if (!field.Matches(spec))
                        continue;
                    // Prefer IIS Monitor's own field: its X-Forwarded-For is kept last.
                    if (!known.ContainsKey(spec.Field) || field.LogFieldName.Equals(spec.LogFieldName, StringComparison.OrdinalIgnoreCase))
                        known[spec.Field] = field.LogFieldName;
                }
            }

            return names.Count == 0 ? CustomFieldLayout.None : new CustomFieldLayout(names, known, maxLength);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or FormatException or InvalidCastException)
        {
            return CustomFieldLayout.None;
        }
    }

    /// <summary>Returns the central W3C log element when IIS uses one central log for all sites, else null.</summary>
    public static ConfigurationElement? CentralW3CLogFile(Configuration config)
    {
        var log = config.GetSection("system.applicationHost/log");
        var mode = Convert.ToString(log.GetAttributeValue("centralLogFileMode"), CultureInfo.InvariantCulture);
        // Enum attributes come back as numbers: Site = 0, CentralBinary = 1, CentralW3C = 2.
        return mode is "2" or "CentralW3C" ? log.GetChildElement("centralW3CLogFile") : null;
    }

    public static bool IsCentralBinary(Configuration config)
    {
        var mode = Convert.ToString(config.GetSection("system.applicationHost/log").GetAttributeValue("centralLogFileMode"), CultureInfo.InvariantCulture);
        return mode is "1" or "CentralBinary";
    }

    /// <summary>
    /// Adds ETW to the W3C log target and adds the response-time fields, for the site defaults and
    /// for every site that overrides them. Existing settings are kept; nothing is removed.
    /// </summary>
    public static string EnableEtwLogging() => Configure(new IisLoggingRequest { RequestFields = false });

    /// <summary>
    /// Applies an <see cref="IisLoggingRequest"/>: ETW plus response-time fields always, the request
    /// fields if asked (all sites), and header custom fields on the sites asked for.
    /// </summary>
    public static string Configure(IisLoggingRequest request)
    {
        lock (IisConfigWriteLock.Gate)
            return ConfigureLocked(request);
    }

    private static string ConfigureLocked(IisLoggingRequest request)
    {
        using var manager = new ServerManager();
        var config = manager.GetApplicationHostConfiguration();
        if (IsCentralBinary(config))
            throw new InvalidOperationException("IIS is set to central binary logging, which can't be sent to ETW. Switch logging to per-site or central W3C first.");

        var fields = RequiredFields | (request.RequestFields ? RequestFields : 0);
        var changes = new List<string>();
        var central = CentralW3CLogFile(config);
        if (central is not null)
            EnsureFields(central, "central W3C log", fields, changes);

        var sitesSection = config.GetSection("system.applicationHost/sites");
        var defaults = sitesSection.GetChildElement("siteDefaults").GetChildElement("logFile");
        EnsureTarget(defaults, "site defaults", changes, local: false);
        if (central is null)
            EnsureFields(defaults, "site defaults", fields, changes);

        var headerSites = request.HeaderFieldSites.ToHashSet();
        var removeSites = request.RemoveHeaderFieldSites.ToHashSet();
        foreach (var site in sitesSection.GetCollection())
        {
            var logFile = site.GetChildElement("logFile");
            var name = Convert.ToString(site.GetAttributeValue("name"), CultureInfo.InvariantCulture) ?? "site";
            var id = Convert.ToInt64(site.GetAttributeValue("id"), CultureInfo.InvariantCulture);
            EnsureTarget(logFile, name, changes, local: true);
            if (central is null)
                EnsureLocalFields(logFile, name, fields, changes);

            if (headerSites.Contains(id))
            {
                if (central is not null)
                    changes.Add($"{name}: header fields skipped (central W3C logging has no custom fields)");
                else
                    EnsureHeaderFields(logFile.GetChildElement("customFields"), name, changes);
            }
            else if (removeSites.Contains(id))
            {
                RemoveHeaderFields(logFile.GetChildElement("customFields"), name, changes);
            }
        }

        if (changes.Count == 0)
            return "IIS logging already had everything IIS Monitor needs.";

        manager.CommitChanges();
        return "Updated IIS logging: " + string.Join("; ", changes) + ".";
    }

    private static void EnsureTarget(ConfigurationElement logFile, string label, List<string> changes, bool local)
    {
        var attribute = logFile.GetAttribute("logTargetW3C");
        if (local && attribute.IsInheritedFromDefaultValue)
            return;
        var target = Flags(attribute.Value, TargetNames);
        if ((target & TargetEtw) == 0)
        {
            logFile.SetAttributeValue("logTargetW3C", (int)(target | TargetEtw));
            changes.Add($"{label}: log target now includes ETW");
        }
    }

    private static void EnsureFields(ConfigurationElement logFile, string label, long required, List<string> changes)
    {
        var fields = Flags(logFile.GetAttributeValue("logExtFileFlags"), FieldNames);
        if ((fields & required) != required)
        {
            logFile.SetAttributeValue("logExtFileFlags", (int)(fields | required));
            changes.Add($"{label}: added {Describe(required & ~fields)}");
        }
    }

    private static void EnsureLocalFields(ConfigurationElement logFile, string site, long required, List<string> changes)
    {
        var attribute = logFile.GetAttribute("logExtFileFlags");
        if (!attribute.IsInheritedFromDefaultValue)
            EnsureFields(logFile, site, required, changes);
    }

    /// <summary>
    /// Adds the header custom fields IIS Monitor reads, reusing an administrator's field for the
    /// same header, and makes IIS Monitor's X-Forwarded-For field the last one (see CustomFieldParser).
    /// </summary>
    private static void EnsureHeaderFields(ConfigurationElement customFields, string site, List<string> changes)
    {
        var collection = customFields.GetCollection();
        var added = new List<string>();
        foreach (var spec in HeaderFields.All)
        {
            var fields = ReadFields(collection);
            if (spec.Field == HeaderField.ForwardedFor)
            {
                var own = fields.FirstOrDefault(f => f.LogFieldName.Equals(spec.LogFieldName, StringComparison.OrdinalIgnoreCase));
                if (own is not null && own.Index == fields.Count - 1)
                    continue;
                if (own is not null)
                    collection.Remove(own.Element);
                AddField(collection, spec, UniqueName(spec.LogFieldName, fields, except: own));
                if (own is null)
                    added.Add(spec.SourceName);
                else
                    changes.Add($"{site}: moved the X-Forwarded-For field last");
                continue;
            }

            if (fields.Any(f => f.Matches(spec)))
                continue;
            var xff = fields.FirstOrDefault(f => f.LogFieldName.Equals(HeaderFields.All[^1].LogFieldName, StringComparison.OrdinalIgnoreCase));
            AddField(collection, spec, UniqueName(spec.LogFieldName, fields, except: null), at: xff?.Index);
            added.Add(spec.SourceName);
        }

        if (added.Count > 0)
            changes.Add($"{site}: now logs {string.Join(", ", added)}");
    }

    private static void RemoveHeaderFields(ConfigurationElement customFields, string site, List<string> changes)
    {
        var collection = customFields.GetCollection();
        var own = ReadFields(collection).Where(f => f.LogFieldName.StartsWith(OwnFieldPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var field in own)
            collection.Remove(field.Element);
        if (own.Count > 0)
            changes.Add($"{site}: removed IIS Monitor's header fields");
    }

    private static void AddField(ConfigurationElementCollection collection, HeaderFieldSpec spec, string name, int? at = null)
    {
        var element = collection.CreateElement("add");
        element["logFieldName"] = name;
        element["sourceName"] = spec.SourceName;
        element["sourceType"] = spec.IsResponseHeader ? "ResponseHeader" : "RequestHeader";
        if (at is { } index)
            collection.AddAt(index, element);
        else
            collection.Add(element);
    }

    /// <summary>logFieldName is the collection key; never add a duplicate.</summary>
    private static string UniqueName(string wanted, List<LoggedField> fields, LoggedField? except)
    {
        var taken = fields.Where(f => !ReferenceEquals(f, except)).Select(f => f.LogFieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static List<LoggedField> ReadFields(ConfigurationElementCollection collection)
    {
        var fields = new List<LoggedField>();
        var index = 0;
        foreach (var element in collection)
        {
            var name = Convert.ToString(element.GetAttributeValue("logFieldName"), CultureInfo.InvariantCulture) ?? "";
            var source = Convert.ToString(element.GetAttributeValue("sourceName"), CultureInfo.InvariantCulture) ?? "";
            var type = SourceType(element.GetAttributeValue("sourceType"));
            fields.Add(new LoggedField(index++, name, source, type, element));
        }

        return fields;
    }

    /// <summary>0 RequestHeader, 1 ResponseHeader, 2 ServerVariable (enums read back as numbers or names).</summary>
    private static int SourceType(object? value) => value switch
    {
        string s when s.Equals("ResponseHeader", StringComparison.OrdinalIgnoreCase) => 1,
        string s when s.Equals("ServerVariable", StringComparison.OrdinalIgnoreCase) => 2,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        string => 0,
        IConvertible c => c.ToInt32(CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static string Describe(long fields)
    {
        var names = FieldNames.Where(kv => (fields & kv.Value) != 0).Select(kv => kv.Key).ToList();
        return names.Count == 0 ? "fields" : string.Join(", ", names) + (names.Count == 1 ? " field" : " fields");
    }

    /// <summary>A site's own value if it sets one, otherwise the site defaults.</summary>
    private static object? Effective(ConfigurationElement logFile, ConfigurationElement defaults, string attribute)
    {
        var local = logFile.GetAttribute(attribute);
        return local.IsInheritedFromDefaultValue ? defaults.GetAttributeValue(attribute) : local.Value;
    }

    /// <summary>Flag attributes are numbers in Microsoft.Web.Administration, but accept "A,B" strings too.</summary>
    internal static long Flags(object? value, Dictionary<string, long> names)
    {
        switch (value)
        {
            case null:
                return 0;
            case int i:
                return i;
            case long l:
                return l;
            case uint u:
                return u;
            case Enum e:
                return Convert.ToInt64(e, CultureInfo.InvariantCulture);
            case string s:
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    return number;
                long result = 0;
                foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (names.TryGetValue(part, out var flag))
                        result |= flag;
                }

                return result;
            default:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    internal static long FieldFlags(object? value) => Flags(value, FieldNames);

    private sealed record LoggedField(int Index, string LogFieldName, string SourceName, int SourceType, ConfigurationElement Element)
    {
        /// <summary>Logs the same header (an equivalent server variable counts too: HTTP_X_FORWARDED_FOR).</summary>
        public bool Matches(HeaderFieldSpec spec)
        {
            if (SourceType == (spec.IsResponseHeader ? 1 : 0) && SourceName.Equals(spec.SourceName, StringComparison.OrdinalIgnoreCase))
                return true;
            return !spec.IsResponseHeader && SourceType == 2
                && SourceName.Equals("HTTP_" + spec.SourceName.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// One writer of applicationHost.config at a time inside this process (logging setup and IP
/// restrictions), so their read-modify-commit cycles don't trip over each other.
/// </summary>
internal static class IisConfigWriteLock
{
    public static readonly object Gate = new();
}
