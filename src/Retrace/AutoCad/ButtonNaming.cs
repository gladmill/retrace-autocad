using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// The name of a palette push button (<see cref="ButtonNames"/> has the order and the matching rules). Nothing here runs on every scan: a button's fallback name (tool tip, accessible name, control name)
/// is read only when a click is being recorded or when a recording that has `buttonFrom` is being matched, and a scan keeps what it read (<see cref="Cache"/>). A naming failure is logged once per recording or replay
/// (<see cref="NewSession"/>), not once per button and scan. Win32 / WinForms: tool tip (a WinForms <c>ToolTip</c> component of the form, else a tool tip control of the thread), accessible name (WinForms
/// <c>AccessibleName</c>, else MSAA), the control's <c>Name</c>. WPF: the tool tip, the automation peer's name, the <c>x:Name</c>. UI thread only.
/// </summary>
static class ButtonNaming
{
    static bool _logged;

    /// <summary>A recording or a replay starts: a naming failure is logged again.</summary>
    public static void NewSession() => _logged = false;

    static void Failed(string what, Exception ex)
    {
        if (_logged) return;
        _logged = true;
        Log.Error("button name (" + what + ")", ex.InnerException ?? ex);
    }

    /// <summary>What a scan has read of the fallback names, by window or element and source: asked once per button for a scan. <c>null</c> in the table means "no name from that source".</summary>
    internal sealed class Cache
    {
        readonly Dictionary<(object, string), string?> _names = new();
        public string? Get(object key, string from, Func<string?> read)
        {
            if (_names.TryGetValue((key, from), out var v)) return v;
            string? r;
            try { r = ButtonNames.Clean(read()) is { Length: > 0 } c ? c : null; } catch (Exception ex) { Failed(from, ex); r = null; }
            _names[(key, from)] = r;
            return r;
        }
    }

    /// <summary>What was recorded for a click on a palette button: the name, where it came from (null for the button's own text, the legacy name), which of the buttons with that name it is, and which of the buttons
    /// without text of their own (`legacyIndex`, written only with a fallback name).</summary>
    internal sealed record Recorded(string Name, string? From, int Index, int LegacyIndex, int NoTextCount = 0);

    // ---- Win32 / WinForms ----------------------------------------------------------------------------------------------------

    /// <summary>One fallback source of the Win32 button <paramref name="h"/>; null when the source has no name. <paramref name="from"/>: <see cref="ButtonNames.FromTooltip"/>, <see cref="ButtonNames.FromAccessible"/> or <see cref="ButtonNames.FromName"/>.</summary>
    internal static string? Win32Source(IntPtr h, string from) => from switch
    {
        ButtonNames.FromTooltip => WinFormsToolTip(h) ?? NativePalette.ToolTipOf(h),
        ButtonNames.FromAccessible => WinFormsControls.NamesOf(h) is { Accessible.Length: > 0 } n ? n.Accessible : Msaa(h),
        ButtonNames.FromName => WinFormsControls.NamesOf(h)?.Name,
        _ => null,
    };

    /// <summary>The name of a Win32 button: its text, else the first fallback source that has a name; ("(button)", "") when none does. For a click that is not in a scan (a native palette button). Uncached.</summary>
    internal static (string Name, string From) Win32Pick(IntPtr h, string ownText) =>
        ButtonNames.Pick(() => ownText, () => Win32Source(h, ButtonNames.FromTooltip), () => Win32Source(h, ButtonNames.FromAccessible), null, () => Win32Source(h, ButtonNames.FromName));

    static string? WinFormsToolTip(IntPtr h)
    {
        try
        {
            var c = WinFormsControls.Control(h);
            if (c == null) return null;
            for (object? a = c; a != null; a = a.GetType().GetProperty("Parent")?.GetValue(a))
                foreach (var tt in ToolTipsOf(a))
                {
                    var get = tt.GetType().GetMethod("GetToolTip", BindingFlags.Instance | BindingFlags.Public);
                    if (get?.Invoke(tt, new[] { c }) is string s && s.Trim().Length > 0) return s;
                }
        }
        catch (Exception ex) { Failed("tooltip", ex); }
        return null;
    }

