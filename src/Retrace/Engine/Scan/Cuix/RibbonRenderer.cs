using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Cuix;

/// <summary>Draws a ribbon tab (CUIX or code) as an SVG wireframe.</summary>
public static class RibbonRenderer
{
    private const double Margin = 8;
    private const double TabStripH = 28;
    private const double SmallH = 22;
    private const double LargeH = SmallH * 3;
    private const double TitleH = 18;
    private const double PanelPad = 6;
    private const double RowGap = 4;
    private const double Font = 10.5;
    private const double PanelGap = 6;

    private sealed class Col
    {
        public double W, H;
        public Action<Svg, double, double, double>? Draw; // svg, x, y, rowHeight
    }

    private sealed class Ids
    {
        private readonly HashSet<string> _used = new();
        public string Make(string prefix, string raw)
        {
            string b = prefix + Rx.Replace(raw ?? "", @"[^\w.-]+", "_");
            string id = b;
            for (int i = 2; !_used.Add(id); i++) id = b + "-" + i;
            return id;
        }
    }

    public static string Render(RibbonTab tab)
    {
        var ids = new Ids();
        // measure every panel
        var panels = new List<(RibbonPanel p, List<(List<Col> cols, double w, double h)> rows, double w, double h, bool slide)>();
        foreach (var p in tab.Panels)
        {
            var rows = new List<(List<Col>, double, double)>();
            bool slide = p.Rows.Any(r => r.IsSlideout);
            foreach (var r in p.Rows.Where(r => !r.IsSlideout))
            {
                var cols = RowCols(r.Items, ids);
                if (cols.Count == 0) continue;
                rows.Add((cols, cols.Sum(c => c.W) + Math.Max(0, cols.Count - 1) * 4, cols.Max(c => c.H)));
            }
            string title = TitleOf(p, slide);
            double bodyW = rows.Count == 0 ? 40 : rows.Max(r => r.Item2);
            double w = Math.Max(bodyW, Svg.TextWidth(title, 10) + 8) + PanelPad * 2;
            double bodyH = rows.Count == 0 ? SmallH : rows.Sum(r => r.Item3) + Math.Max(0, rows.Count - 1) * RowGap;
            panels.Add((p, rows, w, bodyH, slide));
        }

        double bodyMax = Math.Max(LargeH, panels.Count == 0 ? 0 : panels.Max(p => p.h));
        double panelH = bodyMax + PanelPad * 2 + TitleH;
        double contentW = panels.Count == 0 ? 200 : panels.Sum(p => p.w) + (panels.Count - 1) * PanelGap;
        string header = HeaderText(tab);
        double headW = Margin * 2 + Svg.TextWidth(tab.Title, 12) + 40 + Svg.TextWidth(header, 9.5);
        double width = Math.Max(Math.Max(contentW + Margin * 2, headW), 260);
        double height = TabStripH + panelH + Margin * 2;

        var svg = new Svg(width, height);
        svg.BeginGroup(ids.Make("tab-", tab.Id), tab.Source);
        // tab strip
        double tabW = Svg.TextWidth(tab.Title, 12) + 28;
        svg.Rect(Margin, 4, tabW, TabStripH - 4, Svg.Panel, Svg.Border, 3);
        svg.Label(Margin + tabW / 2, 4 + 16, tab.Title, 12, Svg.Text, "middle", tabW - 8, bold: true);
        svg.Label(Margin + tabW + 12, 4 + 16, header, 9.5, Svg.Muted, "start", Math.Max(40, width - Margin * 2 - tabW - 12));
        // ribbon body
        svg.Rect(Margin / 2, TabStripH, width - Margin, panelH + Margin, Svg.Panel, Svg.Border, 2);

        double x = Margin;
        double top = TabStripH + Margin / 2 + 2;
        foreach (var (p, rows, w, _, slide) in panels)
        {
            svg.BeginGroup(ids.Make("panel-", p.Id), p.Source);
            svg.Rect(x, top, w, panelH - 2, Svg.Button, Svg.Border, 3);
            svg.Rect(x, top + panelH - 2 - TitleH, w, TitleH, Svg.PanelTitle, null, 3);
            string title = TitleOf(p, slide);
            svg.Label(x + w / 2, top + panelH - 2 - 5.5, title, 10, Svg.Muted, "middle", w - 6);
            if (p.DialogLauncherCommand != null)
                svg.Label(x + w - 4, top + panelH - 2 - 5, "↘", 10, Svg.Text, "end");
            double y = top + PanelPad;
            foreach (var (cols, rw, rh) in rows)
            {
                double cx = x + PanelPad;
                foreach (var c in cols)
                {
                    c.Draw?.Invoke(svg, cx, y, rh);
                    cx += c.W + 4;
                }
                y += rh + RowGap;
            }
            svg.EndGroup();
            x += w + PanelGap;
        }
        svg.EndGroup();
        return svg.ToString();
    }

