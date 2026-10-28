using System.Text;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Commands;

/// <summary>Finds AutoLISP commands and functions in .lsp / .mnl files (<see cref="Lang.Lisp"/>): (defun c:NAME ...) and
/// (vlax-add-cmd "NAME" 'fn) are <see cref="EntryKind.LispCommand"/>, (defun NAME ...) is <see cref="EntryKind.LispDefun"/>. Text based:
/// comments (; to the end of the line, ;| ... |;) and strings are skipped.</summary>
/// <remarks>Every definition is kept: a name defined twice is two entries. A LispCommand carries <c>Extra["form"]</c>, "defun c:" or
/// "vlax-add-cmd"; a LispDefun carries "defun". The entry's Method is the name as written ("C:Foo") for a defun, the function symbol for
/// vlax-add-cmd (the command name when there is none). Source is the line of the opening parenthesis.</remarks>
public static class LispScanner
{
    public static List<CommandEntry> Scan(IReadOnlyList<SourceFile> files)
    {
        var result = new List<CommandEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (f.Lang != Lang.Lisp || string.IsNullOrEmpty(f.Text)) continue;
            ScanFile(f, e =>
            {
                if (seen.Add($"{e.Name}|{e.Kind}|{e.Source.File}|{e.Source.Line}")) result.Add(e);
            });
        }
        return result;
    }

    private static void ScanFile(SourceFile f, Action<CommandEntry> add)
    {
        var s = StripComments(f.Text);
        int line = 1, counted = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { i = LispText.EndOfString(s, i); continue; }
            if (c != '(') continue;

            int at = i + 1;
            var head = Symbol(s, ref at);
            bool defun = head.Equals("defun", StringComparison.OrdinalIgnoreCase) || head.Equals("defun-q", StringComparison.OrdinalIgnoreCase);
            bool addCmd = !defun && head.Equals("vlax-add-cmd", StringComparison.OrdinalIgnoreCase);
            if (!defun && !addCmd) continue;

            while (counted < i) if (s[counted++] == '\n') line++;
            var source = new SourceRef(f.RelPath, line);
            if (defun)
            {
                var name = Symbol(s, ref at);
                if (name.Length == 0) continue;
                if (name.StartsWith("c:", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Length == 2) continue;
                    add(Entry(f, EntryKind.LispCommand, name[2..].ToUpperInvariant(), name, source, "defun c:"));
                }
                else add(Entry(f, EntryKind.LispDefun, name, name, source, "defun"));
            }
            else
            {
                // (vlax-add-cmd "NAME" 'fn ...): the name is a string, the function a quoted symbol, (function fn) or a bare symbol
                SkipSpace(s, ref at);
                if (at >= s.Length || s[at] != '"') continue;
                var name = LispText.Unquote(s, at, LispText.EndOfString(s, at));
                if (name.Trim().Length == 0) continue;
                at = LispText.EndOfString(s, at) + 1;
                add(Entry(f, EntryKind.LispCommand, name.Trim().ToUpperInvariant(), FunctionSymbol(s, at) ?? name.Trim(), source, "vlax-add-cmd"));
            }
        }
    }

    private static CommandEntry Entry(SourceFile f, EntryKind kind, string name, string method, SourceRef source, string form)
    {
        var e = new CommandEntry { Name = name, Kind = kind, Lang = Lang.Lisp, Project = f.Project, Source = source, Method = method };
        e.Extra["form"] = form;
        return e;
    }

    /// <summary>The function a vlax-add-cmd names: 'fn, (function fn), (quote fn) or fn. Null when the next thing is none of these.</summary>
    private static string? FunctionSymbol(string s, int at)
    {
        SkipSpace(s, ref at);
        if (at >= s.Length) return null;
        if (s[at] == '\'') { at++; return NonEmpty(Symbol(s, ref at)); }
        if (s[at] == '(')
        {
            at++;
            var head = Symbol(s, ref at);
            if (!head.Equals("function", StringComparison.OrdinalIgnoreCase) && !head.Equals("quote", StringComparison.OrdinalIgnoreCase)) return null;
            SkipSpace(s, ref at);
            if (at < s.Length && s[at] == '\'') at++;
            return NonEmpty(Symbol(s, ref at));
        }
        var symbol = Symbol(s, ref at);
        return symbol.Equals("nil", StringComparison.OrdinalIgnoreCase) ? null : NonEmpty(symbol);
    }

    private static string? NonEmpty(string s) => s.Length == 0 ? null : s;

    // ------------------------------------------------------------ text helpers

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>The symbol at <paramref name="i"/> after any white space: everything up to a space, a parenthesis, a quote or a ;.</summary>
    private static string Symbol(string s, ref int i)
    {
        SkipSpace(s, ref i);
        int start = i;
        while (i < s.Length && !LispText.IsDelimiter(s[i])) i++;
        return s[start..i];
    }



    /// <summary>The text with comments (; to the end of the line, ;| ... |; over any number of lines) replaced by spaces. Newlines and
    /// strings are kept, so offsets and line numbers stay the same.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text);
        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            if (c == '"') { i = LispText.EndOfString(text, i); continue; }
            if (c != ';') continue;
            if (i + 1 < n && text[i + 1] == '|')
            {
                int end = text.IndexOf("|;", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? n : end + 2;
                for (int k = i; k < stop; k++) if (text[k] != '\n' && text[k] != '\r') sb[k] = ' ';
                i = stop - 1;
            }
            else
            {
                int k = i;
                while (k < n && text[k] != '\n' && text[k] != '\r') sb[k++] = ' ';
                i = k - 1;
            }
        }
        return sb.ToString();
    }
}
