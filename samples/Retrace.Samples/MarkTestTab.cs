using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.Samples
{
    /// <summary>Runs the command named in the button's Tag (Esc Esc first). The buttons of <see cref="MarkTestTab"/> have ids that are not command names (custom ids, as a partial CUI gives them).</summary>
    public class TagCommandHandler : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is not RibbonButton { Tag: string command }) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute("\x1B\x1B_" + command + " ", true, false, false);
        }
    }

    /// <summary>
    /// DRAWMARKSTAB: adds the ribbon tabs "Marks Test" and "Marks Other" (not at start-up; the docs scan does not list them) for checking the green box and arrow of a ribbon click's picture:
    /// a button whose command opens a palette at once (DRAWPICK, DRAWTOOLS), a button with a custom id, a split button with two items, a panel with a slide-out, and one id used on two tabs
    /// (the first of them is "Marks Other", which comes first in the ribbon, so a lookup by id finds that one, not the one that was clicked).
    /// </summary>
    public class MarkTestTab
    {
        [CommandMethod("DRAWMARKSTAB")]
        public void Create()
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ribbon.Tabs.Any(t => t.Id == "MARKS_TEST")) return;
            ribbon.Tabs.Add(Tab("MARKS_OTHER", "Marks Other", Panel("Other", Row(Button("RBNU_200_DUPLI", "Dup A", "DRAWCIRCLE")))));
            ribbon.Tabs.Add(Tab("MARKS_TEST", "Marks Test",
                Panel("Palettes", Row(Button("MARK_PICK", "Pick palette", "DRAWPICK"), Button("RBNU_200_ABCDE", "Tools palette", "DRAWTOOLS"), Button("RBNU_200_DUPLI", "Dup B", "DRAWLINE"))),
                Panel("Split", Row(Split())),
                SlidePanel()));
        }

        static RibbonTab Tab(string id, string title, params RibbonPanel[] panels)
        {
            var tab = new RibbonTab { Id = id, Title = title, Name = title };
            foreach (var p in panels) tab.Panels.Add(p);
            return tab;
        }

        static RibbonPanel Panel(string title, RibbonRowPanel row)
        {
            var src = new RibbonPanelSource { Title = title };
            src.Items.Add(row);
            return new RibbonPanel { Source = src };
        }

        static RibbonRowPanel Row(params RibbonItem[] items)
        {
            var row = new RibbonRowPanel();
            foreach (var i in items) row.Items.Add(i);
            return row;
        }

        static RibbonPanel SlidePanel()
        {
            var src = new RibbonPanelSource { Title = "Slide" };
            src.Items.Add(Row(Button("MARK_SLIDE_RECT", "Rectangle", "DRAWRECT")));
            src.Items.Add(new RibbonPanelBreak());   // what follows is in the panel's slide-out
            src.Items.Add(Row(Button("MARK_SLIDE_LABEL", "Label", "DRAWLABEL")));
            return new RibbonPanel { Source = src };
        }

        static RibbonSplitButton Split()
        {
            var split = new RibbonSplitButton { Id = "MARK_SPLIT", Name = "Split", Text = "Draw", ShowText = true, Size = RibbonItemSize.Standard, Orientation = System.Windows.Controls.Orientation.Horizontal };
            split.Items.Add(Button("MARK_SPLIT_LINE", "Line", "DRAWLINE"));
            split.Items.Add(Button("MARK_SPLIT_CIRCLE", "Circle", "DRAWCIRCLE"));
            return split;
        }

        /// <summary>A button with its own id whose click runs <paramref name="command"/>.</summary>
        static RibbonButton Button(string id, string label, string command)
        {
            var b = new RibbonButton { Id = id, Name = id, Text = label, ShowText = true, Size = RibbonItemSize.Standard, Orientation = System.Windows.Controls.Orientation.Horizontal, Tag = command };
            b.CommandHandler = new TagCommandHandler();
            b.CommandParameter = b;
            return b;
        }
    }
}
