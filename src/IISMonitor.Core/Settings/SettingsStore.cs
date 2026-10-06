using System.Text.Json;

namespace IISMonitor.Core.Settings;

/// <summary>Loads and saves <see cref="MonitorSettings"/> as JSON, replacing the file atomically.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public MonitorSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(Path), SettingsJson.Options);
                if (loaded is not null)
                    return loaded.Normalize();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall back to defaults; the next save rewrites the file.
        }

        return new MonitorSettings().Normalize();
    }

    public void Save(MonitorSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalize(), SettingsJson.Options));
        File.Move(temp, Path, overwrite: true);
    }
}
