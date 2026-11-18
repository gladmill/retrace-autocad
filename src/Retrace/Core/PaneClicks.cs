namespace Retrace.Core;

// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).
// Clicks inside panes an add-in puts into AutoCAD (palette sets, modeless forms and windows, custom controls): the pure decisions (which window classes a click on is a value of a field and no step, which types are an add-in's own,
// what chain of classes a log line names, how often a log line may be written).

/// <summary>What a chain of window classes (the clicked window first, then its parents) tells about where a click was.</summary>
public enum PaneChain
{
    /// <summary>No WinForms or WPF host window in the chain and no palette set above it: AutoCAD's own windows, the drawing, a native dialog. Never logged.</summary>
    Native,
    /// <summary>Managed content under the palette set's frame (an AfxWnd page).</summary>
    InPaletteSet,
    /// <summary>Managed content in an AfxWnd window whose parents are not the palette set / control bar pair Retrace knows (another AutoCAD version builds them differently).</summary>
    InOddFrame,
    /// <summary>Managed content with no palette frame above it: a modeless form or window, a pop-up, a ribbon pop-up.</summary>
    ManagedTopLevel,
}

public static class PaneClicks
{
    /// <summary>Is the chain (class names, clicked window first) managed content, and which kind of place?</summary>
    public static PaneChain Classify(IReadOnlyList<string> classes)
    {
        bool managed = false, afx = false;
        foreach (var c in classes)
        {
            if (c.StartsWith("WindowsForms", StringComparison.Ordinal) || c.StartsWith("HwndWrapper", StringComparison.Ordinal)) managed = true;
            else if (c.StartsWith("AfxWnd", StringComparison.Ordinal)) afx = true;
        }
        if (!managed) return PaneChain.Native;
        if (!afx) return PaneChain.ManagedTopLevel;
        // the frame the palette set sits in: AfxWnd (page), AfxWnd (set), AfxControlBar
        int firstAfx = -1;
        for (int i = 0; i < classes.Count; i++) if (classes[i].StartsWith("AfxWnd", StringComparison.Ordinal)) { firstAfx = i; break; }
        bool framed = firstAfx >= 0 && firstAfx + 2 < classes.Count && classes[firstAfx + 1].StartsWith("AfxWnd", StringComparison.Ordinal) && classes[firstAfx + 2].StartsWith("AfxControlBar", StringComparison.Ordinal);
        return framed ? PaneChain.InPaletteSet : PaneChain.InOddFrame;
    }

    /// <summary>The chain as one line of a log: "A &lt; B &lt; C" (at most <paramref name="max"/> classes, then "...").</summary>
    public static string ChainText(IReadOnlyList<string> classes, int max = 10) =>
        string.Join(" < ", classes.Take(max)) + (classes.Count > max ? " < ..." : "");

    /// <summary>Is a type of this assembly an add-in's own, as opposed to the framework's, AutoCAD's (its program folder and the vertical product folders below it), the GAC's, a generated one (dynamic proxies) or Retrace's? By where the assembly
    /// was loaded from and by its exact (or dotted) name: no list of AutoCAD's own prefixes. <paramref name="location"/> is empty for an assembly loaded from bytes (treated as an add-in's unless its name says otherwise); <paramref name="programFolder"/> is
    /// the folder of acad.exe. Retrace's own assembly is "Retrace" exactly (the samples, "Retrace.Samples", are an add-in like any other).</summary>
    public static bool IsAddIn(string? assembly, string? location, bool dynamic, string? programFolder, string? company = null) => AddInDecision(assembly, location, dynamic, programFolder, company).AddIn;

