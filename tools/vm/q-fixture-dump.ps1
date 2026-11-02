# Query: prints, per Retrace behavior, what the ACTIVE drawing contains (read-only). Diff two dumps to see what a mutation changed.
# Names, not handles, identify things, so a dump of a rebuilt fixture compares equal to the one before.
$ErrorActionPreference = 'Continue'
$inv = [Globalization.CultureInfo]::InvariantCulture
$d = $acad.ActiveDocument
function N($v) { if ($v -is [double] -or $v -is [single]) { ([math]::Round([double]$v, 4)).ToString($inv) } else { "$v" } }
function V($v) {
  if ($v -is [array] -or ($v -is [System.Collections.IEnumerable] -and $v -isnot [string])) { '(' + ((@($v) | % { N $_ }) -join ',') + ')' } else { N $v }
}
function Hdr($s) { ''; "[$s]" }
function Safe([scriptblock]$b) { try { & $b } catch { "  ERR: $($_.Exception.Message)" } }
function Xd($e, [string]$app) {
  $t = $null; $v = $null
  try { $e.GetXData($app, [ref]$t, [ref]$v) } catch { return @() }
  if ($null -eq $t) { return @() }
  $o = @(); for ($i = 0; $i -lt $t.Length; $i++) { $o += "$($t[$i]):$(V $v[$i])" }
  return $o
}
function Xr($r) { $t = $null; $v = $null; $r.GetXRecordData([ref]$t, [ref]$v); $o = @(); for ($i = 0; $i -lt $t.Length; $i++) { $o += "$($t[$i]):$(V $v[$i])" }; $o }
function Blocks { $r = New-Object System.Collections.ArrayList; [void]$r.Add($d.ModelSpace); foreach ($l in (@($d.Layouts) | ? { $_.Name -ne 'Model' } | sort TabOrder)) { [void]$r.Add($l.Block) }; , $r }
function Pos($e) { foreach ($p in 'Center', 'InsertionPoint', 'StartPoint') { try { $v = $e.$p; if ($v) { return V $v } } catch { } }; try { return V $e.Coordinates } catch { }; '?' }
function BlockName($b) { if ($b.IsLayout -and $b.Name -like '*Model_Space') { 'Model' } elseif ($b.IsLayout) { $b.Layout.Name } else { $b.Name } }

"drawing: $($d.Name)"

Hdr 'LAYERS'
Safe {
  foreach ($l in $d.Layers) {
    $c = $l.TrueColor
    $col = "aci=$($l.color)"
    if ($c.BookName) { $col += " book=$($c.BookName)/$($c.ColorName)" }
    elseif ($c.ColorMethod -eq 194) { $col += " rgb=$($c.Red),$($c.Green),$($c.Blue)" }
    "  $($l.Name): on=$($l.LayerOn) frozen=$($l.Freeze) locked=$($l.Lock) plot=$($l.Plottable) $col lt=$($l.Linetype) lw=$($l.Lineweight)"
  }
  '  linetypes: ' + ((@($d.Linetypes) | % { $_.Name } | sort) -join ', ')
  $cnt = @{}; foreach ($e in $d.ModelSpace) { $k = $e.Layer; $cnt[$k] = 1 + $cnt[$k] }
  '  model space entities per layer: ' + (($cnt.GetEnumerator() | sort Name | % { "$($_.Key)=$($_.Value)" }) -join ' ')
  foreach ($e in $d.ModelSpace) {
    if ($e.Layer -in 'WALLS', 'DOORS', 'DIM') {
      if ($e.Linetype -ne 'ByLayer' -or $e.Lineweight -ne -1 -or $e.color -ne 256) { "  override: $($e.ObjectName) on $($e.Layer) lt=$($e.Linetype) lw=$($e.Lineweight) aci=$($e.color)" }
    }
  }
}

