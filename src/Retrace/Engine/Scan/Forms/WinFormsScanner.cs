using System.Text;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

/// <summary>Reads InitializeComponent() of WinForms designer code (C# and VB.NET).</summary>
internal static class WinFormsScanner
{
    private const RegexOptions Ci = RegexOptions.Compiled | RegexOptions.IgnoreCase;
    private static readonly Regex CsInit = Rx.Create(@"\bvoid\s+InitializeComponent\s*\(\s*\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex VbInit = Rx.Create(@"\bSub\s+InitializeComponent\s*\(\s*\)", Ci);
    private static readonly Regex VbEndSub = Rx.Create(@"^\s*End\s+Sub\b", Ci | RegexOptions.Multiline);
    private static readonly Regex CsNs = Rx.Create(@"\bnamespace\s+([\w.]+)\s*[;{]", RegexOptions.Compiled);
    private static readonly Regex VbNs = Rx.Create(@"^\s*Namespace\s+([\w.]+)", Ci | RegexOptions.Multiline);
    private static readonly Regex CsClass = Rx.Create(@"\b(?:class|struct)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex VbClass = Rx.Create(@"^\s*(?:(?:Public|Friend|Partial|Private|Protected|MustInherit|NotInheritable)\s+)*Class\s+(\w+)", Ci | RegexOptions.Multiline);

    private static readonly Regex Create = Rx.Create(@"^(?:this|Me)\.(\w+)\s*=\s*(?:new|New)\s+([\w.]+(?:<[^>]*>)?)\s*\(", Ci | RegexOptions.Singleline);
    private static readonly Regex Assign = Rx.Create(@"^(?:this|Me)\.(?:(\w+)\.)?(\w+)\s*=\s*(.+)$", Ci | RegexOptions.Singleline);
    private static readonly Regex AddCtl = Rx.Create(@"^(?:this|Me)\.(?:([\w.]+?)\.)?(Controls|TabPages|Items|DropDownItems)\.(Add|AddRange)\s*\((.*)\)\s*$", Ci | RegexOptions.Singleline);
    private static readonly Regex Event = Rx.Create(@"^(?:this|Me)\.(?:(\w+)\.)?(\w+)\s*\+=\s*(.+)$", Ci | RegexOptions.Singleline);
    private static readonly Regex AddHandler = Rx.Create(@"^AddHandler\s+(?:Me\.)?(?:(\w+)\.)?(\w+)\s*,\s*(?:New\s+[\w.]+\s*\(\s*)?AddressOf\s+(?:Me\.)?(\w+)", Ci | RegexOptions.Singleline);
    private static readonly Regex Handles = Rx.Create(@"\bSub\s+(\w+)\s*\([^)]*\)\s*Handles\s+([^\r\n]+)", Ci);
    private static readonly Regex Ref = Rx.Create(@"(?:this|Me)\.(\w+)", Ci);
    private static readonly Regex Pair = Rx.Create(@"\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)", RegexOptions.Compiled);

    private static readonly HashSet<string> NotControls = new(StringComparer.Ordinal)
    {
        "ComponentResourceManager", "Container", "Timer", "ToolTip", "ImageList", "ErrorProvider", "BindingSource",
        "NotifyIcon", "BackgroundWorker", "DataSet", "HelpProvider", "PrintDocument", "FileSystemWatcher",
    };

    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "Size", "SizeF", "Point", "PointF", "Rectangle", "Padding", "Font", "Color", "Icon", "Cursor", "Bitmap", "Image",
        "Guid", "Version", "DateTime", "TimeSpan", "Margins", "Decimal", "Int32", "Padding",
    };

    private sealed record Handler(string Control, string Event, string Method);

    public static IEnumerable<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        // VB "Handles" clauses can live in the non-designer half of a partial class.
        var vbHandles = new Dictionary<string, List<Handler>>();
        foreach (var f in files.Where(f => f.Lang == Lang.VbNet))
        {
            var ct = new CodeText(f);
            foreach (Match m in Handles.Matches(ct.Code))
            {
                var cls = LastBefore(VbClass, ct.Code, m.Index);
                if (cls == null) continue;
                foreach (var part in m.Groups[2].Value.Split(','))
                {
                    var hm = Rx.Match(part.Trim(), @"^(?:Me\.)?(\w+)\.(\w+)$");
                    if (!hm.Success) continue;
                    var key = f.Project + "|" + cls;
                    if (!vbHandles.TryGetValue(key, out var l)) vbHandles[key] = l = new();
                    l.Add(new Handler(hm.Groups[1].Value, hm.Groups[2].Value, m.Groups[1].Value));
                }
            }
        }

        foreach (var f in files.Where(f => f.Lang is Lang.CSharp or Lang.VbNet))
        {
            bool vb = f.Lang == Lang.VbNet;
            if (!f.Text.Contains("InitializeComponent", StringComparison.Ordinal)) continue;
            var ct = new CodeText(f);
            var code = ct.Code;
            int bodyStart, bodyEnd;
            if (!vb)
            {
                var m = CsInit.Match(code);
                if (!m.Success) continue;
                bodyStart = m.Index + m.Length;
                int close = CodeText.MatchBrace(code, m.Index + m.Length - 1);
                bodyEnd = close < 0 ? code.Length : close;
            }
            else
            {
                var m = VbInit.Match(code);
                if (!m.Success) continue;
                bodyStart = m.Index + m.Length;
                var e = VbEndSub.Match(code, bodyStart);
                bodyEnd = e.Success ? e.Index : code.Length;
            }

            var cls = LastBefore(vb ? VbClass : CsClass, code, bodyStart, out int classOffset);
            if (cls == null) continue;
            var ns = LastBefore(vb ? VbNs : CsNs, code, bodyStart);
            var full = string.IsNullOrEmpty(ns) ? cls : ns + "." + cls;

            var form = ParseBody(f, ct, code, bodyStart, bodyEnd, vb, full, classOffset);
            if (form == null) continue;
            if (vb && vbHandles.TryGetValue(f.Project + "|" + cls, out var hs))
                ApplyHandles(form, hs);
            yield return form;
        }
    }

