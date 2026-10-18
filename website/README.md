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
| `SourceUrl` | Source repository, set to `https://github.com/inanbd/IISMonitor`. It adds the GitHub logo to the header, a **Get IIS Monitor on GitHub** section with the clone command to the home page, links in the footer and the install guide, and, for a GitHub repository, links to its Issues. Leave empty to hide them all. |
| `Publisher` | Company or person shown in the footer copyright and in the structured data; set to `inanbd`, the copyright holder in `LICENSE`. |
| `License` | The license IIS Monitor is released under, set to `MIT`. It makes the site say "free and open source", link the repository's `LICENSE` file, and mark the app as free in the structured data. Leave empty if the code isn't open source. |

## Contact form and email

The **Contact** page (`/contact`) emails each message to you. The message comes from the address in
`Email:From`, with the visitor in **Reply-To**, so your mail program's *Reply* answers the visitor. The
site stores nothing: messages go straight out by email, and logs record only the topic.

Settings, in the `Email` section of `appsettings.json`:

| Setting | Purpose |
|---|---|
| `To` | Who receives messages. Several addresses can be separated by commas. |
| `From`, `FromName` | The sender. Use an address your SMTP server is allowed to send from, ideally on your own domain, or SPF/DMARC checks may send the messages to spam. |
| `SubjectPrefix` | Put in front of every subject, such as `[IIS Monitor] Feature request: Ada Lovelace`, for mail filters. |
| `MaxMessagesPerHour` | How many messages one visitor (IP address) can send per hour. 5 by default. |
| `Smtp:Host`, `Smtp:Port` | Your SMTP server, such as `smtp.example.com` and `587`. |
| `Smtp:Security` | `StartTls` (usually port 587), `SslOnConnect` (usually port 465), `Auto`, `StartTlsWhenAvailable` or `None`. |
| `Smtp:UserName`, `Smtp:Password` | Sign-in for the SMTP server; leave the user name empty if it needs none. |
| `Smtp:TimeoutSeconds` | How long to wait for the server. 30 by default. |
| `PickupDirectory` | When set, messages are written to this folder as `.eml` files instead of being sent. Development uses `mail-pickup` (see `appsettings.Development.json`), so you can try the form without an SMTP server. |

The form is switched off, with a notice on the page and a warning in the log at startup, until `To`,
`From` and either `Smtp:Host` or `PickupDirectory` are set.

**Keep the password out of `appsettings.json`**, which is committed to the repository:

- Development: `dotnet user-secrets set "Email:Smtp:Password" "..." --project website/IISMonitor.Website`
- Production: set the environment variable `Email__Smtp__Password` (in IIS, under the site's
  Configuration Editor → `system.webServer/aspNetCore` → `environmentVariables`, or in `web.config`), or
  your host's secret store. Any other setting can be overridden the same way, such as `Email__Smtp__Host`.

Most mail providers and transactional email services (Amazon SES, Mailgun, SendGrid, Postmark, Brevo
and others) offer SMTP submission on port 587 with STARTTLS. Microsoft 365 and Google Workspace
increasingly restrict password-based SMTP sign-in; check your provider's current guidance, or use a
transactional service's SMTP relay.

Spam protection, built in: an anti-forgery token, a hidden field that only bots fill in, a check that
the form wasn't sent within seconds of loading, the hourly limit per visitor, and validation of every
field. Bots get the same "sent" page as people, so they learn nothing. Behind a reverse proxy or load
balancer, configure ASP.NET Core's forwarded headers so the limit counts real visitors rather than
the proxy; IIS's ASP.NET Core Module already passes the client's address through.

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
| Contact page and its email | `Pages/Contact.cshtml(.cs)`, `Site/ContactMailer.cs`, `Site/EmailOptions.cs` |
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
Azure App Service, or behind a reverse proxy on Linux. Set `Site__BaseUrl` to the public address and the
`Email__*` settings described above.

Deploy the publish output: it holds properly compressed copies of the CSS and JavaScript. Running the
build output (`bin/Release/...`) also works in any environment, because the site loads the build's
static-asset manifest, but its compressed copies are barely compressed.
