# Fixture builders for Retrace test drawings. Dot-sourced by fx-build.ps1 ($acad in scope). Developer aid, not plugin code.
# Every Add-Fx* function fills the drawing $d; Build-Fixture picks which ones a fixture gets.
$ErrorActionPreference = 'Stop'
$script:FxDir = 'C:\temp\retrace\fixtures'

function Pt([double]$x, [double]$y, [double]$z = 0) { , [double[]]@($x, $y, $z) }
function Say($s) { Write-Output $s }

# SetVariable needs the variable's own type: read the current value and cast to it
function Set-Sv($d, [string]$name, $value) {
  $cur = $d.GetVariable($name)
  switch ($cur.GetType().Name) {
    'Int16'  { $v = [int16]$value }
    'Int32'  { $v = [int32]$value }
    'Double' { $v = [double]$value }
    default  { $v = [string]$value }
  }
  $d.SetVariable($name, $v)
}

# SendCommand, then wait until AutoCAD is idle again (CMDACTIVE = 0). Never leave a command half-answered: pass the whole answer.
function Send-Cmd($d, [string]$text) {
  $d.SendCommand($text); Start-Sleep -Milliseconds 700
  for ($i = 0; $i -lt 80; $i++) {
    try { if ([int]$d.GetVariable('CMDACTIVE') -eq 0) { break } } catch { }
    Start-Sleep -Milliseconds 250
  }
}

function Ensure-Layer($d, [string]$name, [int]$aci = 7) {
  try { return $d.Layers.Item($name) } catch { $l = $d.Layers.Add($name); $l.color = $aci; return $l }
}

function New-Fixture([string]$name) {
  $path = Join-Path $script:FxDir "fx-$name.dwg"
  foreach ($x in @($acad.Documents)) { if ($x.FullName -ieq $path) { $x.Close($false); Start-Sleep 1 } }
  New-Item -ItemType Directory -Force $script:FxDir | Out-Null
  Remove-Item $path -Force -ErrorAction SilentlyContinue
  $d = $acad.Documents.Add(); Start-Sleep 3
  $d = $acad.ActiveDocument
  $d.Activate()
  return $d
}

function Save-Fixture($d, [string]$name) {
  $d.ActiveSpace = 1
  try { $d.ActiveLayout = $d.Layouts.Item('Model') } catch { }
  $path = Join-Path $script:FxDir "fx-$name.dwg"
  $d.SaveAs($path)
  Say "saved $path ($((Get-Item $path).Length) bytes)"
}

function Add-Line($sp, $x1, $y1, $x2, $y2, [string]$layer) { $l = $sp.AddLine((Pt $x1 $y1), (Pt $x2 $y2)); if ($layer) { $l.Layer = $layer }; $l }
function Add-Txt($sp, [string]$s, $x, $y, $h, [string]$layer) { $t = $sp.AddText($s, (Pt $x $y), $h); if ($layer) { $t.Layer = $layer }; $t }

# paper-space viewports the fixture made in a layout (skips the layout's own view record, whose target is 0,0,0), in creation order
function Get-FxViewports($d, [string]$layout) {
  $d.ActiveLayout = $d.Layouts.Item($layout); $d.ActiveSpace = 0; Start-Sleep -Milliseconds 700
  $r = @(); foreach ($e in $d.Layouts.Item($layout).Block) { if ($e.ObjectName -eq 'AcDbViewport' -and ($e.Target -join ',') -ne '0,0,0') { $r += $e } }
  , $r
}
function Get-FxEntity($d, $block, [string]$type, [scriptblock]$where) {
  foreach ($e in $block) { if ($e.ObjectName -eq $type -and (& $where $e)) { return $e } }
  throw "no $type matching $where"
}
function Back-ToModel($d) { $d.ActiveSpace = 1; $d.ActiveLayout = $d.Layouts.Item('Model') }

