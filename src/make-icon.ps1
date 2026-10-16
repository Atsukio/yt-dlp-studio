# Draws the YT DLP Studio logo and writes:
#   assets/logo.svg   for the README
#   assets/logo.png   512 px, embedded in the app for its header
#   src/app.ico       the exe icon; small sizes use a simplified drawing where the design has one
# Shapes are defined once below, as SVG path data that WPF can also parse.
#   -Design   which logo to draw (see $designs)
#   -Preview  instead of writing the files, save a comparison sheet of every design to this path
param([string]$Design = 'clapper', [string]$Preview = '')
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$inv = [Globalization.CultureInfo]::InvariantCulture

function F([double]$v) { $v.ToString('0.##', $inv) }
function Ellipse([double]$cx, [double]$cy, [double]$rx, [double]$ry, [double]$deg) {
    $t = $deg * [Math]::PI / 180
    $a = "$(F ($cx - [Math]::Cos($t) * $rx)),$(F ($cy - [Math]::Sin($t) * $rx))"
    $b = "$(F ($cx + [Math]::Cos($t) * $rx)),$(F ($cy + [Math]::Sin($t) * $rx))"
    "M $a A $(F $rx),$(F $ry) $(F $deg) 0 1 $b A $(F $rx),$(F $ry) $(F $deg) 0 1 $a Z"
}
function Circle([double]$cx, [double]$cy, [double]$r) { Ellipse $cx $cy $r $r 0 }
function Rect([double]$x, [double]$y, [double]$w, [double]$h) { "M $(F $x),$(F $y) H $(F ($x + $w)) V $(F ($y + $h)) H $(F $x) Z" }
function RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $x2 = $x + $w; $y2 = $y + $h; $arc = "$(F $r),$(F $r) 0 0 1"
    "M $(F ($x + $r)),$(F $y) H $(F ($x2 - $r)) A $arc $(F $x2),$(F ($y + $r)) V $(F ($y2 - $r)) A $arc $(F ($x2 - $r)),$(F $y2) " +
    "H $(F ($x + $r)) A $arc $(F $x),$(F ($y2 - $r)) V $(F ($y + $r)) A $arc $(F ($x + $r)),$(F $y) Z"
}

# 'tile' is the background gradient, shared in absolute coordinates so cut-outs blend into it.
$tileFrom = '#8B6CFF'; $tileTo = '#5E3EE6'; $white = '#FFFFFF'; $deep = '#4B2BD1'
$tile = @{ d = (RoundRect 0 0 256 256 58); fill = 'tile' }
$badge = @(                                              # download badge in the bottom-right corner
    @{ d = (Circle 196 196 48); fill = 'tile' },         # cut-out ring
    @{ d = (Circle 196 196 38); fill = $white },
    @{ d = (Rect 190 172 12 24); fill = $deep },
    @{ d = 'M 176,192 H 216 L 196,214 Z'; fill = $deep },
    @{ d = (Rect 178 220 36 6); fill = $deep }
)

# Each design: Full (big sizes, README, header) and Small (16-32 px icons) layer lists.
$designs = [ordered]@{}

# A. Clapperboard: a film-studio clapper whose board carries a download arrow.
$stripes = foreach ($x0 in 72, 108, 144, 180) { @{ d = "M $x0,72 H $($x0 + 17) L $($x0 + 5),96 H $($x0 - 12) Z"; fill = 'tile'; rotate = @(-11, 50, 98) } }
$clapTop = @{ d = (RoundRect 48 70 160 28 8); fill = $white; rotate = @(-11, 50, 98) }
$clapBody = @{ d = (RoundRect 48 106 160 98 16); fill = $white }
$clapArrow = @(
    @{ d = (Rect 121 122 14 42); fill = 'tile' },
    @{ d = 'M 102,156 H 154 L 128,186 Z'; fill = 'tile'; stroke = 6 }
)
$designs['clapper'] = @{
    Full  = @($tile, $clapTop) + $stripes + @($clapBody) + $clapArrow
    Small = @($tile, $clapTop) + $stripes + @($clapBody) + $clapArrow
}

# B. Mixing faders: three studio sliders at different levels.
$faders = foreach ($f in @(@(82, 150), @(128, 98), @(174, 132))) {
    @{ d = (RoundRect ($f[0] - 5) 52 10 152 5); fill = $white; opacity = 0.45 }
    @{ d = (RoundRect ($f[0] - 22) ($f[1] - 13) 44 26 9); fill = $white }
}
$designs['faders'] = @{ Full = @($tile) + $faders; Small = @($tile) + $faders }

# C. Film frame: a filmstrip frame with a play button, plus the download badge.
$film = @(@{ d = (RoundRect 36 62 184 132 18); fill = $white })
$film += foreach ($x in 54, 84, 114, 144, 174) { @{ d = (RoundRect $x 72 16 14 3); fill = 'tile' }; @{ d = (RoundRect $x 170 16 14 3); fill = 'tile' } }
$film += @{ d = 'M 112,104 L 112,152 L 152,128 Z'; fill = 'tile'; stroke = 12 }
$designs['film'] = @{ Full = @($tile) + $film + $badge; Small = @($tile) + $film }

