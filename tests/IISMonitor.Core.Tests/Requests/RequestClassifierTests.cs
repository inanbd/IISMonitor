using IISMonitor.Core.Requests;
using Xunit;

namespace IISMonitor.Core.Tests.Requests;

public class RequestClassifierTests
{
    private static RequestKind Kind(string method, string stem, int status,
        string? dest = null, string? mode = null, string? xrw = null, string? ct = null, string? cd = null,
        string? accept = null, string? reqCt = null, string? hx = null, string? hxBoosted = null,
        string? turboFrame = null, string? msAjax = null) =>
        RequestClassifier.Classify(new RequestSignals(method, stem, status)
        {
            SecFetchDest = dest,
            SecFetchMode = mode,
            XRequestedWith = xrw,
            ContentType = ct,
            ContentDisposition = cd,
            Accept = accept,
            RequestContentType = reqCt,
            HxRequest = hx,
            HxBoosted = hxBoosted,
            TurboFrame = turboFrame,
            MicrosoftAjax = msAjax,
        });

    // ---- Fetch metadata (HTTPS browsers) ----

    [Fact] public void Navigation_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/", 200, "document", "navigate", ct: "text/html; charset=utf-8"));

    [Fact] public void Iis_plus_encoded_content_type_still_parses() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/products", 200, "document", "navigate", ct: "text/html;+charset=utf-8"));

    [Fact] public void Fetch_json_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/api/orders", 200, "empty", "cors", ct: "application/json; charset=utf-8"));

    [Fact] public void Image_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/img/logo.png", 200, "image", "no-cors", ct: "image/png"));

    [Fact] public void Svg_keeps_its_plus() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/icons/x", 200, ct: "image/svg+xml"));

    [Fact] public void Not_modified_style_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/css/site.css", 304, "style", "no-cors"));

    [Fact] public void Clicked_pdf_link_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/files/report.pdf", 200, "document", "navigate", ct: "application/pdf"));

