using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

/// <summary>
/// Real pictures of ribbon tabs, taken in AutoCAD by RETRACECAPTURERIBBON (<c>ribbon__&lt;tab&gt;.png</c> + <c>.json</c> in a captures
/// folder): the docs use them instead of the drawn wireframes. A captured tab's SVG is the picture itself with an invisible box over every
/// panel and item it found (the wireframe's ids and <c>data-source</c>, so the source map still points into it); a button section of the
/// guide shows its panel's picture with the button outlined. Tabs, panels and items are matched to the scanned ones by id (<see cref="LiveIds"/>:
/// the live ribbon prefixes CUIX ids with the customization group). By title only for a plugin's tab (an id without a group) whose title
/// no other capture has, so a tab that was not captured never borrows AutoCAD's tab of the same name.
/// </summary>
public sealed class RibbonCaptures
{
    public readonly record struct Rect(int X, int Y, int W, int H);
    public sealed record Item(string? Id, string? Text, string? Name, Rect R);
    /// <param name="PictureAt">where the panel picture starts in the tab picture (the panel clipped to the ribbon), null = at <paramref name="R"/></param>
    public sealed record Panel(string? Id, string? Title, Rect R, string? Picture, Rect? PictureAt, List<Item> Items);
    public sealed record Tab(string Json, string? TabId, string? TabTitle, int Width, int Height, string Png, List<Panel> Panels);

    readonly List<Tab> _tabs = new();
    readonly Dictionary<string, string> _uris = new(StringComparer.OrdinalIgnoreCase);   // picture path -> data URI, read once per build
    public IReadOnlyList<Tab> Tabs => _tabs;

