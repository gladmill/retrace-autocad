using Xunit;

namespace Retrace.Tests;

public class RectMapTests
{
    [Fact]
    public void A_window_scaled_up_125_percent_maps_its_points()
    {
        var l = new PxRect(100, 50, 500, 450); var p = new PxRect(125, 62, 625, 562);
        Assert.Equal((125, 62), RectMap.Map(100, 50, l, p));
        Assert.Equal((375, 312), RectMap.Map(300, 250, l, p));
        Assert.Equal((300, 250), RectMap.Map(375, 312, p, l));
    }

    [Fact]
    public void A_monitor_left_of_and_above_the_primary_has_negative_coordinates()
    {
        var l = new PxRect(-1000, -600, -200, 0); var p = new PxRect(-2000, -1200, -400, 0);   // scale 2
        Assert.Equal((-2000, -1200), RectMap.Map(-1000, -600, l, p));
        Assert.Equal((-1200, -600), RectMap.Map(-600, -300, l, p));
        Assert.Equal((-600, -300), RectMap.Map(-1200, -600, p, l));
    }

    [Fact]
    public void A_window_scaled_down_maps_by_ratio_below_one()
    {
        var l = new PxRect(0, 0, 2000, 1000); var p = new PxRect(0, 0, 1000, 500);
        Assert.Equal((500, 250), RectMap.Map(1000, 500, l, p));
    }

    [Fact]
    public void Scale_150_rounds_halves_to_even_and_the_round_trip_is_exact()
    {
        var l = new PxRect(0, 0, 400, 300); var p = new PxRect(0, 0, 600, 450);
        Assert.Equal((152, 0), RectMap.Map(101, 0, l, p));   // 151.5
        Assert.Equal((154, 0), RectMap.Map(103, 0, l, p));   // 154.5
        Assert.Equal((0, 2), RectMap.Map(0, 1, l, p));       // 1.5
        for (int x = 0; x <= 400; x++)
            Assert.Equal(x, RectMap.Map(RectMap.Map(x, 0, l, p).X, 0, p, l).X);
    }

    [Fact]
    public void Scale_175_maps_exactly_and_round_trips_in_both_axes()
    {
        var l = new PxRect(0, 0, 400, 300); var p = new PxRect(0, 0, 700, 525);
        Assert.Equal((2, 2), RectMap.Map(1, 1, l, p));     // 1.75
        Assert.Equal((5, 5), RectMap.Map(3, 3, l, p));     // 5.25
        Assert.Equal((4, 4), RectMap.Map(2, 2, l, p));     // 3.5
        Assert.Equal((700, 525), RectMap.Map(400, 300, l, p));
        for (int x = 0; x <= 400; x++)
        {
            var q = RectMap.Map(x, x % 301, l, p);
            Assert.Equal((x, x % 301), RectMap.Map(q.X, q.Y, p, l));
        }
    }

    [Fact]
    public void A_non_uniform_scale_and_an_offset_origin_map_each_axis_by_its_own_ratio()
    {
        var l = new PxRect(10, 20, 410, 320); var p = new PxRect(0, 0, 700, 450);   // x 1.75, y 1.5
        Assert.Equal((175, 150), RectMap.Map(110, 120, l, p));
        Assert.Equal((0, 0), RectMap.Map(10, 20, l, p));
        Assert.Equal((700, 450), RectMap.Map(410, 320, l, p));
        Assert.Equal((110, 120), RectMap.Map(175, 150, p, l));
    }
}
