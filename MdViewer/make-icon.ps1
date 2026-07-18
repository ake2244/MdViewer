Add-Type -AssemblyName System.Drawing

function Render-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- rounded square with diagonal gradient ---
    $pad = [Math]::Max(1, [int]($size * 0.02))
    $side = $size - 2 * $pad
    $radius = [Math]::Max(2, [int]($side * 0.22))
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle($pad, $pad, $side, $side)
    $c1 = [System.Drawing.Color]::FromArgb(255, 67, 97, 238)    # индиго
    $c2 = [System.Drawing.Color]::FromArgb(255, 76, 201, 240)   # голубой
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    $white = [System.Drawing.Brushes]::White

    # --- letter M (left part) ---
    $fontSize = $size * 0.50
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $mRect = New-Object System.Drawing.RectangleF(($size * 0.02), ($size * 0.02), ($size * 0.66), $size)
    $g.DrawString("M", $font, $white, $mRect, $sf)
    $font.Dispose(); $sf.Dispose()

    # --- down arrow (right part) ---
    $cx = $size * 0.76
    $shaftW = $size * 0.10
    $shaftTop = $size * 0.30
    $shaftBottom = $size * 0.52
    $headW = $size * 0.26
    $tipY = $size * 0.70

    $shaft = New-Object System.Drawing.RectangleF(($cx - $shaftW / 2), $shaftTop, $shaftW, ($shaftBottom - $shaftTop))
    $g.FillRectangle($white, $shaft)

    $pts = @(
        (New-Object System.Drawing.PointF(($cx - $headW / 2), $shaftBottom)),
        (New-Object System.Drawing.PointF(($cx + $headW / 2), $shaftBottom)),
        (New-Object System.Drawing.PointF($cx, $tipY))
    )
    $g.FillPolygon($white, $pts)

    $g.Dispose()
    return $bmp
}

# DIB-кадр (классический BMP внутри ico): BITMAPINFOHEADER + BGRA снизу вверх + AND-маска
function To-DibFrame([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($s * $s * 4)); $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $bw.Write([int]$bmp.GetPixel($x, $y).ToArgb())
        }
    }
    $maskStride = [int][Math]::Ceiling($s / 32.0) * 4
    $bw.Write((New-Object byte[] ($maskStride * $s)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngList = @()
foreach ($s in $sizes) {
    $bmp = Render-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngList += , $ms.ToArray()
        $ms.Dispose()
    } else {
        $pngList += , (To-DibFrame $bmp)
    }
    $bmp.Dispose()
}

# --- pack PNG frames into .ico ---
$icoPath = "D:\Claude\marktext\MdViewer\app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$sizes.Count)      # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $wb = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$wb)             # width  (0 = 256)
    $bw.Write([byte]$wb)             # height (0 = 256)
    $bw.Write([byte]0)               # palette
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bpp
    $bw.Write([uint32]$pngList[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngList[$i].Length
}
foreach ($png in $pngList) { $bw.Write($png) }
$bw.Close(); $fs.Close()

"OK: $icoPath ($((Get-Item $icoPath).Length) bytes)"
