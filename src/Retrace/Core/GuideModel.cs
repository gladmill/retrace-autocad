using System.Text;

namespace Retrace.Core;

// NO AutoCAD, WPF or Markdig references in this file (linked into the engine test project).

public enum PageKind { Group, Index, HowTo, Ribbon }

/// <summary>One entry of the palette's tree. Groups have no <see cref="Path"/>.</summary>
public sealed class PageNode
{
    public string Title { get; init; } = "";
    public string? Path { get; init; }
    public PageKind Kind { get; init; }
    public List<PageNode> Children { get; } = new();

    public IEnumerable<PageNode> Flatten()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.Flatten()) yield return d;
    }
}

/// <summary>Builds the page tree from ``&lt;output&gt;/guide`lt;docs`&lt;output&gt;/guide`gt;/guide`: Index, How-to (howto/*.md), Ribbon tabs grouped by source folder.</summary>
public static class GuideTree
{
    public static List<PageNode> Build(string guideDir)
    {
        var roots = new List<PageNode>();
        if (!Directory.Exists(guideDir)) return roots;

        var index = System.IO.Path.Combine(guideDir, "index.md");
        if (File.Exists(index)) roots.Add(new PageNode { Title = TitleOf(index), Path = index, Kind = PageKind.Index });

        var howtoDir = System.IO.Path.Combine(guideDir, "howto");
        if (Directory.Exists(howtoDir))
        {
            var g = new PageNode { Title = "How-to", Kind = PageKind.Group };
            foreach (var f in Sorted(Directory.EnumerateFiles(howtoDir, "*.md")))
                g.Children.Add(new PageNode { Title = TitleOf(f), Path = f, Kind = PageKind.HowTo });
            if (g.Children.Count > 0) roots.Add(g);
        }

        var ribbonDir = System.IO.Path.Combine(guideDir, "ribbon");
        if (Directory.Exists(ribbonDir))
        {
            var g = new PageNode { Title = "Ribbon tabs", Kind = PageKind.Group };
            var bySource = Directory.EnumerateFiles(ribbonDir, "*.md", SearchOption.AllDirectories)
                .GroupBy(f => System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(ribbonDir, f))!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in bySource)
            {
                var sub = new PageNode { Title = grp.Key.Length == 0 ? "(ribbon)" : grp.Key, Kind = PageKind.Group };
                foreach (var f in Sorted(grp)) sub.Children.Add(new PageNode { Title = TitleOf(f), Path = f, Kind = PageKind.Ribbon });
                g.Children.Add(sub);
            }
            if (g.Children.Count > 0) roots.Add(g);
        }
        return roots;
    }

    static IEnumerable<string> Sorted(IEnumerable<string> files) => files.OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

    /// <summary>First `# ` heading, else the file name without extension.</summary>
    public static string TitleOf(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file).Take(60))
                if (line.StartsWith("# ")) { var t = line[2..].Trim(); if (t.Length > 0) return t; }
        }
        catch (Exception) { }
        return System.IO.Path.GetFileNameWithoutExtension(file);
    }
}

/// <summary>The `&lt;!-- engine:begin KEY --&gt;` ... `&lt;!-- engine:end KEY --&gt;` blocks of a guide page.</summary>
public static class GeneratedBlocks
{
    const string Begin = "<!-- engine:begin ", End = "<!-- engine:end ", Close = " -->";

    static string? KeyOf(string line, string prefix)
    {
        var t = line.Trim();
        if (!t.StartsWith(prefix, StringComparison.Ordinal) || !t.EndsWith(Close, StringComparison.Ordinal)) return null;
        var k = t[prefix.Length..^Close.Length].Trim();
        return k.Length == 0 ? null : k;
    }

    /// <summary>key -> the block's text between the marker lines (lines joined with \n, line endings normalised). Duplicate keys: first wins.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (key == null)
            {
                var b = KeyOf(line, Begin);
                if (b != null) { key = b; sb.Clear(); }
            }
            else if (KeyOf(line, End) is { } e && e == key)
            {
                result.TryAdd(key, sb.ToString());
                key = null;
            }
            else
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
        }
        return result;
    }

    /// <summary>Keys of generated blocks that exist in both texts but differ inside. Blocks added or removed as a whole are not "edits".</summary>
    public static List<string> EditedBlocks(string onDisk, string edited)
    {
        var a = Parse(onDisk);
        var b = Parse(edited);
        return a.Where(kv => b.TryGetValue(kv.Key, out var v) && !string.Equals(v, kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
    }
}

public enum LinkKind { Anchor, Markdown, Asset, External }

public static class GuideLinks
{
    public const string Host = "guide.local";

    /// <summary>
    /// What a link in a rendered page points at. <paramref name="href"/> is a relative reference ("../x.md#a", "assets/a.svg", "#top")
    /// or an absolute URL; absolute URLs on the guide host are internal, any other http(s)/mailto/etc. URL is External.
    /// </summary>
    public static LinkKind Classify(string href)
    {
        href = (href ?? "").Trim();
        if (href.StartsWith('#')) return LinkKind.Anchor;
        var noFrag = href;
        var h = noFrag.IndexOf('#'); if (h >= 0) noFrag = noFrag[..h];
        var q = noFrag.IndexOf('?'); if (q >= 0) noFrag = noFrag[..q];
        if (Uri.TryCreate(noFrag, UriKind.Absolute, out var abs) && abs.Scheme.Length > 1)
        {
            if ((abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp) && abs.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
                noFrag = abs.AbsolutePath;
            else return LinkKind.External;
        }
        return noFrag.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || noFrag.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ? LinkKind.Markdown : LinkKind.Asset;
    }

    /// <summary>The file a guide-host URL names, or null if it is not on the host or would leave <paramref name="root"/>.</summary>
    public static string? ToFilePath(Uri uri, string root)
    {
        if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>https://guide.local/guide/howto/x.md for a file under <paramref name="root"/> (optionally with a fragment).</summary>
    public static string ToUrl(string file, string root, string? fragment = null)
    {
        var rel = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
        var url = "https://" + Host + "/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        return string.IsNullOrEmpty(fragment) ? url : url + "#" + fragment.TrimStart('#');
    }
}

/// <summary>Reading and saving a page without disturbing its line endings or adding a BOM.</summary>
public static class PageFile
{
    /// <summary>"\r\n" or "\n": whichever the text uses more; <paramref name="fallback"/> for text with no line break.</summary>
    public static string DetectEol(string text, string fallback = "\r\n")
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
        if (crlf == 0 && lf == 0) return fallback;
        return crlf >= lf ? "\r\n" : "\n";
    }

    public static string WithEol(string text, string eol)
    {
        var n = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return eol == "\n" ? n : n.Replace("\n", eol);
    }

    public static string Normalize(string text) => WithEol(text, "\n");

    /// <summary>Writes UTF-8 without BOM, with the line endings the file on disk already has (the editor's own endings are replaced).</summary>
    public static void Save(string path, string editedText)
    {
        var eol = File.Exists(path) ? DetectEol(File.ReadAllText(path), Environment.NewLine) : Environment.NewLine;
        File.WriteAllText(path, WithEol(editedText, eol), new UTF8Encoding(false));
    }
}
