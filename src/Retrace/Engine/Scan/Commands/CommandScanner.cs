using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Commands;

/// <summary>Finds command entry points: [CommandMethod], [LispFunction], addCommand, ACED_ARXCOMMAND_ENTRY.</summary>
public static class CommandScanner
{
    private static readonly Regex Attr = Rx.Create(
        @"\b(?:[A-Za-z_]\w*\s*\.\s*)*(?<kind>CommandMethod|LispFunction)(?:Attribute)?\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AddCmd = Rx.Create(@"(?<![\w])(?:[A-Za-z_]\w*\s*(?:->|\.|::)\s*)+addCommand\s*\(", RegexOptions.Compiled);
    private static readonly Regex AcedMacro = Rx.Create(
        @"\b(?<m>ACED_ARXCOMMAND_ENTRY_AUTO|ACED_ARXCOMMAND_ENTRYBYID_AUTO|ACED_ADSSYMBOL_ENTRY_AUTO|ACED_ADSCOMMAND_ENTRY_AUTO)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex DefineLit = Rx.Create(@"^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+(.+?)[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AssignLit = Rx.Create(
        @"\b(\w+)\s*(?:\[\s*\])?\s*=\s*((?:_T\(|ACRX_T\(|TEXT\()?\s*L?""(?:[^""\\\n]|\\.)*""\s*\)?)\s*;", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Modal", "Transparent", "UsePickSet", "Redraw", "NoPerspective", "NoMultiple", "NoTileMode", "NoPaperSpace",
        "NoOem", "Undefined", "InProgress", "Defun", "NoNewStack", "NoInternalLock", "DocReadLock", "DocExclusiveLock",
        "Session", "Interruptible", "NoHistory", "NoUndoMarker", "NoBlockEditor", "NoActionRecording", "ActionMacro",
        "NoInferConstraint", "TempShowDynDimension",
    };

    public static List<CommandEntry> Scan(IReadOnlyList<SourceFile> files)
    {
        var result = new List<CommandEntry>();
        var seen = new HashSet<string>();
        void Add(CommandEntry e)
        {
            if (seen.Add($"{e.Name}|{e.Source}|{e.Kind}")) result.Add(e);
        }

        var cppSymbols = new Dictionary<string, Dictionary<string, string>>();
        foreach (var f in files.Where(f => f.Lang == Lang.Cpp))
        {
            if (!cppSymbols.TryGetValue(f.Project, out var d)) cppSymbols[f.Project] = d = new();
            var code = CodeText.StripCComments(f.Text);
            foreach (Match m in DefineLit.Matches(code))
            {
                var v = m.Groups[2].Value;
                if (CodeText.Literal(v) != null) d.TryAdd(m.Groups[1].Value, CodeText.Literal(v)!);
            }
            foreach (Match m in AssignLit.Matches(code))
            {
                var lit = CodeText.Literal(m.Groups[2].Value);
                if (lit != null) d.TryAdd(m.Groups[1].Value, lit);
            }
        }

        // C++ function definitions per name, to disambiguate static functions that share a name across samples
        var cppDefs = new Dictionary<string, HashSet<string>>();
        foreach (var f in files)
        {
            if (string.IsNullOrEmpty(f.Text)) continue;
            switch (f.Lang)
            {
                case Lang.CSharp:
                case Lang.VbNet:
                    ScanDotNet(f, Add);
                    break;
                case Lang.Cpp:
                    ScanCpp(f, cppSymbols.GetValueOrDefault(f.Project) ?? new(), cppDefs, files, Add);
                    break;
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- .NET

    private static void ScanDotNet(SourceFile f, Action<CommandEntry> add)
    {
        var ct = new CodeText(f);
        string s = ct.Code;
        if (!s.Contains("CommandMethod", StringComparison.OrdinalIgnoreCase) && !s.Contains("LispFunction", StringComparison.OrdinalIgnoreCase)) return;
        List<MethodSpan>? spans = null;
        bool vb = f.Lang == Lang.VbNet;

        foreach (Match m in Attr.Matches(s))
        {
            // must sit inside an attribute list: previous non-space char is [ < or ,
            int p = m.Index - 1;
            while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
            if (p < 0 || !(s[p] == (vb ? '<' : '[') || s[p] == ','))
                continue;
            if (s[p] == ',' && !InAttributeList(s, p, vb)) continue;

            int paren = m.Index + m.Length - 1;
            var argText = CodeText.ArgsAt(s, paren, out int close);
            if (argText == null) continue;
            var args = CodeText.SplitArgs(argText).Where(a => a.Length > 0).ToList();
            args = args.Where(a => !IsNamedArg(a)).ToList();
            bool isLisp = m.Groups["kind"].Value.Equals("LispFunction", StringComparison.OrdinalIgnoreCase);

            string? group = null, name, local = null, flags = "";
            if (isLisp)
            {
                if (args.Count == 0) continue;
                name = ArgName(args[0]);
            }
            else
            {
                int flagAt = args.FindIndex(IsFlagsArg);
                var strs = (flagAt >= 0 ? args.Take(flagAt) : args.TakeWhile(a => !a.StartsWith("typeof", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("GetType", StringComparison.OrdinalIgnoreCase))).ToList();
                if (flagAt >= 0) flags = NormalizeFlags(args[flagAt]);
                if (strs.Count == 0) continue;
                if (strs.Count == 1) name = ArgName(strs[0]);
                else
                {
                    group = ArgName(strs[0]);
                    name = ArgName(strs[1]);
                    if (strs.Count >= 3) local = ArgName(strs[2]);
                }
            }
            if (string.IsNullOrEmpty(name)) continue;

            spans ??= MethodFinder.Find(ct);
            // the attributed method: first method starting after the attribute with no block boundary between
            MethodSpan? target = null;
            foreach (var sp in spans.OrderBy(x => x.Start))
            {
                if (sp.Start <= close) continue;
                var between = s[(close + 1)..sp.Start];
                if (between.IndexOfAny(new[] { '{', '}', ';' }) >= 0) break;
                target = sp;
                break;
            }
            if (target == null) continue;

            var entry = new CommandEntry
            {
                Name = name.ToUpperInvariant(),
                LocalName = local,
                Group = group,
                Flags = flags,
                Kind = isLisp ? EntryKind.LispFunction : EntryKind.CommandMethod,
                Lang = f.Lang,
                Project = f.Project,
                Source = ct.Ref(m.Index),
                Method = target.QualifiedName,
            };
            entry.Extra["declared"] = name;
            add(entry);
        }
    }

    private static bool InAttributeList(string s, int comma, bool vb)
    {
        // walk back to the opening bracket of the list without crossing a closing one
        char open = vb ? '<' : '[', closeCh = vb ? '>' : ']';
        int depth = 0;
        for (int i = comma; i >= 0; i--)
        {
            if (s[i] == '\n' && vb && !(i > 0 && s[i - 1] == '_')) { /* VB lists may continue with _ */ }
            if (s[i] == closeCh) depth++;
            else if (s[i] == open) { if (depth == 0) return true; depth--; }
            else if (s[i] == ';' || s[i] == '{' || s[i] == '}') return false;
        }
        return false;
    }

    private static bool IsNamedArg(string a) =>
        Rx.IsMatch(a, @"^\w+\s*:=") || Rx.IsMatch(a, @"^\w+\s*=(?!=)");

    private static bool IsFlagsArg(string a)
    {
        if (a.Contains("CommandFlags", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = Rx.Split(a, @"\||\bOr\b").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 0 && parts.All(p => KnownFlags.Contains(p.Split('.').Last()));
    }

    private static string NormalizeFlags(string a) =>
        string.Join(" | ", Rx.Split(a, @"\||\bOr\b")
            .Select(p => p.Trim().Trim('(', ')').Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.Split('.').Last()));

    private static string ArgName(string arg) => CodeText.Literal(arg) ?? arg.Trim();

    // ---------------------------------------------------------------- C++

    private static void ScanCpp(SourceFile f, Dictionary<string, string> symbols, Dictionary<string, HashSet<string>> cppDefs,
        IReadOnlyList<SourceFile> allFiles, Action<CommandEntry> add)
    {
        var ct = new CodeText(f);
        string s = ct.Code;
        List<MethodSpan>? spans = null;

        string Res(string expr)
        {
            var lit = CodeText.Literal(expr);
            if (lit != null) return lit;
            var id = expr.Trim();
            return symbols.TryGetValue(id, out var v) ? v : id;
        }
        bool Resolved(string expr) => CodeText.Literal(expr) != null || symbols.ContainsKey(expr.Trim());

        foreach (Match m in AddCmd.Matches(s))
        {
            var argText = CodeText.ArgsAt(s, m.Index + m.Length - 1, out _);
            if (argText == null) continue;
            var args = CodeText.SplitArgs(argText);
            if (args.Count < 5) continue;
            string fn = CleanFnPtr(args[4]);
            spans ??= MethodFinder.Find(ct);
            string method = ResolveFn(fn, spans);
            if (spans.Any(sp => sp.QualifiedName == method))
            {
                var defs = DefsOf(cppDefs, allFiles, method);
                if (defs.Count > 1) method += "@" + f.RelPath;
            }
            string declared = Res(args[1]);
            bool ok = Resolved(args[1]);
            var e = new CommandEntry
            {
                Name = ok ? declared.ToUpperInvariant() : declared,
                Group = Res(args[0]),
                LocalName = Res(args[2]),
                Flags = args[3].Trim(),
                Kind = EntryKind.ArxAddCommand,
                Lang = Lang.Cpp,
                Project = f.Project,
                Source = ct.Ref(m.Index),
                Method = method,
            };
            e.Extra["declared"] = declared;
            if (!ok) e.Extra["unresolved"] = "true";
            if (!Resolved(args[0])) e.Extra["groupUnresolved"] = "true";
            add(e);
        }

        foreach (Match m in AcedMacro.Matches(s))
        {
            // skip the macro's own #define line
            int ls = s.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            if (s[ls..m.Index].Contains("#")) continue;
            var argText = CodeText.ArgsAt(s, m.Index + m.Length - 1, out _);
            if (argText == null) continue;
            var a = CodeText.SplitArgs(argText);
            string macro = m.Groups["m"].Value;
            CommandEntry e;
            if (macro.StartsWith("ACED_ARXCOMMAND"))
            {
                if (a.Count < 5) continue;
                string cls = a[0].Trim(), group = a[1].Trim(), glob = a[2].Trim(), loc = a[3].Trim();
                string globName = glob.TrimStart('_');
                e = new CommandEntry
                {
                    Name = globName.ToUpperInvariant(),
                    LocalName = loc,
                    Group = group,
                    Flags = a[4].Trim(),
                    Kind = EntryKind.ArxCommandEntry,
                    Lang = Lang.Cpp,
                    Project = f.Project,
                    Source = ct.Ref(m.Index),
                    Method = $"{cls}::{group}{glob}",
                };
                e.Extra["declared"] = glob;
                e.Extra["macro"] = macro;
                if (a.Count > 5) e.Extra["uiContext"] = a[5].Trim();
            }
            else
            {
                // ACED_ADSSYMBOL_ENTRY_AUTO(classname, name, regFunc): generates static int ads_<name>()
                if (a.Count < 2) continue;
                string cls = a[0].Trim(), name = a[1].Trim();
                e = new CommandEntry
                {
                    Name = name.ToUpperInvariant(),
                    Kind = EntryKind.LispFunction,
                    Lang = Lang.Cpp,
                    Project = f.Project,
                    Source = ct.Ref(m.Index),
                    Method = $"{cls}::ads_{name}",
                };
                e.Extra["declared"] = name;
                e.Extra["macro"] = macro;
                if (macro.Contains("ADSCOMMAND")) e.Extra["lisp"] = "c:" + name;
                if (a.Count > 2) e.Extra["regFunc"] = a[2].Trim();
            }
            add(e);
        }
    }

    private static HashSet<string> DefsOf(Dictionary<string, HashSet<string>> cache, IReadOnlyList<SourceFile> files, string qualified)
    {
        if (cache.Count == 0)
        {
            foreach (var cf in files.Where(x => x.Lang == Lang.Cpp && !string.IsNullOrEmpty(x.Text)))
                foreach (var sp in CppFunctions.Find(new CodeText(cf)))
                {
                    if (!cache.TryGetValue(sp.QualifiedName, out var set)) cache[sp.QualifiedName] = set = new();
                    set.Add(cf.RelPath);
                }
            cache[""] = new();
        }
        return cache.GetValueOrDefault(qualified) ?? new();
    }

    private static string CleanFnPtr(string a)
    {
        a = a.Trim();
        a = Rx.Replace(a, @"^\(\s*[\w:\s\*&\(\)]*?\)\s*(?=[&\w])", ""); // (AcRxFunctionPtr) cast
        return a.TrimStart('&', ' ').Trim();
    }

    private static string ResolveFn(string fn, List<MethodSpan> spans)
    {
        if (fn.Length == 0) return fn;
        string shortName = fn.Contains("::") ? fn[(fn.LastIndexOf("::") + 2)..] : fn;
        if (fn.Contains("::"))
        {
            var exact = spans.FirstOrDefault(s => s.QualifiedName == fn);
            return exact?.QualifiedName ?? fn;
        }
        var cands = spans.Where(s => s.Name == shortName).ToList();
        return cands.Count == 1 ? cands[0].QualifiedName : fn;
    }
}
