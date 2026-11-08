#include "MfcSample.h"
#include <vector>

CSampleApp theApp;

BOOL CSampleApp::InitInstance()
{
    CWinAppEx::InitInstance();
    INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_WIN95_CLASSES | ICC_USEREX_CLASSES | ICC_DATE_CLASSES | ICC_INTERNET_CLASSES | ICC_HOTKEY_CLASS | ICC_TAB_CLASSES | ICC_TREEVIEW_CLASSES | ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES };
    InitCommonControlsEx(&icc);
    AfxInitRichEdit2();
    LoadLibraryW(L"Msftedit.dll");   // RICHEDIT50W and RichEditD2DPT
    return TRUE;
}

// ---- the shared controls ---------------------------------------------------------------------------------------------

static CString Fmt(CTime t, const wchar_t* f) { return t.Format(f); }

void CSampleControls::Bind(CDataExchange* pDX)
{
    DDX_Control(pDX, IDC_TAB, m_tab);
    DDX_Control(pDX, IDC_NAME, m_name);
    DDX_Control(pDX, IDC_PRIORITY, m_priority);
    DDX_Control(pDX, IDC_UNIT, m_unit);
    DDX_Control(pDX, IDC_NOTES, m_notes);
    DDX_Control(pDX, IDC_NOTES2, m_notes2);
    DDX_Control(pDX, IDC_PARTS, m_parts);
    DDX_Control(pDX, IDC_KIND, m_kind);
    DDX_Control(pDX, IDC_DATE, m_date);
    DDX_Control(pDX, IDC_TIME, m_time);
    DDX_Control(pDX, IDC_CAL, m_cal);
    DDX_Control(pDX, IDC_IP, m_ip);
    DDX_Control(pDX, IDC_HOTKEY, m_hotkey);
    DDX_Control(pDX, IDC_PRODUCTS, m_products);
    DDX_Control(pDX, IDC_TREE, m_tree);
    DDX_Control(pDX, IDC_MFCBUTTON, m_mfcButton);
    DDX_Control(pDX, IDC_MFCBROWSE, m_mfcBrowse);
    DDX_Control(pDX, IDC_MFCCOLOR, m_mfcColor);
}

static void PlaceOver(CWnd* host, UINT holder, CRect& rc)
{
    CWnd* h = host->GetDlgItem(holder);
    h->GetWindowRect(rc);
    host->ScreenToClient(rc);
    h->ShowWindow(SW_HIDE);
}

