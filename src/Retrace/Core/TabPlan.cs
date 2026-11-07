namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The tab controls of a dialog as the reader saw them (texts, selection, which of them sit in the page of which), the path a control is on, and what a replay
// must do next to show a recorded path: nothing, switch one tab control to one of its tabs, or say that no such tab is there.

/// <summary>One tab control: <see cref="Id"/> (its place in the scan's list of tab controls), the <see cref="Items"/> texts in tab order, the <see cref="Selected"/> index (-1 = none) and
/// <see cref="Outer"/>, the ids of the tab controls whose page it sits in (any order: its depth is how many). A tab control that sits in no page has none.</summary>
public sealed record TabInfo(int Id, IReadOnlyList<string> Items, int Selected, IReadOnlyList<int> Outer)
{
    public int Depth => Outer.Count;
    public string SelectedText => Selected >= 0 && Selected < Items.Count ? Items[Selected] : "";
}

public enum TabAction { Done, Switch, Missing }

/// <summary>What <see cref="TabPlan.Next"/> decided. Switch: tab control <see cref="Tab"/> (a <see cref="TabInfo.Id"/>) goes to <see cref="Item"/> (<see cref="Text"/>).
/// Missing: <see cref="Problem"/> says which tab is not there.</summary>
public sealed record TabStep(TabAction Action, int Tab = -1, int Item = -1, string Text = "", string? Problem = null);

public static class TabPlan
{
    /// <summary>The path of a control that sits in the pages of the tab controls <paramref name="containing"/> (ids): the selected tab of each, outermost first. "" for none.</summary>
    public static string PathOf(IEnumerable<int> containing, IReadOnlyList<TabInfo> tabs)
    {
        var ids = containing.ToHashSet();
        return TabPath.Join(tabs.Where(t => ids.Contains(t.Id)).OrderBy(t => t.Depth).Select(t => t.SelectedText));
    }

    /// <summary>The innermost tab control whose page holds tab control <paramref name="t"/>, or -1 when it sits in no page.</summary>
    public static int Container(TabInfo t, IReadOnlyList<TabInfo> tabs)
    {
        int best = -1, depth = -1;
        foreach (var id in t.Outer)
        {
            var o = tabs.FirstOrDefault(x => x.Id == id);
            if (o != null && o.Depth > depth) { best = o.Id; depth = o.Depth; }
        }
        return best;
    }

    /// <summary>What the dialog shows as a whole: the selected tab of the first outermost tab control, then of the first tab control in that page, and so on. A button that sits in no tab
    /// (Apply) is pressed with this showing.</summary>
    public static string Showing(IReadOnlyList<TabInfo> tabs)
    {
        var texts = new List<string>();
        int parent = -1;
        for (int d = 0; d < 16; d++)
        {
            var next = tabs.FirstOrDefault(t => Container(t, tabs) == parent && t.SelectedText.Length > 0);
            if (next == null) break;
            texts.Add(next.SelectedText);
            parent = next.Id;
        }
        return TabPath.Join(texts);
    }

    /// <summary>The index of the tab with <paramref name="text"/> in <paramref name="t"/> (the first one), or -1.</summary>
    public static int ItemIndex(TabInfo t, string text)
    {
        for (int i = 0; i < t.Items.Count; i++) if (TabPath.SameText(t.Items[i], text)) return i;
        return -1;
    }

    /// <summary>The next move toward showing <paramref name="path"/>: level by level from the outermost, the tab control of that level (in the page chosen above it) that has a tab of that
    /// text. One that is not showing that tab yet is switched (the next level's tab control is in its page, which does not exist until then). <see cref="TabAction.Done"/> when every level
    /// shows its tab, also for "" (no tab asked for). <see cref="TabAction.Missing"/> when no tab control at a level has such a tab (or it is not created yet: the caller waits a while).</summary>
    public static TabStep Next(string? path, IReadOnlyList<TabInfo> tabs)
    {
        var levels = TabPath.Split(path);
        int parent = -1;
        for (int d = 0; d < levels.Count; d++)
        {
            var here = tabs.Where(t => Container(t, tabs) == parent && ItemIndex(t, levels[d]) >= 0).ToList();
            if (here.Count == 0)
                return new TabStep(TabAction.Missing, Problem: $"no tab {levels[d]} in the dialog" + (d > 0 ? $" (under {TabPath.Join(levels.Take(d))})" : ""));
            // one that shows the tab already, else the first
            var t = here.FirstOrDefault(x => x.Selected == ItemIndex(x, levels[d])) ?? here[0];
            var item = ItemIndex(t, levels[d]);
            if (t.Selected != item) return new TabStep(TabAction.Switch, t.Id, item, TabPath.Clean(t.Items[item]));
            parent = t.Id;
        }
        return new TabStep(TabAction.Done);
    }
}

/// <summary>A run of consecutive items that sit on the same tab ("" = none).</summary>
public sealed record TabRun<T>(string Tab, IReadOnlyList<T> Items);

