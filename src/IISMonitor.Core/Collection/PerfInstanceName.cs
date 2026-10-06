namespace IISMonitor.Core.Collection;

/// <summary>
/// Performance counter instance names can't contain some characters, so Windows rewrites them
/// (for example "Site (Test)" becomes "Site [Test]"). These helpers match names despite that.
/// </summary>
public static class PerfInstanceName
{
    public static string Mangle(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                '(' => '[',
                ')' => ']',
                '#' or '/' or '\\' => '_',
                _ => chars[i],
            };
        }

        return new string(chars);
    }

    public static bool TryGet<T>(IReadOnlyDictionary<string, T> byInstance, string name, out T value)
    {
        if (byInstance.TryGetValue(name, out value!))
            return true;
        return byInstance.TryGetValue(Mangle(name), out value!);
    }

    /// <summary>
    /// Parses a "W3SVC_W3WP" instance name, which has the form "&lt;pid&gt;_&lt;app pool name&gt;".
    /// </summary>
    public static bool TryParseWorkerInstance(string instance, out int pid, out string appPool)
    {
        pid = 0;
        appPool = "";
        var underscore = instance.IndexOf('_');
        if (underscore <= 0 || underscore == instance.Length - 1)
            return false;
        if (!int.TryParse(instance.AsSpan(0, underscore), out pid) || pid <= 0)
            return false;
        appPool = instance[(underscore + 1)..];
        return true;
    }

    /// <summary>Maps a possibly mangled app pool instance name back to the real pool name.</summary>
    public static string? ResolvePoolName(string instancePoolName, IEnumerable<string> poolNames)
    {
        string? mangledMatch = null;
        foreach (var pool in poolNames)
        {
            if (string.Equals(pool, instancePoolName, StringComparison.OrdinalIgnoreCase))
                return pool;
            if (mangledMatch is null && string.Equals(Mangle(pool), instancePoolName, StringComparison.OrdinalIgnoreCase))
                mangledMatch = pool;
        }

        return mangledMatch;
    }
}
