using System.Globalization;
using System.Reflection;

namespace Retrace.AutoCad;

/// <summary>
/// WinForms controls, asked in process through reflection (Retrace does not reference WinForms; AutoCAD has it loaded). WinForms draws its
/// buttons, check boxes and radio buttons itself (Win32 style BS_OWNERDRAW), so their window style does not say which is which; and some
/// values only take when set through WinForms (a NumericUpDown puts its old value back over text set from outside). UI thread only.
/// </summary>
static class WinFormsControls
{
    static readonly Lazy<MethodInfo?> FromHandle = new(() =>
    {
        try { return Type.GetType("System.Windows.Forms.Control, System.Windows.Forms", false)?.GetMethod("FromHandle", new[] { typeof(IntPtr) }); }
        catch (Exception ex) { Log.Error("winforms lookup", ex); return null; }
    });

    static object? Control(IntPtr h)
    {
        try { return FromHandle.Value?.Invoke(null, new object[] { h }); }
        catch (Exception) { return null; }
    }

    /// <summary>"check" | "radio" | "button" for a WinForms CheckBox / RadioButton / other ButtonBase; null if <paramref name="h"/> is not a WinForms button.</summary>
    public static string? KindOf(IntPtr h)
    {
        for (var t = Control(h)?.GetType(); t != null; t = t.BaseType)
            switch (t.FullName)
            {
                case "System.Windows.Forms.CheckBox": return "check";
                case "System.Windows.Forms.RadioButton": return "radio";
                case "System.Windows.Forms.ButtonBase": return "button";
            }
        return null;
    }

    /// <summary>The Checked property of a WinForms CheckBox / RadioButton; false if there is none.</summary>
    public static bool IsChecked(IntPtr h) => Control(h) is { } c && c.GetType().GetProperty("Checked")?.GetValue(c) is true;

    static bool IsA(Type? t, string fullName)
    {
        for (; t != null; t = t.BaseType) if (t.FullName == fullName) return true;
        return false;
    }

    /// <summary>Sets a WinForms check box / radio button; false if <paramref name="h"/> is not one.</summary>
    public static bool TrySetChecked(IntPtr h, bool on)
    {
        if (KindOf(h) is not ("check" or "radio") || Control(h) is not { } c) return false;
        try
        {
            // clicked the way a person would (Click handlers run, AutoCheck toggles it), then set directly if that did not do it
            var prop = c.GetType().GetProperty("Checked");
            if (prop?.GetValue(c) is bool now && now != on)
                c.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(EventArgs) })?.Invoke(c, new object[] { EventArgs.Empty });
            if (prop?.GetValue(c) is bool after && after != on) prop.SetValue(c, on);
            return true;
        }
        catch (Exception ex) { Log.Error("winforms set checked", ex); return false; }
    }

    /// <summary>Sets what a WinForms edit shows: for the edit inside a NumericUpDown its Value, else its Text. false if <paramref name="h"/> is not a
    /// WinForms control or the value does not fit (not a number, out of range).</summary>
    public static bool TrySetText(IntPtr h, string text)
    {
        if (Control(h) is not { } c) return false;
        try
        {
            var parent = c.GetType().GetProperty("Parent")?.GetValue(c);
            if (parent != null && IsA(parent.GetType(), "System.Windows.Forms.NumericUpDown"))
            {
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var d)
                    && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return false;
                parent.GetType().GetProperty("Value")!.SetValue(parent, d);   // throws when out of range
                return true;
            }
            c.GetType().GetProperty("Text")?.SetValue(c, text);
            return true;
        }
        catch (Exception ex) { Log.Error("winforms set text", ex); return false; }
    }

    /// <summary>(Name, AccessibleName) of a WinForms control, or null if <paramref name="h"/> is not one. For the edit inside a NumericUpDown, the
    /// NumericUpDown's.</summary>
    public static (string Name, string Accessible)? NamesOf(IntPtr h)
    {
        if (Control(h) is not { } c) return null;
        try
        {
            var t = c.GetType();
            var parent = t.GetProperty("Parent")?.GetValue(c);
            if (parent != null && IsA(parent.GetType(), "System.Windows.Forms.UpDownBase")) { c = parent; t = c.GetType(); }
            return (t.GetProperty("Name")?.GetValue(c) as string ?? "", t.GetProperty("AccessibleName")?.GetValue(c) as string ?? "");
        }
        catch (Exception) { return null; }
    }

    /// <summary>Runs a WinForms button's Click as a click would (its protected OnClick): works while the button is hidden, where a posted click
    /// or PerformClick does nothing. false if <paramref name="h"/> is not a WinForms button.</summary>
    public static bool TryClick(IntPtr h)
    {
        if (KindOf(h) != "button" || Control(h) is not { } c) return false;
        try
        {
            c.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(EventArgs) })!.Invoke(c, new object[] { EventArgs.Empty });
            return true;
        }
        catch (Exception ex) { Log.Error("winforms click", ex.InnerException ?? ex); return false; }
    }
}
