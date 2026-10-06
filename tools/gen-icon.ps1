$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# La salida se calcula desde la carpeta de este guion, nunca desde una ruta absoluta: así el
# repositorio se puede compilar en cualquier equipo sin depender de dónde lo tenga quien lo usa.
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $raiz '..\src\MonitorRedPCJ\assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Make-PngBytes([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $pad = [int]($size * 0.06)
  $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2*$pad), ($size - 2*$pad))
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
      [System.Drawing.Color]::FromArgb(255,15,163,163),
      [System.Drawing.Color]::FromArgb(255,67,97,238), 45)
  $g.FillEllipse($br, $rect)
  $penW = [Math]::Max(2, $size / 12)
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $penW)
  $cx = $size / 2; $cy = $size * 0.62
  foreach ($r in @(0.16, 0.30)) {
    $d = $size * $r * 2
    $g.DrawArc($pen, [single]($cx - $d/2), [single]($cy - $d/2), [single]$d, [single]$d, 200, 140)
  }
  $dotD = $size * 0.10
  $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx - $dotD/2), [single]($cy - $dotD/2), [single]$dotD, [single]$dotD)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return ,$ms.ToArray()
}

# Construir .ico (formato: header + entries + datos PNG)
$sizes = @(16, 32, 48, 256)
$pngs = $sizes | ForEach-Object { ,(Make-PngBytes $_) }
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $data = $pngs[$i]
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes("$out\app.ico", $ms.ToArray())
$bw.Close()
Write-Output "ICO_OK $((Get-Item "$out\app.ico").Length) bytes"
