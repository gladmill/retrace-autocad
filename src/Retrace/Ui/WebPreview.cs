using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>
/// WebView2 wrapper. The output folder root is mapped to https://guide.local/ so relative links and images (SVG, PNG) resolve;
/// requests for *.md are intercepted and answered with Markdig HTML (from the editor buffer when the page is being edited).
/// Everything WebView2 lives here so a missing runtime is one try/catch in the caller.
/// </summary>
sealed class WebPreview : Grid
{
    readonly WebView2 _web = new();
    CoreWebView2Environment? _env;
    string _root = "";
    string? _allowNext;
    string? _pendingUrl;
    bool _ready;

    /// <summary>Returns the unsaved editor text for a file, or null to read the file from disk.</summary>
    public Func<string, string?> TextFor { get; set; } = _ => null;
    /// <summary>The user clicked a link to another .md (file, fragment). The navigation itself is cancelled.</summary>
    public Action<string, string?>? MarkdownLinkClicked { get; set; }
    public bool IsReady => _ready;

    public WebPreview()
    {
        Children.Add(_web);
    }

    /// <summary>Throws if the WebView2 runtime is missing; the caller shows a message.</summary>
    public async Task InitAsync(string root)
    {
        var userData = Paths.WebViewCache();
        Directory.CreateDirectory(userData);
        _env = await CoreWebView2Environment.CreateAsync(null, userData);
        await _web.EnsureCoreWebView2Async(_env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.AddWebResourceRequestedFilter("https://" + GuideLinks.Host + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindow;
        SetRoot(root);
        _ready = true;
        if (_pendingUrl != null) { var u = _pendingUrl; _pendingUrl = null; Navigate(u); }
    }

    public void SetRoot(string root)
    {
        if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase) && _ready) return;
        _root = root;
        Directory.CreateDirectory(root);
        // No SetVirtualHostNameToFolderMapping: in AutoCAD 2026's WebView2 the mapping answered .md requests itself (raw text,
        // wrong encoding) before WebResourceRequested could render them. OnRequested serves every guide.local file instead.
    }

    public void Navigate(string url)
    {
        if (!_ready) { _pendingUrl = url; return; }
        _allowNext = url;
        _web.CoreWebView2.Navigate(url);
    }

    public void Reload()
    {
        if (!_ready) return;
        _allowNext = _web.CoreWebView2.Source;
        _web.CoreWebView2.Reload();
    }

    /// <summary>Replaces the rendered page body without reloading (keeps the scroll position). False if the page is not ready for it.</summary>
    public async Task<bool> UpdateBodyAsync(string markdown)
    {
        if (!_ready) return false;
        try
        {
            var json = JsonSerializer.Serialize(MarkdownPage.Body(markdown));
            var r = await _web.CoreWebView2.ExecuteScriptAsync("(function(){var c=document.getElementById('content');if(!c)return false;c.innerHTML=" + json + ";return true;})()");
            return r == "true";
        }
        catch (Exception) { return false; }
    }

    public void Close()
    {
        try { _web.Dispose(); } catch (Exception) { }
    }

    void OnRequested(object? s, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
            var file = GuideLinks.ToFilePath(uri, _root);
            if (file == null) return;
            if (!uri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                e.Response = File.Exists(file)
                    ? _env!.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: " + ContentType(file) + "\r\nCache-Control: no-store")
                    : _env!.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "Content-Type: text/plain");
                return;
            }
            string html;
            var buffer = TextFor(file);
            if (buffer != null) html = MarkdownPage.Page(buffer, Path.GetFileName(file));
            else if (File.Exists(file)) html = MarkdownPage.Page(File.ReadAllText(file), Path.GetFileName(file));
            else html = MarkdownPage.Message("Page not found", "`" + Path.GetFileName(file) + "` does not exist. Build the guide again, or use Refresh.");
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
            e.Response = _env!.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception ex) { Log.Error("md request", ex); }
    }

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".txt" or ".log" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    void OnNavigationStarting(object? s, CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            if (_allowNext != null && string.Equals(e.Uri, _allowNext, StringComparison.Ordinal)) { _allowNext = null; return; }
            Route(e.Uri, () => e.Cancel = true);
        }
        catch (Exception ex) { Log.Error("navigation", ex); }
    }

    void OnNewWindow(object? s, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try { Route(e.Uri, () => { }); } catch (Exception ex) { Log.Error("new window", ex); }
    }

    /// <summary>.md on the guide host: stay in the palette; other http(s)/mailto: default browser; same-page anchors and assets: let the web view do it.</summary>
    void Route(string uriText, Action cancel)
    {
        switch (GuideLinks.Classify(uriText))
        {
            case LinkKind.External:
                cancel();
                if (Uri.TryCreate(uriText, UriKind.Absolute, out var ext) && (ext.Scheme is "http" or "https" or "mailto"))
                    Process.Start(new ProcessStartInfo(ext.AbsoluteUri) { UseShellExecute = true });
                break;
            case LinkKind.Markdown:
                cancel();
                if (Uri.TryCreate(uriText, UriKind.Absolute, out var md) && GuideLinks.ToFilePath(md, _root) is { } file)
                    MarkdownLinkClicked?.Invoke(file, md.Fragment.TrimStart('#'));
                break;
        }
    }
}
