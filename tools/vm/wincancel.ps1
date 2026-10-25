# Runs in the interactive session: post IDCANCEL to every top-level window whose title matches -Title; screenshot.
param([string]$Title = "Component Updates Required")
$out='C:\retrace-vm\out'; Remove-Item "$out\done.txt" -EA 0; $res="$out\result.txt"; "" | Set-Content $res
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W {
 public delegate bool EP(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EP f, IntPtr l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 public static List<string> Hits = new List<string>();
 public static void Cancel(string t) { EnumWindows((h,l)=>{ var s=new StringBuilder(256); GetWindowText(h,s,256);
   if (IsWindowVisible(h) && s.Length>0) { Hits.Add(s.ToString()); if (s.ToString()==t) { PostMessage(h,0x0111,(IntPtr)2,IntPtr.Zero); Hits.Add("  ^ posted IDCANCEL"); } } return true; }, IntPtr.Zero); }
}
"@
[W]::Cancel($Title); [W]::Hits | % { Add-Content $res $_ }
Start-Sleep 3
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b=[Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp=New-Object Drawing.Bitmap $b.Width,$b.Height
[Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location,[Drawing.Point]::Empty,$b.Size); $bmp.Save("$out\screen.png")
'done' | Set-Content "$out\done.txt"