    public static RibbonCaptures Load(IEnumerable<string> dirs, ICollection<string> warnings)
    {
        var c = new RibbonCaptures();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "ribbon__*.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"ribbon captures in {dir}: {ex.Message}"); continue; }
            foreach (var jf in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jf));
                    var r = doc.RootElement;
                    if (Str(r, "kind") != "ribbon-tab") continue;
                    var png = Path.ChangeExtension(jf, ".png");
                    if (!File.Exists(png)) { warnings.Add($"ribbon capture {Path.GetFileName(jf)}: its picture is missing"); continue; }
                    var panels = new List<Panel>();
                    if (r.TryGetProperty("panels", out var ps) && ps.ValueKind == JsonValueKind.Array)
                        foreach (var p in ps.EnumerateArray())
                        {
                            var items = new List<Item>();
                            if (p.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                                foreach (var i in its.EnumerateArray()) if (RectOf(i, "rect") is { } ir) items.Add(new Item(Str(i, "id"), Str(i, "text"), Str(i, "name"), ir));
                            var pic = Str(p, "picture") is { } f && File.Exists(Path.Combine(dir, f)) ? Path.Combine(dir, f) : null;
                            if (RectOf(p, "rect") is { } pr) panels.Add(new Panel(Str(p, "id"), Str(p, "title"), pr, pic, RectOf(p, "pictureRect"), items));
                        }
                    c._tabs.Add(new Tab(jf, Str(r, "tabId"), Str(r, "tabTitle"), Int(r, "width"), Int(r, "height"), png, panels));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    warnings.Add($"ribbon capture {Path.GetFileName(jf)}: cannot read it: {ex.Message}");
                }
            }
        }
        return c;
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    static Rect? RectOf(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4) return null;
        var a = r.EnumerateArray().Select(x => x.TryGetInt32(out var i) ? i : 0).ToArray();
        return a[2] > 0 && a[3] > 0 ? new Rect(a[0], a[1], a[2], a[3]) : null;
    }

    public Tab? For(RibbonTab tab)
    {
        if (_tabs.FirstOrDefault(t => LiveIds.Matches(t.TabId, tab.Id)) is { } byId) return byId;
        // a plugin's own tab only (no group in its id), and only when one capture has that title
        var byTitle = _tabs.Where(t => LiveIds.Same(t.TabTitle, tab.Title)).ToList();
        return byTitle.Count == 1 && byTitle[0].TabId is { } id && !id.Contains('.') ? byTitle[0] : null;
    }

    public static Panel? PanelOf(Tab t, RibbonPanel p) =>
        t.Panels.FirstOrDefault(c => LiveIds.Matches(c.Id, p.Id)) ?? t.Panels.FirstOrDefault(c => LiveIds.Same(c.Title, p.Title));

    /// <summary>The captured item: by id anywhere on the tab, else by its text inside its panel, skipping captured items in <paramref name="taken"/>.</summary>
    static Item? ItemOf(Tab t, RibbonPanel p, RibbonItem i, ICollection<Item>? taken = null)
    {
        foreach (var cp in t.Panels) if (cp.Items.FirstOrDefault(c => LiveIds.Matches(c.Id, i.Id)) is { } byId) return byId;
        var label = ModelIndex.Label(i);
        return PanelOf(t, p)?.Items.FirstOrDefault(c => (taken == null || !taken.Contains(c)) && (LiveIds.Same(c.Text, label) || LiveIds.Same(c.Name, label)));
    }

    static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    string DataUri(string png)
    {
        if (!_uris.TryGetValue(png, out var uri)) _uris[png] = uri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png));
        return uri;
    }

    /// <summary>Width and height from a PNG's header, or null.</summary>
    static (int W, int H)? PngSize(string png)
    {
        var head = new byte[24];
        using var fs = File.OpenRead(png);
        if (fs.Read(head, 0, 24) < 24 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return null;
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20)));
    }

    /// <summary>The tab's SVG: the captured picture, with an invisible box (wireframe ids, <c>data-source</c>) over every panel and item found.
    /// Ids repeat as in the wireframe ("-2", "-3"); a captured item covers one scanned item.</summary>
    public string TabSvg(RibbonTab tab, Tab cap)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string Id(string raw) { var id = raw; for (int n = 2; !ids.Add(id); n++) id = $"{raw}-{n}"; return Esc(id); }
        var taken = new HashSet<Item>();
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{cap.Width}\" height=\"{cap.Height}\" viewBox=\"0 0 {cap.Width} {cap.Height}\">\n");
        sb.Append($"  <title>{Esc(tab.Title)} (captured in AutoCAD)</title>\n");
        sb.Append($"  <image x=\"0\" y=\"0\" width=\"{cap.Width}\" height=\"{cap.Height}\" href=\"{DataUri(cap.Png)}\"/>\n");
        sb.Append($"  <g id=\"{Id(SvgIds.Tab(tab))}\"{Src(tab.Source)}>\n");
        foreach (var p in tab.Panels)
        {
            if (PanelOf(cap, p) is not { } cp) continue;
            sb.Append($"    <g id=\"{Id(SvgIds.Panel(p))}\"{Src(p.Source)}>{Box(cp.R)}\n");
            foreach (var item in p.Rows.SelectMany(r => r.Items).SelectMany(Deep))
            {
                if (ItemOf(cap, p, item, taken) is not { } ci || !taken.Add(ci)) continue;
                sb.Append($"      <g id=\"{Id(SvgIds.Item(item))}\"{Src(item.Source)}>{Box(ci.R)}</g>\n");
            }
            sb.Append("    </g>\n");
        }
        sb.Append("  </g>\n</svg>\n");
        return sb.ToString();
    }

    /// <summary>A panel's picture with the item outlined, or null when the capture lacks the panel picture or the item, or the item lies
    /// outside the picture (a collapsed panel, a drop-down, a panel cut off at the ribbon's edge).</summary>
    public string? ButtonSvg(Tab cap, RibbonPanel p, RibbonItem item)
    {
        if (PanelOf(cap, p) is not { Picture: { } pic } cp || ItemOf(cap, p, item) is not { } ci) return null;
        if (PngSize(pic) is not { } size) return null;
        var at = cp.PictureAt ?? cp.R;   // older captures: the panel picture starts at the panel, clipped to the tab picture
        int ox = cp.PictureAt == null ? Math.Max(0, at.X) : at.X, oy = cp.PictureAt == null ? Math.Max(0, at.Y) : at.Y;
        int x = ci.R.X - ox, y = ci.R.Y - oy;
        if (x < 0 || y < 0 || x + ci.R.W > size.W + 1 || y + ci.R.H > size.H + 1) return null;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size.W}\" height=\"{size.H}\" viewBox=\"0 0 {size.W} {size.H}\">\n");
        sb.Append($"  <title>{Esc(ModelIndex.Label(item))} in {Esc(p.Title)} (captured in AutoCAD)</title>\n");
        sb.Append($"  <image x=\"0\" y=\"0\" width=\"{size.W}\" height=\"{size.H}\" href=\"{DataUri(pic)}\"/>\n");
        sb.Append($"  <rect x=\"{F(x - 1.5)}\" y=\"{F(y - 1.5)}\" width=\"{F(ci.R.W + 3)}\" height=\"{F(ci.R.H + 3)}\" rx=\"3\" fill=\"none\" stroke=\"#ff3b30\" stroke-width=\"2\"/>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    static IEnumerable<RibbonItem> Deep(RibbonItem i) => new[] { i }.Concat(i.Children.SelectMany(Deep));
    static string Src(SourceRef? s) => s == null ? "" : $" data-source=\"{Esc(s.ToString())}\"";
    static string Box(Rect r) => $"<rect x=\"{r.X}\" y=\"{r.Y}\" width=\"{r.W}\" height=\"{r.H}\" fill=\"none\" stroke=\"none\"/>";
}