    [Fact] public void Attachment_wins() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/export/42", 200, "document", "navigate",
            ct: "application/octet-stream", cd: "attachment; filename=\"orders.xlsx\""));

    [Fact] public void Attachment_on_api_path_wins() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/api/files/7", 200, "empty", "cors", ct: "application/pdf", cd: "attachment;+filename=a.pdf"));

    [Fact] public void Chromium_download_attribute_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/files/data", 200, "empty", "navigate"));

    [Fact] public void Image_opened_in_tab_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/img/a.png", 200, "document", "navigate", ct: "image/png"));

    [Fact] public void Json_opened_in_tab_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/api/status", 200, "document", "navigate", ct: "application/json"));

    [Fact] public void Video_range_request_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/media/intro.mp4", 206, "video", "no-cors", ct: "video/mp4"));

    [Fact] public void Pdf_viewer_range_request_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/docs/big.pdf", 206, "empty", "no-cors", ct: "application/pdf"));

    [Fact] public void Csp_report_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("POST", "/csp-report", 204, "report", "no-cors"));

    [Fact] public void Service_worker_script_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/sw.js", 200, "serviceworker", "same-origin", ct: "text/javascript"));

    [Fact] public void Unknown_dest_is_ignored() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/", 200, "speculationrules-v9", "no-cors", ct: "text/html"));

    [Fact] public void Dest_casing_is_tolerated() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/x", 200, "Image", "no-cors"));

    // ---- No fetch metadata (plain HTTP, old browsers, non-browser clients) ----

    [Fact] public void Jquery_over_http_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/Home/GetData", 200, xrw: "XMLHttpRequest", ct: "application/json"));

    [Fact] public void Android_webview_package_name_is_not_xhr() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/", 200, xrw: "com.example.app", ct: "text/html"));

    [Fact] public void Dash_means_absent() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/about", 200, "-", "-", "-", "text/html", "-"));

    [Fact] public void Missing_image_404_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/img/missing.png", 404, ct: "text/html"));

    [Fact] public void Missing_page_404_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/no-such-page", 404, ct: "text/html"));

    [Fact] public void Curl_msi_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/setup/agent.msi", 200));

    [Fact] public void Resumed_iso_download_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/iso/win.iso", 206, ct: "application/octet-stream"));

    [Fact] public void Octet_stream_extensionless_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/download/123", 200, ct: "application/octet-stream"));

    [Fact] public void Office_type_is_download() =>
        Assert.Equal(RequestKind.Download, Kind("GET", "/r/1", 200, ct: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

    [Fact] public void Json_post_without_response_type_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/orders", 201, reqCt: "application/json"));

    [Fact] public void Form_post_redirect_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("POST", "/Account/Login", 302, reqCt: "application/x-www-form-urlencoded"));

    [Fact] public void Accept_json_on_extensionless_304_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/data/today", 304, accept: "application/json, text/plain, */*"));

    [Fact] public void Problem_json_error_falls_back_to_path() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/api/orders/9", 404, ct: "application/problem+json"));

    [Fact] public void Event_stream_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/events", 200, ct: "text/event-stream"));

    [Fact] public void Robots_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("GET", "/robots.txt", 200, ct: "text/plain"));

    [Fact] public void Acme_challenge_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("GET", "/.well-known/acme-challenge/abc", 200, ct: "text/plain"));

    [Fact] public void Dotenv_probe_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("GET", "/.env", 404, ct: "text/html"));

    [Fact] public void Php_probe_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/wp-login.php", 404, ct: "text/html"));

    [Fact] public void Pwa_manifest_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/manifest.json", 304));

    // ---- Methods and statuses ----

    [Fact] public void Cors_preflight_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("OPTIONS", "/api/orders", 204, "empty", "cors"));

    [Fact] public void Options_probe_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("OPTIONS", "/", 200));

    [Fact] public void WebSocket_upgrade_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/ws", 101));

    [Fact] public void WebSocket_mode_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/chat", 400, "empty", "websocket"));

    [Fact] public void Head_is_classified_by_url() =>
        Assert.Equal(RequestKind.PageView, Kind("HEAD", "/", 200, ct: "text/html"));

    [Fact] public void Put_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("PUT", "/items/1", 204));

    [Fact] public void Delete_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("DELETE", "/items/1", 204));

    [Fact] public void Trace_is_other() =>
        Assert.Equal(RequestKind.Other, Kind("TRACE", "/", 405));

    // ---- ASP.NET specifics ----

    [Fact] public void UpdatePanel_async_postback_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Default.aspx", 200, xrw: "XMLHttpRequest", ct: "text/plain; charset=utf-8", msAjax: "Delta=true"));

    [Fact] public void UpdatePanel_over_https_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Default.aspx", 200, "empty", "cors", ct: "text/plain; charset=utf-8"));

    [Fact] public void WebForms_full_postback_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("POST", "/Default.aspx", 200, "document", "navigate", ct: "text/html; charset=utf-8"));

    [Fact] public void Page_method_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Default.aspx/GetTime", 200));

    [Fact] public void Asmx_soap_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Services/Data.asmx", 200, ct: "text/xml; charset=utf-8"));

    [Fact] public void Asmx_json_method_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Services/Data.asmx/GetItems", 500, ct: "text/html"));

    [Fact] public void Wcf_svc_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/Service.svc/rest/items", 200, ct: "application/xml"));

    [Fact] public void WebResource_axd_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/WebResource.axd", 304));

    [Fact] public void ScriptResource_axd_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/ScriptResource.axd", 200, ct: "application/x-javascript"));

    // ---- SignalR and Blazor ----

    [Fact] public void Blazor_negotiate_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/_blazor/negotiate", 200, ct: "application/json"));

    [Fact] public void Blazor_websocket_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/_blazor", 101));

    [Fact] public void Long_polling_octet_stream_is_api_not_download() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/_blazor", 200, ct: "application/octet-stream"));

    [Fact] public void Custom_hub_negotiate_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/hubs/chat/negotiate", 200));

    [Fact] public void Classic_signalr_poll_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/signalr/poll", 200, xrw: "XMLHttpRequest", ct: "application/json"));

    [Fact] public void Classic_signalr_without_headers_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/signalr/send", 200));

    [Fact] public void Blazor_framework_dll_is_static() =>
        Assert.Equal(RequestKind.Static, Kind("GET", "/_framework/System.Private.CoreLib.dll", 200, ct: "application/octet-stream"));

    [Fact] public void Blazor_enhanced_navigation_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/counter", 200, "empty", "cors", ct: "text/html; charset=utf-8",
            accept: "text/html; blazor-enhanced-nav=on"));

    // ---- htmx and Turbo ----

    [Fact] public void Htmx_partial_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/contacts/1/edit", 200, "empty", "cors", ct: "text/html", hx: "true"));

    [Fact] public void Htmx_boosted_link_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/contacts", 200, "empty", "cors", ct: "text/html", hx: "true", hxBoosted: "true"));

    [Fact] public void Htmx_over_http_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/contacts/1", 200, ct: "text/html", hx: "true"));

    [Fact] public void Turbo_drive_visit_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/posts", 200, "empty", "cors", ct: "text/html", accept: "text/html, application/xhtml+xml"));

    [Fact] public void Turbo_drive_visit_with_iis_plus_encoding_is_page_view() =>
        Assert.Equal(RequestKind.PageView, Kind("GET", "/posts", 200, "empty", "cors", ct: "text/html", accept: "text/html,+application/xhtml+xml"));

    [Fact] public void Turbo_frame_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("GET", "/posts/1/comments", 200, "empty", "cors", ct: "text/html",
            accept: "text/html, application/xhtml+xml", turboFrame: "comments"));

    [Fact] public void Turbo_stream_form_submit_is_api() =>
        Assert.Equal(RequestKind.XhrApi, Kind("POST", "/posts", 200, "empty", "cors", ct: "text/vnd.turbo-stream.html; charset=utf-8",
            accept: "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"));

    // ---- Method groups ----

    [Theory]
    [InlineData("GET", MethodGroup.Get)]
    [InlineData("get", MethodGroup.Get)]
    [InlineData("POST", MethodGroup.Post)]
    [InlineData("HEAD", MethodGroup.Other)]
    [InlineData("OPTIONS", MethodGroup.Other)]
    [InlineData("PROPFIND", MethodGroup.Other)]
    [InlineData(null, MethodGroup.Other)]
    public void Methods_are_grouped(string? method, MethodGroup expected) =>
        Assert.Equal(expected, RequestClassifier.GroupMethod(method));
}
