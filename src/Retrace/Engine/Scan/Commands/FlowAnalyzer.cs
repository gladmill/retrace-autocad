using System.Text;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Commands;

/// <summary>Walks a method body (and the methods it calls, up to maxDepth) and lists the UI it touches.</summary>
public sealed class FlowAnalyzer
{
    private const int StepCap = 200;

    private sealed class MethodInfo
    {
        public required MethodSpan S { get; init; }
        public required CodeText Ct { get; init; }
        public SourceFile F => Ct.File;
        public string Key => $"{F.RelPath}#{S.Start}";
        public string Scope => Ct.Code[S.Start..Math.Min(Ct.Code.Length, S.BodyEnd + 1)];
        public int ParamCount
        {
            get
            {
                int p = S.Signature.IndexOf('(');
                if (p < 0) return 0;
                var a = CodeText.ArgsAt(S.Signature, p, out _);
                if (a == null || a.Trim().Length == 0) return 0;
                return CodeText.SplitArgs(a).Count;
            }
        }
    }

    private readonly int _maxDepth;
    private readonly List<MethodInfo> _all = new();
    private readonly Dictionary<string, List<MethodInfo>> _byQualified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MethodInfo>> _byShort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MethodInfo>> _byClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _uiClasses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paletteClasses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _editorVars = new();
    private readonly Dictionary<(string, int, string), string?> _typeCache = new();

    public FlowAnalyzer(IReadOnlyList<SourceFile> files, int maxDepth)
    {
        _maxDepth = maxDepth;
        foreach (var f in files)
        {
            if (f.Lang is not (Lang.CSharp or Lang.VbNet or Lang.Cpp) || string.IsNullOrEmpty(f.Text)) continue;
            CodeText ct;
            List<MethodSpan> spans;
            
            try { ct = new CodeText(f); spans = MethodFinder.Find(ct); }
            catch { continue; }
            foreach (var sp in spans)
            {
                var mi = new MethodInfo { S = sp, Ct = ct };
                _all.Add(mi);
                Put(_byQualified, Norm(sp.QualifiedName), mi);
                Put(_byShort, sp.Name, mi);
                if (sp.ClassName.Length > 0) Put(_byClass, SimpleType(sp.ClassName), mi);
            }
            CollectClassKinds(ct);
            var ed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ed", "editor", "_ed", "_editor", "acEd", "acEditor" };
            foreach (Match m in Rx.Matches(ct.Code, @"\bEditor\s+(\w+)|\b(\w+)\s+As\s+(?:New\s+)?(?:[\w.]+\.)?Editor\b", RegexOptions.IgnoreCase))
                ed.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            foreach (Match m in Rx.Matches(ct.Code, @"\b(\w+)\s*=\s*[\w.()\s]*\.\s*Editor\b"))
                ed.Add(m.Groups[1].Value);
            _editorVars[f.RelPath] = ed;
        }
    }

