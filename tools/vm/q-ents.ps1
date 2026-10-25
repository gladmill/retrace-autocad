# Model space objects by type, plus the input-related system variables.
$ms = $doc.ModelSpace
"ModelSpace count: $($ms.Count)"
$types = @{}; for ($i = 0; $i -lt $ms.Count; $i++) { $n = $ms.Item($i).ObjectName; $types[$n] = 1 + $types[$n] }
$types.GetEnumerator() | % { "  $($_.Key): $($_.Value)" }
foreach ($v in 'DYNMODE','OSMODE','AUTOSNAP','CMDNAMES','TABMODE','PICKFIRST') { "$v = $($doc.GetVariable($v))" }
"Documents: " + (@($acad.Documents) | % { $_.Name }) -join ', '
