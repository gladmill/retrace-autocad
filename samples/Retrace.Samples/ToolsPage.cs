using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// The page of DRAWTOOLS (a palette set of its own, "Retrace Tools"): a toolbar of each kind, image-only buttons, above a tree, each button doing something checkable that depends on the tree node selected, so a replay that skips a click, or
    /// clicks it with another node selected, draws on the wrong layer or not at all. Tree <c>ProjectsTree</c> ("Projects": Alpha, Beta, Gamma). A WinForms <c>ToolStrip</c> <c>ProjStrip</c> (one window, windowless items, tool tips and no text):
    /// Upload (circle r 2 on <c>StripUp_node</c>), Lock (disabled), Download (circle r 3 on <c>StripDown_node</c>), a separator, Refresh (a message box first, then a circle r 4 on <c>StripRefresh_node</c>) and Export, a drop-down with
    /// "As text" (TEXT on <c>StripText_node</c>) and "As circle" (circle r 5 on <c>StripCircle_node</c>). A Win32 toolbar <c>ProjToolBar</c> (a <c>ToolbarWindow32</c> in a WinForms control: the WinForms <c>ToolBar</c> class is gone in .NET) with Sync (circle r 6 on <c>BarSync_node</c>)
    /// and Fetch (circle r 7 on <c>BarFetch_node</c>). The selected node is "none" when there is no selection.
    /// </summary>
    internal class ToolsPage : WinForms.UserControl
    {
        private readonly WinForms.TreeView tree = new() { Name = "ProjectsTree", HideSelection = false, Dock = WinForms.DockStyle.Fill };

        private static System.Drawing.Bitmap Icon(System.Drawing.Color c)
        {
            var bmp = new System.Drawing.Bitmap(24, 24);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.Clear(System.Drawing.Color.White); using var b = new System.Drawing.SolidBrush(c); g.FillEllipse(b, 3, 3, 18, 18); }
            return bmp;
        }

        private const int MenuId = 1003;
        // the variant file C:\retrace-vm\tools-variant.txt may also hold "cmdchanged": the native toolbar's command ids are renumbered and Sync is called Synchronize
        private static readonly bool CmdChanged = VariantIs("cmdchanged");
        private static bool VariantIs(string v) { try { var f = @"C:\retrace-vm\tools-variant.txt"; return System.IO.File.Exists(f) && System.IO.File.ReadAllText(f).Trim() == v; } catch (System.Exception) { return false; } }
        private static readonly int SyncId = CmdChanged ? 1101 : 1001, FetchId = CmdChanged ? 1102 : 1002;
        private readonly NativeToolBar bar;
        private static readonly System.Collections.Generic.Dictionary<int, System.IntPtr> Tips = new();

        private string Node => tree.SelectedNode?.Text ?? "none";

        private void Circle(string layerPrefix, double radius)
        {
            var node = Node;
            SamplePalette.Draw((_, _) => new Circle(new Point3d(radius * 10, 0, 0), Vector3d.ZAxis, radius), layerPrefix + node);
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            const int WM_COMMAND = 0x0111, WM_NOTIFY = 0x004E, TTN_NEEDTEXTW = -530;
            if (m.Msg == WM_COMMAND && bar != null && m.LParam == bar.Handle)
            {
                int id = (int)((long)m.WParam & 0xFFFF);
                if (id == SyncId) Circle("BarSync_", 6);
                else if (id == FetchId) Circle("BarFetch_", 7);
                else if (id == MenuId) Circle("BarMenu_", 9);
            }
            else if (m.Msg == WM_NOTIFY && System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 16) == -710 /* TBN_DROPDOWN */ && System.Runtime.InteropServices.Marshal.ReadIntPtr(m.LParam, 0) == bar.Handle)
            {
                // the arrow of the Menu button: the owner shows its own non-modal menu (its item is not recorded: a menu with no owner item)
                var menu = new WinForms.ContextMenuStrip();
                menu.Items.Add("Bar menu item", null, (_, _) => Circle("BarMenuItem_", 10));
                menu.Show(WinForms.Cursor.Position);
                m.Result = System.IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_NOTIFY && System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 16) == TTN_NEEDTEXTW)
            {
                // NMTTDISPINFOW: NMHDR (hwndFrom, idFrom, code), lpszText at 24; for a tool tip the idFrom is the command id (TTF_IDISHWND off)
                int id = (int)System.Runtime.InteropServices.Marshal.ReadInt64(m.LParam, 8);
                if (Tips.TryGetValue(id, out var text)) { System.Runtime.InteropServices.Marshal.WriteIntPtr(m.LParam, 24, text); m.Result = System.IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }

        public ToolsPage()
        {
            var projects = tree.Nodes.Add("Projects");
            projects.Nodes.Add("Alpha");
            projects.Nodes.Add("Beta");
            projects.Nodes.Add("Gamma");
            projects.Expand();

            var strip = new WinForms.ToolStrip { Name = "ProjStrip", Dock = WinForms.DockStyle.Top, ImageScalingSize = new System.Drawing.Size(24, 24), GripStyle = WinForms.ToolStripGripStyle.Hidden };
            WinForms.ToolStripButton Button(string name, string tip, System.Drawing.Color c, bool enabled, Action? click)
            {
                var b = new WinForms.ToolStripButton { Name = name, ToolTipText = tip, Image = Icon(c), DisplayStyle = WinForms.ToolStripItemDisplayStyle.Image, Enabled = enabled };
                if (click != null) b.Click += (_, _) => click();
                strip.Items.Add(b);
                return b;
            }
            Button("tsUpload", "Upload", System.Drawing.Color.SteelBlue, !VariantIs("disabled"), () => Circle("StripUp_", 2));   // variant "disabled": Upload was enabled when recorded and is disabled now; a replay must stop without clicking it
            Button("tsLock", "Lock", System.Drawing.Color.Goldenrod, false, () => Circle("StripLock_", 9));
            // a test of a changed program: the file C:\retrace-vm\tools-variant.txt (VM only) holds "gone" (no Download button), "renamed" (Download is called Delete now) or "disabled" (Upload is disabled): a replay must fail without clicking anything
            string variant = "";
            try { var vf = @"C:\retrace-vm\tools-variant.txt"; if (System.IO.File.Exists(vf)) variant = System.IO.File.ReadAllText(vf).Trim(); } catch (System.Exception) { }
            if (variant != "gone") Button("tsDownload", variant == "renamed" ? "Delete" : "Download", System.Drawing.Color.SeaGreen, true, () => Circle("StripDown_", 3));
            else Button("tsOther", "Other", System.Drawing.Color.SeaGreen, true, () => Circle("StripOther_", 3));
            strip.Items.Add(new WinForms.ToolStripSeparator());
            Button("tsRefresh", "Refresh", System.Drawing.Color.Orange, true, () =>
            {
                WinForms.MessageBox.Show("Refreshed " + Node, "Retrace Tools");
                Circle("StripRefresh_", 4);
            });
            var export = new WinForms.ToolStripDropDownButton { Name = "tsExport", ToolTipText = "Export", Image = Icon(System.Drawing.Color.MediumPurple), DisplayStyle = WinForms.ToolStripItemDisplayStyle.Image };
            var asText = new WinForms.ToolStripMenuItem("As text") { Name = "miAsText" };
            asText.Click += (_, _) => { var node = Node; SamplePalette.Draw((_, _) => new DBText { TextString = node, Position = new Point3d(0, 20, 0), Height = 2.5 }, "StripText_" + node); };
            var asCircle = new WinForms.ToolStripMenuItem("As circle") { Name = "miAsCircle" };
            asCircle.Click += (_, _) => Circle("StripCircle_", 5);
            // the menu is filled when it opens (as a program does with its recent files): at the button-down it has no items
            export.DropDownOpening += (_, _) => { export.DropDownItems.Clear(); export.DropDownItems.Add(asText); export.DropDownItems.Add(asCircle); };
            strip.Items.Add(export);
            // a split button that is not the first item: its button part draws a circle, its arrow opens a menu with "Share as text"
            var share = new WinForms.ToolStripSplitButton { Name = "tsShare", ToolTipText = "Share", Image = Icon(System.Drawing.Color.DeepPink), DisplayStyle = WinForms.ToolStripItemDisplayStyle.Image };
            share.ButtonClick += (_, _) => Circle("StripShare_", 8);
            var shareText = new WinForms.ToolStripMenuItem("Share as text") { Name = "miShareText" };
            shareText.Click += (_, _) => { var node = Node; SamplePalette.Draw((_, _) => new DBText { TextString = node, Position = new Point3d(0, 30, 0), Height = 2.5 }, "StripShareText_" + node); };
            share.DropDownItems.Add(shareText);
            strip.Items.Add(share);

            // a Win32 toolbar (ToolbarWindow32, as an MFC CToolBar is; the WinForms ToolBar class no longer exists in .NET): buttons with no text and a tool tip each, answered to TTN_NEEDTEXT by this page
            bar = new NativeToolBar { Name = "ProjToolBar", Dock = WinForms.DockStyle.Top, Height = 34 };
            var images = new WinForms.ImageList { ImageSize = new System.Drawing.Size(24, 24), ColorDepth = WinForms.ColorDepth.Depth32Bit };
            images.Images.Add(Icon(System.Drawing.Color.Crimson));
            images.Images.Add(Icon(System.Drawing.Color.Teal));
            images.Images.Add(Icon(System.Drawing.Color.Sienna));
            bar.Setup(images, new[] { 0, 1, 2 }, new[] { SyncId, FetchId, MenuId }, new[] { 0, 0, 0x08 /* BTNS_DROPDOWN: a split button, its arrow opens the owner's menu */ });

            var label = new WinForms.Label { Name = "ProjLabel", Text = "Projects", Dock = WinForms.DockStyle.Top, Height = 18 };
            Tips[SyncId] = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(CmdChanged ? "Synchronize" : "Sync");
            Tips[FetchId] = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("Fetch");
            Tips[MenuId] = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("Menu");
            Controls.Add(tree);
            Controls.Add(label);
            Controls.Add(bar);
            Controls.Add(strip);
        }
    }
}

