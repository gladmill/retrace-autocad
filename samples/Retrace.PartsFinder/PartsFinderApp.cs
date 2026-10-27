using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Retrace.PartsFinder.PartsFinderApp))]
[assembly: CommandClass(typeof(Retrace.PartsFinder.PartsFinderCommands))]

namespace Retrace.PartsFinder
{
    /// <summary>Loads with AutoCAD and adds the "Parts" ribbon tab as soon as the ribbon exists.</summary>
    public class PartsFinderApp : IExtensionApplication
    {
        public void Initialize()
        {
            if (ComponentManager.Ribbon != null) { PartsTab.Create(); return; }
            ComponentManager.ItemInitialized += OnItemInitialized;
        }

        public void Terminate() => ComponentManager.ItemInitialized -= OnItemInitialized;

        private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            PartsTab.Create();
        }
    }

    /// <summary>Runs the ribbon button's Id as a command (Esc Esc first, like a ^C^C macro).</summary>
    public class RunCommandHandler : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (parameter is not RibbonButton b) return;
            Application.DocumentManager.MdiActiveDocument?.SendStringToExecute("\x1B\x1B_" + b.Id + " ", true, false, false);
        }
    }

    /// <summary>The "Parts" ribbon tab: panel "Finder" with the two ways to open the palette.</summary>
    public static class PartsTab
    {
        public const string TabId = "PARTS_TAB";

        public static void Create()
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ribbon.Tabs.Any(t => t.Id == TabId)) return;
            var tab = new RibbonTab();
            tab.Id = TabId;
            tab.Title = "Parts";
            tab.Name = "Parts";
            var src = new RibbonPanelSource();
            src.Title = "Finder";
            src.Items.Add(Button("PARTSFINDER", "Parts finder", "Opens the Parts Finder palette floating, with auto roll-up: it rolls up to its title bar when the mouse leaves it."));
            src.Items.Add(Button("PARTSFINDERWPF", "WPF finder", "Opens the WPF Parts Finder palette (view model, bindings, commands), floating with auto roll-up."));
            src.Items.Add(Button("PARTSFINDERDOCK", "Docked finder", "Opens the Parts Finder palette docked on the left with auto-hide: it shrinks to a tab and slides out when the mouse is over it."));
            var panel = new RibbonPanel();
            panel.Source = src;
            tab.Panels.Add(panel);
            ribbon.Tabs.Add(tab);
        }

        static RibbonButton Button(string command, string label, string tip)
        {
            var b = new RibbonButton();
            b.Id = command;
            b.Name = command;
            b.Text = label;
            b.ToolTip = tip;
            b.ShowText = true;
            b.Size = RibbonItemSize.Large;
            b.Orientation = System.Windows.Controls.Orientation.Vertical;
            b.CommandHandler = new RunCommandHandler();
            b.CommandParameter = b;
            return b;
        }
    }

    public class PartsFinderCommands
    {
        [CommandMethod("PARTSFINDER")]
        public void Floating() => FinderPalette.Show(docked: false);

        [CommandMethod("PARTSFINDERWPF")]
        public void Wpf() => WpfFinder.Show();

        [CommandMethod("PARTSFINDERDOCK")]
        public void Docked() => FinderPalette.Show(docked: true);

        /// <summary>What the palette's "Place..." button runs: asks for a point and puts the selected part's label there.</summary>
        [CommandMethod("PARTSPLACE")]
        public void Place()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var part = FinderPalette.Selected ?? WpfFinder.Selected;
            if (part == null) { doc.Editor.WriteMessage("\nSelect a part in the Parts Finder first."); return; }
            var r = doc.Editor.GetPoint($"\nPosition for {part.Title}: ");
            if (r.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
            Drawing.AddLabel(doc, part, r.Value);
        }
    }
}
