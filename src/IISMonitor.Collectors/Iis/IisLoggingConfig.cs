using System.Runtime.InteropServices;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads and changes the IIS log settings that live response times depend on: the W3C log target
/// must include ETW (IIS 8.5+), and the log must contain the site name, URL, status and time taken.
/// </summary>
internal static class IisLoggingConfig
{
    private const int TargetFile = 1;
    private const int TargetEtw = 2;

    // logExtFileFlags values from the IIS configuration schema.
    private const long FlagSiteName = 16;
    private const long FlagUriStem = 256;
    private const long FlagHttpStatus = 1024;
    private const long FlagTimeTaken = 16384;
    private const long RequiredFields = FlagSiteName | FlagUriStem | FlagHttpStatus | FlagTimeTaken;

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
    public static bool IsEtwReady(Site site, ConfigurationElement siteDefaultsLogFile, ConfigurationElement? centralW3C)
    {
        try
        {
            if (centralW3C is not null)
            {
                return HasFlags(centralW3C, "logTargetW3C", TargetEtw, TargetNames)
                    && HasFlags(centralW3C, "logExtFileFlags", RequiredFields, FieldNames);
            }

            var logFile = site.GetChildElement("logFile");
            var enabled = Effective(logFile, siteDefaultsLogFile, "enabled");
            if (enabled is bool on && !on)
                return false;

            return (Flags(Effective(logFile, siteDefaultsLogFile, "logTargetW3C"), TargetNames) & TargetEtw) != 0
                && (Flags(Effective(logFile, siteDefaultsLogFile, "logExtFileFlags"), FieldNames) & RequiredFields) == RequiredFields;
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
    /// Adds ETW to the W3C log target and adds the required fields, for the site defaults and for
    /// every site that overrides them. Existing settings are kept; nothing is removed.
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
            return "ETW logging was already enabled for all sites.";

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
        if ((fields & RequiredFields) != RequiredFields)
        {
            logFile.SetAttributeValue("logExtFileFlags", (int)(fields | RequiredFields));
            changes.Add($"{label}: added site name, URL, status and time-taken fields");
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
            if ((value & RequiredFields) != RequiredFields)
            {
                logFile.SetAttributeValue("logExtFileFlags", (int)(value | RequiredFields));
                changes.Add($"{site}: added required log fields");
            }
        }
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
