# Interactive session only (ui-act.sh runs it as a one-shot task). Drives AutoCAD's UI for palette and checkpoint tests: reads one action
# per line from C:\retrace-vm\act.txt, writes what it did to C:\retrace-vm\out\act.txt. Window targets are found by exact window text in
# AutoCAD's process; "text@class*" also matches the class. Actions:
#   mouse x,y         real click at a screen point (brings AutoCAD to the front first)      rawmouse x,y   the same, without that
#   rawdbl x,y        real double-click at a screen point (two clicks 80 ms apart), as is
#   shiftclick x,y    real left click with Shift held, as is (a deselect at a selection prompt)
#   drag x1,y1,x2,y2 real left-button drag, as is (a PICKDRAG window)
#   rawrclick x,y    real right-click at a screen point, as is
#   rawtab <text>     real click on the tab with that text of a tab control (found by UI Automation, clicked as is: a modal dialog stays in front)
#   rawel <window title>|<automationId>[@dx,dy]   real click in the middle of the control with that AutomationId (a WPF x:Name, a WinForms Name) in the top-level window with that title, found by UI Automation from outside (only that window is searched); @dx,dy: from its bottom-right corner instead
#   rawhold <window title>|<automationId> <ms>   the same, with the mouse button held for that long
#   move x,y          rest the mouse there (hover: opens a rolled-up / auto-hidden palette)
#   click <text>      posted click in the middle of a window (WinForms buttons react; WPF buttons do not)
#   clickat <text>,x,y   posted click at a client point     wpfclick <palette>,x,y   posted click in a palette's WPF host
#   edit <current>=<new>   WM_SETTEXT on the window showing <current> (a NumericUpDown puts its old value back)
#   keys <sendkeys>   real keys to AutoCAD's frame (SendKeys syntax)      raw <sendkeys>   real keys to whatever has the focus
#   chord             Ctrl+Shift+K after bringing AutoCAD to the front     chordraw       Ctrl+Shift+K as is
#   uia <automationId>=toggle|invoke|set:<text>   UI Automation from outside (changes values without input messages)
#   state | titled <text> | dump | rect <text>     window lists and rectangles      sleep <ms>
#   ids <text>        the visible controls under the window with that text, one line each: control id, class, text (which ids a tabbed dialog reuses)
# Bringing AutoCAD to the front taps Alt then Esc: that rolls up an open auto roll-up palette and Esc cancels a running command, so use the
# raw forms while a palette must stay open.
$o = 'C:\retrace-vm\out\act.txt'
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class A {
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsChild(IntPtr p, IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public struct RECT { public int L, T, R, B; }
  public static string Cls(IntPtr h){var s=new StringBuilder(256);GetClassName(h,s,256);return s.ToString();}
  public static string Txt(IntPtr h){var s=new StringBuilder(256);GetWindowText(h,s,256);return s.ToString();}
  public static List<IntPtr> All(uint pid){var l=new List<IntPtr>();EnumWindows((h,x)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid){l.Add(h);EnumChildWindows(h,(c,y)=>{l.Add(c);return true;},IntPtr.Zero);}return true;},IntPtr.Zero);return l;}
  public static void Click(IntPtr h){RECT r;GetClientRect(h,out r);int x=(r.R)/2,y=(r.B)/2;IntPtr lp=(IntPtr)((y<<16)|(x&0xFFFF));PostMessage(h,0x201,(IntPtr)1,lp);PostMessage(h,0x202,IntPtr.Zero,lp);}
  public static void ClickAt(IntPtr h,int x,int y){IntPtr lp=(IntPtr)((y<<16)|(x&0xFFFF));PostMessage(h,0x201,(IntPtr)1,lp);PostMessage(h,0x202,IntPtr.Zero,lp);}
}
'@
$acad = Get-Process acad | Select -First 1
$log = New-Object System.Collections.Generic.List[string]
function Front { $f = [A]::All([uint32]$acad.Id) | ? { [A]::Cls($_) -like 'AfxMDIFrame*' } | Select -First 1
  if ([A]::GetForegroundWindow() -eq $f) { return $true }
  # a hidden task may not take the foreground until a key is pressed: tap Alt, then Esc to close the ribbon key tips it shows
  [A]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [A]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
  $ok = [A]::SetForegroundWindow($f); Start-Sleep -Milliseconds 300
  [A]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [A]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
  return $ok }
