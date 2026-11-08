// Retrace.MfcSample: a small MFC DLL (shared MFC, Unicode) with a modal CFrameWnd + CFormView window and a CDialog that hold the controls Retrace
// covers: Win32 common controls, rich edit, date/time, month calendar, IP address, hot key, ComboBoxEx, a CCheckListBox, a CListCtrl in icon
// mode, a CTreeCtrl, a CTabCtrl, a CToolBar, and the MFC Feature Pack controls. All names are made up for the sample.
#pragma once

#include <afxwin.h>
#include <afxext.h>
#include <afxcmn.h>
#include <afxdtctl.h>
#include <afxcontrolbars.h>
#include "resource.h"

class CSampleApp : public CWinAppEx
{
public:
    BOOL InitInstance() override;
};

// The controls both windows share. A host (the form view or the dialog) owns one, forwards DoDataExchange, its tab notification and the
// date / calendar notifications to it, and asks it for the values.
class CSampleControls
{
public:
    CTabCtrl m_tab;
    CEdit m_name;
    CButton m_priority;
    CComboBoxEx m_unit;
    CRichEditCtrl m_notes, m_notes2;
    CWnd m_notes3;                 // RichEditD2DPT, created in code
    CCheckListBox m_parts;
    CComboBox m_kind;
    CDateTimeCtrl m_date, m_time;
    CMonthCalCtrl m_cal;
    CIPAddressCtrl m_ip;
    CHotKeyCtrl m_hotkey;
    CListCtrl m_products;
    CTreeCtrl m_tree;
    CMFCButton m_mfcButton;
    CMFCEditBrowseCtrl m_mfcBrowse;
    CMFCColorButton m_mfcColor;
    CMFCTabCtrl m_mfcTab;
    CStatic m_mfcPage1, m_mfcPage2;
    CMFCPropertyGridCtrl m_mfcGrid;
    CMFCToolBar m_mfcBar;
    CImageList m_icons;
    CString m_dateEcho, m_calEcho;
    int m_dtnCount = 0;

    void Bind(CDataExchange* pDX);
    void Init(CWnd* host);
    void ShowPage(int page);
    void SetViewMode(DWORD style);
    void OnDate(NMHDR*, LRESULT* r);
    void OnCal(NMHDR*, LRESULT* r);
    CString Describe(CWnd* host) const;
};

class CSampleView : public CFormView
{
    DECLARE_DYNCREATE(CSampleView)
public:
    enum { IDD = IDD_SAMPLE_VIEW };
    CSampleView() : CFormView(IDD) {}
    CSampleControls c;
    CString result;
    bool added = false;
protected:
    void DoDataExchange(CDataExchange* pDX) override;
    void OnInitialUpdate() override;
    afx_msg void OnTab(NMHDR*, LRESULT*);
    afx_msg void OnDate(NMHDR* h, LRESULT* r) { c.OnDate(h, r); }
    afx_msg void OnCal(NMHDR* h, LRESULT* r) { c.OnCal(h, r); }
    afx_msg void OnAdd();
    DECLARE_MESSAGE_MAP()
};

class CSampleFrame : public CFrameWnd
{
public:
    CToolBar m_bar;
    CSampleView* View() { return (CSampleView*)GetDescendantWindow(AFX_IDW_PANE_FIRST, TRUE); }
protected:
    int OnCreate(LPCREATESTRUCT cs);
    afx_msg void OnViewIcons();
    afx_msg void OnViewList();
    afx_msg void OnViewDetails();
    afx_msg void OnClose();
    afx_msg BOOL OnTip(UINT, NMHDR*, LRESULT*);
    DECLARE_MESSAGE_MAP()
};

class CSampleDlg : public CDialog
{
public:
    enum { IDD = IDD_SAMPLE_DIALOG };
    CSampleDlg(CWnd* parent) : CDialog(IDD, parent) {}
    CSampleControls c;
    CToolBar m_bar;
    CString result;
protected:
    void DoDataExchange(CDataExchange* pDX) override;
    BOOL OnInitDialog() override;
    void OnOK() override;
    afx_msg void OnTab(NMHDR*, LRESULT*);
    afx_msg void OnDate(NMHDR* h, LRESULT* r) { c.OnDate(h, r); }
    afx_msg void OnCal(NMHDR* h, LRESULT* r) { c.OnCal(h, r); }
    afx_msg void OnViewIcons();
    afx_msg void OnViewList();
    afx_msg void OnViewDetails();
    DECLARE_MESSAGE_MAP()
};
