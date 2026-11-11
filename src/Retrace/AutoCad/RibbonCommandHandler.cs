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
            if (RibbonPrompts.IsPaired(button.Id))   // the Prompts toggle: Settings, Training doc, Map and Stop run their command-line form
                macro = RibbonPrompts.Macro(button.Id, macro, Ask.UiSetting() ? PromptStyles.Ui : PromptStyles.CommandLine);
            var text = macro.Replace("^C", "\u001b").Replace(";", "\n");
            StatusBoardHost.UserClicked(macro);   // a person clicked: the run this starts is the user's (status board owner)
            SdiDocs.Send(doc, text, true, false, false);
        }
        catch (System.Exception ex) { Log.Error("ribbon click", ex); }
    }
}

/// <summary>The Prompts toggle click: flips promptStyle, saves settings.json and shows the new state.</summary>
public sealed class PromptToggleHandler : System.Windows.Input.ICommand
{
    public bool CanExecute(object? parameter) => true;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public void Execute(object? parameter)
    {
        try
        {
            // the button flipped itself on the click: every refusal below puts it back to what it showed
            if ((Ask.ActiveBits() ?? 0) != 0)
            {
                Commands.Print("Prompts: a command is running; finish or cancel it first. Nothing changed.");
                RibbonInstaller.ShowPromptStyle(RibbonInstaller.ShownPromptStyle);
                return;
            }
            if (!RetraceSettings.TryLoad(out var s))
            {
                Commands.Print("Prompts: settings.json could not be read, so it was not changed. Fix or delete " + Paths.SettingsFile() + ".");
                RibbonInstaller.ShowPromptStyle(RibbonInstaller.ShownPromptStyle);
                return;
            }
            s.PromptStyle = RibbonPrompts.Flip(s.PromptStyle);
            if (!s.Save(out var why))
            {
                Commands.Print("Prompts: could not save settings.json: " + why);
                RibbonInstaller.ShowPromptStyle(RibbonInstaller.ShownPromptStyle);
                return;
            }
            RibbonInstaller.RefreshFromSettings(true);
        }
        catch (System.Exception ex) { Log.Error("prompts toggle", ex); }
    }
}
