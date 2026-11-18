using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System.Windows;
using System.Windows.Media;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// The ways an add-in can put a pane into AutoCAD, as small generic samples (clicks in them are recorded by Retrace as palette steps; see README). Each click draws a circle on a layer named after the pane, so a replay that
    /// misses a click draws nothing there:
    /// DRAWPANES = a palette set with four tabs: Canvas (a custom-drawn WinForms control with no click handler, mouse messages only), Host (WinForms hosting a WPF element through ElementHost), Wpf (AddVisual: a WPF
    /// control with a custom drawn element, a tree, a multi-select list and a text box), Grid (a custom-drawn WinForms grid that only changes its own selection: no command, no change to the drawing).
    /// DRAWPANEFORM = a modeless WinForms Form (ShowModelessDialog) with the custom canvas and a button. DRAWPANEWIN = a modeless WPF Window (ShowModelessWindow). DRAWPANEWPFHOST = a modeless WPF Window that hosts a
    /// WinForms canvas (WindowsFormsHost). DRAWPANELATE = a second palette set, made when the command runs (so after a recording started); every call makes another one. DRAWPANEROLL = the DRAWPANES set with auto roll-up.
    /// </summary>
    public class PaneCommands
    {
        private static PaletteSet? set;
        private static int lateCount;

        [CommandMethod("DRAWPANES")] public void Panes() { Show(false); }
        [CommandMethod("DRAWPANEROLL")] public void PanesRoll() { Show(true); }
        // the same set docked at the right edge with Auto-hide on (an anchored strip when rolled up); the set is made docked once (a set made earlier by DRAWPANES / DRAWPANEROLL keeps its place)
        [CommandMethod("DRAWPANEROLLDOCK")] public void PanesRollDock() { Show(true, dockRight: true); }
        // closes the set without disposing it (a later DRAWPANES... shows it again): for opening it again from a ribbon or Quick Access Toolbar button
        // moves a floating set away from the ribbon buttons (to 700,300), so a button of the Panes panel is not under it
        [CommandMethod("DRAWPANEAWAY")] public void PanesAway() { if (set != null) set.Location = new System.Drawing.Point(700, 300); }
        [CommandMethod("DRAWPANECLOSE")] public void PanesClose() { if (set != null) set.Visible = false; }

        // the tab strip of a small floating set may not show: these bring a page to the front (the way a person clicks its tab)
        [CommandMethod("DRAWPANEHOST")] public void TabHost() { Show(false); set?.Activate(1); }
        [CommandMethod("DRAWPANEWPF")] public void TabWpf() { Show(false); set?.Activate(2); }
        [CommandMethod("DRAWPANEGRID")] public void TabGrid() { Show(false); set?.Activate(3); }

        private static void Show(bool roll, bool dockRight = false)
        {
            if (set == null)
            {
                // built into a local and assigned at the end: a page that fails to build leaves no half-made set behind, and the exception is printed
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                try
                {
                    var s = new PaletteSet("Retrace Panes", new System.Guid("5b8f6a42-3c1d-4e7a-8b90-1d2c3e4f5a61"));
                    s.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton;
                    s.MinimumSize = new System.Drawing.Size(260, 300);
                    s.Size = new System.Drawing.Size(320, 380);
                    s.Add("Canvas", new CanvasControl("PaneCanvas") { Name = "CanvasPane" });
                    s.Add("Host", new HostPage());
                    s.AddVisual("Wpf", new WpfPaneView());
                    s.Add("Grid", new SelectGrid());
                    set = s;
                    doc?.Editor.WriteMessage("\nRetrace Panes: " + s.Count + " pages.");
                }
                catch (System.Exception ex)
                {
                    doc?.Editor.WriteMessage("\nRetrace Panes failed: " + ex);
                    return;
                }
            }
            set.Visible = true;
            if (roll) set.AutoRollUp = true;   // only valid once the set shows
            if (dockRight && set.Dock == DockSides.None) set.Dock = DockSides.Right;   // after the roll-up: docking a fresh set first made AutoRollUp throw ("operation not valid")
        }

        [CommandMethod("DRAWPANEFORM")]
        public void ModelessForm()
        {
            var f = new WinForms.Form { Text = "Retrace Pane Form", Width = 300, Height = 260, StartPosition = WinForms.FormStartPosition.Manual, Location = new System.Drawing.Point(760, 160) };
            f.Controls.Add(new CanvasControl("PaneForm") { Dock = WinForms.DockStyle.Fill });
            f.Controls.Add(new WinForms.Button { Name = "FormDrawButton", Text = "Draw square", Dock = WinForms.DockStyle.Bottom, Height = 34 });
            ((WinForms.Button)f.Controls[1]).Click += (_, _) => Pane.Draw("PaneFormButton", new Point3d(40, 40, 0), 3.0);
            AcApp.ShowModelessDialog(f);
        }

        [CommandMethod("DRAWPANEWIN")]
        public void ModelessWindow()
        {
            var w = new System.Windows.Window { Title = "Retrace Pane Window", Width = 300, Height = 260, Content = new WpfPaneView() };
            AcApp.ShowModelessWindow(w);
        }

        /// <summary>A plain WPF Window shown modally with ShowDialog (not through AutoCAD): a click in it must not be recorded twice, as a pane and as a dialog.</summary>
        [CommandMethod("DRAWPANEDIALOG")]
        public void WpfShowDialog()
        {
            var w = new System.Windows.Window { Title = "Retrace Pane Dialog", Width = 300, Height = 220, Content = new WpfPaneView() };
            w.ShowDialog();
        }

        [CommandMethod("DRAWPANEWPFHOST")]
        public void WpfHostsWinForms()
        {
            var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = new CanvasControl("PaneWpfHost") };
            var w = new System.Windows.Window { Title = "Retrace Pane Wpf Host", Width = 300, Height = 260, Content = host };
            AcApp.ShowModelessWindow(w);
        }

        [CommandMethod("DRAWPANELATE")]
        public void LateSet()
        {
            lateCount++;
            var s = new PaletteSet("Retrace Late " + lateCount, System.Guid.NewGuid());
            s.Style = PaletteSetStyles.ShowCloseButton;
            s.Size = new System.Drawing.Size(280, 240);
            s.Add("Late", new CanvasControl("PaneLate" + lateCount));
            s.Visible = true;
        }
    }

    internal static class Pane
    {
        public static void Draw(string layer, Point3d at, double radius) =>
            SamplePalette.Draw((_, _) => new Circle(at, Vector3d.ZAxis, radius), layer);
    }

    /// <summary>A custom-drawn control: it paints itself and works from OnMouseDown, with no Click event and no child controls (what many third-party panels and lists are).</summary>
    internal class CanvasControl : WinForms.UserControl
    {
        private readonly string layer;
        private System.Drawing.Point last = new(-1, -1);

        public CanvasControl(string layer)
        {
            this.layer = layer;
            DoubleBuffered = true;
            BackColor = System.Drawing.Color.WhiteSmoke;
        }

        protected override void OnPaint(WinForms.PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.DrawString("Click anywhere (" + layer + ")", Font, System.Drawing.Brushes.Black, 8, 8);
            if (last.X >= 0) e.Graphics.FillEllipse(System.Drawing.Brushes.SeaGreen, last.X - 5, last.Y - 5, 10, 10);
        }

        protected override void OnMouseDown(WinForms.MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != WinForms.MouseButtons.Left) return;
            last = e.Location;
            Invalidate();
            Pane.Draw(layer, new Point3d(e.X / 4.0, -e.Y / 4.0, 0), 2.0);
        }
    }

    /// <summary>A WinForms page that hosts WPF through ElementHost.</summary>
    internal class HostPage : WinForms.UserControl
    {
        public HostPage()
        {
            Controls.Add(new System.Windows.Forms.Integration.ElementHost { Dock = WinForms.DockStyle.Fill, Child = new WpfPaneView() });
        }
    }

    /// <summary>A custom-drawn grid of cells that only changes its own selection: no command, no change to the drawing (the click is a step that has no effect to check but the picture).</summary>
    internal class SelectGrid : WinForms.UserControl
    {
        private int selected = -1;
        public SelectGrid() { DoubleBuffered = true; BackColor = System.Drawing.Color.White; }

        protected override void OnPaint(WinForms.PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            for (int i = 0; i < 12; i++)
            {
                var r = new System.Drawing.Rectangle(8 + (i % 3) * 70, 8 + (i / 3) * 30, 64, 24);
                e.Graphics.FillRectangle(i == selected ? System.Drawing.Brushes.LightSkyBlue : System.Drawing.Brushes.Gainsboro, r);
                e.Graphics.DrawString("Cell " + (i + 1), Font, System.Drawing.Brushes.Black, r.X + 4, r.Y + 4);
            }
        }

        protected override void OnMouseDown(WinForms.MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int col = (e.X - 8) / 70, row = (e.Y - 8) / 30;
            selected = col is >= 0 and < 3 && row is >= 0 and < 4 ? row * 3 + col : -1;
            Invalidate();
        }
    }

    /// <summary>WPF content: a custom drawn element (draws a circle), a tree and a multi-select list (selection only), a text box.</summary>
    internal class WpfPaneView : System.Windows.Controls.UserControl
    {
        public WpfPaneView()
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(new WpfCanvasElement { Height = 80, Margin = new Thickness(0, 0, 0, 6) });
            var tree = new System.Windows.Controls.TreeView { Height = 70, Name = "PaneWpfTree" };
            var root = new System.Windows.Controls.TreeViewItem { Header = "Group", IsExpanded = true };
            root.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "One" });
            root.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "Two" });
            tree.Items.Add(root);
            panel.Children.Add(tree);
            var list = new System.Windows.Controls.ListBox { Height = 50, SelectionMode = System.Windows.Controls.SelectionMode.Extended, Margin = new Thickness(0, 6, 0, 6) };
            foreach (var s in new[] { "Red", "Green", "Blue" }) list.Items.Add(s);
            panel.Children.Add(list);
            panel.Children.Add(new System.Windows.Controls.TextBox { Name = "PaneWpfText", Text = "text" });
            Content = panel;
        }
    }

    /// <summary>A custom drawn WPF element: paints a rectangle and draws a circle at a left click (works from OnMouseLeftButtonDown, no button).</summary>
    internal class WpfCanvasElement : FrameworkElement
    {
        private System.Windows.Point? last;
        public WpfCanvasElement() { Focusable = true; }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.WhiteSmoke, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Gray, 1), new Rect(0, 0, ActualWidth, ActualHeight));
            if (last is { } p) dc.DrawEllipse(System.Windows.Media.Brushes.SeaGreen, null, p, 5, 5);
        }

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            last = e.GetPosition(this);
            InvalidateVisual();
            Pane.Draw("PaneWpf", new Point3d(last.Value.X / 4.0, -last.Value.Y / 4.0, 0), 2.0);
        }
    }
}
