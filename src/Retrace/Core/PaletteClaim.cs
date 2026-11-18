namespace Retrace.Core;

// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).

/// <summary>Which command a palette click claims as the one it started (`origin` "palette"): the first command that starts within 30 s after the click, with at most one more input counted in between (the click's own button-down is the
/// one counted at the click; a typed key or another click makes a command the person's). A push button, a tree, a custom control or a menu choice can run code that sends a command later (an awaited call), so the window is long for all of them.
/// Real typing never claims: every key down except Shift, Ctrl and Alt advances the input count. A command sent by code (COM SendCommand, SendStringToExecute) has no key down and is claimed.</summary>
public static class PaletteClaim
{
    public const long ClickWindowMs = 30000;

    /// <param name="consumed">an earlier command already took this click</param>
    /// <param name="inputTick">the count of mouse and key inputs now, <paramref name="clickTick"/> its value when the click was recorded</param>
    public static bool Claims(bool consumed, long nowMs, long clickMs, long inputTick, long clickTick) =>
        !consumed && nowMs - clickMs <= ClickWindowMs && inputTick <= clickTick + 1;
}