void CSampleControls::Init(CWnd* host)
{
    m_tab.InsertItem(0, L"General");
    m_tab.InsertItem(1, L"Dates");
    m_tab.InsertItem(2, L"Network");
    m_tab.InsertItem(3, L"Pack");

    m_name.SetWindowText(L"Sample");
    m_unit.InsertItem(&COMBOBOXEXITEM{ CBEIF_TEXT, 0, const_cast<LPWSTR>(L"mm") });
    m_unit.InsertItem(&COMBOBOXEXITEM{ CBEIF_TEXT, 1, const_cast<LPWSTR>(L"cm") });
    m_unit.InsertItem(&COMBOBOXEXITEM{ CBEIF_TEXT, 2, const_cast<LPWSTR>(L"inch") });
    m_unit.SetCurSel(0);
    m_kind.AddString(L"Bolt"); m_kind.AddString(L"Nut"); m_kind.AddString(L"Plate");
    m_kind.SetCurSel(0);
    m_parts.AddString(L"Frame"); m_parts.AddString(L"Lid"); m_parts.AddString(L"Base");
    m_notes.SetWindowText(L"");
    m_notes2.SetWindowText(L"");

    CRect rc;
    PlaceOver(host, IDC_NOTES3_HOLDER, rc);
    m_notes3.CreateEx(WS_EX_CLIENTEDGE, L"RichEditD2DPT", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_MULTILINE | ES_AUTOVSCROLL, rc, host, IDC_NOTES3);

    SYSTEMTIME st = { 2026, 1, 0, 5, 9, 0, 0, 0 };
    m_date.SetTime(&st);
    m_time.SetTime(&st);
    m_cal.SetCurSel(&st);
    m_ip.ClearAddress();

    // icon mode list with generated icons
    m_icons.Create(32, 32, ILC_COLOR24 | ILC_MASK, 4, 1);
    for (int i = 0; i < 4; i++)
    {
        CClientDC dc(host);
        CDC mem; mem.CreateCompatibleDC(&dc);
        CBitmap bmp; bmp.CreateCompatibleBitmap(&dc, 32, 32);
        CBitmap* old = mem.SelectObject(&bmp);
        mem.FillSolidRect(0, 0, 32, 32, RGB(60 + i * 50, 120, 200 - i * 40));
        mem.SelectObject(old);
        m_icons.Add(&bmp, RGB(0, 0, 0));
    }
    m_products.SetImageList(&m_icons, LVSIL_NORMAL);
    m_products.ModifyStyle(LVS_TYPEMASK, LVS_ICON | LVS_SHOWSELALWAYS);
    m_products.InsertColumn(0, L"Product", LVCFMT_LEFT, 120);
    const wchar_t* names[] = { L"Bolts", L"Nuts", L"Washers", L"Rivets" };
    for (int i = 0; i < 4; i++) m_products.InsertItem(i, names[i], i);

    HTREEITEM parts = m_tree.InsertItem(L"Parts");
    HTREEITEM fast = m_tree.InsertItem(L"Fasteners", parts);
    m_tree.InsertItem(L"M6", fast); m_tree.InsertItem(L"M8", fast);
    m_tree.InsertItem(L"Frames", parts);
    m_tree.InsertItem(L"Tools");

    // Feature Pack parts
    m_mfcButton.SetWindowText(L"Feature button");
    m_mfcBrowse.EnableFileBrowseButton();
    m_mfcColor.SetColor(RGB(200, 30, 30));
    m_mfcColor.EnableOtherButton(L"More...");

    PlaceOver(host, IDC_MFCTAB_HOLDER, rc);
    m_mfcTab.Create(CMFCTabCtrl::STYLE_3D, rc, host, IDC_MFCTAB, CMFCTabCtrl::LOCATION_TOP);
    m_mfcPage1.Create(L"first page", WS_CHILD | WS_VISIBLE, CRect(0, 0, 10, 10), &m_mfcTab, IDC_MFCPAGE1);
    m_mfcPage2.Create(L"second page", WS_CHILD | WS_VISIBLE, CRect(0, 0, 10, 10), &m_mfcTab, IDC_MFCPAGE2);
    m_mfcTab.AddTab(&m_mfcPage1, L"One", (UINT)-1, FALSE);
    m_mfcTab.AddTab(&m_mfcPage2, L"Two", (UINT)-1, FALSE);

    PlaceOver(host, IDC_MFCGRID_HOLDER, rc);
    m_mfcGrid.Create(WS_CHILD | WS_VISIBLE | WS_BORDER, rc, host, IDC_MFCGRID);
    m_mfcGrid.EnableHeaderCtrl(FALSE);
    m_mfcGrid.AddProperty(new CMFCPropertyGridProperty(L"Width", (_variant_t)10L, L"Width of the part"));
    m_mfcGrid.AddProperty(new CMFCPropertyGridProperty(L"Label", (_variant_t)L"abc", L"Label of the part"));

    PlaceOver(host, IDC_MFCBAR_HOLDER, rc);
    if (m_mfcBar.CreateEx(host, TBSTYLE_FLAT, WS_CHILD | WS_VISIBLE | CBRS_TOP | CBRS_TOOLTIPS, CRect(0, 0, 0, 0), IDC_MFCBAR))
    {
        m_mfcBar.InsertButton(CMFCToolBarButton(ID_MFC_ONE, -1, L"One"));
        m_mfcBar.InsertButton(CMFCToolBarButton(ID_MFC_TWO, -1, L"Two"));
        m_mfcBar.SetWindowPos(nullptr, rc.left, rc.top, rc.Width(), 28, SWP_NOZORDER);
    }
    ShowPage(0);
}

