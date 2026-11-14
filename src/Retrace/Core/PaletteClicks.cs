using System.Globalization;
using System.Text.Json;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The intermediate clicks of a palette: a click on a tree item, the +/- of a node, an image, an item of a list view, or a control that has no name (`palette_click` with `kind`
// "tree", "expand", "collapse", "image", "listview", "point"). What the recorder writes (the names of the fields), how a step names such a click in words, and how a replay finds
// the target among the live controls (the pure part: the AutoCAD side only asks the window for its pictures, rows and rectangles).

/// <summary>What a `palette_click` of the new kinds holds beyond the tree fields of a double-click (`tree`, `treeLabel`, `path`, which tree / expand / collapse reuse) and the button name of an image (`button`, `buttonFrom`,
/// `index`, `legacyIndex`, `noTextCount`, which the step already has).</summary>
public sealed record PaletteHit(
    string Control = "",       // listview: the list view's key (`name:X`, `ord:N`, `id:N`, as a context menu keys it)
    string ControlLabel = "",  // listview: its label, for the wording
    string Item = "",          // listview: the item's first-column text
    int ItemIndex = -1,        // listview: its row when recorded
    int Occurrence = 0,        // listview: which row with that text it is (1 = the first)
    string View = "",          // listview: "icon" | "smallicon" | "list" | "report" | "tile"
    string Class = "",         // point: the control's window class segment (`panel`, `window`, `static`, ...)
    int ClassIndex = 0,        // point: which control of that class on the page it is (1 = the first)
    string Name = "",          // point: the control's `Name` when it has one (information, and the first thing a replay looks for)
    double Fx = -1, double Fy = -1)   // the click point as a fraction of the control's width and height (0..1); -1 = not recorded
{
    public bool HasPoint => Fx >= 0 && Fy >= 0;

    /// <summary>The hit of a `palette_click` event, or null when it holds none of the fields.</summary>
    public static PaletteHit? FromJson(JsonElement e)
    {
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int N(string k, int d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : d;
        double D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : -1;
        var hit = new PaletteHit(S("list"), S("listLabel"), S("item"), N("itemIndex", -1), N("occurrence", 0), S("view"), S("class"), N("classIndex", 0), S("controlName"), D("x"), D("y"));
        return hit == new PaletteHit() ? null : hit;
    }
}

/// <summary>The kinds of a `palette_click` and their words.</summary>
public static class PaletteClickKinds
{
    public const string Tree = "tree", Expand = "expand", Collapse = "collapse", Image = "image", ListView = "listview", Point = "point";

    /// <summary>The kind a `kind` property names for a palette click: the kinds of <see cref="DialogClickKinds.Of"/> ("click", "dblclick") and the ones above; anything else (also none: every recording from before) is a button click.</summary>
    public static string Of(string? kind) => kind switch
    {
        Tree => Tree, Expand => Expand, Collapse => Collapse, Image => Image, ListView => ListView, Point => Point,
        _ => DialogClickKinds.Of(kind),
    };

    /// <summary>The name a click on a tree item is recorded under: the item's text, "(blank)" for an item with none (as a list view names a blank row), so a click is never recorded with an empty name.</summary>
    public static string ItemName(string? text) => LevelText(text);

    /// <summary>The path a click on a tree item is recorded under: the item texts from the top level down, a blank level as "(blank)" (a path with only a blank top-level item would otherwise be empty and name nothing).</summary>
    public static string ClickPath(IEnumerable<string?> texts) => TreePath.Join(texts.Select(t => string.IsNullOrWhiteSpace(t) ? BlankItem : t));

    /// <summary>The levels of a recorded click path as a replay compares them: "(blank)" and the empty level an older recording holds for a blank item both name a blank item.</summary>
    public static List<string> ClickLevels(string? path) => TreePath.Split(path).Select(l => l.Length == 0 ? BlankItem : l).ToList();

    /// <summary>The text of a live tree item as <see cref="ClickLevels"/> names it.</summary>
    public static string LevelText(string? itemText) => string.IsNullOrWhiteSpace(itemText) ? BlankItem : itemText.Trim();

    public const string BlankItem = "(blank)";

    /// <summary>Does comctl32 draw a +/- button for a node a replay has to open with a click? Only with TVS_HASBUTTONS, and on a top-level node only when TVS_LINESATROOT is set too (WinForms clears it for ShowRootLines = false):
    /// without a button there is nothing to click, and the node is opened by message (with a warning) as for a tree with no buttons.</summary>
    public static bool ButtonExpected(uint treeStyle, bool topLevel) => (treeStyle & 0x1) != 0 && (!topLevel || (treeStyle & 0x4) != 0);

    /// <summary>How long a real click has to wait so that it cannot merge with the one before it into a double-click: nothing when the last click was not near it (or none was made); else until the double-click time (plus a margin) after it.</summary>
    public static long SpacingWaitMs(long lastClickAtMs, long nowMs, long doubleClickMs, bool nearLast) =>
        lastClickAtMs == 0 || !nearLast ? 0 : Math.Max(0, lastClickAtMs + doubleClickMs + 60 - nowMs);

    /// <summary>Does the kind work on a tree item (`tree`, `treeLabel`, `path`)?</summary>
    public static bool OnTree(string kind) => kind is Tree or Expand or Collapse or DialogClickKinds.DoubleClick;

    /// <summary>Is the click one of the new kinds that is not a press of a push button and has no button text of its own?</summary>
    public static bool IsIntermediate(string kind) => kind is Tree or Expand or Collapse or Image or ListView or Point;

    /// <summary>The words of a click, with the caller's markup for names and paths (<paramref name="code"/>: a path or item, <paramref name="bold"/>: a control's name): "Click tree item `A › B` in **Catalog**",
    /// "Expand tree item `A`", "Click list item `Oak` in **Finishes**", "Click Panel no. 2 at 40% × 60%". Null for a plain button (the caller words it).</summary>
    public static string? Words(string kind, string button, string path, string treeLabel, PaletteHit? hit, Func<string, string> code, Func<string, string> bold, bool capital = true)
    {
        string C(string s) => capital ? char.ToUpperInvariant(s[0]) + s[1..] : s;
        string In(string label) => label.Length > 0 ? " in " + bold(label) : "";
        switch (kind)
        {
            case Tree: return C("click tree item ") + code(path) + In(treeLabel);
            case Expand: return C("expand tree item ") + code(path) + In(treeLabel);
            case Collapse: return C("collapse tree item ") + code(path) + In(treeLabel);
            case ListView:
                return C("click list item ") + code(hit?.Item.Length > 0 ? hit.Item : "(blank)") + In(hit?.ControlLabel ?? "");
            case Point:
                return C("click ") + (hit == null ? "a control" : PointName(hit)) + (hit is { HasPoint: true } ? $" at {Percent(hit.Fx)} × {Percent(hit.Fy)}" : "");
            default: return null;
        }
    }

    /// <summary>The name of a picture box nothing names (<see cref="Words"/> then says "no. N").</summary>
    public const string ImageUnnamed = "(image)";

    /// <summary>Words for a click on an image with the live position: "Click image no. 2" when it has no name.</summary>
    public static string ImageWords(string button, int index, int legacyIndex, Func<string, string> bold, bool capital = true)
    {
        var start = capital ? "Click image " : "click image ";
        if (button.Length == 0 || button == ImageUnnamed) return start + "no. " + Math.Max(index, legacyIndex > 0 ? legacyIndex : 1);
        return start + bold(button) + (index > 1 ? $" (no. {index})" : "");
    }

    static string PointName(PaletteHit h) =>
        (h.Name.Length > 0 ? h.Name : h.Class.Length > 0 ? h.Class : "control") + (h.ClassIndex > 0 && h.Name.Length == 0 ? " no. " + h.ClassIndex : "");

    static string Percent(double f) => Math.Round(f * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The words of a palette step's click as one sentence part, for every kind (a button: "click Draw box"; a double-click as before).</summary>
    public static string Phrase(string kind, string button, string path, string treeLabel, int index, int legacyIndex, PaletteHit? hit, Func<string, string> code, Func<string, string> bold, bool capital)
    {
        if (kind == Image) return ImageWords(button, index, legacyIndex, bold, capital);
        if (kind == DialogClickKinds.DoubleClick) return DialogClickKinds.DoubleClickWords(path, treeLabel, code, bold, capital);
        if (Words(kind, button, path, treeLabel, hit, code, bold, capital) is { } w) return w;
        return (capital ? "Click " : "click ") + bold(button);
    }

    // ---- replay: which live control is the recorded one ------------------------------------------------------------------------------

    /// <summary>A control of one window class on a page, as the replay lists it: its place in the class's order and, for a WinForms control, its `Name`.</summary>
    public readonly record struct Candidate(string Class, string Name);

    /// <summary>The position in <paramref name="live"/> of the control a `point` click was recorded on: when the recording has the control's `Name` it is the control with that name, and when that name is gone the answer is -1 (a control that was
    /// renamed or removed is not "the Nth control of its class": another control would be clicked); a control that had no name is the <see cref="PaletteHit.ClassIndex"/>-th of the recorded class. -1 when there is none, with the reason.</summary>
    public static int FindPoint(IReadOnlyList<Candidate> live, PaletteHit hit, out string? problem)
    {
        problem = null;
        if (hit.Name.Length > 0)
        {
            for (int i = 0; i < live.Count; i++)
                if (string.Equals(live[i].Name, hit.Name, StringComparison.OrdinalIgnoreCase)) return i;
            problem = $"no control named {hit.Name} ({hit.Class}) in the palette";
            return -1;
        }
        int want = Math.Max(1, hit.ClassIndex), seen = 0;
        for (int i = 0; i < live.Count; i++)
            if (string.Equals(live[i].Class, hit.Class, StringComparison.OrdinalIgnoreCase) && ++seen == want) return i;
        problem = $"no {hit.Class} control no. {want} in the palette";
        return -1;
    }

    /// <summary>The client point of a `point` click inside a control of the given size, kept inside it: the recorded fractions, the middle when none were recorded.</summary>
    public static (int X, int Y) PointIn(PaletteHit hit, int width, int height)
    {
        double fx = hit.HasPoint ? hit.Fx : 0.5, fy = hit.HasPoint ? hit.Fy : 0.5;
        int x = (int)Math.Round(fx * width), y = (int)Math.Round(fy * height);
        return (Math.Clamp(x, 0, Math.Max(0, width - 1)), Math.Clamp(y, 0, Math.Max(0, height - 1)));
    }

    /// <summary>The fraction of a size a client coordinate is, rounded to 3 decimals and kept in 0..1.</summary>
    public static double Fraction(int coordinate, int size) => size <= 0 ? 0.5 : Math.Round(Math.Clamp((double)coordinate / size, 0, 1), 3);

    /// <summary>The list view's view mode as recorded (`view`): the LVS_* view bits of its style.</summary>
    public static string ViewName(uint style) => (style & 0x3) switch { 0 => "icon", 1 => "report", 2 => "smallicon", _ => "list" };

    /// <summary>The picture box a replay clicks: the <paramref name="index"/>-th live picture that answers to the recorded name (<see cref="ButtonNames.Find{T}"/>: a picture has no text of its own, so only a recorded
    /// `buttonFrom` source names it, and a recording with none matches every picture by the unnamed name). -1 when there are not that many.</summary>
    public static int FindImage(IReadOnlyList<string?> liveNames, string recorded, string? from, int index)
    {
        var all = Enumerable.Range(0, liveNames.Count).ToList();
        return ButtonNames.Find(all, recorded, from, index, _ => ImageUnnamed, _ => false, (i, _) => liveNames[i], (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which of the pictures with the same name the one at <paramref name="at"/> is (1 = the first), counted as <see cref="FindImage"/> counts.</summary>
    public static int ImageIndexAmong(IReadOnlyList<string?> liveNames, int at, string name, string? from)
    {
        var all = Enumerable.Range(0, liveNames.Count).ToList();
        return ButtonNames.IndexAmong(all, at, name, from, _ => ImageUnnamed, _ => false, (i, _) => liveNames[i], (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
    }
}