# ---------------------------------------------------------------- 1. layers
function Add-FxLayers($d) {
  $d.Linetypes.Load('DASHED', 'acad.lin'); $d.Linetypes.Load('HIDDEN', 'acad.lin'); $d.Linetypes.Load('CENTER', 'acad.lin')
  $ms = $d.ModelSpace
  # name, aci, linetype, lineweight (hundredths of mm; -3 = default, -1 = ByLayer is not valid on a layer)
  $defs = @(
    @('WALLS', 1, 'Continuous', 50), @('DOORS', 5, 'Continuous', 25), @('DIM', 3, 'DASHED', -3),
    @('HIDE', 2, 'HIDDEN', 25), @('CTR', 6, 'CENTER', 18), @('OFF_L', 4, 'Continuous', 35),
    @('FRZ_L', 30, 'DASHED', 70), @('LOCK_L', 7, 'Continuous', -3), @('NOPLOT', 8, 'Continuous', 13),
    @('RGB_L', 7, 'Continuous', 40), @('BOOK_L', 7, 'Continuous', 30)
  )
  foreach ($x in $defs) {
    $l = Ensure-Layer $d $x[0] $x[1]; $l.color = $x[1]; $l.Linetype = $x[2]; $l.Lineweight = $x[3]
  }
  # true colour and colour-book colour
  $c = $acad.GetInterfaceObject('AutoCAD.AcCmColor.25'); $c.SetRGB(255, 128, 32); $d.Layers.Item('RGB_L').TrueColor = $c
  $book = 'none'
  foreach ($bk in @(@('PANTONE(R) solid coated', 'PANTONE 185 C'), @('DIC COLOR GUIDE(R)', 'DIC-1'), @('RAL CLASSIC', 'RAL 1000'), @('RAL K5 CLASSIC', 'RAL 1000'))) {
    try {
      $cb = $acad.GetInterfaceObject('AutoCAD.AcCmColor.25'); $cb.SetColorBookColor($bk[0], $bk[1])
      $d.Layers.Item('BOOK_L').TrueColor = $cb; $book = "$($bk[0]) / $($bk[1])"; break
    } catch { }
  }
  Say "layers: colour book colour: $book"
  # entities: several per layer, varied values
  $row = 0
  foreach ($x in $defs) {
    $n = $x[0]; $y = 10 + 14 * $row; $row++
    Add-Line $ms 0 $y (20 + 3 * $row) $y $n | Out-Null
    $ci = $ms.AddCircle((Pt (40 + $row) ($y + 3)), (2 + $row * 0.25)); $ci.Layer = $n
    $pl = $ms.AddLightWeightPolyline([double[]]@(60, $y, 70, $y, 70, ($y + 6), 60, ($y + 6))); $pl.Closed = $true; $pl.Layer = $n
    $tx = Add-Txt $ms "$n sample $row" 80 $y 2.0 $n
  }
  # entity-level overrides on top of the layers
  $o = Add-Line $ms 0 170 30 170 'WALLS'; $o.Linetype = 'HIDDEN'; $o.Lineweight = 70; $o.color = 2
  $o2 = Add-Line $ms 0 176 30 176 'DOORS'; $o2.Linetype = 'CENTER'; $o2.Lineweight = 9
  $o3 = Add-Line $ms 0 182 30 182 'DIM'; $oc = $acad.GetInterfaceObject('AutoCAD.AcCmColor.25'); $oc.SetRGB(10, 200, 100); $o3.TrueColor = $oc
  # layer states last (an off/frozen/locked layer is fine to leave; entities already exist)
  $d.Layers.Item('OFF_L').LayerOn = $false
  $d.Layers.Item('FRZ_L').Freeze = $true
  $d.Layers.Item('LOCK_L').Lock = $true
  $d.Layers.Item('NOPLOT').Plottable = $false
  Say "layers: $($d.Layers.Count) layers, model space $($ms.Count) entities"
}

