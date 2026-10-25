using System.Text;
using System.Text.RegularExpressions;

namespace Retrace.Engine.Util;

public static class Slug
{
    /// <summary>File-name-safe lower-kebab slug: "Home - 2D" -> "home-2d".</summary>
    public static string Of(string? s)
    {
        var t = Rx.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (t.Length > 60) t = t[..60].TrimEnd('-');
        return t.Length == 0 ? "unnamed" : t;
    }
}

public static class Md
{
    /// <summary>Escape text for a markdown table cell (pipes, newlines).</summary>
    public static string Cell(string? s) =>
        (s ?? "").Replace("\r", "").Replace("\n", " ").Replace("|", "\\|").Trim();

    /// <summary>Inline code span that survives backticks in content.</summary>
    public static string Code(string? s)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ");
        if (s.Length == 0) return "";
        return s.Contains('`') ? $"`` {s} ``" : $"`{s}`";
    }

    public static string Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var h = headers.ToList();
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", h)).Append(" |\n");
        sb.Append('|').Append(string.Concat(h.Select(_ => "---|"))).Append('\n');
        foreach (var r in rows) sb.Append("| ").Append(string.Join(" | ", r.Select(Cell))).Append(" |\n");
        return sb.ToString();
    }

    /// <summary>Relative link from one output file to another (both relative to the output root).</summary>
    public static string Rel(string fromFile, string toFile)
    {
        var fromDir = Path.GetDirectoryName(fromFile.Replace('\\', '/')) ?? "";
        var rel = Path.GetRelativePath(fromDir.Length == 0 ? "." : fromDir, toFile.Replace('\\', '/'));
        return rel.Replace('\\', '/').Replace(" ", "%20");
    }

    /// <summary>Mermaid-safe label.</summary>
    public static string Mermaid(string? s) =>
        "\"" + (s ?? "").Replace("\"", "'").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;") + "\"";
}
