using Retrace.Engine.Scan.Forms;

namespace Retrace.Tests;

public class FormScannerTests
{
    internal static SourceFile Src(string rel, Lang lang, string text, string project = "Proj") =>
        new() { AbsPath = "/x/" + rel, RelPath = rel, Lang = lang, Project = project, Text = text };

    private static UiControl Find(IEnumerable<UiControl> cs, string name)
    {
        foreach (var c in cs)
        {
            if (c.Name == name) return c;
            try { return Find(c.Children, name); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("no control " + name);
    }

    internal const string CsDesigner = @"
namespace Demo.Forms
{
    partial class ExportForm
    {
        private void InitializeComponent()
        {
            this.grpOutput = new System.Windows.Forms.GroupBox();
            this.lblSuffix = new System.Windows.Forms.Label();
            this.btnRun = new System.Windows.Forms.Button();
            this.components = new System.ComponentModel.Container();
            this.grpOutput.SuspendLayout();
            this.SuspendLayout();
            // grpOutput
            this.grpOutput.Controls.Add(this.lblSuffix);
            this.grpOutput.Location = new System.Drawing.Point(16, 10);
            this.grpOutput.Name = ""grpOutput"";
            this.grpOutput.Size = new System.Drawing.Size(210, 90);
            this.grpOutput.Text = ""Output Folder"";
            // lblSuffix
            this.lblSuffix.Location = new System.Drawing.Point(8, 30);
            this.lblSuffix.Size = new System.Drawing.Size(48, 16);
            this.lblSuffix.Text = ""Suffix"";
            // btnRun
            this.btnRun.Location = new System.Drawing.Point(150, 120);
            this.btnRun.Size = new System.Drawing.Size(80, 24);
            this.btnRun.Text = ""Run"";
            this.btnRun.Click += new System.EventHandler(this.btnRun_Click);
            this.ClientSize = new System.Drawing.Size(260, 160);
            this.Controls.Add(this.grpOutput);
            this.Controls.Add(this.btnRun);
            this.Name = ""ExportForm"";
            this.Text = ""Export"";
        }
        private System.Windows.Forms.Label lblSuffix;
    }
}";

    [Fact]
    public void WinFormsDesignerWithGroupChildAndEvent()
    {
        var forms = FormScanner.Scan(new[] { Src("Forms/ExportForm.Designer.cs", Lang.CSharp, CsDesigner) });
        var f = Assert.Single(forms);
        Assert.Equal("winform.Demo.Forms.ExportForm", f.Id);
        Assert.Equal(UiFormKind.WinForm, f.Kind);
        Assert.Equal("Export", f.Title);
        Assert.Equal(new[] { "Demo.Forms.ExportForm" }, f.ClassNames);
        Assert.Equal((260, 160), (f.Width, f.Height));
        Assert.Equal("px", f.Units);
        Assert.Equal(2, f.Controls.Count);
        var group = Assert.Single(f.Controls, c => c.Name == "grpOutput");
        Assert.Equal("GroupBox", group.Type);
        var label = Assert.Single(group.Children);
        Assert.Equal(("Label", "Suffix", 8, 30), (label.Type, label.Text, label.X, label.Y));
        var ok = Find(f.Controls, "btnRun");
        Assert.Equal("Click -> btnRun_Click", Assert.Single(ok.Events));
        Assert.Equal("Forms/ExportForm.Designer.cs", ok.Source!.File);
        Assert.DoesNotContain(f.Controls, c => c.Type == "Container");
    }

    [Fact]
    public void VbDesignerWithAddRangeAndHandlers()
    {
        const string vb = @"
Namespace Demo
  Public Class NoteForm
    Inherits System.Windows.Forms.Form
    Friend WithEvents NoteLabel1 As System.Windows.Forms.Label
    Private Sub InitializeComponent()
        Me.NoteLabel1 = New System.Windows.Forms.Label()
        Me.CloseButton = New System.Windows.Forms.Button()
        Me.SuspendLayout()
        Me.NoteLabel1.Location = New System.Drawing.Point(12, 20)
        Me.NoteLabel1.Size = New System.Drawing.Size(240, 18)
        Me.NoteLabel1.Text = ""Sample note""
        Me.CloseButton.Location = New System.Drawing.Point(150, 64)
        Me.CloseButton.Text = ""Close""
        AddHandler Me.CloseButton.Click, AddressOf Me.CloseButton_Click
        Me.ClientSize = New System.Drawing.Size(320, 110)
        Me.Controls.AddRange(New System.Windows.Forms.Control() {Me.CloseButton, Me.NoteLabel1})
        Me.Text = ""Note viewer""
    End Sub
    Private Sub NoteLabel1_Click(sender As Object, e As EventArgs) Handles NoteLabel1.Click
    End Sub
  End Class
End Namespace";
        var f = Assert.Single(FormScanner.Scan(new[] { Src("NoteForm.vb", Lang.VbNet, vb) }));
        Assert.Equal("winform.Demo.NoteForm", f.Id);
        Assert.Equal("Note viewer", f.Title);
        Assert.Equal(320, f.Width);
        var ok = Find(f.Controls, "CloseButton");
        Assert.Equal(("Button", 150, 64), (ok.Type, ok.X, ok.Y));
        Assert.Equal("Click -> CloseButton_Click", Assert.Single(ok.Events));
        var lbl = Find(f.Controls, "NoteLabel1");
        Assert.Equal("Click -> NoteLabel1_Click", Assert.Single(lbl.Events));
        Assert.Equal(240, lbl.W);
    }

    internal const string Rc = @"
#include ""resource.h""
#ifdef APSTUDIO_INVOKED
#endif
IDD_DEMO DIALOGEX DISCARDABLE  0, 0, 200, 100
STYLE DS_MODALFRAME | WS_POPUP | WS_CAPTION | WS_SYSMENU
CAPTION ""Demo dialog""
FONT 8, ""MS Shell Dlg"", 400, 0, 0x1
BEGIN
    DEFPUSHBUTTON   ""&Close"",IDCANCEL,140,80,50,14
    LTEXT           ""Name:"",IDC_STATIC,6,6,30,8
    EDITTEXT        IDC_NAME,40,4,100,13,ES_AUTOHSCROLL
    COMBOBOX        IDC_KIND,40,20,100,60,CBS_DROPDOWNLIST | WS_VSCROLL
    CONTROL         ""Auto"",IDC_AUTO,""Button"",BS_AUTOCHECKBOX |
                    WS_TABSTOP,6,40,60,10
    CONTROL         ""List1"",IDC_LIST,""SysListView32"",LVS_REPORT |
                    WS_BORDER,6,54,120,30
    GROUPBOX        ""Options"",IDC_STATIC,4,36,150,52
END
IDD_OTHER DIALOG 0, 0, 100, 50
CAPTION ""Other""
{
    PUSHBUTTON ""Run"",IDOK,10,10,40,14
}";

    internal const string Cpp = @"
class CDemoDlg : public CAcUiDialog
{
public:
    enum { IDD = IDD_DEMO };
};
CDemoDlg::CDemoDlg(CWnd* pParent)
    : CAcUiDialog(CDemoDlg::IDD, pParent, hInst)
{
}
CThing::CThing() : CDialog(IDD_OTHER, NULL) {}
";

    [Fact]
    public void RcDialogWithWrappedControlsAndCaption()
    {
        var forms = FormScanner.Scan(new[] { Src("res/demo.rc", Lang.Rc, Rc, "Demo") });
        Assert.Equal(2, forms.Count);
        var f = forms[0];
        Assert.Equal("rc.Demo.IDD_DEMO", f.Id);
        Assert.Equal(UiFormKind.Win32Dialog, f.Kind);
        Assert.Equal("Demo dialog", f.Title);
        Assert.Equal((200, 100, "dlu", "IDD_DEMO"), (f.Width, f.Height, f.Units, f.ResourceId));
        Assert.Equal(7, f.Controls.Count);
        var close = Assert.Single(f.Controls, c => c.Name == "IDCANCEL");
        Assert.Equal(("DEFPUSHBUTTON", "&Close", 140, 80, 50, 14), (close.Type, close.Text, close.X, close.Y, close.W, close.H));
        Assert.Equal("EDITTEXT", Assert.Single(f.Controls, c => c.Name == "IDC_NAME").Type);
        var check = Assert.Single(f.Controls, c => c.Name == "IDC_AUTO");
        Assert.Equal(("CHECKBOX", "Auto", 6, 40), (check.Type, check.Text, check.X, check.Y));
        var list = Assert.Single(f.Controls, c => c.Name == "IDC_LIST");
        Assert.Equal(("ListView", 6, 54, 120, 30), (list.Type, list.X, list.Y, list.W, list.H));
        Assert.Equal("GroupBox".ToUpperInvariant(), Assert.Single(f.Controls, c => c.Type == "GROUPBOX").Type);
        Assert.Equal("Other", forms[1].Title);
        Assert.Single(forms[1].Controls);
    }

    [Fact]
    public void RcDialogsGetCppClassBinding()
    {
        var forms = FormScanner.Scan(new[]
        {
            Src("res/demo.rc", Lang.Rc, Rc, "Demo"),
            Src("src/demo.h", Lang.Cpp, Cpp, "Demo"),
        });
        Assert.Equal(new[] { "CDemoDlg" }, forms[0].ClassNames);
        Assert.Equal(new[] { "CThing" }, forms[1].ClassNames);
    }

    [Fact]
    public void DuplicateIdsGetSuffix()
    {
        var rc = "IDD_A DIALOG 0,0,10,10\nBEGIN\nEND\nIDD_A DIALOG 0,0,10,10\nBEGIN\nEND\n";
        var forms = FormScanner.Scan(new[] { Src("a.rc", Lang.Rc, rc) });
        Assert.Equal(new[] { "rc.Proj.IDD_A", "rc.Proj.IDD_A.2" }, forms.Select(f => f.Id).ToArray());
    }

    internal const string Xaml = @"<UserControl x:Class=""Demo.Views.MainView""
 xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
 xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
 d:DesignWidth=""500"" d:DesignHeight=""300"">
  <UserControl.Resources><Style x:Key=""s""/></UserControl.Resources>
  <Grid>
    <Grid.RowDefinitions><RowDefinition Height=""Auto""/><RowDefinition/></Grid.RowDefinitions>
    <Label x:Name=""lbl"" Grid.Row=""0"" Grid.Column=""0"" Content=""Name""/>
    <TextBox Grid.Row=""0"" Grid.Column=""1"" Text=""{Binding UserName}""/>
    <Button x:Name=""go"" Grid.Row=""1"" Content=""Go"" Click=""Go_Click"" Command=""{Binding RunCommand}""/>
  </Grid>
</UserControl>";

    [Fact]
    public void XamlGridButtonAndBinding()
    {
        var f = Assert.Single(FormScanner.Scan(new[] { Src("Views/MainView.xaml", Lang.Xaml, Xaml) }));
        Assert.Equal("xaml.Demo.Views.MainView", f.Id);
        Assert.Equal(("MainView", "auto", 500, 300), (f.Title, f.Units, f.Width, f.Height));
        var grid = Assert.Single(f.Controls);
        Assert.Equal("Grid", grid.Type);
        Assert.Equal(3, grid.Children.Count);
        var tb = grid.Children[1];
        Assert.Equal("{Binding UserName}", tb.Text);
        Assert.Equal("0,1", tb.Extra["grid"]);
        var go = Find(f.Controls, "go");
        Assert.Equal("Click -> Go_Click", Assert.Single(go.Events));
        Assert.Equal("{Binding RunCommand}", go.Extra["command"]);
        Assert.Equal("1,0", go.Extra["grid"]);
    }

    internal const string Dcl = @"
// comment
my_button : button { width = 12; fixed_width = true; }
sample : dialog {
    label = ""Sample Dialog"";
    : row {
        : column {
            : boxed_column {
                label = ""Modes"";
                : toggle { key = ""ortho""; label = ""&Ortho""; }
                : edit_box { key = ""e1""; label = ""Value""; edit_width = 8; }
            }
        }
        : my_button { key = ""b1""; label = ""Mine""; }
    }
    /* block */
    spacer;
    ok_cancel;
}
other : dialog { : text { label = ""x""; } }
";

    [Fact]
    public void DclNestedTilesPrototypeAndOkCancel()
    {
        var forms = FormScanner.Scan(new[] { Src("acad.dcl", Lang.Dcl, Dcl, "(root)") });
        Assert.Equal(2, forms.Count);
        var f = forms[0];
        Assert.Equal(("dcl.acad.sample", "Sample Dialog", "sample", "auto"), (f.Id, f.Title, f.ResourceId, f.Units));
        Assert.Equal(UiFormKind.Dcl, f.Kind);
        Assert.Equal(new[] { "row", "spacer", "ok_cancel" }, f.Controls.Select(c => c.Type).ToArray());
        var row = f.Controls[0];
        var boxed = row.Children[0].Children[0];
        Assert.Equal(("boxed_column", "Modes"), (boxed.Type, boxed.Text));
        Assert.Equal("ortho", boxed.Children[0].Name);
        Assert.Equal("&Ortho", boxed.Children[0].Text);
        var mine = row.Children[1];
        Assert.Equal(("my_button", "b1", "Mine"), (mine.Type, mine.Name, mine.Text));
        Assert.Equal("button", mine.Extra["base"]);
        Assert.Equal(4, f.Source.Line);
        Assert.Equal("dcl.acad.other", forms[1].Id);
    }

    [Fact]
    public void PaletteSetClassesAndHostedViews()
    {
        const string cs = @"
namespace Demo.Palettes
{
    public class ToolsPaletteSet : PanelHostBase
    {
        public static Guid PaletteGuid = new Guid(""22df245e-68d0-4d2c-b8f5-e74a15938e01"");
        public void Show()
        {
            var ps = new Autodesk.AutoCAD.Windows.PaletteSet(""Tools"", PaletteGuid);
            ps.AddVisual(""Main"", new MainView());
            ps.Add(""Legacy"", new LegacyControl());
        }
    }
    public class PanelHostBase { protected PaletteSet Open(string title, Guid id) => new PaletteSet(title, id); }
}";
        var f = Assert.Single(FormScanner.Scan(new[] { Src("Tools.cs", Lang.CSharp, cs) }));
        Assert.Equal(UiFormKind.PaletteSet, f.Kind);
        Assert.Equal("Demo.Palettes.ToolsPaletteSet", Assert.Single(f.ClassNames));
        Assert.Equal("Tools", f.Title);
        Assert.Equal("22df245e-68d0-4d2c-b8f5-e74a15938e01", f.Extra["guid"]);
        Assert.Equal(new[] { ("MainView", "Main"), ("LegacyControl", "Legacy") }, f.Controls.Select(c => (c.Type, c.Text!)).ToArray());
    }
}