    private static void Put(Dictionary<string, List<MethodInfo>> d, string k, MethodInfo m)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new();
        l.Add(m);
    }

    private static string Norm(string q) => q.Replace("::", ".");

    private void CollectClassKinds(CodeText ct)
    {
        string s = ct.Code;
        var ui = new[] { "Form", "Window", "UserControl", "Page", "Dialog", "CommonDialog", "DialogWindow", "MetroWindow", "CAcUiDialog", "CAdUiBaseDialog" };
        foreach (Match m in Rx.Matches(s, @"\bclass\s+(\w+)\s*(?:<[^>]*>)?\s*:\s*([^{;]+)"))
        {
            foreach (var b in m.Groups[2].Value.Split(',').Select(x => SimpleType(x.Trim())))
            {
                if (ui.Contains(b, StringComparer.OrdinalIgnoreCase) || b.EndsWith("Form") || b.EndsWith("Window")) _uiClasses.Add(m.Groups[1].Value);
                if (b.Equals("PaletteSet", StringComparison.OrdinalIgnoreCase) || b.EndsWith("PaletteSet")) _paletteClasses.Add(m.Groups[1].Value);
            }
        }
        foreach (Match m in Rx.Matches(s, @"^\s*(?:Partial\s+|Public\s+|Friend\s+)*Class\s+(\w+)\s*\r?\n\s*Inherits\s+([\w.]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var b = SimpleType(m.Groups[2].Value);
            if (ui.Contains(b, StringComparer.OrdinalIgnoreCase) || b.EndsWith("Form") || b.EndsWith("Window")) _uiClasses.Add(m.Groups[1].Value);
            if (b.EndsWith("PaletteSet")) _paletteClasses.Add(m.Groups[1].Value);
        }
    }

    // ------------------------------------------------------------------ public

    /// <summary>Steps in source order. qualifiedMethod is CommandEntry.Method / RibbonItem.HandlerMethod.</summary>
    public List<FlowStep> Analyze(string qualifiedMethod)
    {
        var steps = new List<FlowStep>();
        var entry = Find(qualifiedMethod);
        if (entry == null) return steps;
        var stack = new HashSet<string> { entry.Key };
        var expanded = new HashSet<string> { entry.Key };
        Walk(entry, 0, stack, expanded, steps);
        return steps;
    }

    /// <summary>True when the method body is in the scanned sources.</summary>
    public bool Knows(string qualifiedMethod) => Find(qualifiedMethod) != null;

    /// <summary>Like <see cref="Knows"/>, but the class must match too (the full name or a dotted tail of it):
    /// a method name alone, even a unique one, does not count.</summary>
    public bool Declares(string qualifiedMethod) => Find(qualifiedMethod, byShortName: false) != null;

    private MethodInfo? Find(string name, bool byShortName = true)
    {
        string? hint = null;
        int at = name.IndexOf('@');
        if (at > 0) { hint = name[(at + 1)..]; name = name[..at]; }
        name = Norm(name.Trim().TrimStart('&'));
        if (name.Length == 0) return null;
        if (hint != null)
        {
            var hit = _all.FirstOrDefault(x => x.F.RelPath == hint && Norm(x.S.QualifiedName).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        if (_byQualified.TryGetValue(name, out var ex)) return ex[0];
        var suffix = _byQualified.Where(kv => kv.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value).ToList();
        if (suffix.Count >= 1) return suffix[0];
        if (!byShortName) return null;
        string shortName = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        if (_byShort.TryGetValue(shortName, out var sh) && sh.Count == 1) return sh[0];
        return null;
    }

    // ------------------------------------------------------------------ walker

    private static readonly Regex Events = Rx.Create(
        @"(?<new>\bnew\s+(?<ctype>[A-Za-z_][\w.]*)\s*(?:<[^<>()]*>)?\s*\()" +
        @"|(?<vis>(?<![\w])(?<vchain>[A-Za-z_][\w.]*(?:\s*(?:->|::)\s*\w+)*)\s*(?:\.|->)\s*Visible\s*=\s*(?:true|True)\b)" +
        @"|(?<call>(?<![\w])(?<chain>(?:[A-Za-z_]\w*\s*(?:<[^<>()]*>)?\s*(?:\((?:[^()]|\([^()]*\))*\))?\s*(?:\.|::|->)\s*)*)(?<name>~?[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "else", "typeof", "sizeof", "nameof",
        "throw", "fixed", "do", "when", "get", "set", "default", "and", "or", "not", "is", "as", "in", "await", "ctype", "directcast",
        "trycast", "gettype", "cstr", "cint", "cdbl", "clng", "cbool", "cdec", "csng", "cbyte", "cshort", "cobj", "select", "case", "sub",
        "function", "while", "until", "with", "do", "elseif", "then", "await", "yield", "checked", "unchecked", "delegate", "stackalloc",
        "assert", "defined", "static_cast", "dynamic_cast", "reinterpret_cast", "const_cast", "_T", "ACRX_T", "TEXT", "L",
    };

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "remove", "clear", "get", "set", "contains", "tostring", "dispose", "close", "open", "show", "start", "stop", "run", "write",
        "read", "format", "parse", "equals", "gettype", "insert", "indexof", "where", "first", "any", "count", "tolist", "append", "trim",
        "split", "join", "replace", "substring", "startswith", "endswith", "tryparse", "addrange", "invoke", "getobject", "commit", "abort",
        "gettransaction", "starttransaction", "begin", "end", "update", "init", "initialize", "load", "save", "reset", "execute", "create",
        "delete", "find", "select", "next", "push", "pop", "sort", "copy", "move", "refresh", "validate", "apply", "ok", "cancel",
    };

    private bool Full(List<FlowStep> o) => o.Count >= StepCap;

    private void Walk(MethodInfo m, int depth, HashSet<string> stack, HashSet<string> expanded, List<FlowStep> o)
    {
        var code = m.Ct.Code;
        int bs = m.S.BodyStart, be = Math.Min(code.Length, m.S.BodyEnd + 1);
        if (be <= bs) return;
        bool cpp = m.F.Lang == Lang.Cpp, vb = m.F.Lang == Lang.VbNet;
        var palettesHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Match mt;
        try { mt = Events.Match(code, bs, be - bs); } catch (RegexMatchTimeoutException) { return; }
        for (; mt.Success && !Full(o); mt = SafeNext(mt))
        {
            int at = mt.Index;
            SourceRef src = m.Ct.Ref(at);
            FlowStep? Mk(FlowKind k, string text, string? target = null, Dictionary<string, string>? extra = null)
            {
                var st = new FlowStep { Kind = k, Text = text, Target = target, Source = src, Depth = depth };
                if (extra != null) foreach (var kv in extra) st.Extra[kv.Key] = kv.Value;
                return st;
            }

            if (mt.Groups["new"].Success)
            {
                string tw = mt.Groups["ctype"].Value;
                string t = SimpleType(tw);
                if (_paletteClasses.Contains(t) || t.EndsWith("PaletteSet", StringComparison.OrdinalIgnoreCase))
                {
                    var a = ArgsOf(code, mt.Index + mt.Length - 1);
                    string? title = a.Count > 0 ? CodeText.Literal(a[0]) : null;
                    var ex = new Dictionary<string, string> { ["type"] = tw };
                    o.Add(Mk(FlowKind.Palette, title ?? t, t, ex)!);
                    palettesHere.Add(t);
                    continue;
                }
                if (_uiClasses.Contains(t)) continue; // UI constructors are InitializeComponent noise
                var ctor = ResolveCtor(m, t);
                if (ctor != null)
                    Follow(m, ctor, $"new {t}", depth, stack, expanded, o, Mk, new Dictionary<string, string> { ["ctor"] = "true" });
                continue;
            }

            if (mt.Groups["vis"].Success)
            {
                string chain = mt.Groups["vchain"].Value;
                string last = Segs(chain).LastOrDefault() ?? chain;
                string? ty = SimpleTypeOrNull(TypeOfExpr(m, last));
                if ((ty != null && ty.Contains("Palette", StringComparison.OrdinalIgnoreCase)) ||
                    (ty == null && last.Contains("palette", StringComparison.OrdinalIgnoreCase)))
                {
                    string target = ty ?? "PaletteSet";
                    if (palettesHere.Contains(target)) continue;
                    palettesHere.Add(target);
                    o.Add(Mk(FlowKind.Palette, target, target, new() { ["via"] = "Visible" })!);
                }
                continue;
            }

            // ---- call
            string name = mt.Groups["name"].Value;
            if (Keywords.Contains(name) || name.StartsWith("~")) continue;
            // skip the declaration of a local function / method signature: previous token is a type and next is ')' + '{' is too fuzzy; ignore
            string chainText = mt.Groups["chain"].Value;
            var segs = Segs(chainText);
            int paren = mt.Index + mt.Length - 1;
            var args = ArgsOf(code, paren);
            string lname = name.ToLowerInvariant();
            string? implicitType = null;
            if (segs.Count == 0 && at > 0)
            {
                int p = at - 1;
                while (p >= 0 && char.IsWhiteSpace(code[p])) p--;
                if (p >= 0 && (code[p] == '.' || (p >= 1 && code[p - 1] == '-' && code[p] == '>')))
                    implicitType = TypeBeforeDot(code, p);
            }

            if (cpp)
            {
                if (CppCall(m, lname, name, segs, args, depth, src, o, Mk)) continue;
            }
            else
            {
                if (DotNetCall(m, lname, name, segs, implicitType, args, vb, depth, src, o, Mk, code, at)) continue;
            }

            // plain call into an indexed method
            var target2 = Resolve(m, segs, name, args.Count, implicitType);
            if (target2 != null)
                Follow(m, target2, $"{SimpleType(target2.S.ClassName)}{(target2.S.ClassName.Length > 0 ? "." : "")}{target2.S.Name}", depth, stack, expanded, o, Mk, null);
        }
    }

    private static Match SafeNext(Match m)
    {
        try { return m.NextMatch(); } catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    private void Follow(MethodInfo from, MethodInfo to, string text, int depth, HashSet<string> stack, HashSet<string> expanded,
        List<FlowStep> o, Func<FlowKind, string, string?, Dictionary<string, string>?, FlowStep?> mk, Dictionary<string, string>? extra)
    {
        if (Full(o)) return;
        if (stack.Contains(to.Key))
        {
            var ex = extra != null ? new Dictionary<string, string>(extra) : new();
            ex["recursive"] = "true";
            o.Add(mk(FlowKind.Call, text, to.S.QualifiedName, ex)!);
            return;
        }
        var step = mk(FlowKind.Call, text, to.S.QualifiedName, extra)!;
        bool already = expanded.Contains(to.Key);
        if (already) step.Extra["seen"] = "true";
        o.Add(step);
        if (already || depth >= _maxDepth) return;
        expanded.Add(to.Key);
        stack.Add(to.Key);
        int before = o.Count;
        Walk(to, depth + 1, stack, expanded, o);
        stack.Remove(to.Key);
        // drop nested helper calls that lead to no UI step (keep the entry method's own direct calls)
        if (depth > 0 && o.Count == before) o.Remove(step);
    }

    // ------------------------------------------------------------------ .NET calls

    private static readonly HashSet<string> PromptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "getpoint", "getentity", "getnestedentity", "getstring", "getkeywords", "getinteger", "getdouble", "getdistance", "getangle",
        "getcorner", "getfilenameforopen", "getfilenameforsave",
    };
    private static readonly HashSet<string> SelectionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "getselection", "selectall", "selectwindow", "selectcrossingwindow", "selectimplied", "selectfence", "selectcrossingpolygon",
        "selectwindowpolygon", "selectprevious", "selectlast",
    };

    private bool DotNetCall(MethodInfo m, string l, string name, List<string> segs, string? implicitType, List<string> args, bool vb, int depth,
        SourceRef src, List<FlowStep> o, Func<FlowKind, string, string?, Dictionary<string, string>?, FlowStep?> mk, string code, int at)
    {
        string? last = segs.Count > 0 ? segs[^1] : null;
        bool isEd = IsEditor(m, segs);

        if (PromptNames.Contains(l) && isEd)
        {
            var ex = new Dictionary<string, string> { ["method"] = name };
            string? text = null;
            if (args.Count > 0)
            {
                text = Tmpl(args[0]);
                if (text == null) text = PromptFromOptions(m, args[0], ex);
            }
            if (text == null) { text = name; ex["unresolved"] = "true"; }
            o.Add(mk(FlowKind.Prompt, text, null, ex)!);
            return true;
        }
        if (SelectionNames.Contains(l) && isEd)
        {
            var ex = new Dictionary<string, string> { ["method"] = name };
            string? text = null;
            if (args.Count > 0 && l == "getselection")
            {
                var a0 = args[0];
                if (Rx.IsMatch(a0, @"^\w+$"))
                {
                    var init = FindOptionsInit(m, a0);
                    text = FindMessage(m, a0, init, "MessageForAdding");
                    if (text == null && init != null && init.Count > 0) text = Tmpl(init[0]);
                }
            }
            if (text == null) { text = "Select objects"; }
            o.Add(mk(FlowKind.Selection, text, null, ex)!);
            return true;
        }
        switch (l)
        {
            case "showmodaldialog":
            case "showmodalwindow":
            {
                var (ty, wr) = FirstUiArg(m, args);
                var ex = new Dictionary<string, string> { ["via"] = name };
                if (wr != null) ex["type"] = wr;
                o.Add(mk(FlowKind.Dialog, ty ?? "(dialog)", ty, ex)!);
                return true;
            }
            case "showmodelessdialog":
            case "showmodelesswindow":
            {
                var (ty, wr) = FirstUiArg(m, args);
                var ex = new Dictionary<string, string> { ["via"] = name };
                if (wr != null) ex["type"] = wr;
                o.Add(mk(FlowKind.Palette, ty ?? "(window)", ty, ex)!);
                return true;
            }
            case "showdialog":
            {
                string? wr = implicitType ?? (last != null ? TypeOfExpr(m, last) : null);
                string? ty = SimpleTypeOrNull(wr);
                var ex = new Dictionary<string, string> { ["via"] = name };
                if (wr != null) ex["type"] = wr;
                o.Add(mk(FlowKind.Dialog, ty ?? "(dialog)", ty, ex)!);
                return true;
            }
            case "show" when args.Count == 0 && (last != null || implicitType != null):
            {
                string? wr = implicitType ?? TypeOfExpr(m, last!);
                string? ty = SimpleTypeOrNull(wr);
                if (ty == null || !IsUiType(ty)) return false;
                o.Add(mk(FlowKind.Palette, ty, ty, new() { ["via"] = "Show", ["type"] = wr! })!);
                return true;
            }
            case "show" when last != null && last.Equals("MessageBox", StringComparison.OrdinalIgnoreCase):
            {
                var ex = new Dictionary<string, string>();
                string text = args.Count > 0 ? Tmpl(args[0], true) ?? args[0] : "";
                if (args.Count > 1 && Tmpl(args[1]) is { } cap) ex["caption"] = cap;
                o.Add(mk(FlowKind.Message, text, null, ex)!);
                return true;
            }
            case "showalertdialog":
            {
                string text = args.Count > 0 ? Tmpl(args[0], true) ?? args[0] : "";
                o.Add(mk(FlowKind.Message, text, null, new() { ["via"] = name })!);
                return true;
            }
            case "writemessage":
            {
                string text = args.Count > 0 ? Tmpl(args[0], true) ?? args[0] : "";
                if (text.Length == 0) return true;
                o.Add(mk(FlowKind.Message, text, null, new() { ["via"] = name })!);
                return true;
            }
            case "sendstringtoexecute":
            case "command" when isEd:
            case "commandasync" when isEd:
            {
                string? raw = null;
                foreach (var a in args) { raw = Tmpl(a); if (raw != null) break; }
                if (raw == null) return name.Equals("SendStringToExecute", StringComparison.OrdinalIgnoreCase);
                o.Add(mk(FlowKind.SendCommand, raw.Trim(), CmdToken(raw), new() { ["raw"] = raw })!);
                return true;
            }
            case "drag" when args.Count >= 1:
            {
                var (_, wr) = ExprTypes(m, args[0]);
                string? ty = SimpleTypeOrNull(wr);
                if (!isEd && !(ty?.EndsWith("Jig", StringComparison.OrdinalIgnoreCase) ?? false)) return false;
                var ex = new Dictionary<string, string>();
                if (wr != null) ex["type"] = wr;
                o.Add(mk(FlowKind.Jig, ty ?? "Drag", ty, ex)!);
                return true;
            }
        }
        // wrapper like Dialogs.Show(dialog) / ShowForm(dlg): any "Show" call taking a UI-typed variable
        if (l.Contains("show") && !l.StartsWith("showmodeless") && args.Count > 0)
        {
            var (ty, wr) = FirstUiArg(m, args, strict: true);
            if (ty != null)
            {
                bool modeless = l.Contains("modeless");
                var ex = new Dictionary<string, string> { ["via"] = (last != null ? last + "." : "") + name };
                if (wr != null) ex["type"] = wr;
                o.Add(mk(modeless ? FlowKind.Palette : FlowKind.Dialog, ty, ty, ex)!);
                return true;
            }
        }
        return false;
    }

    private static string CmdToken(string raw)
    {
        var t = raw.Trim().Replace("^C", "").Trim();
        t = Rx.Split(t, @"\s+").FirstOrDefault() ?? "";
        t = t.TrimStart('_', '.', '\'', '(');
        return t.ToUpperInvariant();
    }

    // ------------------------------------------------------------------ C++ calls

    private bool CppCall(MethodInfo m, string l, string name, List<string> segs, List<string> args, int depth, SourceRef src,
        List<FlowStep> o, Func<FlowKind, string, string?, Dictionary<string, string>?, FlowStep?> mk)
    {
        string? FirstLit(int from = 0)
        {
            for (int i = from; i < args.Count; i++) { var v = CodeText.Literal(args[i]); if (v != null) return v; }
            return null;
        }
        string Clean(string s) => Rx.Replace(s.Replace("\\n", " ").Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();

        switch (l)
        {
            case "acedgetpoint": case "acedgetstring": case "acedgetint": case "acedgetreal": case "acedgetkword": case "acedgetdist":
            case "acedgetcorner": case "acedgetangle": case "acedgetorient": case "acedentsel": case "acednentsel": case "acednentselp":
            case "acedgetfiled": case "acedgetstringb": case "acedgetpointb":
            {
                var ex = new Dictionary<string, string> { ["method"] = name };
                var p = FirstLit();
                if (p == null) ex["unresolved"] = "true";
                o.Add(mk(FlowKind.Prompt, p != null ? Clean(p) : name, null, ex)!);
                return true;
            }
            case "acedssget":
            {
                var ex = new Dictionary<string, string> { ["method"] = name };
                if (args.Count > 0 && CodeText.Literal(args[0]) is { } mode) ex["mode"] = mode;
                o.Add(mk(FlowKind.Selection, "Select objects", null, ex)!);
                return true;
            }
            case "acseddraggen":
            case "aceddraggen":
                o.Add(mk(FlowKind.Jig, FirstLit() is { } t ? Clean(t) : "acedDragGen", null, new() { ["method"] = name })!);
                return true;
            case "drag" when segs.Count > 0:
            {
                var wr = TypeOfExpr(m, segs[^1]);
                var ty = SimpleTypeOrNull(wr);
                if ((ty?.EndsWith("Jig", StringComparison.OrdinalIgnoreCase) ?? false) || segs[^1].Contains("jig", StringComparison.OrdinalIgnoreCase))
                {
                    o.Add(mk(FlowKind.Jig, ty ?? segs[^1], ty, wr != null ? new() { ["type"] = wr } : null)!);
                    return true;
                }
                return false;
            }
            case "domodal":
            {
                string? wr = segs.Count > 0 ? (segs[^1] is "this" ? m.S.ClassName : TypeOfExpr(m, segs[^1])) : (m.S.ClassName.Length > 0 ? m.S.ClassName : null);
                string? ty = SimpleTypeOrNull(wr);
                var ex = new Dictionary<string, string> { ["via"] = "DoModal" };
                if (wr != null) ex["type"] = wr;
                o.Add(mk(FlowKind.Dialog, ty ?? "(dialog)", ty, ex)!);
                return true;
            }
            case "acedalert": case "afxmessagebox": case "messagebox": case "acutprintf": case "acedprompt": case "acedprintf":
            {
                if (l == "messagebox" && segs.Count > 0 && !segs.All(s => s.Length == 0)) { /* member MessageBox: still a message */ }
                var lit = FirstLit();
                string text = lit != null ? Clean(lit) : (args.Count > 0 ? args[0] : "");
                if (text.Length == 0) return true;
                o.Add(mk(FlowKind.Message, text, null, new() { ["via"] = name })!);
                return true;
            }
            case "acedcommand": case "acedcommands": case "acedcmd": case "acedcommandc": case "acedcommands_": case "acedinitget":
            {
                if (l == "acedinitget") return true;
                var lit = FirstLit();
                if (lit == null) return true;
                o.Add(mk(FlowKind.SendCommand, lit.Trim(), CmdToken(lit), new() { ["raw"] = lit, ["via"] = name })!);
                return true;
            }
        }
        if (l == "acedcommands") return true;
        return false;
    }

    // ------------------------------------------------------------------ resolution

    private MethodInfo? ResolveCtor(MethodInfo ctx, string typeSimple)
    {
        if (!_byClass.TryGetValue(typeSimple, out var inClass)) return null;
        var c = inClass.Where(x => (x.F.Lang == Lang.Cpp) == (ctx.F.Lang == Lang.Cpp)).Where(x => x.S.Name.Equals(typeSimple, StringComparison.OrdinalIgnoreCase) || x.S.Name.Equals("New", StringComparison.OrdinalIgnoreCase)).ToList();
        return c.Count > 0 ? c[0] : null;
    }

    private MethodInfo? Resolve(MethodInfo ctx, List<string> segs, string name, int argc, string? implicitType)
    {
        if (!_byShort.TryGetValue(name, out var all)) return null;
        var cands = all.Where(c => (c.F.Lang == Lang.Cpp) == (ctx.F.Lang == Lang.Cpp)).ToList();
        if (cands.Count == 0) return null;
        string? last = segs.Count > 0 ? segs[^1] : null;
        bool self = last == null && implicitType == null || (last != null && last.ToLowerInvariant() is "this" or "me" or "myclass");
        if (self)
        {
            var same = cands.Where(c => c.S.FullClass == ctx.S.FullClass && c.S.ClassName == ctx.S.ClassName && (ctx.S.ClassName.Length > 0 || c.F.RelPath == ctx.F.RelPath)).ToList();
            var pick = PickByArgs(same, argc);
            if (pick != null) return pick;
            var sameFile = cands.Where(c => c.F.RelPath == ctx.F.RelPath).ToList();
            if (sameFile.Count == 1) return sameFile[0];
            if (last != null) return null;
            // free function / inherited call: unique within the project, then unique overall
            var proj = cands.Where(c => c.F.Project == ctx.F.Project).ToList();
            if (proj.Count == 1) return proj[0];
            return cands.Count == 1 && !Generic.Contains(name) && ctx.F.Lang != Lang.Cpp ? cands[0] : null;
        }
        if (last != null && last.ToLowerInvariant() is "base" or "mybase") return cands.Count == 1 ? cands[0] : null;

        string? typeName = implicitType != null ? SimpleType(implicitType) : null;
        if (typeName == null && last != null)
        {
            if (_byClass.ContainsKey(last)) typeName = last;
            else typeName = SimpleTypeOrNull(TypeOfExpr(ctx, last));
        }
        if (typeName != null)
        {
            var inType = cands.Where(c => SimpleType(c.S.ClassName).Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            return PickByArgs(inType, argc);
        }
        // receiver of unknown type
        if (Generic.Contains(name)) return null;
        if (ctx.F.Lang == Lang.Cpp) cands = cands.Where(c => c.F.Project == ctx.F.Project).ToList();
        return cands.Count == 1 ? cands[0] : null;
    }

    private static MethodInfo? PickByArgs(List<MethodInfo> c, int argc)
    {
        if (c.Count == 0) return null;
        if (c.Count == 1) return c[0];
        return c.FirstOrDefault(x => x.ParamCount == argc) ?? c[0];
    }

    private bool IsEditor(MethodInfo m, List<string> segs)
    {
        if (segs.Count == 0) return false;
        string last = segs[^1];
        if (last.Equals("Editor", StringComparison.OrdinalIgnoreCase)) return true;
        return _editorVars.TryGetValue(m.F.RelPath, out var s) && s.Contains(last);
    }

    private bool IsUiType(string simple) =>
        _uiClasses.Contains(simple) || Rx.IsMatch(simple, @"^(Form|Window)|(Form|Window|Dialog)$", RegexOptions.IgnoreCase);

    /// <summary>First argument whose expression resolves to a UI type. strict: require IsUiType; else accept any resolvable type.</summary>
    private (string? simple, string? written) FirstUiArg(MethodInfo m, List<string> args, bool strict = false)
    {
        string? fallbackSimple = null, fallbackWritten = null;
        foreach (var a in args)
        {
            var (s, w) = ExprTypes(m, a);
            if (s == null) continue;
            if (IsUiType(s)) return (s, w);
            fallbackSimple ??= s; fallbackWritten ??= w;
        }
        return strict ? (null, null) : (fallbackSimple, fallbackWritten);
    }

    private (string? simple, string? written) ExprTypes(MethodInfo m, string expr)
    {
        var w = TypeOfExpr(m, expr);
        return (SimpleTypeOrNull(w), w);
    }

    // ------------------------------------------------------------------ types of expressions

    private static readonly HashSet<string> NotTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "var", "return", "new", "else", "throw", "case", "await", "yield", "in", "is", "as", "using", "dim", "set", "let", "if", "while", "delete", "goto", "ref", "out",
    };

    private string? TypeOfExpr(MethodInfo m, string expr)
    {
        expr = expr.Trim();
        var nm = Rx.Match(expr, @"^(?:new|New)\s+([A-Za-z_][\w.:]*)");
        if (nm.Success) return nm.Groups[1].Value;
        if (expr == "this" || expr.Equals("Me", StringComparison.OrdinalIgnoreCase)) return m.S.ClassName;
        expr = Rx.Replace(expr, @"^\(\s*[\w.<>]+\s*\)", "").Trim();      // (Type)x cast
        expr = Rx.Replace(expr, @"^(?:ref|out|ByRef|ByVal)\s+", "", RegexOptions.IgnoreCase);
        var segs = Segs(expr);
        if (segs.Count == 0) return null;
        string v = segs[^1];
        if (!Rx.IsMatch(v, @"^[A-Za-z_]\w*$")) return null;
        var key = (m.F.RelPath, m.S.Start, v);
        if (_typeCache.TryGetValue(key, out var c)) return c;
        var t = TypeOfVar(m, v);
        _typeCache[key] = t;
        return t;
    }

    private string? TypeOfVar(MethodInfo m, string v)
    {
        string ev = Regex.Escape(v);
        foreach (var scope in new[] { m.Scope, m.Ct.Code })
        {
            switch (m.F.Lang)
            {
                case Lang.VbNet:
                {
                    var r = Rx.Match(scope, $@"\b{ev}\s+As\s+[\w.()]+\s*=\s*New\s+([\w.]+)", RegexOptions.IgnoreCase);
                    if (r.Success) return r.Groups[1].Value;
                    r = Rx.Match(scope, $@"\b{ev}\s+As\s+(?:New\s+)?([\w.]+)", RegexOptions.IgnoreCase);
                    if (r.Success) return r.Groups[1].Value;
                    r = Rx.Match(scope, $@"\b{ev}\s*=\s*New\s+([\w.]+)", RegexOptions.IgnoreCase);
                    if (r.Success) return r.Groups[1].Value;
                    break;
                }
                case Lang.Cpp:
                {
                    var r = Rx.Match(scope, $@"\b{ev}\s*=\s*new\s+(\w+(?:::\w+)*)");
                    if (r.Success) return r.Groups[1].Value;
                    foreach (Match d in Rx.Matches(scope, $@"(?<![\w:])(\w+(?:::\w+)*)\s*[*&]*\s+{ev}\s*(?:\(|;|=|,|\[|\))|(?<![\w:])(\w+(?:::\w+)*)[*&]+\s*{ev}\s*(?:\(|;|=|,|\[|\))"))
                    {
                        string ty = d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value;
                        if (!NotTypes.Contains(ty)) return ty;
                    }
                    break;
                }
                default:
                {
                    var r = Rx.Match(scope, $@"\b{ev}\s*=\s*new\s+([A-Za-z_][\w.]*)");
                    if (r.Success) return r.Groups[1].Value;
                    foreach (Match d in Rx.Matches(scope, $@"(?<![\w.])([A-Za-z_][\w.]*(?:<[^<>;()]*>)?(?:\[\])?\??)\s+{ev}\b\s*(?:=|;|,|\)|:)"))
                    {
                        string ty = d.Groups[1].Value;
                        if (!NotTypes.Contains(ty)) return ty;
                    }
                    break;
                }
            }
        }
        return null;
    }

    private string? TypeBeforeDot(string code, int dotAt)
    {
        // `new X(...)` immediately before the dot
        int start = Math.Max(0, dotAt - 400);
        var win = code[start..dotAt];
        var m = Rx.Match(win, @"\bnew\s+([A-Za-z_][\w.]*)\s*(?:<[^<>]*>)?\s*\((?:[^()]|\([^()]*\))*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------ prompt options

    /// <summary>Args of the `new PromptXxxOptions(...)` that initializes variable `v` in the method, or null.</summary>
    private List<string>? FindOptionsInit(MethodInfo m, string v)
    {
        return FindOptionsInit(m, v, m.Scope) ?? FindOptionsInit(m, v, m.Ct.Code);
    }

    private List<string>? FindOptionsInit(MethodInfo m, string v, string scope)
    {
        bool vb = m.F.Lang == Lang.VbNet;
        foreach (Match n in Rx.Matches(scope, @"\bnew\s+(?:[\w.]+\.)?Prompt\w*Options\b", RegexOptions.IgnoreCase))
        {
            int stmtStart = 0;
            for (int i = n.Index - 1; i >= 0; i--)
            {
                char c = scope[i];
                if (c == ';' || c == '{' || c == '}' || (vb && c == '\n')) { stmtStart = i + 1; break; }
            }
            var pre = scope[stmtStart..n.Index];
            if (!Rx.IsMatch(pre, $@"\b{Regex.Escape(v)}\b")) continue;
            int p = n.Index + n.Length;
            while (p < scope.Length && char.IsWhiteSpace(scope[p])) p++;
            if (p < scope.Length && scope[p] == '(')
            {
                var a = CodeText.ArgsAt(scope, p, out _);
                return a == null ? new() : CodeText.SplitArgs(a).Where(x => x.Length > 0).ToList();
            }
            return new();
        }
        return null;
    }

    private string? FindMessage(MethodInfo m, string v, List<string>? init, string prop)
    {
        string scope = m.Scope;
        var r = Rx.Match(scope, $@"\b{Regex.Escape(v)}\s*\.\s*{prop}\s*=\s*([^;\n]+)", RegexOptions.IgnoreCase);
        if (r.Success && Tmpl(r.Groups[1].Value) is { } t) return t;
        // object initializer: new PromptEntityOptions("x") { Message = "..." }
        r = Rx.Match(scope, $@"\b{Regex.Escape(v)}\b[^;]*?\{{\s*[^}}]*\b{prop}\s*=\s*([^,}}]+)", RegexOptions.IgnoreCase);
        if (r.Success && Tmpl(r.Groups[1].Value) is { } t2) return t2;
        return null;
    }

    private string? PromptFromOptions(MethodInfo m, string arg, Dictionary<string, string> ex)
    {
        arg = arg.Trim();
        List<string>? init;
        string? v = null;
        var nm = Rx.Match(arg, @"^new\s+(?:[\w.]+\.)?Prompt\w*Options\s*\(", RegexOptions.IgnoreCase);
        if (nm.Success)
        {
            var a = CodeText.ArgsAt(arg, nm.Length - 1, out _);
            init = a == null ? new() : CodeText.SplitArgs(a);
        }
        else if (Rx.IsMatch(arg, @"^\w+$"))
        {
            v = arg;
            init = FindOptionsInit(m, v);
        }
        else return null;

        string? text = null;
        if (v != null) text = FindMessage(m, v, init, "Message");
        if (text == null && init != null && init.Count > 0) text = Tmpl(init[0]);
        if (init != null && init.Count > 1 && Tmpl(init[1]) is { } kws && Rx.IsMatch(arg + (v ?? ""), "Keyword", RegexOptions.IgnoreCase) | (nm.Success && arg.Contains("Keyword")))
        {
            // PromptKeywordOptions(message, "Yes No")
            if (!kws.Contains('[')) ex["keywords"] = string.Join(",", kws.Split(new[] { ' ', '/' }, StringSplitOptions.RemoveEmptyEntries));
        }
        if (v != null)
        {
            string scope = m.Scope, ev = Regex.Escape(v);
            var rej = Rx.Match(scope, $@"\b{ev}\s*\.\s*SetRejectMessage\s*\(", RegexOptions.IgnoreCase);
            if (rej.Success)
            {
                var a = CodeText.ArgsAt(scope, rej.Index + rej.Length - 1, out _);
                var first = a == null ? null : CodeText.SplitArgs(a).FirstOrDefault();
                if (first != null && Tmpl(first) is { } rm) ex["reject"] = rm;
            }
            var allowed = new List<string>();
            foreach (Match al in Rx.Matches(scope, $@"\b{ev}\s*\.\s*AddAllowedClass\s*\(\s*(?:typeof|GetType)\s*\(\s*([\w.<>]+)\s*\)", RegexOptions.IgnoreCase))
                allowed.Add(SimpleType(al.Groups[1].Value));
            if (allowed.Count > 0) ex["allowed"] = string.Join(",", allowed);
            var kw = new List<string>();
            foreach (Match k in Rx.Matches(scope, $@"\b{ev}\s*\.\s*Keywords\s*\.\s*Add\s*\(", RegexOptions.IgnoreCase))
            {
                var a = CodeText.ArgsAt(scope, k.Index + k.Length - 1, out _);
                var first = a == null ? null : CodeText.SplitArgs(a).FirstOrDefault();
                if (first != null && CodeText.Literal(first) is { } kv) kw.Add(kv);
            }
            if (kw.Count > 0) ex["keywords"] = string.Join(",", kw);
        }
        return text;
    }

    // ------------------------------------------------------------------ small helpers

    private static List<string> ArgsOf(string code, int paren)
    {
        var a = CodeText.ArgsAt(code, paren, out _);
        if (a == null) return new();
        return CodeText.SplitArgs(a).Where(x => x.Length > 0).ToList();
    }

    /// <summary>Identifier chain "a.b(c).d" -> ["a","b","d"].</summary>
    private static List<string> Segs(string chain)
    {
        var l = new List<string>();
        if (string.IsNullOrWhiteSpace(chain)) return l;
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char ch in chain)
        {
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth--; continue; }
            if (depth > 0) continue;
            sb.Append(ch);
        }
        foreach (var p in Rx.Split(sb.ToString(), @"\.|::|->"))
        {
            var t = Rx.Replace(p, @"<[^<>]*>", "").Trim();
            if (t.Length > 0) l.Add(t);
        }
        return l;
    }

    private static string SimpleType(string t)
    {
        t = Rx.Replace(t.Trim(), @"<.*>", "").Replace("[]", "").Replace("?", "").Trim('*', '&', ' ');
        int i = Math.Max(t.LastIndexOf('.'), t.LastIndexOf("::", StringComparison.Ordinal) >= 0 ? t.LastIndexOf("::", StringComparison.Ordinal) + 1 : -1);
        return i >= 0 ? t[(i + 1)..] : t;
    }

    private static string? SimpleTypeOrNull(string? t) => string.IsNullOrWhiteSpace(t) ? null : SimpleType(t);

    private static readonly HashSet<string> Newlines = new(StringComparer.OrdinalIgnoreCase)
        { "vbcrlf", "vblf", "vbnewline", "environment.newline", "system.environment.newline", "controlchars.lf", "controlchars.crlf", "controlchars.cr", "controlchars.newline", "vbcr" };

    /// <summary>Text of a string expression: literal, interpolated template, concatenation or String.Format. Null if no literal part.</summary>
    internal static string? Tmpl(string expr, bool any = false)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return null;
        var fm = Rx.Match(expr, @"^(?:System\.)?(?:string|String)\.Format\s*\(", RegexOptions.IgnoreCase);
        if (fm.Success)
        {
            var a = CodeText.ArgsAt(expr, fm.Length - 1, out _);
            var f = a == null ? null : CodeText.SplitArgs(a).FirstOrDefault();
            return f == null ? null : Tmpl(f);
        }
        if (Rx.IsMatch(expr, @"^(?:\$@|@\$|\$)"""))
        {
            string rest = expr[(expr.IndexOf('"'))..];
            int end = rest.LastIndexOf('"');
            if (end <= 0) return null;
            string inner = rest[1..end].Replace("{{", "\u0001").Replace("}}", "\u0002");
            inner = inner.Replace("\\n", " ").Replace("\\t", " ").Replace("\\\"", "\"").Replace("\\r", "");
            inner = inner.Replace("\u0001", "{").Replace("\u0002", "}");
            return Clean(inner);
        }
        var adj = Rx.Matches(expr, "(?:_T\\(|ACRX_T\\(|TEXT\\()?\\s*L?\"(?:[^\"\\\\]|\\\\.)*\"\\s*\\)?");
        if (adj.Count > 1 && Rx.Replace(expr, "(?:_T\\(|ACRX_T\\(|TEXT\\()?\\s*L?\"(?:[^\"\\\\]|\\\\.)*\"\\s*\\)?", "").Trim().Length == 0)
            return Clean(string.Concat(adj.Select(x => CodeText.Literal(x.Value.Trim()) ?? "")));
        var parts = SplitConcat(expr);
        var sb = new StringBuilder();
        bool anyLit = false;
        foreach (var p in parts)
        {
            var lit = CodeText.Literal(p);
            if (lit != null) { sb.Append(lit); anyLit = true; }
            else if (Newlines.Contains(p.Trim())) sb.Append(' ');
            else if (p.Trim().Length > 0)
            {
                var pt = p.Trim();
                var inner = pt.StartsWith("(") && pt.EndsWith(")") && pt.Length > 2 ? Tmpl(pt[1..^1]) : null;
                if (inner != null) { sb.Append(inner); anyLit = true; }
                else sb.Append('{').Append(p.Trim().Length > 30 ? p.Trim()[..30] : p.Trim()).Append('}');
            }
        }
        return any || anyLit ? Clean(sb.ToString()) : null;
    }

    private static string Clean(string s) => Rx.Replace(s.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();

    private static List<string> SplitConcat(string expr)
    {
        var l = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"')
            {
                i++;
                while (i < expr.Length && expr[i] != '"') { if (expr[i] == '\\') i++; i++; }
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if ((c == '+' || c == '&') && depth == 0)
            {
                l.Add(expr[start..i].Trim());
                start = i + 1;
            }
        }
        l.Add(expr[start..].Trim());
        return l;
    }
}