void CSampleControls::ShowPage(int page)
{
    struct Item { UINT id; int page; };
    static const Item items[] = {
        { IDC_NAME, 0 }, { IDC_PRIORITY, 0 }, { IDC_UNIT, 0 }, { IDC_NOTES, 0 }, { IDC_NOTES2, 0 }, { IDC_NOTES3, 0 }, { IDC_PARTS, 0 }, { IDC_KIND, 0 },
        { IDC_DATE, 1 }, { IDC_TIME, 1 }, { IDC_CAL, 1 }, { IDC_DATE_ECHO, 1 }, { IDC_CAL_ECHO, 1 },
        { IDC_IP, 2 }, { IDC_HOTKEY, 2 },
        { IDC_MFCBUTTON, 3 }, { IDC_MFCBROWSE, 3 }, { IDC_MFCCOLOR, 3 }, { IDC_MFCTAB, 3 }, { IDC_MFCGRID, 3 }, { IDC_MFCBAR, 3 }, { IDC_LABEL_MFC, 3 },
    };
    CWnd* host = m_tab.GetParent();
    for (auto& it : items)
        if (CWnd* w = host->GetDlgItem(it.id)) w->ShowWindow(it.page == page ? SW_SHOW : SW_HIDE);
    // page labels (statics with ids 3000 + page) show with their page
    for (UINT id = 3000; id < 3100; id++)
        if (CWnd* w = host->GetDlgItem(id)) w->ShowWindow((int)(id - 3000) / 20 == page ? SW_SHOW : SW_HIDE);
}

void CSampleControls::SetViewMode(DWORD style)
{
    m_products.ModifyStyle(LVS_TYPEMASK, style);
    CWnd* host = m_tab.GetParent();
    if (CWnd* w = host->GetDlgItem(IDC_LASTVIEW)) w->SetWindowText(style == LVS_ICON ? L"View: icons" : style == LVS_LIST ? L"View: list" : L"View: details");
}

void CSampleControls::OnDate(NMHDR* h, LRESULT* r)
{
    m_dtnCount++;
    CTime t; m_date.GetTime(t);
    m_dateEcho = Fmt(t, L"%Y-%m-%d");
    if (CWnd* host = m_tab.GetParent()) if (CWnd* w = host->GetDlgItem(IDC_DATE_ECHO)) w->SetWindowText(L"Date seen: " + m_dateEcho);
    *r = 0;
}

void CSampleControls::OnCal(NMHDR*, LRESULT* r)
{
    CTime t; m_cal.GetCurSel(t);
    m_calEcho = Fmt(t, L"%Y-%m-%d");
    if (CWnd* host = m_tab.GetParent()) if (CWnd* w = host->GetDlgItem(IDC_CAL_ECHO)) w->SetWindowText(L"Day seen: " + m_calEcho);
    *r = 0;
}

static CString Join(const std::vector<CString>& v) { CString s; for (size_t i = 0; i < v.size(); i++) { if (i) s += L","; s += v[i]; } return s; }

