Add-Type -AssemblyName System.Drawing

$edge  = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$root  = Split-Path -Parent $PSScriptRoot   # repo root, wherever the repo is checked out
$tmp   = Join-Path $env:TEMP 'brand-shot'
$flasherOut = Join-Path $root 'brand\flasher\png'
$backupOut  = Join-Path $root 'brand\backup\png'
$iconOut    = Join-Path $root 'brand\icon'
New-Item -ItemType Directory -Force -Path $flasherOut, $backupOut, $iconOut, $tmp | Out-Null

function Shot([string]$svgPath, [int]$w, [int]$h, [string]$dest) {
  $html = Join-Path $tmp 'wrap.html'
  $url  = 'file:///' + ($svgPath -replace '\\','/')
  Set-Content -Path $html -Encoding ASCII -Value "<!doctype html><html><head><meta charset=`"utf-8`"><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}img{display:block;width:${w}px;height:${h}px}</style></head><body><img src=`"$url`"></body></html>"
  $page = 'file:///' + ($html -replace '\\','/')
  if (Test-Path $dest) { Remove-Item $dest -Force }
  for ($i = 0; $i -lt 3 -and -not (Test-Path $dest); $i++) {
    $prof = Join-Path $tmp ([guid]::NewGuid().ToString('N'))
    $args = @('--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check',
              "--user-data-dir=$prof",'--hide-scrollbars','--force-device-scale-factor=1',
              "--window-size=$w,$h",'--default-background-color=00000000',
              "--screenshot=$dest",$page)
    Start-Process -FilePath $edge -ArgumentList $args -Wait -WindowStyle Hidden
    Start-Sleep -Milliseconds 300
  }
  if (-not (Test-Path $dest)) { throw "screenshot failed: $dest" }
}

function Bounds([System.Drawing.Bitmap]$b) {
  $minx = $b.Width; $miny = $b.Height; $maxx = -1; $maxy = -1
  for ($y = 0; $y -lt $b.Height; $y += 2) {
    for ($x = 0; $x -lt $b.Width; $x += 2) {
      if ($b.GetPixel($x, $y).A -gt 8) {
        if ($x -lt $minx) { $minx = $x }
        if ($x -gt $maxx) { $maxx = $x }
        if ($y -lt $miny) { $miny = $y }
        if ($y -gt $maxy) { $maxy = $y }
      }
    }
  }
  return @{ x0 = $minx; y0 = $miny; x1 = $maxx; y1 = $maxy; w = ($maxx - $minx + 1); h = ($maxy - $miny + 1) }
}

# Loads an image from bytes so the file is not kept locked.
function LoadBitmap([string]$path) {
  $bytes = [System.IO.File]::ReadAllBytes($path)
  $ms = New-Object System.IO.MemoryStream(,$bytes)
  return New-Object System.Drawing.Bitmap($ms)
}

# Renders a master at the smallest window that yields >= $minArt px of art.
function Master([string]$svgPath, [int]$win, [int]$minArt) {
  $shot = Join-Path $tmp ((Split-Path $svgPath -Leaf) + ".$win.png")
  Shot $svgPath $win $win $shot
  $bmp = LoadBitmap $shot
  $bb = Bounds $bmp
  if ($bb.w -lt $minArt -and $win -lt 6000) {
    $bmp.Dispose(); Remove-Item $shot -Force
    return Master $svgPath ([int]($win * 1.9)) $minArt
  }
  Remove-Item $shot -Force
  return @{ bmp = $bmp; bb = $bb }
}

function Emit([System.Drawing.Bitmap]$src, $bb, [double]$sx0, [double]$sy0, [double]$sx1, [double]$sy1,
              [int]$outW, [int]$outH, [double]$dx0, [double]$dy0, [double]$dx1, [double]$dy1, [string]$dest) {
  $dst = New-Object System.Drawing.Bitmap($outW, $outH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($dst)
  $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  # source rect: the measured art, i.e. the design box sx0..sx1 / sy0..sy1 inside the 512 (or 512x640) grid
  $srcRect = New-Object System.Drawing.Rectangle($bb.x0, $bb.y0, $bb.w, $bb.h)
  $dstRect = New-Object System.Drawing.Rectangle(
      [int][math]::Round($dx0), [int][math]::Round($dy0),
      [int][math]::Round($dx1 - $dx0), [int][math]::Round($dy1 - $dy0))
  $g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()
  $dst.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
  $dst.Dispose()
}

# Packs the exported icon PNG frames into a multi-resolution .ico.
function WriteIco([string]$dest, [int[]]$sizes) {
  $pngs = @{}
  foreach ($s in $sizes) { $pngs[$s] = [System.IO.File]::ReadAllBytes((Join-Path $iconOut "flasher-icon-$s.png")) }

  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
  $off = 6 + 16 * $sizes.Count
  foreach ($s in $sizes) {
    $b = $pngs[$s]
    $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$d); $bw.Write([Byte]$d); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$b.Length); $bw.Write([UInt32]$off)
    $off += $b.Length
  }
  foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
  $bw.Flush()
  [System.IO.File]::WriteAllBytes($dest, $ms.ToArray())
  $bw.Dispose(); $ms.Dispose()
}