namespace Retrace.Samples
{
    /// <summary>A Win32 toolbar (<c>ToolbarWindow32</c>) as a WinForms control: the window class of an MFC CToolBar. Buttons are image-only; the owner (the page) gets WM_COMMAND and answers TTN_NEEDTEXT.</summary>
    internal sealed class NativeToolBar : System.Windows.Forms.Control
    {
        [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern System.IntPtr SendMessage(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct INITCOMMONCONTROLSEX { public int Size, Icc; }

        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var icc = new INITCOMMONCONTROLSEX { Size = 8, Icc = 0x4 /* ICC_BAR_CLASSES */ };
                InitCommonControlsEx(ref icc);
                var cp = base.CreateParams;
                cp.ClassName = "ToolbarWindow32";
                cp.Style = 0x40000000 | 0x10000000 | 0x04000000 /* WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS */ | 0x0800 /* TBSTYLE_FLAT */ | 0x0100 /* TBSTYLE_TOOLTIPS */ | 0x40 /* CCS_NODIVIDER */ | 0x4 /* CCS_NORESIZE */ | 0x8 /* CCS_NOPARENTALIGN */;
                return cp;
            }
        }

        /// <summary>Gives the toolbar its image list and one button per image, with the command ids.</summary>
        public NativeToolBar() { SetStyle(System.Windows.Forms.ControlStyles.UserPaint | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer, false); }   // the toolbar window draws itself

