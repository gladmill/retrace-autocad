namespace Retrace.Core;

// NO AutoCAD, WPF or System.Drawing references in this file (linked into the engine test project).

/// <summary>A numbered box on a picture, in pixels of that picture (<paramref name="Left"/>, <paramref name="Top"/> = top-left corner).</summary>
public readonly record struct AnnotationBox(int Left, int Top, int Width, int Height, int Number);

/// <summary>Green numbered boxes drawn on a picture (the Tdoc training document: "type a value here"). Works on raw BGRA pixels, so it needs no imaging library.</summary>
public static class Annotate
{
    /// <summary>ARGB of the frame and the tag.</summary>
    public const uint Green = 0xFF22B14C;

    const int Frame = 3;      // frame thickness in px
    const int Pad = 3;        // tag padding in px
    const int GlyphW = 3, GlyphH = 5;

    // 3x5 digits 0..9, one string per row, '#' = on.
    static readonly string[][] Digits =
    [
        ["###", "# #", "# #", "# #", "###"],
        [" # ", "## ", " # ", " # ", "###"],
        ["###", "  #", "###", "#  ", "###"],
        ["###", "  #", "###", "  #", "###"],
        ["# #", "# #", "###", "  #", "  #"],
        ["###", "#  ", "###", "  #", "###"],
        ["###", "#  ", "###", "# #", "###"],
        ["###", "  #", "  #", "  #", "  #"],
        ["###", "# #", "###", "# #", "###"],
        ["###", "# #", "###", "  #", "###"],
    ];

    /// <summary>Draws each box as a 3 px green frame and a filled green tag with the white number above its top-left corner (over the corner
    /// when there is no room above). Each side of the frame lies just outside the box where the picture has room for it and inside the box's
    /// edge where it has not. The digits come from a tiny built-in font; numbers 1..99 get a tag, other numbers only the frame.
    /// Boxes are clipped to the picture; empty ones are skipped. <paramref name="pixels"/>: 32-bit BGRA, top-down,
    /// <c>width * height * 4</c> bytes; changed in place. Never throws.</summary>
    public static void Draw(byte[] pixels, int width, int height, IReadOnlyList<AnnotationBox> boxes)
    {
        if (pixels == null || boxes == null || width <= 0 || height <= 0 || (long)width * height * 4 > pixels.Length) return;
        foreach (var b in boxes)
        {
            // clip to the picture
            long l = Math.Max(0L, b.Left), t = Math.Max(0L, b.Top);
            long r = Math.Min(width, (long)b.Left + b.Width), bt = Math.Min(height, (long)b.Top + b.Height);
            if (r <= l || bt <= t) continue;
            int L = (int)l, T = (int)t, R = (int)r, B = (int)bt;

            // each side: outside the box when there is room, else inside it
            int ol = L >= Frame ? L - Frame : L, orr = R + Frame <= width ? R + Frame : R;
            int ot = T >= Frame ? T - Frame : T, ob = B + Frame <= height ? B + Frame : B;
            int il = L >= Frame ? L : Math.Min(L + Frame, R), ir = R + Frame <= width ? R : Math.Max(R - Frame, L);
            int it = T >= Frame ? T : Math.Min(T + Frame, B), ib = B + Frame <= height ? B : Math.Max(B - Frame, T);
            // the top and bottom bars span the full outer width, the left and right bars the rows between them
            Fill(pixels, width, height, ol, ot, orr, it, false);
            Fill(pixels, width, height, ol, ib, orr, ob, false);
            Fill(pixels, width, height, ol, it, il, ib, false);
            Fill(pixels, width, height, ir, it, orr, ib, false);

            if (b.Number is >= 1 and <= 99) Tag(pixels, width, height, ol, ot, b.Number);
        }
    }

    static void Tag(byte[] px, int w, int h, int x, int y, int number)
    {
        var text = number.ToString();
        int scale = w >= 1600 ? 3 : 2;
        int gw = GlyphW * scale, gap = scale;
        int tw = text.Length * gw + (text.Length - 1) * gap + 2 * Pad;
        int th = GlyphH * scale + 2 * Pad;
        int ty = y >= th ? y - th : y;   // above the frame when there is room, else over its top-left corner
        Fill(px, w, h, x, ty, x + tw, ty + th, false);
        int gx = x + Pad;
        foreach (var ch in text)
        {
            var rows = Digits[ch - '0'];
            for (int ry = 0; ry < GlyphH; ry++)
                for (int rx = 0; rx < GlyphW; rx++)
                    if (rows[ry][rx] == '#')
                        Fill(px, w, h, gx + rx * scale, ty + Pad + ry * scale, gx + (rx + 1) * scale, ty + Pad + (ry + 1) * scale, true);
            gx += gw + gap;
        }
    }

    /// <summary>Fills [x0, x1) x [y0, y1) with green or white, clipped to the picture.</summary>
    static void Fill(byte[] px, int w, int h, int x0, int y0, int x1, int y1, bool white)
    {
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, w); y1 = Math.Min(y1, h);
        byte b = white ? (byte)255 : (byte)(Green & 0xFF), g = white ? (byte)255 : (byte)((Green >> 8) & 0xFF), r = white ? (byte)255 : (byte)((Green >> 16) & 0xFF);
        for (int y = y0; y < y1; y++)
        {
            int i = (y * w + x0) * 4;
            for (int x = x0; x < x1; x++, i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        }
    }

    /// <summary>A screen rectangle (<paramref name="right"/> and <paramref name="bottom"/> exclusive, like a RECT) as a picture rectangle,
    /// when the picture is the screen area (<paramref name="sx"/>, <paramref name="sy"/>, <paramref name="sw"/>, <paramref name="sh"/>) scaled to
    /// <paramref name="pw"/> x <paramref name="ph"/>. The part outside the area is cut off. null when nothing of it is left or a size is not positive.</summary>
    public static AnnotationBox? ToPicture(int left, int top, int right, int bottom, int sx, int sy, int sw, int sh, int pw, int ph, int number)
    {
        if (sw <= 0 || sh <= 0 || pw <= 0 || ph <= 0) return null;
        long l = Math.Max(left, sx), t = Math.Max(top, sy), r = Math.Min(right, (long)sx + sw), b = Math.Min(bottom, (long)sy + sh);
        if (r <= l || b <= t) return null;
        double kx = (double)pw / sw, ky = (double)ph / sh;
        int pl = (int)Math.Round((l - sx) * kx), pt = (int)Math.Round((t - sy) * ky);
        int pr = (int)Math.Round((r - sx) * kx), pb = (int)Math.Round((b - sy) * ky);
        return new AnnotationBox(pl, pt, Math.Max(1, pr - pl), Math.Max(1, pb - pt), number);
    }
}
