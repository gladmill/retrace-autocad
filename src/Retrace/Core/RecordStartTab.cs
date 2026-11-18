namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). Decides which ribbon tab to activate when a recording has started.

/// <summary>A ribbon tab as the choice sees it: the id, the title, and whether it is in the ribbon now (<see cref="Showing"/>: visible; a contextual tab is only visible while its context is on).</summary>
public readonly record struct TabFact(string Id, string Title, bool Showing, bool Contextual);

/// <summary>The setting `recordStartTab`: what the ribbon does the moment a recording has started (the person clicked Record on the Retrace tab and gave the test a name).
/// "home" (default) = the Home tab of the current workspace, "last" = the tab that was active before the Retrace tab, "stay" = leave the Retrace tab active (how it was before).</summary>
public static class RecordStartTab
{
    public const string Home = "home", Last = "last", Stay = "stay";

    /// <summary>The ids AutoCAD products use for their Home tab (compared without case). Products and workspaces differ; a title match follows when none is there.</summary>
    public static readonly string[] KnownHomeIds = { "ID_TabHome", "ID_TabHome3D", "ID_TabHome2D", "ID_Home", "ID_TabHomeAEC" };

    /// <summary>Canonical form of a stored value: "home", "last" or "stay"; anything else (missing, misspelt) is the default "home".</summary>
    public static string Normalize(string? setting)
    {
        var t = (setting ?? "").Trim();
        if (t.Equals(Last, StringComparison.OrdinalIgnoreCase)) return Last;
        if (t.Equals(Stay, StringComparison.OrdinalIgnoreCase)) return Stay;
        return Home;
    }

    /// <summary>True for exactly one of the three values (without case).</summary>
    public static bool IsKnown(string? setting)
    {
        var t = (setting ?? "").Trim();
        return t.Equals(Home, StringComparison.OrdinalIgnoreCase) || t.Equals(Last, StringComparison.OrdinalIgnoreCase) || t.Equals(Stay, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What was chosen: the tab to activate (null = leave the ribbon alone) and a short text of the rule that chose it, for the log.</summary>
    public readonly record struct Choice(string? TabId, string Rule);

    /// <summary>
    /// The tab to activate. <paramref name="startedOnOwnTab"/>: the recording was started with the Retrace tab active (a typed command or the Quick Access Toolbar leaves it false: nothing is switched).
    /// <paramref name="lastTabId"/>: the tab that was active before the Retrace tab became active (null = unknown). Never returns the Retrace tab (<paramref name="ownTabId"/>) or a tab that is not showing.
    /// "last" falls back to "home" when its tab is gone, hidden, or contextual and not showing.
    /// Home rule, in order: a known Home id, a showing tab titled "Home", the first showing tab that is not contextual and not the Retrace tab.
    /// </summary>
    public static Choice Choose(string? setting, IReadOnlyList<TabFact> tabs, string ownTabId, string? lastTabId, bool startedOnOwnTab)
    {
        var mode = Normalize(setting);
        if (mode == Stay) return new Choice(null, "stay: the setting leaves the tab alone");
        if (!startedOnOwnTab) return new Choice(null, "the recording was not started from the Retrace tab: nothing switched");
        var usable = tabs.Where(t => !string.Equals(t.Id, ownTabId, StringComparison.OrdinalIgnoreCase) && t.Showing && !string.IsNullOrEmpty(t.Id)).ToList();
        var fallback = "";
        if (mode == Last)
        {
            if (!string.IsNullOrEmpty(lastTabId))
            {
                var hit = usable.Where(x => string.Equals(x.Id, lastTabId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).FirstOrDefault();
                if (hit != null) return new Choice(hit, "last: the tab that was active before the Retrace tab");
                fallback = "the last tab is gone, hidden or not showing, so ";
            }
            else fallback = "no last tab is known, so ";
        }
        var homes = usable.Where(t => !t.Contextual).ToList();
        foreach (var id in KnownHomeIds)
        {
            var byId = homes.Where(t => IdIs(t.Id, id)).Select(t => t.Id).FirstOrDefault();
            if (byId != null) return new Choice(byId, fallback + "home: known Home id " + id);
        }
        var byTitle = homes.Where(t => string.Equals(t.Title?.Trim(), "Home", StringComparison.OrdinalIgnoreCase)).Select(t => t.Id).FirstOrDefault();
        if (byTitle != null) return new Choice(byTitle, fallback + "home: the tab titled Home");
        var first = homes.Where(t => !string.IsNullOrWhiteSpace(t.Title)).Select(t => t.Id).FirstOrDefault();
        if (first != null) return new Choice(first, fallback + "home: no Home tab, the first visible tab");
        return new Choice(null, fallback + "home: no usable tab");
    }

    /// <summary>The id is <paramref name="known"/>, or that id with a prefix such as the product's ("ACAD.ID_TabHome", as AutoCAD 2026 names the Home tab).</summary>
    static bool IdIs(string? id, string known) =>
        id != null && (id.Equals(known, StringComparison.OrdinalIgnoreCase) || id.EndsWith("." + known, StringComparison.OrdinalIgnoreCase));

    /// <summary>The switch is made only while the Retrace tab is still the active one: a person who clicked another tab during the name prompt, or before the next Idle, has chosen.</summary>
    public static bool StillOnOwnTab(string? activeTabId, string ownTabId) => !string.IsNullOrEmpty(activeTabId) && string.Equals(activeTabId, ownTabId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id the recorder must hold as the known active tab after a switch was tried (done, skipped or failed): the real active tab, null when there is none, so a failed switch cannot make the recorder swallow the next real click on that tab.</summary>
    public static string? RecorderSyncId(string? activeTabId) => string.IsNullOrEmpty(activeTabId) ? null : activeTabId;
}

/// <summary>Remembers which tab was active before the Retrace tab: the last active tab that is not the Retrace tab. Kept as an id, so a tab that was rebuilt (a workspace switch, a repair) is found again by it.</summary>
public sealed class LastTabTracker
{
    /// <summary>The tab that is active now. The Retrace tab does not replace what is remembered. A tab without an id (an add-in's) or no active tab makes the memory unknown (null), so "last" then falls back to Home
    /// instead of going to an older tab the person has left since.</summary>
    public void Note(string? activeTabId, string ownTabId)
    {
        if (string.Equals(activeTabId, ownTabId, StringComparison.OrdinalIgnoreCase)) return;
        Id = string.IsNullOrEmpty(activeTabId) ? null : activeTabId;
    }

    public string? Id { get; private set; }
}