CString CSampleControls::Describe(CWnd* host) const
{
    CSampleControls& c = const_cast<CSampleControls&>(*this);
    CString out, s;
    auto add = [&](const wchar_t* k, const CString& v) { out += CString(k) + L"=" + v + L"\n"; };
    c.m_name.GetWindowText(s); add(L"name", s);
    add(L"priority", c.m_priority.GetCheck() == BST_CHECKED ? L"yes" : L"no");
    { COMBOBOXEXITEM it = { CBEIF_TEXT }; wchar_t buf[64] = {}; it.pszText = buf; it.cchTextMax = 64; it.iItem = c.m_unit.GetCurSel(); c.m_unit.GetItem(&it); add(L"unit", buf); }
    c.m_notes.GetWindowText(s); add(L"notes", s);
    c.m_notes2.GetWindowText(s); add(L"notes2", s);
    c.m_notes3.GetWindowText(s); add(L"notes3", s);
    { std::vector<CString> v; for (int i = 0; i < c.m_parts.GetCount(); i++) if (c.m_parts.GetCheck(i)) { c.m_parts.GetText(i, s); v.push_back(s); } add(L"parts", Join(v)); }
    c.m_kind.GetWindowText(s); add(L"kind", s);
    { CTime t; c.m_date.GetTime(t); add(L"date", Fmt(t, L"%Y-%m-%d")); c.m_time.GetTime(t); add(L"time", Fmt(t, L"%H:%M:%S")); }
    { CTime t; c.m_cal.GetCurSel(t); add(L"day", Fmt(t, L"%Y-%m-%d")); }
    add(L"dateEcho", m_dateEcho); add(L"dayEcho", m_calEcho);
    { BYTE a, b, d, e; if (c.m_ip.GetAddress(a, b, d, e) == 4) s.Format(L"%d.%d.%d.%d", a, b, d, e); else s = L""; add(L"ip", s); }
    { WORD vk, mod; c.m_hotkey.GetHotKey(vk, mod); s.Format(L"%d/%d", mod, vk); add(L"hotkey", s); }
    { std::vector<CString> v; POSITION p = c.m_products.GetFirstSelectedItemPosition(); while (p) v.push_back(c.m_products.GetItemText(c.m_products.GetNextSelectedItem(p), 0)); add(L"products", Join(v)); }
    { std::vector<CString> v; for (HTREEITEM h = c.m_tree.GetSelectedItem(); h; h = c.m_tree.GetParentItem(h)) v.insert(v.begin(), c.m_tree.GetItemText(h)); s = Join(v); s.Replace(L",", L">"); add(L"tree", s); }
    { int i = c.m_tab.GetCurSel(); TCITEM ti = { TCIF_TEXT }; wchar_t buf[64] = {}; ti.pszText = buf; ti.cchTextMax = 64; c.m_tab.GetItem(i, &ti); add(L"tab", buf); }
    if (CWnd* w = host->GetDlgItem(IDC_LASTVIEW)) { w->GetWindowText(s); add(L"view", s); }
    c.m_mfcBrowse.GetWindowText(s); add(L"browse", s);
    s.Format(L"%06X", (unsigned)c.m_mfcColor.GetColor()); add(L"color", s);
    return out;
}

static void CopyOut(const CString& s, wchar_t* dst, int cap)
{
    if (!dst || cap <= 0) return;
    wcsncpy_s(dst, cap, s, _TRUNCATE);
}

// ---- the form view and its frame -----------------------------------------------------------------------------------

IMPLEMENT_DYNCREATE(CSampleView, CFormView)
BEGIN_MESSAGE_MAP(CSampleView, CFormView)
    ON_NOTIFY(TCN_SELCHANGE, IDC_TAB, &CSampleView::OnTab)
    ON_NOTIFY(DTN_DATETIMECHANGE, IDC_DATE, &CSampleView::OnDate)
    ON_NOTIFY(MCN_SELCHANGE, IDC_CAL, &CSampleView::OnCal)
    ON_BN_CLICKED(IDC_ADD, &CSampleView::OnAdd)
END_MESSAGE_MAP()

void CSampleView::DoDataExchange(CDataExchange* pDX) { CFormView::DoDataExchange(pDX); c.Bind(pDX); }
void CSampleView::OnTab(NMHDR*, LRESULT* r) { c.ShowPage(c.m_tab.GetCurSel()); *r = 0; }

void CSampleView::OnInitialUpdate()
{
    CFormView::OnInitialUpdate();
    if (!c.m_tab.GetSafeHwnd()) return;
    if (c.m_tab.GetItemCount() == 0) c.Init(this);
    ResizeParentToFit(FALSE);
}

void CSampleView::OnAdd()
{
    result = c.Describe(this);
    added = true;
    GetParentFrame()->PostMessage(WM_CLOSE);
}

