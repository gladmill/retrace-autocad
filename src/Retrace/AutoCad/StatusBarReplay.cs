using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>Replay side of a `statusbar_click` step: finds the item in the status bar's WPF tree again (<see cref="StatusBarMatch.Find"/>: key for AutoCAD's own items, else text, the stable part of the text, tool tip, place for an icon),
/// checks it is shown and enabled, moves the replay's own window off it, and queues a real mouse click at its middle (<see cref="RealMouse"/>: the cursor goes there and back, the buttons may be swapped, a window over the item stops the click).
/// An item that is not found is a problem the caller retries and then fails the step with; never a pass. UI thread only.</summary>
static class StatusBarReplay
{
    /// <summary>The live item for the step (null with the reason when not found), for the click and for the Tdoc picture.</summary>
    public static (StatusBarWatch.Item? Item, string How, string? Problem) Find(Step s)
    {
        if (s.StatusBar is not { } click) return (null, "", "the step has no status bar item");
        var all = StatusBarWatch.ReadAll();
        if (all.Count == 0) return (null, "", "AutoCAD's status bar could not be read (no items found in it)");
        var live = all.Select(i => new StatusBarMatch.Live(i.Text, i.Tooltip, i.Index, i.Kind, i.Own, i.Key, i.Visible && i.Rect.Width > 0, i)).ToList();
        var f = StatusBarMatch.Find(click, live);
        if (f.Item?.Tag is not StatusBarWatch.Item item) return (null, f.How, f.Problem ?? "the item was not found");
        return (item, f.How, f.Problem);
    }

    /// <summary>Null when the click is queued, else why not.</summary>
    public static string? Run(Step s, Action<int, int, int, int> avoid)
    {
        try
        {
            var (item, how, problem) = Find(s);
            var label = s.StatusBar?.Label ?? "the item";
            if (item == null || problem != null) return $"the status bar item {label} was not found: {problem}";
            if (!item.Enabled) return $"the status bar item {label} is disabled";
            var r = item.Rect;
            avoid(r.Left, r.Top, r.Right, r.Bottom);   // the replay window sits lower right, where the bar's right end is: it steps aside first
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            var model = item.Model; var host = item.Host;
            Log.Info($"replay: status bar item {label} found {how} (index {item.Index}, {item.Kind}); clicking {cx},{cy}");
            RealMouse.Enqueue($"the status bar item {label}", host, cx, cy,
                check: () => StatusBarWatch.At(cx, cy, host).Item is { } now && ReferenceEquals(now.Model, model) ? null : "the status bar changed since the item was found (it is no longer under that point)",
                person: false, right: s.StatusBar!.Button == StatusBarClick.Right);
            return null;
        }
        catch (Exception ex) { Log.Error("status bar replay", ex); return ex.Message; }
    }
}
