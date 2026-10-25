using System.Diagnostics;
using System.Windows;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>
/// Shows a recording's test script (`steps.md` plus its `screenshots` pictures) in a small modeless window. The Guide palette cannot:
/// its web view serves one root, the docs folder, and a script lives next to the session. The window serves the script's folder
/// instead, with the same WebPreview (so images resolve).
/// </summary>
public static class ScriptViewer
{
    static Window? _win;
    static WebPreview? _web;
    static string _dir = "";

    /// <summary>True if the window is showing the script (WebView2 may still be starting); false means the caller should shell-open the file.</summary>
    public static bool Open(string mdPath)
    {
        try
        {
            if (!File.Exists(mdPath)) return false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(mdPath))!;
            if (_win == null)
            {
                var web = new WebPreview();
                web.MarkdownLinkClicked = (f, _) => web.Navigate(GuideLinks.ToUrl(f, _dir));
                var win = new Window { Title = "Retrace test script", Width = 920, Height = 720, Content = web, ShowInTaskbar = false };
                win.Closed += (_, _) => { try { _web?.Close(); } catch (Exception) { } _web = null; _win = null; };
                _win = win; _web = web;
                _dir = dir;
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessWindow(win);
                _ = StartAsync(web, dir, mdPath);
            }
            else
            {
                _dir = dir;
                _web!.SetRoot(dir);
                _web.Navigate(GuideLinks.ToUrl(mdPath, dir));
                _win.Activate();
            }
            return true;
        }
        catch (Exception ex) { Log.Error("script viewer", ex); return false; }
    }

    static async Task StartAsync(WebPreview web, string dir, string mdPath)
    {
        try
        {
            await web.InitAsync(dir);
            web.Navigate(GuideLinks.ToUrl(mdPath, dir));
        }
        catch (Exception ex)
        {
            // no WebView2 runtime: close the window and open the file in the default app instead
            Log.Error("script viewer webview2", ex);
            try { _win?.Close(); } catch (Exception) { }
            try { Process.Start(new ProcessStartInfo(mdPath) { UseShellExecute = true }); } catch (Exception e2) { Log.Error("script shell open", e2); }
        }
    }
}
