// The product's name, and the word "recorded" (any case, also inside a longer word), in documents people open (steps.md, steps.docx, steps.xlsx, the evidence workbook, the training document, the suite report): never.
// NO AutoCAD references in this file (linked into the engine test project).
using System.Text.RegularExpressions;

namespace Retrace.Engine.Util;

/// <summary>Keeps the product's name, and the word "recorded" (also "unrecorded", "prerecorded"), out of the documents a tester, trainee or customer opens (the recording itself, retrace.log, settings.json and the result files are not documents).
/// The program's own messages are worded without the name at their sources: nothing here rewrites text, so a layer, a palette or a test a person named "retrace..." or a prompt that says "recorded" reaches a document as it is.
/// <see cref="Guard"/> is a safety net: it passes a program message on unchanged and reports it through <see cref="OnLeak"/> (the plugin logs one line) when the name or the word is in it.
/// A test builds every document kind and searches every part for the name, so a later change that brings it back fails there.</summary>
public static class DocText
{
    static readonly Regex Name = Rx.Create(@"retrace", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Recorded = Rx.Create(@"recorded", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Called with a description when a program message with the name reached <see cref="Guard"/>. The plugin sets it to a log line.</summary>
    public static Action<string>? OnLeak { get; set; }

    /// <summary>True when <paramref name="text"/> has the product's name in any letter case.</summary>
    public static bool MentionsProduct(string? text) => !string.IsNullOrEmpty(text) && Name.IsMatch(text);

    /// <summary>True when <paramref name="text"/> has "recorded" in any letter case, also inside a longer word ("unrecorded"). Other forms of the verb ("recording", "record") are allowed.</summary>
    public static bool MentionsRecorded(string? text) => !string.IsNullOrEmpty(text) && Recorded.IsMatch(text);

    /// <summary>A program-written message on its way into a document: returned as it is; when it has the product's name or the word "recorded", <see cref="OnLeak"/> gets one line.</summary>
    public static string Guard(string? text, string where)
    {
        if (MentionsProduct(text)) { try { OnLeak?.Invoke($"{where}: a program message names the product: {text}"); } catch (Exception) { } }
        if (MentionsRecorded(text)) { try { OnLeak?.Invoke($"{where}: a program message says \"recorded\": {text}"); } catch (Exception) { } }
        return text ?? "";
    }

    /// <summary>A path as a document says it: a path under the tests folder (the default root has the product's name in it) is the test folder's name and the rest ("2026-11-18_0900_t\start.dwg"); any other path is a person's own and stays as it is.</summary>
    public static string UnderTests(string? path, string? testsFolder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(testsFolder)) return path ?? "";
        var root = testsFolder.TrimEnd('\\', '/').Replace('/', '\\') + "\\";
        var p = path.Replace('/', '\\');
        return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
    }

    /// <summary>The name of a test folder, without the path: documents say "2026-11-18_0900_demo", never "C:\temp\retrace\tests\2026-11-18_0900_demo" (the default root folder has the product's name in it).</summary>
    public static string FolderName(string? folder)
    {
        var t = (folder ?? "").TrimEnd('\\', '/');
        int i = Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/'));
        return i >= 0 ? t[(i + 1)..] : t;
    }
}
