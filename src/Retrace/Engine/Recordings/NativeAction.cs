using System.Text.Json;

namespace Retrace.Engine.Recordings;

/// <summary>What the person did in a native palette (AutoCAD's own palettes, such as AutoCAD Electrical's Project Manager), from a
/// <c>native_click</c> or <c>native_select</c> event: standard Windows controls under the palette's window, found again on replay by
/// their kind and position (and control id).</summary>
/// <param name="Control">tree | button | combo</param>
/// <param name="Action">tree: click | dblclick | rclick; button: click; combo: select</param>
/// <param name="Path">tree: the item's texts from the top level down</param>
/// <param name="Index">which control of that kind in the palette (1 = the first, in window order)</param>
/// <param name="Id">the control's id (GetDlgCtrlID), 0 when unknown</param>
/// <param name="Label">button: its tool tip or text, null when it has none</param>
/// <param name="Value">combo: the entry picked; null until a <c>native_value</c> names it (a drop-down list that draws its entries itself
/// holds no text: AutoCAD Electrical's project list, named by the project that became active)</param>
/// <param name="Entry">combo: the entry's position in the list, 1 = the first; 0 when unknown</param>
public sealed record NativeAction(string Control, string Action, IReadOnlyList<string> Path, int Index, int Id, string? Label, string? Value, int Entry = 0)
{
    /// <summary>A modal window opening this soon after a native palette action is that action's doing. If it also closed by itself (its
    /// <c>dialog_values</c> read nothing and saw no OK or Cancel: AutoCAD Electrical's "Updating..."), it is not a step.</summary>
    public const long ModalMs = 3000;

    /// <summary>Whether a <c>dialog_values</c> event describes a window that closed by itself: no field read, no OK or Cancel seen.</summary>
    public static bool ClosedByItself(JsonElement dialogValues) =>
        !(dialogValues.TryGetProperty("closedWith", out var c) && c.ValueKind == JsonValueKind.String)
        && !(dialogValues.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.Number && r.GetInt32() > 0);

    public static NativeAction? From(JsonElement e)
    {
        string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        var control = S("control");
        if (control is not ("tree" or "button" or "combo")) return null;
        var path = new List<string>();
        if (e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var x in p.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) path.Add(x.GetString() ?? "");
        if (control == "tree" && path.Count == 0) return null;
        return new NativeAction(control, S("action") ?? (control == "combo" ? "select" : "click"), path, Math.Max(1, I("index")), I("id"), S("label"), S("value"), I("entry"));
    }

    /// <summary>"NFPADEMO › SCHEMATIC › demo01.dwg"</summary>
    public string PathText => string.Join(" › ", Path);

    /// <summary>The thing acted on, in a few words: the tree item, the button's label, the entry picked. For names and keys.</summary>
    public string Target => Control switch
    {
        "tree" => Path.Count > 0 ? Path[^1] : "",
        "button" => Label ?? $"button {Index}",
        _ => Value ?? (Entry > 0 ? $"entry {Entry}" : ""),
    };

    /// <summary>What to do, as a phrase: "double-click NFPADEMO › SCHEMATIC › demo01.dwg in the tree". <paramref name="em"/> wraps the
    /// names (bold in markdown), <paramref name="esc"/> escapes them.</summary>
    public string Describe(Func<string, string>? esc = null, Func<string, string>? em = null)
    {
        esc ??= s => s; em ??= s => s;
        string N(string s) => em(esc(s));
        return Control switch
        {
            "tree" => (Action switch { "dblclick" => "double-click ", "rclick" => "right-click ", _ => "select " }) + N(PathText) + " in the tree",
            "button" => Label != null ? $"click the {N(Label)} button" : $"click toolbar button {Index}",
            _ => Value != null ? $"pick {N(Value)} in the drop-down list" : $"pick entry {Entry} in the drop-down list",
        };
    }
}