function Find($text) { $cls = $null; if ($text -match '^(.+)@(.+)$') { $text = $Matches[1]; $cls = $Matches[2] }
  [A]::All([uint32]$acad.Id) | ? { [A]::IsWindowVisible($_) -and [A]::Txt($_) -eq $text -and (-not $cls -or [A]::Cls($_) -like $cls) } | Select -First 1 }
foreach ($line in (Get-Content 'C:\retrace-vm\act.txt')) {
  if (-not $line.Trim()) { continue }
  $verb, $arg = $line -split ' ', 2
  switch ($verb) {
    'click' { $h = Find $arg; if ($h) { [A]::Click($h); $log.Add("clicked '$arg' [$([A]::Cls($h))]") } else { $log.Add("NOT FOUND '$arg'") } }
    'clickat' { $t, $x, $y = $arg -split ','; $h = Find $t; if ($h) { [A]::ClickAt($h, [int]$x, [int]$y); $log.Add("clicked '$t' at $x,$y") } else { $log.Add("NOT FOUND '$t'") } }
    'edit' { $cur, $new = $arg -split '=', 2; $h = Find $cur; if ($h) { [A]::Click($h); Start-Sleep -Milliseconds 300; [A]::SendMessage($h, 0x000C, [IntPtr]::Zero, $new) | Out-Null; $log.Add("set '$cur' -> '$new'") } else { $log.Add("NOT FOUND '$cur'") } }
    'chord' {
      $ok = Front
      [A]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [A]::keybd_event(0x10,0,0,[UIntPtr]::Zero); [A]::keybd_event(0x4B,0,0,[UIntPtr]::Zero)
      [A]::keybd_event(0x4B,0,2,[UIntPtr]::Zero); [A]::keybd_event(0x10,0,2,[UIntPtr]::Zero); [A]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
      $log.Add("chord sent (foreground $ok)")
    }
    'type' { $t, $text = $arg -split '=', 2; $h = Find $t
      if (-not $h) { $log.Add("NOT FOUND '$t'"); break }
      [A]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [A]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
      $ok = [A]::SetForegroundWindow([A]::GetAncestor($h, 2)); Start-Sleep -Milliseconds 300
      [A]::Click($h); Start-Sleep -Milliseconds 300
      Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('^a'); [System.Windows.Forms.SendKeys]::SendWait($text)
      $log.Add("typed '$text' into '$t' (foreground $ok)") }
    'rect' { $h = Find $arg; if ($h) { $r = New-Object A+RECT; [A]::GetWindowRect($h, [ref]$r) | Out-Null; $log.Add("rect '$arg' $($r.L),$($r.T),$($r.R),$($r.B)") } }
    'wpfclick' { $t, $x, $y = $arg -split ','; $pg = Find "$t@AfxWnd*"
      $w = $null; if ($pg) { [A]::EnumChildWindows($pg, { param($c, $l) if ([A]::Cls($c) -like 'HwndWrapper*') { $script:w = $c; return $false }; return $true }, [IntPtr]::Zero) | Out-Null; $w = $script:w }
      if ($w) { $r = New-Object A+RECT; [A]::GetWindowRect($w, [ref]$r) | Out-Null; [A]::ClickAt($w, [int]$x, [int]$y); $log.Add("wpf click in '$t' at $x,$y (host at $($r.L),$($r.T))") } else { $log.Add("no WPF host in '$t'") } }
    'uia' { # uia <automationId>=toggle|invoke|set:<text>
      Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
      $id, $what = $arg -split '=', 2
      $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
      $el = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
      if (-not $el) { $log.Add("uia: no '$id'"); break }
      if ($what -eq 'toggle') { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
      elseif ($what -eq 'invoke') { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
      elseif ($what -like 'set:*') { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($what.Substring(4)) }
      $log.Add("uia $what on '$id'") }
    'keys' { $ok = Front
      Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait($arg); $log.Add("keys '$arg' (foreground $ok)") }
    'mouse' { $x, $y = $arg -split ','; $ok = Front; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("mouse click at $x,$y") }
    'rawmouse' { $x, $y = $arg -split ','; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("raw mouse at $x,$y") }
    'shiftclick' { $x, $y = $arg -split ','; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 200   # real click with Shift held (deselect at a selection prompt), as is
      [A]::keybd_event(0x10,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 150
      [A]::keybd_event(0x10,0,2,[UIntPtr]::Zero); $log.Add("shift-click at $x,$y") }
    'drag' { $x1, $y1, $x2, $y2 = ($arg -split ',') | % { [int]$_ }   # real left-button drag (press, move in steps, release), as is
      [A]::SetCursorPos($x1, $y1) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
      for ($i = 1; $i -le 10; $i++) { [A]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 10), [int]($y1 + ($y2 - $y1) * $i / 10)) | Out-Null; Start-Sleep -Milliseconds 60 }
      Start-Sleep -Milliseconds 200; [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("drag $x1,$y1 -> $x2,$y2") }
    'rawtab' { # the tab item is found from outside through UI Automation (a SysTabControl32's item is a TabItem), the click is a real one at its middle
      Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
      $ae = [System.Windows.Automation.AutomationElement]
      $cond = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)), (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $arg)))
      $el = $ae::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
      if (-not $el) { $log.Add("rawtab: no tab '$arg'"); break }
      $r = $el.Current.BoundingRectangle; $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
      [A]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("tab '$arg' clicked at $x,$y") }
    'rawdbl' { $x, $y = $arg -split ','; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("raw double-click at $x,$y") }
    'rawel' { # rawel <window title>|<AutomationId>[@dx,dy]: a real click in the middle of the control with that AutomationId (a WPF x:Name, a WinForms Name) in the top-level window with that title, found by UI Automation from outside.
              # Only that window is searched, so AutoCAD's own controls are never hit. @dx,dy clicks that far from the control's bottom-right corner instead (negative = inside: a scroll bar's arrow).
      Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
      $ae = [System.Windows.Automation.AutomationElement]
      $win, $rest = $arg -split '\|', 2
      if (-not $rest) { $log.Add("rawel: use '<window title>|<AutomationId>'"); break }
      $id, $off = $rest -split '@', 2
      $hw = Find $win
      if (-not $hw) { $log.Add("rawel: no window '$win'"); break }
      $w = $ae::FromHandle([IntPtr]$hw)
      $el = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id)))
      if (-not $el -or $el.Current.IsOffscreen) { $log.Add("rawel: nothing '$id' in window '$win'"); break }
      $r = $el.Current.BoundingRectangle
      if ($off) { $dx, $dy = $off -split ','; $x = [int]($r.X + $r.Width + [int]$dx); $y = [int]($r.Y + $r.Height + [int]$dy) } else { $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2) }
      [A]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 250; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("'$win|$id' clicked at $x,$y") }
    'rawhold' { # rawhold <window title>|<AutomationId> <ms>: real mouse-down in the middle of the control, held that long, then up (a slow click)
      Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
      $ae = [System.Windows.Automation.AutomationElement]
      $ms = ($arg -split ' ')[-1]; $spec = $arg.Substring(0, $arg.LastIndexOf(' '))
      $win, $id = $spec -split '\|', 2
      $hw = Find $win
      $w = if ($hw) { $ae::FromHandle([IntPtr]$hw) }
      $el = if ($w) { $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id))) }
      if (-not $el) { $log.Add("rawhold: nothing '$spec'"); break }
      $r = $el.Current.BoundingRectangle; $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
      [A]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 250; [A]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds ([int]$ms); [A]::mouse_event(4,0,0,0,[UIntPtr]::Zero); $log.Add("'$spec' held $ms ms at $x,$y") }
    'rawrclick' { $x, $y = $arg -split ','; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 200; [A]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [A]::mouse_event(16,0,0,0,[UIntPtr]::Zero); $log.Add("raw right click at $x,$y") }
    'move' { $x, $y = $arg -split ','; [A]::SetCursorPos([int]$x, [int]$y) | Out-Null; $log.Add("moved") }
    'raw' { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait($arg); $log.Add("raw '$arg'") }
    'state' { foreach ($h in [A]::All([uint32]$acad.Id)) { $t = [A]::Txt($h); if ($t -in @('Parts Finder','Search','Search ','Insert','SearchButton') -or [A]::Cls($h) -like '*LISTBOX*' -or [A]::Cls($h) -like '*Afx:*') { $r = New-Object A+RECT; [A]::GetWindowRect($h, [ref]$r) | Out-Null; $log.Add(("0x{0:X} [{1}] '{2}' vis={3} rect={4},{5},{6},{7}" -f $h.ToInt64(), [A]::Cls($h), $t, [A]::IsWindowVisible($h), $r.L, $r.T, $r.R, $r.B)) } } }
    'titled' { foreach ($h in [A]::All([uint32]$acad.Id)) { if ([A]::Txt($h) -like "*$arg*") { $r = New-Object A+RECT; [A]::GetWindowRect($h, [ref]$r) | Out-Null; $log.Add(("0x{0:X} [{1}] '{2}' vis={3} rect={4},{5},{6},{7} parent=0x{8:X}" -f $h.ToInt64(), [A]::Cls($h), [A]::Txt($h), [A]::IsWindowVisible($h), $r.L, $r.T, $r.R, $r.B, [A]::GetParent($h).ToInt64())) } } }
    'chordraw' { [A]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [A]::keybd_event(0x10,0,0,[UIntPtr]::Zero); [A]::keybd_event(0x4B,0,0,[UIntPtr]::Zero)
      [A]::keybd_event(0x4B,0,2,[UIntPtr]::Zero); [A]::keybd_event(0x10,0,2,[UIntPtr]::Zero); [A]::keybd_event(0x11,0,2,[UIntPtr]::Zero); $log.Add("chord sent as is (foreground 0x{0:X})" -f [A]::GetForegroundWindow().ToInt64()) }
    'ids' { $w = Find $arg
      if (-not $w) { $log.Add("ids: no '$arg'"); break }
      foreach ($h in [A]::All([uint32]$acad.Id)) { if ([A]::IsWindowVisible($h) -and $h -ne $w -and [A]::IsChild($w, $h)) { $log.Add(("id={0} [{1}] '{2}'" -f [A]::GetDlgCtrlID($h), [A]::Cls($h), [A]::Txt($h))) } } }
    'sleep' { Start-Sleep -Milliseconds ([int]$arg) }
    'dump' { foreach ($h in [A]::All([uint32]$acad.Id)) { if ([A]::IsWindowVisible($h)) { $p=[A]::GetParent($h); $log.Add(("0x{0:X} [{1}] '{2}' style=0x{3:X8} parent=0x{4:X}" -f $h.ToInt64(), [A]::Cls($h), [A]::Txt($h), [A]::GetWindowLong($h,-16), $p.ToInt64())) } } }
  }
}
$log | Set-Content $o -Encoding UTF8
'done' | Set-Content 'C:\retrace-vm\out\act-done.txt'
