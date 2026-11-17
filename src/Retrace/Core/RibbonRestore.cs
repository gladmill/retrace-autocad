namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). The decisions behind the Retrace tab's repair:
// what the idle check does for what it finds, the loop guard, the one-shot "the tab may be missing" flag the event handlers set,
// and the command-line sentences of RETRACERIBBON.

/// <summary>What a look at the live ribbon found about the Retrace tab.</summary>
public enum RibbonFinding
{
    /// <summary>One Retrace tab, visible, in the current ribbon.</summary>
    Fine,
    /// <summary>There is no ribbon object (AutoCAD has not made one).</summary>
    NoRibbon,
    /// <summary>The ribbon exists but is closed (RIBBONCLOSE).</summary>
    Closed,
    /// <summary>The current ribbon has no tab with the Retrace id.</summary>
    TabMissing,
    /// <summary>The tab is in the collection but not shown (Show Tabs menu, a workspace).</summary>
    TabHidden,
    /// <summary>More than one tab with the Retrace id.</summary>
    TabDuplicate,
}

/// <summary>What to do about a finding.</summary>
[Flags]
public enum RibbonAction
{
    None = 0,
    OpenRibbon = 1,
    RemoveStale = 2,
    Build = 4,
}

/// <summary>Why the check ran before its next scheduled second: the words that go into the one log line of a repair.</summary>
public static class RestoreReason
{
    public const string Periodic = "tab missing at the periodic check";
    public const string CollectionChanged = "tab removed from the ribbon (collection changed)";
    public const string RibbonReplaced = "ribbon instance replaced";
    public const string WorkspaceChanged = "ribbon rebuilt after workspace change";
    public const string ItemInitialized = "ribbon items initialised again";
}

public static class RibbonRestoreRules
{
    /// <summary>
    /// The automatic repair (the idle check): only a tab that is not in the ribbon is built again. A tab that is in the collection but
    /// hidden is the user's choice (Show Tabs) and a closed ribbon is the user's choice; both are left to RETRACERIBBON.
    /// </summary>
    public static RibbonAction Auto(RibbonFinding f) => f == RibbonFinding.TabMissing ? RibbonAction.Build : RibbonAction.None;

    /// <summary>RETRACERIBBON: makes the tab present, visible and single, opening the ribbon if it is closed.</summary>
    public static RibbonAction Manual(RibbonFinding f) => f switch
    {
        RibbonFinding.Fine => RibbonAction.None,
        RibbonFinding.NoRibbon or RibbonFinding.Closed => RibbonAction.OpenRibbon | RibbonAction.RemoveStale | RibbonAction.Build,
        RibbonFinding.TabMissing => RibbonAction.Build,
        _ => RibbonAction.RemoveStale | RibbonAction.Build,   // hidden, duplicate
    };

    /// <summary>The finding from plain facts of the ribbon.</summary>
    public static RibbonFinding Find(bool ribbonExists, bool ribbonOpen, int tabsWithId, bool allVisible)
    {
        if (!ribbonExists) return RibbonFinding.NoRibbon;
        if (!ribbonOpen) return RibbonFinding.Closed;
        if (tabsWithId == 0) return RibbonFinding.TabMissing;
        if (tabsWithId > 1) return RibbonFinding.TabDuplicate;
        return allVisible ? RibbonFinding.Fine : RibbonFinding.TabHidden;
    }

    /// <summary>The line RETRACERIBBON prints when it found <paramref name="f"/> (and, if <paramref name="buildFailure"/> is set, could not build the tab).</summary>
    public static string Message(RibbonFinding f, bool ribbonOpened, string? buildFailure)
    {
        if (buildFailure != null) return "The Retrace tab could not be built: " + buildFailure + " (see retrace.log).";
        return f switch
        {
            RibbonFinding.Fine => "The Retrace tab is already in the ribbon; it is now the active tab.",
            RibbonFinding.NoRibbon => "The ribbon was not there" + (ribbonOpened ? "; opened it" : "") + ". The Retrace tab is built and will be shown as the active tab.",
            RibbonFinding.Closed => "The ribbon was closed" + (ribbonOpened ? "; opened it" : "") + ". The Retrace tab is built and will be shown as the active tab.",
            RibbonFinding.TabMissing => "The Retrace tab was missing from the ribbon. Built it again; it is the active tab.",
            RibbonFinding.TabHidden => "The Retrace tab was hidden (Show Tabs). Built it again and showed it; it is the active tab.",
            RibbonFinding.TabDuplicate => "The ribbon had more than one Retrace tab. Left one; it is the active tab.",
            _ => "",
        };
    }
}

