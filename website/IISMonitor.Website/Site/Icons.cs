using Microsoft.AspNetCore.Html;

namespace IISMonitor.Website.Site;

/// <summary>Small line icons, drawn on a 24×24 grid and inlined so they cost no extra requests.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Shapes = new()
    {
        ["overview"] = """<path d="M3 3v18h18"/><path d="m7 15 4-5 3 3 5-7"/>""",
        ["layers"] = """<path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/>""",
        ["globe"] = """<circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3Z"/>""",
        ["list"] = """<path d="M9 6h12M9 12h12M9 18h12"/><path d="M4 6h.01M4 12h.01M4 18h.01" stroke-width="2.5"/>""",
        ["shield"] = """<path d="M12 3 5 6v6c0 4.4 3 7.7 7 9 4-1.3 7-4.6 7-9V6l-7-3Z"/><path d="m9 12 2 2 4-4"/>""",
        ["database"] = """<ellipse cx="12" cy="5.5" rx="7.5" ry="2.5"/><path d="M4.5 5.5v13c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5v-13"/><path d="M4.5 12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5"/>""",
        ["server"] = """<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01" stroke-width="2.5"/>""",
        ["clock"] = """<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>""",
        ["pulse"] = """<path d="M3 12h4l2.5-6 5 12 2.5-6h4"/>""",
        ["sliders"] = """<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>""",
        ["download"] = """<path d="M12 4v11"/><path d="m7 10 5 5 5-5"/><path d="M5 20h14"/>""",
        ["flow"] = """<rect x="3" y="4" width="7" height="6" rx="1.5"/><rect x="14" y="14" width="7" height="6" rx="1.5"/><path d="M6.5 10v4a3 3 0 0 0 3 3H14"/>""",
        ["lock"] = """<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>""",
        ["wrench"] = """<path d="M14.5 6.5a4 4 0 0 0 5 5L12 19a2.1 2.1 0 0 1-3-3l7.5-7.5"/><path d="M14.5 6.5 17 4a4 4 0 0 1 3 3l-2.5 2.5"/>""",
        ["help"] = """<circle cx="12" cy="12" r="9"/><path d="M9.6 9.3a2.5 2.5 0 0 1 4.8.9c0 1.7-2.4 2.2-2.4 3.8"/><path d="M12 17h.01" stroke-width="2.5"/>""",
        ["grid"] = """<rect x="4" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="4" y="13.5" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="13.5" width="6.5" height="6.5" rx="1.5"/>""",
        ["arrow-right"] = """<path d="M5 12h14"/><path d="m13 6 6 6-6 6"/>""",
        ["arrow-left"] = """<path d="M19 12H5"/><path d="m11 6-6 6 6 6"/>""",
        ["check"] = """<path d="m5 12.5 4.5 4.5L19 7.5"/>""",
        ["code"] = """<path d="m8 7-5 5 5 5"/><path d="m16 7 5 5-5 5"/>""",
        ["mail"] = """<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m4 7 8 6 8-6"/>""",
        ["zoom"] = """<circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.4-4.4M11 8.5v5M8.5 11h5"/>""",
    };

    public static IHtmlContent Svg(string name, string cssClass = "icon") =>
        Shapes.TryGetValue(name, out var shape)
            ? new HtmlString($"""<svg class="{cssClass}" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">{shape}</svg>""")
            : HtmlString.Empty;

    /// <summary>The IIS Monitor mark: a chart line on the dashboard's navy.</summary>
    public static IHtmlContent Logo(string cssClass = "logo-mark") =>
        new HtmlString($"""<svg class="{cssClass}" viewBox="0 0 32 32" width="32" height="32" aria-hidden="true" focusable="false"><rect width="32" height="32" rx="8" fill="#1f4e79"/><path d="M7 21.5l5.5-6 4 3.5 8-9.5" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="24.5" cy="9.5" r="2.2" fill="#7cc4ff"/></svg>""");
}
