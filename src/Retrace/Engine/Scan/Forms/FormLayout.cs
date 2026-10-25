using Retrace.Engine.Util;

namespace Retrace.Engine.Scan.Forms;

internal enum Cat
{
    Button, Edit, Label, Combo, Check, Radio, Group, List, Tree, Grid, Picture, Frame, Slider, Progress, Tab, Menu,
    Panel, Spacer, Unknown,
}

/// <summary>A positioned control. X/Y are relative to the parent node's origin (parent padding already included).</summary>
internal sealed class Node
{
    public UiControl C = null!;
    public Cat Cat;
    public string Text = "";
    public double X, Y, W, H;
    public bool Default;
    public bool Center, Right;
    public List<Node> Kids = new();
    /// <summary>Tab headers for Cat.Tab.</summary>
    public List<string> Tabs = new();
    /// <summary>For Cat.Group / Panel: caption height drawn at the top.</summary>
    public bool Framed;
}

/// <summary>Turns a UiForm into positioned nodes: absolute layouts (px / dlu) and flow layouts (XAML, DCL).</summary>
internal static class FormLayout
{
    public const double CharW = 7.5, LineH = 20;

    [ThreadStatic] private static TextFont? _font;
    /// <summary>Font used to measure text while laying out the form being rendered (set by FormRenderer).</summary>
    public static TextFont Font { get => _font ??= FontMetrics.SegoeUi9; set => _font = value; }

    /// <summary>Win32 MapDialogRect: each edge is MulDiv(v, base, 4 or 8), rounded to the nearest pixel.</summary>
    private static double Edge(double v, double scale, bool px) => px ? v * scale : Math.Round(v * scale, MidpointRounding.AwayFromZero);

    /// <summary>Type used for drawing: a DCL prototype resolves to the built-in tile it derives from.</summary>
    public static string EffectiveType(UiControl c)
    {
        var t = c.Extra.TryGetValue("base", out var b) ? b : c.Type;
        // stock DCL prototypes defined in base.dcl (not in the input): edit12_box, edit16_box ...
        return Rx.IsMatch(t, @"^(edit\d+_box|fcf_ebox\d*)$") ? "edit_box" : t;
    }

    private static readonly string[] SuffixTypes = { "DataGrid", "ListView", "ListBox", "ComboBox", "TextBox", "CheckBox", "RadioButton", "Button", "Label", "TreeView", "TabControl", "Slider", "ProgressBar", "GroupBox", "Image" };

