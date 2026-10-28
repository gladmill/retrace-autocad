# Runs in the interactive session (logon task "AcadDialogWatch", installed by install-dialog-watch.ps1): watches AutoCAD for dialogs and
# message boxes, so a script that waits on AutoCAD can see what blocks it. Every 500 ms it lists acad.exe's visible top-level windows
# that look like dialogs (#32770, or a titled WinForms / WPF window; not the main frame, floating palettes, menus or tool tips):
#   C:\retrace-vm\out\dialogs-open.txt   the dialogs open now, one per line (empty: none), rewritten every pass
#   C:\retrace-vm\out\dialogs.log        "opened" with title, class, texts and buttons; "closed"; "answered"
#   C:\retrace-vm\out\dialogs\<time>.png a picture of each dialog when it opens
# Optional answers: C:\retrace-vm\dialog-rules.txt, one rule per line, "<title regex> => <button text>" (# comments). A dialog whose title
# matches gets that button pressed once (BM_CLICK, or UI Automation Invoke for WPF). Security prompts ("Security - ...") are never
# answered, whatever the rules say: a person decides those.
param([int]$PollMs = 500)
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class DW {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32")] public static extern bool EnumChildWindows(IntPtr h, P p, IntPtr l);
  [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32", EntryPoint="SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
  public struct RECT { public int L, T, R, B; }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Txt(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static List<IntPtr> Top(uint pid) { var l = new List<IntPtr>(); EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static List<IntPtr> Kids(IntPtr h) { var l = new List<IntPtr>(); EnumChildWindows(h, (c, x) => { if (IsWindowVisible(c)) l.Add(c); return true; }, IntPtr.Zero); return l; }
  // BM_CLICK with a timeout: a hung dialog must not hang the watcher
  public static void Press(IntPtr b) { IntPtr r; SendMessageTimeout(b, 0x00F5, IntPtr.Zero, IntPtr.Zero, 2, 2000, out r); }
}
"@
$out = 'C:\retrace-vm\out'; $log = "$out\dialogs.log"; $now = "$out\dialogs-open.txt"; $pics = "$out\dialogs"
New-Item -ItemType Directory -Force $pics | Out-Null
$rulesFile = 'C:\retrace-vm\dialog-rules.txt'

function IsDialog($h) {
  $c = [DW]::Cls($h); $t = [DW]::Txt($h)
  if ($c -eq '#32770') { return $true }
  if ($c -like 'AfxMDIFrame*' -or $c -like 'Afx:*' -or $c -like 'AfxControlBar*' -or $c -in '#32768', 'ComboLBox', 'tooltips_class32') { return $false }
  return $t -and ($c -like 'WindowsForms10.Window*' -or $c -like 'HwndWrapper*')
}
# texts and buttons: child windows for Win32 / WinForms, UI Automation for WPF (no child windows)
function Parts($h) {
  $texts = @(); $buttons = @()
  if ([DW]::Cls($h) -like 'HwndWrapper*') {
    try {
      $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
      foreach ($e in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = $e.Current.Name; if (-not $n) { continue }
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { $buttons += $n }
        elseif ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text) { $texts += $n }
        if ($texts.Count + $buttons.Count -ge 40) { break }
      }
    } catch { $texts += "(UI Automation failed: $($_.Exception.Message))" }
  } else {
    foreach ($k in [DW]::Kids($h)) {
      $t = [DW]::Txt($k).Trim(); if (-not $t) { continue }
      if ([DW]::Cls($k) -match 'Button') { $buttons += $t } else { $texts += $t }
    }
  }
  [pscustomobject]@{ Texts = $texts; Buttons = $buttons }
}
function Picture($h, $file) {
  try {
    $r = New-Object DW+RECT; [DW]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $ht = $r.B - $r.T; if ($w -le 0 -or $ht -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  } catch {}
}
function Rules {
  if (-not (Test-Path $rulesFile)) { return @() }
  @(Get-Content $rulesFile | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*#' } | ForEach-Object {
    $re, $btn = $_ -split '\s*=>\s*', 2; if ($btn) { [pscustomobject]@{ Re = $re.Trim(); Button = $btn.Trim() } } })
}
function Answer($h, $title, $button) {
  if ([DW]::Cls($h) -like 'HwndWrapper*') {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $isButton = New-Object System.Windows.Automation.PropertyCondition -ArgumentList ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
    $named = New-Object System.Windows.Automation.PropertyCondition -ArgumentList ([System.Windows.Automation.AutomationElement]::NameProperty), $button
    $cond = New-Object System.Windows.Automation.AndCondition -ArgumentList $isButton, $named
    $b = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    return $false
  }
  $b = [DW]::Kids($h) | Where-Object { [DW]::Cls($_) -match 'Button' -and ([DW]::Txt($_) -replace '&', '') -eq $button } | Select-Object -First 1
  if ($b) { [DW]::Press($b); return $true }
  return $false
}

$open = @{}   # handle -> title of the dialogs seen open
while ($true) {
  $acad = Get-Process acad -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
  $lines = @(); $current = @{}
  if ($acad) {
    $rules = Rules
    foreach ($h in [DW]::Top([uint32]$acad.Id)) {
      if (-not (IsDialog $h)) { continue }
      $key = [string]$h; $title = [DW]::Txt($h); $current[$key] = $title
      $lines += "$key class=$([DW]::Cls($h)) title=[$title]"
      if ($open.ContainsKey($key)) { continue }
      $open[$key] = $title
      $p = Parts $h; $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
      Picture $h "$pics\$stamp-$key.png"
      Add-Content -Encoding UTF8 $log ("{0:s} opened {1} class={2} title=[{3}] buttons=[{4}] text=[{5}] picture=dialogs\{6}-{1}.png" -f (Get-Date), $key, [DW]::Cls($h), $title, ($p.Buttons -join ' | '), (($p.Texts -join ' / ') -replace '\s+', ' '), $stamp)
      if ($title -match '^Security\b') { continue }   # never answered automatically
      foreach ($r in $rules) {
        if ($title -notmatch $r.Re) { continue }
        $ok = $false; try { $ok = Answer $h $title $r.Button } catch {}
        Add-Content -Encoding UTF8 $log ("{0:s} answered {1} [{2}] with '{3}': {4}" -f (Get-Date), $key, $title, $r.Button, $(if ($ok) { 'pressed' } else { 'button not found' }))
        break
      }
    }
  }
  foreach ($k in @($open.Keys)) { if (-not $current.ContainsKey($k)) { Add-Content -Encoding UTF8 $log ("{0:s} closed {1} [{2}]" -f (Get-Date), $k, $open[$k]); $open.Remove($k) } }
  Set-Content -Encoding UTF8 $now ($lines -join "`r`n")
  Start-Sleep -Milliseconds $PollMs
}
