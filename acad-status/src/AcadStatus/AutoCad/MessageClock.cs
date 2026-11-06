using System.Runtime.InteropServices;

namespace AcadStatus.AutoCad;

/// <summary>The identity of the Windows message the UI thread is handling now: its time stamp. Two events raised while handling one message
/// (the window losing focus, and the pane's mouse-down, both from one click) share it.</summary>
static class MessageClock
{
    [DllImport("user32.dll")] static extern int GetMessageTime();

    public static long Id() => GetMessageTime();
}
