using System.Text.RegularExpressions;

namespace Retrace.AutoCad;

public sealed record DclDialog(string File, string Stem, string Name, int Line)
{
    /// <summary>Same id retrace gives the form (DclScanner): dcl.&lt;file name without extension&gt;.&lt;dialog name&gt;.</summary>
    public string FormId => $"dcl.{Stem}.{Name}";
}

/// <summary>Lists the dialogs in DCL files: top-level `name : dialog {` blocks, like retrace's DclScanner.</summary>
public static class DclList
{
    static readonly Regex Comments = Rx.Create(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Strings = Rx.Create(@"""(?:\\.|[^""\\])*""", RegexOptions.Compiled);
    static readonly Regex Head = Rx.Create(@"\G\s*([A-Za-z_][\w]*)\s*:\s*([A-Za-z_][\w]*)\s*\{", RegexOptions.Compiled);

    public static List<DclDialog> InFile(string path)
    {
        // blank out comments and string contents but keep offsets, so line numbers stay right and braces in text don't count
        var text = File.ReadAllText(path);
        text = Comments.Replace(text, m => Blank(m.Value));
        text = Strings.Replace(text, m => "\"" + Blank(m.Value[1..^1]) + "\"");
        var stem = Path.GetFileNameWithoutExtension(path);
        var list = new List<DclDialog>();
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (depth == 0)
            {
                var m = Head.Match(text, i);
                if (m.Success)
                {
                    if (m.Groups[2].Value == "dialog")
                        list.Add(new DclDialog(path, stem, m.Groups[1].Value, 1 + text.AsSpan(0, m.Groups[1].Index).Count('\n')));
                    i = m.Index + m.Length - 1; // at the '{'
                    depth = 1;
                    continue;
                }
            }
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && depth > 0) depth--;
        }
        return list;
    }

    /// <summary>A .dcl file, or every .dcl under a folder (sorted, so runs are repeatable).</summary>
    public static List<DclDialog> In(string fileOrFolder) =>
        (Directory.Exists(fileOrFolder)
            ? Directory.EnumerateFiles(fileOrFolder, "*.dcl", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { fileOrFolder })
        .SelectMany(InFile).ToList();

    static string Blank(string s) => Rx.Replace(s, @"[^\n]", " ");
}