    public static Cat Categorize(string type)
    {
        var c = CategorizeExact(type);
        if (c != Cat.Unknown) return c;
        foreach (var suf in SuffixTypes)
            if (type.Length > suf.Length && type.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return CategorizeExact(suf);
        return Cat.Unknown;
    }

    private static Cat CategorizeExact(string type)
    {
        switch (type.ToLowerInvariant())
        {
            case "button": case "pushbutton": case "defpushbutton": case "toolstripbutton": case "toolstripsplitbutton":
            case "repeatbutton": case "togglebutton": case "image_button": case "retirement_button": case "splitbutton":
            case "bitmapbutton": case "ok_only": case "linkbutton": case "ok_button": case "cancel_button": case "help_button": case "fcf_ibut": case "fcf_ibut1": case "info_button":
                return Cat.Button;
            case "textbox": case "edittext": case "edit_box": case "richtextbox": case "maskedtextbox": case "numericupdown":
            case "passwordbox": case "datetimepicker": case "spin": case "domainupdown": case "datepicker": case "toolstriptextbox":
            case "bedit": case "hedit": case "iedit": case "monthcalendar":
                return Cat.Edit;
            case "label": case "ltext": case "rtext": case "ctext": case "text": case "textblock": case "text_part":
            case "linklabel": case "paragraph": case "toolstriplabel": case "errtile":
                return Cat.Label;
            case "combobox": case "popup_list": case "toolstripcombobox": case "comboboxex32":
                return Cat.Combo;
            case "checkbox": case "autocheckbox": case "auto3state": case "state3": case "toggle":
                return Cat.Check;
            case "radiobutton": case "autoradiobutton": case "radio_button":
                return Cat.Radio;
            case "groupbox": case "boxed_column": case "boxed_row": case "boxed_radio_column": case "boxed_radio_row":
            case "expander":
                return Cat.Group;
            case "listbox": case "list_box": case "listview": case "checkedlistbox": case "itemscontrol":
                return Cat.List;
            case "treeview": return Cat.Tree;
            case "datagrid": case "datagridview": return Cat.Grid;
            case "picturebox": case "icon": case "icon_image": case "image": case "picture": case "image_button_": case "bitmap":
                return Cat.Picture;
            case "frame": return Cat.Frame;
            case "slider": case "trackbar": case "scrollbar": case "hscrollbar": case "vscrollbar": case "scrollbar_":
                return Cat.Slider;
            case "progressbar": return Cat.Progress;
            case "tabcontrol": return Cat.Tab;
            case "menustrip": case "toolstrip": case "statusstrip": case "menu": case "toolbar": case "statusbar":
            case "contextmenustrip": case "contextmenu":
                return Cat.Menu;
            case "panel": case "flowlayoutpanel": case "tablelayoutpanel": case "splitcontainer": case "grid":
            case "stackpanel": case "wrappanel": case "dockpanel": case "canvas": case "border": case "scrollviewer":
            case "usercontrol": case "row": case "column": case "concatenation": case "radio_row": case "radio_column":
            case "uniformgrid": case "viewbox": case "tabpage": case "tabitem": case "splitcontainer_panel":
            case "ok_cancel": case "ok_cancel_help": case "ok_cancel_help_info": case "ok_cancel_help_errtile": case "ok_cancel_err":
            case "contentcontrol": case "flowdocument": case "groupitem": case "toolstripcontainer": case "form": case "window":
                return Cat.Panel;
            case "spacer": case "spacer_0": case "spacer_1": return Cat.Spacer;
            default: return Cat.Unknown;
        }
    }

    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '&' && i + 1 < s.Length) { if (s[i + 1] == '&') { sb.Append('&'); i++; } continue; } // mnemonic marker
            if (c == '\t' || c == '\n' || c == '\r') sb.Append(' ');
            else if (c < 0x20 || c == '￾' || c == '￿') sb.Append(' ');
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
            else if (char.IsSurrogate(c)) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Display(UiControl c)
    {
        var t = Clean(c.Text);
        if (t.Length == 0 && c.Children.Count > 0 && Categorize(EffectiveType(c)) is Cat.Button or Cat.Check or Cat.Radio or Cat.Label or Cat.Group)
        {
            // <CheckBox><TextBlock Text="..."/></CheckBox>: the label lives in a child
            var child = c.Children.FirstOrDefault(k => !string.IsNullOrEmpty(k.Text));
            if (child != null && Categorize(EffectiveType(child)) == Cat.Label) t = Clean(child.Text);
        }
        var m = Rx.Match(t, @"^\{\s*Binding\s+(?:Path\s*=\s*)?([\w.]+)");
        if (m.Success) t = "{" + m.Groups[1].Value + "}";
        else if (t.StartsWith("{") && t.EndsWith("}")) t = "{…}";
        return t;
    }

    // ============================================================ absolute (px / dlu)

