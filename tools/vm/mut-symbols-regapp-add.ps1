# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: symbol tables
$d.RegisteredApplications.Add('FX_EXTRA_REG3') | Out-Null; 'registered application FX_EXTRA_REG3'