Hdr 'LAYOUTS'
Safe {
  foreach ($l in (@($d.Layouts) | sort TabOrder)) {
    "  tab $($l.TabOrder): $($l.Name)  active=$($d.ActiveLayout.Name -eq $l.Name)"
    if ($l.Name -eq 'Model') { continue }
    $types = @{}; $texts = @()
    foreach ($e in $l.Block) {
      if ($e.ObjectName -ne 'AcDbViewport') { $types[$e.ObjectName] = 1 + $types[$e.ObjectName] }
      if ($e.ObjectName -eq 'AcDbText') { $texts += "'$($e.TextString)'@$(V $e.InsertionPoint)" }
      if ($e.ObjectName -eq 'AcDbLine') { $texts += "line $(V $e.StartPoint)-$(V $e.EndPoint) on $($e.Layer)" }
    }
    '    objects: ' + (($types.GetEnumerator() | sort Name | % { "$($_.Key)=$($_.Value)" }) -join ' ')
    $texts | % { "    $_" }
  }
}

Hdr 'XDATA'
Safe {
  $apps = @($d.RegisteredApplications | % { $_.Name } | ? { $_ -like 'FX_*' })
  '  regapps FX_*: ' + (($apps | sort) -join ', ')
  foreach ($b in (Blocks)) {
    foreach ($e in $b) {
      foreach ($a in ($apps | sort)) {
        $x = Xd $e $a
        if ($x.Count) { "  $(BlockName $b) $($e.ObjectName) on $($e.Layer) @$(Pos $e) $a  " + ($x -join ' ') }
      }
    }
  }
}

Hdr 'EXTDICT'
Safe {
  foreach ($b in (Blocks)) {
    foreach ($e in $b) {
      if (-not $e.HasExtensionDictionary -or $e.ObjectName -eq 'AcDbViewport') { continue }
      $x = $e.GetExtensionDictionary()
      "  $(BlockName $b) $($e.ObjectName) on $($e.Layer): $($x.Count) entries"
      foreach ($r in $x) { "    $($r.Name) $($r.ObjectName): " + ((Xr $r) -join ' ') }
    }
  }
  function Dd($dict, $ind) {
    foreach ($m in $dict) {
      if ($m.ObjectName -eq 'AcDbDictionary') { "$ind$($m.Name)/ ($($m.Count))"; Dd $m "$ind  " }
      elseif ($m.ObjectName -eq 'AcDbXrecord') { "$ind$($m.Name): " + ((Xr $m) -join ' ') }
    }
  }
  foreach ($dd in $d.Dictionaries) {
    if ($dd.Name -like 'FX_*') { "  dictionary $($dd.Name) ($($dd.Count))"; Dd $dd '    ' }
  }
}

Hdr 'SYMBOLS'
Safe {
  foreach ($s in $d.TextStyles) { if ($s.Name -like 'FX_*') { "  textstyle $($s.Name): font=$($s.fontFile) h=$(N $s.Height) w=$(N $s.Width) obl=$(N $s.ObliqueAngle)" } }
  $curDs = $d.GetVariable('DIMSTYLE')
  foreach ($s in $d.DimStyles) {
    if ($s.Name -eq 'Annotative') { continue }
    $d.ActiveDimStyle = $s   # made current to read its variables; restored below
    "  dimstyle $($s.Name): DIMSCALE=$(V $d.GetVariable('DIMSCALE')) DIMTXT=$(V $d.GetVariable('DIMTXT')) DIMASZ=$(V $d.GetVariable('DIMASZ')) DIMBLK='$($d.GetVariable('DIMBLK'))'"
  }
  $d.ActiveDimStyle = $d.DimStyles.Item($curDs)
  foreach ($s in $d.Blocks) {
    if ($s.IsLayout -or $s.IsXRef -or $s.Name.StartsWith('*') -or $s.Name -like 'A$*') { continue }
    $items = @(); foreach ($e in $s) { $items += $e.ObjectName -replace '^AcDb', ''; if ($e.ObjectName -eq 'AcDbAttributeDefinition') { $items[-1] = "attdef:$($e.TagString)" }; if ($e.ObjectName -eq 'AcDbBlockReference') { $items[-1] = "insert:$($e.Name)" } }
    "  block $($s.Name): $($items -join ' ')"
  }
  foreach ($u in $d.UserCoordinateSystems) { "  ucs $($u.Name): origin=$(V $u.Origin) x=$(V $u.XVector) y=$(V $u.YVector)" }
  '  regapps: ' + ((@($d.RegisteredApplications | % { $_.Name }) | ? { $_ -like 'FX_*' } | sort) -join ', ')
  $n = 0; foreach ($e in $d.ModelSpace) { if ($e.ObjectName -like 'AcDb*Dimension') { "  dimension style=$($e.StyleName) text=$($e.TextHeight)" } }
}

