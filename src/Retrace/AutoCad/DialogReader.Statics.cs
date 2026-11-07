namespace Retrace.AutoCad;

// The static texts of a window, read on request (a click, the input that closes a dialog, a native palette click, the replay's press and close): not in every poll of
// Scan. What is kept is decided by Core/DialogStatics.

static partial class DialogReader
{
    /// <summary>The static texts of <paramref name="window"/> as they are now: its visible Static controls and read-only edit boxes (never a password box), in window order
    /// (<see cref="DialogStatics"/> cuts and caps them). A pass of its own over the child windows, never the fields. Never throws.</summary>
    internal static List<DialogStatic> ReadStatics(IntPtr window, bool hiddenToo = false)
    {
        try
        {
            if (WpfDialogs.IsWindow(window)) return WpfPalette.ScanHost(window, hiddenToo).ReadParts().Statics;   // a WPF window has no child windows: its texts come from its visual tree
            var handles = new List<IntPtr>();
            EnumChildWindows(window, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
            return DialogStatics.Build(StaticTexts(window, handles, hiddenToo));
        }
        catch (Exception ex) { Log.Error("dialog statics", ex); return new List<DialogStatic>(); }
    }

    // read one at a time: DialogStatics.Build stops at its cap
    static IEnumerable<(int Id, string? Text)> StaticTexts(IntPtr dialog, List<IntPtr> handles, bool hiddenToo)
    {
        foreach (var h in handles)
        {
            if (hiddenToo ? ((uint)GetWindowLong(h, GWL_STYLE) & WS_VISIBLE) == 0 : !IsWindowVisible(h)) continue;
            var seg = Segment(ClassOf(h));
            if (seg == "static") { yield return (GetDlgCtrlID(h), Text(h)); continue; }
            if (seg != "edit" && !seg.Contains("richedit")) continue;
            var style = (uint)GetWindowLong(h, GWL_STYLE);
            if ((style & ES_READONLY) == 0 || (style & ES_PASSWORD) != 0) continue;
            var parent = GetParent(h);
            if (parent != IntPtr.Zero && parent != dialog && Segment(ClassOf(parent)).StartsWith("combobox")) continue;   // a combo's own edit is its value, not a text
            yield return (GetDlgCtrlID(h), Text(h));
        }
    }
}
