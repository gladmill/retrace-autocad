# Interactive session only (ui-menu.sh runs it as a one-shot task). Real mouse input on controls found by UI Automation, for context menu tests
# (right-click a tree item, click a menu item) where screen coordinates are not known. Reads one action per line from C:\retrace-vm\act.txt, writes
# what it did to C:\retrace-vm\out\act.txt. An element is "<ControlType>|<Name>" (TreeItem, ListItem, MenuItem, Edit, Button ...); the Name is matched exactly,
# then without mnemonic ampersands. Actions:
#   rclick <ControlType>|<Name>   real right click at the middle of the element       lclick <ControlType>|<Name>   real left click
#   dblclick <ControlType>|<Name> real double click                                    expand <ControlType>|<Name>   ExpandCollapse pattern
#   menuitem <Name>               real left click on the MenuItem with that name; waits up to 4 s for it to show (a context menu is up)
#   menuitems                     the names of the MenuItems visible now (what a menu offers)
#   vk <hex>                      real key press by virtual-key code, e.g. vk 5D = the Menu (Apps) key (SendKeys has none; Shift+F10 is key +{F10})
#   key <sendkeys>                real keys (SendKeys syntax) to whatever has the focus      sleep <ms>
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
}
'@
$o = 'C:\retrace-vm\out\act.txt'
$ae = [System.Windows.Automation.AutomationElement]
$log = New-Object System.Collections.Generic.List[string]
function TypeOf($name) { [System.Windows.Automation.ControlType]::($name) }
function FindEl($spec) {
  $t, $n = $spec -split '\|', 2
  $c = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, (TypeOf $t))
  $all = $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
  foreach ($e in $all) { if ($e.Current.Name -eq $n -and -not $e.Current.IsOffscreen) { return $e } }
  foreach ($e in $all) { if (($e.Current.Name -replace '&', '') -eq ($n -replace '&', '') -and -not $e.Current.IsOffscreen) { return $e } }
  foreach ($e in $all) { if (($e.Current.Name -replace '&', '') -like (($n -replace '&', '') + '*') -and -not $e.Current.IsOffscreen) { return $e } }   # a Win32 menu item's name carries its accelerator ("Mark M")
  return $null
}
function Mid($e) { $r = $e.Current.BoundingRectangle; @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function Click($e, $button, $times) {
  $x, $y = Mid $e
  [M]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 250
  $down, $up = if ($button -eq 'right') { 8, 16 } else { 2, 4 }
  for ($i = 0; $i -lt $times; $i++) { [M]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); [M]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80 }
  "$x,$y"
}
foreach ($line in (Get-Content 'C:\retrace-vm\act.txt')) {
  if (-not $line.Trim()) { continue }
  $verb, $arg = $line -split ' ', 2
  switch ($verb) {
    'rclick' { $e = FindEl $arg; if ($e) { $log.Add("right click '$arg' at " + (Click $e 'right' 1)) } else { $log.Add("NOT FOUND '$arg'") } }
    'lclick' { $e = FindEl $arg; if ($e) { $log.Add("click '$arg' at " + (Click $e 'left' 1)) } else { $log.Add("NOT FOUND '$arg'") } }
    'dblclick' { $e = FindEl $arg; if ($e) { $log.Add("double click '$arg' at " + (Click $e 'left' 2)) } else { $log.Add("NOT FOUND '$arg'") } }
    'expand' { $e = FindEl $arg; if ($e) { try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); $log.Add("expanded '$arg'") } catch { $log.Add("expand failed '$arg': $($_.Exception.Message)") } } else { $log.Add("NOT FOUND '$arg'") } }
    'menuitem' {
      $e = $null; $until = (Get-Date).AddSeconds(4)
      while (-not $e -and (Get-Date) -lt $until) { $e = FindEl "MenuItem|$arg"; if (-not $e) { Start-Sleep -Milliseconds 250 } }
      if ($e) { $log.Add("menu item '$arg' clicked at " + (Click $e 'left' 1)) } else { $log.Add("NOT FOUND menu item '$arg'") } }
    'menuitems' {
      $c = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
      foreach ($e in $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) { if (-not $e.Current.IsOffscreen) { $log.Add("menu item: '" + $e.Current.Name + "'") } } }
    'findany' { # every element (any type) whose name starts with the text
      foreach ($e in $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($e.Current.Name -like "$arg*") { $log.Add("any: " + $e.Current.ControlType.ProgrammaticName + " '" + $e.Current.Name + "' off=" + $e.Current.IsOffscreen) } } }
    'vk' { $v = [Convert]::ToByte($arg.Trim(), 16)
      # KEYEVENTF_EXTENDEDKEY (1) only for the extended keys: Menu (Apps), Ins/Del/Home/End/PgUp/PgDn, the arrows, right Ctrl/Alt, LWin/RWin, Num Lock, Divide, Print Screen
      $ext = @(0x5D, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0xA3, 0xA5, 0x90, 0x6F, 0x2C, 0x5B, 0x5C) -contains [int]$v
      $scan = [byte][M]::MapVirtualKey([uint32]$v, 0)
      $f = if ($ext) { 1 } else { 0 }
      [M]::keybd_event($v, $scan, $f, [UIntPtr]::Zero); [M]::keybd_event($v, $scan, $f -bor 2, [UIntPtr]::Zero); $log.Add("vk $arg (scan $scan, extended $ext)") }
    'key' { [System.Windows.Forms.SendKeys]::SendWait($arg); $log.Add("keys '$arg'") }
    'sleep' { Start-Sleep -Milliseconds ([int]$arg) }
  }
}
$log.Add("(end)"); $log | Set-Content $o -Encoding UTF8
'done' | Set-Content 'C:\retrace-vm\out\act-done.txt'
