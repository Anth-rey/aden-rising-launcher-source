# Draws the launcher mark. Done in code rather than by hand so every size is
# rendered from the same geometry, and so the letters come from the wordmark's
# own bold cut instead of being clipped out of the logo -- the logo sets ADEN
# lighter than RISING, so an A taken from it never matched an R taken from it.
#
#   powershell -ExecutionPolicy Bypass -File make-icon.ps1

param(
    [int]$Size = 1024,
    [switch]$NoFrame,          # small sizes: a one-pixel frame only steals room
    [string]$Out = 'icon-full.png'
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$font = Join-Path $dir 'Fonts\nimbus-sans-narrow-bold.otf'

$pfc = New-Object System.Drawing.Text.PrivateFontCollection
$pfc.AddFontFile($font)
$family = $pfc.Families[0]

$S   = $Size
$bmp = New-Object System.Drawing.Bitmap $S, $S
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.PixelOffsetMode   = 'HighQuality'
$g.TextRenderingHint = 'AntiAliasGridFit'

function New-Blend([int[][]]$stops) {
    $cb = New-Object System.Drawing.Drawing2D.ColorBlend $stops.Count
    $cb.Colors    = @($stops | ForEach-Object { [System.Drawing.Color]::FromArgb($_[1], $_[2], $_[3], $_[4]) })
    $cb.Positions = @($stops | ForEach-Object { [float]$_[0] / 100 })
    $cb
}

# --- ground: lit from above left, falling away to near black at the corners ---
$full = New-Object System.Drawing.Rectangle 0, 0, $S, $S
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddEllipse(-0.35 * $S, -0.5 * $S, 1.9 * $S, 1.9 * $S)
$pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
$pg.CenterPoint    = New-Object System.Drawing.PointF (0.42 * $S), (0.34 * $S)
$pg.CenterColor    = [System.Drawing.Color]::FromArgb(255, 52, 49, 43)
$pg.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 13, 13, 11))
$g.FillRectangle($pg, $full)
$pg.Dispose(); $path.Dispose()

# A faint diagonal sheen, the thing that stops a flat fill reading as plastic.
$sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $S, $S), `
    ([System.Drawing.Color]::White), ([System.Drawing.Color]::White)
$sheen.InterpolationColors = New-Blend @(
    @(0,   0, 255, 255, 255),
    @(38, 16, 255, 255, 255),
    @(52,  0, 255, 255, 255),
    @(100, 0, 255, 255, 255))
$g.FillRectangle($sheen, $full)
$sheen.Dispose()

# --- frame ---
if (-not $NoFrame) {
    $inset = [int]($S * 0.055)
    $fr = New-Object System.Drawing.Rectangle $inset, $inset, ($S - 2 * $inset - 1), ($S - 2 * $inset - 1)
    $fb = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, $inset), (New-Object System.Drawing.Point 0, ($S - $inset)), `
        ([System.Drawing.Color]::FromArgb(200, 232, 200, 138)), `
        ([System.Drawing.Color]::FromArgb(140, 120, 88, 42))
    $pen = New-Object System.Drawing.Pen $fb, ([float]($S * 0.006))
    $g.DrawRectangle($pen, $fr)
    $pen.Dispose(); $fb.Dispose()
}

# --- letters, measured by their real ink so centring is exact ---
$probe = New-Object System.Drawing.Drawing2D.GraphicsPath
$probe.AddString('AR', $family, [int][System.Drawing.FontStyle]::Bold, 400, `
    (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
$b = $probe.GetBounds()
$probe.Dispose()

$targetW = if ($NoFrame) { $S * 0.86 } else { $S * 0.66 }
$scale   = $targetW / $b.Width
$em      = 400 * $scale
$ink     = New-Object System.Drawing.Drawing2D.GraphicsPath
$ink.AddString('AR', $family, [int][System.Drawing.FontStyle]::Bold, $em, `
    (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
$ib = $ink.GetBounds()
$mv = New-Object System.Drawing.Drawing2D.Matrix
$mv.Translate((($S - $ib.Width) / 2 - $ib.X), (($S - $ib.Height) / 2 - $ib.Y))
$ink.Transform($mv)
$mv.Dispose()

# Cast shadow, faked with three decreasing passes -- System.Drawing has no blur.
$d = $S * 0.012
foreach ($pass in @(@(3.0, 26), @(2.0, 34), @(1.0, 46))) {
    $sp = $ink.Clone()
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate($d * $pass[0], $d * $pass[0] * 1.15)
    $sp.Transform($m); $m.Dispose()
    $sb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([int]$pass[1], 0, 0, 0))
    $g.FillPath($sb, $sp)
    $sb.Dispose(); $sp.Dispose()
}

# Metal: light top, a darker band across the middle, light again at the foot.
$lb = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.PointF 0, $ib.Y), (New-Object System.Drawing.PointF 0, ($ib.Y + $ib.Height)), `
    ([System.Drawing.Color]::White), ([System.Drawing.Color]::White)
$lb.InterpolationColors = New-Blend @(
    @(0,   255, 250, 231, 191),
    @(30,  255, 226, 186, 116),
    @(49,  255, 176, 130, 62),
    @(51,  255, 138,  98, 44),
    @(74,  255, 198, 154,  86),
    @(100, 255, 240, 214, 160))
$g.FillPath($lb, $ink)
$lb.Dispose()

# A dark edge stops the gold bleeding into the ground.
$ep = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 74, 52, 22)), ([float]($S * 0.004))
$g.DrawPath($ep, $ink)
$ep.Dispose()

# Top highlight, clipped inside the letters so it reads as a lit edge.
$g.SetClip($ink)
$hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.PointF 0, $ib.Y), (New-Object System.Drawing.PointF 0, ($ib.Y + $ib.Height * 0.34)), `
    ([System.Drawing.Color]::FromArgb(120, 255, 252, 240)), `
    ([System.Drawing.Color]::FromArgb(0, 255, 252, 240))
$g.FillRectangle($hl, 0, [int]$ib.Y, $S, [int]($ib.Height * 0.34))
$hl.Dispose()
$g.ResetClip()

$ink.Dispose()
$bmp.Save((Join-Path $dir $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $pfc.Dispose()

Write-Host ("  {0}  {1}x{1}{2}" -f $Out, $Size, $(if ($NoFrame) { '  (bez ramu)' } else { '' }))
