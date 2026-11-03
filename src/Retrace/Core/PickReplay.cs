using System.Text.Json;

namespace Retrace.Core;

/// <summary>What a replay may do with the view and the pick points of one step (<see cref="Plan.InputText"/> asks it). The AutoCAD side gives the two checks;
/// without them (<see cref="Plan"/> called with null, the tests) nothing here applies and the replay is as it was before: clicks as typed points, entities as `(handent ..)`.
/// Rule: **when unsure, use handles.** A pick point is typed only when the recorded view can be set here, is a plan view of the current UCS, is the one view the step uses,
/// and the point still hits the recorded object. Anything else falls back to `(handent ..)` and is written to <see cref="Notes"/>.
/// <see cref="View"/> is set when a typed point needs it: the replayer shows that view before it types the step and puts the person's view back after.</summary>
public sealed class PickReplay
{
    /// <summary>Why this view cannot be shown here (another layout, no such viewport, perspective, ...), or null when it can.</summary>
    public Func<ViewSpec, string?>? ViewProblem { get; init; }

    /// <summary>Why the recorded point no longer picks that object in <c>view</c> (as it would be shown here), or null when it does.
    /// Arguments: view, handle, recorded dxf name, the point as recorded (current UCS), whether the pick was inside a block (nested).</summary>
    public Func<ViewSpec, string, string?, double[], bool, string?>? PickProblem { get; init; }

    /// <summary>The command the step runs (global name): TRIM and EXTEND pick the part to cut by the click, so their single-click selections replay as the click.</summary>
    public string? Command { get; set; }

    /// <summary>The view the typed points of this step need; null when none does.</summary>
    public ViewSpec? View { get; private set; }

    /// <summary>What was decided and why not (one line each): shown in the replay's messages and logged.</summary>
    public List<string> Notes { get; } = new();

    bool IsTrim => Command is { } c0 && c0.TrimStart('_', '.', '-').Equals("TRIM", StringComparison.OrdinalIgnoreCase);

    public bool SelectionPicks => Command is { } c && c.TrimStart('_', '.', '-') is var n && (n.Equals("TRIM", StringComparison.OrdinalIgnoreCase) || n.Equals("EXTEND", StringComparison.OrdinalIgnoreCase));

    /// <summary>The point line that picks the recorded object of an `entity` / `nestedEntity` answer, or null when the replay must use `(handent ..)` (a note says why).</summary>
    public string? EntityPoint(bool nested, string handle, string? dxf, JsonElement value, string? prompt = null)
    {
        var what = nested ? "nested pick" : "pick";
        // the checks look at the drawing as it is before the step: a second pick at the same prompt (OFFSET, FILLET Multiple, ...) comes after an edit the first one made,
        // whether the first one was typed as a point or fell back to its handle (it edited either way)
        if (_afterEdit) return Fallback($"{what} of {handle}: it follows a dialog that may have changed the drawing");
        if (prompt != null && !_seenPrompts.Add(prompt)) return Fallback($"{what} of {handle}: an earlier pick at this prompt may have changed the drawing");
        var pt = ViewSpec.Nums(value, "pick", 2);
        if (pt == null) return Fallback($"{what} of {handle}: no pick point recorded");
        var view = ViewSpec.FromValue(value);
        if (view == null) return Fallback($"{what} of {handle}: no view recorded");
        if (Claim(view) is { } why) return Fallback($"{what} of {handle}: {why}");
        if (PickProblem?.Invoke(view, handle, dxf, pt, nested) is { } miss) { Release(); return Fallback($"{what} of {handle}: {miss}"); }
        return TypedSelection.PointLine(pt[0], pt[1], pt.Length > 2 ? pt[2] : 0);
    }

    // What may already have changed the drawing, counted for every answer of the step, typed as a point or not:
    readonly HashSet<string> _seenPrompts = new(StringComparer.Ordinal);               // prompts that had an entity pick
    readonly HashSet<string> _clickTargets = new(StringComparer.OrdinalIgnoreCase);    // objects of TRIM / EXTEND single clicks
    int _clickAnswers;                                                                 // TRIM / EXTEND single clicks
    int _otherSelections;                                                              // TRIM / EXTEND selections that are not a single click: handles, a window, a fence, typed text, a Shift-click (which extends)

    /// <summary>A selection answer of a TRIM / EXTEND step that is not a single click (handles, window, fence, typed text, a Shift-click that extends, boundary edges): it may have edited
    /// the drawing in ways the replay does not track, so no later click of the step is typed as a point. Called by <see cref="Plan"/> for every such answer.</summary>
    public void OtherSelection() { if (SelectionPicks) _otherSelections++; }

    bool _afterEdit;

