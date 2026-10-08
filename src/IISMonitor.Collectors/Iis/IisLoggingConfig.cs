using System.Runtime.InteropServices;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads and changes the IIS log settings that live response times and IP and URL tracking depend
/// on: the W3C log target must include ETW (IIS 8.5+), and the log must contain the site name, URL,
/// status and time taken, plus the client IP, method and substatus for tracking.
/// </summary>
internal static class IisLoggingConfig
{
    private const int TargetFile = 1;
    private const int TargetEtw = 2;

    // logExtFileFlags values from the IIS configuration schema.
    internal const long FlagClientIp = 4;
    internal const long FlagSiteName = 16;
    internal const long FlagMethod = 128;
    internal const long FlagUriStem = 256;
    internal const long FlagHttpStatus = 1024;
    internal const long FlagTimeTaken = 16384;
    internal const long FlagHttpSubStatus = 2097152;

    /// <summary>Fields live response times need.</summary>
    internal const long RequiredFields = FlagSiteName | FlagUriStem | FlagHttpStatus | FlagTimeTaken;

    /// <summary>Further fields IP and URL tracking needs (all three are on by default in IIS).</summary>
    internal const long TrackingFields = FlagClientIp | FlagMethod | FlagHttpSubStatus;

    internal static readonly Dictionary<string, long> FieldNames = new(StringComparer.OrdinalIgnoreCase)
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

    // The fields EnableEtwLogging may add, as its messages name them.
    private static readonly (long Flag, string Name)[] FieldLabels =
    [
        (FlagSiteName, "site name"),
        (FlagUriStem, "URL"),
        (FlagHttpStatus, "status"),
        (FlagTimeTaken, "time taken"),
        (FlagClientIp, "client IP"),
        (FlagMethod, "method"),
        (FlagHttpSubStatus, "substatus"),
    ];

    /// <summary>Whether IIS sends usable log events for <paramref name="site"/> to ETW.</summary>
    public static bool IsEtwReady(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C) =>
        SendsToEtw(site, siteDefaultsLogFile, centralW3C, RequiredFields);

    /// <summary>
    /// Whether the site's ETW log events also carry the client IP, method and substatus for IP and URL
    /// tracking (and the fields <see cref="IsEtwReady"/> checks, without which nothing is recorded).
    /// </summary>
    public static bool LogsRequestFields(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C) =>
        SendsToEtw(site, siteDefaultsLogFile, centralW3C, RequiredFields | TrackingFields);

