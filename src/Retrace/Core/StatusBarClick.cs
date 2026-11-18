// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Util;
namespace Retrace.Core;

// (regexes are made with Rx: a pattern in the process must not hang AutoCAD; see LayerRulesTests)

/// <summary>A click on an item of AutoCAD's status bar (the bar below the command line) as recorded in a `statusbar_click` event, and how it is worded, tested for "is this the same item" and decided.
/// An item is a pane (a text or icon button, AutoCAD's own Grid / Snap / Ortho and an add-in's) or a tray item. <see cref="Own"/> items are AutoCAD's: they carry a stable <see cref="Key"/> (the
/// registry key of the pane, which does not change with the language) and a <see cref="Name"/>; an add-in's item has neither, only the text and the tooltip it set.</summary>
/// <param name="Text">the item's text (empty for an icon)</param>
/// <param name="Tooltip">the item's tool tip, plain text (empty when none or when it is one of AutoCAD's macro tips)</param>
/// <param name="Index">the item's place among the panes (or tray items) of the status bar, counted from 0 in the order the bar lists them; -1 = unknown</param>
/// <param name="Kind">"pane" or "tray"</param>
/// <param name="Own">AutoCAD's own item, not an add-in's</param>
/// <param name="Key">own items: the pane's registry key, else empty</param>
/// <param name="Name">own items: the name AutoCAD gives the item ("Grid", "Snap Mode"), else empty</param>
/// <param name="Button">"left" or "right"</param>
public sealed record StatusBarClick(string Text, string Tooltip, int Index, string Kind, bool Own, string Key, string Name, string Button)
{
    public const string Pane = "pane", Tray = "tray", Left = "left", Right = "right";

    /// <summary>What the step calls the item: the text; for an item with no text its tool tip, else (AutoCAD's own) the name AutoCAD gives it; null when there is nothing to call it (the step then says "an item" with its place, never a guessed name).</summary>
    public string? Label => StatusBarWording.Label(Text, Tooltip, Name);

    public IEnumerable<(string, object?)> ToFields()
    {
        yield return ("text", Text); yield return ("tooltip", Tooltip); yield return ("index", Index); yield return ("kind", Kind); yield return ("own", Own);
        if (Key.Length > 0) yield return ("key", Key);
        if (Name.Length > 0) yield return ("name", Name);
        yield return ("button", Button);
    }

    /// <summary>The click of a `statusbar_click` event, or null when the event holds neither a text, a tooltip, a key nor a name (nothing to find the item by).</summary>
    public static StatusBarClick? From(JsonElement e)
    {
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var c = new StatusBarClick(S("text"), S("tooltip"), e.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : -1,
            S("kind") == Tray ? Tray : Pane, e.TryGetProperty("own", out var o) && o.ValueKind == JsonValueKind.True, S("key"), S("name"), S("button") == Right ? Right : Left);
        return c.Text.Length == 0 && c.Tooltip.Length == 0 && c.Key.Length == 0 && c.Name.Length == 0 && c.Index < 0 ? null : c;
    }
}

/// <summary>The words of a status bar click step.</summary>
public static class StatusBarWording
{
    internal static readonly Regex Blanks = Rx.Create(@"\s+", RegexOptions.CultureInvariant);
    internal static readonly Regex Digits = Rx.Create(@"\d+", RegexOptions.CultureInvariant);
    internal static readonly Regex ToolPane = Rx.Create(@"^acad-\d+:", RegexOptions.CultureInvariant);

    public static string? Label(string? text, string? tooltip, string? name)
    {
        foreach (var s in new[] { text, tooltip, name })
            if (!string.IsNullOrWhiteSpace(s)) return Flatten(s!);
        return null;
    }

    static string Flatten(string s) => StatusBarWording.Blanks.Replace(s.Trim(), " ");

    /// <summary>"Click Sample TB: edit on the status bar" (<paramref name="mark"/> puts the label in bold, e.g. markdown), right click: "Right-click ...". An item with no label: "Click the status bar item at position 3 (no text or tool tip)".</summary>
    public static string Phrase(StatusBarClick c, Func<string, string>? mark = null)
    {
        mark ??= x => x;
        var verb = c.Button == StatusBarClick.Right ? "Right-click" : "Click";
        if (c.Label is { } l) return $"{verb} {mark(l)} on the status bar";
        return c.Index >= 0 ? $"{verb} the {(c.Kind == StatusBarClick.Tray ? "tray item" : "item")} at position {c.Index + 1} of the status bar (it has no text or tool tip)" : $"{verb} an item of the status bar (it has no text or tool tip)";
    }

