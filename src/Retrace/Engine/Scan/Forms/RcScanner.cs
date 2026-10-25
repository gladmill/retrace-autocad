using System.Text;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

/// <summary>Win32 resource scripts (.rc): DIALOG / DIALOGEX blocks, then binds MFC dialog classes from C++.</summary>
internal static class RcScanner
{
    private static readonly Regex DialogHead = Rx.Create(@"^\s*([A-Za-z_]\w*|\d+)\s+DIALOG(EX)?\b(.*)$", RegexOptions.Compiled);
    private static readonly Regex Caption = Rx.Create(@"^\s*CAPTION\s+(""(?:[^""]|"""")*"")", RegexOptions.Compiled);
    private static readonly Regex DialogStyle = Rx.Create(@"^\s*STYLE\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Font = Rx.Create(@"^\s*FONT\s+(\d+)\s*,\s*(""(?:[^""]|"""")*"")", RegexOptions.Compiled);
    private static readonly Regex Keyword = Rx.Create(@"^\s*([A-Z][A-Z0-9]*)\b(.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HashSet<string> TextFirst = new()
    {
        "PUSHBUTTON", "DEFPUSHBUTTON", "LTEXT", "RTEXT", "CTEXT", "GROUPBOX", "CHECKBOX", "AUTOCHECKBOX",
        "AUTO3STATE", "STATE3", "RADIOBUTTON", "AUTORADIOBUTTON", "ICON",
    };
    private static readonly HashSet<string> IdFirst = new() { "EDITTEXT", "COMBOBOX", "LISTBOX", "SCROLLBAR", "BEDIT", "HEDIT", "IEDIT" };

    public static IEnumerable<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        var forms = new List<UiForm>();
        foreach (var f in files.Where(f => f.Lang == Lang.Rc))
            forms.AddRange(ScanFile(f));
        BindClasses(files, forms);
        return forms;
    }