# ---------------------------------------------------------------- 6. symbol tables
function Add-FxSymbols($d) {
  # text styles
  $s = $d.TextStyles.Add('FX_Arial'); $s.SetFont('Arial', $false, $false, 0, 34); $s.Height = 0; $s.Width = 1.0
  $s = $d.TextStyles.Add('FX_Narrow'); $s.fontFile = 'romans.shx'; $s.Height = 0.25; $s.Width = 0.8; $s.ObliqueAngle = 0.2618
  $s = $d.TextStyles.Add('FX_Times'); $s.SetFont('Times New Roman', $true, $true, 0, 18); $s.Height = 0.5; $s.Width = 1.25
  # block definitions: nested and with attributes
  $inner = $d.Blocks.Add((Pt 0 0), 'FX_INNER')
  $inner.AddCircle((Pt 0 0), 1.5) | Out-Null; $inner.AddLine((Pt -2 0), (Pt 2 0)) | Out-Null; $inner.AddLine((Pt 0 -2), (Pt 0 2)) | Out-Null
  $outer = $d.Blocks.Add((Pt 0 0), 'FX_OUTER')
  $outer.AddLine((Pt 0 0), (Pt 12 0)) | Out-Null; $outer.AddLine((Pt 12 0), (Pt 12 6)) | Out-Null
  $outer.InsertBlock((Pt 3 3), 'FX_INNER', 1, 1, 1, 0) | Out-Null
  $outer.InsertBlock((Pt 9 3), 'FX_INNER', 0.5, 0.5, 0.5, 0.5) | Out-Null
  $tag = $d.Blocks.Add((Pt 0 0), 'FX_TAG')
  $tag.AddCircle((Pt 0 0), 3) | Out-Null
  $tag.AddAttribute(1.0, 0, 'Tag number', (Pt -2 -0.5), 'TAGNO', 'T-000') | Out-Null
  $tag.AddAttribute(0.6, 0, 'Service', (Pt -2 -2), 'SERVICE', 'unused') | Out-Null
  $ms = $d.ModelSpace
  $ms.InsertBlock((Pt 20 300), 'FX_OUTER', 1, 1, 1, 0) | Out-Null
  $ms.InsertBlock((Pt 40 300), 'FX_OUTER', 1.5, 1.5, 1.5, 0.3) | Out-Null
  foreach ($i in 0..2) {
    $r = $ms.InsertBlock((Pt (20 + 10 * $i) 285), 'FX_TAG', 1, 1, 1, 0)
    $vals = @(@('T-101', 'feed water'), @('T-205', 'return'), @('T-317', 'vent'))[$i]
    $k = 0; foreach ($a in $r.GetAttributes()) { $a.TextString = $vals[$k]; $k++ }
  }
  # dimension styles: set the variables, copy them into a new style
  $ds = $d.DimStyles.Add('FX_DIM1'); Set-Sv $d 'DIMSCALE' 1; Set-Sv $d 'DIMTXT' 0.18; Set-Sv $d 'DIMASZ' 0.18; Set-Sv $d 'DIMBLK' ''; $ds.CopyFrom($d)
  $ds = $d.DimStyles.Add('FX_DIM2'); Set-Sv $d 'DIMSCALE' 48; Set-Sv $d 'DIMTXT' 0.125; Set-Sv $d 'DIMASZ' 0.0625; Set-Sv $d 'DIMBLK' '_ARCHTICK'; $ds.CopyFrom($d)
  foreach ($x in @(@('FX_DIM1', 280, 10), @('FX_DIM2', 280, 30))) {
    $dm = $ms.AddDimAligned((Pt 20 $x[1]), (Pt 50 $x[1]), (Pt 35 ($x[1] + 5))); $dm.StyleName = $x[0]
  }
  # UCS (two named), extra registered apps
  $d.UserCoordinateSystems.Add((Pt 100 100), (Pt 101 100), (Pt 100 101), 'FX_UCS_A') | Out-Null
  $d.UserCoordinateSystems.Add((Pt -50 20), (Pt -50 21), (Pt -51 20), 'FX_UCS_B') | Out-Null
  foreach ($a in 'FX_EXTRA_REG1', 'FX_EXTRA_REG2') { try { $d.RegisteredApplications.Add($a) | Out-Null } catch { } }
  Say "symbols: textstyles $($d.TextStyles.Count) dimstyles $($d.DimStyles.Count) blocks $($d.Blocks.Count) ucs $($d.UserCoordinateSystems.Count) regapps $($d.RegisteredApplications.Count)"
}