    public static Node BuildAbsolute(UiControl c, double sx, double sy, double parentW, double parentH, bool px, ref DockState dock)
    {
        var cat = Categorize(EffectiveType(c));
        var n = new Node { C = c, Cat = cat, Text = Display(c), Default = c.Type.Equals("DEFPUSHBUTTON", StringComparison.OrdinalIgnoreCase) };
        n.Center = c.Type.Equals("CTEXT", StringComparison.OrdinalIgnoreCase);
        n.Right = c.Type.Equals("RTEXT", StringComparison.OrdinalIgnoreCase);
        var (dw, dh) = Natural(n, c);
        if (c.X != null && c.Y != null)
        {
            n.X = Edge(c.X.Value, sx, px); n.Y = Edge(c.Y.Value, sy, px);
            n.W = c.W != null ? Edge(c.X.Value + c.W.Value, sx, px) - n.X : dw;
            n.H = c.H != null ? Edge(c.Y.Value + c.H.Value, sy, px) - n.Y : dh;
        }
        else if (c.Extra.TryGetValue("dock", out var d) && d is "Top" or "Bottom" or "Left" or "Right" or "Fill")
        {
            dock.Place(n, d, parentW, parentH, c.W != null ? Edge(c.W.Value, sx, px) : dw, c.H != null ? Edge(c.H.Value, sy, px) : dh);
        }
        else
        {
            n.X = 0; n.Y = 0;
            n.W = c.W != null ? Edge(c.W.Value, sx, px) : dw; n.H = c.H != null ? Edge(c.H.Value, sy, px) : dh;
        }
        if (cat == Cat.Menu)
        {
            foreach (var k in c.Children) n.Tabs.Add(Display(k));
            if (c.X == null) n.H = Math.Max(n.H, 24);
            return n;
        }
        if (cat == Cat.Tab)
        {
            foreach (var k in c.Children) n.Tabs.Add(Display(k));
            var page = c.Children.FirstOrDefault();
            if (page != null)
            {
                var d2 = new DockState();
                // page children are relative to the page, which is drawn at the page's own Location
                var pn = BuildAbsolute(page, sx, sy, n.W, n.H, px, ref d2);
                if (page.X == null) { pn.X = 4 * sx; pn.Y = 24 * sy; pn.W = Math.Max(0, n.W - 8 * sx); pn.H = Math.Max(0, n.H - 28 * sy); }
                n.Kids.Add(pn);
            }
            return n;
        }
        double ox = 0, oy = 0;
        // WinForms child locations are relative to the parent's client origin (caption area included): no offset.
        var dk = new DockState { W = Math.Max(0, n.W - 2 * ox), H = Math.Max(0, n.H - oy - ox) };
        foreach (var k in OrderForDock(c.Children))
        {
            var kn = BuildAbsolute(k, sx, sy, dk.W, dk.H, px, ref dk);
            kn.X += ox; kn.Y += oy;
            n.Kids.Add(kn);
        }
        n.Framed = cat is Cat.Group || (cat == Cat.Panel && n.Kids.Count > 0);
        return n;
    }

    private static IEnumerable<UiControl> OrderForDock(List<UiControl> kids) =>
        kids.Where(k => !(k.Extra.TryGetValue("dock", out var d) && d == "Fill")).Concat(kids.Where(k => k.Extra.TryGetValue("dock", out var d) && d == "Fill"));

    /// <summary>Remaining rectangle for docked WinForms controls.</summary>
    public struct DockState
    {
        public double W, H;
        private double _l, _t, _r, _b;
        public void Place(Node n, string dock, double pw, double ph, double w, double h)
        {
            if (W == 0 && H == 0) { W = pw; H = ph; }
            double aw = W - _l - _r, ah = H - _t - _b;
            switch (dock)
            {
                case "Top": n.X = _l; n.Y = _t; n.W = aw; n.H = Math.Min(h, 40); _t += n.H; break;
                case "Bottom": n.H = Math.Min(h, 40); n.X = _l; n.Y = H - _b - n.H; n.W = aw; _b += n.H; break;
                case "Left": n.X = _l; n.Y = _t; n.W = Math.Min(w, 150); n.H = ah; _l += n.W; break;
                case "Right": n.W = Math.Min(w, 150); n.X = W - _r - n.W; n.Y = _t; n.H = ah; _r += n.W; break;
                default: n.X = _l; n.Y = _t; n.W = aw; n.H = ah; break;
            }
        }
    }

    /// <summary>Default size in px for a control with no explicit size.</summary>
    public static (double W, double H) Natural(Node n, UiControl c)
    {
        double tw = Svg.TextWidth(n.Text, Font);
        switch (n.Cat)
        {
            case Cat.Button: return (Math.Max(75, tw + 24), 26);
            case Cat.Edit: return (Math.Min(Math.Max(120, tw + 12), 220),c.Extra.ContainsKey("multiline") ? 60 : 24);
            case Cat.Label: return (Math.Max(20, tw + 4), 18);
            case Cat.Combo: return (Math.Max(120, tw + 30), 24);
            case Cat.Check: case Cat.Radio: return (tw + 24, 20);
            case Cat.Group: return (200, 100);
            case Cat.List: case Cat.Tree: case Cat.Grid: return (200, 120);
            case Cat.Picture: return (64, 64);
            case Cat.Frame: return (80, 60);
            case Cat.Slider: return (150, 22);
            case Cat.Progress: return (150, 18);
            case Cat.Tab: return (260, 160);
            case Cat.Menu: return (200, 24);
            case Cat.Panel: return (100, 60);
            case Cat.Spacer: return (10, 10);
            default: return (Math.Max(100, Svg.TextWidth(c.Type, Font) + 16), 28);
        }
    }

    // ============================================================ flow (XAML / DCL)

