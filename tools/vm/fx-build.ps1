# Query: builds one fixture drawing from a NEW drawing and saves C:\temp\retrace\fixtures\<name>.dwg (overwrites).
# name comes from C:\retrace-vm\fx-arg.txt (written by fixtures.sh): layers layouts viewports xdata extdict symbols sysvars views attrs all
# A build that AutoCAD refuses mid-way over COM (RPC_E_CALL_REJECTED while busy) is thrown away and started again, up to 3 times.
$name = (Get-Content C:\retrace-vm\fx-arg.txt -Raw).Trim()
. C:\retrace-vm\fx-lib.ps1
for ($try = 1; $try -le 3; $try++) {
  try { Build-Fixture $name; break }
  catch {
    "BUILD ERROR in ${name} (try $try): $($_.Exception.Message)"; $_.InvocationInfo.PositionMessage
    try { $x = $acad.ActiveDocument; if ($x.FullName -notlike '*\fixtures\fx-*') { $x.Close($false) } } catch { }
    Start-Sleep 5
  }
}