    /// <summary>What follows is after something that may have changed the drawing (a dialog the step opened): every later pick uses its handle.</summary>
    public void AfterPossibleEdit() => _afterEdit = true;

    /// <summary>A selection that replays as its clicks (they are all that says what was selected: attributes of -ATTEDIT): the clicks need their view.
    /// The clicks are typed in any case (as before); this only asks for the view and says when it cannot be shown.</summary>
    public void ClicksNeedView(JsonElement value)
    {
        var view = ViewSpec.FromValue(value);
        if (view == null) { Notes.Add("clicks replayed in the current view: no view recorded"); return; }
        if (Claim(view) is { } why) Notes.Add($"clicks replayed in the current view: {why}");
    }

    /// <summary>One click on one object at a prompt where the part picked matters (TRIM, EXTEND): the click as a point line, or null for the handles.
    /// <paramref name="clicks"/> are the lines of `clicks`.</summary>
    public List<string>? SingleClick(JsonElement value, List<string> clicks, out bool counted)
    {
        counted = false;
        if (!SelectionPicks || ViewProblem == null) return null;
        if (clicks.Count != 1 || !TypedInput.IsPointLine(clicks[0])) return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("handles", out var hs) || hs.ValueKind != JsonValueKind.Array || hs.GetArrayLength() != 1) return null;
        if (value.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cnt) && cnt != 1) return null;
        var handle = hs[0].ValueKind == JsonValueKind.String ? hs[0].GetString() : null;
        if (string.IsNullOrEmpty(handle) || handle == "?") return null;
        // The checks look at the drawing as it is before the step. TRIM only removes geometry, and only on the object clicked, so what is near a later click on another object can only be
        // less than the scan saw: such a click may be typed. A second click on the same object, any later EXTEND click (it makes objects longer) and any click after another kind of
        // selection answer (which may have extended or trimmed more) use the handle. Every click answer is counted here, typed or not: it edited either way.
        counted = true;
        if (_afterEdit) { _clickTargets.Add(handle); _clickAnswers++; Notes.Add($"click on {handle}: it follows a dialog that may have changed the drawing, selected by handle"); return null; }
        bool again = !_clickTargets.Add(handle);
        bool earlier = _clickAnswers++ > 0;
        if (_otherSelections > 0 || again || (earlier && !IsTrim))
        {
            Notes.Add(_otherSelections > 0 ? $"click on {handle}: an earlier selection of this step may have changed the drawing, selected by handle"
                : again ? $"click on {handle}: that object was clicked earlier in this step and may have changed, selected by handle"
                : $"click on {handle}: an earlier click of this step may have changed the drawing, selected by handle");
            return null;
        }
        string? dxf = value.TryGetProperty("dxf", out var ds) && ds.ValueKind == JsonValueKind.Array && ds.GetArrayLength() == 1 && ds[0].ValueKind == JsonValueKind.String ? ds[0].GetString() : null;
        var parts = clicks[0].TrimStart('@', '#', '*').Split(',');
        if (parts.Length < 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return null;
        var pt = new[] { x, y, parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z) ? z : 0 };
        var view = ViewSpec.FromValue(value);
        if (view == null) { Notes.Add($"click on {handle}: no view recorded, selected by handle"); return null; }
        if (Claim(view) is { } why) { Notes.Add($"click on {handle}: {why}, selected by handle"); return null; }
        if (PickProblem?.Invoke(view, handle, dxf, pt, false) is { } miss) { Release(); Notes.Add($"click on {handle}: {miss}, selected by handle"); return null; }
        return clicks;
    }

    // ---- the one view of the step ----

    int _claims;   // answers that use <see cref="View"/>: a failed pick check gives its claim back, and the view with it when nothing else uses it

    /// <summary>Takes <paramref name="view"/> as the step's view. A reason when it cannot: no way to show it here, inside a floating viewport, or the step already needs another view.</summary>
    string? Claim(ViewSpec view)
    {
        if (ViewProblem == null) return "view cannot be restored here";
        // SetCurrentView inside a floating viewport changes the Viewport object (its scale and view; DBMOD and an undo entry): a replay never does that
        if (view.Space == "viewport") return "recorded inside a floating viewport (a replay does not change a viewport's view)";
        if (ViewProblem(view) is { } p) return p;
        if (View != null && !View.Same(view)) return "the step needs two different views";
        View ??= view;
        _claims++;
        return null;
    }

    /// <summary>A pick check failed after <see cref="Claim"/>: gives the claim back, and the view when no other answer uses it.</summary>
    void Release()
    {
        if (--_claims <= 0) { View = null; _claims = 0; }
    }

    string? Fallback(string note) { Notes.Add(note + ", used (handent)"); return null; }
}