    private static bool SendsToEtw(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C, long fields)
    {
        try
        {
            if (centralW3C is not null)
            {
                return HasFlags(centralW3C, "logTargetW3C", TargetEtw, TargetNames)
                    && HasFlags(centralW3C, "logExtFileFlags", fields, FieldNames);
            }

            var logFile = site.GetChildElement("logFile");
            var enabled = Effective(logFile, siteDefaultsLogFile, "enabled");
            if (enabled is bool on && !on)
                return false;

            return (Flags(Effective(logFile, siteDefaultsLogFile, "logTargetW3C"), TargetNames) & TargetEtw) != 0
                && (Flags(Effective(logFile, siteDefaultsLogFile, "logExtFileFlags"), FieldNames) & fields) == fields;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Returns the central W3C log element when IIS uses one central log for all sites, else null.</summary>
    public static ConfigurationElement? CentralW3CLogFile(Configuration config)
    {
        var log = config.GetSection("system.applicationHost/log");
        var mode = Convert.ToString(log.GetAttributeValue("centralLogFileMode"));
        // Enum attributes come back as numbers: Site = 0, CentralBinary = 1, CentralW3C = 2.
        return mode is "2" or "CentralW3C" ? log.GetChildElement("centralW3CLogFile") : null;
    }

    public static bool IsCentralBinary(Configuration config)
    {
        var mode = Convert.ToString(config.GetSection("system.applicationHost/log").GetAttributeValue("centralLogFileMode"));
        return mode is "1" or "CentralBinary";
    }

    /// <summary>
    /// Adds ETW to the W3C log target and adds the fields response times and IP and URL tracking need
    /// (site name, URL, status, time taken, client IP, method and substatus), for the site defaults and
    /// for every site that overrides them. Existing settings are kept; nothing is removed.
    /// </summary>
    public static string EnableEtwLogging()
    {
        using var manager = new ServerManager();
        var config = manager.GetApplicationHostConfiguration();
        if (IsCentralBinary(config))
            throw new InvalidOperationException("IIS is set to central binary logging, which can't be sent to ETW. Switch logging to per-site or central W3C first.");

        var changes = new List<string>();
        var central = CentralW3CLogFile(config);
        if (central is not null)
            Ensure(central, "central W3C log", changes);

        var sitesSection = config.GetSection("system.applicationHost/sites");
        var defaults = sitesSection.GetChildElement("siteDefaults").GetChildElement("logFile");
        Ensure(defaults, "site defaults", changes);

        foreach (var site in sitesSection.GetCollection())
        {
            var logFile = site.GetChildElement("logFile");
            var name = Convert.ToString(site.GetAttributeValue("name")) ?? "site";
            EnsureLocalOverrides(logFile, name, changes);
        }

        if (changes.Count == 0)
            return "ETW logging and the log fields it needs were already on for all sites.";

        manager.CommitChanges();
        return "Updated IIS logging: " + string.Join("; ", changes) + ".";
    }

    private static void Ensure(ConfigurationElement logFile, string label, List<string> changes)
    {
        var target = Flags(logFile.GetAttributeValue("logTargetW3C"), TargetNames);
        if ((target & TargetEtw) == 0)
        {
            logFile.SetAttributeValue("logTargetW3C", (int)(target | TargetEtw));
            changes.Add($"{label}: log target now includes ETW");
        }

        var fields = Flags(logFile.GetAttributeValue("logExtFileFlags"), FieldNames);
        var missing = MissingFields(fields);
        if (missing != 0)
        {
            logFile.SetAttributeValue("logExtFileFlags", (int)(fields | missing));
            changes.Add($"{label}: {AddedFieldsMessage(missing)}");
        }
    }

    private static void EnsureLocalOverrides(ConfigurationElement logFile, string site, List<string> changes)
    {
        var target = logFile.GetAttribute("logTargetW3C");
        if (!target.IsInheritedFromDefaultValue)
        {
            var value = Flags(target.Value, TargetNames);
            if ((value & TargetEtw) == 0)
            {
                logFile.SetAttributeValue("logTargetW3C", (int)(value | TargetEtw));
                changes.Add($"{site}: log target now includes ETW");
            }
        }

        var fields = logFile.GetAttribute("logExtFileFlags");
        if (!fields.IsInheritedFromDefaultValue)
        {
            var value = Flags(fields.Value, FieldNames);
            var missing = MissingFields(value);
            if (missing != 0)
            {
                logFile.SetAttributeValue("logExtFileFlags", (int)(value | missing));
                changes.Add($"{site}: {AddedFieldsMessage(missing)}");
            }
        }
    }

    /// <summary>The fields response times and IP and URL tracking need that <paramref name="fields"/> lacks.</summary>
    internal static long MissingFields(long fields) => (RequiredFields | TrackingFields) & ~fields;

    /// <summary>"added the client IP, method and substatus log fields", naming the fields in <paramref name="added"/>.</summary>
    internal static string AddedFieldsMessage(long added)
    {
        var names = FieldLabels.Where(f => (added & f.Flag) != 0).Select(f => f.Name).ToList();
        if (names.Count == 0)
            return "no log fields added";

        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return $"added the {list} log field{(names.Count == 1 ? "" : "s")}";
    }

    private static bool HasFlags(ConfigurationElement element, string attribute, long required, Dictionary<string, long> names) =>
        (Flags(element.GetAttributeValue(attribute), names) & required) == required;

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
                return Convert.ToInt64(e);
            case string s:
                if (long.TryParse(s, out var number))
                    return number;
                long result = 0;
                foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (names.TryGetValue(part, out var flag))
                        result |= flag;
                }

                return result;
            default:
                return Convert.ToInt64(value);
        }
    }
}
