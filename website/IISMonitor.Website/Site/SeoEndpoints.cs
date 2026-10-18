using System.Xml.Linq;

namespace IISMonitor.Website.Site;

public static class SeoEndpoints
{
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly XNamespace Image = "http://www.google.com/schemas/sitemap-image/1.1";

    public static void MapSeoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sitemap.xml", (SiteContext site, Screenshots screenshots) =>
        {
            var urls = SiteMap.All.Select(page =>
            {
                var url = new XElement(Sitemap + "url", new XElement(Sitemap + "loc", site.Absolute(page.Path)));
                if (page.Screenshot is { } name && screenshots.Find(name) is { } file)
                    url.Add(new XElement(Image + "image", new XElement(Image + "loc", site.Absolute(file.Path))));
                return url;
            });
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(Sitemap + "urlset", new XAttribute(XNamespace.Xmlns + "image", Image), urls));
            return Results.Text(document.Declaration + "\n" + document.Root, "application/xml; charset=utf-8");
        });

        app.MapGet("/robots.txt", (SiteContext site) =>
            Results.Text($"User-agent: *\nAllow: /\n\nSitemap: {site.Absolute("/sitemap.xml")}\n", "text/plain; charset=utf-8"));
    }
}
