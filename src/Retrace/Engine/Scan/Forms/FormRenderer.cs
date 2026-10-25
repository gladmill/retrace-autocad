using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

/// <summary>
/// Draws a UiForm as an SVG wireframe that tries to match what Windows 10/11 draws at 96 DPI (100%):
/// one SVG pixel is one screen pixel for Win32 (.rc) and WinForms forms. All metrics (dialog units, caption
/// height, control geometry, colors) are calibrated against real Windows 11 captures; see Util/FontMetrics.cs.
/// </summary>
public static class FormRenderer
{
    // Window chrome at 100%: 1 px border, 30 px caption inside it, client area starts at (1, 31).
    private const double CaptionH = 30;
    private const double ClientPad = 12;
    // Colors sampled from Windows 11 captures at 96 DPI (themed common controls v6).
    private const string Field = "#ffffff", FieldBorder = "#8d8d8d", ButtonFill = "#fdfdfd", ButtonBorder = "#d0d0d0", ButtonEdge = "#bababa";
    private const string ListBorder = "#828790", GroupFrame = "#dcdcdc", Glyph = "#626262", GlyphFill = "#f3f3f3";
    private const string Accent = "#0078d4", AccentEdge = "#006bbe", CaptionBg = "#f3f3f3", WindowBorder = "#8c929d", Chevron = "#6f6f6f";

    /// <summary>Window chrome: pixels from the window edge to the client area, per side.</summary>
    private readonly record struct Chrome(double L, double T, double R, double B, bool Caption);
    private const string Faint = "#e4e4e4", Muted = "#666666";

    private sealed class Ctx
    {
        public required Svg S;
        public required TextFont F;
        public required bool Absolute;
        public required UiForm Form;
        public string? Root;
        public int Counter;
        /// <summary>Control that gets the initial keyboard focus (Win32 dialogs only).</summary>
        public Node? Focus;
        /// <summary>Push button drawn with the accent border: the focused push button, else the DEFPUSHBUTTON.</summary>
        public Node? AccentButton;
    }

    public static string Render(UiForm form, string? inputRoot = null)
    {
        bool absolute = form.Units != "auto";
        var font = FormFont(form);
        var saved = FormLayout.Font;
        FormLayout.Font = font;
        try
        {
            var (nodes, cw, ch) = Layout(form, font);
            var chrome = ChromeFor(form);
            double totalW = cw + chrome.L + chrome.R, totalH = ch + chrome.T + chrome.B;
            // Absolute (px / dlu) forms are drawn at true 100% size so a real screenshot overlays 1:1.
            // Auto layouts (XAML / DCL, sizes are guesses) are scaled up when tiny and capped when huge.
            double k = 1;
            if (!absolute)
            {
                double shortSide = Math.Min(totalW, totalH);
                if (shortSide < 300) k = Math.Min(5, 300 / Math.Max(shortSide, 1));
                double longSide = Math.Max(totalW, totalH) * k;
                if (longSide > 2000) k = k * 2000 / longSide;
            }

            var svg = new Svg(Math.Round(totalW * k), Math.Round(totalH * k), Svg.FormBg, font.Css);
            svg.Raw($"  <g id=\"{Svg.Esc(Emit.SvgIds.Form(form))}\" data-source=\"{Svg.Esc(form.Source.ToString())}\" transform=\"scale({Svg.F(k)})\">\n");
            if (chrome.Caption)
            {
                svg.Rect(0.5, 0.5, totalW - 1, totalH - 1, Svg.FormBg, WindowBorder);
                svg.Rect(1, 1, totalW - 2, CaptionH, CaptionBg, null);
                var cap = FontMetrics.Caption;
                // measured on captures: title glyphs start at x=10, baseline y=20; the close X is 10x10 with its box at (W-21, 10)
                svg.DText(10, 20, cap.Clip(FormLayout.Clean(form.Title), totalW - 10 - 50), cap, Svg.FormText, "start", cap.Css);
                svg.Line(totalW - 20.5, 10.5, totalW - 11.5, 19.5, Svg.FormText).Line(totalW - 20.5, 19.5, totalW - 11.5, 10.5, Svg.FormText);
            }
            else if (chrome.L >= 3)
            {
                // DS_MODALFRAME without caption: 3 px raised frame (measured): e3e3e3 / 696969, then ffffff / a0a0a0, then the face
                Bevel(svg, 0, 0, totalW, totalH, "#e3e3e3", "#696969");
                Bevel(svg, 1, 1, totalW - 2, totalH - 2, "#ffffff", "#a0a0a0");
            }
            else if (chrome.L > 0)
            {
                svg.Rect(0.5, 0.5, totalW - 1, totalH - 1, Svg.FormBg, WindowBorder);
            }

            var cx = new Ctx { S = svg, F = font, Absolute = absolute, Form = form, Root = inputRoot };
            if (form.Kind == UiFormKind.Win32Dialog)
            {
                cx.Focus = nodes.FirstOrDefault(IsTabStop);
                bool IsDef(Node n) => n.Cat == Cat.Button && (n.Default || Has(n, "BS_DEFPUSHBUTTON"));
                cx.AccentButton = cx.Focus is { Cat: Cat.Button } ? cx.Focus : nodes.FirstOrDefault(IsDef);
                // with several DEFPUSHBUTTONs Windows ends up with the last one as default (seen in the modeless sample capture)
                if (cx.AccentButton != null && IsDef(cx.AccentButton)) cx.AccentButton = nodes.Last(IsDef);
            }
            foreach (var n in nodes) Draw(cx, n, chrome.L, chrome.T);
            svg.Raw("  </g>\n");
            return svg.ToString();
        }
        finally { FormLayout.Font = saved; }
    }