BEGIN_MESSAGE_MAP(CSampleFrame, CFrameWnd)
    ON_WM_CREATE()
    ON_WM_CLOSE()
    ON_NOTIFY_EX_RANGE(TTN_NEEDTEXTW, 0, 0xFFFF, &CSampleFrame::OnTip)
    ON_COMMAND(ID_PV_ICONS, &CSampleFrame::OnViewIcons)
    ON_COMMAND(ID_PV_LIST, &CSampleFrame::OnViewList)
    ON_COMMAND(ID_PV_DETAILS, &CSampleFrame::OnViewDetails)
END_MESSAGE_MAP()

int CSampleFrame::OnCreate(LPCREATESTRUCT cs)
{
    if (CFrameWnd::OnCreate(cs) == -1) return -1;
    if (!m_bar.CreateEx(this, TBSTYLE_FLAT, WS_CHILD | WS_VISIBLE | CBRS_TOP | CBRS_TOOLTIPS | CBRS_FLYBY)) return -1;
    static UINT ids[] = { ID_PV_ICONS, ID_PV_LIST, ID_PV_DETAILS };
    m_bar.SetButtons(ids, 3);
    CClientDC dc(this);
    CDC mem; mem.CreateCompatibleDC(&dc);
    CBitmap bmp; bmp.CreateCompatibleBitmap(&dc, 16 * 3, 15);
    CBitmap* old = mem.SelectObject(&bmp);
    for (int i = 0; i < 3; i++) mem.FillSolidRect(i * 16, 0, 16, 15, RGB(40 + i * 80, 100, 180 - i * 60));
    mem.SelectObject(old);
    m_bar.SetBitmap((HBITMAP)bmp.Detach());
    return 0;
}

// The tooltips of the view buttons, answered here (a frame of a regular DLL does not find its string resources when the message loop is the host's).
BOOL CSampleFrame::OnTip(UINT, NMHDR* h, LRESULT* r)
{
    auto* t = reinterpret_cast<NMTTDISPINFOW*>(h);
    UINT id = (UINT)h->idFrom;
    if (t->uFlags & TTF_IDISHWND) id = (UINT)::GetDlgCtrlID((HWND)h->idFrom);
    const wchar_t* s = id == ID_PV_ICONS ? L"Icon view" : id == ID_PV_LIST ? L"List view" : id == ID_PV_DETAILS ? L"Detail view" : nullptr;
    if (!s) return FALSE;
    wcscpy_s(t->szText, s);
    *r = 0;
    return TRUE;
}

void CSampleFrame::OnViewIcons() { if (auto* v = View()) v->c.SetViewMode(LVS_ICON); }
void CSampleFrame::OnViewList() { if (auto* v = View()) v->c.SetViewMode(LVS_LIST); }
void CSampleFrame::OnViewDetails() { if (auto* v = View()) v->c.SetViewMode(LVS_REPORT); }
void CSampleFrame::OnClose()
{
    // the close box cancels; Add posts WM_CLOSE after it filled the result
    EndModalLoop(IDCANCEL);
}

extern "C" __declspec(dllexport) int __cdecl RetraceMfcFrame(HWND owner, wchar_t* result, int cap)
{
    AFX_MANAGE_STATE(AfxGetStaticModuleState());
    CSampleFrame* frame = new CSampleFrame;
    CCreateContext ctx;
    ctx.m_pNewViewClass = RUNTIME_CLASS(CSampleView);
    CWnd* parent = CWnd::FromHandle(owner);
    if (!frame->Create(nullptr, L"MFC frame sample", WS_POPUP | WS_CAPTION | WS_SYSMENU, CRect(120, 120, 900, 700), parent, nullptr, 0, &ctx))
    {
        // MFC's own pattern (a CMainFrame in InitInstance): no delete. A CFrameWnd deletes itself in PostNcDestroy once its window has been created and destroyed, which is what a failed
        // Create after the HWND existed does, so a delete here would be a second one. (A Create that fails before any window exists leaks the small object; the sample accepts that.)
        return -1;
    }
    if (owner) ::EnableWindow(owner, FALSE);
    frame->InitialUpdateFrame(nullptr, TRUE);   // the view reads its controls and the frame is shown
    frame->CenterWindow(parent);
    frame->RunModalLoop(0);
    CSampleView* v = frame->View();
    bool added = v && v->added;
    if (added) CopyOut(v->result, result, cap);
    if (owner) { ::EnableWindow(owner, TRUE); ::SetForegroundWindow(owner); }
    frame->DestroyWindow();
    return added ? 1 : 0;
}

