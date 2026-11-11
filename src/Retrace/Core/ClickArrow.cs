namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

/// <summary>A rectangle in pixels (<see cref="Right"/> and <see cref="Bottom"/> exclusive, like a RECT).</summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>A click arrow: it starts at (<see cref="StartX"/>, <see cref="StartY"/>) and its head is at (<see cref="TipX"/>, <see cref="TipY"/>), the click point.</summary>
public readonly record struct ArrowLine(int StartX, int StartY, int TipX, int TipY);

/// <summary>Where the green click arrow goes: the geometry for the live overlay (screen pixels) and for step pictures (picture pixels).</summary>
public static class ClickArrow
{
    /// <summary>Length of the arrow, start to tip, in screen pixels.</summary>
    public const int Length = 80;
    /// <summary>How long a live arrow stays, in milliseconds (until the next arrow, if that comes first).</summary>
    public const int ShowMs = 1500;

    /// <summary>The monitor that holds the point, else the nearest one; null when there is none.</summary>
    public static PxRect? MonitorOf(int x, int y, IReadOnlyList<PxRect> monitors)
    {
        PxRect? best = null;
        long bestD = long.MaxValue;
        foreach (var m in monitors)
        {
            if (m.Width <= 0 || m.Height <= 0) continue;
            if (m.Contains(x, y)) return m;
            long dx = x < m.Left ? m.Left - x : x >= m.Right ? x - m.Right + 1 : 0;
            long dy = y < m.Top ? m.Top - y : y >= m.Bottom ? y - m.Bottom + 1 : 0;
            var d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    /// <summary>The arrow for a click at (<paramref name="x"/>, <paramref name="y"/>): the head on the point, the tail up and to the left at 45 degrees.
    /// Near the left edge of <paramref name="area"/> the tail goes to the right of the point instead, near the top edge below it, so the arrow stays in view; a tail that still
    /// does not fit (a tiny area) is cut to the area, and the head is kept inside it.</summary>
    public static ArrowLine For(int x, int y, PxRect area, int length = Length)
    {
        int tx = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - 1)), ty = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - 1));
        int d = (int)Math.Round(length / Math.Sqrt(2));
        int dx = tx - d < area.Left ? d : -d;
        int dy = ty - d < area.Top ? d : -d;
        int sx = Math.Clamp(tx + dx, area.Left, Math.Max(area.Left, area.Right - 1));
        int sy = Math.Clamp(ty + dy, area.Top, Math.Max(area.Top, area.Bottom - 1));
        return new ArrowLine(sx, sy, tx, ty);
    }

    /// <summary>The arrow for a screen point on a monitor list (the point's own monitor); null when there is no monitor.</summary>
    public static ArrowLine? ForScreen(int x, int y, IReadOnlyList<PxRect> monitors, int length = Length) =>
        MonitorOf(x, y, monitors) is { } m ? For(x, y, m, length) : null;

    /// <summary>The arrow for a step picture. The picture shows the screen rectangle (<paramref name="src"/>) scaled to <paramref name="imgW"/> x <paramref name="imgH"/>; the point
    /// is translated to the picture and clamped to it, and the arrow is as long as on the screen scaled by the picture's scale (at least 40 px, at most half the picture's smaller side).
    /// null when a size is not positive.</summary>
    public static ArrowLine? ForImage(int screenX, int screenY, PxRect src, int imgW, int imgH, int length = Length)
    {
        if (src.Width <= 0 || src.Height <= 0 || imgW <= 0 || imgH <= 0) return null;
        double kx = (double)imgW / src.Width, ky = (double)imgH / src.Height;
        int px = (int)Math.Round((screenX - src.Left) * kx), py = (int)Math.Round((screenY - src.Top) * ky);
        int len = (int)Math.Round(length * Math.Min(kx, ky));
        len = Math.Min(Math.Max(len, 40), Math.Max(10, Math.Min(imgW, imgH) / 2));
        return For(px, py, new PxRect(0, 0, imgW, imgH), len);
    }
}
