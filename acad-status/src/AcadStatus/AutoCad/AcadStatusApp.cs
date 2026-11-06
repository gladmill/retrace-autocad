using System;
using Autodesk.AutoCAD.Runtime;
using AcadStatus.AutoCad;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AcadStatusApp))]
[assembly: CommandClass(typeof(AcadStatusApp))]

namespace AcadStatus.AutoCad;

/// <summary>The one entry point AutoCAD loads: starts the status service, stops it, and owns the two commands.</summary>
public sealed class AcadStatusApp : IExtensionApplication
{
    public void Initialize()
    {
        try
        {
            var service = StatusService.Start();
            service.StartPaneClicks();
        }
        catch (System.Exception ex) { Log.Error("Initialize", ex); }
    }

    public void Terminate()
    {
        try { StatusService.Current?.Dispose(); }
        catch (System.Exception ex) { Log.Error("Terminate", ex); }
    }

    /// <summary>ACADSTATUS: prints the same dump as the details window's "Copy for Claude" button to the command line.</summary>
    [CommandMethod("ACADSTATUS", CommandFlags.Transparent)]
    public static void Status()
    {
        Run("ACADSTATUS", () =>
        {
            var s = StatusService.Current;
            Print(s == null ? "AcadStatus is not running." : s.DumpText());
        });
    }

    /// <summary>ACADSTATUSPANE: shows or hides the status bar pane (the writer keeps running).</summary>
    [CommandMethod("ACADSTATUSPANE", CommandFlags.Transparent)]
    public static void Pane()
    {
        Run("ACADSTATUSPANE", () =>
        {
            var s = StatusService.Current;
            Print(s == null ? "AcadStatus is not running." : "AcadStatus pane: " + (s.TogglePane() ? "on" : "off"));
        });
    }

    static void Run(string name, Action body)
    {
        try { body(); }
        catch (System.Exception ex) { Log.Error(name, ex); Print(name + " failed: " + ex.Message); }
    }

    static void Print(string text)
    {
        try
        {
            // a format argument, never the text itself as the format: the dump holds JSON braces
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n{0}\n", text.Replace("\r\n", "\n").TrimEnd());
        }
        catch (System.Exception ex) { Log.Error("print", ex); }
    }
}
