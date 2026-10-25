// Lightweight source-text helpers shared by the C#, VB.NET and C++ scanners.
// No real parser: we blank out comments, keep offsets stable, and match braces / Sub..End Sub.
using System.Text;
using System.Text.RegularExpressions;

namespace Retrace.Engine.Util;

/// <summary>A method (or C++ function) found in a file. Offsets index into CodeText.Code.</summary>
public sealed record MethodSpan(
    string Namespace, string ClassName, string Name, string Signature,
    int Start, int BodyStart, int BodyEnd, int Line)
{
    public string FullClass => string.IsNullOrEmpty(Namespace) ? ClassName : $"{Namespace}.{ClassName}";
    /// <summary>"Namespace.Class.Method" for .NET, "Class::Method" or "Function" for C++.</summary>
    public string QualifiedName { get; init; } = "";
}

public sealed class CodeText
{
    public SourceFile File { get; }
    /// <summary>Text with comments replaced by spaces (newlines kept). String literals intact.</summary>
    public string Code { get; }
    private readonly int[] _lineStarts;

    public CodeText(SourceFile file)
    {
        File = file;
        Code = file.Lang == Lang.VbNet ? StripVbComments(file.Text) : StripCComments(file.Text);
        var starts = new List<int> { 0 };
        for (int i = 0; i < Code.Length; i++) if (Code[i] == '\n') starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    /// <summary>1-based line number of an offset.</summary>
    public int LineOf(int offset)
    {
        int i = Array.BinarySearch(_lineStarts, offset);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    public SourceRef Ref(int offset) => new(File.RelPath, LineOf(offset));

    // ------------------------------------------------------------ comment stripping

    /// <summary>Blank // and /* */ comments (C#, C++), preserving length and newlines.
    /// Handles "..." , '...', C# verbatim @"..." and raw-ish strings well enough.</summary>
    public static string StripCComments(string s)
    {
        var sb = new StringBuilder(s);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n') sb[i++] = ' ';
            }
            else if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
                {
                    if (s[i] != '\n') sb[i] = ' ';
                    i++;
                }
                if (i < n) { sb[i] = ' '; sb[i + 1] = ' '; i += 2; }
            }
            else if (c == '@' && i + 1 < n && s[i + 1] == '"')
            {
                i += 2;
                while (i < n)
                {
                    if (s[i] == '"' && i + 1 < n && s[i + 1] == '"') { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
            }
            else if (c == '"' || c == '\'')
            {
                i = SkipQuoted(s, i, c);
            }
            else i++;
        }
        return sb.ToString();
    }

    private static int SkipQuoted(string s, int i, char q)
    {
        i++;
        while (i < s.Length && s[i] != q && s[i] != '\n')
        {
            if (s[i] == '\\') i++;
            i++;
        }
        return Math.Min(i + 1, s.Length);
    }

    /// <summary>Blank VB comments (' and REM) outside string literals.</summary>
    public static string StripVbComments(string s)
    {
        var sb = new StringBuilder(s);
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n') { inStr = false; continue; }
            if (c == '"') { inStr = !inStr; continue; }
            if (!inStr && c == '\'')
            {
                while (i < s.Length && s[i] != '\n') sb[i++] = ' ';
                i--;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ braces and args

    /// <summary>Offset of the brace matching the one at `open` ('{', '(' or '['), skipping strings. -1 if none.</summary>
    public static int MatchBrace(string s, int open)
    {
        char o = s[open];
        char c = o switch { '{' => '}', '(' => ')', '[' => ']', _ => throw new ArgumentException("not a brace") };
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '"' || ch == '\'')
            {
                if (ch == '"' && i > 0 && s[i - 1] == '@')
                {
                    i++;
                    while (i < s.Length && !(s[i] == '"' && (i + 1 >= s.Length || s[i + 1] != '"'))) i += s[i] == '"' ? 2 : 1;
                    continue;
                }
                i = SkipQuoted(s, i, ch) - 1;
                continue;
            }
            if (ch == o) depth++;
            else if (ch == c && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Split a call's argument text (without the outer parens) on top-level commas.</summary>
    public static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char ch = args[i];
            if (ch == '"' || ch == '\'') { i = SkipQuoted(args, i, ch) - 1; continue; }
            if (ch is '(' or '{' or '[' or '<') depth++;
            else if (ch is ')' or '}' or ']' or '>') depth--;
            else if (ch == ',' && depth == 0)
            {
                result.Add(args[start..i].Trim());
                start = i + 1;
            }
        }
        var last = args[start..].Trim();
        if (last.Length > 0 || result.Count > 0) result.Add(last);
        return result;
    }

    /// <summary>Find "name(" at `at` and return the argument text inside the parens, or null.</summary>
    public static string? ArgsAt(string s, int parenOffset, out int closeOffset)
    {
        closeOffset = -1;
        if (parenOffset < 0 || parenOffset >= s.Length || s[parenOffset] != '(') return null;
        closeOffset = MatchBrace(s, parenOffset);
        return closeOffset < 0 ? null : s[(parenOffset + 1)..closeOffset];
    }

    private static readonly Regex StringLit = Rx.Create(
        "^(?:_T\\(|L|TEXT\\(|ACRX_T\\()?\\s*@?\"((?:[^\"\\\\]|\\\\.|\"\")*)\"\\s*\\)?$",
        RegexOptions.Compiled);

    /// <summary>Value of a single string literal expression ("x", @"x", L"x", _T("x"), ACRX_T("x")), else null.</summary>
    public static string? Literal(string expr)
    {
        var m = StringLit.Match(expr.Trim());
        if (!m.Success) return null;
        return m.Groups[1].Value
            .Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"")
            .Replace("\"\"", "\"").Replace("\\\\", "\\");
    }

    // ------------------------------------------------------------ methods

    private static readonly Regex CsNamespace = Rx.Create(@"\bnamespace\s+([\w.]+)\s*[;{]", RegexOptions.Compiled);
    private static readonly Regex CsType = Rx.Create(@"\b(?:class|struct|interface|record|module)\s+(\w+)", RegexOptions.Compiled);
    // modifiers + return type + name + ( ... ) then '{' or '=>'
    private static readonly Regex CsMethod = Rx.Create(
        @"(?<=[;{}\]\n]\s*)(?:(?:public|private|protected|internal|static|virtual|override|async|sealed|extern|unsafe|new|partial)\s+)*" +
        @"(?:[\w.<>\[\],?]+\s+)?(?<name>\w+)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled);
    private static readonly HashSet<string> NotMethods = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "else",
        "typeof", "sizeof", "nameof", "base", "this", "throw", "fixed", "do", "when", "get", "set", "default",
    };

    /// <summary>All methods/functions in the file with their bodies. C# and C++ use braces, VB uses Sub/Function blocks.</summary>
    public List<MethodSpan> Methods()
    {
        return File.Lang switch
        {
            // linear-time finders; regex versions backtracked catastrophically on AssemblyInfo.vb and long C++ lines
            Lang.VbNet => Scan.Commands.VbFunctions.Find(this),
            Lang.Cpp => Scan.Commands.CppFunctions.Find(this),
            _ => CsMethods(),
        };
    }

    private List<MethodSpan> CsMethods()
    {
        var list = new List<MethodSpan>();
        string s = Code;
        string ns = CsNamespace.Match(s) is { Success: true } nm ? nm.Groups[1].Value : "";
        // map each type's body span so we know the enclosing class of a method
        var types = new List<(string name, int start, int end)>();
        foreach (Match t in CsType.Matches(s))
        {
            int open = s.IndexOf('{', t.Index);
            int semi = s.IndexOf(';', t.Index);
            if (open < 0 || (semi >= 0 && semi < open && !s[t.Index..semi].Contains('('))) continue;
            int close = MatchBrace(s, open);
            if (close > 0) types.Add((t.Groups[1].Value, open, close));
        }
        foreach (Match m in CsMethod.Matches(s))
        {
            string name = m.Groups["name"].Value;
            if (NotMethods.Contains(name)) continue;
            int paren = m.Index + m.Length - 1;
            int closeParen = MatchBrace(s, paren);
            if (closeParen < 0) continue;
            int k = closeParen + 1;
            while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
            // skip constructor initializer ": base(...)" and generic constraints "where T : ..."
            if (k < s.Length && s[k] == ':' && !(k + 1 < s.Length && s[k + 1] == ':'))
            {
                int brace = s.IndexOf('{', k);
                if (brace < 0) continue;
                k = brace;
            }
            while (k < s.Length && s.AsSpan(k).StartsWith("where ")) { int b = s.IndexOf('{', k); if (b < 0) break; k = b; }
            int bodyStart, bodyEnd;
            if (k < s.Length && s[k] == '{') { bodyStart = k; bodyEnd = MatchBrace(s, k); }
            else if (k + 1 < s.Length && s[k] == '=' && s[k + 1] == '>') { bodyStart = k; bodyEnd = s.IndexOf(';', k); }
            else continue;
            if (bodyEnd < 0) continue;
            // a method must sit directly in a type body, and must have a return type or modifier before its name
            var owner = types.Where(t => t.start < m.Index && m.Index < t.end).OrderByDescending(t => t.start).FirstOrDefault();
            if (owner.name == null) continue;
            string head = s[m.Index..(m.Index + m.Length)].Trim();
            if (!head.Contains(' ') && name != owner.name) continue; // bare call like Foo(...) { — not a declaration
            int start = m.Index;
            while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
            string sig = Rx.Replace(s[start..(closeParen + 1)], @"\s+", " ");
            list.Add(new MethodSpan(ns, owner.name, name, sig, start, bodyStart, bodyEnd, LineOf(start))
            {
                QualifiedName = $"{(ns.Length > 0 ? ns + "." : "")}{owner.name}.{name}",
            });
        }
        // drop spans nested inside another method (lambdas / local functions matched as methods)
        return list.Where(a => !list.Any(b => b != a && b.BodyStart < a.Start && a.BodyEnd <= b.BodyEnd)).ToList();
    }
}