    private static string TitleOf(RibbonPanel p, bool slide)
    {
        string t = string.IsNullOrWhiteSpace(p.Title) ? p.Id : p.Title;
        return slide ? t + " ▾" : t;
    }

    private static string HeaderText(RibbonTab tab)
    {
        var parts = new List<string> { tab.Origin == RibbonOrigin.Cuix ? "CUIX" : "code" };
        if (!string.IsNullOrEmpty(tab.Container)) parts.Add(tab.Container);
        if (tab.ContextualRule != null) parts.Add("contextual: " + tab.ContextualRule);
        if (tab.Source != null) parts.Add(tab.Source.ToString());
        return string.Join("  |  ", parts);
    }

    // ------------------------------------------------------------------ layout

    private static bool IsLarge(RibbonItem i) => i.Size == RibbonItemSize.Large && i.Kind is not (RibbonItemKind.RowPanel or RibbonItemKind.Separator);

    private static List<Col> RowCols(IReadOnlyList<RibbonItem> items, Ids ids, bool inSub = false)
    {
        var cols = new List<Col>();
        var stack = new List<RibbonItem>();
        void Flush()
        {
            for (int i = 0; i < stack.Count; i += inSub ? 1 : 3)
                cols.Add(StackCol(stack.Skip(i).Take(inSub ? 1 : 3).ToList(), ids));
            stack.Clear();
        }
        foreach (var it in items)
        {
            if (it.Kind == RibbonItemKind.Separator) { Flush(); cols.Add(SepCol(inSub)); }
            else if (it.Kind == RibbonItemKind.RowPanel) { Flush(); cols.Add(RowPanelCol(it, ids)); }
            else if (IsLarge(it)) { Flush(); cols.Add(LargeCol(it, ids)); }
            else stack.Add(it);
        }
        Flush();
        return cols;
    }

    private static Col SepCol(bool sub) => new()
    {
        W = 5, H = sub ? SmallH : LargeH,
        Draw = (s, x, y, h) => s.Line(x + 2.5, y + 2, x + 2.5, y + h - 2, Svg.Border),
    };

    private static Col StackCol(List<RibbonItem> items, Ids ids)
    {
        double w = items.Max(SmallWidth);
        return new Col
        {
            W = w, H = items.Count * SmallH,
            Draw = (s, x, y, h) =>
            {
                for (int i = 0; i < items.Count; i++) DrawSmall(s, items[i], x, y + i * SmallH, w, ids);
            },
        };
    }

    private static Col RowPanelCol(RibbonItem rp, Ids ids)
    {
        var sub = rp.SubRows.Select(r => RowCols(r.Items, ids, true)).Where(c => c.Count > 0).ToList();
        if (sub.Count == 0) return new Col { W = 8, H = SmallH };
        double w = sub.Max(c => c.Sum(k => k.W) + (c.Count - 1) * 4);
        var hs = sub.Select(c => c.Max(k => k.H)).ToList();
        double h = hs.Sum();
        return new Col
        {
            W = w, H = h,
            Draw = (s, x, y, rowH) =>
            {
                s.BeginGroup(ids.Make("rowpanel-", rp.Id), rp.Source);
                double yy = y;
                for (int r = 0; r < sub.Count; r++)
                {
                    double xx = x;
                    foreach (var c in sub[r]) { c.Draw?.Invoke(s, xx, yy, hs[r]); xx += c.W + 4; }
                    yy += hs[r];
                }
                s.EndGroup();
            },
        };
    }

    private static string ItemLabel(RibbonItem i) =>
        !string.IsNullOrWhiteSpace(i.Text) ? i.Text : (i.Command ?? "");

