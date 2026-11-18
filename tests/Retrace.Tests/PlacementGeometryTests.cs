using Retrace.Core;

namespace Retrace.Tests;

public class PlacementGeometryTests
{
    static readonly PxBox Primary = new(0, 0, 1920, 1040);          // work area (taskbar removed)
    static readonly PxBox Left = new(-1920, 0, 0, 1040);            // a monitor left of the primary one: negative X

    [Fact]
    public void Centres_on_the_owner()
    {
        var owner = new PxBox(200, 100, 1000, 700);
        Assert.Equal((200 + (800 - 400) / 2, 100 + (600 - 300) / 2), PlacementGeometry.CenterOn(owner, 400, 300, Primary));
    }

    [Fact]
    public void Centred_on_a_maximized_owner_is_the_middle_of_the_monitor()
    {
        var owner = new PxBox(-8, -8, 1928, 1048);   // a maximized window overhangs by the border
        Assert.Equal((760, 370), PlacementGeometry.CenterOn(owner, 400, 300, Primary));
    }

    [Fact]
    public void Clamps_a_window_that_would_leave_the_work_area()
    {
        var owner = new PxBox(1700, 900, 1900, 1000);
        Assert.Equal((1520, 740), PlacementGeometry.CenterOn(owner, 400, 300, Primary));
        var o2 = new PxBox(-100, -100, 100, 100);
        Assert.Equal((0, 0), PlacementGeometry.CenterOn(o2, 400, 300, Primary));
    }

    [Fact]
    public void A_window_larger_than_the_work_area_sits_at_its_top_left() =>
        Assert.Equal((0, 0), PlacementGeometry.CenterOn(Primary, 3000, 2000, Primary));

    [Fact]
    public void Second_monitor_with_negative_coordinates()
    {
        var owner = new PxBox(-1800, 100, -200, 900);   // AutoCAD on the left monitor
        var work = PlacementGeometry.PickWork(owner, new[] { Primary, Left })!.Value;
        Assert.Equal(Left, work);
        Assert.Equal((-1000 - 200, 500 - 150), PlacementGeometry.CenterOn(owner, 400, 300, work));
        Assert.Equal((0 - 16 - 420, 1040 - 16 - 160), PlacementGeometry.LowerRight(work, 420, 160, 16));
    }

    [Fact]
    public void Picks_the_monitor_with_the_most_overlap()
    {
        var owner = new PxBox(-300, 0, 1500, 800);   // straddles both, mostly the primary
        Assert.Equal(Primary, PlacementGeometry.PickWork(owner, new[] { Left, Primary }));
    }

    [Fact]
    public void Owner_off_every_monitor_takes_the_nearest()
    {
        var owner = new PxBox(5000, 0, 5400, 300);
        Assert.Equal(Primary, PlacementGeometry.PickWork(owner, new[] { Left, Primary }));
        Assert.Null(PlacementGeometry.PickWork(owner, Array.Empty<PxBox>()));
    }

    [Fact] public void Lower_right_with_margin() => Assert.Equal((1920 - 420 - 16, 1040 - 160 - 16), PlacementGeometry.LowerRight(Primary, 420, 160, 16));

    [Fact]
    public void Lower_right_of_a_work_area_not_at_the_origin()
    {
        var work = new PxBox(1920, 40, 3840, 1080);
        Assert.Equal((3840 - 420 - 20, 1080 - 160 - 20), PlacementGeometry.LowerRight(work, 420, 160, 20));
    }

    [Theory]
    [InlineData(96, 16)]
    [InlineData(120, 20)]
    [InlineData(144, 24)]
    [InlineData(192, 32)]
    [InlineData(0, 16)]
    public void Margin_scales_with_dpi(int dpi, int expected) => Assert.Equal(expected, PlacementGeometry.Scale(PlacementGeometry.CornerMargin, dpi));

    [Fact] public void Dip_size_to_pixels_at_150_percent() => Assert.Equal(630, PlacementGeometry.Scale(420, 144));

    [Fact]
    public void Minimized_owner_centres_on_the_work_area()
    {
        var shown = new PxBox(100, 100, 500, 400);
        Assert.Equal(Primary, PlacementGeometry.OwnerOrWork(shown, true, Primary));
        Assert.Equal(Primary, PlacementGeometry.OwnerOrWork(null, false, Primary));
        Assert.Equal(Primary, PlacementGeometry.OwnerOrWork(new PxBox(0, 0, 0, 0), false, Primary));
        Assert.Equal(shown, PlacementGeometry.OwnerOrWork(shown, false, Primary));
    }

    [Fact]
    public void Child_dialog_centres_on_its_parent_not_on_the_screen()
    {
        var parent = new PxBox(300, 200, 1100, 900);
        Assert.Equal((500, 400), PlacementGeometry.CenterOn(parent, 400, 300, Primary));
    }

    [Fact]
    public void FitSize_caps_each_dimension_on_its_own()
    {
        Assert.Equal((1920, 300), PlacementGeometry.FitSize(3000, 300, Primary));   // overflow in one dimension only
        Assert.Equal((400, 1040), PlacementGeometry.FitSize(400, 2000, Primary));
        Assert.Equal((400, 300), PlacementGeometry.FitSize(400, 300, Primary));
        Assert.Equal((1920, 1040), PlacementGeometry.FitSize(3000, 2000, Primary));
    }

    [Fact]
    public void KeepBottom_grows_upward_and_keeps_x()
    {
        // 420 x 135 in the corner, then its text grows to 260 high: the bottom edge stays 16 above the work area's bottom
        var grown = new PxBox(1484, 889, 1904, 1149);   // WPF grew it downward from the old top
        var (x, y, h) = PlacementGeometry.KeepBottom(grown, Primary, 16);
        Assert.Equal((1484, 1040 - 16 - 260, 260), (x, y, h));
        Assert.Equal(1024, y + h);
    }

    [Fact]
    public void KeepBottom_keeps_a_window_that_was_moved_aside_on_the_left()
    {
        var (x, y, h) = PlacementGeometry.KeepBottom(new PxBox(24, 800, 444, 1000), Primary, 16);
        Assert.Equal((24, 1040 - 16 - 200, 200), (x, y, h));
    }

    [Fact]
    public void KeepBottom_caps_the_height_to_the_work_area_and_moves_x_inside()
    {
        var (x, y, h) = PlacementGeometry.KeepBottom(new PxBox(1700, 0, 2120, 3000), Primary, 16);
        Assert.Equal(1040 - 32, h);
        Assert.Equal(16, y);
        Assert.Equal(1920 - 420, x);
    }

    [Fact]
    public void KeepBottom_on_a_second_monitor_with_negative_coordinates()
    {
        var (x, y, h) = PlacementGeometry.KeepBottom(new PxBox(-460, 700, -40, 900), Left, 16);
        Assert.Equal((-460, 1040 - 16 - 200, 200), (x, y, h));
    }

    [Fact]
    public void PickWork_tie_takes_the_first_work_area_in_the_list()
    {
        var owner = new PxBox(-100, 0, 100, 800);   // 100 px on each monitor
        Assert.Equal(Left, PlacementGeometry.PickWork(owner, new[] { Left, Primary }));
        Assert.Equal(Primary, PlacementGeometry.PickWork(owner, new[] { Primary, Left }));
    }
}
