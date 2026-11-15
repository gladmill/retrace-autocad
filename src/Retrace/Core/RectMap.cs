namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>Maps a point from one coordinate space to another by the same window's rectangle in both (the affine map that takes <c>from</c> onto <c>to</c>): exact on the monitor the window is on,
/// whatever that monitor's scale or position (negative coordinates, a scale below or above 1).</summary>
public static class RectMap
{
    public static (int X, int Y) Map(int x, int y, PxRect from, PxRect to) =>
        ((int)Math.Round(to.Left + (x - from.Left) * (double)to.Width / from.Width),
         (int)Math.Round(to.Top + (y - from.Top) * (double)to.Height / from.Height));
}
