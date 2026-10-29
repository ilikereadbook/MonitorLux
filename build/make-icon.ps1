$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# repo root = parent of this build/ folder
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
$log = New-Object System.Collections.Generic.List[string]

function New-RoundPath([System.Drawing.RectangleF]$r, [single]$rad) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [single]($rad * 2)
    if ($d -gt $r.Width) { $d = [single]$r.Width }
    if ($d -gt $r.Height) { $d = [single]$r.Height }
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc(($r.Right - $d), $r.Y, $d, $d, 270, 90)
    $p.AddArc(($r.Right - $d), ($r.Bottom - $d), $d, $d, 0, 90)
    $p.AddArc($r.X, ($r.Bottom - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Glyph([System.Drawing.Graphics]$g, [int]$size, [string]$variant) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $rect = [System.Drawing.RectangleF]::new(0, 0, $size, $size)
    $rad = [single]($size * 0.235)
    $path = New-RoundPath $rect $rad

    $c1 = [System.Drawing.Color]::FromArgb(255, 92, 198, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0, 88, 184)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c1, $c2, [single]90)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    $g.SetClip($path)
    $glossRect = [System.Drawing.RectangleF]::new(0, 0, $size, [single]($size * 0.6))
    $glossA = 64
    if ($size -lt 32) { $glossA = 34 }
    $gc1 = [System.Drawing.Color]::FromArgb($glossA, 255, 255, 255)
    $gc2 = [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
    $gb = [System.Drawing.Drawing2D.LinearGradientBrush]::new($glossRect, $gc1, $gc2, [single]90)
    $g.FillRectangle($gb, $glossRect)
    $gb.Dispose()

    $shadeRect = [System.Drawing.RectangleF]::new(0, [single]($size * 0.65), $size, [single]($size * 0.35))
    $sc1 = [System.Drawing.Color]::FromArgb(0, 0, 0, 0)
    $sc2 = [System.Drawing.Color]::FromArgb(30, 0, 30, 70)
    $sb2 = [System.Drawing.Drawing2D.LinearGradientBrush]::new($shadeRect, $sc1, $sc2, [single]90)
    $g.FillRectangle($sb2, $shadeRect)
    $sb2.Dispose()
    $g.ResetClip()

    $cx = [single]($size / 2.0)
    $cy = [single]($size / 2.0)

    if ($size -le 16 -and $variant -eq "rays4") {
        $discR = $size * 0.26; $rayW = [Math]::Max(1.3, $size * 0.115)
        $inner = $size * 0.345; $outer = $size * 0.455; $rayCount = 4
    } elseif ($size -le 16) {
        $discR = $size * 0.215; $rayW = [Math]::Max(1.0, $size * 0.10)
        $inner = $size * 0.335; $outer = $size * 0.445; $rayCount = 8
    } elseif ($size -le 20) {
        $discR = $size * 0.215; $rayW = $size * 0.09
        $inner = $size * 0.325; $outer = $size * 0.44; $rayCount = 8
    } elseif ($size -le 24) {
        $discR = $size * 0.215; $rayW = $size * 0.078
        $inner = $size * 0.315; $outer = $size * 0.425; $rayCount = 8
    } elseif ($size -le 40) {
        $discR = $size * 0.215; $rayW = $size * 0.072
        $inner = $size * 0.30; $outer = $size * 0.405; $rayCount = 8
    } else {
        $discR = $size * 0.20; $rayW = $size * 0.058
        $inner = $size * 0.288; $outer = $size * 0.385; $rayCount = 8
    }

    $whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [single]$rayW)
    $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    for ($i = 0; $i -lt $rayCount; $i++) {
        $a = [Math]::PI * 2.0 / $rayCount * $i
        if ($rayCount -eq 4) { $a = [Math]::PI / 2.0 * $i + [Math]::PI / 4.0 }
        $x1 = $cx + $inner * [Math]::Cos($a)
        $y1 = $cy + $inner * [Math]::Sin($a)
        $x2 = $cx + $outer * [Math]::Cos($a)
        $y2 = $cy + $outer * [Math]::Sin($a)
        $g.DrawLine($whitePen, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
    }
    $whitePen.Dispose()

    $wb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillEllipse($wb, [single]($cx - $discR), [single]($cy - $discR), [single]($discR * 2), [single]($discR * 2))
    $wb.Dispose()

    if ($size -ge 64) {
        $sr = $discR * 0.3
        $sb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(120, 255, 255, 255))
        $g.FillEllipse($sb, [single]($cx - $discR * 0.5), [single]($cy - $discR * 0.66), [single]($sr * 2), [single]($sr * 2))
        $sb.Dispose()
    }
}

