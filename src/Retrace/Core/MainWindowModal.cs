using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;

namespace Retrace.Core;

/// <summary>
/// A modal state AutoCAD entered with no dialog window of its own: an add-in that hides its dialog while a command runs, a BeginEditorCommand style interaction. The recorder's
/// `modal_enter` then carries the foreground window, which is AutoCAD's main window ("AutoCAD Mechanical 2026 - [Drawing1.dwg]"). That is not a dialog a person fills in: the
/// readers drop it from the plan, and what happens during it (the prompts) stays part of the command or script chain around it. AutoCAD-free (unit-tested).
/// New recordings say it themselves: `main` on `modal_enter` / `modal_title` (the window IS AutoCAD's main window) and `modal_nowindow` (`for` = the enter's `n`) just before the `modal_leave` of a state in which no
/// other window of the process ever showed. Older recordings are judged by the title of the window.
/// </summary>
static class MainWindowModal
{
    // "AutoCAD Mechanical 2026 - [Drawing2.dwg]", "AutoCAD Plant 3D 2026 - [x.dwg]", "AutoCAD 2026 - [Drawing1.dwg]", "Autodesk Civil 3D 2026 - [x.dwg]"; or the product alone
    static readonly Regex WithDrawing = Rx.Create(@"^((Autodesk\s+)?AutoCAD\b[^\[\]]*|Autodesk\s+[\w .]*?\s\d{4})\s-\s\[[^\[\]]*\]\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex ProductOnly = Rx.Create(@"^((Autodesk\s+)?AutoCAD(\s+\w+)*|Autodesk\s+[\w .]*?)\s+\d{4}\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The title is AutoCAD's main window title. A bare "AutoCAD" (a message box) is not.</summary>
    public static bool IsTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && (WithDrawing.IsMatch(title.Trim()) || ProductOnly.IsMatch(title.Trim()));

    /// <summary>A recorded modal window is AutoCAD's main window: the title matches and it is no standard dialog (#32770). No class recorded: the title decides.</summary>
    public static bool IsRecorded(string? title, string? windowClass) => IsTitle(title) && windowClass != "#32770";

    /// <summary>Is the window of a `modal_enter` / `modal_title` event AutoCAD's main window? The event's own `main` when the recorder wrote it; else (older recordings) the title rule.</summary>
    static bool IsMain(JsonElement e) =>
        e.TryGetProperty("main", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.ValueKind == JsonValueKind.True : IsRecorded(Str(e, "title"), Str(e, "windowClass"));

    /// <summary>A `modal_title` that names another window than the one the `modal_enter` saw: the recorder says it is not the main window, or (older recordings) its title is not the main window's title, or it has no
    /// title and its class differs from the enter's (an untitled dialog, `#32770` or another class).</summary>
    static bool IsOtherWindow(JsonElement title, JsonElement enter)
    {
        if (title.TryGetProperty("main", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False) return m.ValueKind == JsonValueKind.False;
        if (IsRecorded(Str(title, "title"), Str(title, "windowClass"))) return false;
        if (!string.IsNullOrWhiteSpace(Str(title, "title"))) return true;
        var cls = Str(title, "windowClass");
        if (string.IsNullOrEmpty(cls)) return false;   // nothing to tell it from the main window by
        return cls == "#32770" || cls != Str(enter, "windowClass");
    }

    /// <summary>Indexes (in <paramref name="list"/>) of the `modal_enter` and `modal_title` events of modal states that never had a dialog window. A state with `dialog_values`, `dialog_click` or `modal_alias`, or with a
    /// `modal_title` of another window, is a dialog. Otherwise: a recording whose `modal_enter` carries `main` is judged by the recorder's `modal_nowindow` alone (it says so only when no other window showed while the
    /// state was open); an older recording by the title (the enter is the main window's, nothing named another window).</summary>
    public static HashSet<int> Windowless(IReadOnlyList<JsonElement> list)
    {
        var res = new HashSet<int>();
        var withValues = new HashSet<long>();
        var noWindow = new HashSet<long>();
        foreach (var x in list)
        {
            var ty = Str(x, "type");
            if (ty is "dialog_values" or "dialog_click" or "modal_alias" && Num(x, "for") > 0) { withValues.Add(Num(x, "for")); if (ty == "modal_alias" && Num(x, "of") > 0) withValues.Add(Num(x, "of")); }
            else if (ty == "modal_nowindow" && Num(x, "for") > 0) noWindow.Add(Num(x, "for"));
        }
        for (int k = 0; k < list.Count; k++)
        {
            if (Str(list[k], "type") != "modal_enter") continue;
            long n = Num(list[k], "n");
            var members = new List<int> { k };
            bool other = false, hasValues = n > 0 && withValues.Contains(n);
            bool recorderSays = list[k].TryGetProperty("main", out var mv) && mv.ValueKind is JsonValueKind.True or JsonValueKind.False;
            bool main = IsMain(list[k]);
            for (int j = k + 1; j < list.Count; j++)
            {
                var ty = Str(list[j], "type");
                if (ty is "modal_enter" or "modal_leave") break;   // the episode ends at its leave: a title after it belongs to something else
                if (ty != "modal_title") continue;
                members.Add(j);
                if (Num(list[j], "n") > 0 && withValues.Contains(Num(list[j], "n"))) hasValues = true;
                if (IsOtherWindow(list[j], list[k])) other = true; else main = true;
            }
            bool flagged = n > 0 && noWindow.Contains(n);
            bool windowless = recorderSays ? flagged : flagged || main;   // the recorder's marker alone for a new recording, whatever `main` says
            if (!hasValues && !other && windowless) foreach (var m in members) res.Add(m);
        }
        return res;
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