    /// <summary>The step's title in the plan: the same words, plain.</summary>
    public static string Title(StatusBarClick c) => Phrase(c);
}

/// <summary>Which live item is the recorded one (<see cref="Find"/>), and the rule for what is recorded at all (<see cref="Decide"/>).</summary>
public static class StatusBarMatch
{
    /// <summary>One item the status bar shows now, as the replay sees it.</summary>
    public sealed record Live(string Text, string Tooltip, int Index, string Kind, bool Own, string Key, bool Visible, object? Tag = null);

    public sealed record Found(Live? Item, string How, string? Problem);

    /// <summary>The part of an item's text that does not change with the item's state: the text before the last ':' ("Sample TB: edit" and "Sample TB: view" are both "Sample TB"); a text with digits has them replaced by '#'
    /// ("Layer 3" and "Layer 12" are "Layer #"); any other text is its own stable part. Compared without case and extra blanks.</summary>
    public static string StablePart(string? text)
    {
        var t = StatusBarWording.Blanks.Replace((text ?? "").Trim(), " ");
        int colon = t.LastIndexOf(':');
        if (colon > 0) t = t[..colon].Trim();
        t = StatusBarWording.Digits.Replace(t, "#").ToLowerInvariant();
        return t.Any(char.IsLetter) ? t : "";   // digits-only texts ("1:1", "100%") have no stable part: they would match any other number
    }

    static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(StatusBarWording.Blanks.Replace(a!.Trim(), " "), StatusBarWording.Blanks.Replace((b ?? "").Trim(), " "), StringComparison.OrdinalIgnoreCase);

    /// <summary>The live item for a recorded click, trying in this order: the key (own items; never anything else for them), the exact text, the stable part of the text (an item whose text changes with state), the exact tool tip, and the index only for
    /// an item that had neither text nor tool tip (an icon): a recorded text that is not found is never replaced by whatever sits at the same place. Several candidates at one step: the one at the recorded index, else the first; the answer says so.
    /// An item that exists but is not shown (hidden when the window is narrow) is reported as a problem, not as missing. Only items of the recorded kind are looked at.</summary>
    public static Found Find(StatusBarClick rec, IReadOnlyList<Live> live)
    {
        var kind = live.Where(x => x.Kind == rec.Kind).ToList();
        Found Pick(List<Live> c, string how)
        {
            if (c.Count == 0) return new(null, how, null);
            var chosen = c.FirstOrDefault(x => x.Index == rec.Index && x.Visible) ?? c.FirstOrDefault(x => x.Visible) ?? c[0];
            var more = c.Count > 1 ? $" ({c.Count} items match; the one {(chosen.Index == rec.Index ? "at the recorded place" : "first in the bar")} is used)" : "";
            return chosen.Visible ? new(chosen, how + more, null) : new(chosen, how, "the item exists but is not shown (turn it on in the status bar's Customization menu or widen AutoCAD's window: the bar hides items when the window is narrow)");
        }
        if (rec.Key.Length > 0)
        {
            var r = Pick(kind.Where(x => x.Own && string.Equals(x.Key, rec.Key, StringComparison.Ordinal)).ToList(), "by key");
            return r.Item != null ? r : new(null, "", $"AutoCAD's status bar has no item with the key {rec.Key}" + (rec.Name.Length > 0 ? $" ({rec.Name})" : ""));
        }
        var addIn = kind.Where(x => !x.Own).ToList();
        if (rec.Text.Trim().Length > 0)
        {
            if (Pick(addIn.Where(x => Same(x.Text, rec.Text)).ToList(), "by its text") is { Item: not null } byText) return byText;
            var stable = StablePart(rec.Text);
            if (stable.Length > 0 && stable != rec.Text.Trim().ToLowerInvariant() && Pick(addIn.Where(x => x.Text.Trim().Length > 0 && StablePart(x.Text) == stable).ToList(), "by the stable part of its text") is { Item: not null } byStable) return byStable;
        }
        if (rec.Tooltip.Trim().Length > 0 && Pick(addIn.Where(x => Same(x.Tooltip, rec.Tooltip)).ToList(), "by its tool tip") is { Item: not null } byTip) return byTip;
        if (rec.Text.Trim().Length == 0 && rec.Tooltip.Trim().Length == 0 && rec.Index >= 0 && addIn.FirstOrDefault(x => x.Index == rec.Index && x.Text.Trim().Length == 0 && x.Tooltip.Trim().Length == 0) is { } byIndex)
            return Pick([byIndex], "by its place (it has no text or tool tip)");
        var what = rec.Text.Trim().Length > 0 ? $"text \"{rec.Text.Trim()}\"" : rec.Tooltip.Trim().Length > 0 ? $"tool tip \"{rec.Tooltip.Trim()}\"" : $"place {rec.Index + 1}";
        return new(null, "", $"no {(rec.Kind == StatusBarClick.Tray ? "tray item" : "status bar item")} with {what} (it may belong to an add-in that is not loaded)");
    }

