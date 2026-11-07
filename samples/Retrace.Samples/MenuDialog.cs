using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// Modal dialog of DRAWMENU: right-click context menus of the three kinds Retrace records and replays.
    /// A TreeView (<c>CatalogTree</c>, "Catalog") whose ContextMenuStrip has Edit > Rename (acts in place), Edit > Duplicate (adds a sibling) and Properties... (opens the sub form <see cref="NoteForm"/>);
    /// a list box (<c>FilesList</c>, "Files") with a Win32 popup menu (TrackPopupMenu, WM_COMMAND to the form): Mark, Unmark, More > Clear all.
    /// OK needs nothing; the command draws TEXT from the results, so a checkpoint fails when a menu choice was skipped.
    /// </summary>
    public class MenuDialog : Form
    {
        private readonly TreeView tree = new()
        {
            Name = "CatalogTree", AccessibleName = "Catalog", HideSelection = false,
            Location = new Point(16, 36), Size = new Size(288, 130), TabIndex = 1,
        };
        private readonly NativeMenuListBox files = new() { Name = "FilesList", AccessibleName = "Files", Location = new Point(16, 190), Size = new Size(288, 70), TabIndex = 2 };
        private readonly ContextMenuStrip catalogMenu = new() { Name = "CatalogMenu" };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(148, 330), Size = new Size(75, 27), TabIndex = 3 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(229, 330), Size = new Size(75, 27), TabIndex = 4, DialogResult = DialogResult.Cancel };
        private readonly Label status = new() { Name = "StatusLabel", AutoSize = true, Location = new Point(16, 270), Text = "No menu used yet" };

        private const int IdMark = 9001, IdUnmark = 9002, IdClearAll = 9003;

        public MenuDialog()
        {
            Text = "Menu practice";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(320, 372);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "catalogLabel", AutoSize = true, Location = new Point(16, 14), Text = "Catalog:" });
            Controls.Add(tree);
            Controls.Add(new Label { Name = "filesLabel", AutoSize = true, Location = new Point(16, 172), Text = "Files:" });
            Controls.Add(files);
            Controls.Add(status);
            Controls.Add(ok);
            Controls.Add(cancel);
            PartCatalog.Fill(tree);
            foreach (var f in new[] { "plan.dwg", "section.dwg", "detail.dwg" }) files.Items.Add(f);

            var rename = new ToolStripMenuItem("&Rename") { Name = "renameItem" };
            rename.Click += (_, _) => { if (tree.SelectedNode is { } n) { n.Text += " (renamed)"; status.Text = "Renamed"; } };
            var duplicate = new ToolStripMenuItem("&Duplicate") { Name = "duplicateItem" };
            duplicate.Click += (_, _) =>
            {
                if (tree.SelectedNode is not { } n) return;
                var copy = new TreeNode(n.Text + " copy");
                (n.Parent?.Nodes ?? tree.Nodes).Add(copy);
                status.Text = "Duplicated";
            };
            var edit = new ToolStripMenuItem("&Edit") { Name = "editItem" };
            edit.DropDownItems.Add(rename);
            edit.DropDownItems.Add(duplicate);
            var properties = new ToolStripMenuItem("Properties...") { Name = "propertiesItem" };
            properties.Click += (_, _) =>
            {
                if (tree.SelectedNode is not { } n) return;
                using var form = new NoteForm(n.Tag as string ?? "");
                if (form.ShowDialog(this) == DialogResult.OK) { n.Tag = form.Note; status.Text = "Note set"; }
            };
            catalogMenu.Items.Add(edit);
            catalogMenu.Items.Add(new ToolStripSeparator());
            catalogMenu.Items.Add(properties);
            tree.ContextMenuStrip = catalogMenu;
            // a right-click does not select a node by itself: as most programs do, select the node under the mouse first
            tree.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right && tree.GetNodeAt(e.Location) is { } hit) tree.SelectedNode = hit; };

            // a right-click does NOT select in the list: the menu acts on the item under the mouse (MenuIndex), while the selection stays where a left click put it
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        }

        /// <summary>The text of the selected tree node, "" when none.</summary>
        public string Selected => tree.SelectedNode?.Text ?? "";
        /// <summary>The note set with Properties... on the selected node.</summary>
        public string Note => tree.SelectedNode?.Tag as string ?? "";
        /// <summary>The number of nodes under Tools (a Duplicate there adds one).</summary>
        public int ToolsCount => tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == "Tools")?.Nodes.Count ?? 0;
        public string FileList => string.Join(",", files.Items.Cast<string>());

        protected override void WndProc(ref Message m)
        {
            // the command of the list box's Win32 popup menu (TrackPopupMenu without TPM_RETURNCMD posts WM_COMMAND to its owner, this form)
            if (m.Msg == 0x0111 && m.LParam == IntPtr.Zero && ((long)m.WParam >> 16) == 0)
            {
                var names = files.Items.Cast<string>().ToList();
                int id = (int)((long)m.WParam & 0xFFFF);
                if (id == IdMark && files.MenuIndex >= 0) { Refill(i => i == files.MenuIndex ? names[i].TrimEnd('*') + "*" : names[i]); status.Text = "Marked"; return; }
                if (id == IdUnmark && files.MenuIndex >= 0) { Refill(i => i == files.MenuIndex ? names[i].TrimEnd('*') : names[i]); status.Text = "Unmarked"; return; }
                if (id == IdClearAll) { Refill(i => names[i].TrimEnd('*')); status.Text = "Cleared"; return; }
            }
            base.WndProc(ref m);
        }

        /// <summary>Rebuilds the list with new item texts and the same selection (assigning to <c>Items[i]</c> of a ListBox left a stale item state that threw when the handle was destroyed).</summary>
        private void Refill(Func<int, string> text)
        {
            int selected = files.SelectedIndex, count = files.Items.Count;
            var names = Enumerable.Range(0, count).Select(text).ToList();
            files.BeginUpdate();
            files.Items.Clear();
            foreach (var n in names) files.Items.Add(n);
            files.SelectedIndex = selected;
            files.EndUpdate();
        }

        /// <summary>A list box that shows a Win32 popup menu (CreatePopupMenu / TrackPopupMenu), not a ContextMenuStrip, when it gets WM_CONTEXTMENU.</summary>
        private sealed class NativeMenuListBox : ListBox
        {
            [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
            [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
            [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);

            /// <summary>The item the menu was opened on: the one under the mouse (the selected one for the keyboard).</summary>
            public int MenuIndex = -1;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x007B)   // WM_CONTEXTMENU
                {
                    long l = (long)m.LParam;
                    int x = (short)(l & 0xFFFF), y = (short)((l >> 16) & 0xFFFF);
                    if (l == -1) { var p = PointToScreen(new Point(Width / 2, Height / 2)); x = p.X; y = p.Y; MenuIndex = SelectedIndex; }
                    else MenuIndex = IndexFromPoint(PointToClient(new Point(x, y)));
                    var more = CreatePopupMenu();
                    AppendMenuW(more, 0, (UIntPtr)IdClearAll, "Clear &all");
                    var menu = CreatePopupMenu();
                    AppendMenuW(menu, 0, (UIntPtr)IdMark, "&Mark\tM");
                    AppendMenuW(menu, 0, (UIntPtr)IdUnmark, "&Unmark");
                    AppendMenuW(menu, 0x10 /* MF_POPUP */, (UIntPtr)(ulong)(long)more, "M&ore");
                    TrackPopupMenu(menu, 0x0002 /* TPM_RIGHTBUTTON */, x, y, 0, FindForm()!.Handle, IntPtr.Zero);
                    DestroyMenu(menu);
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }

    /// <summary>The sub form the Properties... menu item opens: a Note field and OK / Cancel.</summary>
    public class NoteForm : Form
    {
        private readonly TextBox note = new() { Name = "noteBox", AccessibleName = "Note", Location = new Point(16, 36), Size = new Size(240, 23), TabIndex = 1 };

        public NoteForm(string text)
        {
            Text = "Properties";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(272, 120);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            var ok = new Button { Name = "okButton", Text = "OK", Location = new Point(100, 80), Size = new Size(75, 27), TabIndex = 2, DialogResult = DialogResult.OK };
            var cancel = new Button { Name = "cancelButton", Text = "Cancel", Location = new Point(181, 80), Size = new Size(75, 27), TabIndex = 3, DialogResult = DialogResult.Cancel };
            Controls.Add(new Label { Name = "noteLabel", AutoSize = true, Location = new Point(16, 14), Text = "Note:" });
            Controls.Add(note);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            note.Text = text;
        }

        public string Note => note.Text;
    }
}
