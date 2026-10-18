using System.Collections.Concurrent;

namespace IISMonitor.Website.Site;

/// <param name="Path">Site-relative URL, such as /images/screenshots/overview.png.</param>
public sealed record ScreenshotFile(string Path, int? Width, int? Height);

/// <summary>
/// Finds screenshots in wwwroot/images/screenshots by name, with or without extension, and reads
/// their size from the file, so replacing one (any size; .webp, .png or .jpg) needs no code change.
/// </summary>
public sealed class Screenshots(IWebHostEnvironment environment)
{
    public const string Folder = "images/screenshots";

    private static readonly string[] Extensions = [".webp", ".png", ".jpg", ".jpeg"];
    private readonly ConcurrentDictionary<(string, DateTimeOffset), (int, int)?> _sizes = new();

    public ScreenshotFile? Find(string name)
    {
        var candidates = System.IO.Path.HasExtension(name) ? [name] : Extensions.Select(x => name + x);
        foreach (var candidate in candidates)
        {
            var info = environment.WebRootFileProvider.GetFileInfo($"{Folder}/{candidate}");
            if (!info.Exists)
                continue;

            var size = _sizes.GetOrAdd((candidate, info.LastModified), _ =>
            {
                using var stream = info.CreateReadStream();
                return ImageSize.Read(stream);
            });
            return new ScreenshotFile($"/{Folder}/{candidate}", size?.Item1, size?.Item2);
        }

        return null;
    }
}
