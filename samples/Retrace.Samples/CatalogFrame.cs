using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Retrace.Samples
{
    /// <summary>
    /// A modal window that is not a stock dialog (<c>#32770</c>) and not WinForms: a plain Win32 frame of a window class registered under the name an MFC frame has (<c>AfxFrameOrView140u</c>), made with
    /// RegisterClassEx / CreateWindowEx and run with its own message loop while its owner (AutoCAD's main window) is disabled, as a CFrameWnd run modally is. The controls sit in a child "view" window of
    /// the same class (a CFormView): a tab control (<c>SysTabControl32</c>, pages Products and Notes), on the Products page the label "Products:", three view-mode buttons (Icons, List, Details; each
    /// sends <c>LVM_SETVIEW</c>) and an icon-mode list view (<c>SysListView32</c>, multiple selection) of a few products, on the Notes page a text box; Add and Close buttons. Add closes the window and
    /// <see cref="CatalogCommands"/> opens the second dialog at once (two sequential modals, not nested). The names are made up for the sample.
    /// </summary>
    internal sealed class CatalogFrame
    {
        /// <summary>The window class name of the frame and of its view (an MFC class name).</summary>
        public const string FrameClass = "AfxFrameOrView140u";

        /// <summary>The products selected when Add was pressed (texts, in list order).</summary>
        public List<string> Chosen { get; } = new();
        /// <summary>The note on the Notes page.</summary>
        public string Note { get; private set; } = "";
        /// <summary>The product the context menu item Mark was chosen on ("" when it was not).</summary>
        public string Marked { get; private set; } = "";

        // ---- Win32 ---------------------------------------------------------------------------------------------------------

        delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WndClassEx
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct TcItem { public uint mask, state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential)] struct LvItem { public uint mask; public int iItem, iSubItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; }
        [StructLayout(LayoutKind.Sequential)] struct LvColumn { public uint mask; public int fmt, cx; public IntPtr pszText; public int cchTextMax, iSubItem, iImage, iOrder; }
        [StructLayout(LayoutKind.Sequential)] struct NmHdr { public IntPtr hwndFrom, idFrom; public int code; }

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WndClassEx wc);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string cls, string? text, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr id, IntPtr instance, IntPtr param);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetMessageW")] static extern int GetMessage(out Msg m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll", EntryPoint = "IsDialogMessageW")] static extern bool IsDialogMessage(IntPtr h, ref Msg m);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg m);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] static extern IntPtr DispatchMessage(ref Msg m);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool enable);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref Rect r, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, StringBuilder l);
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] static extern int MessageBox(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
        [DllImport("comctl32.dll")] static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int initial, int grow);
        [DllImport("comctl32.dll")] static extern int ImageList_Add(IntPtr himl, IntPtr bitmap, IntPtr mask);
        [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref InitCommonControls icc);
        [StructLayout(LayoutKind.Sequential)] struct InitCommonControls { public int size; public uint classes; }

        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000,
            WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000, WS_GROUP = 0x00020000, WS_EX_DLGMODALFRAME = 1, WS_EX_CONTROLPARENT = 0x10000;
        const uint BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1, ES_AUTOHSCROLL = 0x80, LVS_ICON = 0, LVS_SHOWSELALWAYS = 8;
        const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_COMMAND = 0x111, WM_NOTIFY = 0x4E, WM_NCDESTROY = 0x82, WM_CONTEXTMENU = 0x7B;
        const uint TCM_INSERTITEMW = 0x133E, TCM_GETCURSEL = 0x130B, TCIF_TEXT = 1;
        const uint LVM_SETIMAGELIST = 0x1003, LVM_GETITEMTEXTW = 0x1073, LVM_INSERTITEMW = 0x104D, LVM_INSERTCOLUMNW = 0x1061, LVM_SETVIEW = 0x108E, LVM_GETNEXTITEM = 0x100C;
        const int TCN_SELCHANGE = -551, LV_VIEW_ICON = 0, LV_VIEW_DETAILS = 1, LV_VIEW_LIST = 3, LVNI_SELECTED = 2, LVSIL_NORMAL = 0, LVSIL_SMALL = 1;
        const int IdTab = 100, IdProductsLabel = 101, IdIcons = 102, IdList = 103, IdDetails = 104, IdProducts = 105, IdNoteLabel = 106, IdNote = 107, IdAdd = 108, IdClose = 109, IdStatus = 110, IdMark = 9001;

        static WndProc? _proc;                  // the delegate must outlive the class registration
        static bool _registered;
        static CatalogFrame? _current;          // the one frame that is open (the window procedure is static)

        IntPtr _frame, _view, _tab, _list, _note, _owner;
        readonly List<IntPtr> _productsPage = new(), _notesPage = new();
        bool _added, _done;

        static readonly string[] Names = { "Bolts", "Nuts", "Washers", "Screws", "Rivets" };

        static IntPtr Instance => Marshal.GetHINSTANCE(typeof(CatalogFrame).Module);

        static void Register()
        {
            if (_registered) return;
            var icc = new InitCommonControls { size = Marshal.SizeOf<InitCommonControls>(), classes = 0x0000FFFF };
            InitCommonControlsEx(ref icc);
            _proc = FrameProc;
            var wc = new WndClassEx
            {
                cbSize = Marshal.SizeOf<WndClassEx>(), style = 0x3,   // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = Instance,
                hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512), hbrBackground = (IntPtr)16,   // COLOR_BTNFACE + 1
                lpszClassName = FrameClass,
            };
            RegisterClassEx(ref wc);   // 0 when it exists already (another load of this dll): the class is there either way
            _registered = true;
        }

        /// <summary>Shows the frame and runs it until it is closed; true = Add was pressed with a product selected. The owner is disabled meanwhile.</summary>
        public bool Show(IntPtr owner)
        {
            Register();
            _owner = owner;
            _current = this;
            const uint style = WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;
            var client = new Rect { Left = 0, Top = 0, Right = 450, Bottom = 360 };
            AdjustWindowRectEx(ref client, style, false, WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT);
            int w = client.Right - client.Left, h = client.Bottom - client.Top;
            int x = (GetSystemMetrics(0) - w) / 2, y = (GetSystemMetrics(1) - h) / 2;
            _frame = CreateWindowEx(WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT, FrameClass, "Catalog", style, x, y, w, h, owner, IntPtr.Zero, Instance, IntPtr.Zero);
            if (_frame == IntPtr.Zero) { _current = null; return false; }
            try
            {
                // the view: a child window of the frame's class, holding the controls
                _view = CreateWindowEx(WS_EX_CONTROLPARENT, FrameClass, null, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 450, 360, _frame, IntPtr.Zero, Instance, IntPtr.Zero);
                BuildControls();
                if (owner != IntPtr.Zero) EnableWindow(owner, false);   // modal: the owner takes no input until this window is gone
                ShowWindow(_frame, 5);
                SetForegroundWindow(_frame);
                while (!_done && GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
                    if (!IsDialogMessage(_frame, ref m)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            }
            finally
            {
                if (owner != IntPtr.Zero) { EnableWindow(owner, true); SetForegroundWindow(owner); }
                if (_frame != IntPtr.Zero && IsWindowAlive(_frame)) DestroyWindow(_frame);
                _current = null;
            }
            return _added;
        }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        static bool IsWindowAlive(IntPtr h) => IsWindow(h);

        // ---- controls ------------------------------------------------------------------------------------------------------

        IntPtr Make(string cls, string? text, uint style, int x, int y, int w, int h, int id, uint ex = 0)
        {
            var c = CreateWindowEx(ex, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, _view, (IntPtr)id, Instance, IntPtr.Zero);
            SendMessage(c, WM_SETFONT, GetStockObject(17), (IntPtr)1);   // DEFAULT_GUI_FONT
            return c;
        }

        void BuildControls()
        {
            _tab = Make("SysTabControl32", null, WS_TABSTOP | WS_CLIPSIBLINGS, 10, 10, 430, 300, IdTab);
            AddTab(0, "Products"); AddTab(1, "Notes");

            // Products page
            _productsPage.Add(Make("Static", "Products:", 0, 22, 46, 70, 16, IdProductsLabel));
            _productsPage.Add(Make("Button", "Icons", BS_PUSHBUTTON | WS_TABSTOP | WS_GROUP, 140, 40, 72, 27, IdIcons));
            _productsPage.Add(Make("Button", "List", BS_PUSHBUTTON | WS_TABSTOP, 220, 40, 72, 27, IdList));
            _productsPage.Add(Make("Button", "Details", BS_PUSHBUTTON | WS_TABSTOP, 300, 40, 72, 27, IdDetails));
            _list = Make("SysListView32", null, LVS_ICON | LVS_SHOWSELALWAYS | WS_BORDER | WS_TABSTOP, 22, 78, 406, 220, IdProducts);
            _productsPage.Add(_list);
            FillList();

            // Notes page (hidden until its tab is shown)
            _notesPage.Add(Make("Static", "Note:", 0, 22, 48, 40, 16, IdNoteLabel));
            _note = Make("Edit", null, ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP, 70, 44, 220, 23, IdNote);
            _notesPage.Add(_note);
            foreach (var c in _notesPage) ShowWindow(c, 0);

            Make("Static", "Icons", 0, 10, 330, 120, 16, IdStatus);
            Make("Button", "Add", BS_DEFPUSHBUTTON | WS_TABSTOP, 270, 322, 80, 27, IdAdd);
            Make("Button", "Close", BS_PUSHBUTTON | WS_TABSTOP, 360, 322, 80, 27, IdClose);
        }

        void AddTab(int index, string text)
        {
            var mem = Marshal.StringToHGlobalUni(text);
            try
            {
                var item = new TcItem { mask = TCIF_TEXT, pszText = mem };
                var p = Marshal.AllocHGlobal(Marshal.SizeOf<TcItem>());
                try { Marshal.StructureToPtr(item, p, false); SendMessage(_tab, TCM_INSERTITEMW, (IntPtr)index, p); }
                finally { Marshal.FreeHGlobal(p); }
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        void FillList()
        {
            // one icon per product (a coloured disc), shared by the large and the small image list
            var colors = new[] { Color.SteelBlue, Color.Goldenrod, Color.SeaGreen, Color.IndianRed, Color.MediumPurple };
            var himl = ImageList_Create(32, 32, 0x20 /* ILC_COLOR32 */, colors.Length, 0);
            foreach (var c in colors)
            {
                using var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); using var brush = new SolidBrush(c); g.FillEllipse(brush, 3, 3, 26, 26); }
                var hbmp = bmp.GetHbitmap();
                ImageList_Add(himl, hbmp, IntPtr.Zero);
                DeleteObject(hbmp);
            }
            SendMessage(_list, LVM_SETIMAGELIST, (IntPtr)LVSIL_NORMAL, himl);
            SendMessage(_list, LVM_SETIMAGELIST, (IntPtr)LVSIL_SMALL, himl);
            var colText = Marshal.StringToHGlobalUni("Product");
            var col = new LvColumn { mask = 6 /* LVCF_TEXT | LVCF_WIDTH */, cx = 200, pszText = colText };
            var cp = Marshal.AllocHGlobal(Marshal.SizeOf<LvColumn>());
            Marshal.StructureToPtr(col, cp, false);
            SendMessage(_list, LVM_INSERTCOLUMNW, IntPtr.Zero, cp);
            Marshal.FreeHGlobal(cp); Marshal.FreeHGlobal(colText);
            for (int i = 0; i < Names.Length; i++)
            {
                var text = Marshal.StringToHGlobalUni(Names[i]);
                var item = new LvItem { mask = 3 /* LVIF_TEXT | LVIF_IMAGE */, iItem = i, pszText = text, iImage = i };
                var ip = Marshal.AllocHGlobal(Marshal.SizeOf<LvItem>());
                Marshal.StructureToPtr(item, ip, false);
                SendMessage(_list, LVM_INSERTITEMW, IntPtr.Zero, ip);
                Marshal.FreeHGlobal(ip); Marshal.FreeHGlobal(text);
            }
        }

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        // ---- behaviour ------------------------------------------------------------------------------------------------------

        void ShowPage(int page)
        {
            foreach (var c in _productsPage) ShowWindow(c, page == 0 ? 5 : 0);
            foreach (var c in _notesPage) ShowWindow(c, page == 1 ? 5 : 0);
        }

        void SetView(int mode, string label)
        {
            SendMessage(_list, LVM_SETVIEW, (IntPtr)mode, IntPtr.Zero);
            var status = GetDlgItem(_view, IdStatus);
            SetWindowText(status, label);
        }

        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr h, string text);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder text, int max);

        /// <summary>A Win32 popup menu (TrackPopupMenu, WM_COMMAND to the view) on the product list, which is on the Products tab: one item, Mark, that notes the first selected product.</summary>
        void ProductMenu(IntPtr lParam)
        {
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            if ((long)lParam == -1 || (x == -1 && y == -1)) { GetWindowRect(_list, out var r); x = r.Left + 40; y = r.Top + 40; }
            var menu = CreatePopupMenu();
            try { AppendMenu(menu, 0, (UIntPtr)IdMark, "Mark"); TrackPopupMenu(menu, 0x2 /* TPM_RIGHTBUTTON */, x, y, 0, _view, IntPtr.Zero); }
            finally { DestroyMenu(menu); }
        }

        void Mark()
        {
            int i = (int)SendMessage(_list, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED);
            if (i < 0 || i >= Names.Length) return;
            Marked = Names[i];
            SetWindowText(GetDlgItem(_view, IdStatus), "Marked " + Marked);
        }

        /// <summary>Closes the frame the way MFC does: the owner is enabled again before the window is destroyed (so the owner, not another window, gets the activation).</summary>
        void CloseFrame()
        {
            if (_owner != IntPtr.Zero) EnableWindow(_owner, true);
            DestroyWindow(_frame);
        }

        void Add()
        {
            Chosen.Clear();
            var buf = Marshal.AllocHGlobal(512);
            var item = Marshal.AllocHGlobal(Marshal.SizeOf<LvItem>());
            try
            {
                for (int i = (int)SendMessage(_list, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED); i >= 0; i = (int)SendMessage(_list, LVM_GETNEXTITEM, (IntPtr)i, (IntPtr)LVNI_SELECTED))
                {
                    Marshal.WriteInt16(buf, 0, 0);
                    Marshal.StructureToPtr(new LvItem { pszText = buf, cchTextMax = 256 }, item, false);
                    int n = (int)SendMessage(_list, LVM_GETITEMTEXTW, (IntPtr)i, item);
                    Chosen.Add(n > 0 ? Marshal.PtrToStringUni(buf, n) ?? "" : "");
                }
            }
            finally { Marshal.FreeHGlobal(buf); Marshal.FreeHGlobal(item); }
            if (Chosen.Count == 0) { MessageBox(_frame, "Select a product first.", "Catalog", 0x30); return; }
            var sb = new StringBuilder(256);
            GetWindowText(_note, sb, sb.Capacity);
            Note = sb.ToString();
            _added = true;
            CloseFrame();   // closes this window; the command opens the next dialog at once
        }

        static IntPtr FrameProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            var self = _current;
            if (self != null)
            {
                switch (msg)
                {
                    case WM_COMMAND when h == self._view || h == self._frame:
                    {
                        int id = (int)((long)w & 0xFFFF), code = (int)(((long)w >> 16) & 0xFFFF);
                        if (code == 0 && id == IdMark) { self.Mark(); return IntPtr.Zero; }
                        if (code == 0)   // BN_CLICKED (and 1 / 2 for the accelerator-style IDOK / IDCANCEL of IsDialogMessage)
                        {
                            if (id == IdIcons) self.SetView(LV_VIEW_ICON, "Icons");
                            else if (id == IdList) self.SetView(LV_VIEW_LIST, "List");
                            else if (id == IdDetails) self.SetView(LV_VIEW_DETAILS, "Details");
                            else if (id == IdAdd || id == 1) self.Add();
                            else if (id == IdClose || id == 2) self.CloseFrame();
                            return IntPtr.Zero;
                        }
                        break;
                    }
                    case WM_CONTEXTMENU when h == self._view && w == self._list:
                        self.ProductMenu(l);
                        return IntPtr.Zero;
                    case WM_NOTIFY when h == self._view:
                    {
                        var hdr = Marshal.PtrToStructure<NmHdr>(l);
                        if (hdr.hwndFrom == self._tab && hdr.code == TCN_SELCHANGE) { self.ShowPage((int)SendMessage(self._tab, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero)); return IntPtr.Zero; }
                        break;
                    }
                    case WM_CLOSE when h == self._frame:
                        self.CloseFrame();
                        return IntPtr.Zero;
                    case WM_NCDESTROY when h == self._frame:
                        self._done = true;
                        break;
                }
            }
            return DefWindowProc(h, msg, w, l);
        }
    }
}
