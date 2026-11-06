using System;

namespace AcadStatus.Core;

/// <summary>Where a window goes: above the status bar, centred on the click, inside the working area of the screen it is on.</summary>
public static class Placement
{
    /// <summary>The top left corner of a window of <paramref name="width"/> x <paramref name="height"/> whose bottom edge sits 4 px above
    /// <paramref name="barTop"/>, centred on <paramref name="clickX"/>, kept inside the area (left, top, right, bottom). The area is the
    /// working area of the screen the click is on, whose top may be above 0 (a monitor above the primary) or below it.</summary>
    public static (int X, int Y) Above(int clickX, int barTop, int width, int height, int areaLeft, int areaTop, int areaRight, int areaBottom)
    {
        int x = Math.Min(clickX - width / 2, areaRight - width);
        x = Math.Max(areaLeft, x);
        int y = barTop - 4 - height;
        if (y + height > areaBottom) y = areaBottom - height;
        y = Math.Max(areaTop, y);
        return (x, y);
    }
}

/// <summary>Tells the click that made the details window lose focus from a new click on the pane. AutoCAD may or may not deliver the
/// pane's mouse-down for the click that deactivated the window; if it does, it comes from the same Windows message, which carries the
/// same message time. So the guard compares message identity, not elapsed time.</summary>
public sealed class ClickGuard
{
    long? _deactivatedBy;

    /// <summary>The window lost focus while the message <paramref name="messageId"/> was being handled.</summary>
    public void Deactivated(long messageId) => _deactivatedBy = messageId;

    /// <summary>A pane click arrived while handling <paramref name="messageId"/>: false when it is the click that just closed the window.</summary>
    public bool IsNewClick(long messageId)
    {
        var same = _deactivatedBy == messageId;
        _deactivatedBy = null;
        return !same;
    }

    /// <summary>The window closed some other way (Esc, the close button): a later click is a new one.</summary>
    public void Reset() => _deactivatedBy = null;
}