    private static bool IsWide(RibbonItem i) => i.Kind is RibbonItemKind.Control or RibbonItemKind.Gallery or RibbonItemKind.DropDown;
    private static bool HasArrow(RibbonItem i) => i.Kind is RibbonItemKind.SplitButton or RibbonItemKind.DropDown or RibbonItemKind.Gallery;

    private static double SmallWidth(RibbonItem i)
    {
        string t = ItemLabel(i);
        if (IsWide(i)) return Math.Clamp(Svg.TextWidth(t, Font) + 34, 80, 170);
        if (i.Kind == RibbonItemKind.Label) return Svg.TextWidth(t, Font) + 10;
        double w = 22; // icon + gap
        if (i.ShowText && t.Length > 0) w += Svg.TextWidth(t, Font) + 8;
        if (HasArrow(i)) w += 12;
        return Math.Min(Math.Max(w, 24), 190);
    }

    private static void DrawSmall(Svg s, RibbonItem it, double x, double y, double w, Ids ids)
    {
        s.BeginGroup(ids.Make("item-", it.Id), it.Source);
        string t = ItemLabel(it);
        if (IsWide(it))
        {
            s.Rect(x + 1, y + 2, w - 2, SmallH - 5, Svg.Bg, Svg.Border, 2);
            s.Label(x + 6, y + 15, t, Font, Svg.Muted, "start", w - 26);
            s.Label(x + w - 6, y + 15, "▾", 9, Svg.Muted, "end");
        }
        else if (it.Kind == RibbonItemKind.Label)
        {
            s.Label(x + 4, y + 15, t, Font, Svg.Text, "start", w - 6);
        }
        else
        {
            Icon(s, it, x + 2, y + 3, 16);
            double textW = w - 22 - (HasArrow(it) ? 12 : 0) - 4;
            if (it.ShowText && t.Length > 0) s.Label(x + 22, y + 15, t, Font, Svg.Text, "start", textW);
            if (HasArrow(it)) s.Label(x + w - 3, y + 15, "▾", 9, Svg.Muted, "end");
        }
        s.EndGroup();
    }

    private static List<string> WrapLarge(string t)
    {
        t = t.Trim();
        if (t.Length <= 9 || !t.Contains(' ')) return new() { t };
        // split at the space that balances the two lines
        int best = -1; double bestScore = double.MaxValue;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] != ' ') continue;
            double score = Math.Abs(i - (t.Length - i - 1));
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return new() { t[..best], t[(best + 1)..] };
    }

    private static Col LargeCol(RibbonItem it, Ids ids)
    {
        var lines = it.ShowText ? WrapLarge(ItemLabel(it)) : new List<string>();
        if (it.Kind == RibbonItemKind.SplitButton || HasArrow(it))
        {
            if (lines.Count == 0) lines.Add("▾");
            else if (lines.Count == 1) lines.Add("▾");
            else lines[^1] += " ▾";
        }
        double w = Math.Max(40, lines.Count == 0 ? 0 : lines.Max(l => Svg.TextWidth(l, Font)) + 8);
        w = Math.Min(w, 110);
        return new Col
        {
            W = w, H = LargeH,
            Draw = (s, x, y, h) =>
            {
                s.BeginGroup(ids.Make("item-", it.Id), it.Source);
                Icon(s, it, x + w / 2 - 16, y + 3, 32);
                for (int i = 0; i < lines.Count; i++)
                    s.Label(x + w / 2, y + 3 + 32 + 11 + i * 11.5, lines[i], Font, Svg.Text, "middle", w);
                s.EndGroup();
            },
        };
    }

    private static void Icon(Svg s, RibbonItem it, double x, double y, double size)
    {
        s.Rect(x, y, size, size, Svg.Panel, Svg.Border, 3);
        string letters = Letters(ItemLabel(it).Length > 0 ? ItemLabel(it) : it.Id);
        double fs = size >= 32 ? 12 : 8;
        s.Label(x + size / 2, y + size / 2 + fs * 0.35, letters, fs, Svg.Accent, "middle", bold: true);
    }

    private static string Letters(string text)
    {
        var words = Rx.Matches(text, @"[A-Za-z0-9]+").Select(m => m.Value).ToList();
        if (words.Count == 0) return "?";
        if (words.Count >= 2) return (words[0][..1] + words[1][..1]).ToUpperInvariant();
        var w = words[0];
        return (w.Length >= 2 ? w[..2] : w).ToUpperInvariant();
    }
}
