namespace IISMonitor.Core;

public static class DataPaths
{
    /// <summary>Overrides the data directory (used by tests and portable setups).</summary>
    public const string OverrideVariable = "IISMONITOR_DATA";

    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IISMonitor");
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryDatabase => Path.Combine(DataDirectory, "history.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