function New-IconBitmap([int]$size, [string]$variant) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Glyph $g $size $variant
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int][Math]::Floor(($w + 31) / 32) * 4
    $zero = New-Object byte[] ($maskRow * $h)
    $bw.Write($zero)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $variantFor16 = "std"

    $entries = @()
    foreach ($s in $sizes) {
        $vname = "std"
        if ($s -eq 16) { $vname = $variantFor16 }
        $bmp = New-IconBitmap $s $vname
        $raw = Get-DibBytes $bmp
        $data = [byte[]]$raw
        $log.Add("size=$s type=" + $raw.GetType().Name + " len=" + $data.Length)
        $bmp.Dispose()
        $entries += [pscustomobject]@{ Size = $s; Data = $data }
    }

    $icoPath = Join-Path $root "assets\app.ico"
    $fs = [System.IO.File]::Create($icoPath)
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $sb = 0
        if ($e.Size -lt 256) { $sb = $e.Size }
        $bw.Write([byte]$sb); $bw.Write([byte]$sb); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$e.Data.Length); $bw.Write([int]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Data) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    $log.Add("ICO OK size=" + (Get-Item $icoPath).Length)

    $pw = 900; $ph = 470
    $sheet = [System.Drawing.Bitmap]::new($pw, $ph, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor

    $big = New-IconBitmap 256 "std"
    $g.DrawImage($big, 24, 24, 150, 150)
    $g.DrawImage($big, 190, 24, 72, 72)
    $g.DrawImage($big, 278, 24, 40, 40)
    $big.Dispose()

    $x = 340
    foreach ($s in @(48, 40, 32, 24, 20, 16)) {
        $vname = "std"; if ($s -eq 16) { $vname = $variantFor16 }
        $b = New-IconBitmap $s $vname
        $g.DrawImage($b, $x, (200 - $s), $s, $s)
        $b.Dispose()
        $x += $s + 14
    }

    $zx = 24
    foreach ($item in @(@(16, 7), @(20, 6), @(24, 5), @(32, 4))) {
        $vname = "std"; if ($item[0] -eq 16) { $vname = $variantFor16 }
        $b = New-IconBitmap $item[0] $vname
        $w = $item[0] * $item[1]
        $g.DrawImage($b, $zx, 215, $w, $w)
        $b.Dispose()
        $zx += $w + 22
    }

    $db = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
    $g.FillRectangle($db, 0, 380, $pw, 90)
    $db.Dispose()
    $dx = 30
    foreach ($s in @(16, 20, 24, 32, 40, 48)) {
        $vname = "std"; if ($s -eq 16) { $vname = $variantFor16 }
        $b = New-IconBitmap $s $vname
        $g.DrawImage($b, $dx, (380 + (90 - $s) / 2), $s, $s)
        $b.Dispose()
        $dx += $s + 24
    }
    $b = New-IconBitmap 16 $variantFor16
    $g.DrawImage($b, 420, 384, 16 * 5, 16 * 5)
    $b.Dispose()

    $g.Dispose()
    $prev = Join-Path $root "docs\icon.png"
    $sheet.Save($prev, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
    $log.Add("PREVIEW OK")
}
catch {
    $log.Add("ERROR: " + $_.Exception.Message)
    $log.Add("AT: " + $_.InvocationInfo.PositionMessage)
}

[System.IO.File]::WriteAllLines((Join-Path $dist "icon.log"), $log.ToArray(), [System.Text.Encoding]::ASCII)
