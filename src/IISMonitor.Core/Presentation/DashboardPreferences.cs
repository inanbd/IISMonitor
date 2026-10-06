using System.Text.Json;

namespace IISMonitor.Core.Presentation;

/// <summary>Per-user dashboard choices that should survive a restart.</summary>
public sealed class DashboardPreferences
{
    /// <summary>
    /// App pools the user unticked on the Overview tab. Unticked (not ticked) pools are stored so
    /// that newly created pools show up ticked.
    /// </summary>
    public List<string> OverviewHiddenPools { get; set; } = [];

    /// <summary>Overview memory chart metric key: "working_set" or "private_bytes".</summary>
    public string OverviewMemoryMetric { get; set; } = "working_set";

    /// <summary>Style slot per app pool, so each keeps its color across restarts.</summary>
    public Dictionary<string, int> PoolStyleSlots { get; set; } = [];
}

/// <summary>Loads and saves <see cref="DashboardPreferences"/>; never throws on a bad or missing file.</summary>
public sealed class DashboardPreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IISMonitor", "dashboard.json");

    public string Path { get; } = path;

    public DashboardPreferences Load()
    {
        try
        {
            if (File.Exists(Path) && JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(Path), Json) is { } loaded)
            {
                loaded.OverviewHiddenPools ??= [];
                loaded.PoolStyleSlots ??= [];
                loaded.OverviewMemoryMetric ??= "working_set";
                return loaded;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new DashboardPreferences();
    }

    /// <summary>Returns false if the file couldn't be written (preferences are a convenience, not critical).</summary>
    public bool Save(DashboardPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, Json));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