    private static List<UiForm> ScanFile(SourceFile f)
    {
        var result = new List<UiForm>();
        var lines = CodeText.StripCComments(f.Text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var images = ImageResources(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = DialogHead.Match(lines[i]);
            if (!m.Success) continue;
            var id = m.Groups[1].Value;
            int headerLine = i + 1;
            // coordinates: rest of the line, else following lines
            var coordText = Rx.Replace(m.Groups[3].Value, @"\b(DISCARDABLE|MOVEABLE|PURE|IMPURE|LOADONCALL|PRELOAD|FIXED)\b", "");
            int j = i + 1;
            var nums = Ints(coordText);
            while (nums.Count < 4 && j < lines.Length && !StartsBody(lines[j]) && !lines[j].TrimStart().StartsWith("#"))
            { nums.AddRange(Ints(lines[j])); j++; }
            if (nums.Count < 4) continue;
            var form = new UiForm
            {
                Id = $"rc.{f.Project}.{id}", Title = id, Kind = UiFormKind.Win32Dialog, Project = f.Project,
                Source = new SourceRef(f.RelPath, headerLine), ResourceId = id,
                Width = nums[2], Height = nums[3], Units = "dlu",
            };
            // style lines until BEGIN / {
            string styleText = "";
            bool hasCaption = false;
            for (; j < lines.Length; j++)
            {
                var t = lines[j].Trim();
                if (t == "BEGIN" || t.StartsWith("{") || t.StartsWith("BEGIN ")) break;
                var cm = Caption.Match(lines[j]);
                if (cm.Success) { form.Title = Unquote(cm.Groups[1].Value); hasCaption = true; }
                var sm = DialogStyle.Match(lines[j]);
                if (sm.Success)
                {
                    var st = new StringBuilder(sm.Groups[1].Value.Trim());
                    while (st.ToString().TrimEnd().EndsWith("|") && j + 1 < lines.Length) st.Append(' ').Append(lines[++j].Trim());
                    styleText = st.ToString();
                }
                var fm = Font.Match(lines[j]);
                if (fm.Success) form.Extra["font"] = fm.Groups[1].Value + " " + Unquote(fm.Groups[2].Value);
                if (t.Length > 0 && !t.StartsWith("#") && Rx.IsMatch(t, @"^[A-Za-z_]\w*\s+(DIALOG|DIALOGEX)\b")) { j--; break; }
            }
            if (j >= lines.Length) break;
            // window style (frame/caption decide the chrome the renderer draws); a CAPTION statement implies WS_CAPTION
            if (hasCaption) styleText += (styleText.Length > 0 ? " | " : "") + "WS_CAPTION";
            if (styleText.Length > 0) form.Extra["style"] = styleText;
            j++;
            // controls until END / }
            while (j < lines.Length)
            {
                var t = lines[j].Trim();
                if (t == "END" || t == "}" || t.StartsWith("END ") ) break;
                if (t.Length == 0 || t.StartsWith("#")) { j++; continue; }
                int startLine = j + 1;
                var sb = new StringBuilder(t);
                while ((EndsContinued(sb.ToString()) || OpenQuote(sb.ToString())) && j + 1 < lines.Length)
                {
                    var next = lines[j + 1].Trim();
                    if (next == "END" || next == "}") break;
                    sb.Append(' ').Append(next); j++;
                }
                j++;
                var c = ParseControl(sb.ToString(), f, startLine);
                if (c != null) { ResolveImage(c, images, f); form.Controls.Add(c); }
            }
            result.Add(form);
            i = Math.Max(i, j);
        }
        return result;
    }

    private static readonly Regex ImageRes = Rx.Create(@"^\s*([A-Za-z_]\w*|\d+)\s+(ICON|BITMAP)\s+(?:(?:DISCARDABLE|MOVEABLE|PURE|IMPURE|LOADONCALL|PRELOAD|FIXED)\s+)*""((?:[^""]|"""")*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>`NAME ICON "file.ico"` / `NAME BITMAP "file.bmp"` lines: resource name -> file path as written.</summary>
    private static Dictionary<string, string> ImageResources(string[] lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            var m = ImageRes.Match(l);
            if (m.Success) d.TryAdd(m.Groups[1].Value, m.Groups[3].Value.Replace("\\\\", "/").Replace('\\', '/'));
        }
        return d;
    }

    /// <summary>Find the image file of an ICON/bitmap static: next to the .rc, else in its res/ folder; never outside the input root.</summary>
    private static void ResolveImage(UiControl c, Dictionary<string, string> images, SourceFile f)
    {
        if (!c.Extra.TryGetValue("icon", out var name) || !images.TryGetValue(name, out var file)) return;
        string root = f.AbsPath.EndsWith(f.RelPath, StringComparison.Ordinal) ? f.AbsPath[..^f.RelPath.Length] : "";
        if (root.Length == 0) return;
        string dir = Path.GetDirectoryName(f.RelPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        string fullRoot = Path.GetFullPath(root);
        foreach (var cand in new[] { Path.Combine(dir, file), Path.Combine(dir, "res", file), Path.Combine(dir, "..", "res", file) })
        {
            var full = Path.GetFullPath(Path.Combine(fullRoot, cand));
            if (!full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (!File.Exists(full)) continue;
            c.Extra["image"] = Path.GetRelativePath(fullRoot, full).Replace(Path.DirectorySeparatorChar, '/');
            var ext = Path.GetExtension(full).ToLowerInvariant();
            c.Extra["imagekind"] = ext == ".bmp" ? "bitmap" : "icon";
            return;
        }
    }

    private static bool StartsBody(string line)
    {
        var t = line.Trim();
        return t == "BEGIN" || t == "{" || Rx.IsMatch(t, @"^(STYLE|EXSTYLE|CAPTION|FONT|CLASS|MENU|CHARACTERISTICS|LANGUAGE|VERSION)\b");
    }

    private static bool EndsContinued(string s) => s.EndsWith("|") || s.EndsWith(",");
    private static bool OpenQuote(string s) => s.Count(ch => ch == '"') % 2 == 1;

    private static List<int> Ints(string s)
    {
        var l = new List<int>();
        foreach (var p in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(p, out int v)) l.Add(v); else break;
        return l;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Replace("\"\"", "\"");
    }

    /// <summary>Split on commas outside quotes.</summary>
    private static List<string> Args(string s)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { if (q && i + 1 < s.Length && s[i + 1] == '"') { sb.Append("\"\""); i++; continue; } q = !q; sb.Append(c); }
            else if (c == ',' && !q) { r.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        r.Add(sb.ToString().Trim());
        return r;
    }

    private static int? Num(string? s) => s != null && int.TryParse(s.Trim(), out int v) ? v : null;

    private static UiControl? ParseControl(string stmt, SourceFile f, int line)
    {
        var m = Keyword.Match(stmt);
        if (!m.Success) return null;
        var kw = m.Groups[1].Value;
        var a = Args(m.Groups[2].Value);
        UiControl? c = null;
        if (TextFirst.Contains(kw) && a.Count >= 2)
        {
            var text = a[0].StartsWith("\"") ? Unquote(a[0]) : a[0];
            c = new UiControl { Type = kw, Text = text, Name = a[1] };
            c.X = Num(Get(a, 2)); c.Y = Num(Get(a, 3)); c.W = Num(Get(a, 4)); c.H = Num(Get(a, 5));
            if (Get(a, 6) is { Length: > 0 } st) c.Extra["style"] = st;
            if (kw == "ICON") { c.Extra["imagekind"] = "icon"; }
            if (kw == "ICON") { c.W ??= 20; c.H ??= 20; if (!a[0].StartsWith("\"")) { c.Extra["icon"] = a[0]; c.Text = null; } }
        }
        else if (IdFirst.Contains(kw) && a.Count >= 1)
        {
            c = new UiControl { Type = kw, Name = a[0] };
            c.X = Num(Get(a, 1)); c.Y = Num(Get(a, 2)); c.W = Num(Get(a, 3)); c.H = Num(Get(a, 4));
            if (Get(a, 5) is { Length: > 0 } st) c.Extra["style"] = st;
        }
        else if (kw == "CONTROL" && a.Count >= 7)
        {
            var text = a[0].StartsWith("\"") ? Unquote(a[0]) : a[0];
            var cls = Unquote(a[2]);
            var style = a[3];
            c = new UiControl { Name = a[1], Text = text.Length == 0 ? null : text, Type = MapClass(cls, style) };
            c.Extra["class"] = cls;
            c.Extra["style"] = style;
            c.X = Num(a[4]); c.Y = Num(a[5]); c.W = Num(Get(a, 6)); c.H = Num(Get(a, 7));
            if (c.Type is "ICON")
            {
                // static with SS_ICON / SS_BITMAP: the "text" is the image resource name
                c.Extra["imagekind"] = style.Contains("SS_BITMAP", StringComparison.OrdinalIgnoreCase) ? "bitmap" : "icon";
                if (!string.IsNullOrEmpty(c.Text)) c.Extra["icon"] = c.Text;
                c.Text = null;
            }
        }
        if (c == null || c.X == null || c.Y == null) return null;
        c.Source = new SourceRef(f.RelPath, line);
        if (c.W == null || c.H == null) { c.W ??= 20; c.H ??= 14; }
        return c;
    }

    private static string? Get(List<string> a, int i) => i < a.Count ? a[i] : null;

    private static string MapClass(string cls, string style)
    {
        var c = cls.ToLowerInvariant();
        bool Has(string s) => style.Contains(s, StringComparison.OrdinalIgnoreCase);
        switch (c)
        {
            case "button":
                if (Has("BS_AUTOCHECKBOX") || Has("BS_CHECKBOX") || Has("BS_3STATE") || Has("BS_AUTO3STATE")) return "CHECKBOX";
                if (Has("BS_AUTORADIOBUTTON") || Has("BS_RADIOBUTTON")) return "RADIOBUTTON";
                if (Has("BS_GROUPBOX")) return "GROUPBOX";
                return Has("BS_DEFPUSHBUTTON") ? "DEFPUSHBUTTON" : "PUSHBUTTON";
            case "static":
                if (Has("SS_ICON") || Has("SS_BITMAP") || Has("SS_ENHMETAFILE")) return "ICON";
                if (Has("FRAME") || Has("SS_RECT") || Has("SS_ETCHED") || Has("SS_GRAYRECT")) return "FRAME";
                if (Has("SS_CENTER")) return "CTEXT";
                if (Has("SS_RIGHT")) return "RTEXT";
                return "LTEXT";
            case "edit": return "EDITTEXT";
            case "combobox": case "comboboxex32": return "COMBOBOX";
            case "listbox": return "LISTBOX";
            case "scrollbar": return "SCROLLBAR";
            case "syslistview32": return "ListView";
            case "systreeview32": return "TreeView";
            case "msctls_trackbar32": return "Slider";
            case "msctls_progress32": return "ProgressBar";
            case "systabcontrol32": return "TabControl";
            case "msctls_updown32": return "Spin";
            case "sysdatetimepick32": return "DateTimePicker";
            case "sysmonthcal32": return "MonthCalendar";
            case "richedit": case "richedit20a": case "richedit20w": return "EDITTEXT";
            default: return cls;
        }
    }

    // ------------------------------------------------------------ C++ class binding

    private static readonly Regex ClassHead = Rx.Create(@"\b(?:class|struct)\s+(?:\w+\s+)*?(\w+)\s*:\s*(?:public|protected|private)?\s*[\w:<>]+[^{;]*\{", RegexOptions.Compiled);
    private static readonly Regex EnumIdd = Rx.Create(@"enum\s*(?:\w+\s*)?\{\s*IDD\s*=\s*(\w+)", RegexOptions.Compiled);
    private static readonly Regex CtorInit = Rx.Create(@"\b(\w+)\s*::\s*(\w+)\s*\([^{;]*?\)\s*:\s*([A-Za-z_]\w*)\s*\(\s*(?:_T\(\s*)?""?([\w:]+)""?", RegexOptions.Compiled | RegexOptions.Singleline);

    private static void BindClasses(IReadOnlyList<SourceFile> files, List<UiForm> forms)
    {
        if (forms.Count == 0) return;
        var byId = forms.GroupBy(x => x.ResourceId!).ToDictionary(g => g.Key, g => g.ToList());
        var iddOf = new Dictionary<string, string>(); // class -> resource id (from enum IDD)

        var cpp = files.Where(f => f.Lang == Lang.Cpp).Select(f => (f, code: CodeText.StripCComments(f.Text))).ToList();
        foreach (var (f, code) in cpp)
        {
            if (!code.Contains("IDD", StringComparison.Ordinal)) continue;
            foreach (Match m in ClassHead.Matches(code))
            {
                int open = m.Index + m.Length - 1;
                int close = CodeText.MatchBrace(code, open);
                if (close < 0) continue;
                var em = EnumIdd.Match(code, open, close - open);
                if (!em.Success) continue;
                iddOf.TryAdd(m.Groups[1].Value, em.Groups[1].Value);
                Bind(m.Groups[1].Value, em.Groups[1].Value, f.Project);
            }
        }
        foreach (var (f, code) in cpp)
        {
            if (!code.Contains("::", StringComparison.Ordinal)) continue;
            foreach (Match m in CtorInit.Matches(code))
            {
                if (m.Groups[1].Value != m.Groups[2].Value) continue; // constructor: Foo::Foo
                var cls = m.Groups[1].Value;
                var arg = m.Groups[4].Value;
                string? id = null;
                if (arg == "IDD" || arg.EndsWith("::IDD")) iddOf.TryGetValue(arg == "IDD" ? cls : arg[..^5], out id);
                else if (byId.ContainsKey(arg)) id = arg;
                if (id != null) Bind(cls, id, f.Project);
            }
        }

        void Bind(string cls, string id, string project)
        {
            if (!byId.TryGetValue(id, out var list)) return;
            var same = list.Where(x => x.Project == project).ToList();
            var targets = same.Count > 0 ? same : list;
            foreach (var t in targets)
                if (!t.ClassNames.Contains(cls)) t.ClassNames.Add(cls);
        }
    }
}