# 1200x630 link-preview card composed from a finished 1024 lockup.
function Card([string]$lockup1024, [string]$dest) {
  $card = New-Object System.Drawing.Bitmap(1200, 630, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $cg = [System.Drawing.Graphics]::FromImage($card)
  $cg.SmoothingMode = 'HighQuality'; $cg.InterpolationMode = 'HighQualityBicubic'; $cg.PixelOffsetMode = 'HighQuality'
  $cg.Clear([System.Drawing.Color]::FromArgb(255, 18, 21, 28))
  # soft crimson glow behind the mark
  $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
  $glowPath.AddEllipse(330, -190, 540, 540)
  $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
  $pgb.CenterColor = [System.Drawing.Color]::FromArgb(52, 220, 20, 60)
  $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 220, 20, 60))
  $cg.FillPath($pgb, $glowPath)
  $pgb.Dispose(); $glowPath.Dispose()

  $logo = LoadBitmap $lockup1024
  $lw = 330; $lh = [int]($lw * 640 / 512)
  $cg.DrawImage($logo, [int]((1200 - $lw) / 2), [int]((630 - $lh) / 2), $lw, $lh)
  $logo.Dispose()
  $cg.Dispose()
  $card.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
  $card.Dispose()
}

# ---------- lockups: design 512x640, art = badge(16..496) x wordmark bottom(604) ----------
$lockups = @(
  @{ svg = "$root\brand\flasher\flasher-logo-on-dark.svg";  name = 'flasher-logo-on-dark';  dir = $flasherOut },
  @{ svg = "$root\brand\flasher\flasher-logo-on-light.svg"; name = 'flasher-logo-on-light'; dir = $flasherOut },
  @{ svg = "$root\brand\backup\flasher-logo-backup-on-dark.svg";  name = 'flasher-logo-backup-on-dark';  dir = $backupOut },
  @{ svg = "$root\brand\backup\flasher-logo-backup-on-light.svg"; name = 'flasher-logo-backup-on-light'; dir = $backupOut }
)
foreach ($lk in $lockups) {
  New-Item -ItemType Directory -Force -Path $lk.dir | Out-Null
  $m = Master $lk.svg 2048 1100
  Write-Output ("{0}: master art {1}x{2} at {3},{4}" -f $lk.name, $m.bb.w, $m.bb.h, $m.bb.x0, $m.bb.y0)
  foreach ($w in 1024, 512, 256, 128) {
    $h = [int]($w * 640 / 512)
    $s = $w / 512.0
    Emit $m.bmp $m.bb 16 16 496 604 $w $h (16 * $s) (16 * $s) (496 * $s) (604 * $s) (Join-Path $lk.dir "$($lk.name)-$w.png")
  }
  $m.bmp.Dispose()
}

# ---------- icon only (wordmark-free): design 512x512, art = badge(16..496) ----------
$icon = Master "$root\SRFlasher\Assets\logo-sr.svg" 2048 1100
Write-Output ("icon: master art {0}x{1} at {2},{3}" -f $icon.bb.w, $icon.bb.h, $icon.bb.x0, $icon.bb.y0)
foreach ($s in 512, 256, 192, 180, 128, 64, 48, 32, 24, 16) {
  $k = $s / 512.0
  Emit $icon.bmp $icon.bb 16 16 496 496 $s $s (16 * $k) (16 * $k) (496 * $k) (496 * $k) (Join-Path $iconOut "flasher-icon-$s.png")
}
$icon.bmp.Dispose()

# ---------- .ico files: app icon, plus one in each wordmark set ----------
$icoSizes = 16, 24, 32, 48, 64, 128, 256
WriteIco (Join-Path $iconOut 'flasher-icon.ico') $icoSizes
WriteIco (Join-Path $backupOut 'flasher-backup-icon.ico') $icoSizes
Write-Output 'ico: flasher-icon.ico, flasher-backup-icon.ico'

# ---------- 1200x630 link-preview cards, one per wordmark set ----------
Card (Join-Path $flasherOut 'flasher-logo-on-dark-1024.png') (Join-Path $flasherOut 'flasher-social-1200x630.png')
Card (Join-Path $backupOut  'flasher-logo-backup-on-dark-1024.png') (Join-Path $backupOut 'flasher-backup-social-1200x630.png')
Write-Output 'cards: flasher-social-1200x630.png, flasher-backup-social-1200x630.png'

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
foreach ($d in @(@{p=$flasherOut; n='flasher'}, @{p=$backupOut; n='backup'}, @{p=$iconOut; n='icon'})) {
  Write-Output ("--- {0} ---" -f $d.n)
  Get-ChildItem $d.p | ForEach-Object { Write-Output ("{0}  {1} bytes" -f $_.Name, $_.Length) }
}
