// Minimal SVG builder for wireframe "screenshots". Every renderer uses it so assets look alike.
using System.Globalization;
using System.Net;
using System.Text;

namespace Retrace.Engine.Util;

public sealed class Svg
{
    // AutoCAD dark-theme-ish palette, kept in one place.
    public const string Bg = "#3b4453";
    public const string Panel = "#4a5466";
    public const string PanelTitle = "#2f3744";
    public const string Button = "#56617a";
    public const string Border = "#6d7891";
    public const string Text = "#f0f3f8";
    public const string Muted = "#aab4c5";
    public const string Accent = "#1e90ff";
    public const string FormBg = "#f0f0f0";
    public const string FormText = "#1a1a1a";
    public const string FormBorder = "#7a7a7a";

    private readonly StringBuilder _sb = new();
    public double Width { get; }
    public double Height { get; }

    public Svg(double width, double height, string background = Bg, string fontFamily = "Segoe UI, Arial, sans-serif")
    {
        Width = width;
        Height = height;
        _sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\" font-family=\"{Esc(fontFamily)}\">\n");
        Rect(0, 0, width, height, background, null);
    }

    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    public Svg Rect(double x, double y, double w, double h, string fill, string? stroke, double rx = 0, string? id = null)
    {
        _sb.Append($"  <rect{(id != null ? $" id=\"{Esc(id)}\"" : "")} x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(w, 0))}\" height=\"{F(Math.Max(h, 0))}\" fill=\"{fill}\"");
        if (stroke != null) _sb.Append($" stroke=\"{stroke}\"");
        if (rx > 0) _sb.Append($" rx=\"{F(rx)}\"");
        _sb.Append("/>\n");
        return this;
    }

    /// <summary>Text; anchor is "start", "middle" or "end". Long text is clipped with an ellipsis to maxWidth.</summary>
    public Svg Label(double x, double y, string? text, double size = 11, string fill = Text, string anchor = "start",
        double? maxWidth = null, bool bold = false)
    {
        string t = text ?? "";
        if (maxWidth is { } mw) t = Clip(t, mw, size);
        _sb.Append($"  <text x=\"{F(x)}\" y=\"{F(y)}\" font-size=\"{F(size)}\" fill=\"{fill}\" text-anchor=\"{anchor}\"{(bold ? " font-weight=\"600\"" : "")}>{Esc(t)}</text>\n");
        return this;
    }

    /// <summary>Pixel-exact box: the outer edge is exactly x,y,w,h and the stroke sits inside it.</summary>
    public Svg Box(double x, double y, double w, double h, string fill, string? stroke, double strokeWidth = 1, double outerRadius = 0)
    {
        if (stroke == null) return Rect(x, y, w, h, fill, null);
        double i = strokeWidth / 2;
        _sb.Append($"  <rect x=\"{F(x + i)}\" y=\"{F(y + i)}\" width=\"{F(Math.Max(w - strokeWidth, 0))}\" height=\"{F(Math.Max(h - strokeWidth, 0))}\" fill=\"{fill}\" stroke=\"{stroke}\"");
        if (strokeWidth != 1) _sb.Append($" stroke-width=\"{F(strokeWidth)}\"");
        if (outerRadius > 0) _sb.Append($" rx=\"{F(Math.Max(outerRadius - i, 0))}\"");
        _sb.Append("/>\n");
        return this;
    }

    public Svg Circle(double cx, double cy, double r, string fill, string stroke)
    {
        _sb.Append($"  <circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>\n");
        return this;
    }

    public Svg Polyline(string stroke, params (double X, double Y)[] pts)
    {
        _sb.Append($"  <polyline points=\"{string.Join(" ", pts.Select(p => F(p.X) + "," + F(p.Y)))}\" fill=\"none\" stroke=\"{stroke}\"/>\n");
        return this;
    }

    /// <summary>One line of dialog text at a baseline, in the given font (px size from the font). No clipping or ellipsis here.</summary>
    public Svg DText(double x, double baseline, string? text, TextFont font, string fill = FormText, string anchor = "start", string? family = null, string? clipId = null)
    {
        _sb.Append($"  <text x=\"{F(x)}\" y=\"{F(baseline)}\" font-size=\"{font.Px}\" fill=\"{fill}\"");
        if (anchor != "start") _sb.Append($" text-anchor=\"{anchor}\"");
        if (family != null) _sb.Append($" font-family=\"{Esc(family)}\"");
        if (clipId != null) _sb.Append($" clip-path=\"url(#{Esc(clipId)})\"");
        _sb.Append($" xml:space=\"preserve\">{Esc(text)}</text>\n");
        return this;
    }

    /// <summary>A clipPath made of one rect; reference it from text with clipId. Ids must be unique per file.</summary>
    public Svg ClipRect(string id, double x, double y, double w, double h)
    {
        _sb.Append($"  <clipPath id=\"{Esc(id)}\"><rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(w, 0))}\" height=\"{F(Math.Max(h, 0))}\"/></clipPath>\n");
        return this;
    }

    public Svg Image(double x, double y, double w, double h, string href)
    {
        _sb.Append($"  <image x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" href=\"{href}\"/>\n");
        return this;
    }

    public Svg Line(double x1, double y1, double x2, double y2, string stroke, double width = 1)
    {
        _sb.Append($"  <line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{stroke}\" stroke-width=\"{F(width)}\"/>\n");
        return this;
    }

    /// <summary>Open a group carrying a data-source attribute so a later tool can map pixels back to code.</summary>
    public Svg BeginGroup(string? id, SourceRef? source = null)
    {
        _sb.Append("  <g");
        if (id != null) _sb.Append($" id=\"{Esc(id)}\"");
        if (source != null) _sb.Append($" data-source=\"{Esc(source.ToString())}\"");
        _sb.Append(">\n");
        return this;
    }

    public Svg EndGroup()
    {
        _sb.Append("  </g>\n");
        return this;
    }

    public Svg Raw(string fragment)
    {
        _sb.Append(fragment);
        return this;
    }

    /// <summary>Rough text width estimate (no font metrics available).</summary>
    public static double TextWidth(string text, double size) => text.Length * size * 0.58;

    public static double TextWidth(string? text, TextFont font) => font.Width(text);

    public static string Clip(string text, double maxWidth, double size)
    {
        if (TextWidth(text, size) <= maxWidth) return text;
        int n = Math.Max(1, (int)(maxWidth / (size * 0.58)) - 1);
        return n >= text.Length ? text : text[..n] + "…";
    }

    public override string ToString() => _sb + "</svg>\n";
}
