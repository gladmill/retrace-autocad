using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AcadStatus.Ui;

/// <summary>Puts plain text on the clipboard with the Win32 calls directly, in one try. <c>Clipboard.SetDataObject</c> failed in AutoCAD 2026 on
/// the test VM ("Requested Clipboard operation did not succeed") while the same call worked in another process, and it sleeps between its
/// retries on the UI thread. Another program that holds the clipboard open for a moment is not waited for: the window says
/// <see cref="Busy"/> and the person clicks again.</summary>
static class ClipboardText
{
    public const string Busy = "clipboard busy, try again";

    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>Null when the text is on the clipboard, else what went wrong (<see cref="Busy"/> when it could not be opened).</summary>
    public static string? Set(string text, IntPtr owner)
    {
        if (!OpenClipboard(owner)) return Busy;
        try
        {
            if (!EmptyClipboard()) return "EmptyClipboard failed (error " + Marshal.GetLastWin32Error() + ")";
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (mem == IntPtr.Zero) return "GlobalAlloc failed";
            var ptr = GlobalLock(mem);
            if (ptr == IntPtr.Zero) { GlobalFree(mem); return "GlobalLock failed"; }
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(mem);
            if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
            {
                var e = Marshal.GetLastWin32Error();
                GlobalFree(mem);
                return "SetClipboardData failed (error " + e + ")";
            }
            return null;   // the clipboard owns the memory now
        }
        finally { CloseClipboard(); }
    }
}
