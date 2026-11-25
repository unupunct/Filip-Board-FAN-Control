# Generates src/FilipBoard.App/app.ico (fan glyph, red on charcoal). Run with Windows PowerShell 5.1.
Add-Type -AssemblyName System.Drawing
$sizes = 16,32,48,64,128,256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s,$s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,16,16,21))
  $g.FillEllipse($bg, 0, 0, $s-1, $s-1)
  $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,255,59,78))
  $c = $s/2.0
  for ($i=0; $i -lt 3; $i++) {
    $st = $g.Save(); $g.TranslateTransform($c,$c); $g.RotateTransform($i*120)
    $g.FillPie($red, [single](-$s*0.40), [single](-$s*0.40), [single]($s*0.80), [single]($s*0.80), -100, 62)
    $g.Restore($st)
  }
  $g.FillEllipse($bg, [single]($c-$s*0.12), [single]($c-$s*0.12), [single]($s*0.24), [single]($s*0.24))
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,$ms.ToArray(); $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$off = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = if ($s -ge 256) {0} else {$s}
  $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$off); $off += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\src\FilipBoard.App\app.ico'), $out.ToArray())
"icon written"