    static IEnumerable<object> ToolTipsOf(object control)
    {
        var found = new List<object>();
        for (var t = control.GetType(); t != null && t.FullName != "System.Windows.Forms.Control" && t.FullName != "System.Object"; t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object? v;
                try { v = f.GetValue(control); } catch (Exception) { continue; }
                if (v == null) continue;
                if (IsToolTip(v)) found.Add(v);
                else if (f.Name == "components" && v is System.ComponentModel.IContainer box)
                    foreach (System.ComponentModel.IComponent comp in box.Components) if (IsToolTip(comp)) found.Add(comp);
            }
        return found;
    }

    static bool IsToolTip(object o) => WinFormsControls.IsA(o.GetType(), "System.Windows.Forms.ToolTip");

    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IAccessible
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object get_accParent();
        int get_accChildCount();
        [return: MarshalAs(UnmanagedType.IDispatch)] object get_accChild([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)] string get_accName([In, MarshalAs(UnmanagedType.Struct)] object varChild);
    }

    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? acc);

    static string? Msaa(IntPtr h)
    {
        try
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (AccessibleObjectFromWindow(h, 0xFFFFFFFC, ref iid, out var acc) != 0 || acc == null) return null;   // OBJID_CLIENT
            try { return acc.get_accName(0); }
            finally { Marshal.ReleaseComObject(acc); }
        }
        catch (Exception) { return null; }
    }

    // ---- WPF -----------------------------------------------------------------------------------------------------------------

    /// <summary>Does the WPF button have a name of its own: an explicit <c>AutomationProperties.Name</c>, or text in its content (a string, an AccessText, a TextBlock, a Label, the first text in a panel)? An image, a shape or a
    /// control with no text has none, and is named by a fallback source for a new recording.</summary>
    internal static bool WpfHasOwnText(ButtonBase b)
    {
        try
        {
            if (AutomationProperties.GetName(b) is { Length: > 0 } n && DialogFieldsCore.CleanLabel(n).Length > 0) return true;
            return ContentText(b.Content, 0).Trim().Length > 0;
        }
        catch (Exception ex) { Failed("wpf text", ex); return false; }
    }

    /// <summary>One fallback source of a WPF button (null when it has no name): <see cref="ButtonNames.FromTooltip"/>, <see cref="ButtonNames.FromAutomation"/> (the name the automation peer reports) or <see cref="ButtonNames.FromName"/>.</summary>
    internal static string? WpfSource(ButtonBase b, string from)
    {
        try
        {
            return from switch
            {
                ButtonNames.FromTooltip => ToolTipText(b),
                ButtonNames.FromAutomation => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(b)?.GetName(),
                ButtonNames.FromName => b.Name,
                _ => null,
            };
        }
        catch (Exception ex) { Failed("wpf " + from, ex); return null; }
    }

    static string? ToolTipText(FrameworkElement e) => e.ToolTip switch
    {
        null => null,
        string s => s,
        ToolTip { Content: { } o } => ContentText(o, 0),
        var o => ContentText(o, 0),
    };

    /// <summary>The text in a button's (or tool tip's) content: a string, or the first TextBlock, AccessText or Label text found anywhere inside it (a Border, a Viewbox, a panel, a content control, a template-less
    /// element's logical children); "" for an image, a shape or an object that only has a type name. Only used for new recordings: the legacy name never looks inside.</summary>
    static string ContentText(object? c, int depth)
    {
        if (depth > 8) return "";
        switch (c)
        {
            case null: return "";
            case string s: return s;
            case AccessText at: return at.Text;
            case TextBlock tb: return tb.Text;
            case ContentControl cc: return ContentText(cc.Content, depth + 1);
            case Decorator d: return ContentText(d.Child, depth + 1);
            case Panel p:
                foreach (UIElement child in p.Children)
                    if (ContentText(child, depth + 1) is { Length: > 0 } t) return t;
                return "";
            case DependencyObject dep and FrameworkElement:
                foreach (var child in LogicalTreeHelper.GetChildren(dep))
                    if (ContentText(child, depth + 1) is { Length: > 0 } t) return t;
                return "";
            default:
                var str = c.ToString() ?? "";
                return str == c.GetType().FullName ? "" : str;
        }
    }
}
