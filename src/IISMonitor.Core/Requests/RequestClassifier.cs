using System.Collections.Frozen;

namespace IISMonitor.Core.Requests;

/// <summary>What a request was for, as shown on the Requests tab.</summary>
public enum RequestKind : byte
{
    PageView,
    XhrApi,
    Static,
    Download,
    Other,
}

/// <summary>The method filter on the Requests tab. HEAD, OPTIONS, PUT, DELETE ... are Other.</summary>
public enum MethodGroup : byte
{
    Get,
    Post,
    Other,
}

/// <summary>
/// The per-request values the classifier can use. Only Method, UriStem and Status come from the
/// standard W3C fields; every header comes from an IIS custom field and is optional: pass null
/// when it is not logged. IIS writes "-" for a header that was absent; that counts as null too.
/// </summary>
public readonly record struct RequestSignals(string? Method, string? UriStem, int Status)
{
    // Request headers.
    public string? SecFetchDest { get; init; }
    public string? SecFetchMode { get; init; }
    public string? XRequestedWith { get; init; }
    public string? Accept { get; init; }
    public string? RequestContentType { get; init; }
    public string? HxRequest { get; init; }
    public string? HxBoosted { get; init; }
    public string? TurboFrame { get; init; }
    public string? MicrosoftAjax { get; init; }

    // Response headers.
    public string? ContentType { get; init; }
    public string? ContentDisposition { get; init; }
}

/// <summary>
/// Sorts a logged request into page view / XHR-API / static / download / other. Pure and
/// allocation-free; evidence is used strongest first:
/// <list type="number">
/// <item>method and status that settle it alone (101, OPTIONS, PUT/PATCH/DELETE, CONNECT/TRACE)</item>
/// <item>framework asset paths (/_framework/, /_content/, WebResource.axd, ScriptResource.axd)</item>
/// <item>Content-Disposition: attachment on a 2xx response</item>
/// <item>Sec-Fetch-Dest / Sec-Fetch-Mode (sent by browsers to HTTPS and localhost URLs only)</item>
/// <item>X-Requested-With: XMLHttpRequest, HX-Request, X-MicrosoftAjax</item>
/// <item>response Content-Type, trusted only on 2xx and 304 (error and redirect bodies are generic HTML)</item>
/// <item>API and realtime paths (/api/, .asmx, .svc, /signalr/, /_blazor, /negotiate, ...)</item>
/// <item>application/octet-stream on a 2xx GET</item>
/// <item>URL extension and a few well-known file names</item>
/// <item>Accept, then the request Content-Type</item>
/// <item>extensionless GET/HEAD/POST is a page view; anything else is Other</item>
/// </list>
/// </summary>
public static class RequestClassifier
{
    public static MethodGroup GroupMethod(string? method)
    {
        var m = Clean(method);
        return Is(m, "GET") ? MethodGroup.Get : Is(m, "POST") ? MethodGroup.Post : MethodGroup.Other;
    }