# ---------------------------------------------------------------- 4. xdata
function Set-Xd($ent, [string]$app, [object[]]$pairs) {
  $codes = New-Object System.Collections.Generic.List[int16]; $vals = New-Object System.Collections.Generic.List[object]
  $codes.Add(1001); $vals.Add($app)
  for ($i = 0; $i -lt $pairs.Count; $i += 2) {
    $c = [int16]$pairs[$i]; $v = $pairs[$i + 1]
    switch ($c) {
      1040 { $v = [double]$v }  1041 { $v = [double]$v }  1070 { $v = [int16]$v }  1071 { $v = [int32]$v }
      1010 { $v = [double[]]$v } 1000 { $v = [string]$v } 1002 { $v = [string]$v }
    }
    $codes.Add($c); $vals.Add($v)
  }
  $ent.SetXData([int16[]]$codes.ToArray(), [object[]]$vals.ToArray())
}

function Add-FxXdata($d) {
  foreach ($a in 'FX_APP_A', 'FX_APP_B', 'FX_APP_C') { try { $d.RegisteredApplications.Add($a) | Out-Null } catch { } }
  $ms = $d.ModelSpace
  Ensure-Layer $d 'XD' 4 | Out-Null
  $e1 = Add-Line $ms 0 400 20 400 'XD'
  Set-Xd $e1 'FX_APP_A' @(1000, 'pump station 4', 1040, 12.5, 1070, 7, 1071, 1234567, 1010, @(1.0, 2.0, 3.0), 1002, '{', 1000, 'inner', 1040, 0.001, 1002, '}')
  $e2 = $ms.AddCircle((Pt 40 400), 5); $e2.Layer = 'XD'
  Set-Xd $e2 'FX_APP_A' @(1000, 'tank', 1040, 3.75, 1070, -12, 1071, 70000)
  Set-Xd $e2 'FX_APP_B' @(1000, 'second app on same entity', 1010, @(10.5, 20.25, 0.0), 1070, 99)
  $e3 = Add-Txt $ms 'xdata text' 60 400 2 'XD'
  Set-Xd $e3 'FX_APP_C' @(1002, '{', 1000, 'a', 1002, '{', 1070, 1, 1040, 2.5, 1002, '}', 1002, '}', 1071, 42)
  $e4 = $ms.AddPoint((Pt 80 400)); $e4.Layer = 'XD'
  Set-Xd $e4 'FX_APP_B' @(1000, 'point', 1040, -0.5, 1040, 100.125, 1070, 0)
  # paper space entities with xdata
  $lay = $null
  foreach ($l in $d.Layouts) { if ($l.Name -ne 'Model') { $lay = $l; break } }
  $d.ActiveLayout = $lay; $d.ActiveSpace = 0; $ps = $d.PaperSpace
  $p1 = Add-Line $ps 1 1 5 1 'XD'
  Set-Xd $p1 'FX_APP_A' @(1000, 'paper line', 1040, 9.75, 1070, 3)
  $p2 = Add-Txt $ps 'paper xdata' 1 2 0.25 'XD'
  Set-Xd $p2 'FX_APP_C' @(1000, 'paper text', 1071, 555, 1010, @(1.0, 1.0, 0.0))
  $d.ActiveSpace = 1
  Say "xdata: 3 apps, 4 model + 2 paper entities ($($lay.Name))"
}