// ---- the dialog ------------------------------------------------------------------------------------------------------

BEGIN_MESSAGE_MAP(CSampleDlg, CDialog)
    ON_NOTIFY(TCN_SELCHANGE, IDC_TAB, &CSampleDlg::OnTab)
    ON_NOTIFY(DTN_DATETIMECHANGE, IDC_DATE, &CSampleDlg::OnDate)
    ON_NOTIFY(MCN_SELCHANGE, IDC_CAL, &CSampleDlg::OnCal)
    ON_COMMAND(ID_PV_ICONS, &CSampleDlg::OnViewIcons)
    ON_COMMAND(ID_PV_LIST, &CSampleDlg::OnViewList)
    ON_COMMAND(ID_PV_DETAILS, &CSampleDlg::OnViewDetails)
END_MESSAGE_MAP()

void CSampleDlg::DoDataExchange(CDataExchange* pDX) { CDialog::DoDataExchange(pDX); c.Bind(pDX); }
void CSampleDlg::OnTab(NMHDR*, LRESULT* r) { c.ShowPage(c.m_tab.GetCurSel()); *r = 0; }
void CSampleDlg::OnViewIcons() { c.SetViewMode(LVS_ICON); }
void CSampleDlg::OnViewList() { c.SetViewMode(LVS_LIST); }
void CSampleDlg::OnViewDetails() { c.SetViewMode(LVS_REPORT); }

BOOL CSampleDlg::OnInitDialog()
{
    CDialog::OnInitDialog();
    c.Init(this);
    CRect rc;
    CWnd* h = GetDlgItem(IDC_TOOLBAR_HOLDER);
    h->GetWindowRect(rc); ScreenToClient(rc); h->ShowWindow(SW_HIDE);
    if (m_bar.CreateEx(this, TBSTYLE_FLAT | TBSTYLE_LIST, WS_CHILD | WS_VISIBLE | CBRS_TOP | CBRS_TOOLTIPS))
    {
        static UINT ids[] = { ID_PV_ICONS, ID_PV_LIST, ID_PV_DETAILS };
        m_bar.SetButtons(ids, 3);
        {
            CClientDC dc(this);
            CDC mem; mem.CreateCompatibleDC(&dc);
            CBitmap bmp; bmp.CreateCompatibleBitmap(&dc, 16 * 3, 15);
            CBitmap* old = mem.SelectObject(&bmp);
            for (int i = 0; i < 3; i++) mem.FillSolidRect(i * 16, 0, 16, 15, RGB(40 + i * 80, 100, 180 - i * 60));
            mem.SelectObject(old);
            m_bar.SetBitmap((HBITMAP)bmp.Detach());
        }
        m_bar.GetToolBarCtrl().SetExtendedStyle(TBSTYLE_EX_MIXEDBUTTONS);
        static const wchar_t* texts[] = { L"Icons", L"List", L"Details" };
        for (int i = 0; i < 3; i++) { m_bar.SetButtonText(i, texts[i]); m_bar.SetButtonStyle(i, TBBS_BUTTON | TBBS_AUTOSIZE); }
        m_bar.SetWindowPos(nullptr, rc.left, rc.top, rc.Width(), 24, SWP_NOZORDER);
    }
    return TRUE;
}

void CSampleDlg::OnOK()
{
    result = c.Describe(this);
    CDialog::OnOK();
}

extern "C" __declspec(dllexport) int __cdecl RetraceMfcDialog(HWND owner, wchar_t* result, int cap)
{
    AFX_MANAGE_STATE(AfxGetStaticModuleState());
    CSampleDlg dlg(CWnd::FromHandle(owner));
    INT_PTR r = dlg.DoModal();
    if (r == IDOK) { CopyOut(dlg.result, result, cap); return 1; }
    return r == IDCANCEL ? 0 : -1;
}
