<#
.SYNOPSIS
    生成多尺寸应用图标 app.ico（项目没有任何现成图标素材）。

.DESCRIPTION
    为什么用脚本生成而不是手工画：图标要在 16/20/24/32/40/48/64/128/256 八个尺寸下
    都清晰，手工维护八个文件不现实。改设计只需要改本脚本里的 Draw-IconBitmap 再重跑。

    设计语义：深色圆角底 + 一条上升的温度折线 + 底部网格线 = "长期温度趋势记录"。
    配色跟随项目深色主题（UI\Theme.cs），橙红曲线在深底上对比度足够，16px 下也能辨识。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\gen-icon.ps1
    powershell -ExecutionPolicy Bypass -File scripts\gen-icon.ps1 -Output D:\test.ico
#>
[CmdletBinding()]
param(
    # 输出路径，默认写到项目内供 csproj 的 ApplicationIcon 使用
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'src\ThermalTrace\app.ico' }

# 覆盖 Win11 的缩放档位：100/125/150/175/200/250/300/400/500%
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function Draw-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap(
        $Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- 圆角底板 ----
    $pad = [Math]::Max(1, [int]($Size * 0.06))
    $w = $Size - 2 * $pad
    $h = $Size - 2 * $pad
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, $w, $h)

    # 小尺寸下圆角半径要保底 2px，否则 16px 会糊成一个方块
    $radius = [Math]::Max(2, [int]($Size * 0.22))
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 32, 41, 58),
        [System.Drawing.Color]::FromArgb(255, 18, 23, 34),
        45.0)
    $g.FillPath($bg, $path)

    # ---- 底部网格线：暗示"趋势/时间轴"，不做成仪表盘（LiteMonitor 的定位）----
    $gridPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
        [Math]::Max(1.0, $Size * 0.02))
    for ($i = 1; $i -le 2; $i++) {
        $y = $rect.Y + ($rect.Height * $i / 3.0)
        $g.DrawLine($gridPen, $rect.X + $w * 0.12, $y, $rect.Right - $w * 0.12, $y)
    }

    # ---- 温度折线：左下 → 起伏 → 右上 ----
    # 【为什么用 List[PointF] 而不是嵌套数组】PowerShell 的 @( @(1,2), @(3,4) ) 会被展平，
    # 取下标时拿到的是标量而非数组，DrawPath 直接炸。这里必须用强类型集合。
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    $pts.Add((New-Object System.Drawing.PointF(
        [float]($rect.X + $w * 0.14), [float]($rect.Y + $h * 0.70))))
    $pts.Add((New-Object System.Drawing.PointF(
        [float]($rect.X + $w * 0.34), [float]($rect.Y + $h * 0.52))))
    $pts.Add((New-Object System.Drawing.PointF(
        [float]($rect.X + $w * 0.50), [float]($rect.Y + $h * 0.60))))
    $pts.Add((New-Object System.Drawing.PointF(
        [float]($rect.X + $w * 0.66), [float]($rect.Y + $h * 0.32))))
    $pts.Add((New-Object System.Drawing.PointF(
        [float]($rect.X + $w * 0.86), [float]($rect.Y + $h * 0.22))))

    $curve = New-Object System.Drawing.Drawing2D.GraphicsPath
    $curve.AddLines($pts.ToArray())

    $linePen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(255, 255, 122, 89),
        [Math]::Max(2.0, $Size * 0.095))
    $linePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawPath($linePen, $curve)

    # ---- 末端"当前值"圆点 ----
    $dotR = [float][Math]::Max(2.0, $Size * 0.085)
    $last = $pts[$pts.Count - 1]
    $dot = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 255, 224, 168))
    $g.FillEllipse($dot, $last.X - $dotR, $last.Y - $dotR, $dotR * 2, $dotR * 2)

    $g.Dispose()
    $bmp
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $Bitmap.Dispose()
    # 逗号前缀：确保返回的是数组本身而不是被 PowerShell 展平
    ,$bytes
}

