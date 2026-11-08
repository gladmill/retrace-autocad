// Resource ids of the MFC sample (made-up names; see the README of Retrace.Samples).
#pragma once

#define IDD_SAMPLE_VIEW        101   // the CFormView template (child window)
#define IDD_SAMPLE_DIALOG      102   // the CDialog template

#define IDC_TAB                1001
#define IDC_NAME               1002
#define IDC_PRIORITY           1003
#define IDC_UNIT               1004   // ComboBoxEx32
#define IDC_NOTES              1005   // RichEdit20W
#define IDC_NOTES2             1006   // RICHEDIT50W
#define IDC_NOTES3_HOLDER      1007   // a static: the place of RichEditD2DPT, which is created in code
#define IDC_PARTS              1008   // CCheckListBox
#define IDC_KIND               1009   // ComboBox
#define IDC_DATE               1010   // SysDateTimePick32 (date)
#define IDC_TIME               1011   // SysDateTimePick32 (time)
#define IDC_CAL                1012   // SysMonthCal32
#define IDC_DATE_ECHO          1013
#define IDC_CAL_ECHO           1014
#define IDC_IP                 1015   // SysIPAddress32
#define IDC_HOTKEY             1016   // msctls_hotkey32
#define IDC_PRODUCTS           1017   // SysListView32, icon mode
#define IDC_TREE               1018   // SysTreeView32
#define IDC_ADD                1019
#define IDC_MFCBUTTON          1020   // CMFCButton
#define IDC_MFCBROWSE          1021   // CMFCEditBrowseCtrl
#define IDC_MFCCOLOR           1022   // CMFCColorButton
#define IDC_MFCTAB_HOLDER      1023   // static: the place of the CMFCTabCtrl
#define IDC_MFCGRID_HOLDER     1024   // static: the place of the CMFCPropertyGridCtrl
#define IDC_MFCBAR_HOLDER      1025   // static: the place of the CMFCToolBar
#define IDC_LABEL_MFC          1026
#define IDC_TOOLBAR_HOLDER     1027   // static: the place of the CToolBar in the dialog
#define IDC_MFCTAB             1030
#define IDC_MFCGRID            1031
#define IDC_MFCBAR             1032
#define IDC_NOTES3             1033
#define IDC_LASTVIEW           1034   // static: shows the last view button pressed
#define IDC_MFCPAGE1           1035
#define IDC_MFCPAGE2           1036

#define ID_PV_ICONS          2001   // the CToolBar buttons (view modes)
#define ID_PV_LIST           2002
#define ID_PV_DETAILS        2003
#define ID_MFC_ONE             2010   // the CMFCToolBar buttons
#define ID_MFC_TWO             2011

#ifndef IDC_STATIC
#define IDC_STATIC (-1)
#endif
