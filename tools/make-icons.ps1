# 生成 src\HaTray\Assets 下的图标：托盘用的白 / 黑两套（跟随任务栏深浅色），加程序图标。
# 图形是「托盘上的家」（2026-10-18 用户从四套方案里选的 A）：实心房子带门洞，下面一条托盘。
# 实心图形在 16 像素下也看得清；原来的单线房子太细。只在改图标时跑一次，产物提交进仓库。
#   powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\HaTray\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# 在 24 × 24 的格子里画：房子（门洞挖空）和托盘。$box 是图形占的边长，($ox, $oy) 是左上角。
function Add-Glyph([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$ox, [single]$oy, [single]$box) {
    $k = $box / 24
    $pt = { param($x, $y) New-Object System.Drawing.PointF ([single]($ox + $x * $k)), ([single]($oy + $y * $k)) }
    $house = [System.Drawing.PointF[]]@((& $pt 12 2.6), (& $pt 21.2 10), (& $pt 21.2 18), (& $pt 2.8 18), (& $pt 2.8 10))
    $path.AddPolygon($house)
    $path.AddRectangle((New-Object System.Drawing.RectangleF ([single]($ox + 10.4 * $k)), ([single]($oy + 12 * $k)), ([single](3.2 * $k)), ([single](6 * $k))))
    Add-RoundRect $path ($ox + 2 * $k) ($oy + 19.6 * $k) (20 * $k) (2.6 * $k) (1.3 * $k)
}

function Add-RoundRect([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = 2 * $r
    $path.StartFigure()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
}

function New-Png([int]$size, [System.Drawing.Color]$fg, [bool]$app) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($app) {
        # 程序图标：macOS 那种圆角方块（圆角 22.5%），蓝底白图形，图形占 60%。
        $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $bg 0 0 ($size - 1) ($size - 1) ($size * 0.225)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 132, 255))), $bg)
        $box = $size * 0.6
        $off = ($size - $box) / 2
    } else {
        # 托盘图标：单色，占满。
        $box = $size
        $off = 0
    }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath ([System.Drawing.Drawing2D.FillMode]::Alternate)
    Add-Glyph $path $off $off $box
    $g.FillPath((New-Object System.Drawing.SolidBrush $fg), $path)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes, [System.Drawing.Color]$fg, [bool]$app) {
    $images = foreach ($s in $sizes) { , (New-Png $s $fg $app) }
    $fs = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Dispose()
}

$tray = 16, 20, 24, 32, 40, 48, 64
Write-Ico (Join-Path $assets 'TrayLight.ico') $tray ([System.Drawing.Color]::FromArgb(255, 28, 28, 28)) $false
Write-Ico (Join-Path $assets 'TrayDark.ico') $tray ([System.Drawing.Color]::White) $false
Write-Ico (Join-Path $assets 'HaTray.ico') (16, 24, 32, 48, 64, 128, 256) ([System.Drawing.Color]::White) $true
Get-ChildItem $assets -Filter *.ico | Select-Object Name, Length
