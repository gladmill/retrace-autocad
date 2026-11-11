using System.Windows;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>Creates the tab once the ribbon exists and again if AutoCAD rebuilds the ribbon (workspace switch). Checked on Idle, at most once a second.</summary>
public static class RibbonInstaller
{
    static bool _hooked;
    static long _lastCheck;

    public static void Install()
    {
        if (_hooked) return;
        _hooked = true;
        AcApp.Idle += OnIdle;
    }

    public static void Uninstall()
    {
        if (!_hooked) return;
        _hooked = false;
        AcApp.Idle -= OnIdle;
        try
        {
            var rc = ComponentManager.Ribbon;
            var tab = rc?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab != null) rc!.Tabs.Remove(tab);
        }
        catch (System.Exception) { }
    }

    static void OnIdle(object? s, EventArgs e)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastCheck < 1000) return;
            _lastCheck = now;
            var rc = ComponentManager.Ribbon;
            if (rc == null) return;
            if (rc.Tabs.Any(t => t.Id == RetraceTab.TabId)) { RefreshFromSettings(false); return; }   // settings.json changed elsewhere: follow it
            var tab = RetraceTab.Build();
            rc.Tabs.Add(tab);
            ShowCheckpointKey(tab);
            Place(rc, tab, RibbonTabPosition.ReadSettingFromDisk());
            RefreshFromSettings(true);
        }
        catch (System.Exception ex) { Log.Error("ribbon create", ex); }
    }

    /// <summary>Moves the Retrace tab to where settings.json says (key `ribbonTabPosition`). Called after Settings are saved. Never throws.</summary>
    public static void ApplyPosition(string? setting = null)
    {
        try
        {
            var rc = ComponentManager.Ribbon;
            var tab = rc?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (rc != null && tab != null) Place(rc, tab, setting ?? RibbonTabPosition.ReadSettingFromDisk());
        }
        catch (System.Exception ex) { Log.Error("ribbon move", ex); }
    }

    /// <summary>
    /// Shows the checkpoint shortcut on the Checkpoint button: a second line under the label and a sentence in the tooltip. Done here after the
    /// tab is built, so RetraceTab.Build keeps the plain literal shape the code-ribbon scanner reads. Called when the tab is made and when
    /// Settings are saved. Never throws.
    /// </summary>
    public static void ShowCheckpointKey(RibbonTab? tab = null, RetraceSettings? settings = null)
    {
        try
        {
            tab ??= ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab == null) return;
            var key = (settings ?? RetraceSettings.Load()).CheckpointChord(out _)?.Format();
            foreach (var panel in tab.Panels)
                foreach (var item in panel.Source.Items)
                {
                    if (item is not RibbonButton b || b.Id != "RETRACEMARK") continue;
                    const string marker = " Shortcut: ";
                    b.Text = key == null ? "Checkpoint" : "Checkpoint\n" + key;
                    var tip = (b.Description ?? "");
                    var at = tip.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0) tip = tip.Substring(0, at);
                    if (key != null) tip += marker + key + " (change it in Settings).";
                    b.Description = tip;
                    b.ToolTip = tip;
                }
        }
        catch (System.Exception ex) { Log.Error("ribbon checkpoint key", ex); }
    }

    static DateTime _settingsSeen = DateTime.MinValue;

    /// <summary>The promptStyle the Prompts toggle shows now (the default until settings.json has been read).</summary>
    public static string ShownPromptStyle { get; private set; } = PromptStyles.CommandLine;

    /// <summary>Makes the ribbon show what settings.json says: the Prompts toggle (pressed = window), the checkpoint shortcut on the Checkpoint button and the
    /// tab's place. With <paramref name="force"/> always, else only when the file was written since the last look (so it follows the Settings window,
    /// -RETRACESETTINGS and a hand edit). A file that cannot be read changes nothing and is not marked as seen, so the next look tries again. Never throws.</summary>
    public static void RefreshFromSettings(bool force)
    {
        try
        {
            var file = Paths.SettingsFile();
            var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (!force && written == _settingsSeen) return;
            if (!RetraceSettings.TryLoad(out var s)) return;
            ShowPromptStyle(s.PromptStyle);
            ShowCheckpointKey(null, s);
            ApplyPosition(s.RibbonTabPosition);
            _settingsSeen = written;
        }
        catch (System.Exception ex) { Log.Error("ribbon refresh", ex); }
    }

    /// <summary>Shows <paramref name="style"/> on the Prompts toggle: pressed = window, text and tooltip. Never throws.</summary>
    public static void ShowPromptStyle(string? style)
    {
        try
        {
            var tab = ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == RetraceTab.TabId);
            if (tab == null) return;
            ShownPromptStyle = RibbonPrompts.IsWindow(style) ? PromptStyles.Ui : PromptStyles.CommandLine;
            foreach (var panel in tab.Panels)
                foreach (var item in panel.Source.Items)
                {
                    if (item is not RibbonToggleButton b || b.Id != RetraceTab.PromptToggleId) continue;
                    b.IsChecked = RibbonPrompts.IsWindow(style);
                    b.Text = RibbonPrompts.ToggleText(style);
                    var tip = RibbonPrompts.ToggleTip(style);
                    b.ToolTip = tip;
                    b.Description = tip;
                }
        }
        catch (System.Exception ex) { Log.Error("ribbon prompt style", ex); }
    }

    /// <summary>The ribbon tabs the user can anchor to (visible, not contextual, not Retrace): (id, title). Empty if there is no ribbon.</summary>
    public static IReadOnlyList<(string Id, string Title)> AnchorTabs()
    {
        try
        {
            var rc = ComponentManager.Ribbon;
            if (rc == null) return Array.Empty<(string, string)>();
            return rc.Tabs.Where(t => t.Id != RetraceTab.TabId && t.IsVisible && !t.IsContextualTab && !string.IsNullOrWhiteSpace(t.Title))
                .Select(t => (t.Id ?? "", t.Title ?? "")).ToList();
        }
        catch (System.Exception) { return Array.Empty<(string, string)>(); }
    }

    static void Place(RibbonControl rc, RibbonTab tab, string? setting)
    {
        var others = rc.Tabs.Where(t => !ReferenceEquals(t, tab)).Select(t => (Id: t.Id ?? "", Title: t.Title ?? "")).ToList();
        var index = RibbonTabPosition.ChooseIndex(setting, others, RetraceTab.TabId);
        if (rc.Tabs.IndexOf(tab) == index) return;
        rc.Tabs.Remove(tab);
        rc.Tabs.Insert(Math.Min(index, rc.Tabs.Count), tab);
    }
}