    public static RequestKind Classify(in RequestSignals r)
    {
        var method = Clean(r.Method);
        var path = r.UriStem is { Length: > 0 } stem ? stem.AsSpan() : "/".AsSpan();
        var dest = Clean(r.SecFetchDest);
        var mode = Clean(r.SecFetchMode);
        var isGet = method.IsEmpty || Is(method, "GET") || Is(method, "HEAD");
        var isPost = Is(method, "POST");

        // 1. Settled by method or status alone.
        if (r.Status == 101 || Is(mode, "websocket"))
            return RequestKind.XhrApi;
        if (Is(method, "OPTIONS"))
            return Is(mode, "cors") ? RequestKind.XhrApi : RequestKind.Other; // CORS preflight vs. scanner probe
        if (Is(method, "PUT") || Is(method, "PATCH") || Is(method, "DELETE"))
            return RequestKind.XhrApi; // HTML forms can only send GET and POST
        if (!isGet && !isPost)
            return RequestKind.Other; // CONNECT, TRACE, PROPFIND, ...

        // 2. Framework asset folders are static whatever they are fetched with.
        if (IsFrameworkAssetPath(path))
            return RequestKind.Static;

        var success = r.Status is >= 200 and < 300;
        var media = success || r.Status == 304 ? MediaKindOf(MediaType(r.ContentType)) : Media.None;

        // 3. The server said "save this".
        if (success && IsAttachment(r.ContentDisposition))
            return RequestKind.Download;

        var fileKind = KindOfFile(path);

        // 4. Fetch metadata.
        if (!dest.IsEmpty)
        {
            switch (Lower(dest))
            {
                case "document" or "iframe" or "frame" or "fencedframe":
                    return media switch
                    {
                        Media.Download or Media.Binary => RequestKind.Download,
                        Media.Static => RequestKind.Static,
                        Media.Api => RequestKind.XhrApi, // someone opened a JSON URL in a tab
                        Media.Page => RequestKind.PageView,
                        _ => fileKind is RequestKind.Download or RequestKind.Static ? fileKind.Value : RequestKind.PageView,
                    };
                case "empty":
                    if (Is(mode, "navigate"))
                        return RequestKind.Download; // Chromium sends empty + navigate for <a download>
                    if (isGet && (media == Media.Download || (media == Media.None && fileKind == RequestKind.Download)))
                        return RequestKind.Download; // PDF viewer range requests, fetch() of a file
                    return IsSoftNavigation(r, isGet) ? RequestKind.PageView : RequestKind.XhrApi;
                case "object" or "embed":
                    return media is Media.Download or Media.Binary || fileKind == RequestKind.Download
                        ? RequestKind.Download
                        : RequestKind.Static;
                case "report":
                    return RequestKind.Other; // CSP / NEL / Reporting API reports
                case "webidentity":
                    return RequestKind.XhrApi;
                case "audio" or "audioworklet" or "font" or "image" or "json" or "manifest" or "paintworklet"
                    or "script" or "serviceworker" or "sharedworker" or "style" or "text" or "track" or "video"
                    or "worker" or "xslt":
                    return RequestKind.Static;
                default:
                    break; // unknown value: the spec says ignore it
            }
        }

        // 5. Markers set by script libraries. Compare the value: Android WebView used to put the app's
        //    package name in X-Requested-With on every request.
        if (Is(Clean(r.XRequestedWith), "XMLHttpRequest") || Is(Clean(r.HxRequest), "true")
            || Clean(r.MicrosoftAjax).StartsWith("Delta=true", StringComparison.OrdinalIgnoreCase))
        {
            return IsSoftNavigation(r, isGet) ? RequestKind.PageView : RequestKind.XhrApi;
        }

        // 6. What the server sent back.
        switch (media)
        {
            case Media.Page:
                return IsSoftNavigation(r, isGet) || !IsPartialHtmlRequest(r) ? RequestKind.PageView : RequestKind.XhrApi;
            case Media.Api:
                return RequestKind.XhrApi;
            case Media.Static:
                return RequestKind.Static;
            case Media.Download:
                return RequestKind.Download;
        }

        // 7. API and realtime endpoints (SignalR long polling answers with application/octet-stream).
        if (IsApiPath(path, isPost))
            return RequestKind.XhrApi;

        // 8. A successful binary body that isn't an API is a file.
        if (media == Media.Binary && isGet && success)
            return RequestKind.Download;

        // 9. URL extension and well-known names.
        if (fileKind is not null)
            return fileKind.Value;

        // 10. What the client asked for.
        var accept = FirstMediaRange(r.Accept);
        if (!accept.IsEmpty)
        {
            if (Is(accept, "text/html") || Is(accept, "application/xhtml+xml"))
                return RequestKind.PageView;
            if (Is(accept, "application/json") || Is(accept, "text/event-stream") || Is(accept, "text/vnd.turbo-stream.html")
                || accept.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return RequestKind.XhrApi;
            if (accept.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || Is(accept, "text/css"))
                return RequestKind.Static;
        }

        var requestType = MediaType(r.RequestContentType);
        if (Is(requestType, "application/json") || requestType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || Is(requestType, "text/xml") || Is(requestType, "application/xml") || Is(requestType, "application/soap+xml"))
            return RequestKind.XhrApi;

        // 11. Routed URLs without an extension (MVC, Razor Pages, Blazor, ...). A form POST is a page view.
        return HasExtension(path) ? RequestKind.Other : RequestKind.PageView;
    }

    // ---- Fetch-based navigations (Turbo Drive, htmx hx-boost, Blazor enhanced navigation) ----

    private static bool IsSoftNavigation(in RequestSignals r, bool isGet)
    {
        if (!isGet)
            return false;
        if (Is(Clean(r.HxBoosted), "true"))
            return true;
        if (!Clean(r.TurboFrame).IsEmpty)
            return false;

        Span<char> buffer = stackalloc char[96];
        var accept = CompactAccept(r.Accept, buffer);
        return accept.Contains("blazor-enhanced-nav=on", StringComparison.OrdinalIgnoreCase)
            || Is(accept, "text/html,application/xhtml+xml"); // Turbo Drive's Accept for a visit
    }

    /// <summary>A text/html answer to a request that is known to come from script (Turbo Frame, htmx).</summary>
    private static bool IsPartialHtmlRequest(in RequestSignals r) =>
        !Clean(r.TurboFrame).IsEmpty || Is(Clean(r.HxRequest), "true");

    /// <summary>Accept with spaces removed and IIS's "+" for a space after "," or ";" dropped.</summary>
    private static ReadOnlySpan<char> CompactAccept(string? accept, Span<char> buffer)
    {
        var value = Clean(accept);
        var length = 0;
        for (var i = 0; i < value.Length && length < buffer.Length; i++)
        {
            var c = value[i];
            if (c == ' ' || (c == '+' && i > 0 && value[i - 1] is ',' or ';'))
                continue;
            buffer[length++] = c;
        }

        return buffer[..length];
    }

    // ---- Paths ----

    private static bool IsFrameworkAssetPath(ReadOnlySpan<char> path) =>
        path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase)
        || LastSegment(path).Equals("WebResource.axd", StringComparison.OrdinalIgnoreCase)
        || LastSegment(path).Equals("ScriptResource.axd", StringComparison.OrdinalIgnoreCase);

