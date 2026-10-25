# NETLOAD the Retrace plugin once (the autoloader only picks up new bundles at startup).
$fd = $doc.GetVariable('FILEDIA')
$doc.SetVariable('FILEDIA', [int16]0)
$doc.SendCommand("NETLOAD`n$env:APPDATA\Autodesk\ApplicationPlugins\Retrace.bundle\Contents\Retrace.dll`n")
$doc.SetVariable('FILEDIA', [int16]$fd)
"FILEDIA restored to $($doc.GetVariable('FILEDIA'))"
