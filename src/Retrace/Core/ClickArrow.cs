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

    /// <summary>Like <see cref="ForImage"/>, but the tail goes to the corner (upper left, upper right, lower left, lower right of the point) where the picture is least busy along the arrow (the sum of
    /// the brightness steps of <paramref name="bgra"/> under the shaft): an arrow that comes in over the label above a tree item hides it, one from the free side does not. The upper left, the old
    /// direction, wins ties and gets a head start, so an arrow that was fine stays where it was. <paramref name="bgra"/> is the picture at <paramref name="imgW"/> x <paramref name="imgH"/>, 32-bit top-down.</summary>
    public static ArrowLine? ForImageQuiet(int screenX, int screenY, PxRect src, int imgW, int imgH, byte[] bgra, int length = Length)
    {
        var plain = ForImage(screenX, screenY, src, imgW, imgH, length);
        if (plain is not { } a0 || bgra == null || (long)imgW * imgH * 4 > bgra.Length) return plain;
        int tx = a0.TipX, ty = a0.TipY;
        int d = (int)Math.Round(Math.Sqrt((double)(a0.StartX - tx) * (a0.StartX - tx) + (double)(a0.StartY - ty) * (a0.StartY - ty)) / Math.Sqrt(2));
        if (d < 8) return plain;
        long best = long.MaxValue; ArrowLine? pick = plain;
        int order = 0;
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            int ex = tx + sx * d, ey = ty + sy * d;
            order++;
            if (ex < 0 || ey < 0 || ex >= imgW || ey >= imgH) continue;   // the tail must fit in the picture
            long score = Busy(bgra, imgW, imgH, tx, ty, ex, ey);
            if (order == 1) score = score * 3 / 4;   // the old direction wins a close call
            if (score < best) { best = score; pick = new ArrowLine(ex, ey, tx, ty); }
        }
        return pick;
    }

    /// <summary>The arrow for a clicked thing whose rectangle (<paramref name="box"/>, in picture pixels) is known: its tip ends one pixel inside a corner of the rectangle and the head lies outside it, so the
    /// text of the thing stays readable. Of the four corners (tail up-left, up-right, down-left, down-right of it) the one whose shaft crosses the least busy part of <paramref name="bgra"/> is taken (the upper left
    /// wins a close call); a tail that would leave the picture rules a corner out. The length is that of <see cref="ForImage"/> for the same scale (<paramref name="src"/> shown at <paramref name="imgW"/> x <paramref name="imgH"/>).
    /// null when a size is not positive.</summary>
    public static ArrowLine? ForBoxQuiet(PxRect box, PxRect src, int imgW, int imgH, byte[] bgra, int length = Length)
    {
        if (box.Width <= 0 || box.Height <= 0 || src.Width <= 0 || src.Height <= 0 || imgW <= 0 || imgH <= 0) return null;
        double k = Math.Min((double)imgW / src.Width, (double)imgH / src.Height);
        int len = (int)Math.Round(length * k);
        len = Math.Min(Math.Max(len, 40), Math.Max(10, Math.Min(imgW, imgH) / 2));
        int d = (int)Math.Round(len / Math.Sqrt(2));
        bool haveBits = bgra != null && (long)imgW * imgH * 4 <= bgra.Length;
        long best = long.MaxValue; ArrowLine? pick = null;
        int order = 0;
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            order++;
            int tx = sx < 0 ? box.Left + 1 : box.Right - 2, ty = sy < 0 ? box.Top + 1 : box.Bottom - 2;
            int ex = tx + sx * d, ey = ty + sy * d;
            if (ex < 0 || ey < 0 || ex >= imgW || ey >= imgH) continue;
            long score = haveBits ? Busy(bgra!, imgW, imgH, tx, ty, ex, ey) : 0;
            if (order == 1) score = score * 3 / 4;
            if (score < best) { best = score; pick = new ArrowLine(ex, ey, tx, ty); }
        }
        if (pick != null) return pick;
        // no tail fits (a tiny picture): the upper left corner, the tail cut to the picture
        return new ArrowLine(Math.Clamp(box.Left + 1 - d, 0, imgW - 1), Math.Clamp(box.Top + 1 - d, 0, imgH - 1), box.Left + 1, box.Top + 1);
    }

    static long Busy(byte[] px, int w, int h, int x0, int y0, int x1, int y1)
    {
        long sum = 0;
        const int Steps = 24;
        for (int i = 1; i <= Steps; i++)   // from the tip outwards; the first steps (on the item itself) count too: all four candidates share them
        {
            int x = x0 + (x1 - x0) * i / Steps, y = y0 + (y1 - y0) * i / Steps;
            int x2 = Math.Min(w - 1, x + 3), y2 = Math.Min(h - 1, y + 3);
            sum += Math.Abs(Gray(px, w, x, y) - Gray(px, w, x2, y)) + Math.Abs(Gray(px, w, x, y) - Gray(px, w, x, y2));
        }
        return sum;
    }

    static int Gray(byte[] px, int w, int x, int y) { int i = (y * w + x) * 4; return (px[i] + px[i + 1] * 2 + px[i + 2]) / 4; }

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

    /// <summary>The length of an arrow in a picture <paramref name="imgW"/> x <paramref name="imgH"/> showing <paramref name="src"/> (as <see cref="ForImage"/> scales it).</summary>
    public static int ImageLength(PxRect src, int imgW, int imgH, int length = Length)
    {
        double kx = (double)imgW / Math.Max(1, src.Width), ky = (double)imgH / Math.Max(1, src.Height);
        int len = (int)Math.Round(length * Math.Min(kx, ky));
        return Math.Min(Math.Max(len, 40), Math.Max(10, Math.Min(imgW, imgH) / 2));
    }

    /// <summary>One arrow of a picture: the rectangle it points at (picture pixels). <see cref="AtEdge"/>: the head ends just inside the rectangle's edge, so it does not cover what is in it (a tree label, a list item);
    /// otherwise on the middle of the rectangle (a button). <see cref="Own"/>: the entries of the avoid list that belong to this target (its own box and number tag), which its arrow may run over.</summary>
    public readonly record struct ArrowTarget(PxRect Rect, bool AtEdge = false, IReadOnlyList<PxRect>? Own = null);

    static readonly (int X, int Y)[] Directions = [(-1, 1), (1, -1), (1, 1), (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1)];   // where the tail goes from the head, after the usual up-left place

    /// <summary>The arrows of one picture, one per target, placed so that they do not run over each other or over the boxes of the other targets. Each arrow tries the usual place first (<see cref="For"/>: the head on the
    /// point, the tail up and to the left) and takes it when nothing is in the way; else the direction with the least in the way. A picture with one arrow always gets the usual place. <paramref name="avoid"/>: everything else on the picture that an arrow should not cover (the boxes
    /// and their number tags, picture pixels); a target's own rectangle is skipped for its own arrow. A direction whose tail would leave the picture is only taken when no other fits.</summary>
    public static List<ArrowLine> Place(IReadOnlyList<ArrowTarget> targets, IReadOnlyList<PxRect> avoid, int imgW, int imgH, int length)
    {
        var area = new PxRect(0, 0, imgW, imgH);
        var placed = new List<ArrowLine>();
        foreach (var t in targets)
        {
            int cx = (t.Rect.Left + t.Rect.Right) / 2, cy = (t.Rect.Top + t.Rect.Bottom) / 2;
            var candidates = new List<ArrowLine>();
            var usual = For(cx, cy, area, length);
            candidates.Add(t.AtEdge ? ToEdge(usual, t.Rect) : usual);
            foreach (var (dx, dy) in Directions)
            {
                double n = Math.Sqrt(dx * dx + dy * dy);
                int sx = cx + (int)Math.Round(dx / n * length), sy = cy + (int)Math.Round(dy / n * length);
                var line = new ArrowLine(sx, sy, cx, cy);
                candidates.Add(t.AtEdge ? ToEdge(line, t.Rect) : line);
            }
            ArrowLine best = candidates[0];
            if (targets.Count == 1) { placed.Add(best); continue; }   // one arrow in the picture: always the usual place, as before there were several (nothing to keep it off)
            long bestScore = long.MaxValue;
            for (int k = 0; k < candidates.Count; k++)
            {
                var c = candidates[k];
                long score = k;
                if (c.StartX < 0 || c.StartY < 0 || c.StartX >= imgW || c.StartY >= imgH) score += 100000;
                var mine = Samples(c).ToList();
                score += 1000L * mine.Count(p => avoid.Any(r => !r.Equals(t.Rect) && t.Own?.Contains(r) != true && r.Contains(p.X, p.Y)));
                foreach (var o in placed)
                {
                    var theirs = Samples(o).ToList();
                    score += 5000L * mine.Count(p => theirs.Any(q => Math.Abs(p.X - q.X) < 7 && Math.Abs(p.Y - q.Y) < 7));
                }
                if (score < bestScore) { bestScore = score; best = c; }
            }
            placed.Add(best);
        }
        return placed;
    }

    static IEnumerable<(int X, int Y)> Samples(ArrowLine a)
    {
        double dx = a.TipX - a.StartX, dy = a.TipY - a.StartY, len = Math.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)(len / 3));
        for (int i = 0; i <= steps; i++) yield return ((int)Math.Round(a.StartX + dx * i / steps), (int)Math.Round(a.StartY + dy * i / steps));
    }

    /// <summary>The arrow with its head moved from the middle of <paramref name="r"/> to just inside the edge the tail comes from (the length stays: the tail moves with it).</summary>
    static ArrowLine ToEdge(ArrowLine a, PxRect r)
    {
        double dx = a.StartX - a.TipX, dy = a.StartY - a.TipY, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return a;
        double ux = dx / len, uy = dy / len;
        double hw = Math.Max(1, r.Width / 2.0 - 2), hh = Math.Max(1, r.Height / 2.0 - 2);
        double e = Math.Min(ux == 0 ? double.MaxValue : hw / Math.Abs(ux), uy == 0 ? double.MaxValue : hh / Math.Abs(uy));   // from the middle along the tail's direction to the rectangle's edge
        double tx = a.TipX + ux * e, ty = a.TipY + uy * e;
        return new ArrowLine((int)Math.Round(tx + ux * len), (int)Math.Round(ty + uy * len), (int)Math.Round(tx), (int)Math.Round(ty));
    }
}
