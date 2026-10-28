using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Map;

/// <summary>What a ribbon item can run, by name: commands, LISP functions, methods, classes and LISP files, found in the chosen folders.
/// Filled folder by folder in the user's order. The definitions of one name come back in that order, then by file path and line.
/// Text based like the scanners it reads from, so it finds less rather than something wrong.</summary>
internal sealed class MapIndex
{
    private sealed record Hit(int Folder, MapDef Def);
    private sealed record MethodHit(int Folder, string Qualified, Lang Lang, string? Project, MapSource Source);
    private sealed record ClassHit(int Folder, string Full, string Simple, string Namespace, Lang Lang, string? Project, MapSource Source, MapSource? Execute);

    private readonly Dictionary<string, List<Hit>> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Hit>> _lisp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Hit>> _files = new(StringComparer.OrdinalIgnoreCase);
    // by the qualified name and by each dotted tail of it with two parts or more ("Ns.Class.Method", "Class.Method"), as FlowAnalyzer.Declares matches
    private readonly Dictionary<string, List<MethodHit>> _methods = new(StringComparer.OrdinalIgnoreCase);
    // by the simple name
    private readonly Dictionary<string, List<ClassHit>> _classes = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------ filling

    /// <summary>Adds what one folder defines. <paramref name="commands"/> are its scanned entry points (commands and LISP).</summary>
    /// <returns>The number of code files that could not be read for methods and classes.</returns>
    public int AddFolder(int folderIndex, string folder, IReadOnlyList<SourceFile> files, IEnumerable<CommandEntry> commands, CancellationToken ct)
    {
        foreach (var e in commands) AddCommand(folderIndex, folder, e);
        int failed = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (IsLispFile(f.AbsPath)) AddLispFile(folderIndex, folder, f);
            else if ((f.Lang is Lang.CSharp or Lang.VbNet or Lang.Cpp) && f.Text.Length > 0)
            {
                try { AddCode(folderIndex, folder, f); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }   // a pattern that ran out of time, a file the finders cannot split: left out
            }
        }
        return failed;
    }

    private static bool IsLispFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".lsp" or ".mnl" or ".fas" or ".vlx";

    private void AddCommand(int folder, string root, CommandEntry e)
    {
        var source = RibbonMapper.Source(root, e.Source);
        // the C++ scanner marks a function that several files define with "@file" after its name
        var symbol = e.Lang == Lang.Cpp && e.Method.IndexOf('@') is var at and > 0 ? e.Method[..at] : e.Method;
        MapDef Def(string what, string name, string entry) => new(what, name, e.Lang, entry, symbol, source, e.Project);
        var vb = e.Lang == Lang.VbNet;
        switch (e.Kind)
        {
            case EntryKind.CommandMethod:
                var entry = vb ? "<CommandMethod>" : "[CommandMethod]";
                Put(_commands, e.Name.ToUpperInvariant(), new Hit(folder, Def("command", e.Name.ToUpperInvariant(), entry)));
                if (!string.IsNullOrWhiteSpace(e.LocalName) && !string.Equals(e.LocalName, e.Name, StringComparison.OrdinalIgnoreCase))
                    Put(_commands, e.LocalName.Trim().ToUpperInvariant(), new Hit(folder, Def("command", e.LocalName.Trim().ToUpperInvariant(), entry)));
                break;
            // a name the scanner could not read (a variable, not a literal) would be a made-up key
            case EntryKind.ArxAddCommand when !e.Extra.ContainsKey("unresolved"):
                Put(_commands, e.Name.ToUpperInvariant(), new Hit(folder, Def("command", e.Name.ToUpperInvariant(), "addCommand")));
                break;
            case EntryKind.ArxCommandEntry:
                Put(_commands, e.Name.ToUpperInvariant(), new Hit(folder, Def("command", e.Name.ToUpperInvariant(), e.Extra.GetValueOrDefault("macro") ?? "ACED_ARXCOMMAND_ENTRY_AUTO")));
                break;
            case EntryKind.LispCommand:
                Put(_commands, e.Name.ToUpperInvariant(), new Hit(folder, Def("command", e.Name.ToUpperInvariant(), e.Extra.GetValueOrDefault("form") ?? "defun c:")));
                break;
            case EntryKind.LispDefun:
                Put(_lisp, e.Name, new Hit(folder, Def("lisp", e.Name, "defun")));
                break;
            case EntryKind.LispFunction:
                var lisp = e.Extra.GetValueOrDefault("declared") ?? e.Name;
                var text = e.Lang == Lang.Cpp ? e.Extra.GetValueOrDefault("macro") ?? "LispFunction" : vb ? "<LispFunction>" : "[LispFunction]";
                Put(_lisp, lisp, new Hit(folder, Def("lisp", lisp, text)));
                break;
        }
    }

    private void AddLispFile(int folder, string root, SourceFile f)
    {
        var name = Path.GetFileName(f.AbsPath);
        var lisp = f.Lang == Lang.Lisp;
        var source = new MapSource(f.AbsPath, 0, root);
        Put(_files, name, new Hit(folder, new MapDef("file", name, lisp ? Lang.Lisp : Lang.Other, "file", name, source, f.Project)));
    }

    private void AddCode(int folder, string root, SourceFile f)
    {
        var ct = new CodeText(f);
        var spans = ct.Methods();
        MapSource At(int line) => RibbonMapper.Source(root, new SourceRef(f.RelPath, line));
        foreach (var sp in spans)
        {
            if (sp.QualifiedName.Length == 0) continue;
            var hit = new MethodHit(folder, sp.QualifiedName, f.Lang, f.Project, At(sp.Line));
            foreach (var key in Tails(Dotted(sp.QualifiedName))) Put(_methods, key, hit);
        }
        var executes = spans.Where(sp => sp.Name == "Execute").ToList();
        foreach (var c in ClassDecls(ct, executes))
        {
            var execute = c.Execute is { } line ? At(line) : null;
            Put(_classes, c.Name, new ClassHit(folder, c.Full, c.Name, c.Namespace, f.Lang, f.Project, At(c.Line), execute));
        }
    }

    private static void Put<T>(Dictionary<string, List<T>> d, string key, T value)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new List<T>();
        list.Add(value);
    }

    /// <summary>"Ns.Class::Method" and "Ns.Class.Method" are the same name.</summary>
    private static string Dotted(string name) => name.Replace("::", ".").Replace('+', '.').Trim();

    /// <summary>The whole name and each tail of two parts or more: "A.B.C" gives "A.B.C", "B.C".</summary>
    private static IEnumerable<string> Tails(string name)
    {
        yield return name;
        for (int dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
        {
            var tail = name[(dot + 1)..];
            if (tail.Contains('.')) yield return tail;
        }
    }

    // ------------------------------------------------------------ looking up

    /// <summary>True when a command of this name (any case) is defined.</summary>
    public bool HasCommand(string name) => _commands.ContainsKey(name);

    /// <summary>True when the method is declared: its qualified name, or a dotted tail of one ("Class.Method"). A method name alone does not count.</summary>
    public bool Declares(string method)
    {
        var name = Dotted(method);
        return name.Contains('.') && _methods.ContainsKey(name);
    }

    public List<MapDef> Commands(string name) => Ordered(_commands, name.Trim());

    public List<MapDef> LispFunctions(string name) => Ordered(_lisp, name.Trim());

    /// <summary>The files a (load "x") can mean: x, x.lsp, x.fas, x.vlx, x.mnl, whatever folder the string names.</summary>
    public List<MapDef> Files(string loadString)
    {
        var s = loadString.Trim();
        var name = s[(s.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        if (name.Length == 0) return [];
        var hits = new List<Hit>();
        foreach (var candidate in new[] { name, name + ".lsp", name + ".fas", name + ".vlx", name + ".mnl" }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_files.TryGetValue(candidate, out var l)) hits.AddRange(l);
        return Sorted(hits).Select(h => h.Def with { Name = name }).ToList();
    }

    public List<MapDef> Methods(string method)
    {
        var name = Dotted(method);
        if (!_methods.TryGetValue(name, out var list)) return [];
        return Sorted(list, h => h.Folder, h => h.Source)
            .Select(h => new MapDef("method", name, h.Lang, "method", h.Qualified, h.Source, h.Project)).ToList();
    }

    /// <summary>The class a type name means, as the live ribbon reports it ("Ns.Outer+Inner", the project's root namespace in front). Its
    /// Execute method is the location when it has one (a command handler runs there), else the declaration.</summary>
    public List<MapDef> Classes(string type)
    {
        var name = Dotted(type);
        var bracket = name.IndexOfAny(new[] { '`', '[', ',' });
        if (bracket >= 0) name = name[..bracket].Trim();   // generic arity, assembly-qualified suffix
        var simple = name[(name.LastIndexOf('.') + 1)..];
        if (!_classes.TryGetValue(simple, out var all)) return [];

        // the full name; else, for VB, the name with the project's root namespace in front (a project setting, not in its source)
        var found = all.Where(c => Eq(c.Full, name)).ToList();
        if (found.Count == 0)
            found = all.Where(c => c.Lang == Lang.VbNet && name.EndsWith("." + c.Full, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return [];

        // a partial class is declared in several files: the Execute method decides, else every declaration is listed
        var withExecute = Sorted(found, c => c.Folder, c => c.Execute ?? c.Source).Where(c => c.Execute != null).ToList();
        if (withExecute.Count > 0)
        {
            var c = withExecute[0];
            return [new MapDef("class", name, c.Lang, "class", c.Full + ".Execute", c.Execute!, c.Project)];
        }
        // Execute may sit in another file of a partial class
        foreach (var c in found)
        {
            var execute = Dotted(c.Namespace.Length > 0 ? c.Namespace + "." + c.Simple + ".Execute" : c.Simple + ".Execute");
            if (_methods.TryGetValue(execute, out var ms) && ms.Where(m => Eq(m.Qualified, execute)).ToList() is { Count: > 0 } exact)
            {
                var m = Sorted(exact, h => h.Folder, h => h.Source)[0];
                return [new MapDef("class", name, m.Lang, "class", m.Qualified, m.Source, m.Project)];
            }
        }
        return Sorted(found, c => c.Folder, c => c.Source).Select(c => new MapDef("class", name, c.Lang, "class", c.Full, c.Source, c.Project)).ToList();
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<MapDef> Ordered(Dictionary<string, List<Hit>> d, string key) =>
        d.TryGetValue(key, out var list) ? Sorted(list).Select(h => h.Def).ToList() : [];

    private static IEnumerable<Hit> Sorted(IEnumerable<Hit> hits) => Sorted(hits, h => h.Folder, h => h.Def.Source);

    /// <summary>Folder order, then file path, then line.</summary>
    private static List<T> Sorted<T>(IEnumerable<T> items, Func<T, int> folder, Func<T, MapSource> source) =>
        items.OrderBy(folder).ThenBy(i => source(i).Path, StringComparer.OrdinalIgnoreCase).ThenBy(i => source(i).Line).ToList();

    // ------------------------------------------------------------ classes

    private sealed record ClassDecl(string Name, string Full, string Namespace, int Line, int? Execute);

    private static readonly Regex CsType = Rx.Create(@"\b(class|struct|interface|record)\s+(?!where\b)([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex CsNamespace = Rx.Create(@"\bnamespace\s+([\w.]+)\s*([;{])", RegexOptions.Compiled);
    private static readonly Regex VbClass = Rx.Create(
        @"^[ \t]*(?:(?:Public|Private|Friend|Protected|Partial|NotInheritable|MustInherit|Shared)[ \t]+)*Class[ \t]+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex VbNamespace = Rx.Create(@"^[ \t]*(Namespace[ \t]+([\w.]+)|End[ \t]+Namespace\b)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>The classes declared in a file, with the line of each one's Execute method when the file has it
    /// (<paramref name="spans"/>: the Execute methods of the file).</summary>
    private static IEnumerable<ClassDecl> ClassDecls(CodeText ct, List<MethodSpan> spans) => ct.File.Lang switch
    {
        Lang.CSharp => CsClasses(ct, spans),
        Lang.VbNet => VbClasses(ct, spans),
        _ => CppClasses(ct, spans),
    };

    private static IEnumerable<ClassDecl> CsClasses(CodeText ct, List<MethodSpan> spans)
    {
        string s = ct.Code;
        var types = new List<(string Kind, string Name, int At, int Open, int Close)>();
        foreach (Match t in CsType.Matches(s))
        {
            int open = s.IndexOf('{', t.Index);
            int semi = s.IndexOf(';', t.Index);
            if (open < 0 || (semi >= 0 && semi < open && !s[t.Index..semi].Contains('('))) continue;   // a declaration without a body
            int close = CodeText.MatchBrace(s, open);
            if (close > 0) types.Add((t.Groups[1].Value, t.Groups[2].Value, t.Index, open, close));
        }
        // "namespace A.B { ... }" holds what is between its braces, "namespace A.B;" the rest of the file
        var spaces = new List<(string Name, int From, int To)>();
        foreach (Match n in CsNamespace.Matches(s))
        {
            if (n.Groups[2].Value == ";") { spaces.Add((n.Groups[1].Value, n.Index, s.Length)); continue; }
            int open = n.Index + n.Length - 1;
            int close = CodeText.MatchBrace(s, open);
            if (close > 0) spaces.Add((n.Groups[1].Value, open, close));
        }
        foreach (var t in types.Where(t => t.Kind == "class"))
        {
            var ns = string.Join(".", spaces.Where(n => n.From < t.At && t.At < n.To).OrderBy(n => n.From).Select(n => n.Name));
            var outer = types.Where(o => o.Open < t.At && t.At < o.Close).OrderBy(o => o.Open).Select(o => o.Name);
            var full = string.Join(".", new[] { ns }.Concat(outer).Append(t.Name).Where(p => p.Length > 0));
            var execute = spans.FirstOrDefault(m => m.Name == "Execute" && m.ClassName == t.Name && m.Start > t.Open && m.Start < t.Close);
            yield return new ClassDecl(t.Name, full, ns, ct.LineOf(t.At), execute?.Line);
        }
    }

    private static IEnumerable<ClassDecl> VbClasses(CodeText ct, List<MethodSpan> spans)
    {
        string s = ct.Code;
        var spaces = new List<(string Name, int From)>();   // open Namespace blocks, innermost last
        var events = new List<(int At, string? Open)>();
        foreach (Match n in VbNamespace.Matches(s)) events.Add((n.Index, n.Groups[2].Success ? n.Groups[2].Value : null));
        foreach (Match c in VbClass.Matches(s))
        {
            // replay the namespace blocks up to this class
            spaces.Clear();
            foreach (var e in events.TakeWhile(e => e.At < c.Index))
            {
                if (e.Open != null) spaces.Add((e.Open, e.At));
                else if (spaces.Count > 0) spaces.RemoveAt(spaces.Count - 1);
            }
            var ns = string.Join(".", spaces.Select(n => n.Name));
            var name = c.Groups[1].Value;
            var full = ns.Length > 0 ? ns + "." + name : name;
            var execute = spans.FirstOrDefault(m => m.Name == "Execute" && m.ClassName == name);
            yield return new ClassDecl(name, full, ns, ct.LineOf(c.Index), execute?.Line);
        }
    }

    /// <summary>The word that ends just before <paramref name="at"/>, skipping white space; "" when there is none.</summary>
    private static string WordBefore(string s, int at)
    {
        int end = at;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        int start = end;
        while (start > 0 && (char.IsLetterOrDigit(s[start - 1]) || s[start - 1] == '_')) start--;
        return s[start..end];
    }

    // C++: "class [export macro] Name [: bases] {". No namespaces: the name is the whole name.
    private static IEnumerable<ClassDecl> CppClasses(CodeText ct, List<MethodSpan> spans)
    {
        string s = ct.Code;
        for (int from = s.IndexOf("class", StringComparison.Ordinal); from >= 0; from = s.IndexOf("class", from + 5, StringComparison.Ordinal))
        {
            int after = from + 5;
            if (from > 0 && (char.IsLetterOrDigit(s[from - 1]) || s[from - 1] == '_')) continue;                 // subclass
            if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) continue;               // classes
            if (WordBefore(s, from) == "enum") continue;                                                           // enum class
            int end = after, limit = Math.Min(s.Length, after + 400);
            while (end < limit && s[end] is not ('{' or ';' or '(' or ')' or '<' or '>' or ',' or '=')) end++;
            if (end >= limit || s[end] != '{') continue;                                                          // a forward declaration, a template parameter
            var head = s[after..end];
            if (head.IndexOf(':') is var colon and >= 0) head = head[..colon];
            var words = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w != "final").ToArray();
            if (words.Length == 0 || !Rx.IsMatch(words[^1], @"^[A-Za-z_]\w*$")) continue;
            var name = words[^1];
            var execute = spans.FirstOrDefault(m => m.Name == "Execute" && m.ClassName == name);
            yield return new ClassDecl(name, name, "", ct.LineOf(from), execute?.Line);
        }
    }
}
