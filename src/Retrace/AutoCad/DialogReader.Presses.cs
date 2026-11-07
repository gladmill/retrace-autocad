using System.Runtime.InteropServices;
using Retrace.Core;

namespace Retrace.AutoCad;

// What a press in a dialog goes to when it is not a click of the mouse: the default button (Enter) and the button a mnemonic names. UI thread only.
// Whether Enter or a letter is the control's own is not decided from its class: the control says so (DialogCode, WM_GETDLGCODE).

static partial class DialogReader
{
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dlg, int id);

    const uint DM_GETDEFID = 0x0400;   // WM_USER
    const long DC_HASDEFID = 0x534B;

    const uint WM_GETDLGCODE = 0x0087;

    /// <summary>What <paramref name="control"/> wants of <paramref name="msg"/>, as the dialog manager asks (WM_GETDLGCODE with the message): DLGC_WANTALLKEYS for a key it handles itself (Enter in a
    /// multi-line edit that takes Return, in a combo box with its list open, in a grid cell being edited), DLGC_WANTCHARS for a character it types. A WinForms control answers from
    /// IsInputKey / IsInputChar. 0 when it does not answer. The control is a window of this thread: UI thread only.</summary>
    internal static int DialogCode(IntPtr control, Native.Msg msg)
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Msg>());
        try
        {
            Marshal.StructureToPtr(msg, p, false);
            return SendNum(control, WM_GETDLGCODE, msg.wParam, p, SMTO_ABORTIFHUNG, 200, out var r) == IntPtr.Zero ? 0 : (int)(long)r;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>The push button Enter presses in <paramref name="dialog"/> when no push button has the focus: a WinForms form's AcceptButton, else the one a standard dialog says is
    /// its default (DM_GETDEFID), else a visible BS_DEFPUSHBUTTON. Zero when there is none. A disabled one is returned too (Enter then does nothing).</summary>
    internal static IntPtr DefaultButtonOf(IntPtr dialog)
    {
        try
        {
            if (WinFormsControls.AcceptButtonOf(dialog) is var accept && accept != IntPtr.Zero) return accept;
            SendNum(dialog, DM_GETDEFID, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var r);
            var v = r.ToInt64();
            if (((v >> 16) & 0xFFFF) == DC_HASDEFID && GetDlgItem(dialog, (int)(v & 0xFFFF)) is var def && def != IntPtr.Zero) return def;
            var found = IntPtr.Zero;
            EnumChildWindows(dialog, (h, _) =>
            {
                if (Segment(ClassOf(h)) == "button" && IsWindowVisible(h) && (GetWindowLong(h, GWL_STYLE) & 0xF) == 1) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        catch (Exception ex) { Log.Error("default button", ex); return IntPtr.Zero; }
    }

    /// <summary>The enabled push button the mnemonic <paramref name="letter"/> presses in <paramref name="dialog"/>, or zero: only when it is the one control (button, check box, radio,
    /// group, label) that has that letter underlined (<see cref="DialogClickRules.UniqueMnemonicButton"/>).</summary>
    internal static IntPtr MnemonicButton(IntPtr dialog, char letter)
    {
        try
        {
            var handles = new List<IntPtr>();
            var controls = new List<(bool PushButton, bool Enabled, string? Text)>();
            EnumChildWindows(dialog, (h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var seg = Segment(ClassOf(h));
                if (seg is not ("button" or "static")) return true;
                var style = (uint)GetWindowLong(h, GWL_STYLE);
                var push = seg == "button" && (style & 0xF) is 0 or 1 or 0xB && WinFormsControls.KindOf(h) is null or "button";
                var text = Text(h);
                if (text.IndexOf('&') < 0) return true;
                handles.Add(h); controls.Add((push, (style & WS_DISABLED) == 0, text));
                return true;
            }, IntPtr.Zero);
            return DialogClickRules.UniqueMnemonicButton(letter, controls) is { } i ? handles[i] : IntPtr.Zero;
        }
        catch (Exception ex) { Log.Error("mnemonic button", ex); return IntPtr.Zero; }
    }
}
