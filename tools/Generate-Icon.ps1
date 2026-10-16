Add-Type -AssemblyName System.Drawing
$dest = Join-Path $PSScriptRoot '..\windows-client\KeyboardBridge\obj\bridge.ico'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
$bmp = New-Object System.Drawing.Bitmap 256,256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(14,44,90))
$g.FillRectangle($background,0,0,256,256)
$cyan = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(32,224,244)), 13
$cyan.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$cyan.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawArc($cyan,65,35,126,104,220,100)
$g.DrawArc($cyan,88,65,80,60,220,100)
$dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32,224,244))
$g.FillEllipse($dot,122,119,12,12)
$screenBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(33,111,218))
$border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(191,239,255)), 5
$g.FillRectangle($screenBrush,19,144,90,59)
$g.DrawRectangle($border,19,144,90,59)
$g.FillRectangle($screenBrush,147,144,90,59)
$g.DrawRectangle($border,147,144,90,59)
$g.DrawArc($cyan,82,164,92,55,180,180)
$g.DrawLine($border,10,211,117,211)
$g.DrawLine($border,139,211,246,211)
$g.Dispose()
$stream = New-Object System.IO.MemoryStream
$bmp.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$data = $stream.ToArray()
$stream.Dispose()
$file = [System.IO.File]::Open($dest,[System.IO.FileMode]::Create)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]1)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$data.Length)
    $writer.Write([uint32]22)
    $writer.Write($data)
} finally { $writer.Dispose() }
