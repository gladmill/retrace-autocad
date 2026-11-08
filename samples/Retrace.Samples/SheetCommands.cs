using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWSHEET: a real comctl32 property sheet (<c>PropertySheetW</c>, pages from in-memory dialog templates), the kind of tab control that is switched with <c>PSM_SETCURSEL</c>.
    /// Three pages that reuse the control ids 1001 and 1002: General (Name, Note), Size (Width, Height) and Options (Mark the center, Radius). The Size page refuses to be left (PSN_KILLACTIVE
    /// answers TRUE) while Width or Height is not a number above zero. OK draws a plate from every page's value, so a replay that missed one draws something else.
    /// </summary>
    public class SheetCommands
    {
        [CommandMethod("DRAWSHEET")]
        public void DrawSheet()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var sheet = new PropSheet();
            if (!sheet.Show(Application.MainWindow.Handle)) return;
            double w = sheet.Width, h = sheet.Height;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                void Add(Entity entity) { modelSpace.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); }
                var outline = new Polyline();
                outline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                outline.AddVertexAt(1, new Point2d(w, 0), 0, 0, 0);
                outline.AddVertexAt(2, new Point2d(w, h), 0, 0, 0);
                outline.AddVertexAt(3, new Point2d(0, h), 0, 0, 0);
                outline.Closed = true;
                Add(outline);
                Add(new DBText { TextString = sheet.PlateName + " / " + sheet.Note, Position = new Point3d(0, -4, 0), Height = 2.5 });
                if (sheet.Mark) Add(new Circle(new Point3d(w / 2, h / 2, 0), Vector3d.ZAxis, sheet.Radius));
                tr.Commit();
            }
            ed.WriteMessage("\nSheet plate " + sheet.PlateName + " drawn: " + w + " x " + h + (sheet.Mark ? ", center marked, radius " + sheet.Radius : "") + ", note " + sheet.Note + ".");
        }
    }

    /// <summary>The property sheet and its state. Values are read from a page when it is left (PSN_KILLACTIVE) and applied (PSN_APPLY); a page never shown is never created.</summary>
    internal sealed class PropSheet
    {
        public string PlateName = "Plate", Note = "";
        public double Width = 20, Height = 10, Radius = 2;
        public bool Mark;

        // ---- comctl32 ----------------------------------------------------------------------------------------------------------
        delegate IntPtr DlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct PropSheetPage
        {
            public int dwSize, dwFlags;
            public IntPtr hInstance, pResource, hIcon, pszTitle, pfnDlgProc, lParam, pfnCallback, pcRefParent, pszHeaderTitle, pszHeaderSubTitle, hActCtx, pszbmHeader;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropSheetHeader
        {
            public int dwSize, dwFlags;
            public IntPtr hwndParent, hInstance, hIcon, pszCaption;
            public int nPages;
            public IntPtr nStartPage, ppsp, pfnCallback, hbmWatermark, hplWatermark, hbmHeader;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NmHdr { public IntPtr hwndFrom, idFrom; public int code; }

        [DllImport("comctl32.dll", EntryPoint = "PropertySheetW", SetLastError = true)] static extern IntPtr PropertySheet(ref PropSheetHeader header);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetDlgItemTextW", CharSet = CharSet.Unicode)] static extern bool SetDlgItemText(IntPtr h, int id, string text);
        [DllImport("user32.dll", EntryPoint = "GetDlgItemTextW", CharSet = CharSet.Unicode)] static extern int GetDlgItemText(IntPtr h, int id, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool CheckDlgButton(IntPtr h, int id, uint check);
        [DllImport("user32.dll")] static extern uint IsDlgButtonChecked(IntPtr h, int id);

        const uint WM_INITDIALOG = 0x0110, WM_NOTIFY = 0x004E;
        const int PSN_SETACTIVE = -200, PSN_KILLACTIVE = -201, PSN_APPLY = -202;
        const int PSP_DLGINDIRECT = 0x1, PSP_USETITLE = 0x8, PSH_PROPSHEETPAGE = 0x8, PSH_NOAPPLYNOW = 0x80, PSH_NOCONTEXTHELP = 0x2000000;
        const int IdA = 1001, IdB = 1002;

        enum Page { General, Size, Options }

        // the delegates and memory must outlive the modal call
        readonly List<DlgProc> _procs = new();
        readonly List<IntPtr> _memory = new();

        /// <summary>Shows the sheet (modal) with this title; true = OK was pressed.</summary>
        public bool Show(IntPtr parent, string caption = "Sheet practice")
        {
            var pages = new PropSheetPage[3];
            var titles = new[] { "General", "Size", "Options" };
            for (int i = 0; i < pages.Length; i++)
            {
                var page = (Page)i;
                DlgProc proc = (h, m, w, l) => PageProc(page, h, m, w, l);
                _procs.Add(proc);
                pages[i] = new PropSheetPage
                {
                    dwSize = Marshal.SizeOf<PropSheetPage>(),
                    dwFlags = PSP_DLGINDIRECT | PSP_USETITLE,
                    pResource = Template(page),
                    pszTitle = Alloc(Marshal.StringToHGlobalUni(titles[i])),
                    pfnDlgProc = Marshal.GetFunctionPointerForDelegate(proc),
                };
            }
            var pageMem = Alloc(Marshal.AllocHGlobal(Marshal.SizeOf<PropSheetPage>() * pages.Length));
            for (int i = 0; i < pages.Length; i++) Marshal.StructureToPtr(pages[i], pageMem + i * Marshal.SizeOf<PropSheetPage>(), false);
            var header = new PropSheetHeader
            {
                dwSize = Marshal.SizeOf<PropSheetHeader>(),
                dwFlags = PSH_PROPSHEETPAGE | PSH_NOAPPLYNOW | PSH_NOCONTEXTHELP,
                hwndParent = parent,
                pszCaption = Alloc(Marshal.StringToHGlobalUni(caption)),
                nPages = pages.Length,
                ppsp = pageMem,
            };
            try
            {
                long result = PropertySheet(ref header).ToInt64();
                return result > 0;
            }
            finally { foreach (var m in _memory) Marshal.FreeHGlobal(m); _memory.Clear(); }
        }

        IntPtr Alloc(IntPtr p) { _memory.Add(p); return p; }

        // ---- the pages ---------------------------------------------------------------------------------------------------------

        IntPtr PageProc(Page page, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_INITDIALOG) { Load(page, hwnd); return (IntPtr)1; }
                if (msg == WM_NOTIFY)
                {
                    int code = Marshal.PtrToStructure<NmHdr>(lParam).code;
                    if (code == PSN_KILLACTIVE || code == PSN_APPLY)
                    {
                        bool ok = Save(page, hwnd);
                        // PSN_KILLACTIVE: TRUE in DWLP_MSGRESULT refuses to leave the page
                        SetWindowLongPtr(hwnd, 0, code == PSN_KILLACTIVE && !ok ? (IntPtr)1 : IntPtr.Zero);
                        return (IntPtr)1;
                    }
                    if (code == PSN_SETACTIVE) { SetWindowLongPtr(hwnd, 0, IntPtr.Zero); return (IntPtr)1; }
                }
            }
            catch { /* a page procedure must not throw into the message loop */ }
            return IntPtr.Zero;
        }

        void Load(Page page, IntPtr hwnd)
        {
            switch (page)
            {
                case Page.General: SetDlgItemText(hwnd, IdA, PlateName); SetDlgItemText(hwnd, IdB, Note); break;
                case Page.Size: SetDlgItemText(hwnd, IdA, Width.ToString("0.###")); SetDlgItemText(hwnd, IdB, Height.ToString("0.###")); break;
                case Page.Options: CheckDlgButton(hwnd, IdA, Mark ? 1u : 0u); SetDlgItemText(hwnd, IdB, Radius.ToString("0.###")); break;
            }
        }

        static string Text(IntPtr hwnd, int id) { var sb = new StringBuilder(256); GetDlgItemText(hwnd, id, sb, sb.Capacity); return sb.ToString(); }

        /// <summary>Reads the page; false = a value is not acceptable (the Size page then refuses to be left).</summary>
        bool Save(Page page, IntPtr hwnd)
        {
            switch (page)
            {
                case Page.General: PlateName = Text(hwnd, IdA); Note = Text(hwnd, IdB); return true;
                case Page.Size:
                    if (!(double.TryParse(Text(hwnd, IdA), out var w) && w > 0 && double.TryParse(Text(hwnd, IdB), out var h) && h > 0)) return false;
                    Width = w; Height = h;
                    return true;
                default:
                    Mark = IsDlgButtonChecked(hwnd, IdA) == 1;
                    if (double.TryParse(Text(hwnd, IdB), out var r) && r > 0) Radius = r;
                    return true;
            }
        }

        // ---- dialog templates (DLGTEMPLATE in memory) --------------------------------------------------------------------------

        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CAPTION = 0x00C00000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000, DS_SETFONT = 0x40, DS_CONTROL = 0x400,
            ES_AUTOHSCROLL = 0x80, BS_AUTOCHECKBOX = 0x3, SS_LEFT = 0;

        IntPtr Template(Page page)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.Unicode);
            void Align() { while (ms.Position % 4 != 0) w.Write((byte)0); }
            void Str(string s) { foreach (var c in s) w.Write((ushort)c); w.Write((ushort)0); }
            void Item(uint style, short x, short y, short cx, short cy, int id, ushort cls, string text)
            {
                Align();
                w.Write(style | WS_CHILD | WS_VISIBLE); w.Write(0u); w.Write(x); w.Write(y); w.Write(cx); w.Write(cy); w.Write((ushort)id);
                w.Write((ushort)0xFFFF); w.Write(cls); Str(text); w.Write((ushort)0);
            }
            string a, b; bool check = page == Page.Options;
            (a, b) = page switch { Page.General => ("Name:", "Note:"), Page.Size => ("Width:", "Height:"), _ => ("Mark the center", "Radius:") };
            // header: style, exStyle, items, x, y, cx, cy, menu, class, title, then the font
            w.Write(DS_SETFONT | DS_CONTROL | WS_CHILD | WS_CAPTION); w.Write(0u); w.Write((ushort)4); w.Write((short)0); w.Write((short)0); w.Write((short)200); w.Write((short)100);
            w.Write((ushort)0); w.Write((ushort)0); Str("");
            w.Write((ushort)9); Str("MS Shell Dlg");
            const ushort Button = 0x0080, Edit = 0x0081, Static = 0x0082;
            if (check)
            {
                Item(BS_AUTOCHECKBOX | WS_TABSTOP, 10, 12, 120, 10, IdA, Button, a);
                Item(SS_LEFT, 10, 34, 40, 10, -1, Static, b);
            }
            else
            {
                Item(SS_LEFT, 10, 14, 40, 10, -1, Static, a);
                Item(SS_LEFT, 10, 34, 40, 10, -1, Static, b);
                Item(ES_AUTOHSCROLL | WS_TABSTOP | WS_BORDER, 55, 12, 120, 12, IdA, Edit, "");
            }
            Item(ES_AUTOHSCROLL | WS_TABSTOP | WS_BORDER, 55, 32, 120, 12, IdB, Edit, "");
            Align();
            var bytes = ms.ToArray();
            // the item count in the header: the check page has 3 items, the others 4
            BitConverter.GetBytes((ushort)(check ? 3 : 4)).CopyTo(bytes, 8);
            var p = Alloc(Marshal.AllocHGlobal(bytes.Length));
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }
    }
}
