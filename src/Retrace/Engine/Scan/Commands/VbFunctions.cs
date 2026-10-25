using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Commands;

/// <summary>Kept for callers in this folder; CodeText.Methods() now dispatches to the linear finders itself.</summary>
internal static class MethodFinder
{
    public static List<MethodSpan> Find(CodeText ct) => ct.Methods();
}

internal static class VbFunctions
{
    private static readonly Regex Ns = Rx.Create(@"^[ \t]*Namespace[ \t]+([\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex Type = Rx.Create(@"^[ \t]*(?:(?:Public|Private|Friend|Partial|NotInheritable|MustInherit|Shared)[ \t]+)*(?:Class|Module|Structure)[ \t]+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex Decl = Rx.Create(
        @"^(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|Overloads|Async|Static|MustOverride|NotOverridable|Shadows)[ \t]+)*(?<kind>Sub|Function)[ \t]+(?<name>\w+)[ \t]*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<MethodSpan> Find(CodeText ct)
    {
        string s = ct.Code;
        var list = new List<MethodSpan>();
        string ns = Ns.Match(s) is { Success: true } nm ? nm.Groups[1].Value : "";
        var types = Type.Matches(s).Select(t => (name: t.Groups[1].Value, at: t.Index)).ToList();
        int lineStart = 0;
        while (lineStart < s.Length)
        {
            int nl = s.IndexOf('\n', lineStart);
            int lineEnd = nl < 0 ? s.Length : nl;
            int i = lineStart;
            while (i < lineEnd && (s[i] == ' ' || s[i] == '\t')) i++;
            // skip leading attribute blocks "<A(...)> _"
            while (i < lineEnd && s[i] == '<')
            {
                int gt = s.IndexOf('>', i, lineEnd - i);
                if (gt < 0) break;
                i = gt + 1;
                while (i < lineEnd && (s[i] == ' ' || s[i] == '\t' || s[i] == '_')) i++;
            }
            if (i < lineEnd && (s[i] == 'S' || s[i] == 's' || s[i] == 'F' || s[i] == 'f' || char.IsLetter(s[i])))
            {
                var m = Decl.Match(s[i..lineEnd]);
                if (m.Success)
                {
                    string kind = m.Groups["kind"].Value;
                    var end = Rx.Create(@"^[ \t]*End[ \t]+" + kind + @"\b", RegexOptions.Multiline | RegexOptions.IgnoreCase).Match(s, lineEnd);
                    if (end.Success)
                    {
                        int nameAt = i + m.Groups["kind"].Index;
                        int openParen = i + m.Length - 1;
                        int closeParen = CodeText.MatchBrace(s, openParen);
                        string owner = types.Where(t => t.at < nameAt).Select(t => t.name).LastOrDefault() ?? "";
                        string name = m.Groups["name"].Value;
                        string sig = s[nameAt..(closeParen > 0 ? closeParen + 1 : lineEnd)].Trim();
                        int bodyStart = closeParen > 0 ? Math.Max(closeParen, s.IndexOf('\n', closeParen) is var q && q >= 0 ? q : closeParen) : lineEnd;
                        list.Add(new MethodSpan(ns, owner, name, Rx.Replace(sig, @"\s+", " "), nameAt, bodyStart, end.Index, ct.LineOf(nameAt))
                        {
                            QualifiedName = $"{(ns.Length > 0 ? ns + "." : "")}{(owner.Length > 0 ? owner + "." : "")}{name}",
                        });
                    }
                }
            }
            lineStart = lineEnd + 1;
        }
        return list;
    }
}
