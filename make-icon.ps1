# Generates ShotCopy.ico (16-256px, PNG entries). Run once; the .ico is kept in the repository.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot "ShotCopy.ico"

function New-RoundPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"
    # Design C: blue rounded square + four thick white corner brackets (32px grid)
    $k = $s / 32.0
    $bg = New-RoundPath (1*$k) (1*$k) (30*$k) (30*$k) (7*$k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(74, 144, 226))), $bg)
    $pw = [Math]::Max(2.0, 3.4*$k)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"
    $a = 8*$k; $b = 24*$k; $L = 5*$k
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $a, ($a+$L)), (New-Object System.Drawing.PointF $a, $a), (New-Object System.Drawing.PointF ($a+$L), $a)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($b-$L), $a), (New-Object System.Drawing.PointF $b, $a), (New-Object System.Drawing.PointF $b, ($a+$L))))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF $b, ($b-$L)), (New-Object System.Drawing.PointF $b, $b), (New-Object System.Drawing.PointF ($b-$L), $b)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($a+$L), $b), (New-Object System.Drawing.PointF $a, $b), (New-Object System.Drawing.PointF $a, ($b-$L))))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256px: PNG entry (standard for large icons)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # smaller: classic 32-bit DIB entry (readable by every Windows / .NET loader)
        $bw = New-Object System.IO.BinaryWriter $ms
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($s * $s * 4 + $maskStride * $s)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) }
        }
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$pngs = @(); foreach ($s in $sizes) { $pngs += ,(New-IconPng $s) }
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bs = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$bs); $w.Write([byte]$bs); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"Icon written: $out"
