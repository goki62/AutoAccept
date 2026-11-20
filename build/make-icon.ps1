# Draws the AutoAccept logo (green circle + check) and writes a multi-size PNG-based .ico.
param([Parameter(Mandatory = $true)][string]$Out)
Add-Type -AssemblyName System.Drawing

function New-LogoPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [Math]::Max(0.5, $s * 0.04)
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 185, 80))
    $g.FillEllipse($fill, [single]$pad, [single]$pad, [single]($s - 2 * $pad), [single]($s - 2 * $pad))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 13, 17, 23)), ([single][Math]::Max(1.5, $s * 0.13))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single]($s * 0.28)), ([single]($s * 0.52))),
        (New-Object System.Drawing.PointF ([single]($s * 0.44)), ([single]($s * 0.68))),
        (New-Object System.Drawing.PointF ([single]($s * 0.73)), ([single]($s * 0.36)))
    )
    $g.DrawLines($pen, $pts)
    $g.Dispose(); $pen.Dispose(); $fill.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 256)
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $pngs.Add([byte[]](New-LogoPng $s)) }

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Wrote $Out"