    private static Chrome ChromeFor(UiForm form)
    {
        var caption = new Chrome(1, 31, 1, 1, true);
        if (form.Kind != UiFormKind.Win32Dialog || !form.Extra.TryGetValue("style", out var st)) return caption;
        bool H(string f) => st.Contains(f, StringComparison.OrdinalIgnoreCase);
        if (H("WS_CAPTION")) return caption;
        // captionless popup: modal frame / dialog frame = 3 px, WS_BORDER = 1 px, else no frame at all
        if (H("WS_POPUP") && !H("WS_CHILD"))
        {
            double f = H("DS_MODALFRAME") || H("WS_DLGFRAME") ? 3 : H("WS_BORDER") ? 1 : 0;
            return new Chrome(f, f, f, f, false);
        }
        return caption; // WS_CHILD pages (tab dialogs) are shown with a caption
    }

    /// <summary>
    /// Initial focus: the first control in tab order with WS_TABSTOP. In .rc, PUSHBUTTON, DEFPUSHBUTTON, EDITTEXT, COMBOBOX,
    /// LISTBOX and the check box keywords get it by default; LTEXT, GROUPBOX, ICON do not; a CONTROL only if its style says so.
    /// </summary>
    internal static bool IsTabStop(Node n)
    {
        if (Has(n, "WS_DISABLED") || Has(n, "NOT WS_VISIBLE") || Has(n, "NOT WS_TABSTOP")) return false;
        if (n.C.Type.Equals("RADIOBUTTON", StringComparison.OrdinalIgnoreCase) || n.C.Type.Equals("AUTORADIOBUTTON", StringComparison.OrdinalIgnoreCase)) return false; // unchecked radios lose it at runtime
        if (Has(n, "WS_TABSTOP")) return true;
        if (n.C.Extra.ContainsKey("class")) return false;
        return n.C.Type.ToUpperInvariant() is "PUSHBUTTON" or "DEFPUSHBUTTON" or "EDITTEXT" or "COMBOBOX" or "LISTBOX" or "CHECKBOX" or "AUTOCHECKBOX" or "AUTO3STATE" or "STATE3";
    }

    /// <summary>Dialog font: the FONT line, else the WinForms default (Microsoft Sans Serif 8.25), the Win32 default (MS Shell Dlg 8), or Segoe UI 9 for auto layouts.</summary>
    internal static TextFont FormFont(UiForm form)
    {
        if (form.Extra.TryGetValue("font", out var f) && !string.IsNullOrWhiteSpace(f)) return FontMetrics.Parse(f);
        if (form.Units == "auto") return FontMetrics.SegoeUi9;
        return form.Kind == UiFormKind.WinForm ? FontMetrics.WinFormsDefault : FontMetrics.Default;
    }