# ---- rendering ----
$grad = New-Object System.Windows.Media.LinearGradientBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($tileFrom)),
    ([System.Windows.Media.ColorConverter]::ConvertFromString($tileTo)), (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 256, 256)
$grad.MappingMode = 'Absolute'
function Brush($fill) {
    if ($fill -eq 'tile') { return $grad }
    New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($fill))
}

function RenderBitmap([object[]]$layers, [int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256), ($size / 256)))
    foreach ($l in $layers) {
        $pushed = 0
        if ($l.rotate) { $dc.PushTransform((New-Object System.Windows.Media.RotateTransform $l.rotate[0], $l.rotate[1], $l.rotate[2])); $pushed++ }
        if ($l.opacity) { $dc.PushOpacity($l.opacity); $pushed++ }
        $pen = $null
        if ($l.stroke) { $pen = New-Object System.Windows.Media.Pen (Brush $l.fill), $l.stroke; $pen.LineJoin = 'Round' }
        $dc.DrawGeometry((Brush $l.fill), $pen, [System.Windows.Media.Geometry]::Parse($l.d))
        for ($i = 0; $i -lt $pushed; $i++) { $dc.Pop() }
    }
    $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $bmp
}

function Png($bitmapSource) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmapSource))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

if ($Preview) {
    # Comparison sheet: each design at 256 px and at real icon sizes, on dark and light backgrounds.
    $names = @($designs.Keys)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $W = 300 * $names.Count; $H = 430
    $dc.DrawRectangle((Brush '#202020'), $null, (New-Object System.Windows.Rect 0, 0, $W, 330))
    $dc.DrawRectangle((Brush '#F3F3F3'), $null, (New-Object System.Windows.Rect 0, 330, $W, 100))
    for ($i = 0; $i -lt $names.Count; $i++) {
        $d = $designs[$names[$i]]
        $dc.DrawImage((RenderBitmap $d.Full 256), (New-Object System.Windows.Rect (22 + 300 * $i), 20, 256, 256))
        $label = New-Object System.Windows.Media.FormattedText ("$([char](65 + $i)). $($names[$i])"), $inv, 'LeftToRight',
            (New-Object System.Windows.Media.Typeface 'Segoe UI'), 18, (Brush '#FFFFFF'), 1.0
        $dc.DrawText($label, (New-Object System.Windows.Point (22 + 300 * $i), 290))
        $x = 22 + 300 * $i
        foreach ($s in 16, 24, 32, 48) {
            $dc.DrawImage((RenderBitmap ($(if ($s -le 32) { $d.Small } else { $d.Full })) $s), (New-Object System.Windows.Rect $x, (380 - $s / 2), $s, $s))
            $x += $s + 18
        }
    }
    $dc.Close()
    $sheet = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $W, $H, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($dv)
    [IO.File]::WriteAllBytes($Preview, (Png $sheet))
    Write-Output "Wrote $Preview"
    return
}

$chosen = $designs[$Design]
if (-not $chosen) { throw "Unknown design '$Design'. Choose one of: $($designs.Keys -join ', ')" }

# ---- SVG ----
$svg = @(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">',
    '  <title>YT DLP Studio</title>',
    "  <defs><linearGradient id=`"tile`" gradientUnits=`"userSpaceOnUse`" x1=`"0`" y1=`"0`" x2=`"256`" y2=`"256`">" +
    "<stop offset=`"0`" stop-color=`"$tileFrom`"/><stop offset=`"1`" stop-color=`"$tileTo`"/></linearGradient></defs>"
)
foreach ($l in $chosen.Full) {
    $fill = if ($l.fill -eq 'tile') { 'url(#tile)' } else { $l.fill }
    $extra = ''
    if ($l.stroke) { $extra += " stroke=`"$fill`" stroke-width=`"$($l.stroke)`" stroke-linejoin=`"round`"" }
    if ($l.opacity) { $extra += " fill-opacity=`"$($l.opacity)`"" }
    if ($l.rotate) { $extra += " transform=`"rotate($($l.rotate -join ' '))`"" }
    $svg += "  <path d=`"$($l.d)`" fill=`"$fill`"$extra/>"
}
$svg += '</svg>'
New-Item -ItemType Directory -Force (Join-Path $repo 'assets') | Out-Null
[IO.File]::WriteAllLines((Join-Path $repo 'assets\logo.svg'), $svg, (New-Object Text.UTF8Encoding $false))

# ---- PNG + ICO (PNG-compressed frames) ----
[IO.File]::WriteAllBytes((Join-Path $repo 'assets\logo.png'), (Png (RenderBitmap $chosen.Full 512)))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Png (RenderBitmap ($(if ($s -le 32) { $chosen.Small } else { $chosen.Full })) $s)) }
$fs = [IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Output "Wrote assets\logo.svg, assets\logo.png and src\app.ico ($Design)"