# ---------------------------------------------------------------- 5. extension dictionaries
function Add-FxExtDict($d) {
  $ms = $d.ModelSpace
  Ensure-Layer $d 'EXT' 5 | Out-Null
  $e1 = $ms.AddCircle((Pt 0 450), 4); $e1.Layer = 'EXT'
  $x = $e1.GetExtensionDictionary()
  $r = $x.AddXRecord('FX_PARAMS'); $r.SetXRecordData([int16[]]@(1, 40, 70, 90, 10), [object[]]@('pressure rating', [double]150.5, [int16]3, [int32]100000, [double[]]@(1.5, 2.5, 3.5)))
  $r = $x.AddXRecord('FX_NOTES'); $r.SetXRecordData([int16[]]@(1, 1), [object[]]@('first note', 'second note'))
  $e2 = $ms.AddLine((Pt 20 450), (Pt 50 460)); $e2.Layer = 'EXT'
  $x = $e2.GetExtensionDictionary()
  $r = $x.AddXRecord('FX_PARAMS'); $r.SetXRecordData([int16[]]@(1, 40, 40), [object[]]@('flow', [double]0.25, [double]-8.0))
  $e3 = Add-Txt $ms 'ext dict text' 60 450 2 'EXT'
  $x = $e3.GetExtensionDictionary()
  $r = $x.AddXRecord('FX_TAGS'); $r.SetXRecordData([int16[]]@(70, 70, 70, 1), [object[]]@([int16]1, [int16]2, [int16]3, 'tags'))
  # custom dictionary under the named object dictionary, with a nested dictionary
  $cd = $d.Dictionaries.Add('FX_DICT')
  $r = $cd.AddXRecord('TOP_REC'); $r.SetXRecordData([int16[]]@(1, 40, 70), [object[]]@('top level', [double]2.718, [int16]5))
  $sub = $cd.AddObject('FX_SUB', 'AcDbDictionary')
  $r = $sub.AddXRecord('NESTED_REC_1'); $r.SetXRecordData([int16[]]@(1, 90, 10), [object[]]@('nested one', [int32]777, [double[]]@(0.0, 5.0, 10.0)))
  $r = $sub.AddXRecord('NESTED_REC_2'); $r.SetXRecordData([int16[]]@(40, 40, 1), [object[]]@([double]1.0, [double]2.0, 'nested two'))
  Say "extdict: 3 entities with extension dictionaries, custom dictionary FX_DICT (1 xrecord + FX_SUB with 2 xrecords)"
}

# ---------------------------------------------------------------- 7. system variables
function Add-FxSysvars($d) {
  Ensure-Layer $d 'WALLS' 1 | Out-Null; Ensure-Layer $d 'DOORS' 5 | Out-Null
  try { $d.Linetypes.Load('DASHED', 'acad.lin') } catch { }
  if (-not (Test-TextStyle $d 'FX_Narrow')) { $s = $d.TextStyles.Add('FX_Narrow'); $s.fontFile = 'romans.shx' }
  if (-not (Test-DimStyle $d 'FX_DIM2')) { $d.DimStyles.Add('FX_DIM2') | Out-Null }
  $set = [ordered]@{
    CLAYER = 'DOORS'; CELTYPE = 'DASHED'; CECOLOR = '3'; CELWEIGHT = 25; TEXTSTYLE = 'FX_Narrow'; DIMSTYLE = 'FX_DIM2'
    LTSCALE = 2.5; PSLTSCALE = 0; INSUNITS = 4; LUNITS = 4; LUPREC = 3; AUNITS = 1; AUPREC = 2; MEASUREMENT = 1
    PDMODE = 35; PDSIZE = 2.5; MIRRTEXT = 1; FILLMODE = 0
  }
  foreach ($k in $set.Keys) {
    try { if ($k -eq 'DIMSTYLE') { $d.ActiveDimStyle = $d.DimStyles.Item($set[$k]) } else { Set-Sv $d $k $set[$k] } } catch { Say "sysvars: $k FAILED: $($_.Exception.Message)" }
  }
  Say ('sysvars: ' + (($set.Keys | % { "$_=$($d.GetVariable($_))" }) -join ' '))
}
function Test-TextStyle($d, $n) { try { $d.TextStyles.Item($n) | Out-Null; $true } catch { $false } }
function Test-DimStyle($d, $n) { try { $d.DimStyles.Item($n) | Out-Null; $true } catch { $false } }

