namespace IISMonitor.Website.Site;

/// <summary>The "Site" section of appsettings.json.</summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>
    /// Public address of the site, such as https://www.example.com, used for canonical links,
    /// Open Graph tags and the sitemap. Leave empty to use the address of each request.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Where the download buttons point. Empty: the Getting started guide.</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>Source code repository. Empty: no source link is shown.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Company or person publishing IIS Monitor, for the footer and structured data. Optional.</summary>
    public string Publisher { get; set; } = "";

    /// <summary>The license IIS Monitor is released under, such as "MIT". Empty: the site doesn't call it free or open source.</summary>
    public string License { get; set; } = "";
}