    /// <summary>The decision of <see cref="IsAddIn"/> with the reason when it is "not an add-in" (for the one log line per assembly: the folder kind, never a full user path). Under AutoCAD's own program folder, an Autodesk bundle folder
    /// (ApplicationPlugins, Common Files\Autodesk Shared) or the GAC the location alone says nothing (an add-in may be installed in a Plugins subfolder, a third-party control library may live in the GAC): the file's company decides
    /// (<paramref name="company"/>, `FileVersionInfo.CompanyName`): Autodesk (or Microsoft in the GAC) is not an add-in, anything else is.</summary>
    public static (bool AddIn, string Why) AddInDecision(string? assembly, string? location, bool dynamic, string? programFolder, string? company)
    {
        if (dynamic) return (false, "a dynamic assembly");
        if (string.IsNullOrEmpty(assembly) || !NameIsAddIn(assembly!)) return (false, "a framework or Retrace assembly by name");
        if (string.IsNullOrEmpty(location)) return (true, "");
        var l = location!.Replace('/', '\\');
        if (l.Contains("\\dotnet\\shared\\", StringComparison.OrdinalIgnoreCase) || l.Contains("\\Microsoft.NET\\assembly\\GAC_", StringComparison.OrdinalIgnoreCase) && company is { } mc && mc.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            || l.Contains("\\Windows\\assembly\\", StringComparison.OrdinalIgnoreCase) && company is { } mc2 && mc2.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return (false, "the runtime");
        string? kind = null;
        if (!string.IsNullOrEmpty(programFolder))
        {
            var f = programFolder!.Replace('/', '\\').TrimEnd('\\') + "\\";
            if (l.StartsWith(f, StringComparison.OrdinalIgnoreCase)) kind = "the AutoCAD folder (" + l[f.Length..].Split('\\')[0] + ")";
        }
        if (kind == null && (l.Contains("\\Autodesk\\ApplicationPlugins\\", StringComparison.OrdinalIgnoreCase) || l.Contains("\\Autodesk Shared\\", StringComparison.OrdinalIgnoreCase))) kind = "an Autodesk bundle folder";
        if (kind == null && (l.Contains("\\GAC_", StringComparison.OrdinalIgnoreCase) || l.Contains("\\Microsoft.NET\\assembly\\", StringComparison.OrdinalIgnoreCase) || l.Contains("\\Windows\\assembly\\", StringComparison.OrdinalIgnoreCase))) kind = "the GAC";
        if (kind == null) return (true, "");
        // the company decides: only Autodesk's own (and, in the GAC, Microsoft's) files are not an add-in's
        bool theirs = !string.IsNullOrEmpty(company) && (company!.Contains("Autodesk", StringComparison.OrdinalIgnoreCase) || (kind == "the GAC" && company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrEmpty(company)) theirs = true;   // nothing known about the file: not taken for an add-in's
        return theirs ? (false, kind + ", company " + (string.IsNullOrEmpty(company) ? "unknown" : company)) : (true, "");
    }

    static readonly string[] FrameworkNames = { "mscorlib", "netstandard", "WindowsBase", "System", "Microsoft", "PresentationCore", "PresentationFramework", "PresentationUI", "ReachFramework", "UIAutomationClient", "UIAutomationTypes", "UIAutomationProvider", "WindowsFormsIntegration" };

    /// <summary>The name alone: not a framework assembly (exact or dotted: "System" and "System.Windows.Forms", never "SystemTools"), not Retrace's or AcadStatus's own.</summary>
    public static bool NameIsAddIn(string assembly)
    {
        foreach (var n in FrameworkNames) if (assembly.Equals(n, StringComparison.OrdinalIgnoreCase) || assembly.StartsWith(n + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return !assembly.Equals("Retrace", StringComparison.OrdinalIgnoreCase) && !assembly.Equals("AcadStatus", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is a top-level form or window an add-in's pane window: its own type is an add-in's, or it is the framework's plain `Form` / `Window` with content of an add-in's own types (<paramref name="contentAddIn"/>). A plain window with
    /// no add-in content (Retrace's script viewer, a sign-in or help window of AutoCAD) is not.</summary>
    public static bool IsPaneWindow(bool typeAddIn, bool plain, bool contentAddIn) => typeAddIn || (plain && contentAddIn);

    /// <summary>Is a click on a WinForms control that is no field and no push button worth a step: the control has a click handler, or it is a type of the add-in's own with no child controls (custom drawn: it paints itself and works from
    /// mouse messages, so it may have no handler Retrace can see). A container's blank area, a framework control without a handler (a plain Panel, a Label) are not. <paramref name="topOfPage"/>: the control a palette page IS (a leaf is the pane
    /// itself; one with children has a blank area that is no control).</summary>
    public static bool WinFormsClickable(bool hasClickHandler, bool addInType, bool topOfPage = false, int childControls = 0) =>
        topOfPage ? childControls == 0 && (hasClickHandler || addInType) : hasClickHandler || (addInType && childControls == 0);

    /// <summary>A click on WPF content is a step only for the innermost element under it when that is a custom drawn element of the add-in (its own type, no visual children). A label, a margin, blank space, a tab page's content, a
    /// framework control or a parent view of the add-in is not; AutoCAD's own WPF palettes therefore write what they always did. The element must also handle the mouse itself (its type, or a base type of the add-in, declares a mouse override such
    /// as OnMouseLeftButtonDown, or an instance handler is attached): a plain label or icon subclass is no control. Never inside a framework input control (a button, list or tree item, grid cell, menu item, text box, slider, scroll bar).</summary>
    public static bool WpfLeafClick(bool innermostIsAddIn, int visualChildren, bool declaresMouseHandling, bool insideInputControl) =>
        innermostIsAddIn && visualChildren == 0 && declaresMouseHandling && !insideInputControl;

    /// <summary>How well a live window answers the recorded title and type: 0 = exact title and type, 1 = exact title, 2 = loose title (<see cref="SameTitle"/>) and type, 3 = loose title; -1 = not the window.</summary>
    public static int TitleRank(string? recorded, string? live, bool typeMatches)
    {
        if (!SameTitle(recorded, live)) return -1;
        bool exact = recorded == live;
        return exact ? (typeMatches ? 0 : 1) : (typeMatches ? 2 : 3);
    }

    /// <summary>Do two window titles name the same window: equal, or equal before a document or counter suffix (" - [Drawing1.dwg]", " - Drawing1")?</summary>
    public static bool SameTitle(string? recorded, string? live)
    {
        if (string.IsNullOrWhiteSpace(recorded) || live == null) return false;
        if (recorded == live) return true;
        static string Head(string t) { int i = t.IndexOf(" - ", StringComparison.Ordinal); return (i > 0 ? t[..i] : t).Trim(); }
        return Head(recorded!).Length > 0 && Head(recorded!) == Head(live);
    }

    /// <summary>Window classes whose clicks are the values of the field they edit (or no action at all): a click on one is no step of its own and needs no line in the log.</summary>
    public static bool KnownQuiet(string seg) =>
        seg.StartsWith("msctls_", StringComparison.Ordinal) || seg.StartsWith("richedit", StringComparison.Ordinal) || seg.StartsWith("sys", StringComparison.Ordinal) || seg is "directuihwnd" or "shelldll_defview" or "mdiclient" or "rebarwindow32" or "tooltips_class32" or "scintilla"
        || seg is "edit" or "combobox" or "comboboxex32" or "combolbox" or "listbox" or "button" or "systabcontrol32" or "scrollbar" or "msctls_updown32" or "msctls_trackbar32" or "msctls_progress32"
            or "sysdatetimepick32" or "sysmonthcal32" or "sysipaddress32" or "msctls_hotkey32" or "richedit20w" or "richedit50w" or "systreeview32" or "syslistview32" or "sysheader32" or "msctls_statusbar32" or "tooltips_class32";

    /// <summary>A window class (segment) that no kind of click is recorded for but that is no standard control either (a third-party grid, a custom window class an add-in registered): a click on it is a `point` click, found again by its class and
    /// place among the controls of that class. Not the WinForms plain `window` / `static` (decided by the control's type), not a WPF host, not AutoCAD's own frame classes.</summary>
    public static bool UnknownClassIsPoint(string seg) =>
        seg.Length > 0 && !KnownQuiet(seg) && seg is not ("static" or "window") && !seg.StartsWith("hwndwrapper", StringComparison.Ordinal) && !seg.StartsWith("afx", StringComparison.Ordinal)
        && !seg.StartsWith("#32", StringComparison.Ordinal) && !IsBrowserOrHost(seg);

    /// <summary>Window classes of an embedded browser, an ActiveX or shell host: their content is not controls Retrace can find again.</summary>
    public static bool IsBrowserOrHost(string seg) =>
        seg.StartsWith("chrome_", StringComparison.Ordinal) || seg.StartsWith("intermediate d3d", StringComparison.Ordinal) || seg.StartsWith("internet explorer_server", StringComparison.Ordinal)
        || seg.StartsWith("shell ", StringComparison.Ordinal) || seg.StartsWith("shell_", StringComparison.Ordinal) || seg.StartsWith("atl:", StringComparison.Ordinal) || seg.StartsWith("cef", StringComparison.Ordinal)
        || seg.StartsWith("webview", StringComparison.Ordinal) || seg.StartsWith("mshtml", StringComparison.Ordinal);

    /// <summary>The kind a click on WPF content that is no push button is written under: the class segment "wpf", and the element's type name as its name.</summary>
    public const string WpfClass = "wpf";

    /// <summary>Decides how often a "click not recorded" line may be written: a distinct reason once, at most <see cref="MaxLines"/> in a recording, at least <see cref="MinGapMs"/> apart; the lines left out are counted and named on the next one.</summary>
    public sealed class LogGate
    {
        public const int MaxLines = 40;
        public const long MinGapMs = 400;
        readonly HashSet<string> _seen = new();
        long _lastMs = long.MinValue / 2;
        int _lines, _skipped;

        /// <summary>A new recording starts.</summary>
        public void Reset() { _seen.Clear(); _lastMs = long.MinValue / 2; _lines = 0; _skipped = 0; }

        /// <summary>Lines left out since the last one written (for the summary line when the recording stops).</summary>
        public int Skipped => _skipped;

        /// <summary>May a line for <paramref name="key"/> be written at <paramref name="nowMs"/> (a monotonic clock)? <paramref name="skipped"/>: how many lines were left out since the last one written.</summary>
        public bool Allow(string key, long nowMs, out int skipped)
        {
            skipped = 0;
            if (_seen.Contains(key)) return false;
            if (_lines >= MaxLines || nowMs - _lastMs < MinGapMs) { _skipped++; return false; }
            _seen.Add(key); _lines++; _lastMs = nowMs; skipped = _skipped; _skipped = 0;
            return true;
        }
    }
}
