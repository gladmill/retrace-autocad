// Shared model. Every scanner fills these types; the emitters only read them.
// Scanners must not add fields here: put anything extra in an `Extra` dictionary.
using System.Text.Json.Serialization;

namespace Retrace.Engine;

/// <summary>A position in the input. File is relative to the input root, '/'-separated.
/// Entries inside a .cuix use "acad.CUIX!RibbonRoot.cui".</summary>
public sealed record SourceRef(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

public enum Lang { CSharp, VbNet, Cpp, Xaml, Rc, Dcl, Cuix, Other }

/// <summary>One input file, already read. Text is null for binary/unread files.</summary>
public sealed class SourceFile
{
    public required string AbsPath { get; init; }
    public required string RelPath { get; init; }
    public required Lang Lang { get; init; }
    /// <summary>Name of the owning project (nearest .csproj/.vbproj/.vcxproj folder, else top folder).</summary>
    public required string Project { get; init; }
    public string Text { get; init; } = "";
}

public sealed class SourceProject
{
    public required string Name { get; init; }
    /// <summary>Folder relative to the input root.</summary>
    public required string Dir { get; init; }
    public string? ProjectFile { get; init; }
    public Lang Lang { get; set; }
    public List<string> Files { get; } = new();
}

// ---------------------------------------------------------------- ribbon

public enum RibbonOrigin { Cuix, Code }

public enum RibbonItemKind
{
    Button, SplitButton, ToggleButton, Control, Gallery, DropDown, Separator, RowPanel, Label, Other
}

public enum RibbonItemSize { Small, Standard, Large }

public sealed class RibbonTab
{
    /// <summary>Stable id: CUIX UID (e.g. "ID_TabHome") or code Id/Name.</summary>
    public required string Id { get; init; }
    public required string Title { get; set; }
    public RibbonOrigin Origin { get; init; }
    /// <summary>For CUIX: the .cuix file name. For code: the project name.</summary>
    public string Container { get; init; } = "";
    public SourceRef? Source { get; set; }
    public string? DisplayName { get; set; }
    public string? KeyTip { get; set; }
    /// <summary>Contextual tab rule (e.g. "AcTableCellSelected"), null for normal tabs.</summary>
    public string? ContextualRule { get; set; }
    /// <summary>Workspaces that show this tab (CUIX only).</summary>
    public List<string> Workspaces { get; } = new();
    public List<RibbonPanel> Panels { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

public sealed class RibbonPanel
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public SourceRef? Source { get; set; }
    /// <summary>Command id / macro behind the panel's dialog-box launcher arrow, if any.</summary>
    public string? DialogLauncherCommand { get; set; }
    public List<RibbonRow> Rows { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

public sealed class RibbonRow
{
    /// <summary>True for rows after a RibbonPanelBreak (only visible in the slide-out).</summary>
    public bool IsSlideout { get; set; }
    public List<RibbonItem> Items { get; } = new();
}

public sealed class RibbonItem
{
    public required string Id { get; init; }
    public RibbonItemKind Kind { get; set; }
    public string Text { get; set; } = "";
    public RibbonItemSize Size { get; set; } = RibbonItemSize.Standard;
    public bool ShowText { get; set; } = true;
    /// <summary>Icon reference as written in the source (resource id, file, property name).</summary>
    public string? Image { get; set; }
    public string? Tooltip { get; set; }
    public string? KeyTip { get; set; }
    /// <summary>CUIX MenuMacroID / code button id.</summary>
    public string? MacroId { get; set; }
    /// <summary>Raw macro string, e.g. "^C^C_mtext".</summary>
    public string? Macro { get; set; }
    /// <summary>Resolved command-line command name, upper case, e.g. "MTEXT". Null if unknown.</summary>
    public string? Command { get; set; }
    /// <summary>For code buttons routed to a method rather than a command: "Namespace.Class.Method".</summary>
    public string? HandlerMethod { get; set; }
    public SourceRef? Source { get; set; }
    /// <summary>Split-button / drop-down members.</summary>
    public List<RibbonItem> Children { get; } = new();
    /// <summary>Nested rows of a RowPanel.</summary>
    public List<RibbonRow> SubRows { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

/// <summary>A CUIX menu macro (MenuGroup.cui MenuMacro).</summary>
public sealed class MacroDef
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string Macro { get; set; } = "";
    public string? Command { get; set; }
    public string? HelpString { get; set; }
    public string? SmallImage { get; set; }
    public string? LargeImage { get; set; }
}

public sealed class CuixModel
{
    public required string File { get; init; }
    public string MenuGroup { get; set; } = "";
    public List<RibbonTab> Tabs { get; } = new();
    public Dictionary<string, MacroDef> Macros { get; } = new();
    public List<string> Workspaces { get; } = new();
    public List<string> PartialMenus { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

// ---------------------------------------------------------------- commands and flows

public enum EntryKind
{
    /// <summary>.NET [CommandMethod] / &lt;CommandMethod&gt;.</summary>
    CommandMethod,
    /// <summary>.NET [LispFunction].</summary>
    LispFunction,
    /// <summary>ObjectARX acedRegCmds->addCommand / cmdStack->addCommand.</summary>
    ArxAddCommand,
    /// <summary>ObjectARX ACED_ARXCOMMAND_ENTRY_AUTO macros.</summary>
    ArxCommandEntry,
    /// <summary>A method a ribbon button calls directly (e.g. through a handler object that names the class and method), not a registered command.</summary>
    RibbonHandler,
}

public enum FlowKind
{
    /// <summary>Command-line prompt: GetPoint, GetEntity, acedGetString ... Text is the prompt.</summary>
    Prompt,
    /// <summary>Selection set prompt: GetSelection, acedSSGet.</summary>
    Selection,
    /// <summary>Modal dialog shown: ShowModalDialog, DoModal, ShowDialog. Target is the form class / dialog id.</summary>
    Dialog,
    /// <summary>Modeless window / palette set shown. Target is the class.</summary>
    Palette,
    /// <summary>Jig / drag: Editor.Drag, acedDragGen.</summary>
    Jig,
    /// <summary>Message to the user: WriteMessage, MessageBox, acutPrintf, alert.</summary>
    Message,
    /// <summary>Another command sent: SendStringToExecute, acedCommand, Editor.Command.</summary>
    SendCommand,
    /// <summary>Call into another method of the scanned code (followed up to the depth limit).</summary>
    Call,
}

public sealed class FlowStep
{
    public FlowKind Kind { get; init; }
    /// <summary>Human text: the prompt string, message, called method name, ...</summary>
    public string Text { get; init; } = "";
    /// <summary>For Dialog/Palette: the form class or dialog resource id. For Call: "Class.Method". For SendCommand: the command.</summary>
    public string? Target { get; init; }
    public SourceRef? Source { get; init; }
    /// <summary>0 for steps in the entry method itself, +1 per followed call.</summary>
    public int Depth { get; init; }
    /// <summary>Filled by the linker: id of the UiForm this step opens.</summary>
    public string? UiFormId { get; set; }
    public Dictionary<string, string> Extra { get; } = new();
}

public sealed class CommandEntry
{
    /// <summary>Command name as typed at the command line (global name), upper case for ARX/.NET commands.</summary>
    public required string Name { get; init; }
    public string? LocalName { get; set; }
    public string? Group { get; set; }
    public string Flags { get; set; } = "";
    public EntryKind Kind { get; init; }
    public Lang Lang { get; init; }
    public required string Project { get; init; }
    public required SourceRef Source { get; init; }
    /// <summary>"Namespace.Class.Method" (C#/VB) or the C++ function name.</summary>
    public required string Method { get; init; }
    public List<FlowStep> Flow { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

// ---------------------------------------------------------------- forms / dialogs

public enum UiFormKind { WinForm, Win32Dialog, Xaml, Dcl, PaletteSet }

public sealed class UiForm
{
    /// <summary>Stable id, e.g. "winform.Contoso.Tools.Forms.FormOptions",
    /// "rc.ArxDbg.ARXDBG_DLG_DXF", "dcl.acad.ddim", "xaml.Contoso.Tools.Views.TrellisView".</summary>
    public required string Id { get; init; }
    public required string Title { get; set; }
    public UiFormKind Kind { get; init; }
    public required string Project { get; init; }
    public required SourceRef Source { get; init; }
    /// <summary>Full class name for WinForms/XAML/palette sets; C++ dialog classes bound to this resource.</summary>
    public List<string> ClassNames { get; } = new();
    /// <summary>Resource id for Win32 dialogs, dialog name for DCL.</summary>
    public string? ResourceId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>"px", "dlu" (dialog units) or "auto" (layout-driven: XAML grid / DCL).</summary>
    public string Units { get; set; } = "px";
    public List<UiControl> Controls { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

public sealed class UiControl
{
    public string Name { get; set; } = "";
    /// <summary>Control type as in source: Button, TextBox, PUSHBUTTON, edit_box, DataGrid ...</summary>
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }
    /// <summary>Event handlers wired to this control, e.g. "Click -> buttonOk_Click".</summary>
    public List<string> Events { get; } = new();
    public SourceRef? Source { get; set; }
    public List<UiControl> Children { get; } = new();
    public Dictionary<string, string> Extra { get; } = new();
}

// ---------------------------------------------------------------- whole run

public sealed class EngineModel
{
    public required string InputRoot { get; init; }
    public DateTimeOffset Generated { get; init; } = DateTimeOffset.Now;
    public List<SourceProject> Projects { get; } = new();
    public List<CuixModel> Cuix { get; } = new();
    /// <summary>Ribbon tabs built in code (C#/VB).</summary>
    public List<RibbonTab> CodeTabs { get; } = new();
    public List<CommandEntry> Commands { get; } = new();
    public List<UiForm> Forms { get; } = new();
    /// <summary>Input files deliberately not read (secrets, binaries), relative paths.</summary>
    public List<string> Skipped { get; } = new();
    /// <summary>Non-fatal problems: parse failures etc.</summary>
    public List<string> Warnings { get; } = new();

    [JsonIgnore]
    public List<SourceFile> Files { get; } = new();
}
