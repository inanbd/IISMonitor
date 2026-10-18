using IISMonitor.Website.Site;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Screenshots>();
builder.Services.AddScoped<SiteContext>();
builder.Services.AddResponseCompression(options =>
{
    // Pages hold no secrets, so compressing over HTTPS carries no BREACH risk.
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStatusCodePagesWithReExecute("/not-found");

// Pages only: MapStaticAssets already serves static files compressed at publish time.
app.UseWhen(context => !Path.HasExtension(context.Request.Path.Value), branch => branch.UseResponseCompression());

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
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

app.UseRouting();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapSeoEndpoints();

app.Run();
