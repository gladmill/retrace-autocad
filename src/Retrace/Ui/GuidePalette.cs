using Autodesk.AutoCAD.Windows;
using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>The dockable "Retrace Guide" PaletteSet. One instance; the fixed Guid lets AutoCAD remember size and dock state.</summary>
static class GuidePalette
{
    static readonly Guid PaletteGuid = new("D462662C-730D-409D-8B9F-E0EAEA3CD9DC");
    static PaletteSet? _set;
    static GuideControl? _control;

    /// <summary>The palette set's title. The recorder never reads this palette (AutoCad/PaletteWatch).</summary>
    public const string SetTitle = "Retrace Guide";

    public static void Show()
    {
        if (_set == null)
        {
            _control = new GuideControl();
            _set = new PaletteSet(SetTitle, PaletteGuid)
            {
                Style = PaletteSetStyles.ShowPropertiesMenu | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton,
                DockEnabled = DockSides.Left | DockSides.Right | DockSides.None,
                MinimumSize = new System.Drawing.Size(420, 300),
                Size = new System.Drawing.Size(900, 640),
            };
            _set.AddVisual("Guide", _control);
            _set.PaletteActivated += (_, _) => _control?.OnPaletteActivated();
        }
        _set.Visible = true;
        _control!.RefreshTree();
    }

    public static void Dispose()
    {
        try { _control?.ConfirmShutdown(); } catch (Exception) { }
        _control = null;
        try { _set?.Close(); } catch (Exception) { }
        _set = null;
    }
}
