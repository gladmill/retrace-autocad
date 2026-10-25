using System.Windows.Input;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.Windows;

namespace Retrace.Samples
{
    /// <summary>Runs the ribbon button's Id as a command: Esc Esc _DRAWLINE (the "^C^C" of a CUI macro means nothing to SendStringToExecute).</summary>
    public class SendCommandHandler : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is not RibbonButton button) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute("\x1B\x1B_" + button.Id + " ", true, false, false);
        }
    }

    /// <summary>
    /// The "Retrace Samples" ribbon tab: panel "Draw" (4 buttons in two rows) and panel "Dialogs" (1 button).
    /// Written in the plain shape Retrace's code-ribbon scanner reads, so Build docs on this folder lists the tab
    /// (CommandNamesTests checks that it still does).
    /// </summary>
    public static class SamplesTab
    {
        /// <summary>The tab's Id.</summary>
        public const string TabId = "SAMPLES_TAB";

        /// <summary>Adds the tab to the ribbon unless it is already there.</summary>
        public static void Create()
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ribbon.Tabs.Any(t => t.Id == TabId)) return;

            var tab = new RibbonTab();
            tab.Id = TabId;
            tab.Title = "Retrace Samples";
            tab.Name = "Retrace Samples";
            tab.Panels.Add(DrawPanel());
            tab.Panels.Add(DialogsPanel());
            ribbon.Tabs.Add(tab);
        }

        static RibbonPanel DrawPanel()
        {
            var lines = new RibbonRowPanel();
            lines.Items.Add(Button("DRAWLINE", "Line", "Draws a line. Pick the start point, then the end point."));
            lines.Items.Add(Button("DRAWCIRCLE", "Circle", "Draws a circle. Pick the center, then drag or type the radius."));
            var shapes = new RibbonRowPanel();
            shapes.Items.Add(Button("DRAWRECT", "Rectangle", "Draws a rectangle. Pick two opposite corners."));
            shapes.Items.Add(Button("DRAWLABEL", "Label", "Adds a text label. Type the text, pick the position, then enter the height."));

            var src = new RibbonPanelSource();
            src.Title = "Draw";
            src.Items.Add(lines);
            src.Items.Add(new RibbonRowBreak());
            src.Items.Add(shapes);
            var panel = new RibbonPanel();
            panel.Source = src;
            return panel;
        }

        static RibbonPanel DialogsPanel()
        {
            var only = new RibbonRowPanel();
            only.Items.Add(Button("DRAWGRID", "Grid...", "Opens a dialog where you set rows, columns, spacing and layer, then draws a grid of points."));

            var src = new RibbonPanelSource();
            src.Title = "Dialogs";
            src.Items.Add(only);
            var panel = new RibbonPanel();
            panel.Source = src;
            return panel;
        }

        /// <summary>A small button with its label. A click runs its Id as a command (see <see cref="SendCommandHandler"/>).</summary>
        static RibbonButton Button(string command, string label, string tip)
        {
            var b = new RibbonButton();
            b.Id = command;
            b.Name = command;
            b.Text = label;
            b.ToolTip = tip;
            b.ShowText = true;
            b.Size = RibbonItemSize.Standard;
            b.Orientation = System.Windows.Controls.Orientation.Horizontal;
            b.CommandHandler = new SendCommandHandler();
            b.CommandParameter = b;
            return b;
        }
    }
}
