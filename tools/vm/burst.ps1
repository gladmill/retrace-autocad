# Interactive session only (one-shot task, like shot.ps1). Takes <n> screenshots (default 14), one every 1.5 s, to
# C:\retrace-vm\out\burst-NN.png: start it, then a replay, to see what the screen shows while steps run.
param([int]$Count = 14)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
for ($i = 0; $i -lt $Count; $i++) {
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save(('C:\retrace-vm\out\burst-{0:D2}.png' -f $i)); $bmp.Dispose()
  Start-Sleep -Milliseconds 1500
}
'done' | Set-Content C:\retrace-vm\out\burst.txt
