using IISMonitor.Website.Site;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Serve static files (and their compressed copies) from the build output in every environment, not
// only Development. Without it, running a Release build outside Development sends browsers an empty
// stylesheet. Published output has no manifest to load, so this changes nothing there.
builder.WebHost.UseStaticWebAssets();

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Screenshots>();
builder.Services.AddScoped<SiteContext>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddScoped<IContactMailer, ContactMailer>();
builder.Services.AddSingleton<ContactThrottle>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddResponseCompression(options =>
{
    // Pages hold no secrets, so compressing over HTTPS carries no BREACH risk.
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});

var app = builder.Build();

if (!app.Services.GetRequiredService<IOptions<EmailOptions>>().Value.IsConfigured)
    app.Logger.LogWarning("The contact form is off: set Email:To, Email:From and Email:Smtp:Host (or Email:PickupDirectory) in appsettings.json.");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStatusCodePagesWithReExecute("/not-found");

// Pages only: MapStaticAssets already serves static files compressed at publish time.
app.UseWhen(context => !Path.HasExtension(context.Request.Path.Value), branch => branch.UseResponseCompression());

// Google Analytics needs its own hosts, as Google documents for GA4 (google.com and doubleclick.net
// receive the Google signals hit); everything else stays same-origin.
var analytics = app.Services.GetRequiredService<IOptions<SiteOptions>>().Value.AnalyticsIdFor(app.Environment) is not null;
const string GoogleHosts = "https://*.google-analytics.com https://*.analytics.google.com https://*.googletagmanager.com https://*.g.doubleclick.net https://*.google.com";
var contentSecurityPolicy = analytics
    ? $"default-src 'self'; img-src 'self' data: {GoogleHosts}; style-src 'self'; " +
      "script-src 'self' https://*.googletagmanager.com; " +
      $"connect-src 'self' {GoogleHosts}; " +
      "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
    : "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

// One address per page: /Features/Overview/ redirects permanently to /features/overview.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is { Length: > 1 } && HttpMethods.IsGet(context.Request.Method) && !Path.HasExtension(path)
        && (path.EndsWith('/') || path.Any(char.IsUpper)))
    {
        var canonical = path.TrimEnd('/').ToLowerInvariant();
        context.Response.Redirect(context.Request.PathBase + (canonical.Length == 0 ? "/" : canonical) + context.Request.QueryString, permanent: true);
        return;
    }

    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers.ContentSecurityPolicy = contentSecurityPolicy;
    await next();
});

app.UseRouting();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapSeoEndpoints();

app.Run();