/// <summary>Wording by tab: the changes of a dialog grouped by the tab they were made on ("On the Text tab: set Text height to 3.5").</summary>
public static class TabWording
{
    /// <summary>Consecutive items on the same tab together, in the order they come (a replay keeps that order, and switches tabs only between runs).</summary>
    public static List<TabRun<T>> Runs<T>(IEnumerable<T> items, Func<T, string?> tabOf)
    {
        var runs = new List<(string Tab, List<T> Items)>();
        foreach (var item in items)
        {
            var tab = tabOf(item) ?? "";
            if (runs.Count > 0 && TabPath.Same(runs[^1].Tab, tab)) runs[^1].Items.Add(item);
            else runs.Add((tab, new List<T> { item }));
        }
        return runs.Select(r => new TabRun<T>(r.Tab, r.Items)).ToList();
    }

    /// <summary>"on the Text tab" (the tab path is shown as it is: "on the Fit › Text placement tab").</summary>
    public static string OnTab(string tab) => $"on the {tab} tab";

    /// <summary>Short phrases in order (each starting with a lower-case word: "set Text height to 3.5") with the tab each belongs to; the first phrase of a run on a tab is prefixed
    /// "on the Text tab: ", and the first one after a run on a tab that sits on none "outside the tabs: ". Without any tab the phrases come back as they are.</summary>
    public static List<string> Phrases(IEnumerable<(string Tab, string Phrase)> items)
    {
        var list = new List<string>();
        string? prev = null;
        foreach (var (tab, phrase) in items)
        {
            var t = tab ?? "";
            if (prev == null ? t.Length > 0 : !TabPath.Same(prev, t))
                list.Add((t.Length > 0 ? OnTab(t) : "outside the tabs") + ": " + phrase);
            else list.Add(phrase);
            prev = t;
        }
        return list;
    }
}

/// <summary>Instruction sentences, one per line, with a line "On the Text tab:" before the first sentence of a run on a tab (and "Outside the tabs:" for the first one after such a run that sits on none).
/// Feed it the sentences in order; <see cref="Reset"/> forgets the tab (after the sentences of a nested dialog, which has its own).</summary>
public sealed class TabLines
{
    string? _prev;

    /// <summary>The tab the last sentence was on ("" = none), or null before the first.</summary>
    public string? Current => _prev;

    public void Reset() => _prev = null;

    /// <summary>The lines to write for one sentence: its header when the tab changed, then the sentence.</summary>
    public IEnumerable<string> Add(string? tab, string line)
    {
        var t = tab ?? "";
        if (_prev == null ? t.Length > 0 : !TabPath.Same(_prev, t)) yield return t.Length > 0 ? $"On the {t} tab:" : "Outside the tabs:";
        _prev = t;
        yield return line;
    }
}

/// <summary>One value of a segment as the planner sees it: the tab it sits on ("" = none), whether a live field matches it now (<see cref="FillMatcher.Match"/>), and whether it still has to be set.</summary>
/// <param name="Retrying">a live field matches but setting it failed before (a list the program is still filling): it is tried again on its tab and blocks any switch meanwhile</param>
/// <param name="Expired">the controls timeout of this value has run out: it is counted from when the value was first tried on its tab, and a tab switch does not restart it</param>
public sealed record FillItem(string Tab, bool Matched, bool Pending, bool Retrying = false, bool Expired = false);

/// <summary>What to do next in a segment with tabs: set these values now (indexes into the items, in order), give these up, wait on the showing tab for this one (<see cref="WaitFor"/>, -1 = none),
/// or, after the setting, show tab <see cref="SwitchPath"/> by <see cref="Switch"/>.</summary>
public sealed record FillPlan(IReadOnlyList<int> Set, IReadOnlyList<int> Fail, int WaitFor, TabStep? Switch, string? SwitchPath, string? Problem);

/// <summary>The one planner of a fill with tabs, shared by the dialog replay and the palette replay. In the recorded order: a value on the tab that shows (or on none) is set when a live field
/// matches it, and otherwise (or when setting it failed before) retried on that tab (<see cref="FillPlan.WaitFor"/>) until its own <see cref="FillItem.Expired"/> (the controls timeout), then given up with its own reason; no other tab is shown while
/// such a value waits, so the tabs never alternate. The first value on a tab that is not showing ends the pass: what comes before it is set (the caller reads it back), then that tab is shown.</summary>
public static class TabFill
{
    /// <param name="expired">the wait for a tab that is not there yet (its page is made when the page above is shown) has run out</param>
    public static FillPlan Next(IReadOnlyList<FillItem> items, IReadOnlyList<TabInfo> tabs, bool expired)
    {
        var set = new List<int>();
        var fail = new List<int>();
        int wait = -1;
        string? problem = null;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!it.Pending) continue;
            var step = string.IsNullOrEmpty(it.Tab) ? new TabStep(TabAction.Done) : TabPlan.Next(it.Tab, tabs);
            if (step.Action == TabAction.Done)
            {
                if (it.Matched) { set.Add(i); if (it.Retrying && wait < 0) wait = i; }
                else if (it.Expired) fail.Add(i);
                else if (wait < 0) wait = i;
                continue;
            }
            if (wait >= 0 || fail.Count > 0) break;   // a value on this tab is still waiting: no other tab is shown
            if (step.Action == TabAction.Missing)
            {
                if (expired) { fail.Add(i); problem = step.Problem; } else wait = i;
                break;
            }
            return new FillPlan(set, fail, -1, step, it.Tab, null);
        }
        return new FillPlan(set, fail, wait, null, null, problem);
    }
}