Hdr 'SYSVARS'
Safe {
  foreach ($v in 'CLAYER', 'CELTYPE', 'CECOLOR', 'CELWEIGHT', 'TEXTSTYLE', 'DIMSTYLE', 'LTSCALE', 'PSLTSCALE', 'INSUNITS', 'LUNITS', 'LUPREC', 'AUNITS', 'AUPREC', 'MEASUREMENT', 'PDMODE', 'PDSIZE', 'MIRRTEXT', 'FILLMODE', 'DIMSCALE', 'DIMTXT') {
    "  $v = $(V $d.GetVariable($v))"
  }
}

Hdr 'VIEWS'
Safe {
  foreach ($v in $d.Views) { "  $($v.Name) space=$(try { if ($v.LayoutId -eq 0) { 'model' } else { 'paper' } } catch { '?' }): center=$(V $v.Center) h=$(N $v.Height) w=$(N $v.Width) target=$(V $v.Target) dir=$(V $v.Direction)" }
}

Hdr 'ATTRIBUTES'
Safe {
  foreach ($b in (Blocks)) {
    foreach ($e in $b) {
      if ($e.ObjectName -ne 'AcDbBlockReference' -or -not $e.HasAttributes) { continue }
      "  $(BlockName $b) insert $($e.Name) @$(V $e.InsertionPoint)"
      foreach ($a in $e.GetAttributes()) { "    $($a.TagString) = '$($a.TextString -replace "`r?`n", '\n')' invisible=$($a.Invisible) mtext=$($a.MTextAttribute)" }
    }
  }
}

Hdr 'VIEWPORTS'
Safe {
  # ViewportOn reads False until the layout has been made current once (tested), so visit each layout, then go back
  $origLayout = $d.ActiveLayout.Name; $origSpace = $d.ActiveSpace
  $pslt = $d.GetVariable('PSLTSCALE')   # visiting layouts was seen to flip PSLTSCALE 0 -> 1; put it back
  foreach ($l in (@($d.Layouts) | ? { $_.Name -ne 'Model' } | sort TabOrder)) {
    $d.ActiveLayout = $l; Start-Sleep -Milliseconds 700
    foreach ($e in $l.Block) {
      if ($e.ObjectName -ne 'AcDbViewport') { continue }
      if ((V $e.Target) -eq '(0,0,0)') { continue }   # the layout's own paper-space view record, not a viewport made by the fixture
      $fz = (Xd $e 'ACAD') -match '^1003:' | % { $_ -replace '^1003:', '' }
      "  $($l.Name) vp$($e.Number): center=$(V $e.Center) size=$(N $e.Width)x$(N $e.Height) target=$(V $e.Target) dir=$(V $e.Direction) scale=$(N $e.CustomScale) locked=$($e.DisplayLocked) on=$($e.ViewportOn) vpFrozen=[$($fz -join ',')]"
    }
  }
  $d.ActiveLayout = $d.Layouts.Item($origLayout); if ($origLayout -ne 'Model') { $d.ActiveSpace = $origSpace }
  if ($d.GetVariable('PSLTSCALE') -ne $pslt) { $d.SetVariable('PSLTSCALE', $pslt) }
}

Hdr 'NOISE (view state, not part of any behavior)'
Safe {
  "  active layout: $($d.ActiveLayout.Name)  space: $($d.ActiveSpace)  VIEWCTR=$(V $d.GetVariable('VIEWCTR'))  VIEWSIZE=$(V $d.GetVariable('VIEWSIZE'))"
}
