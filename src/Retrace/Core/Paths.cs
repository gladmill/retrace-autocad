namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the test project). The one place that knows where Retrace keeps its files.
// Every function takes the root, so tests build the same layout in a temp folder; the production root is the constant below.

/// <summary>
/// The folders and files on disk, all under <see cref="Root"/>:
/// <code>
/// C:\temp\retrace\settings.json  retrace.log  tests\  captures\  docs\  cache\webview\  cache\native\
/// </code>
/// The Tests, Captures and Docs folders can be moved with settings (<see cref="RetraceSettings"/>); the rest stays under the root.
/// </summary>
public static class Paths
{
    /// <summary>Where Retrace keeps everything by default. A constant on purpose: no environment variable, no setting.</summary>
    public const string Root = @"C:\temp\retrace";

    public const string SettingsFileName = "settings.json";
    public const string LogFileName = "retrace.log";

    public static string SettingsFile(string root = Root) => Path.Combine(root, SettingsFileName);
    /// <summary>Errors and warnings of the plugin.</summary>
    public static string LogFile(string root = Root) => Path.Combine(root, LogFileName);
    public static string Tests(string root = Root) => Path.Combine(root, "tests");
    public static string Captures(string root = Root) => Path.Combine(root, "captures");
    public static string Docs(string root = Root) => Path.Combine(root, "docs");
    public static string Cache(string root = Root) => Path.Combine(root, "cache");
    /// <summary>WebView2's user data folder.</summary>
    public static string WebViewCache(string root = Root) => Path.Combine(Cache(root), "webview");
    /// <summary>Where the native WebView2 loader is extracted to (one sub folder per version).</summary>
    public static string NativeCache(string root = Root) => Path.Combine(Cache(root), "native");
}