function ConvertTo-DibBytes {
    <#
        传统 BMP(DIB) 编码的 ICO 条目：BITMAPINFOHEADER + 自下而上的 BGRA 像素 + AND 掩码。
        小尺寸必须用这种格式，理由见下方主循环的注释。
    #>
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $stride = $w * 4

    # LockBits 按 BGRA 读出，和 ICO 里 DIB 的像素顺序一致
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $locked = $Bitmap.LockBits(
        $rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $raw, 0, $raw.Length)
    $Bitmap.UnlockBits($locked)

    # DIB 是自下而上存储，逐行翻转
    $pixels = New-Object byte[] $raw.Length
    for ($y = 0; $y -lt $h; $y++) {
        [System.Array]::Copy($raw, ($y * $stride), $pixels, (($h - 1 - $y) * $stride), $stride)
    }

    # AND 掩码：每行按 4 字节对齐。32 位色已经有 alpha 通道，掩码全 0 即可
    $maskStride = [int]([Math]::Floor(($w + 31) / 32)) * 4
    $mask = New-Object byte[] ($maskStride * $h)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([UInt32]40)                 # biSize
    $bw.Write([Int32]$w)                  # biWidth
    $bw.Write([Int32]($h * 2))            # biHeight（DIB 里是实际高度的两倍）
    $bw.Write([UInt16]1)                  # biPlanes
    $bw.Write([UInt16]32)                 # biBitCount
    $bw.Write([UInt32]0)                  # biCompression = BI_RGB
    $bw.Write([UInt32]$pixels.Length)     # biSizeImage
    $bw.Write([Int32]0)                   # biXPelsPerMeter
    $bw.Write([Int32]0)                   # biYPelsPerMeter
    $bw.Write([UInt32]0)                  # biClrUsed
    $bw.Write([UInt32]0)                  # biClrImportant

    $bw.Write($pixels, 0, $pixels.Length)
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()

    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    $Bitmap.Dispose()
    ,$bytes
}

# ---- 渲染全部尺寸 ----
$images = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $bmp = Draw-IconBitmap -Size $s
    # 【为什么 ≤48 用 DIB、≥64 用 PNG】
    # 托盘与窗口图标走 Icon.ExtractAssociatedIcon → System.Drawing.Icon → GDI+，
    # 而 GDI+ 不识别 PNG 压缩的 ICO 条目，会提取失败或悄悄退回系统默认图标。
    # 16~48 是 GDI+ 实际会取的尺寸，必须保持传统 BMP 格式；
    # 64 以上只有资源管理器/安装器用得上，用 PNG 体积更小（256px 图标 DIB 要 256KB）。
    $data = if ($s -le 48) { ConvertTo-DibBytes -Bitmap $bmp }
            else { ConvertTo-PngBytes -Bitmap $bmp }
    $null = $images.Add(@{
        Size = $s
        Data = $data
    })
}

# ---- 组装 ICO（ICONDIR + ICONDIRENTRY* + 各尺寸 PNG 数据）----
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

$bw.Write([UInt16]0)          # Reserved
$bw.Write([UInt16]1)          # Type = 1 (icon)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($img in $images) {
    # 【为什么要显式强转 byte[]】PowerShell 从 hashtable 取数组值时会还原成 object[]，
    # 传给 BinaryWriter.Write 时重载解析退化成 Write(byte)——每张图只写 1 字节，
    # 产物变成只有 150 字节头部、图标全空。用带 (buffer,index,count) 的重载杜绝歧义。
    $chunk = [byte[]]$img.Data
    # ICONDIRENTRY 的宽高字段是单字节，256 必须写成 0（0 表示 256）
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)        # ColorCount = 0 (>256 色)
    $bw.Write([byte]0)        # Reserved
    $bw.Write([UInt16]1)      # Planes
    $bw.Write([UInt16]32)     # BitCount
    $bw.Write([UInt32]$chunk.Length)
    $bw.Write([UInt32]$offset)
    $offset += $chunk.Length
}

foreach ($img in $images) {
    $chunk = [byte[]]$img.Data
    $bw.Write($chunk, 0, $chunk.Length)
}

$bw.Flush()
$bytes = $ms.ToArray()
$bw.Dispose()
$ms.Dispose()

$dir = Split-Path $Output -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($Output, $bytes)

Write-Host ""
Write-Host "[OK] 已生成图标: $Output" -ForegroundColor Green
Write-Host "    尺寸: $($sizes -join ', ')"
Write-Host ("    体积: {0:N1} KB" -f ($bytes.Length / 1KB))
Write-Host ""
Write-Host "下一步: 在 ThermalTrace.csproj 加 <ApplicationIcon>app.ico</ApplicationIcon>" -ForegroundColor Yellow
