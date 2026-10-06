namespace IISMonitor.Core.Collection;

/// <summary>
/// Works out which application (and therefore which app pool) served a request, by finding the
/// longest application path that is a prefix of the request URL, on a path-segment boundary.
/// </summary>
public sealed class ApplicationMatcher
{
    private readonly Dictionary<long, ApplicationInfo[]> _bySite = [];
    private readonly Dictionary<string, long> _siteIdsByName = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationMatcher(IEnumerable<SiteInfo> sites)
    {
        foreach (var site in sites)
        {
            _bySite[site.Id] = site.Applications
                .OrderByDescending(a => a.Path.TrimEnd('/').Length)
                .ToArray();
            _siteIdsByName.TryAdd(site.Name, site.Id);
        }
    }

    public long? ResolveSiteId(IisLogEvent e)
    {
        if (e.SiteId is { } id)
            return _bySite.ContainsKey(id) ? id : null;
        return e.SiteName is not null && _siteIdsByName.TryGetValue(e.SiteName, out var byName) ? byName : null;
    }

    public string? ResolveAppPool(long siteId, string uriStem)
    {
        if (!_bySite.TryGetValue(siteId, out var apps))
            return null;

        foreach (var app in apps)
        {
            var path = app.Path.TrimEnd('/');
            if (path.Length == 0)
                return app.AppPool;
            if (uriStem.StartsWith(path, StringComparison.OrdinalIgnoreCase)
                && (uriStem.Length == path.Length || uriStem[path.Length] == '/'))
                return app.AppPool;
        }

        return null;
    }
}