    private const double Pad = 6, Gap = 6;

    public static Node BuildFlow(UiControl c)
    {
        var cat = Categorize(EffectiveType(c));
        var n = new Node { C = c, Cat = cat, Text = Display(c) };
        string t = EffectiveType(c).ToLowerInvariant();

        double? ew = null, eh = null; // explicit sizes (XAML pixels, DCL characters)
        if (c.W != null) ew = c.W; if (c.H != null) eh = c.H;
        if (c.Extra.TryGetValue("width", out var cw) && double.TryParse(cw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cwv)) ew = cwv * CharW;
        if (c.Extra.TryGetValue("height", out var chh) && double.TryParse(chh, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var chv)) eh = chv * LineH;

        // ---- composite dialog buttons
        if (t is "ok_cancel" or "ok_cancel_help" or "ok_cancel_help_info" or "ok_cancel_help_errtile" or "ok_cancel_err" or "ok_only")
        {
            var labels = t == "ok_only" ? new[] { "OK" } : t.Contains("help") ? new[] { "OK", "Cancel", "Help" } : new[] { "OK", "Cancel" };
            var kids = labels.Select(l => new UiControl { Type = "button", Text = l, Source = c.Source }).ToList();
            var row = Row(kids.Select(BuildFlow).ToList(), 0, Gap);
            row.C = c; row.Cat = Cat.Panel;
            if (t.Contains("errtile")) { }
            return row;
        }

        // ---- containers
        if (cat is Cat.Group or Cat.Panel or Cat.Tab)
        {
            if (cat == Cat.Tab)
            {
                foreach (var k in c.Children) n.Tabs.Add(Display(k));
                var pageKids = c.Children.Take(1).Select(BuildFlow).ToList();
                var inner = pageKids.Count > 0 ? pageKids[0] : new Node { C = c, Cat = Cat.Spacer };
                n.Kids.Add(inner);
                inner.X = Pad; inner.Y = 28;
                n.W = Math.Max(inner.W + 2 * Pad, 160); n.H = inner.H + 28 + Pad;
                return n;
            }
            var children = c.Children.Select(BuildFlow).Where(k => k.Cat != Cat.Spacer || k.W > 0).ToList();
            bool horizontal = t is "row" or "boxed_row" or "radio_row" or "boxed_radio_row" or "concatenation" or "toolbar" ||
                              (c.Extra.TryGetValue("orientation", out var o) && o.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)) ||
                              t == "wrappanel";
            bool hasGrid = t == "grid" && c.Children.Any(k => k.Extra.ContainsKey("grid"));
            bool boxed = cat == Cat.Group;
            double padL = boxed ? 10 : (cat == Cat.Panel && t is "row" or "column" ? 0 : Pad), padT = boxed ? 20 : padL;
            Node inner2;
            if (hasGrid) inner2 = GridLayout(c, children);
            else if (horizontal) inner2 = Row(children, 0, Gap);
            else inner2 = Column(children, 0, Gap);
            // single-child pass-through (Border, ScrollViewer ...) needs no extra padding
            foreach (var k in inner2.Kids) { k.X += padL; k.Y += padT; }
            n.Kids.AddRange(inner2.Kids);
            n.W = Math.Max(inner2.W + 2 * padL, boxed ? Svg.TextWidth(n.Text, Font) + 30 : 0);
            n.H = inner2.H + padT + (boxed ? 8 : padL);
            if (ew != null && cat == Cat.Group) n.W = Math.Max(n.W, ew.Value);
            if (eh != null && cat == Cat.Group) n.H = Math.Max(n.H, eh.Value);
            n.Framed = boxed;
            if (t is "window" or "form") n.Framed = false;
            return n;
        }

        // ---- leaves
        var (dw, dh) = Natural(n, c);
        if (t is "edit_box" or "popup_list")
        {
            double fieldW = ew ?? 110;
            double labW = Svg.TextWidth(n.Text, Font) + 8;
            n.W = labW + fieldW; n.H = 24;
            n.Right = false; n.Center = false;
        }
        else if (t is "button" or "image_button" or "retirement_button")
        {
            n.W = ew ?? dw; n.H = eh ?? dh;
            if (c.Extra.TryGetValue("is_default", out var dflt) && dflt == "true") n.Default = true;
        }
        else if (t is "list_box" && ew == null) { n.W = 240; n.H = eh ?? 120; }
        else { n.W = ew ?? dw; n.H = eh ?? dh; }
        if (t is "toggle" or "radio_button") n.W = Math.Max(n.W, tw(n) + 24);
        if (t is "text" or "text_part" && ew != null) n.W = ew.Value;
        if (t == "spacer" || t == "spacer_0" || t == "spacer_1") { n.Cat = Cat.Spacer; n.W = ew ?? 8; n.H = eh ?? 8; }
        return n;

