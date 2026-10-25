using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

/// <summary>AutoCAD PaletteSet windows: classes deriving from (or wrapping) Autodesk.AutoCAD.Windows.PaletteSet.</summary>
internal static class PaletteScanner
{
    private static readonly Regex ClassDecl = Rx.Create(@"\bclass\s+(\w+)\s*(?::\s*([^{]+?))?\s*\{", RegexOptions.Compiled);
    private static readonly Regex NsDecl = Rx.Create(@"\bnamespace\s+([\w.]+)\s*[;{]", RegexOptions.Compiled);
    private static readonly Regex NewPalette = Rx.Create(@"\bnew\s+(?:[\w.]+\.)?PaletteSet\s*\(", RegexOptions.Compiled);
    private static readonly Regex GuidLit = Rx.Create(@"new\s+(?:System\.)?Guid\s*\(\s*""([0-9a-fA-F-]{36})""", RegexOptions.Compiled);
    private static readonly Regex AddCall = Rx.Create(@"\b([\w.]+)\s*\.\s*(AddVisual|Add)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ViewMember = Rx.Create(
        @"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?([\w.]*(?:View|UserControl|Panel|Control))\s+(\w+)\s*(?:\{|=|;)",
        RegexOptions.Compiled);

    public static IEnumerable<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        foreach (var f in files.Where(f => f.Lang == Lang.CSharp))
        {
            if (!f.Text.Contains("PaletteSet", StringComparison.Ordinal)) continue;
            var ct = new CodeText(f);
            var code = ct.Code;
            var nss = NsDecl.Matches(code).Select(m => (m.Index, m.Groups[1].Value)).ToList();
            foreach (Match m in ClassDecl.Matches(code))
            {
                var cls = m.Groups[1].Value;
                if (cls.EndsWith("Base")) continue;
                int open = m.Index + m.Length - 1;
                int close = CodeText.MatchBrace(code, open);
                if (close < 0) continue;
                var body = code.Substring(open, close - open + 1);
                var bases = m.Groups[2].Success ? m.Groups[2].Value : "";
                bool derives = Rx.IsMatch(bases, @"\bPaletteSet\w*|[\w.]*PaletteSet\b");
                var nm = NewPalette.Match(body);
                if (!derives && !nm.Success) continue;

                string ns = nss.LastOrDefault(n => n.Index < m.Index).Item2 ?? "";
                var full = ns.Length == 0 ? cls : ns + "." + cls;
                string? title = null;
                if (nm.Success)
                {
                    var args = CodeText.ArgsAt(body, nm.Index + nm.Length - 1, out _);
                    if (args != null)
                    {
                        var parts = CodeText.SplitArgs(args);
                        if (parts.Count > 0) title = CodeText.Literal(parts[0]);
                    }
                }
                var form = new UiForm
                {
                    Id = "palette." + full, Title = title ?? cls, Kind = UiFormKind.PaletteSet, Project = f.Project,
                    Source = ct.Ref(m.Index), Units = "auto",
                };
                form.ClassNames.Add(full);
                var g = GuidLit.Match(body);
                if (g.Success) form.Extra["guid"] = g.Groups[1].Value;
                if (bases.Length > 0) form.Extra["base"] = bases.Trim();

                var seen = new HashSet<string>();
                foreach (Match a in AddCall.Matches(body))
                {
                    bool visual = a.Groups[2].Value == "AddVisual";
                    if (!visual && !Rx.IsMatch(a.Groups[1].Value, "palette|PaletteSet|^ps$|^this$", RegexOptions.IgnoreCase)) continue;
                    var args = CodeText.ArgsAt(body, a.Index + a.Length - 1, out _);
                    if (args == null) continue;
                    var parts = CodeText.SplitArgs(args);
                    if (parts.Count < 2) continue;
                    var tab = CodeText.Literal(parts[0]);
                    string type; string name = "";
                    var nw = Rx.Match(parts[1], @"^new\s+([\w.]+)\s*\(");
                    if (nw.Success) type = FormScanner.SimpleName(nw.Groups[1].Value);
                    else if (Rx.IsMatch(parts[1], @"^[\w.]+$"))
                    {
                        name = parts[1].Split('.')[^1];
                        var decl = Rx.Match(body, @"\b([A-Z]\w*)\s+" + Regex.Escape(name) + @"\s*[=;{]");
                        type = decl.Success ? decl.Groups[1].Value : name;
                    }
                    else continue;
                    if (type is "var" or "object") continue;
                    if (!seen.Add(type + "|" + tab)) continue;
                    form.Controls.Add(new UiControl { Type = type, Name = name, Text = tab, Source = ct.Ref(open + a.Index) });
                }
                if (form.Controls.Count == 0)
                {
                    foreach (Match v in ViewMember.Matches(body))
                    {
                        var type = FormScanner.SimpleName(v.Groups[1].Value);
                        if (!seen.Add(type + "|")) continue;
                        form.Controls.Add(new UiControl { Type = type, Name = v.Groups[2].Value, Source = ct.Ref(open + v.Index) });
                    }
                }
                yield return form;
            }
        }
    }
}
