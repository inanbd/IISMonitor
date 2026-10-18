using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace IISMonitor.Website.Site;

/// <summary>
/// &lt;screenshot name="overview" alt="..."&gt;Caption&lt;/screenshot&gt; renders a figure for an image in
/// wwwroot/images/screenshots, with its real width and height and a cache-busting version, and a
/// link that opens it full size.
/// </summary>
[HtmlTargetElement("screenshot", TagStructure = TagStructure.NormalOrSelfClosing)]
public sealed class ScreenshotTagHelper(Screenshots screenshots, IFileVersionProvider versions) : TagHelper
{
    /// <summary>File name in wwwroot/images/screenshots, with or without extension.</summary>
    public string Name { get; set; } = "";

    public string Alt { get; set; } = "";

    /// <summary>Caption; the element's content is used when this is not set.</summary>
    public string? Caption { get; set; }

    /// <summary>Extra CSS class for the figure: "wide" (full container width) or "dialog" (a small window).</summary>
    public string? Variant { get; set; }

    /// <summary>The page's main image: load it at once instead of lazily.</summary>
    public bool Priority { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var e = HtmlEncoder.Default;
        output.TagName = "figure";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(Variant) ? "shot" : $"shot shot-{Variant}");

        // Child content is already encoded by Razor; the attribute is plain text.
        var caption = Caption is not null ? e.Encode(Caption) : (await output.GetChildContentAsync()).GetContent().Trim();
        if (screenshots.Find(Name) is not { } file)
        {
            output.Content.SetHtmlContent($"""<div class="shot-missing">Screenshot not found: {e.Encode($"{Screenshots.Folder}/{Name}")}</div>""");
            return;
        }

        var url = e.Encode(versions.AddFileVersionToPath(ViewContext.HttpContext.Request.PathBase, file.Path));
        var size = file is { Width: { } w, Height: { } h } ? $""" width="{w}" height="{h}" """ : " ";
        var loading = Priority ? """fetchpriority="high" """ : """loading="lazy" """;
        var html = $"""<a class="shot-link" href="{url}" data-zoom><img src="{url}" alt="{e.Encode(Alt)}"{size}{loading}decoding="async"><span class="shot-hint">{Icons.Svg("zoom")}<span class="visually-hidden">View full size</span></span></a>""";
        if (caption.Length > 0)
            html += $"<figcaption>{caption}</figcaption>";
        output.Content.SetHtmlContent(html);
    }
}