    /// <summary>What the recorder does with a click on the status bar it read: record it (<c>Reason</c> null), leave it out silently (<c>Silent</c>: AcadStatus's pane, Retrace's own), or leave it out with a reason for the one
    /// rate-limited line `status bar click not recorded: ...`. An add-in's item is recorded with either button; AutoCAD's own items are not (toggles are relative at replay, their menus are not recorded).</summary>
    public static (bool Record, bool Silent, string? Reason) Decide(string? text, string? tooltip, bool own, string? ownName, string? button)
    {
        if (IsToolItem(tooltip, text)) return (false, true, null);
        // AutoCAD's own items (Grid, Snap, Ortho, Polar, Object Snap ...) are not recorded in this release: a replay of a toggle is relative (it flips whatever the machine has now), a right-click only opens a menu the replay would leave open
        if (own) return (false, false, $"AutoCAD's own item {(string.IsNullOrWhiteSpace(ownName) ? "(no name)" : ownName!.Trim())} (toggles are not recorded)");
        if (button is not (StatusBarClick.Left or StatusBarClick.Right)) return (false, false, $"the {button} mouse button");
        return (true, false, null);
    }

    /// <summary>Which origin a command that starts now has, for the status bar click made at <paramref name="clickMs"/>: "statusbar" (the click started it, as a palette click's claim: <see cref="PaletteClaim"/>), "dialog" (a dialog the click opened has closed since and its OK
    /// started the command: the dialog step replays it, so it is no step of its own), or null (not the click's). </summary>
    public static string? Claim(bool consumed, long nowMs, long clickMs, long inputTick, long clickTick, bool modalSinceClick) =>
        PaletteClaim.Claims(consumed, nowMs, clickMs, inputTick, clickTick) ? (modalSinceClick ? "dialog" : "statusbar") : null;

    /// <summary>The text of an add-in's item as it will be written: a user profile path inside it is replaced by %USERPROFILE% (an item can show a project path or a login name; the rest is written as shown).</summary>
    public static string Scrub(string? text, string? profile)
    {
        var t = text ?? "";
        if (string.IsNullOrEmpty(profile) || profile!.Length < 4) return t;
        var p = profile.TrimEnd('\\', '/');
        return t.Replace(p, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase).Replace(p.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is the item one of ours to leave out: AcadStatus's pane (its tool tip starts "acad-" and the process id, then a colon) or anything of Retrace's own?</summary>
    public static bool IsToolItem(string? tooltip, string? text) =>
        (!string.IsNullOrEmpty(tooltip) && StatusBarWording.ToolPane.IsMatch(tooltip!)) || (text != null && text.StartsWith("Retrace", StringComparison.Ordinal) && text.Length <= 7);
}

/// <summary>The picture of a status bar click in smart zoom: the status bar strip across AutoCAD's window with a little of what lies above it (the command line), so a reader sees where on the screen the item is. The classic mode takes the whole window.</summary>
public static class StatusBarPicture
{
    /// <summary>How many pixels above the status bar the crop reaches.</summary>
    public const int ContextAbove = 90;

    /// <summary>The crop (screen rectangle), or null when the bar's rectangle is not known (then the window picture is taken). Always inside the window and always holding the item.</summary>
    public static PxRect? Crop(PxRect window, PxRect? bar, PxRect item)
    {
        if (bar is not { Width: > 0, Height: > 0 } b || item.Width <= 0 || item.Height <= 0) return null;
        var r = new PxRect(Math.Max(window.Left, b.Left), Math.Max(window.Top, b.Top - ContextAbove), Math.Min(window.Right, b.Right), Math.Min(window.Bottom, Math.Max(b.Bottom, item.Bottom)));
        r = new PxRect(Math.Min(r.Left, item.Left), Math.Min(r.Top, item.Top), Math.Max(r.Right, item.Right), Math.Max(r.Bottom, item.Bottom));
        r = new PxRect(Math.Max(window.Left, r.Left), Math.Max(window.Top, r.Top), Math.Min(window.Right, r.Right), Math.Min(window.Bottom, r.Bottom));
        return r.Width > 0 && r.Height > 0 && r.Left <= item.Left && r.Top <= item.Top && r.Right >= item.Right && r.Bottom >= item.Bottom ? r : null;
    }
}
