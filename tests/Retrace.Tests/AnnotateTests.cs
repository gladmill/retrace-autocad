namespace Retrace.Tests;

/// <summary>Core/Annotate: green frames and numbered tags on raw BGRA pixels, and the screen -> picture rectangle math. Synthetic pictures only.</summary>
public class AnnotateTests
{
    const byte Grey = 0x80;

    static byte[] Picture(int w, int h)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, Grey);
        return px;
    }

    static (byte B, byte G, byte R, byte A) At(byte[] px, int w, int x, int y) { var i = (y * w + x) * 4; return (px[i], px[i + 1], px[i + 2], px[i + 3]); }
    static bool IsGreen(byte[] px, int w, int x, int y) => At(px, w, x, y) == (0x4C, 0xB1, 0x22, 0xFF);
    static bool IsWhite(byte[] px, int w, int x, int y) => At(px, w, x, y) == (255, 255, 255, 255);
    static bool IsOriginal(byte[] px, int w, int x, int y) => At(px, w, x, y) == (Grey, Grey, Grey, Grey);

    [Fact]
    public void Green_is_the_documented_colour()
    {
        Assert.Equal(0xFF22B14Cu, Annotate.Green);
    }

    [Fact]
    public void Frame_is_three_pixels_just_outside_the_box_and_the_inside_is_untouched()
    {
        const int w = 100, h = 100;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(30, 30, 20, 20, 0)]);   // box x 30..49, y 30..49; number 0 = frame only
        foreach (var x in new[] { 27, 28, 29 }) Assert.True(IsGreen(px, w, x, 40), $"left bar x={x}");
        foreach (var x in new[] { 50, 51, 52 }) Assert.True(IsGreen(px, w, x, 40), $"right bar x={x}");
        foreach (var y in new[] { 27, 28, 29 }) Assert.True(IsGreen(px, w, 40, y), $"top bar y={y}");
        foreach (var y in new[] { 50, 51, 52 }) Assert.True(IsGreen(px, w, 40, y), $"bottom bar y={y}");
        Assert.True(IsGreen(px, w, 27, 27));   // corners
        Assert.True(IsGreen(px, w, 52, 52));
        Assert.True(IsOriginal(px, w, 26, 40));   // just beyond the frame
        Assert.True(IsOriginal(px, w, 53, 40));
        for (int y = 30; y < 50; y++)
            for (int x = 30; x < 50; x++)
                Assert.True(IsOriginal(px, w, x, y), $"inside ({x},{y})");
    }

    [Fact]
    public void The_number_is_drawn_in_a_green_tag_above_the_top_left_corner()
    {
        const int w = 100, h = 100;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(30, 40, 20, 20, 7)]);
        // scale 2: the tag is 12 x 16 px, its bottom meets the frame's top (y 37), its left is the frame's left (x 27)
        Assert.True(IsGreen(px, w, 27, 21));
        Assert.True(IsGreen(px, w, 38, 36));
        Assert.True(IsOriginal(px, w, 39, 30));   // right of the tag
        // "7" has 7 lit cells of 2x2 px
        int white = 0;
        for (int y = 21; y < 37; y++) for (int x = 27; x < 39; x++) if (IsWhite(px, w, x, y)) white++;
        Assert.Equal(7 * 4, white);
        Assert.True(IsWhite(px, w, 30, 24));    // top row of the digit
        Assert.False(IsWhite(px, w, 30, 22));   // padding
    }

    [Fact]
    public void Different_numbers_draw_different_digits_and_two_digits_make_a_wider_tag()
    {
        const int w = 100, h = 100;
        int Whites(int number)
        {
            var px = Picture(w, h);
            Annotate.Draw(px, w, h, [new AnnotationBox(30, 40, 20, 20, number)]);
            int n = 0;
            for (int y = 0; y < 37; y++) for (int x = 0; x < w; x++) if (IsWhite(px, w, x, y)) n++;
            return n;
        }
        Assert.Equal(8 * 4, Whites(1));    // "1" lights 8 cells
        Assert.Equal(13 * 4, Whites(8));   // "8" lights 13
        Assert.Equal((13 + 12) * 4, Whites(80));   // "0" lights 12
        var two = Picture(w, h);
        Annotate.Draw(two, w, h, [new AnnotationBox(30, 40, 20, 20, 42)]);   // tag is 2*6 + 2 + 6 = 20 px wide: x 27..46
        Assert.True(IsGreen(two, w, 45, 30));
        Assert.True(IsOriginal(two, w, 47, 30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-3)]
    public void A_number_outside_1_to_99_gets_the_frame_but_no_tag(int number)
    {
        const int w = 100, h = 100;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(30, 40, 20, 20, number)]);
        Assert.True(IsGreen(px, w, 40, 38));
        Assert.True(IsOriginal(px, w, 28, 30));   // where a tag would be
        Assert.True(IsOriginal(px, w, 28, 22));
    }

    [Fact]
    public void A_box_at_the_edges_is_clipped_and_its_frame_moves_inside()
    {
        const int w = 50, h = 50;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(-10, -10, 30, 30, 0)]);   // clipped to 0..19
        foreach (var x in new[] { 0, 1, 2 }) Assert.True(IsGreen(px, w, x, 10), $"left bar x={x}");
        Assert.True(IsOriginal(px, w, 3, 10));
        foreach (var y in new[] { 0, 1, 2 }) Assert.True(IsGreen(px, w, 10, y), $"top bar y={y}");
        Assert.True(IsOriginal(px, w, 10, 3));
        foreach (var x in new[] { 20, 21, 22 }) Assert.True(IsGreen(px, w, x, 10), $"right bar x={x}");   // there is room on the right
        foreach (var y in new[] { 20, 21, 22 }) Assert.True(IsGreen(px, w, 10, y), $"bottom bar y={y}");

        px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(45, 40, 30, 30, 0)]);   // runs past the right and bottom edges
        foreach (var x in new[] { 47, 48, 49 }) Assert.True(IsGreen(px, w, x, 45), $"right bar x={x}");
        Assert.True(IsOriginal(px, w, 46, 45));
        foreach (var y in new[] { 47, 48, 49 }) Assert.True(IsGreen(px, w, 47, y), $"bottom bar y={y}");
        Assert.True(IsGreen(px, w, 43, 45));   // left bar outside the box (room there)
    }

    [Fact]
    public void A_tag_that_does_not_fit_is_clipped_not_thrown()
    {
        const int w = 20, h = 20;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(14, 0, 6, 6, 88)]);   // the tag runs past the right edge; no room above
        Assert.True(IsGreen(px, w, 19, 1));
    }

    [Fact]
    public void Empty_or_outside_boxes_and_bad_input_change_nothing_and_do_not_throw()
    {
        const int w = 40, h = 40;
        var px = Picture(w, h);
        var before = (byte[])px.Clone();
        Annotate.Draw(px, w, h, [new AnnotationBox(10, 10, 0, 5, 1), new AnnotationBox(10, 10, 5, -2, 1), new AnnotationBox(100, 100, 10, 10, 1), new AnnotationBox(-50, 5, 20, 10, 1)]);
        Assert.Equal(before, px);
        Annotate.Draw(px, w, h, []);
        Annotate.Draw(px, 0, 0, [new AnnotationBox(1, 1, 5, 5, 1)]);
        Annotate.Draw(new byte[10], w, h, [new AnnotationBox(1, 1, 5, 5, 1)]);   // too few bytes
        Assert.Equal(before, px);
    }

    [Fact]
    public void Several_boxes_are_all_drawn()
    {
        const int w = 120, h = 60;
        var px = Picture(w, h);
        Annotate.Draw(px, w, h, [new AnnotationBox(10, 20, 20, 20, 1), new AnnotationBox(70, 20, 20, 20, 2)]);
        Assert.True(IsGreen(px, w, 8, 30));
        Assert.True(IsGreen(px, w, 68, 30));
        Assert.True(IsOriginal(px, w, 50, 30));
    }

    // ---- ToPicture ------------------------------------------------------------------------------------------------

    [Fact]
    public void ToPicture_at_the_same_size_only_moves_the_origin()
    {
        var b = Annotate.ToPicture(120, 220, 170, 250, 100, 200, 800, 600, 800, 600, 3);
        Assert.Equal(new AnnotationBox(20, 20, 50, 30, 3), b);
    }

    [Fact]
    public void ToPicture_scales_with_the_picture()
    {
        var half = Annotate.ToPicture(200, 100, 300, 160, 0, 0, 1000, 800, 500, 400, 1);
        Assert.Equal(new AnnotationBox(100, 50, 50, 30, 1), half);
        var wide = Annotate.ToPicture(100, 100, 200, 200, 0, 0, 1000, 500, 2000, 500, 2);   // x doubled, y the same
        Assert.Equal(new AnnotationBox(200, 100, 200, 100, 2), wide);
    }

    [Fact]
    public void ToPicture_works_with_a_negative_screen_origin()
    {
        var b = Annotate.ToPicture(-1900, 10, -1800, 50, -1920, 0, 1920, 1080, 960, 540, 5);
        Assert.Equal(new AnnotationBox(10, 5, 50, 20, 5), b);
    }

    [Fact]
    public void ToPicture_cuts_off_what_is_outside_the_area_and_gives_null_for_nothing()
    {
        var cut = Annotate.ToPicture(-50, 90, 60, 150, 0, 0, 100, 100, 100, 100, 1);
        Assert.Equal(new AnnotationBox(0, 90, 60, 10, 1), cut);
        Assert.Null(Annotate.ToPicture(200, 200, 300, 300, 0, 0, 100, 100, 100, 100, 1));   // fully outside
        Assert.Null(Annotate.ToPicture(10, 10, 10, 20, 0, 0, 100, 100, 100, 100, 1));       // no width
        Assert.Null(Annotate.ToPicture(10, 10, 20, 20, 0, 0, 0, 100, 100, 100, 1));         // bad screen size
        Assert.Null(Annotate.ToPicture(10, 10, 20, 20, 0, 0, 100, 100, 0, 100, 1));         // bad picture size
    }

    [Fact]
    public void ToPicture_keeps_a_tiny_box_at_least_one_pixel()
    {
        var b = Annotate.ToPicture(10, 10, 12, 12, 0, 0, 1000, 1000, 100, 100, 1);
        Assert.NotNull(b);
        Assert.True(b!.Value.Width >= 1 && b.Value.Height >= 1);
    }
}