    // ------------------------------------------------------------ layout

    private static (List<Node> Nodes, double W, double H) Layout(UiForm form, TextFont font)
    {
        var nodes = new List<Node>();
        if (form.Units == "auto")
        {
            if (form.Kind == UiFormKind.PaletteSet)
            {
                foreach (var c in form.Controls)
                {
                    var n = FormLayout.BuildFlow(c);
                    n.Cat = Cat.Unknown; n.Kids.Clear(); n.X = 8; n.Y = 8 + nodes.Count * 0; n.W = 284; n.H = 340;
                    nodes.Add(n);
                    break;
                }
                var tabs = form.Controls.Select(c => FormLayout.Clean(c.Text)).Where(t => t.Length > 0).ToList();
                if (nodes.Count == 0) nodes.Add(new Node { C = new UiControl { Type = "(no views found)" }, Cat = Cat.Unknown, X = 8, Y = 8, W = 284, H = 340 });
                if (tabs.Count > 0) nodes[0].Tabs.AddRange(tabs);
                return (nodes, 300, 360);
            }
            var tops = form.Controls.Select(FormLayout.BuildFlow).ToList();
            double y = ClientPad, w = 0;
            foreach (var n in tops) { n.X = ClientPad; n.Y = y; y += n.H + 6; w = Math.Max(w, n.W); }
            double cw = w + 2 * ClientPad, ch = (tops.Count == 0 ? ClientPad : y - 6) + ClientPad;
            cw = Math.Max(cw, FontMetrics.Caption.Width(FormLayout.Clean(form.Title)) + 60);
            cw = Math.Max(cw, form.Width ?? 0);
            ch = Math.Max(ch, form.Height ?? 0);
            if (tops.Count == 0) { cw = Math.Max(cw, 240); ch = Math.Max(ch, 120); }
            return (tops, cw, ch);
        }

        bool px = form.Units == "px";
        double sx = px ? 1 : font.Sx, sy = px ? 1 : font.Sy;
        Func<double, double, double> edge = (v, s) => px ? v * s : Math.Round(v * s, MidpointRounding.AwayFromZero);
        double pw = edge(form.Width ?? 400, sx), ph = edge(form.Height ?? 300, sy);
        var dock = new FormLayout.DockState { W = pw, H = ph };
        foreach (var c in form.Controls) nodes.Add(FormLayout.BuildAbsolute(c, sx, sy, pw, ph, px, ref dock));
        double fw = form.Width != null ? pw : nodes.Select(n => n.X + n.W).DefaultIfEmpty(200).Max() + 12;
        double fh = form.Height != null ? ph : nodes.Select(n => n.Y + n.H).DefaultIfEmpty(100).Max() + 12;
        return (nodes, Math.Max(fw, 60), Math.Max(fh, 40));
    }

    // ------------------------------------------------------------ drawing

    /// <summary>Baseline of a single text line vertically centered in a box (DT_VCENTER: the font's cell height is centered).</summary>
    private static double MidBase(double y, double h, TextFont f) => y + Math.Floor((h - f.Cy) / 2.0) + f.Ascent;

    private static string Style(Node n) => n.C.Extra.TryGetValue("style", out var s) ? s : "";
    private static bool Has(Node n, string flag) => Style(n).Contains(flag, StringComparison.OrdinalIgnoreCase);

    private static void Draw(Ctx cx, Node n, double ox, double oy)
    {
        var svg = cx.S; var f = cx.F;
        double x = ox + n.X, y = oy + n.Y, w = n.W, h = n.H;
        if (n.Cat == Cat.Spacer && n.Kids.Count == 0) return;
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(w) || double.IsNaN(h)) return;
        int id = ++cx.Counter;
        var name = string.IsNullOrEmpty(n.C.Name) ? n.C.Type : n.C.Name;
        svg.BeginGroup($"c{id}-{FormLayout.Clean(name)}", n.C.Source);
        string type = FormLayout.EffectiveType(n.C).ToLowerInvariant();
        var text = n.Text;
        double mid = MidBase(y, h, f);

