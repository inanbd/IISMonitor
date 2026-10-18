using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Options;

namespace IISMonitor.Website.Site;

/// <summary>Per-request helpers for links and SEO metadata, injected into views as <c>Site</c>.</summary>
public sealed class SiteContext(IOptions<SiteOptions> options, IHttpContextAccessor accessor, Screenshots screenshots)
{
    public const string Name = "IIS Monitor";
    public const string DefaultSocialImage = "/images/og-image.png";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public SiteOptions Options => options.Value;

    private HttpRequest Request => accessor.HttpContext?.Request ?? throw new InvalidOperationException("No current request.");

    /// <summary>The configured public address, or the address of this request.</summary>
    public string BaseUrl =>
        string.IsNullOrWhiteSpace(Options.BaseUrl)
            ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}"
            : Options.BaseUrl.TrimEnd('/');

    /// <summary>The registered page for this request, if any (not for errors and 404s).</summary>
    public SitePage? Page => SiteMap.Find(Request.Path);

    public string Absolute(string path) => BaseUrl + (path.StartsWith('/') ? path : "/" + path);

    public bool HasDownload => !string.IsNullOrWhiteSpace(Options.DownloadUrl);

    public string DownloadUrl => HasDownload ? Options.DownloadUrl : "/docs/getting-started";

    public bool HasSource => !string.IsNullOrWhiteSpace(Options.SourceUrl);

    public bool SourceIsGitHub =>
        Uri.TryCreate(Options.SourceUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>"GitHub" for a GitHub repository, otherwise "source code".</summary>
    public string SourceHost => SourceIsGitHub ? "GitHub" : "source code";

    /// <summary>The repository as people write it, such as "inanbd/IISMonitor".</summary>
    public string SourceName =>
        Uri.TryCreate(Options.SourceUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/') : Options.SourceUrl;

    public string SourceCloneUrl => Options.SourceUrl.TrimEnd('/') + ".git";

    /// <summary>The latest release's page (notes and files), when the source is on GitHub.</summary>
    public string? ReleasesUrl => SourceIsGitHub ? Options.SourceUrl.TrimEnd('/') + "/releases/latest" : null;

    /// <summary>Where to report problems: the repository's GitHub Issues, when the source is on GitHub.</summary>
    public string? IssuesUrl => SourceIsGitHub ? Options.SourceUrl.TrimEnd('/') + "/issues" : null;

    public bool HasLicense => !string.IsNullOrWhiteSpace(Options.License);

    /// <summary>"MIT License", from the License setting.</summary>
    public string LicenseName => Options.License.EndsWith("License", StringComparison.OrdinalIgnoreCase) ? Options.License : Options.License + " License";

    /// <summary>The LICENSE file in the repository, when the source is on GitHub.</summary>
    public string? LicenseUrl => HasLicense && SourceIsGitHub ? Options.SourceUrl.TrimEnd('/') + "/blob/HEAD/LICENSE" : null;

    /// <summary>The GitHub mark for a GitHub repository, a code icon otherwise.</summary>
    public IHtmlContent SourceIcon(string cssClass = "icon") => SourceIsGitHub ? Icons.GitHub(cssClass) : Icons.Svg("code", cssClass);

    /// <summary>Social preview image: the page's own screenshot, else the default card.</summary>
    public (string Url, int? Width, int? Height) SocialImage(SitePage? page)
    {
        if (page is { Screenshot: { } name } && page != SiteMap.Home && screenshots.Find(name) is { } file)
            return (Absolute(file.Path), file.Width, file.Height);
        return (Absolute(DefaultSocialImage), 1200, 630);
    }

    public IHtmlContent JsonLd(object data) =>
        // The default encoder escapes <, > and &, so the JSON can't close the script element.
        new HtmlString($"""<script type="application/ld+json">{JsonSerializer.Serialize(data, JsonOptions)}</script>""");

    public object StructuredData(SitePage page, IReadOnlyList<FaqItem>? faq = null)
    {
        var website = new Dictionary<string, object?>
        {
            ["@type"] = "WebSite",
            ["@id"] = Absolute("/#website"),
            ["name"] = Name,
            ["url"] = Absolute("/"),
        };
        var graph = new List<object> { website };

        if (page == SiteMap.Home)
        {
            var app = new Dictionary<string, object?>
            {
                ["@type"] = "SoftwareApplication",
                ["@id"] = Absolute("/#software"),
                ["name"] = Name,
                ["applicationCategory"] = "DeveloperApplication",
                ["applicationSubCategory"] = "Server monitoring",
                ["operatingSystem"] = "Windows Server 2022, Windows Server 2025",
                ["softwareRequirements"] = "Internet Information Services (IIS) 10, x64",
                ["description"] = page.Description,
                ["url"] = Absolute("/"),
                ["image"] = SocialImage(page).Url,
                ["screenshot"] = SiteMap.FeaturePages.Where(p => p.Screenshot is not null)
                    .Select(p => screenshots.Find(p.Screenshot!)).OfType<ScreenshotFile>().Select(f => Absolute(f.Path)).Distinct().ToList(),
                ["featureList"] = SiteMap.FeaturePages.Select(p => $"{p.NavTitle}: {p.Summary}").ToList(),
            };
            if (HasDownload)
                app["downloadUrl"] = Options.DownloadUrl;
            if (HasSource)
                app["sameAs"] = new[] { Options.SourceUrl };
            if (HasLicense)
            {
                app["license"] = LicenseUrl ?? LicenseName;
                app["isAccessibleForFree"] = true;
                app["offers"] = new Dictionary<string, object?> { ["@type"] = "Offer", ["price"] = "0", ["priceCurrency"] = "USD" };
            }
            if (!string.IsNullOrWhiteSpace(Options.Publisher))
                app["publisher"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = Options.Publisher };
            graph.Add(app);
            return new Dictionary<string, object?> { ["@context"] = "https://schema.org", ["@graph"] = graph };
        }

        graph.Add(Breadcrumbs(page));
        if (faq is { Count: > 0 })
        {
            graph.Add(new Dictionary<string, object?>
            {
                ["@type"] = "FAQPage",
                ["url"] = Absolute(page.Path),
                ["mainEntity"] = faq.Select(q => new Dictionary<string, object?>
                {
                    ["@type"] = "Question",
                    ["name"] = q.Question,
                    ["acceptedAnswer"] = new Dictionary<string, object?> { ["@type"] = "Answer", ["text"] = q.AnswerHtml },
                }).ToList(),
            });
        }
        else if (page == SiteMap.Contact)
        {
            graph.Add(new Dictionary<string, object?>
            {
                ["@type"] = "ContactPage",
                ["name"] = page.Heading,
                ["description"] = page.Description,
                ["url"] = Absolute(page.Path),
                ["isPartOf"] = new Dictionary<string, object?> { ["@id"] = Absolute("/#website") },
            });
        }
        else
        {
            graph.Add(new Dictionary<string, object?>
            {
                ["@type"] = "TechArticle",
                ["headline"] = page.Heading,
                ["description"] = page.Description,
                ["url"] = Absolute(page.Path),
                ["image"] = SocialImage(page).Url,
                ["isPartOf"] = new Dictionary<string, object?> { ["@id"] = Absolute("/#website") },
                ["about"] = new Dictionary<string, object?> { ["@type"] = "SoftwareApplication", ["name"] = Name },
            });
        }

        return new Dictionary<string, object?> { ["@context"] = "https://schema.org", ["@graph"] = graph };
    }

    /// <summary>Home › section › page, as shown above each article.</summary>
    public IReadOnlyList<(string Name, string Path)> Trail(SitePage page)
    {
        var trail = new List<(string, string)> { ("Home", "/") };
        if (page.Section == SiteMap.Features)
            trail.Add((SiteMap.FeatureIndex.NavTitle, SiteMap.FeatureIndex.Path));
        if (page != SiteMap.Home)
            trail.Add((page.NavTitle, page.Path));
        return trail;
    }

    private Dictionary<string, object?> Breadcrumbs(SitePage page) => new()
    {
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = Trail(page).Select((item, i) => new Dictionary<string, object?>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = item.Name,
            ["item"] = Absolute(item.Path),
        }).ToList(),
    };
}