        private System.Windows.Forms.ImageList? _images; private int[] _imageIndexes = System.Array.Empty<int>(); private int[] _ids = System.Array.Empty<int>(); private int[] _styles = System.Array.Empty<int>();

        public void Setup(System.Windows.Forms.ImageList images, int[] imageIndexes, int[] ids, int[] styles) { _images = images; _imageIndexes = imageIndexes; _ids = ids; _styles = styles; if (IsHandleCreated) Fill(); }

        protected override void OnHandleCreated(System.EventArgs e) { base.OnHandleCreated(e); Fill(); }

        private void Fill()
        {
            if (_images == null) return;
            var images = _images; var imageIndexes = _imageIndexes; var ids = _ids;
            var h = Handle;
            SendMessage(h, 0x041E /* TB_BUTTONSTRUCTSIZE */, (System.IntPtr)32, System.IntPtr.Zero);
            SendMessage(h, 0x0430 /* TB_SETIMAGELIST */, System.IntPtr.Zero, images.Handle);
            SendMessage(h, 0x0454 /* TB_SETEXTENDEDSTYLE */, System.IntPtr.Zero, (System.IntPtr)0x01 /* TBSTYLE_EX_DRAWDDARROWS */);
            var size = 32;   // TBBUTTON on x64: iBitmap, idCommand, fsState, fsStyle, 6 reserved, dwData, iString
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size * ids.Length);
            for (int i = 0; i < ids.Length; i++)
            {
                for (int z = 0; z < size; z += 8) System.Runtime.InteropServices.Marshal.WriteInt64(buf, i * size + z, 0);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, i * size, imageIndexes[i]);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, i * size + 4, ids[i]);
                System.Runtime.InteropServices.Marshal.WriteByte(buf, i * size + 8, 0x04 /* TBSTATE_ENABLED */);
                System.Runtime.InteropServices.Marshal.WriteByte(buf, i * size + 9, (byte)_styles[i]);
                System.Runtime.InteropServices.Marshal.WriteInt64(buf, i * size + 24, -1);   // iString: no text
            }
            SendMessage(h, 0x0444 /* TB_ADDBUTTONSW */, (System.IntPtr)ids.Length, buf);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            SendMessage(h, 0x0421 /* TB_AUTOSIZE */, System.IntPtr.Zero, System.IntPtr.Zero);
        }
    }
}