# ---------------------------------------------------------------- 2/3/9. layouts, viewports, title blocks
function Add-FxTitleBlockDef($d) {
  try { $d.Blocks.Item('FX_TITLEBLK') | Out-Null; return } catch { }
  $b = $d.Blocks.Add((Pt 0 0), 'FX_TITLEBLK')
  $b.AddLightWeightPolyline([double[]]@(0, 0, 10.5, 0, 10.5, 8, 0, 8)).Closed = $true
  $b.AddLine((Pt 0 1.5), (Pt 10.5 1.5)) | Out-Null; $b.AddLine((Pt 6 0), (Pt 6 1.5)) | Out-Null
  $b.AddText('FX COMPANY', (Pt 0.2 0.2), 0.2) | Out-Null
  $b.AddAttribute(0.25, 0, 'Title', (Pt 0.2 1.0), 'TITLE', 'TITLE') | Out-Null
  $b.AddAttribute(0.2, 0, 'Drawing number', (Pt 6.2 1.0), 'NUMBER', 'DWG-000') | Out-Null
  $b.AddAttribute(0.2, 0, 'Revision', (Pt 6.2 0.6), 'REV', 'A') | Out-Null
  $b.AddAttribute(0.2, 0, 'Date', (Pt 6.2 0.2), 'DATE', '2026-01-01') | Out-Null
  $b.AddAttribute(0.2, 0, 'Drawn by', (Pt 2.5 0.2), 'DRAWNBY', 'XX') | Out-Null
  $b.AddAttribute(0.2, 1, 'Internal note', (Pt 2.5 0.6), 'INTERNAL', 'hidden note') | Out-Null   # mode 1 = invisible
  $m = $b.AddAttribute(0.2, 0, 'Description', (Pt 0.2 7.0), 'DESC', 'description')
  try { $m.MTextAttribute = $true; $m.MTextAttributeContent = 'line one\Pline two' } catch { Say "title block: multiline attribute not set: $($_.Exception.Message)" }
}

function Add-FxLayouts($d, [bool]$viewports, [bool]$titleBlocks) {
  Add-FxLayers-IfMissing $d
  Add-FxTitleBlockDef $d
  # names and tab order: Model, then A-103 Details, A-101 Plan, Z-Notes, A-102 Sections
  $l1 = $d.Layouts.Item('Layout1'); $l1.Name = 'A-101 Plan'
  $l2 = $d.Layouts.Item('Layout2'); $l2.Name = 'A-102 Sections'
  $l3 = $d.Layouts.Add('A-103 Details')
  $l4 = $d.Layouts.Add('Z-Notes')
  # the template's layouts have no plotter (paper in pixels): give every layout a PDF plotter and Letter landscape, 11 x 8.5 in
  foreach ($l in @($l1, $l2, $l3, $l4)) {
    try { $l.ConfigName = 'DWG To PDF.pc3'; $l.RefreshPlotDeviceInfo(); $l.CanonicalMediaName = 'ANSI_A_(8.50_x_11.00_Inches)'; $l.PlotRotation = 1; $l.PlotType = 1 } catch { Say "layout $($l.Name) page setup: $($_.Exception.Message)" }
  }
  $l3.TabOrder = 1; $l1.TabOrder = 2; $l4.TabOrder = 3; $l2.TabOrder = 4
  $info = @(
    @($l1, 'Ground floor plan', 'A-101', 'B', '2026-03-14', 'AL'),
    @($l2, 'Building sections', 'A-102', 'C', '2026-04-02', 'MB'),
    @($l3, 'Door and window details', 'A-103', 'A', '2026-05-21', 'CJ'),
    @($l4, 'General notes', 'Z-001', 'D', '2026-06-30', 'KS'))
  $k = 0
  foreach ($i in $info) {
    $lay = $i[0]; $k++
    $d.ActiveLayout = $lay; $d.ActiveSpace = 0; $ps = $d.PaperSpace
    # paper-space content: border lines, notes text, a title block
    Add-Line $ps 0.25 0.25 10.75 0.25 'WALLS' | Out-Null; Add-Line $ps 10.75 0.25 10.75 8.25 'WALLS' | Out-Null
    Add-Line $ps 10.75 8.25 0.25 8.25 'WALLS' | Out-Null; Add-Line $ps 0.25 8.25 0.25 0.25 'WALLS' | Out-Null
    Add-Line $ps 1 7.9 (2 + $k) 7.9 'DIM' | Out-Null
    Add-Txt $ps "Sheet note $k : $($i[1])" 0.5 8.0 0.2 'DOORS' | Out-Null
    $ps.AddText("Scale varies per viewport ($k)", (Pt 0.5 0.4), 0.15) | Out-Null
    $ref = $ps.InsertBlock((Pt 0.25 0.25), 'FX_TITLEBLK', 1, 1, 1, 0)
    $vals = @{ TITLE = $i[1]; NUMBER = $i[2]; REV = $i[3]; DATE = $i[4]; DRAWNBY = $i[5]; INTERNAL = "internal $($i[2])"; DESC = "sheet $($i[2])\Pdescription $k" }
    foreach ($a in $ref.GetAttributes()) {
      if ($vals.ContainsKey($a.TagString)) { $a.TextString = $vals[$a.TagString] }
    }
    if (-not $titleBlocks) { $ref.Delete() }
    $d.ActiveSpace = 1
  }
  if ($viewports) { Add-FxViewports $d }
  Say "layouts: $((@($d.Layouts) | sort TabOrder | % { "$($_.TabOrder):$($_.Name)" }) -join ' | ')"
}
function Add-FxLayers-IfMissing($d) {
  foreach ($x in @(@('WALLS', 1), @('DOORS', 5), @('DIM', 3), @('HIDE', 2), @('CTR', 6))) { Ensure-Layer $d $x[0] $x[1] | Out-Null }
}

