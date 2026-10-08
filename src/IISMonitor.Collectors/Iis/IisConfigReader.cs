using System.Runtime.InteropServices;
using IISMonitor.Core.Collection;
using Microsoft.Web.Administration;

namespace IISMonitor.Collectors.Iis;

/// <summary>
/// Reads sites, applications and app pools through Microsoft.Web.Administration. The result is
/// cached and re-read every few seconds (states change) or as soon as applicationHost.config changes.
/// </summary>
internal sealed class IisConfigReader
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    internal static readonly string ConfigPath = Environment.ExpandEnvironmentVariables(@"%windir%\System32\inetsrv\config\applicationHost.config");

    private IisTopology? _cached;
    private DateTime _readAtUtc;
    private DateTime _configWriteTimeUtc;

    public bool IisInstalled => File.Exists(ConfigPath);

    public IisTopology Read(bool force = false)
    {
        var writeTime = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
        if (!force && _cached is not null && writeTime == _configWriteTimeUtc && DateTime.UtcNow - _readAtUtc < RefreshInterval)
            return _cached;

        if (!IisInstalled)
            throw new InvalidOperationException("IIS is not installed (applicationHost.config not found).");

        _cached = Load();
        _readAtUtc = DateTime.UtcNow;
        _configWriteTimeUtc = writeTime;
        return _cached;
    }

    private static IisTopology Load()
    {
        using var manager = new ServerManager(readOnly: true, applicationHostConfigurationPath: null);
        var topology = new IisTopology();

        foreach (var pool in manager.ApplicationPools)
        {
            topology.AppPools.Add(new AppPoolInfo
            {
                Name = pool.Name,
                State = Safe(() => pool.State.ToString()),
                RuntimeVersion = string.IsNullOrEmpty(pool.ManagedRuntimeVersion) ? "No managed code" : pool.ManagedRuntimeVersion,
                PipelineMode = Safe(() => pool.ManagedPipelineMode.ToString()),
                Identity = Safe(() => pool.ProcessModel.IdentityType == ProcessModelIdentityType.SpecificUser
                    ? pool.ProcessModel.UserName
                    : pool.ProcessModel.IdentityType.ToString()),
            });
        }

        var config = manager.GetApplicationHostConfiguration();
        var siteDefaultsLog = config.GetSection("system.applicationHost/sites").GetChildElement("siteDefaults").GetChildElement("logFile");
        var centralW3C = Safe(() => IisLoggingConfig.CentralW3CLogFile(config), null);
        var centralBinary = Safe(() => IisLoggingConfig.IsCentralBinary(config), false);
        var defaultPool = Safe(() => manager.ApplicationDefaults.ApplicationPoolName, "");
        if (string.IsNullOrEmpty(defaultPool))
            defaultPool = "DefaultAppPool";

        foreach (var site in manager.Sites)
        {
            var siteDefaultPool = Safe(() => site.ApplicationDefaults.ApplicationPoolName, "");
            topology.Sites.Add(new SiteInfo
            {
                Id = site.Id,
                Name = site.Name,
                State = Safe(() => site.State.ToString()),
                Bindings = site.Bindings.Select(FormatBinding).ToList(),
                Applications = site.Applications
                    .Select(a => new ApplicationInfo(
                        a.Path,
                        !string.IsNullOrEmpty(a.ApplicationPoolName) ? a.ApplicationPoolName
                        : !string.IsNullOrEmpty(siteDefaultPool) ? siteDefaultPool
                        : defaultPool))
                    .ToList(),
                EtwLoggingEnabled = !centralBinary && IisLoggingConfig.IsEtwReady(site, siteDefaultsLog, centralW3C),
                RequestFieldsLogged = !centralBinary && IisLoggingConfig.LogsRequestFields(site, siteDefaultsLog, centralW3C),
            });
        }

        return topology;
    }

    internal static string FormatBinding(Binding binding)
    {
        var protocol = binding.Protocol;
        var info = binding.BindingInformation ?? "";
        if (protocol is not ("http" or "https"))
            return $"{protocol}: {info}";

        // "address:port:host"; IPv6 addresses contain colons, so split from the right.
        var lastColon = info.LastIndexOf(':');
        var portColon = lastColon > 0 ? info.LastIndexOf(':', lastColon - 1) : -1;
        if (lastColon < 0 || portColon < 0)
            return $"{protocol}://{info}";

        var address = info[..portColon];
        var port = info[(portColon + 1)..lastColon];
        var host = info[(lastColon + 1)..];
        var name = host.Length > 0 ? host : address is "*" or "" ? "*" : address;
        var defaultPort = protocol == "http" ? "80" : "443";
        return port == defaultPort ? $"{protocol}://{name}" : $"{protocol}://{name}:{port}";
    }

    private static string Safe(Func<string> read) => Safe(read, "Unknown");

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            // Runtime state isn't available for some site types (e.g. FTP) or while WAS is stopped.
            return fallback;
        }
    }
}
