// Pure merge of engine-generated blocks into a page the owner edits by hand.
// Generated text lives between "<!-- engine:begin KEY -->" and "<!-- engine:end KEY -->" lines;
// everything outside those lines belongs to the owner and is never changed.
using System.Text;
using System.Text.RegularExpressions;

namespace Retrace.Engine.Emit;

/// <summary>One generated block: a stable key, its fresh content, and the stub inserted after it when it is new.</summary>
public sealed record GuideBlock(string Key, string Content, string Stub);

/// <summary>Text is the merged page, or null when the existing page is malformed (Problem says why).</summary>
public sealed record GuideMergeResult(string? Text, string? Problem);

public static class GuideMerge
{
    public const string RemovedNote =
        "_Removed from the source. Delete this section when no longer needed; your text below is kept._";

    private static readonly Regex Begin = Rx.Create(@"^<!-- engine:begin (.+?) -->$", RegexOptions.Compiled);
    private static readonly Regex End = Rx.Create(@"^<!-- engine:end (.+?) -->$", RegexOptions.Compiled);

    /// <summary>Keys go into HTML comments, so strip anything that could end the comment or break the line.</summary>
    public static string SafeKey(string key)
    {
        var k = Rx.Replace(key, @"[\r\n\t]+", " ").Trim();
        while (k.Contains("--")) k = k.Replace("--", "-‐");
        return k.Length == 0 ? "unnamed" : k;
    }

    /// <summary>A page that does not exist yet: the owner's preamble, then each block followed by its stub.</summary>
    public static string Create(string preamble, IReadOnlyList<GuideBlock> blocks)
    {
        var sb = new StringBuilder(preamble);
        foreach (var b in blocks) sb.Append(Piece(b, "\n"));
        return sb.ToString();
    }

    /// <summary>A block plus the blank line(s) after it. An empty stub (how-to steps) adds no owner stub at all.</summary>
    private static string Piece(GuideBlock b, string nl) =>
        Render(b.Key, b.Content, nl) + nl + (b.Stub.Length == 0 ? "" : b.Stub + nl + nl);

    private static string Render(string key, string content, string nl)
    {
        var k = SafeKey(key);
        return $"<!-- engine:begin {k} -->{nl}{Body(content, nl)}<!-- engine:end {k} -->{nl}";
    }

    /// <summary>Content with a newline at the end, newlines as the page uses them, marker lookalikes defused.</summary>
    private static string Body(string content, string nl)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var t = l.TrimEnd();
            sb.Append(Begin.IsMatch(t) || End.IsMatch(t) ? l.Replace("<!--", "&lt;!--") : l).Append(nl);
        }
        return sb.ToString();
    }

    private abstract record Seg;
    private sealed record Text(string Value) : Seg;
    private sealed record Block(string Key, string BeginLine, string EndLine, string Nl) : Seg;

    private static string Trim(string line) => line.TrimEnd('\r', '\n', ' ', '\t');

    private static (List<Seg>? segs, string? problem) Parse(string text)
    {
        var segs = new List<Seg>();
        var plain = new StringBuilder();
        string? openKey = null, openLine = null, openNl = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int stop = nl < 0 ? text.Length : nl + 1;
            var line = text[pos..stop];
            pos = stop;
            var t = Trim(line);
            var ending = line.EndsWith("\r\n") ? "\r\n" : "\n";
            var mb = Begin.Match(t);
            var me = End.Match(t);
            if (mb.Success)
            {
                if (openKey != null) return (null, $"block '{mb.Groups[1].Value}' starts inside block '{openKey}'");
                var key = mb.Groups[1].Value;
                if (!seen.Add(key)) return (null, $"duplicate block '{key}'");
                if (plain.Length > 0) { segs.Add(new Text(plain.ToString())); plain.Clear(); }
                openKey = key; openLine = line; openNl = ending;
            }
            else if (me.Success)
            {
                if (openKey == null) return (null, $"end marker '{me.Groups[1].Value}' without a begin marker");
                if (me.Groups[1].Value != openKey) return (null, $"block '{openKey}' ends with marker '{me.Groups[1].Value}'");
                segs.Add(new Block(openKey, openLine!, line, openNl!));
                openKey = null;
            }
            else if (openKey == null) plain.Append(line);
            // lines inside a block are the old generated content: dropped
        }
        if (openKey != null) return (null, $"block '{openKey}' has no end marker");
        if (plain.Length > 0) segs.Add(new Text(plain.ToString()));
        return (segs, null);
    }

    /// <summary>Merge fresh blocks into an existing page. See the rules in AGENTS.md.</summary>
    public static GuideMergeResult Merge(string existing, IReadOnlyList<GuideBlock> fresh)
    {
        var (segs, problem) = Parse(existing);
        if (segs == null) return new(null, problem);

        var freshByKey = new Dictionary<string, GuideBlock>(StringComparer.Ordinal);
        foreach (var b in fresh) freshByKey[SafeKey(b.Key)] = b;
        var existingKeys = segs.OfType<Block>().Select(b => b.Key).ToHashSet(StringComparer.Ordinal);

        // New keys go right before the next fresh key that already exists in the page, else at the end.
        var before = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var tail = new StringBuilder();
        for (int i = 0; i < fresh.Count; i++)
        {
            var key = SafeKey(fresh[i].Key);
            if (existingKeys.Contains(key)) continue;
            string? anchor = null;
            for (int j = i + 1; j < fresh.Count && anchor == null; j++)
                if (existingKeys.Contains(SafeKey(fresh[j].Key))) anchor = SafeKey(fresh[j].Key);
            StringBuilder target;
            if (anchor == null) target = tail;
            else if (!before.TryGetValue(anchor, out target!)) before[anchor] = target = new StringBuilder();
            target.Append(Piece(fresh[i], "\n"));
        }

        var sb = new StringBuilder();
        foreach (var seg in segs)
        {
            switch (seg)
            {
                case Text t: sb.Append(t.Value); break;
                case Block b:
                    if (before.TryGetValue(b.Key, out var ins)) sb.Append(ins);
                    var content = freshByKey.TryGetValue(b.Key, out var fb) ? fb.Content : RemovedNote;
                    sb.Append(b.BeginLine.EndsWith('\n') ? b.BeginLine : b.BeginLine + b.Nl)
                      .Append(Body(content, b.Nl)).Append(b.EndLine);
                    break;
            }
        }
        if (tail.Length > 0)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            var s = sb.ToString();
            if (s.Length > 0 && !s.EndsWith("\n\n") && !s.EndsWith("\n\r\n")) sb.Append('\n');
            sb.Append(tail);
        }
        return new(sb.ToString(), null);
    }
}
