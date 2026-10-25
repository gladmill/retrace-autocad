using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Commands;

/// <summary>Linear-time C++ function finder, used by CodeText.Methods() (a regex version backtracked catastrophically).</summary>
internal static class CppFunctions
{
    private static readonly HashSet<string> NotFuncs = new()
    {
        "if", "for", "while", "switch", "return", "catch", "sizeof", "else", "delete", "new", "do", "try", "BEGIN_MESSAGE_MAP",
    };
    private static readonly Regex PreprocLine = Rx.Create(@"^[ \t]*#.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<MethodSpan> Find(CodeText ct)
    {
        string s = ct.Code;
        var list = new List<MethodSpan>();
        int pos = 0;
        while (pos < s.Length)
        {
            int open = s.IndexOf('{', pos);
            if (open < 0) break;
            pos = open + 1;
            int p = open - 1;
            while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
            while (p >= 0 && (EndsWithWord(s, p, "const", out int np) || EndsWithWord(s, p, "noexcept", out np) || EndsWithWord(s, p, "override", out np)))
            {
                p = np - 1;
                while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
            }
            int segStart = open == 0 ? 0 : LastIndexOfAny(s, open - 1, ';', '{', '}') + 1;
            if (p < 0 || p < segStart || s[p] != ')') continue;
            int closeParen = p;
            var seg = s[segStart..open];
            // constructor initializer list "Foo::Foo() : a(1), b(2) {": the last ')' belongs to an initializer
            var init = Rx.Match(seg, @"\)\s*:(?!:)");
            if (init.Success) closeParen = segStart + init.Index;
            int openParen = MatchBack(s, closeParen);
            if (openParen < 0 || openParen < segStart) continue;
            int q = openParen - 1;
            while (q >= 0 && char.IsWhiteSpace(s[q])) q--;
            int nameEnd = q + 1;
            while (q >= 0 && (char.IsLetterOrDigit(s[q]) || s[q] == '_' || s[q] == ':' || s[q] == '~')) q--;
            int nameStart = q + 1;
            if (nameStart >= nameEnd) continue;
            string full = s[nameStart..nameEnd];
            string last = full.Contains("::") ? full[(full.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : full;
            if (last.Length == 0 || NotFuncs.Contains(last) || char.IsDigit(last[0])) continue;
            int bodyEnd = CodeText.MatchBrace(s, open);
            if (bodyEnd < 0) continue;
            pos = bodyEnd + 1;
            int start = segStart;
            var pm = PreprocLine.Matches(seg);
            if (pm.Count > 0) start = segStart + pm[^1].Index + pm[^1].Length;
            while (start < nameStart && char.IsWhiteSpace(s[start])) start++;
            string cls = full.Contains("::") ? full[..full.LastIndexOf("::", StringComparison.Ordinal)] : "";
            list.Add(new MethodSpan("", cls, last, Rx.Replace(s[nameStart..(closeParen + 1)], @"\s+", " "), start, open, bodyEnd, ct.LineOf(start))
            {
                QualifiedName = full,
            });
        }
        return list;
    }

    private static bool EndsWithWord(string s, int p, string w, out int wordStart)
    {
        wordStart = p + 1 - w.Length;
        if (wordStart < 0 || string.CompareOrdinal(s, wordStart, w, 0, w.Length) != 0) return false;
        return wordStart == 0 || !(char.IsLetterOrDigit(s[wordStart - 1]) || s[wordStart - 1] == '_');
    }

    private static int LastIndexOfAny(string s, int from, params char[] cs) => from < 0 ? -1 : s.LastIndexOfAny(cs, from);

    private static int MatchBack(string s, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(' && --depth == 0) return i;
            else if (s[i] is ';' or '{' or '}') return -1;
        }
        return -1;
    }
}