        static double tw(Node nn) => Svg.TextWidth(nn.Text, Font);
    }

    private static Node Column(List<Node> kids, double pad, double gap)
    {
        var n = new Node { Cat = Cat.Panel };
        double y = pad, w = 0;
        foreach (var k in kids)
        {
            k.X = pad; k.Y = y; y += k.H + gap; w = Math.Max(w, k.W);
            n.Kids.Add(k);
        }
        n.W = w + 2 * pad; n.H = kids.Count == 0 ? 2 * pad : y - gap + pad;
        return n;
    }

    private static Node Row(List<Node> kids, double pad, double gap)
    {
        var n = new Node { Cat = Cat.Panel };
        double x = pad, h = 0;
        foreach (var k in kids) { k.X = x; k.Y = pad; x += k.W + gap; h = Math.Max(h, k.H); n.Kids.Add(k); }
        n.W = kids.Count == 0 ? 2 * pad : x - gap + pad; n.H = h + 2 * pad;
        return n;
    }

    private static Node GridLayout(UiControl c, List<Node> kids)
    {
        var cells = new List<(Node N, int R, int C, int RS, int CS)>();
        int idx = 0; int maxR = 0, maxC = 0;
        foreach (var k in kids)
        {
            int r = 0, col = 0;
            if (k.C.Extra.TryGetValue("grid", out var g))
            {
                var p = g.Split(',');
                int.TryParse(p[0], out r); if (p.Length > 1) int.TryParse(p[1], out col);
            }
            else { r = maxR + (idx > 0 ? 1 : 0); }
            int rs = 1, cs = 1;
            if (k.C.Extra.TryGetValue("rowspan", out var rss)) int.TryParse(rss, out rs);
            if (k.C.Extra.TryGetValue("colspan", out var css)) int.TryParse(css, out cs);
            cells.Add((k, r, col, Math.Max(1, rs), Math.Max(1, cs)));
            maxR = Math.Max(maxR, r + rs - 1); maxC = Math.Max(maxC, col + cs - 1); idx++;
        }
        var colW = new double[maxC + 1]; var rowH = new double[maxR + 1];
        foreach (var (nn, r, col, rs, cs) in cells)
        {
            if (cs == 1) colW[col] = Math.Max(colW[col], nn.W);
            if (rs == 1) rowH[r] = Math.Max(rowH[r], nn.H);
        }
        // equal-width columns, like Grid with * columns; spanning cells widen their last column if needed
        double maxCol = colW.Length > 0 ? colW.Max() : 0;
        for (int i = 0; i < colW.Length; i++) colW[i] = Math.Max(colW[i], Math.Min(maxCol, 220));
        foreach (var (nn, r, col, rs, cs) in cells)
        {
            if (cs > 1) { double have = Enumerable.Range(col, Math.Min(cs, colW.Length - col)).Sum(i => colW[i] + Gap) - Gap; if (nn.W > have) colW[Math.Min(col + cs - 1, colW.Length - 1)] += nn.W - have; }
            if (rs > 1) { double have = Enumerable.Range(r, Math.Min(rs, rowH.Length - r)).Sum(i => rowH[i] + Gap) - Gap; if (nn.H > have) rowH[Math.Min(r + rs - 1, rowH.Length - 1)] += nn.H - have; }
        }
        var xs = new double[colW.Length]; var ys = new double[rowH.Length];
        double x = 0; for (int i = 0; i < colW.Length; i++) { xs[i] = x; x += colW[i] + Gap; }
        double y = 0; for (int i = 0; i < rowH.Length; i++) { ys[i] = y; y += rowH[i] + Gap; }
        var n = new Node { Cat = Cat.Panel };
        foreach (var (nn, r, col, _, _) in cells) { nn.X = xs[col]; nn.Y = ys[r]; n.Kids.Add(nn); }
        n.W = Math.Max(0, x - Gap); n.H = Math.Max(0, y - Gap);
        return n;
    }
}
