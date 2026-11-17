using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class PointLikeZoomTests
{
    static ZoomWindow Apply(ref (double w, double h) view, PointZoomMemory mem, double x0, double y0, double x1, double y1, List<string>? log = null)
    {
        var win = ZoomWindow.FitStable(x0, y0, x1, y1, view.w, view.h, 0.15, 0.05, mem, log == null ? null : log.Add)!.Value;
        view = (win.Width, win.Height);   // the view now is the window
        return win;
    }

    // landscape, square and portrait viewports
    public static IEnumerable<object[]> Shapes => new[] { new object[] { 1600.0, 900.0 }, new object[] { 1000.0, 1000.0 }, new object[] { 900.0, 1600.0 } };

    [Theory, MemberData(nameof(Shapes))]
    public void A_point_zoomed_five_times_gives_the_same_window_each_time(double w, double h)
    {
        var mem = new PointZoomMemory(); var view = (w, h);
        var first = Apply(ref view, mem, 5000, 5000, 5000, 5000);
        for (int i = 0; i < 5; i++) Assert.Equal(first, Apply(ref view, mem, 5000, 5000, 5000, 5000));
        Assert.Equal(h * 0.05 * Math.Max(1, h / w), first.Height, 6);   // the minimum is 5 % of the first view, the window takes the viewport's shape
    }

    [Theory, MemberData(nameof(Shapes))]
    public void Another_point_gets_the_same_size_five_times(double w, double h)
    {
        var mem = new PointZoomMemory(); var view = (w, h);
        var a = Apply(ref view, mem, 10, 10, 10, 10);
        for (int i = 0; i < 5; i++)
        {
            var b = Apply(ref view, mem, -400 * (i + 1), 20, -400 * (i + 1), 20);
            Assert.Equal(a.Height, b.Height, 9); Assert.Equal(a.Width, b.Width, 9);
        }
    }

    [Fact]
    public void The_old_rule_compounds_twenty_fold()   // what the bug was
    {
        var a = ZoomWindow.Fit(0, 0, 0, 0, 1600.0 / 900, 0.15, 900 * 0.05)!.Value;
        var b = ZoomWindow.Fit(0, 0, 0, 0, a.Width / a.Height, 0.15, a.Height * 0.05)!.Value;
        Assert.Equal(20, a.Height / b.Height, 6);
    }

    [Fact]
    public void A_view_the_person_changed_starts_again_from_five_percent()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0);
        Apply(ref view, mem, 0, 0, 0, 0);
        view = (3200, 1800);   // the person zoomed out
        Assert.Equal(90, Apply(ref view, mem, 0, 0, 0, 0).Height, 6);
    }

    [Fact]
    public void A_big_object_gets_the_same_window_as_Fit_and_remembers_nothing()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0);
        var expected = ZoomWindow.Fit(0, 0, 700, 300, 1600.0 / 900, 0.15, 45)!.Value;
        Assert.Equal(expected, Apply(ref view, mem, 0, 0, 700, 300));
        Assert.False(mem.IsSet);
    }

    [Fact]
    public void A_big_object_after_a_point_keeps_its_own_size()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0);
        Apply(ref view, mem, 0, 0, 0, 0);
        Assert.Equal(ZoomWindow.Fit(0, 0, 500, 100, 1600.0 / 900, 0.15, 1)!.Value, Apply(ref view, mem, 0, 0, 500, 100));
    }

    [Fact]
    public void A_one_unit_circle_after_a_point_gets_the_ordinary_minimum_not_the_points_window()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0);
        var pt = Apply(ref view, mem, 5000, 5000, 5000, 5000);
        Assert.Equal(45, pt.Height, 6);
        var circle = Apply(ref view, mem, 100, 100, 101, 101);
        Assert.Equal(2.25, circle.Height, 6);                       // 5 % of the 45-high view, as the old rule gave
        Assert.Equal(circle, Apply(ref view, mem, 100, 100, 101, 101));   // zoomed again (record zoom, then post-test zoom): the same window, not tighter, not 45
        Assert.Equal(circle, Apply(ref view, mem, 100, 100, 101, 101));
    }

    [Fact]
    public void A_small_real_object_zoomed_twice_from_a_fresh_view_gives_the_same_window()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0);
        var a = Apply(ref view, mem, 0, 0, 1, 1);
        Assert.Equal(45, a.Height, 6);
        Assert.Equal(a, Apply(ref view, mem, 0, 0, 1, 1));
    }

    [Fact]
    public void A_target_that_qualifies_but_is_shown_in_another_view_logs_it()
    {
        var mem = new PointZoomMemory(); var view = (w: 1600.0, h: 900.0); var log = new List<string>();
        Apply(ref view, mem, 5, 5, 5, 5, log);
        Assert.Empty(log);
        view = (w: 1600.0, h: 900.0);   // the view is not the remembered one
        Apply(ref view, mem, 5, 5, 5, 5, log);
        Assert.Single(log);
        Assert.Contains("not the remembered view", log[0]);
        Assert.Contains("remembered 45", log[0]);
        var big = new List<string>();
        view = (1600, 900); Apply(ref view, mem, 0, 0, 900, 900, big);   // a big object would not have qualified: no line
        Assert.Empty(big);
    }

    [Fact]
    public void Unusable_input_gives_no_window()
    {
        var mem = new PointZoomMemory();
        Assert.Null(ZoomWindow.FitStable(0, 0, 1, 1, 0, 900, 0.15, 0.05, mem));      // a zero-width view
        Assert.Null(ZoomWindow.FitStable(5, 0, 1, 1, 1600, 900, 0.15, 0.05, mem));   // a reversed box
        Assert.Null(ZoomWindow.FitStable(double.NaN, 0, 1, 1, 1600, 900, 0.15, 0.05, mem));
    }
}

