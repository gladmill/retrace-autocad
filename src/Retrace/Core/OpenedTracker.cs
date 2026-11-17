// NO AutoCAD references in this file (linked into the engine test project).
namespace Retrace.Core;

/// <summary>Which palettes were open when a ribbon / Quick Access Toolbar click was made, and which command or LISP of that click they belong to. The click takes the snapshot (<see cref="Click"/>); the top-level command or LISP
/// it starts keeps it (<see cref="CommandStart"/> / <see cref="LispStart"/>) and hands it back when that same top-level command or LISP ends (<see cref="CommandEnd"/> / <see cref="LispEnd"/>), so a command nested in it (an
/// 'ZOOM run by a .NET command, the commands a LISP runs) neither takes nor drops it. A cancelled or failed top-level command, or any other top-level start, drops it: no stale snapshot reaches a later command. The depths are
/// the number of commands / LISP expressions running <i>outside</i> the one being reported (0 = it is the top level). Pure data; UI thread only.</summary>
public sealed class OpenedTracker
{
    /// <summary>How long after the click a command or LISP may start and still be the click's (same as the recorder's attribution window).</summary>
    public const long FreshMs = 2000;

    IReadOnlyList<PaletteState>? _click;
    long _clickAt;
    IReadOnlyList<PaletteState>? _active;
    string? _activeKey;   // the command's name, or "(lisp)"

    const string LispKey = "(lisp)";

    /// <summary>The click was made at <paramref name="nowMs"/>; <paramref name="palettes"/> is what was open then.</summary>
    public void Click(IReadOnlyList<PaletteState> palettes, long nowMs) { _click = palettes; _clickAt = nowMs; }

    /// <summary>A command starts. Top level (no command or LISP running) and started by the click (<paramref name="viaRibbon"/>, within <see cref="FreshMs"/>): it keeps the click's snapshot. Any other top-level
    /// start drops what was kept. A nested command changes nothing.</summary>
    public void CommandStart(string name, bool viaRibbon, int commandDepth, int lispDepth, long nowMs)
    {
        if (commandDepth > 0 || lispDepth > 0) return;
        Take(name, viaRibbon, nowMs);
    }

    /// <summary>A LISP expression starts; same rule as <see cref="CommandStart"/> (the click's macro ran it: the click is fresh).</summary>
    public void LispStart(int commandDepth, int lispDepth, long nowMs)
    {
        if (commandDepth > 0 || lispDepth > 0) return;
        Take(LispKey, _click != null && nowMs - _clickAt <= FreshMs, nowMs);
    }

    void Take(string key, bool fromClick, long nowMs)
    {
        if (fromClick && _click != null && nowMs - _clickAt <= FreshMs) { _active = _click; _activeKey = key; _click = null; }
        else { _active = null; _activeKey = null; }
    }

    /// <summary>A command ended (<paramref name="commandDepth"/>: how many are still running outside it after its end). The snapshot to attach to its end picture when it is the top-level command that kept one, else null (kept).</summary>
    public IReadOnlyList<PaletteState>? CommandEnd(string name, int commandDepth, int lispDepth) => End(name, commandDepth, lispDepth);

    /// <summary>A LISP expression ended; as <see cref="CommandEnd"/>.</summary>
    public IReadOnlyList<PaletteState>? LispEnd(int commandDepth, int lispDepth) => End(LispKey, commandDepth, lispDepth);

    IReadOnlyList<PaletteState>? End(string key, int commandDepth, int lispDepth)
    {
        if (commandDepth > 0 || lispDepth > 0 || _active == null || !string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase)) return null;
        var r = _active; _active = null; _activeKey = null;
        return r;
    }

    /// <summary>A command was cancelled or failed (depths as for <see cref="CommandEnd"/>): when it is the top-level one that kept a snapshot, the snapshot is dropped.</summary>
    public void CommandAborted(string name, int commandDepth, int lispDepth) => End(name, commandDepth, lispDepth);

    /// <summary>A LISP expression was cancelled; as <see cref="CommandAborted"/>.</summary>
    public void LispAborted(int commandDepth, int lispDepth) => End(LispKey, commandDepth, lispDepth);

    /// <summary>A recording starts: nothing is kept.</summary>
    public void Clear() { _click = null; _active = null; _activeKey = null; }

    /// <summary>Is a snapshot kept for a running top-level command or LISP? (tests)</summary>
    public bool Keeping => _active != null;
}

/// <summary>Whether the button of the Quick Access Toolbar under the mouse is the item that was executed: ids equal, else the macros equal, else the texts equal. Anything else (a keytip or the keyboard ran another item while a
/// press was made on a toolbar button) is no match, and the click is not written as a toolbar click.</summary>
public static class QatMatch
{
    public static bool Same(string? copyId, string? copyMacro, string? copyText, string? itemId, string? itemMacro, string? itemText)
    {
        static bool Eq(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(copyId) && !string.IsNullOrWhiteSpace(itemId)) return Eq(copyId, itemId);   // both have an id: it decides
        if (Eq(copyMacro, itemMacro)) return true;
        return Eq(copyText, itemText);
    }

    /// <summary>The decision: a toolbar button whose own item is known must be the executed item (<paramref name="same"/>, <see cref="Same"/>). AutoCAD shows some toolbar items (a button without an icon) through the toolbar's own
    /// overflow host, whose item is not theirs (<paramref name="hostButton"/>): then the toolbar must hold the executed item (<paramref name="toolbarHoldsItem"/>, by object or id).</summary>
    public static bool Accept(bool hostButton, bool toolbarHoldsItem, bool same) => hostButton ? toolbarHoldsItem : same;
}

/// <summary>What a picture's marks are, from what was noted for it: <paramref name="boxOnly"/> (a palette that opened: "look here") draws the frame and never an arrow; otherwise a rectangle gives frame and arrow, a point only the arrow.</summary>
public enum PictureMark { None, Box, BoxAndArrow, ArrowAtPoint }

public static class PictureMarks
{
    public static PictureMark Choose(bool boxOnly, bool haveRect, bool haveClick)
    {
        if (boxOnly) return haveRect ? PictureMark.Box : PictureMark.None;
        if (haveRect) return PictureMark.BoxAndArrow;
        return haveClick ? PictureMark.ArrowAtPoint : PictureMark.None;
    }
}
