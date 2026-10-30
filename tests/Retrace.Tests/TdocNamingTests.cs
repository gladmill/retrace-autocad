using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Tdoc: file and folder names, and the geometry that brings a dialog onto the screen.</summary>
public class TdocNamingTests
{
    [Theory]
    [InlineData("Block training", "Block training")]
    [InlineData("  Q3: plans/review?  ", "Q3- plans-review-")]
    [InlineData("", "training")]
    [InlineData(null, "training")]
    [InlineData("...", "training")]
    [InlineData("test", "test")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Console", "Console")]
    public void The_safe_title_is_a_valid_file_name_part(string? title, string expected) => Assert.Equal(expected, TdocNaming.SafeTitle(title));

    [Fact]
    public void A_long_title_is_cut_to_sixty_characters_without_a_trailing_space()
    {
        var t = TdocNaming.SafeTitle(new string('a', 59) + " bbbbbb");
        Assert.Equal(59, t.Length);   // cut at 60, the space before the cut is trimmed
        Assert.Equal(60, TdocNaming.SafeTitle(new string('x', 200)).Length);
    }

    [Fact]
    public void The_folder_starts_with_the_stamp_and_the_docx_sits_beside_it()
    {
        var folder = TdocNaming.FolderName(new DateTime(2026, 10, 30, 14, 5, 9), "Block: basics");
        Assert.Equal("2026-10-30_14-05-09_Block- basics", folder);
        Assert.Equal("/docs/training/2026-10-30_14-05-09_Block- basics.docx", TdocNaming.DocxPath("/docs/training/" + folder).Replace('\\', '/'));
        Assert.Equal("/x/y.docx", TdocNaming.DocxPath("/x/y/").Replace('\\', '/'));
    }

    [Fact]
    public void Pictures_are_named_after_test_step_and_number()
    {
        Assert.Equal("t2-s5-1.png", TdocNaming.PictureName(2, 5, 1));
        Assert.Equal("pictures/t2-s5-1.png", TdocNaming.PictureRel(2, 5, 1));
        Assert.Equal("tdoc.json", TdocNaming.ManifestFile);
    }

    [Theory]
    [InlineData(10, 10, 110, 110, 0, 0, 1920, 1080, 0, 0)]        // inside: stays
    [InlineData(-50, 20, 50, 120, 0, 0, 1920, 1080, 50, 0)]       // sticks out on the left
    [InlineData(1900, 1000, 2000, 1100, 0, 0, 1920, 1080, -80, -20)]   // sticks out on the right and at the bottom
    public void A_rectangle_is_moved_by_the_least_that_puts_it_inside(int l, int t, int r, int b, int al, int at, int ar, int ab, int dx, int dy)
    {
        Assert.Equal((dx, dy), TdocGeometry.MoveInto(l, t, r, b, al, at, ar, ab));
    }

    [Fact]
    public void A_rectangle_fully_off_the_screen_is_centred_and_a_big_one_keeps_its_corner()
    {
        var (dx, dy) = TdocGeometry.MoveInto(5000, 5000, 5400, 5300, 0, 0, 1920, 1080);
        Assert.Equal((1920 - 400) / 2, 5000 + dx);
        Assert.Equal((1080 - 300) / 2, 5000 + dy);
        Assert.Equal((0, 0), TdocGeometry.MoveInto(0, 0, 3000, 2000, 0, 0, 1920, 1080));   // larger than the screen, corner inside
        Assert.Equal((100, 0), TdocGeometry.MoveInto(-100, 0, 3000, 2000, 0, 0, 1920, 1080));
        Assert.Equal((0, 0), TdocGeometry.MoveInto(10, 10, 10, 10, 0, 0, 100, 100));         // empty rectangle: nothing to do
    }
}
