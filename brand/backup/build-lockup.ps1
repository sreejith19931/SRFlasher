Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root, wherever the repo is checked out
$dir  = Join-Path $root 'brand\backup'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$ff  = New-Object System.Windows.Media.FontFamily('Segoe UI')
$tf  = New-Object System.Windows.Media.Typeface($ff,
        [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Bold, [System.Windows.FontStretches]::Normal)
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# Outlines a string: Segoe UI Bold at $size, $track extra units between letters.
function Outline([string]$text, [double]$size, [double]$track) {
  $group = New-Object System.Windows.Media.GeometryGroup
  $group.FillRule = [System.Windows.Media.FillRule]::Nonzero
  $pen = 0.0
  $chars = $text.ToCharArray()
  for ($i = 0; $i -lt $chars.Length; $i++) {
    $ft = New-Object System.Windows.Media.FormattedText([string]$chars[$i], $inv,
            [System.Windows.FlowDirection]::LeftToRight, $tf, $size, [System.Windows.Media.Brushes]::Black, 1.0)
    [void]$group.Children.Add($ft.BuildGeometry((New-Object System.Windows.Point($pen, 0))))
    $pen += $ft.Width + $track
  }
  $outlined = $group.GetOutlinedPathGeometry()
  # WPF emits an XAML "F1" even-odd prefix that SVG does not parse: drop it and set fill-rule instead
  $d = [regex]::Replace($outlined.ToString(), '^F[01]', '')
  return @{ d = $d; b = $outlined.Bounds; advance = ($pen - $track) }
}

# --- sanity check against the shipped FLASHER wordmark (l 8, t 36.7, r 462.1, b 109.1) ---
$chk = Outline 'FLASHER' 100 6
Write-Output ("FLASHER check: {0:N2},{1:N2} -> {2:N2},{3:N2}  advance {4:N2}" -f `
  $chk.b.X, $chk.b.Y, ($chk.b.X + $chk.b.Width), ($chk.b.Y + $chk.b.Height), $chk.advance)

$wm = Outline 'BACKUP' 100 6
$scale   = 0.88093
$capTop  = 540.0
$tx = 256.0 - ($wm.b.X + $wm.b.Width / 2) * $scale
$ty = $capTop - $wm.b.Y * $scale
Write-Output ("BACKUP: ink {0:N2} wide, cap {1:N2}, placed {2:N2},{3:N2}, wordmark {4:N2} wide" -f `
  ($wm.b.Width * $scale), ($wm.b.Height * $scale), $tx, $ty, ($wm.b.Width * $scale))

$markSrc = [System.IO.File]::ReadAllText((Join-Path $root 'SRFlasher\Assets\logo-sr.svg'))
$inner = $markSrc.Substring($markSrc.IndexOf('>', $markSrc.IndexOf('<svg')) + 1)
$inner = $inner.Substring(0, $inner.LastIndexOf('</svg>'))
$inner = [regex]::Replace($inner, '<title>[\s\S]*?</title>', '').Trim()

$variants = @(
  @{ file = 'flasher-logo-backup-on-dark.svg';  label = 'Flasher Backup — for dark backgrounds';  ink = '#f2f3f6' },
  @{ file = 'flasher-logo-backup-on-light.svg'; label = 'Flasher Backup — for light backgrounds'; ink = '#16181b' }
)

$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($v in $variants) {
  $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 640" width="512" height="640" role="img" aria-label="Flasher Backup">
  <title>$($v.label)</title>
$inner
  <g transform="translate($([math]::Round($tx,3)) $([math]::Round($ty,3))) scale($scale)">
    <path fill="$($v.ink)" d="$($wm.d)"/>
  </g>
</svg>
"@
  $dest = Join-Path $dir $v.file
  [System.IO.File]::WriteAllText($dest, $svg, $utf8)
  Write-Output ("{0}  {1} bytes" -f $v.file, (Get-Item $dest).Length)
}
