namespace Retrace.Core;

// NO Win32, WPF or AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Decides whether a WPF <c>Click</c> is a person's press: the input that causes a click comes first, the Click follows. A mouse press is remembered with the button it went down on (an element token, compared by
/// reference) and counts for a Click of that same button however long the mouse was held; a key counts while it is down and for a short time after it came up (Space acts at the key-up, an access key or Enter at the key-down).
/// A Click with no such input, or on another button, is from code. Nothing here knows the clock: times are the caller's milliseconds, and "no input yet" is explicit, never a very old time.
/// </summary>
public sealed class WpfClickCorrelator
{
    /// <summary>A key counts for a Click this long after its last event (the key-up of a Space press, a handler that raises the Click a moment after the key).</summary>
    public const long KeyGraceMs = 500;
    /// <summary>A mouse press counts this long after its button came up (the Click is raised during that same message).</summary>
    public const long MouseUpGraceMs = 500;

    object? _mouseButton;
    bool _mouseDown;
    long? _mouseUpMs, _keyMs;
    bool _keyHeld;

    /// <summary>The left mouse button went down on <paramref name="button"/> (null: not on a push button).</summary>
    public void MouseDown(object? button) { _mouseButton = button; _mouseDown = true; _mouseUpMs = null; _keyHeld = false; }   // a mouse press ends a key that was never seen coming up

    public void MouseUp(long nowMs) { _mouseDown = false; _mouseUpMs = nowMs; }

    public void KeyDown(long nowMs) { _keyHeld = true; _keyMs = nowMs; }

    public void KeyUp(long nowMs) { _keyHeld = false; _keyMs = nowMs; }

    /// <summary>Is a Click of <paramref name="button"/> at <paramref name="nowMs"/> a person's press? A mouse press is used up by it.</summary>
    public bool IsPress(object button, long nowMs)
    {
        if (_keyHeld || (_keyMs is { } k && nowMs - k <= KeyGraceMs)) return true;
        if (_mouseButton != null && ReferenceEquals(_mouseButton, button) && (_mouseDown || (_mouseUpMs is { } u && nowMs - u <= MouseUpGraceMs)))
        {
            if (!_mouseDown) { _mouseButton = null; _mouseUpMs = null; }
            return true;
        }
        return false;
    }
}