    private static string? LastBefore(Regex re, string code, int offset) => LastBefore(re, code, offset, out _);

    private static string? LastBefore(Regex re, string code, int offset, out int at)
    {
        string? last = null; at = 0;
        foreach (Match m in re.Matches(code))
        {
            if (m.Index >= offset) break;
            last = m.Groups[1].Value; at = m.Index;
        }
        return last;
    }

    private static void ApplyHandles(UiForm form, List<Handler> hs)
    {
        var byName = new Dictionary<string, UiControl>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<UiControl> cs) { foreach (var c in cs) { if (c.Name.Length > 0) byName[c.Name] = c; Walk(c.Children); } }
        Walk(form.Controls);
        foreach (var h in hs)
        {
            var text = $"{h.Event} -> {h.Method}";
            if (byName.TryGetValue(h.Control, out var c)) { if (!c.Events.Contains(text)) c.Events.Add(text); }
            else if (h.Control is "MyBase" or "Me")
                form.Extra["events"] = form.Extra.TryGetValue("events", out var e) ? e + "; " + text : text;
        }
    }

    private static UiForm? ParseBody(SourceFile f, CodeText ct, string code, int start, int end, bool vb, string full, int classOffset)
    {
        var controls = new Dictionary<string, UiControl>(StringComparer.Ordinal);
        var order = new List<UiControl>();
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal); // child -> parent name ("" = form)
        var childOrder = new List<string>();
        string? title = null, formName = null, formFont = null;
        int? cw = null, ch = null;
        var formEvents = new List<string>();
        bool sawAdd = false;

        void AddEvent(string target, string ev, string method)
        {
            var text = $"{ev} -> {method}";
            if (target.Length == 0) { formEvents.Add(text); return; }
            if (controls.TryGetValue(target, out var c)) c.Events.Add(text);
        }

        foreach (var (stmt, off) in SplitStatements(code, start, end, vb))
        {
            var s = stmt.Trim();
            if (s.Length == 0) continue;
            Match m;
            if ((m = Create.Match(s)).Success && !ValueTypes.Contains(FormScanner.SimpleName(m.Groups[2].Value)))
            {
                var type = FormScanner.SimpleName(m.Groups[2].Value);
                if (NotControls.Contains(type) || type.EndsWith("Dialog") || type.EndsWith("Style") || type.EndsWith("Column")) continue;
                var c = new UiControl { Name = m.Groups[1].Value, Type = type, Source = ct.Ref(off) };
                controls[c.Name] = c; order.Add(c);
                continue;
            }
            if ((m = AddCtl.Match(s)).Success)
            {
                sawAdd = true;
                var parent = m.Groups[1].Success ? m.Groups[1].Value.Split('.')[0] : "";
                foreach (Match r in Ref.Matches(m.Groups[4].Value))
                {
                    var child = r.Groups[1].Value;
                    if (!controls.ContainsKey(child) || child == parent) continue;
                    parentOf[child] = parent; childOrder.Add(child);
                }
                continue;
            }
            if ((m = AddHandler.Match(s)).Success)
            {
                AddEvent(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                continue;
            }
            if ((m = Event.Match(s)).Success)
            {
                var hm = Rx.Match(m.Groups[3].Value, @"(?:this\.|Me\.)?(\w+)\s*\)?\s*$");
                if (hm.Success) AddEvent(m.Groups[1].Value, m.Groups[2].Value, hm.Groups[1].Value);
                continue;
            }
            if ((m = Assign.Match(s)).Success)
            {
                var target = m.Groups[1].Value; var prop = m.Groups[2].Value; var val = m.Groups[3].Value.Trim();
                if (target.Length == 0)
                {
                    if (prop == "Text") title = CodeText.Literal(val) ?? title;
                    else if (prop == "Name") formName = CodeText.Literal(val) ?? formName;
                    else if (prop == "Font")
                    {
                        // new System.Drawing.Font("Segoe UI", 9F, ...) -> "9 Segoe UI"
                        var fm = Rx.Match(val, @"Font\(\s*""([^""]+)""\s*,\s*([\d.]+)");
                        if (fm.Success) formFont = fm.Groups[2].Value + " " + fm.Groups[1].Value;
                    }
                    else if (prop is "ClientSize" or "Size")
                    {
                        var p = Pair.Match(val);
                        if (p.Success && (prop == "ClientSize" || cw == null)) { cw = int.Parse(p.Groups[1].Value); ch = int.Parse(p.Groups[2].Value); }
                    }
                    continue;
                }
                if (!controls.TryGetValue(target, out var c)) continue;
                switch (prop)
                {
                    case "Location": { var p = Pair.Match(val); if (p.Success) { c.X = int.Parse(p.Groups[1].Value); c.Y = int.Parse(p.Groups[2].Value); } break; }
                    case "Size": { var p = Pair.Match(val); if (p.Success) { c.W = int.Parse(p.Groups[1].Value); c.H = int.Parse(p.Groups[2].Value); } break; }
                    case "Text": c.Text = CodeText.Literal(val) ?? c.Text; break;
                    case "Name": c.Name = CodeText.Literal(val) ?? c.Name; break;
                    case "Dock": c.Extra["dock"] = val[(val.LastIndexOf('.') + 1)..]; break;
                    case "AutoSize": if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) c.Extra["autosize"] = "true"; break;
                    case "Multiline": if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) c.Extra["multiline"] = "true"; break;
                }
            }
        }
        if (order.Count == 0 && cw == null) return null;

        var simple = full[(full.LastIndexOf('.') + 1)..];
        var form = new UiForm
        {
            Id = "winform." + full, Title = title ?? formName ?? simple, Kind = UiFormKind.WinForm,
            Project = f.Project, Source = ct.Ref(classOffset), Width = cw, Height = ch, Units = "px",
        };
        form.ClassNames.Add(full);
        if (formFont != null) form.Extra["font"] = formFont;
        if (formEvents.Count > 0) form.Extra["events"] = string.Join("; ", formEvents);

        if (!sawAdd)
            form.Controls.AddRange(order);
        else
        {
            var placed = new HashSet<string>();
            foreach (var name in childOrder)
            {
                if (!placed.Add(name)) continue;
                var parent = parentOf[name];
                if (parent.Length == 0) form.Controls.Add(controls[name]);
                else if (controls.TryGetValue(parent, out var p)) p.Children.Add(controls[name]);
            }
        }
        // a control that is never added to anything (components, menus' context strips) is left out: found less, never wrong
        return form;
    }

    // ------------------------------------------------------------ statement splitting

    private static IEnumerable<(string Text, int Offset)> SplitStatements(string code, int start, int end, bool vb)
    {
        if (vb)
        {
            var sb = new StringBuilder(); int stmtStart = -1; int pos = start;
            while (pos < end)
            {
                int nl = code.IndexOf('\n', pos);
                if (nl < 0 || nl > end) nl = end;
                var line = code[pos..nl].TrimEnd('\r');
                var trimmed = line.TrimEnd();
                if (stmtStart < 0 && trimmed.Trim().Length > 0) stmtStart = pos + (line.Length - line.TrimStart().Length);
                bool cont = trimmed.EndsWith(" _") || trimmed.EndsWith("\t_");
                if (cont) trimmed = trimmed[..^1];
                if (stmtStart >= 0) sb.Append(trimmed.Trim()).Append(cont ? " " : "");
                if (!cont && stmtStart >= 0) { yield return (sb.ToString(), stmtStart); sb.Clear(); stmtStart = -1; }
                pos = nl + 1;
            }
            if (stmtStart >= 0) yield return (sb.ToString(), stmtStart);
            yield break;
        }
        int depth = 0, begin = start;
        for (int i = start; i < end; i++)
        {
            char c = code[i];
            if (c == '"')
            {
                bool verbatim = i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@'));
                i++;
                while (i < end)
                {
                    if (verbatim) { if (code[i] == '"') { if (i + 1 < end && code[i + 1] == '"') { i += 2; continue; } break; } }
                    else { if (code[i] == '\\') { i += 2; continue; } if (code[i] == '"') break; }
                    i++;
                }
            }
            else if (c == '\'')
            {
                i++;
                while (i < end && code[i] != '\'') { if (code[i] == '\\') i++; i++; }
            }
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ';' && depth <= 0)
            {
                var t = code[begin..i];
                int lead = t.Length - t.TrimStart().Length;
                yield return (t, begin + lead);
                begin = i + 1; depth = 0;
            }
        }
    }
}
