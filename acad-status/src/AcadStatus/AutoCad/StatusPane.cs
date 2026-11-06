using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.Windows;
using AcadStatus.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadStatus.AutoCad;

/// <summary>Small coloured dots for the pane, drawn once each.</summary>
static class DotIcons
{
    static readonly Dictionary<Tone, Icon> Cache = new();

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static Color ColorOf(Tone t)
    {
        var c = TonePalette.Of(t);
        return Color.FromArgb(c.R, c.G, c.B);
    }

    public static Icon Get(Tone tone)
    {
        if (Cache.TryGetValue(tone, out var cached)) return cached;
        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(ColorOf(tone));
            using var edge = new Pen(Color.FromArgb(150, 0, 0, 0), 1f);
            g.FillEllipse(fill, 3, 3, 10, 10);
            g.DrawEllipse(edge, 3, 3, 10, 10);
        }
        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            var icon = (Icon)temp.Clone();   // the clone owns its own handle; the original handle is destroyed below
            Cache[tone] = icon;
            return icon;
        }
        finally { DestroyIcon(hIcon); }
    }
}

/// <summary>The status bar indicator: a coloured dot and at most 18 characters, one compact cell on AutoCAD's status bar. Clicking it raises
/// <see cref="Clicked"/>.
/// <para>A status bar <c>Pane</c> of AutoCAD 2026 shows its icon or its text, never both (found in the VM test: with a text set the icon
/// disappears). So the indicator is two adjacent panes: the dot (icon only) and the text (text only, hidden while there is no text, which is
/// when AutoCAD is idle). The bar draws them as one cell. Only Icon, Text, ToolTipText and Visible are set: <c>MinimumWidth</c> and
/// <c>MaximumWidth</c> are not supported on a pane and raise a ".NET Assertion Failed" dialog that blocks AutoCAD; the panes size themselves.</para></summary>
sealed class StatusPane : IDisposable
{
    Pane? _dot, _text;
    PaneView? _shown;
    bool _textVisible;
    int _clicks;

    /// <summary>The indicator was clicked with the left button; the points are where, on the screen, and the top of the status bar.</summary>
    public event Action<System.Drawing.Point, int>? Clicked;

    public bool IsAdded => _dot != null;

    /// <summary>What the dump says about the indicator: that it is there, or why it is not.</summary>
    public string Note { get; private set; } = "not added yet";

    /// <summary>Adds the panes. False when AutoCAD's status bar does not exist yet (try again later).</summary>
    public bool Add()
    {
        if (_dot != null) return true;
        var bar = AcApp.StatusBar;
        if (bar == null) { Note = "AutoCAD has no status bar yet"; return false; }
        // Command: the style of AutoCAD's own clickable panes; without it a pane never raises MouseDown (found in the VM test)
        var dot = new Pane { Style = PaneStyles.Command, Visible = true };
        var text = new Pane { Style = PaneStyles.Command, Visible = false };
        dot.MouseDown += OnMouseDown;
        text.MouseDown += OnMouseDown;
        bar.Panes.Add(dot);
        bar.Panes.Add(text);
        _dot = dot;
        _text = text;
        _textVisible = false;
        _shown = null;
        Note = "added (dot and text pane at " + bar.Panes.IndexOf(dot) + " and " + bar.Panes.IndexOf(text) + " of " + bar.Panes.Count + ")";
        Log.Info("pane " + Note);
        return true;
    }

    public void Remove()
    {
        var dot = _dot;
        var text = _text;
        _dot = null;
        _text = null;
        _shown = null;
        if (dot == null || text == null) return;
        Log.Safe("remove pane", () =>
        {
            dot.MouseDown -= OnMouseDown;
            text.MouseDown -= OnMouseDown;
            var bar = AcApp.StatusBar;
            // (Panes.Contains throws a NullReferenceException while AutoCAD shuts down: remove without asking)
            bar?.Panes.Remove(dot);
            bar?.Panes.Remove(text);
            bar?.Update();
        });
    }

    public void Dispose() => Remove();

    /// <summary>Shows a view; changes nothing when it is the one already shown.</summary>
    public void Apply(PaneView view)
    {
        var dot = _dot;
        var text = _text;
        if (dot == null || text == null || Equals(view, _shown)) return;
        var old = _shown;
        _shown = view;
        if (old == null || old.Tone != view.Tone) dot.Icon = DotIcons.Get(view.Tone);
        dot.ToolTipText = view.Tooltip;
        text.ToolTipText = view.Tooltip;
        if (view.Text.Length == 0)
        {
            if (_textVisible) { text.Visible = false; _textVisible = false; }
        }
        else
        {
            if (old == null || old.Text != view.Text) text.Text = view.Text;
            if (!_textVisible) { text.Visible = true; _textVisible = true; }
        }
        AcApp.StatusBar?.Update();
    }

    void OnMouseDown(object? sender, StatusBarMouseDownEventArgs e)
    {
        Log.Safe("pane click", () =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left || sender is not Pane pane) return;
            // PointToScreen is relative to the status bar, not to the pane: (0, 0) is the bar's top left corner, so it gives the bar's top.
            // The event's X and Y are relative to the pane (VM test: 8, 12 for a click near its left edge), so the click's place is the mouse's.
            var barTop = pane.PointToScreen(System.Drawing.Point.Empty);
            var click = System.Windows.Forms.Cursor.Position;
            if (_clicks < 30) Log.Info("pane click " + ++_clicks + " at " + click.X + "," + click.Y + " (event " + e.X + "," + e.Y + "), bar top " + barTop.Y);
            Clicked?.Invoke(click, barTop.Y);
        });
    }
}
