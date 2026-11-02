# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: symbol tables
$cur = $d.GetVariable('DIMSTYLE'); $ds = $d.DimStyles.Item('FX_DIM1'); $d.ActiveDimStyle = $ds; "dim style FX_DIM1 DIMSCALE: $($d.GetVariable('DIMSCALE')) -> 2"; Set-Sv $d 'DIMSCALE' 2; $ds.CopyFrom($d); $d.ActiveDimStyle = $d.DimStyles.Item($cur)
