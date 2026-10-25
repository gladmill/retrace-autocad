using System.Windows;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>What a click does: types the button's macro (CommandParameter) into the active drawing. "^C" becomes Esc, ";" Enter.</summary>
public sealed class RibbonCommandHandler : System.Windows.Input.ICommand
{
    public bool CanExecute(object? parameter) => true;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public void Execute(object? parameter)
    {
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null || parameter is not RibbonButton button) return;
            var macro = button.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(macro)) macro = "_" + button.Id + " ";
            var text = macro.Replace("^C", "\u001b").Replace(";", "\n");
            doc.SendStringToExecute(text, true, false, false);
        }
        catch (System.Exception ex) { Log.Error("ribbon click", ex); }
    }
}
