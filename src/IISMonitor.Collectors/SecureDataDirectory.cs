using System.Security.AccessControl;
using System.Security.Principal;

namespace IISMonitor.Collectors;

/// <summary>
/// Creates the data directory readable only by Administrators and SYSTEM: the settings file can
/// hold SQL Server connection strings.
/// </summary>
public static class SecureDataDirectory
{
    public static void Ensure(string path)
    {
        if (Directory.Exists(path))
            return;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(path).Create(security);
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
