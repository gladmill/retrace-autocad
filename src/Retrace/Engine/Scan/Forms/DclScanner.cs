using System.Text;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

/// <summary>AutoCAD DCL: top-level `name : dialog { ... }` become forms; tiles nest as controls.</summary>
internal static class DclScanner
{
    private readonly record struct Tok(char Kind, string Text, int Line); // Kind: 'i' ident/number, 's' string, or the punctuation char

    public static IEnumerable<UiForm> Scan(IReadOnlyList<SourceFile> files)
    {
        var dcls = files.Where(f => f.Lang == Lang.Dcl).Select(f => (f, toks: Tokenize(CodeText.StripCComments(f.Text)))).ToList();
        // prototypes (top-level `name : base {}` that are not dialogs): name -> base tile
        var protos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, t) in dcls)
            for (int i = 0; i + 3 < t.Count; i++)
                if (t[i].Kind == 'i' && t[i + 1].Kind == ':' && t[i + 2].Kind == 'i' && t[i + 3].Kind == '{' && t[i + 2].Text != "dialog")
                    protos.TryAdd(t[i].Text, t[i + 2].Text);

        foreach (var (f, toks) in dcls)
        {
            var fileName = Path.GetFileNameWithoutExtension(f.RelPath);
            int p = 0;
            while (p < toks.Count)
            {
                // name : base {
                if (toks[p].Kind == 'i' && p + 3 < toks.Count && toks[p + 1].Kind == ':' && toks[p + 2].Kind == 'i' && toks[p + 3].Kind == '{')
                {
                    var name = toks[p].Text; var baseName = toks[p + 2].Text; int line = toks[p].Line;
                    int bodyStart = p + 4;
                    int end = SkipBlock(toks, bodyStart);
                    if (baseName == "dialog")
                    {
                        var form = new UiForm
                        {
                            Id = $"dcl.{fileName}.{name}", Title = name, Kind = UiFormKind.Dcl, Project = f.Project,
                            Source = new SourceRef(f.RelPath, line), ResourceId = name, Units = "auto",
                        };
                        var holder = new UiControl();
                        int q = bodyStart;
                        ParseBody(toks, ref q, holder, f);
                        form.Controls.AddRange(holder.Children);
                        foreach (var c in holder.Children) ResolveBase(c, protos);
                        foreach (var kv in holder.Extra)
                        {
                            if (kv.Key == "label") form.Title = kv.Value;
                            else form.Extra[kv.Key] = kv.Value;
                        }
                        yield return form;
                    }
                    p = end;
                }
                else p++;
            }
        }
    }

    private static void ResolveBase(UiControl c, Dictionary<string, string> protos)
    {
        var b = c.Type; int guard = 0;
        while (protos.TryGetValue(b, out var next) && guard++ < 10) b = next;
        if (b != c.Type) c.Extra["base"] = b;
        foreach (var k in c.Children) ResolveBase(k, protos);
    }

    /// <summary>Index just after the '}' matching an already-consumed '{' (start = first token inside).</summary>
    private static int SkipBlock(List<Tok> toks, int start)
    {
        int depth = 1, i = start;
        for (; i < toks.Count && depth > 0; i++)
        {
            if (toks[i].Kind == '{') depth++;
            else if (toks[i].Kind == '}') depth--;
        }
        return i;
    }

    /// <summary>Parse tile body items into `owner` until the closing '}' (consumed).</summary>
    private static void ParseBody(List<Tok> toks, ref int p, UiControl owner, SourceFile f)
    {
        while (p < toks.Count)
        {
            var t = toks[p];
            if (t.Kind == '}') { p++; return; }
            if (t.Kind == ';') { p++; continue; }
            // attribute:  key = value ;
            if (t.Kind == 'i' && p + 1 < toks.Count && toks[p + 1].Kind == '=')
            {
                p += 2;
                var sb = new StringBuilder();
                while (p < toks.Count && toks[p].Kind != ';' && toks[p].Kind != '}') { if (sb.Length > 0) sb.Append(' '); sb.Append(toks[p].Text); p++; }
                SetAttr(owner, t.Text, sb.ToString());
                continue;
            }
            // nested tile:  : name { ... }   or   : name ;   or   id : name { ... }
            string? tile = null; int line = t.Line;
            if (t.Kind == ':' && p + 1 < toks.Count && toks[p + 1].Kind == 'i') { tile = toks[p + 1].Text; p += 2; }
            else if (t.Kind == 'i' && p + 2 < toks.Count && toks[p + 1].Kind == ':' && toks[p + 2].Kind == 'i') { tile = toks[p + 2].Text; p += 3; }
            else if (t.Kind == 'i') { tile = t.Text; p++; }                  // bare tile: ok_cancel;
            else { p++; continue; }
            var c = new UiControl { Type = tile, Source = new SourceRef(f.RelPath, line) };
            if (p < toks.Count && toks[p].Kind == '{') { p++; ParseBody(toks, ref p, c, f); }
            c.Name = c.Extra.TryGetValue("key", out var k) ? k : "";
            if (c.Extra.Remove("label", out var lbl)) c.Text = lbl;
            else if (c.Extra.TryGetValue("value", out var val) && tile is "text" or "text_part" or "paragraph") c.Text = val;
            owner.Children.Add(c);
        }
    }

    private static void SetAttr(UiControl owner, string key, string value)
    {
        owner.Extra[key.ToLowerInvariant()] = value;
    }

    private static List<Tok> Tokenize(string s)
    {
        var r = new List<Tok>();
        int line = 1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n') { line++; continue; }
            if (char.IsWhiteSpace(c)) continue;
            if (c == '"')
            {
                var sb = new StringBuilder(); int startLine = line; i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    }
                    else { if (s[i] == '\n') line++; sb.Append(s[i]); }
                    i++;
                }
                r.Add(new Tok('s', sb.ToString(), startLine));
            }
            else if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '@')
            {
                int st = i;
                while (i + 1 < s.Length && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] is '_' or '.' or '-')) i++;
                var text = s[st..(i + 1)];
                if (text == "@") continue;
                r.Add(new Tok('i', text, line));
            }
            else r.Add(new Tok(c, c.ToString(), line));
        }
        return r;
    }
}