public class ZoomExtentsPickTests
{
    static ZoomBox B(double x0, double y0, double x1, double y1) => new(x0, y0, 0, x1, y1, 0);
    static readonly Func<ZoomBox?> None = () => null;
    static Func<ExplodeResult> Explodes(ZoomBox? b, int n = 3) => () => new ExplodeResult(b, n, n, null);
    static Func<ExplodeResult> NoExplode => () => throw new InvalidOperationException("eCannotExplodeEntity");
    const double Small = 1.0;   // the size under which the minimum rule decides the window

    sealed class Calls
    {
        public int Bounds, Explode;
        public Func<ZoomBox?> BoundsOf(ZoomBox? b) => () => { Bounds++; return b; };
        public Func<ExplodeResult> ExplodeOf(ExplodeResult r) => () => { Explode++; return r; };
    }

    [Fact]
    public void A_normal_box_is_used_and_neither_fall_back_is_called()
    {
        var c = new Calls();
        var r = ZoomExtentsPick.Pick(() => B(0, 0, 10, 5), c.BoundsOf(B(0, 0, 99, 99)), c.ExplodeOf(new ExplodeResult(B(0, 0, 99, 99), 1, 1, null)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Object, r.Rule); Assert.Equal(B(0, 0, 10, 5), r.Box);
        Assert.Equal(0, c.Bounds); Assert.Equal(0, c.Explode);
    }

    [Fact]
    public void A_point_like_box_falls_back_to_the_exploded_pieces()
    {
        var r = ZoomExtentsPick.Pick(() => B(25.92, 0, 25.92, 0), None, Explodes(B(0, -100, 500, 100)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Exploded, r.Rule); Assert.Equal(B(0, -100, 500, 100), r.Box);
        Assert.Contains("explode (3 pieces)", r.Seen);
    }

    [Fact]
    public void Bounds_is_used_first_and_the_explode_is_not_called()
    {
        var c = new Calls();
        var r = ZoomExtentsPick.Pick(() => B(1, 1, 1, 1), c.BoundsOf(B(0, 0, 40, 40)), c.ExplodeOf(ExplodeResult.None), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Bounds, r.Rule);
        Assert.Equal(1, c.Bounds); Assert.Equal(0, c.Explode);
    }

    [Fact]
    public void A_throwing_reader_falls_through_and_is_noted()
    {
        var r = ZoomExtentsPick.Pick(() => throw new InvalidOperationException("eInvalidExtents"), () => throw new NotSupportedException("eNotApplicable"), Explodes(B(0, 0, 8, 8)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Exploded, r.Rule);
        Assert.Contains("GeometricExtents threw InvalidOperationException: eInvalidExtents", r.Seen);
        Assert.Contains("Bounds threw", r.Seen);
    }

    [Fact]
    public void A_reversed_box_is_no_extents_and_the_explode_is_tried()
    {
        var r = ZoomExtentsPick.Pick(() => B(10, 10, -10, -10), None, Explodes(B(-3, -3, 3, 3)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Exploded, r.Rule); Assert.Contains("unusable", r.Seen);
    }

    [Fact]
    public void Point_like_everywhere_gives_the_point_rule()
    {
        var r = ZoomExtentsPick.Pick(() => B(5000, 5000, 5000, 5000), None, NoExplode, 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Point, r.Rule); Assert.Equal(B(5000, 5000, 5000, 5000), r.Box);
        Assert.Contains("explode threw", r.Seen);
    }

    [Fact]
    public void A_point_from_the_pieces_alone_is_still_a_point()
    {
        var r = ZoomExtentsPick.Pick(() => throw new Exception("x"), None, Explodes(B(2, 3, 2, 3)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Point, r.Rule); Assert.Equal(B(2, 3, 2, 3), r.Box);
    }

    [Fact]
    public void An_enormous_box_and_nothing_else_gives_none()
    {
        var r = ZoomExtentsPick.Pick(() => B(-1e20, 0, 1e20, 0), None, Explodes(null, 0), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.None, r.Rule); Assert.Null(r.Box);
    }

    [Fact]
    public void An_enormous_box_with_real_pieces_uses_the_pieces()
    {
        var r = ZoomExtentsPick.Pick(() => B(-1e20, 0, 1e20, 0), None, Explodes(B(0, 0, 9, 9)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Exploded, r.Rule);
    }

    [Fact]
    public void A_tiny_but_not_zero_box_is_replaced_by_a_clearly_larger_one_that_contains_it()
    {
        var r = ZoomExtentsPick.Pick(() => B(10, 10, 10.01, 10.01), None, Explodes(B(0, 0, 100, 50)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Exploded, r.Rule);
    }

    [Fact]
    public void A_larger_result_that_does_not_contain_the_tiny_box_or_is_not_twice_as_big_is_not_used()
    {
        var elsewhere = ZoomExtentsPick.Pick(() => B(10, 10, 10.01, 10.01), None, Explodes(B(200, 200, 300, 300)), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Object, elsewhere.Rule); Assert.Equal(B(10, 10, 10.01, 10.01), elsewhere.Box);
        var barely = ZoomExtentsPick.Pick(() => B(10, 10, 10.4, 10.4), None, Explodes(B(9.9, 9.9, 10.6, 10.6)), 1e15, Small, true);   // 0.7 against 0.4
        Assert.Equal(ZoomExtentsRule.Object, barely.Rule);
        Assert.Contains("kept", barely.Seen);
    }

    [Fact]
    public void Without_fall_backs_nothing_is_called()
    {
        var c = new Calls();
        var r = ZoomExtentsPick.Pick(() => B(7, 7, 7, 7), c.BoundsOf(B(0, 0, 9, 9)), c.ExplodeOf(new ExplodeResult(B(0, 0, 9, 9), 1, 1, null)), 1e15, Small, false);
        Assert.Equal(ZoomExtentsRule.Point, r.Rule);
        Assert.Equal(0, c.Bounds); Assert.Equal(0, c.Explode);
        var tiny = ZoomExtentsPick.Pick(() => B(7, 7, 7.5, 7.5), c.BoundsOf(null), c.ExplodeOf(ExplodeResult.None), 1e15, Small, false);
        Assert.Equal(ZoomExtentsRule.Object, tiny.Rule);
        var none = ZoomExtentsPick.Pick(() => throw new Exception("x"), c.BoundsOf(null), c.ExplodeOf(ExplodeResult.None), 1e15, Small, false);
        Assert.Equal(ZoomExtentsRule.None, none.Rule);
        Assert.Equal(0, c.Bounds); Assert.Equal(0, c.Explode);
    }

    [Fact]
    public void A_cut_short_or_database_resident_explode_is_no_result()
    {
        var cut = ZoomExtentsPick.Pick(() => B(1, 1, 1, 1), None, () => new ExplodeResult(B(0, 0, 50, 50), 10, 400, "stopped at the time budget after 10 of 400 pieces"), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.Point, cut.Rule); Assert.Contains("stopped at the time budget after 10 of 400 pieces", cut.Seen);
        var db = ZoomExtentsPick.Pick(() => throw new Exception("x"), None, () => new ExplodeResult(null, 0, 1, "returned a piece that belongs to the database: result discarded"), 1e15, Small, true);
        Assert.Equal(ZoomExtentsRule.None, db.Rule); Assert.Contains("belongs to the database", db.Seen);
    }
}
