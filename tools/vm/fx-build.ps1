# Query: builds one fixture drawing from a NEW drawing and saves C:\temp\retrace\fixtures\<name>.dwg (overwrites).
# name comes from C:\retrace-vm\fx-arg.txt (written by fixtures.sh): layers layouts viewports xdata extdict symbols sysvars views attrs all
$name = (Get-Content C:\retrace-vm\fx-arg.txt -Raw).Trim()
. C:\retrace-vm\fx-lib.ps1
try { Build-Fixture $name } catch { "BUILD ERROR in ${name}: $($_.Exception.Message)"; $_.InvocationInfo.PositionMessage }