    private static bool IsApiPath(ReadOnlySpan<char> path, bool isPost)
    {
        var rest = path;
        while (!rest.IsEmpty)
        {
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? default : rest[(slash + 1)..];
            if (segment.IsEmpty)
                continue;

            if (ApiSegmentLookup.Contains(segment)
                || segment.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".svc", StringComparison.OrdinalIgnoreCase))
                return true;

            // ASP.NET AJAX page methods: POST /Page.aspx/MethodName
            if (isPost && !rest.IsEmpty && segment.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                return true;

            // ASP.NET Core SignalR: POST {hub}/negotiate
            if (rest.IsEmpty && segment.Equals("negotiate", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static RequestKind? KindOfFile(ReadOnlySpan<char> path)
    {
        var name = LastSegment(path);
        if (OtherNameLookup.Contains(name) || path.Contains("/.well-known/", StringComparison.OrdinalIgnoreCase))
            return RequestKind.Other;
        if (StaticNameLookup.Contains(name))
            return RequestKind.Static;

        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return null;

        var ext = name[(dot + 1)..];
        if (StaticExtLookup.Contains(ext))
            return RequestKind.Static;
        if (DownloadExtLookup.Contains(ext))
            return RequestKind.Download;
        if (PageExtLookup.Contains(ext))
            return RequestKind.PageView;
        if (ApiExtLookup.Contains(ext))
            return RequestKind.XhrApi;
        if (ext.Equals("axd", StringComparison.OrdinalIgnoreCase))
            return RequestKind.Static; // ChartImg.axd, Telerik.Web.UI.WebResource.axd, ...
        return RequestKind.Other;
    }

    private static bool HasExtension(ReadOnlySpan<char> path)
    {
        var name = LastSegment(path);
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1;
    }

    private static ReadOnlySpan<char> LastSegment(ReadOnlySpan<char> path)
    {
        path = path.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    // ---- Media types ----

    private enum Media : byte { None, Page, Api, Static, Download, Binary }

    private static Media MediaKindOf(ReadOnlySpan<char> type)
    {
        if (type.IsEmpty)
            return Media.None;
        if (Is(type, "text/html") || Is(type, "application/xhtml+xml"))
            return Media.Page;
        if (Is(type, "application/octet-stream"))
            return Media.Binary;
        if (Is(type, "application/manifest+json"))
            return Media.Static;
        if (Is(type, "application/json") || Is(type, "text/json") || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || Is(type, "text/event-stream") || Is(type, "application/x-ndjson") || Is(type, "text/vnd.turbo-stream.html")
            || type.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
            return Media.Api;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || StaticTypeLookup.Contains(type))
            return Media.Static;
        if (DownloadTypeLookup.Contains(type)
            || type.StartsWith("application/vnd.openxmlformats-officedocument.", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("application/vnd.oasis.opendocument.", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("application/vnd.ms-", StringComparison.OrdinalIgnoreCase))
            return Media.Download;
        return Media.None; // text/plain, text/xml, application/xml, ...: decide from the URL
    }

    /// <summary>"text/html;+charset=utf-8" -> "text/html". Keeps the "+" inside "image/svg+xml".</summary>
    private static ReadOnlySpan<char> MediaType(string? value)
    {
        var span = Clean(value);
        var semicolon = span.IndexOf(';');
        return (semicolon < 0 ? span : span[..semicolon]).Trim(" +\t\"");
    }

    private static ReadOnlySpan<char> FirstMediaRange(string? accept)
    {
        var span = Clean(accept);
        var comma = span.IndexOfAny(',', ';');
        return (comma < 0 ? span : span[..comma]).Trim(" +\t");
    }

    private static bool IsAttachment(string? contentDisposition)
    {
        var span = Clean(contentDisposition);
        var end = span.IndexOfAny(';', ' ', '+');
        return Is(end < 0 ? span : span[..end], "attachment");
    }

    // ---- Small helpers ----

    /// <summary>Trims spaces, quotes and IIS's "+" padding; "-" (IIS: not present) becomes empty.</summary>
    private static ReadOnlySpan<char> Clean(string? value)
    {
        if (value is null)
            return default;
        var span = value.AsSpan().Trim(" \t+");
        return span is "-" ? default : span;
    }

    private static bool Is(ReadOnlySpan<char> value, string expected) =>
        value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sec-Fetch-* tokens are lowercase on the wire; tolerate odd casing without allocating for the common case.</summary>
    private static string Lower(ReadOnlySpan<char> token)
    {
        foreach (var known in KnownDestinations)
        {
            if (token.Equals(known, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return string.Empty;
    }

    private static readonly string[] KnownDestinations =
    [
        "document", "empty", "image", "script", "style", "font", "iframe", "frame", "fencedframe", "audio", "video",
        "track", "manifest", "worker", "sharedworker", "serviceworker", "audioworklet", "paintworklet", "object",
        "embed", "report", "xslt", "json", "text", "webidentity",
    ];

    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> StaticExt = Set(
        "css", "js", "mjs", "cjs", "map",
        "png", "jpg", "jpeg", "jpe", "jfif", "pjpeg", "gif", "webp", "avif", "apng", "svg", "svgz", "ico", "cur",
        "bmp", "tif", "tiff", "heic", "heif", "jxl",
        "woff", "woff2", "ttf", "otf", "eot",
        "mp3", "m4a", "aac", "oga", "ogg", "opus", "wav", "weba", "flac", "mid", "midi",
        "mp4", "m4v", "webm", "ogv", "mov", "3gp", "m3u8", "mpd", "ts", "m4s",
        "vtt", "srt", "wasm", "webmanifest", "swf", "xap");

    private static readonly FrozenSet<string> DownloadExt = Set(
        "pdf", "doc", "docx", "docm", "dot", "dotx", "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx",
        "ppt", "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odt", "ods", "odp", "odg", "rtf", "csv", "tsv",
        "epub", "mobi", "xps", "oxps", "vsd", "vsdx", "mpp", "pub", "one", "ics", "vcf", "eml", "msg",
        "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "cab", "z",
        "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dmg", "pkg", "deb", "rpm", "apk", "aab", "ipa",
        "iso", "img", "vhd", "vhdx", "bin", "jar", "nupkg",
        "sql", "bak", "mdb", "accdb", "avi", "wmv", "mkv", "flv");

    private static readonly FrozenSet<string> PageExt = Set(
        "aspx", "asp", "htm", "html", "xhtml", "shtml", "shtm", "stm", "php", "jsp", "jspx", "cfm", "cshtml", "vbhtml");

    private static readonly FrozenSet<string> ApiExt = Set("json", "ashx", "asmx", "svc");

    private static readonly FrozenSet<string> ApiSegments = Set(
        "api", "_api", "graphql", "odata", "wp-json", "signalr", "_blazor", "xmlrpc.php");

    private static readonly FrozenSet<string> StaticNames = Set("manifest.json", "site.webmanifest", "browserconfig.xml");

    private static readonly FrozenSet<string> OtherNames = Set(
        "robots.txt", "sitemap.xml", "ads.txt", "app-ads.txt", "humans.txt", "security.txt",
        "crossdomain.xml", "clientaccesspolicy.xml");

    private static readonly FrozenSet<string> StaticTypes = Set(
        "text/css", "text/javascript", "application/javascript", "application/x-javascript", "application/ecmascript",
        "text/ecmascript", "application/wasm", "text/vtt", "application/x-font-ttf", "application/x-font-woff",
        "application/font-woff", "application/font-woff2", "application/font-sfnt", "application/x-mpegurl",
        "application/vnd.apple.mpegurl", "application/dash+xml", "application/x-shockwave-flash");

    private static readonly FrozenSet<string> DownloadTypes = Set(
        "application/pdf", "application/zip", "application/x-zip-compressed", "application/x-7z-compressed",
        "application/x-rar-compressed", "application/vnd.rar", "application/gzip", "application/x-gzip",
        "application/x-tar", "application/x-bzip2", "application/x-xz", "application/zstd", "application/msword",
        "application/rtf", "text/rtf", "text/csv", "application/csv", "application/epub+zip", "application/x-msdownload",
        "application/x-msi", "application/vnd.microsoft.portable-executable", "application/x-apple-diskimage",
        "application/vnd.android.package-archive", "application/java-archive", "application/x-iso9660-image",
        "application/oxps", "text/calendar", "text/vcard", "application/vnd.visio");

    // Span lookups (FrozenSet alternate lookup, .NET 9+), so classification never allocates.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> StaticExtLookup = StaticExt.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> DownloadExtLookup = DownloadExt.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> PageExtLookup = PageExt.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ApiExtLookup = ApiExt.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ApiSegmentLookup = ApiSegments.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> StaticNameLookup = StaticNames.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> OtherNameLookup = OtherNames.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> StaticTypeLookup = StaticTypes.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> DownloadTypeLookup = DownloadTypes.GetAlternateLookup<ReadOnlySpan<char>>();
}