function Add-FxViewports($d) {
  # model content to look at: a grid of boxes with labels, on several layers
  $ms = $d.ModelSpace
  foreach ($gx in 0..5) { foreach ($gy in 0..3) {
    $ln = @('WALLS', 'DOORS', 'DIM', 'HIDE', 'CTR')[($gx + $gy) % 5]
    $pl = $ms.AddLightWeightPolyline([double[]]@(($gx * 40 + 500), ($gy * 30 + 500), ($gx * 40 + 530), ($gy * 30 + 500), ($gx * 40 + 530), ($gy * 30 + 520), ($gx * 40 + 500), ($gy * 30 + 520)))
    $pl.Closed = $true; $pl.Layer = $ln
    Add-Txt $ms "cell $gx,$gy" ($gx * 40 + 505) ($gy * 30 + 508) 4 $ln | Out-Null
  } }
  # name, then per viewport: centre on paper (x,y), size (w,h), model target (x,y), scale, [locked], [off], [frozen layers]
  $plan = [ordered]@{
    'A-101 Plan' = @(
      @(3.0, 4.5, 5.0, 6.0, 560, 560, 0.05, $false, $true, @()),
      @(8.0, 6.5, 3.8, 3.0, 640, 570, 0.025, $true, $true, @('DIM')))
    'A-102 Sections' = @(
      @(3.0, 4.5, 5.0, 6.0, 520, 530, 0.0625, $false, $true, @('WALLS', 'HIDE')),
      @(8.0, 6.5, 3.8, 3.0, 700, 580, 0.04, $false, $false, @()),
      @(8.0, 3.5, 3.8, 3.0, 600, 620, 0.1, $false, $true, @('DOORS')))
    'A-103 Details' = @(
      , @(5.0, 4.5, 8.0, 5.5, 680, 530, 0.0833, $false, $true, @()))
    'Z-Notes' = @(
      @(5.0, 5.0, 4.0, 3.0, 540, 520, 0.2, $false, $true, @('CTR', 'DIM')),
      @(5.0, 2.5, 2.0, 1.5, 620, 600, 0.125, $true, $true, @()))
  }
  $done = @()
  foreach ($ln in $plan.Keys) {
    $d.ActiveLayout = $d.Layouts.Item($ln); $d.ActiveSpace = 0; $ps = $d.PaperSpace
    # drop the layout's default viewport
    foreach ($o in @($ps)) { if ($o.ObjectName -eq 'AcDbViewport') { try { $o.Delete() } catch { Say "viewport delete: $($_.Exception.Message)" } } }
    foreach ($v in $plan[$ln]) {
      $vp = $ps.AddPViewport((Pt $v[0] $v[1]), $v[2], $v[3])
      $vp.Display($true)
      $vp.Target = (Pt $v[4] $v[5]); $vp.CustomScale = $v[6]
      foreach ($f in $v[9]) {
        # freeze in this viewport only: VPLAYER Freeze <layer> Select <viewport picked by handle> (COM has no per-viewport freeze)
        Send-Cmd $d ("_.VPLAYER`n_F`n$f`n_S`n(handent `"$($vp.Handle)`")`n`n`n")
      }
      $done += , @($ln, $vp.Handle, $v[7], $v[8])
    }
    $d.ActiveSpace = 0
  }
  # second pass: a viewport made in a layout that was left again is saved "off", so go back and set on / off / locked for real
  foreach ($x in $done) {
    $d.ActiveLayout = $d.Layouts.Item($x[0]); $d.ActiveSpace = 0
    $vp = $d.HandleToObject($x[1]); $vp.ViewportOn = [bool]$x[3]
    if ($x[2]) { $vp.DisplayLocked = $true }
  }
  $d.ActiveSpace = 1
}

# ---------------------------------------------------------------- 8. named views
function Add-FxViews($d) {
  # COM Views.Add + ZoomCenter stored the wrong window (magnification, not height), so zoom and save with commands
  $d.ActiveLayout = $d.Layouts.Item('Model'); $d.ActiveSpace = 1
  foreach ($v in @(@('FX_MV_OVERVIEW', 300, 200, 400), @('FX_MV_LAYERS', 40, 70, 200), @('FX_MV_XDATA', 40, 400, 100), @('FX_MV_SYMBOLS', 30, 290, 60))) {
    Send-Cmd $d ("_.ZOOM`n_C`n$($v[1]),$($v[2])`n$($v[3])`n")
    Send-Cmd $d ("_.-VIEW`n_S`n$($v[0])`n")
  }
  # paper space views on two layouts
  foreach ($x in @(@('A-101 Plan', 'FX_PV_PLAN', 5.5, 4.25, 12), @('Z-Notes', 'FX_PV_NOTES', 3, 7, 4))) {
    $d.ActiveLayout = $d.Layouts.Item($x[0]); $d.ActiveSpace = 0
    Send-Cmd $d ("_.ZOOM`n_C`n$($x[2]),$($x[3])`n$($x[4])`n")
    Send-Cmd $d ("_.-VIEW`n_S`n$($x[1])`n")
  }
  $d.ActiveSpace = 1; $d.ActiveLayout = $d.Layouts.Item('Model')
  Say "views: $($d.Views.Count)"
}

# ---------------------------------------------------------------- fixtures
function Build-Fixture([string]$name) {
  $d = New-Fixture $name
  $all = $name -eq 'all'
  if ($all -or $name -eq 'layers') { Add-FxLayers $d }
  if ($all -or $name -in 'layouts', 'attrs', 'viewports') { Add-FxLayouts $d ($all -or $name -eq 'viewports') ($all -or $name -ne 'layouts') }
  if ($all -or $name -eq 'symbols') { Add-FxSymbols $d }
  if ($all -or $name -eq 'xdata') { if ($name -eq 'xdata') { Add-FxLayouts $d $false $false }; Add-FxXdata $d }
  if ($all -or $name -eq 'extdict') { Add-FxExtDict $d }
  if ($all -or $name -eq 'views') { if ($name -eq 'views') { Add-FxLayers $d; Add-FxLayouts $d $false $false }; Add-FxViews $d }
  if ($all -or $name -eq 'sysvars') { Add-FxSysvars $d }
  Save-Fixture $d $name
}