/// <summary>
/// The loop guard of the automatic repair: at most <see cref="Max"/> repairs within <see cref="WindowMs"/>; then it stops (one stop line per trip)
/// and the next attempt window opens only after a pause that doubles with every trip (<see cref="QuietMs"/>, 2x, 4x ... up to <see cref="CapMs"/>), so a
/// fault that never ends costs a bounded number of attempts and log lines. While stopped, the removal of the Retrace tab itself (<c>removal</c>) pushes
/// the resume time out again; nothing else does. <see cref="Reset"/> (RETRACERIBBON) clears everything; a calm of <see cref="CapMs"/> after a pause ended, with no trip, resets the doubling.
/// Times are milliseconds from any monotonic clock.
/// </summary>
public sealed class RestoreGuard
{
    public const int Max = 5, WindowMs = 10_000, QuietMs = 10_000, CapMs = 600_000;
    readonly List<long> _times = new();
    bool _tripped;
    int _trips;
    long _resumeAt, _lastTrip, _pause;

    public bool Tripped => _tripped;
    public int Trips => _trips;
    /// <summary>How long the last stop pauses the repair, ms.</summary>
    public long PauseMs => _pause;

    public enum Gate { Allowed, JustStopped, Stopped }

    /// <param name="now">Milliseconds.</param>
    /// <param name="removal">True when an event reported the Retrace tab removed and the check found it gone; false for the once-a-second look or an
    /// event that only asks for a check (ItemInitialized, WSCURRENT).</param>
    public Gate Try(long now, bool removal = false)
    {
        if (_tripped)
        {
            if (now < _resumeAt)
            {
                if (removal) _resumeAt = Math.Max(_resumeAt, now + _pause);
                return Gate.Stopped;
            }
            _tripped = false;
            _times.Clear();
        }
        _times.RemoveAll(t => now - t > WindowMs);
        if (_times.Count >= Max)
        {
            if (_trips > 0 && now - _lastTrip > _pause + CapMs) _trips = 0;
            _trips++;
            _pause = Math.Min((long)QuietMs << Math.Min(_trips - 1, 20), CapMs);
            _tripped = true;
            _lastTrip = now;
            _resumeAt = now + _pause;
            return Gate.JustStopped;
        }
        if (_trips > 0 && now - _lastTrip > _pause + CapMs) _trips = 0;
        _times.Add(now);
        return Gate.Allowed;
    }

    public void Reset() { _tripped = false; _times.Clear(); _trips = 0; _pause = 0; }
}

/// <summary>Waits for the ribbon to be quiet before a repair, so one rebuild by AutoCAD is one repair (one guard count) however many changes it makes.</summary>
public static class RestoreSettle
{
    public const int SettleMs = 150;

    /// <summary>True when no change of the tab collection happened in the last <see cref="SettleMs"/>.</summary>
    public static bool Ready(long now, long lastChange) => now - lastChange >= SettleMs;
}

public static class RestoreLog
{
    /// <summary>The one log line of a stop (one per trip). A build that fails is worded differently from a tab that keeps being removed.</summary>
    public static string Stopped(int trips, long pauseMs, string? buildFailure)
    {
        var pause = $"automatic repair paused for {pauseMs / 1000} s (stop {trips}); type RETRACERIBBON";
        return buildFailure != null
            ? $"ribbon: the Retrace tab could not be built: {buildFailure}; {pause}"
            : $"ribbon: the Retrace tab keeps being removed ({RestoreGuard.Max} repairs in {RestoreGuard.WindowMs / 1000} s); {pause}";
    }
}

/// <summary>
/// "The tab may be missing": set by event handlers (they do no work), taken by the next idle tick. Keeps the first reason set since the last take.
/// Thread-safe (a handler may run on another thread than Idle).
/// </summary>
public sealed class RestoreFlag
{
    string? _reason;
    readonly object _gate = new();

    public void Set(string reason) { lock (_gate) _reason ??= reason; }

    public bool IsSet { get { lock (_gate) return _reason != null; } }

    public string? Take() { lock (_gate) { var r = _reason; _reason = null; return r; } }
}

/// <summary>Tells whether a message is new: a build that fails every second must put one line in the log, not one per second.</summary>
public sealed class DistinctOnce
{
    string? _last;

    /// <summary>True when <paramref name="text"/> differs from the last one given; null or empty clears the memory.</summary>
    public bool IsNew(string? text)
    {
        if (string.IsNullOrEmpty(text)) { _last = null; return false; }
        if (text == _last) return false;
        _last = text;
        return true;
    }
}
