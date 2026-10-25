using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// `settings.json` in the root folder (see <see cref="Paths"/>). A stored folder of "" means "use the default"; the
/// Tests / Captures / Docs properties give the effective path.
/// </summary>
public sealed class RetraceSettings
{
    /// <summary>The code, CUIX and DCL files the docs are built from.</summary>
    [JsonPropertyName("sourceFolder")] public string SourceFolder { get; set; } = "";
    /// <summary>Where the docs are written. "" = `docs` under the root.</summary>
    [JsonPropertyName("docsFolder")] public string DocsFolder { get; set; } = "";
    /// <summary>Where the recorded tests are kept. "" = `tests` under the root.</summary>
    [JsonPropertyName("testsFolder")] public string TestsFolder { get; set; } = "";
    /// <summary>Where the dialog captures are kept. "" = `captures` under the root.</summary>
    [JsonPropertyName("capturesFolder")] public string CapturesFolder { get; set; } = "";
    /// <summary>"" = the default editor (VS Code if found, else notepad.exe); else the program "Open in editor" starts in the guide palette.</summary>
    [JsonPropertyName("editorPath")] public string EditorPath { get; set; } = "";

    /// <summary>Take screenshots while recording (into the test folder's `screenshots\`). On by default.</summary>
    [JsonPropertyName("screenshots")] public bool Screenshots { get; set; } = true;

    /// <summary>Record which field values the user changed in a dialog (`dialog_values` events). On by default.</summary>
    [JsonPropertyName("dialogValues")] public bool DialogValues { get; set; } = true;

    /// <summary>RETRACERUN fills a dialog with the recorded values and closes it itself when it can (else a person does it). On by default.</summary>
    [JsonPropertyName("autoFillDialogs")] public bool AutoFillDialogs { get; set; } = true;

    /// <summary>Also write `steps.docx` (Word) next to steps.md when a recording is stopped. Off by default.</summary>
    [JsonPropertyName("exportDocx")] public bool ExportDocx { get; set; }

    /// <summary>Also write `steps.xlsx` (Excel) next to steps.md when a recording is stopped. Off by default.</summary>
    [JsonPropertyName("exportXlsx")] public bool ExportXlsx { get; set; }

    /// <summary>Where the Retrace ribbon tab goes: "" / "afterHome" (right of Home), "first", "last" or "after:&lt;tab id or title&gt;".</summary>
    [JsonPropertyName("ribbonTabPosition")] public string RibbonTabPosition { get; set; } = "";

    /// <summary>The root folder this instance was loaded from (not stored). Tests point it at a temp folder.</summary>
    [JsonIgnore] public string RootDir { get; set; } = Paths.Root;

    [JsonIgnore] public string FilePath => Paths.SettingsFile(RootDir);
    [JsonIgnore] public string DefaultTests => Paths.Tests(RootDir);
    [JsonIgnore] public string DefaultCaptures => Paths.Captures(RootDir);
    [JsonIgnore] public string DefaultDocs => Paths.Docs(RootDir);

    [JsonIgnore] public string Tests => Pick(TestsFolder, DefaultTests);
    [JsonIgnore] public string Captures => Pick(CapturesFolder, DefaultCaptures);
    [JsonIgnore] public string Docs => Pick(DocsFolder, DefaultDocs);
    [JsonIgnore] public string Source => Resolve(Clean(SourceFolder));
    [JsonIgnore] public string GuideIndex => Path.Combine(Docs, "guide", "index.md");

    /// <summary>The editor the owner configured, else <see cref="DefaultEditor()"/>.</summary>
    [JsonIgnore] public string Editor { get { var v = Clean(EditorPath); return v.Length == 0 ? DefaultEditor() : Environment.ExpandEnvironmentVariables(v); } }

    /// <summary>VS Code (`code` on PATH, or %LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe), else notepad.exe.</summary>
    public static string DefaultEditor() => DefaultEditor(File.Exists, Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string DefaultEditor(Func<string, bool> fileExists, string? pathVariable, string localAppData)
    {
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "code.cmd", "code.exe" })
            {
                try { var f = Path.Combine(dir.Trim().Trim('"'), name); if (fileExists(f)) return f; } catch (Exception) { }
            }
        var local = Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
        return fileExists(local) ? local : "notepad.exe";
    }

    static string Clean(string? s) => (s ?? "").Trim().Trim('"');
    string Pick(string? value, string dflt) { var v = Clean(value); return v.Length == 0 ? dflt : Resolve(Environment.ExpandEnvironmentVariables(v)); }

    /// <summary>A relative folder setting means "under the root folder", not under AutoCAD's current directory (which changes with every file dialog).</summary>
    string Resolve(string path) => path.Length == 0 || Path.IsPathRooted(path) ? path : Path.Combine(RootDir, path);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions ReadOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Never throws: a missing or broken file gives the defaults.</summary>
    public static RetraceSettings Load(string root = Paths.Root)
    {
        try
        {
            var file = Paths.SettingsFile(root);
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<RetraceSettings>(File.ReadAllText(file), ReadOptions) ?? new();
                s.RootDir = root;
                return s;
            }
        }
        catch (Exception) { }
        return new() { RootDir = root };
    }

    public bool Save(out string error)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json), new System.Text.UTF8Encoding(false));
            error = "";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Creates the root folder, the default sub folders and (first time) settings.json, so the owner can find them. Never throws.</summary>
    public static void EnsureHome(string root = Paths.Root)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Paths.Tests(root));
            Directory.CreateDirectory(Paths.Captures(root));
            Directory.CreateDirectory(Paths.Docs(root));
            if (!File.Exists(Paths.SettingsFile(root))) new RetraceSettings { RootDir = root }.Save(out _);
        }
        catch (Exception) { }
    }

    /// <summary>The effective tests / captures / docs folders exist after this (a configured custom folder is created too).</summary>
    public void EnsureFolders()
    {
        EnsureHome(RootDir);
        foreach (var d in new[] { Tests, Captures, Docs })
            try { Directory.CreateDirectory(d); } catch (Exception) { }
    }

    public RetraceSettings Clone() => (RetraceSettings)MemberwiseClone();

    /// <summary>Store "" when the text equals the default (or is "default" or "."), so a default stays a default.</summary>
    public static string Normalize(string text, string dflt)
    {
        var t = Clean(text);
        return t.Length == 0 || t.Equals("default", StringComparison.OrdinalIgnoreCase) || t == "." || string.Equals(t, dflt, StringComparison.OrdinalIgnoreCase) ? "" : t;
    }
}