        switch (n.Cat)
        {
            case Cat.Button:
            {
                // Win11: 1 px transparent margin, then a 1 px rounded border (radius 4), fill #fdfdfd, slightly darker bottom edge
                bool acc = cx.Form.Kind == UiFormKind.Win32Dialog ? n == cx.AccentButton : n.Default;
                svg.Box(x + 1, y + 1, w - 2, h - 2, ButtonFill, acc ? Accent : ButtonBorder, 1, 4);
                svg.Line(x + 5, y + h - 1.5, x + w - 5, y + h - 1.5, acc ? AccentEdge : ButtonEdge);
                var clip = TextClip(cx, f, text, x, y, w, h, w - 4);
                svg.DText(x + w / 2, mid, text, f, Svg.FormText, "middle", null, clip);
                break;
            }
            case Cat.Edit:
                if (type == "edit_box") { DrawLabeledField(cx, n, x, y, w, h, false); break; }
                DrawEditFrame(cx, n, x, y, w, h);
                bool multi = n.C.Extra.ContainsKey("multiline") || Has(n, "ES_MULTILINE") || h >= 2 * f.Cy;
                var fill = text.StartsWith("{") ? Muted : Svg.FormText;
                if (!multi) svg.DText(x + 3, mid, text, f, fill, "start", null, TextClip(cx, f, text, x + 3, y, w - 6, h, w - 6));
                else
                {
                    var lines = f.Wrap(text, w - 6);
                    for (int i = 0; i < lines.Count && 2 + (i + 1) * f.Cy <= h; i++)
                        svg.DText(x + 3, y + 2 + i * f.Cy + f.Ascent, lines[i], f, fill);
                }
                break;
            case Cat.Label:
                DrawStatic(cx, n, x, y, w, h);
                break;
            case Cat.Combo:
                if (type == "popup_list") { DrawLabeledField(cx, n, x, y, w, h, true); break; }
                // A closed combo is font-height based; the height in the .rc is the dropped-list height.
                double ch = cx.Absolute && !Has(n, "CBS_SIMPLE") ? f.Cy + 8 : h;
                DrawCombo(cx, x, y, w, ch, text, n == cx.Focus);
                break;
            case Cat.Check:
            case Cat.Radio:
            {
                double gy = y + Math.Floor((h - 13) / 2.0);
                if (n.Cat == Cat.Check) svg.Box(x, gy, 13, 13, GlyphFill, Glyph, 1, 2);
                else svg.Circle(x + 6.5, gy + 6.5, 6, GlyphFill, Glyph);
                bool wrap = Has(n, "BS_MULTILINE") || cx.Form.Kind == UiFormKind.WinForm;
                var lines = wrap ? f.Wrap(text, w - 16) : new List<string> { text };
                double top = y + Math.Floor((h - lines.Count * f.Cy) / 2.0);
                if (lines.Count * f.Cy > h) top = y;
                string? clip = null;
                if (lines.Any(l => f.Width(l) > w - 16)) { clip = $"clip-c{id}"; svg.ClipRect(clip, x + 16, y, w - 16, h); }
                for (int i = 0; i < lines.Count && (i == 0 || (i + 1) * f.Cy <= h + 1); i++)
                    svg.DText(x + 16, top + i * f.Cy + f.Ascent, lines[i], f, Svg.FormText, "start", null, clip);
                break;
            }
            case Cat.Group:
                // frame: left x, right x+w-1, top y+cy/2, bottom y+h-2 (measured); caption starts at x+9 with a 2 px patch each side
                svg.Box(x, y + Math.Floor(f.Cy / 2.0), w, h - Math.Floor(f.Cy / 2.0) - 1, "none", GroupFrame);
                if (text.Length > 0)
                {
                    var cap = f.Clip(text, w - 18);
                    svg.Rect(x + 7, y, f.Width(cap) + 4, f.Cy, Svg.FormBg, null);
                    svg.DText(x + 9, y + f.Ascent, cap, f, Svg.FormText);
                }
                break;
            case Cat.List: case Cat.Tree: case Cat.Grid:
                DrawList(cx, n, type, x, y, w, h);
                break;
            case Cat.Picture:
                if (TryDrawImage(cx, n, x, y)) break;
                svg.Rect(x, y, w, h, "#ececec", "#9a9a9a");
                svg.Line(x, y, x + w, y + h, "#9a9a9a");
                svg.Line(x + w, y, x, y + h, "#9a9a9a");
                break;
            case Cat.Frame:
                if (Has(n, "SS_SUNKEN"))
                {
                    // sunken edge (measured): top/left 2 px #a0a0a0 then 2 px #696969; bottom/right #696969, #e3e3e3, 2 px #ffffff
                    Bevel(svg, x, y, w, h, "#a0a0a0", "#ffffff"); Bevel(svg, x + 1, y + 1, w - 2, h - 2, "#a0a0a0", "#ffffff");
                    Bevel(svg, x + 2, y + 2, w - 4, h - 4, "#696969", "#e3e3e3"); Bevel(svg, x + 3, y + 3, w - 6, h - 6, "#696969", "#696969");
                }
                else svg.Box(x, y, w, h, "none", "#a0a0a0");
                break;
            case Cat.Slider:
                svg.Line(x + 4, y + h / 2, x + w - 4, y + h / 2, "#999999", 3);
                svg.Box(x + w * 0.4, y + h / 2 - 8, 8, 16, ButtonFill, ButtonBorder);
                break;
            case Cat.Progress:
                svg.Box(x, y, w, h, Field, FieldBorder);
                svg.Rect(x + 1, y + 1, Math.Max((w - 2) * 0.4, 0), Math.Max(h - 2, 0), "#06b025", null);
                break;
            case Cat.Tab:
                DrawTab(cx, n, x, y, w, h);
                break;
            case Cat.Menu:
                svg.Rect(x, y, w, h, "#f9f9f9", "#d8d8d8");
                double mx = x + 8;
                foreach (var t in n.Tabs)
                {
                    if (mx > x + w - 20) break;
                    svg.DText(mx, mid, f.Clip(t, x + w - mx), f, Svg.FormText);
                    mx += f.Width(t) + 16;
                }
                if (n.Tabs.Count == 0) svg.DText(x + 8, mid, n.C.Type, f, Muted);
                break;
            case Cat.Panel:
                if (n.Framed && cx.Absolute && type is not ("tabpage" or "tabitem"))
                    Dashed(svg, x, y, w, h, "#c8c8c8");
                break;
            default:
                Dashed(svg, x, y, w, h, "#8a8a8a");
                var label = text.Length > 0 ? $"{n.C.Type}: {text}" : n.C.Type;
                svg.DText(x + w / 2, mid, f.Clip(label, Math.Max(w - 6, 20)), f, Muted, "middle");
                if (n.Tabs.Count > 0)
                    svg.DText(x + w / 2, y + h - 8, f.Clip(string.Join("  |  ", n.Tabs), w - 10), f, Muted, "middle");
                break;
        }
        foreach (var kid in n.Kids) Draw(cx, kid, x, y);
        svg.EndGroup();
    }

    /// <summary>
    /// Static text (LTEXT / CTEXT / RTEXT, WinForms Label): top-aligned from the rect's top-left, word-wrapped to the
    /// rect width (not for SS_LEFTNOWORDWRAP / SS_SIMPLE), clipped to the rect: extra lines are dropped, no ellipsis.
    /// A WinForms Label with AutoSize is one unclipped line.
    /// </summary>
    private static void DrawStatic(Ctx cx, Node n, double x, double y, double w, double h)
    {
        var svg = cx.S; var f = cx.F; var text = n.Text;
        double ax = n.Center ? x + w / 2 : n.Right ? x + w : x;
        string anchor = n.Center ? "middle" : n.Right ? "end" : "start";
        if (!cx.Absolute)
        {
            // flow layouts (XAML / DCL) have no real rect for text: keep it on one line, vertically centered
            svg.DText(ax, MidBase(y, h, f), f.Clip(text, Math.Max(w, f.Width(text) + 2)), f, Svg.FormText, anchor);
            return;
        }
        if (n.C.Extra.ContainsKey("autosize"))
        {
            svg.DText(x, y + f.Ascent, text, f, Svg.FormText);
            return;
        }
        bool noWrap = Has(n, "SS_LEFTNOWORDWRAP") || Has(n, "SS_SIMPLE") || n.C.Type.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase);
        var lines = noWrap ? new List<string> { text } : f.Wrap(text, w);
        // Windows draws each line whole and clips at the rect edge (a word wider than the rect is not broken);
        // lines that do not fit in the rect height are not drawn.
        var drawn = new List<(int I, string L)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0 && (i + 1) * f.Cy > h) break;
            if (lines[i].Length > 0) drawn.Add((i, lines[i]));
        }
        string? clip = null;
        if (drawn.Any(d => f.Width(d.L) > w)) { clip = $"clip-c{cx.Counter}"; svg.ClipRect(clip, x, y, w, h); }
        foreach (var (i, line) in drawn)
            svg.DText(ax, y + i * f.Cy + f.Ascent, line, f, Svg.FormText, anchor, null, clip);
    }

    /// <summary>Emit a clipPath for text that is wider than <paramref name="avail"/>; returns its id, else null.</summary>
    private static string? TextClip(Ctx cx, TextFont f, string text, double x, double y, double w, double h, double avail)
    {
        if (f.Width(text) <= avail) return null;
        var id = $"clip-c{cx.Counter}";
        cx.S.ClipRect(id, x, y, w, h);
        return id;
    }

    /// <summary>1 px two-tone frame: top and left in <paramref name="tl"/>, bottom and right in <paramref name="br"/>.</summary>
    private static void Bevel(Svg svg, double x, double y, double w, double h, string tl, string br)
    {
        svg.Line(x, y + 0.5, x + w - 1, y + 0.5, tl).Line(x + 0.5, y, x + 0.5, y + h - 1, tl);
        svg.Line(x + 1, y + h - 0.5, x + w, y + h - 0.5, br).Line(x + w - 0.5, y + 1, x + w - 0.5, y + h, br);
    }

    private static void DrawEditFrame(Ctx cx, Node n, double x, double y, double w, double h)
    {
        // themed edit with client edge: light 1 px frame, darker bottom line
        cx.S.Box(x, y, w, h, Field, "#e8e8e8");
        cx.S.Line(x, y + h - 0.5, x + w, y + h - 0.5, n == cx.Focus ? Accent : "#838383", n == cx.Focus ? 2 : 1);
    }

    private static void DrawCombo(Ctx cx, double x, double y, double w, double h, string text, bool focused = false)
    {
        var svg = cx.S; var f = cx.F;
        // themed combo: 1 px rounded border (#8d8d8d, accent when focused), no arrow button, a chevron at x+w-9
        svg.Box(x, y, w, h, Field, focused ? Accent : FieldBorder, 1, 2);
        double ax = x + w - 9, ay = y + Math.Floor(h / 2.0);
        svg.Polyline(Chevron, (ax - 3.5, ay - 0.5), (ax, ay + 3), (ax + 3.5, ay - 0.5));
        svg.DText(x + 3, MidBase(y, h, f), text, f, Svg.FormText, "start", null, TextClip(cx, f, text, x + 1, y, w - 18, h, w - 22));
    }

    private static void DrawLabeledField(Ctx cx, Node n, double x, double y, double w, double h, bool combo)
    {
        var svg = cx.S; var f = cx.F;
        double lw = f.Width(n.Text) + 8;
        double fx = x + lw, fw = Math.Max(w - lw, 20);
        svg.DText(x, MidBase(y, h, f), f.Clip(n.Text, lw), f, Svg.FormText);
        if (combo) DrawCombo(cx, fx, y, fw, h, "");
        else svg.Box(fx, y, fw, h, Field, FieldBorder);
    }

    private static void DrawList(Ctx cx, Node n, string type, double x, double y, double w, double h)
    {
        var svg = cx.S; var f = cx.F;
        svg.Box(x, y, w, h, Field, ListBorder);
        double top = y + 1;
        bool header = type.Contains("listview") || type.Contains("datagrid");
        if (header)
        {
            double hh = f.Cy + 7;
            svg.Rect(x + 1, top, Math.Max(w - 2, 0), hh, "#f3f3f3", null);
            svg.Line(x + 1, top + hh, x + w - 1, top + hh, "#d0d0d0");
            for (int i = 1; i < 3; i++) svg.Line(x + w * i / 3, top, x + w * i / 3, top + hh, "#d0d0d0");
            top += hh + 1;
        }
        double rowH = f.Cy + 3; // list rows at 8 pt are 16 px
        int row = 0;
        for (double ry = top; ry + rowH <= y + h - 1; ry += rowH, row++)
        {
            double indent = n.Cat == Cat.Tree ? (row % 4 is 1 or 2 ? 14 : 0) : 0;
            double bw = Math.Max((w - 24 - indent) * (0.45 + 0.4 * ((row * 37) % 10) / 10.0), 6);
            svg.Rect(x + 8 + indent, ry + (rowH - 7) / 2, bw, 7, Faint, null, 1);
            if (row > 12) break;
        }
        if (n.Text.Length > 0 && n.Cat == Cat.List && !header && n.C.Type.Equals("ListBox", StringComparison.OrdinalIgnoreCase))
            svg.DText(x + 3, y + 1 + f.Ascent, f.Clip(n.Text, w - 6), f, Muted);
    }

    private static void DrawTab(Ctx cx, Node n, double x, double y, double w, double h)
    {
        var svg = cx.S; var f = cx.F;
        // page: fill #f9f9f9, border #e5e5e5. With no tab items the page starts 2 px below the control top (measured).
        double th = n.Tabs.Count > 0 ? f.Cy + 8 : 2;
        svg.Box(x, y + th, w - 2, Math.Max(h - th - 2, 0), "#f9f9f9", "#e5e5e5");
        double tx = x;
        for (int i = 0; i < n.Tabs.Count; i++)
        {
            double tw = f.Width(n.Tabs[i]) + 22;
            if (tx + tw > x + w) break;
            double top = i == 0 ? y : y + 2;
            svg.Box(tx, top, tw, th - (top - y) + (i == 0 ? 1 : 0), i == 0 ? "#f9f9f9" : "#f0f0f0", "#e5e5e5");
            svg.DText(tx + tw / 2, MidBase(top, th - (top - y), f), f.Clip(n.Tabs[i], tw - 4), f, Svg.FormText, "middle");
            tx += tw;
        }
    }

    // ------------------------------------------------------------ images

    private const int MaxImageBytes = 256 * 1024;

    /// <summary>Embed the ICON / BITMAP file the scanner resolved for this static (inside the input root only).</summary>
    private static bool TryDrawImage(Ctx cx, Node n, double x, double y)
    {
        if (cx.Root == null || !n.C.Extra.TryGetValue("image", out var rel)) return false;
        try
        {
            var root = Path.GetFullPath(cx.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return false;
            var info = new FileInfo(full);
            if (info.Length > MaxImageBytes) return false;
            var bytes = File.ReadAllBytes(full);
            bool bmp = n.C.Extra.TryGetValue("imagekind", out var kind) && kind == "bitmap" ||
                       Path.GetExtension(full).Equals(".bmp", StringComparison.OrdinalIgnoreCase);
            double iw = 32, ih = 32; // icons are 32x32 at 96 DPI
            if (bmp)
            {
                if (bytes.Length < 26 || bytes[0] != 'B' || bytes[1] != 'M') return false;
                int hdr = BitConverter.ToInt32(bytes, 14);
                if (hdr == 12) { iw = BitConverter.ToInt16(bytes, 18); ih = BitConverter.ToInt16(bytes, 20); }
                else { iw = BitConverter.ToInt32(bytes, 18); ih = Math.Abs(BitConverter.ToInt32(bytes, 22)); }
                if (iw <= 0 || ih <= 0 || iw > 4096 || ih > 4096) return false;
            }
            else if (bytes.Length < 6 || bytes[2] != 1 || bytes[3] != 0) return false; // not an ICO
            cx.S.Image(x, y, iw, ih, $"data:image/{(bmp ? "bmp" : "x-icon")};base64,{Convert.ToBase64String(bytes)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void Dashed(Svg svg, double x, double y, double w, double h, string stroke) =>
        svg.Raw($"  <rect x=\"{Svg.F(x)}\" y=\"{Svg.F(y)}\" width=\"{Svg.F(Math.Max(w, 0))}\" height=\"{Svg.F(Math.Max(h, 0))}\" fill=\"none\" stroke=\"{stroke}\" stroke-dasharray=\"4 3\"/>\n");
}
