# IIS Monitor website

The product website for IIS Monitor: an ASP.NET Core Razor Pages site on .NET 10 that explains
every feature in detail. Pages are plain `.cshtml` files, with no client-side framework and one small
script for progressive enhancements.

## Run it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project website/IISMonitor.Website
```

Then open <http://localhost:5080>.

## Configure

`IISMonitor.Website/appsettings.json`, section `Site` (or environment variables such as `Site__BaseUrl`):

| Setting | Purpose |
|---|---|
| `BaseUrl` | Public address, such as `https://www.example.com`. Used for canonical links, Open Graph tags, structured data, `sitemap.xml` and `robots.txt`. Set it in production; when empty, the address of each request is used. |
| `DownloadUrl` | Where the **Download** buttons go. When empty they say **Get started** and open the install guide. |
| `SourceUrl` | Source repository. When set, a **Source** link appears in the header and footer. |
| `Publisher` | Company or person shown in the footer copyright and in the structured data. Optional. |

## Replace the screenshots

The images in `IISMonitor.Website/wwwroot/images/screenshots` are **placeholders**: mockups of the
dashboard with made-up data. Replace each with a real screenshot under the **same name**. You can use
`.png`, `.webp` or `.jpg` and any size: pages read each image's width and height from the file and
add a cache-busting version automatically, so no code changes are needed. If two files share a name,
`.webp` wins over `.png`, which wins over `.jpg`, so delete the old file when you switch formats.

| File | Capture | Suggested state |
|---|---|---|
| `overview.png` | Main window, **Overview** tab | 8–12 pools ticked, mouse over a spike so the read-out shows. Also the home page hero. |
| `app-pools.png` | **App pools** tab | A busy pool selected, with its processes and charts visible. |
| `sites.png` | **Sites** tab | A site selected; response tracing on. |
| `requests.png` | **Requests** tab, *Requests* list | A few red rule-hit rows, one row selected. A taller window (about 960 px) shows more rows. |
| `top-ips.png` | **Requests** tab, *Top IPs* | *Last hour*, a blocked scanner near the top. |
| `blocked-ips.png` | **Requests** tab, *Blocked IPs* | *Show lifted and expired* ticked. |
| `blacklist-rules.png` | **Requests** tab, *Blacklist rules* | The common scanner rule, an allow-list and trusted proxies filled in. |
| `database.png` | **Database** tab, *Running now* | A blocked query selected so the details show. A taller window (about 1080 px) fits everything. |
| `slow-queries.png` | **Database** tab, *Slow queries* | A procedure selected so its details show. |
| `server.png` | **Server** tab | Window about 560 px tall; the three charts fill it. |
| `history.png` | **History** tab | *App pool*, a busy pool, *Last 7 days*. |
| `collectors.png` | **Collectors** tab | All sources visible. |
| `settings.png` | **Settings…** dialog | |
| `iis-logging.png` | **Set up IIS logging…** dialog (Requests tab) | |
| `block-ip.png` | **Block an IP address** dialog | An address and a note filled in. |

Tips:

- Capture just the window: <kbd>Alt</kbd>+<kbd>Print Screen</kbd>, or the Snipping Tool's window mode.
  The placeholders are 1440 × 900 at 100 % display scaling; larger captures look sharper on
  high-density screens.
- Check for anything private before publishing: real client IPs, host names, connection strings,
  SQL statements and customer or application names.
- Each feature page's screenshot is also its preview image when the page is shared on social media.
  The home page and the guides use `wwwroot/images/og-image.png` (1200 × 630), which shows the
  Overview mockup; regenerate it from the real screenshot too.
- Alt text and captions are in `Site/SiteMap.cs` (each page's main screenshot) and in the pages
  themselves (`<screenshot name="..." alt="...">Caption</screenshot>`). Update them if a real
  screenshot shows something different.

## Edit the content

| What | Where |
|---|---|
| Page titles, headings, meta descriptions, card summaries, menu order, main screenshot | `Site/SiteMap.cs`: the single list used by the layout, navigation, previous/next links, structured data and `sitemap.xml` |
| Feature pages | `Pages/Features/*.cshtml` |
| Guides | `Pages/Docs/*.cshtml` |
| Home page | `Pages/Index.cshtml` |
| FAQ (page and FAQ structured data) | `Site/Faq.cs` |
| Header, footer, `<head>` tags | `Pages/Shared/_Layout.cshtml` |
| Breadcrumb, side menu, previous/next | `Pages/Shared/_ArticleLayout.cshtml` |
| Styles (light and dark) | `wwwroot/css/site.css` |

To add a page, create the `.cshtml` file with an `@page "/route"` directive and add a `SitePage` entry
with the same path to `SiteMap.cs`.

## SEO

- One lowercase URL per page; uppercase and trailing-slash variants redirect permanently.
- A unique `<title>`, meta description and canonical link on every page, from `SiteMap.cs`.
- Open Graph and Twitter card tags, with each feature's screenshot as its preview image.
- JSON-LD structured data: `SoftwareApplication` on the home page, `BreadcrumbList` and
  `TechArticle` on feature and guide pages, `FAQPage` on the FAQ.
- `sitemap.xml` (with screenshot image entries) and `robots.txt`, generated from `SiteMap.cs`.
- Semantic HTML (one `h1` per page, ordered headings, landmarks, breadcrumbs), descriptive alt text,
  and `width`/`height` on every image so nothing shifts while loading.
- Fast: no web fonts or frameworks, lazy-loaded screenshots, Brotli/gzip compression, fingerprinted
  static files, and real 404 status codes for missing pages.

## Deploy

```bash
dotnet publish website/IISMonitor.Website -c Release -o out
```

Host the `out` folder like any ASP.NET Core app: on IIS with the
[ASP.NET Core Hosting Bundle](https://learn.microsoft.com/aspnet/core/host-and-deploy/iis/),
Azure App Service, or behind a reverse proxy on Linux. Deploy the publish output rather than the build
output: the pre-compressed static files are created at publish time. Set `Site__BaseUrl` to the public
address.
