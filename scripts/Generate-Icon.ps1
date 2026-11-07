$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$out = Join-Path $assets 'DjShrek-Draiver.ico'
function New-IconPng([int]$Size) {
  $bmp = [System.Drawing.Bitmap]::new($Size,$Size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s=$Size/256.0; $g.ScaleTransform([single]$s,[single]$s)
    $bg=[System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255,9,13,37))
    $cyan=[System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255,218,248,255))
    $dark=[System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255,17,20,47))
    $pink=[System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255,255,53,211),10)
    $blue=[System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255,0,217,255),9)
    try {
      $g.FillRoundedRectangle # intentionally unavailable on older .NET; draw rounded background with path below
      $path=[System.Drawing.Drawing2D.GraphicsPath]::new()
      $path.AddArc(8,8,48,48,180,90);$path.AddArc(200,8,48,48,270,90);$path.AddArc(200,200,48,48,0,90);$path.AddArc(8,200,48,48,90,90);$path.CloseFigure()
      $g.FillPath($bg,$path);$path.Dispose()
      $g.DrawArc($blue,48,25,160,150,180,180)
      $g.DrawRectangle($pink,34,111,30,66);$g.DrawRectangle($blue,192,111,30,66)
      $g.FillEllipse($cyan,72,62,112,132)
      $g.FillRectangle($cyan,91,157,74,37)
      $g.FillEllipse($dark,83,112,38,28);$g.FillEllipse($dark,137,112,38,28)
      $g.FillPolygon($dark,[System.Drawing.Point[]]@([System.Drawing.Point]::new(128,139),[System.Drawing.Point]::new(117,158),[System.Drawing.Point]::new(139,158)))
      $g.DrawLine($blue,103,174,153,174)
      foreach($x in @(110,122,134,146)){ $g.DrawLine($pink,$x,174,$x,188) }
      $g.DrawLine($blue,78,91,62,77);$g.DrawLine($blue,178,91,194,77)
    } finally { $bg.Dispose();$cyan.Dispose();$dark.Dispose();$pink.Dispose();$blue.Dispose() }
    $ms=[System.IO.MemoryStream]::new()
    try{$bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png);return ,$ms.ToArray()}finally{$ms.Dispose()}
  } finally{$g.Dispose();$bmp.Dispose()}
}
$imgs=@(16,32,48,256 | ForEach-Object { [pscustomobject]@{Size=$_;Bytes=(New-IconPng $_)} })
$fs=[System.IO.File]::Open($out,[System.IO.FileMode]::Create)
$w=[System.IO.BinaryWriter]::new($fs)
try {
 $w.Write([UInt16]0);$w.Write([UInt16]1);$w.Write([UInt16]$imgs.Count);$offset=6+16*$imgs.Count
 foreach($i in $imgs){$dim=if($i.Size -eq 256){[byte]0}else{[byte]$i.Size};$w.Write($dim);$w.Write($dim);$w.Write([byte]0);$w.Write([byte]0);$w.Write([UInt16]1);$w.Write([UInt16]32);$w.Write([UInt32]$i.Bytes.Length);$w.Write([UInt32]$offset);$offset+=$i.Bytes.Length}
 foreach($i in $imgs){$w.Write($i.Bytes)}
} finally{$w.Dispose();$fs.Dispose()}
$check=[System.IO.File]::ReadAllBytes($out)
if($check.Length -lt 22 -or $check[2] -ne 1){throw 'Invalid ICO generated'}
Write-Host "Created skull-with-headphones icon: $out ($($check.Length) bytes)"
