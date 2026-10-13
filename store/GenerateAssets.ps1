$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path $PSScriptRoot 'Assets'
[void][IO.Directory]::CreateDirectory($assetRoot)

function New-LayoutBitmap([int]$Size) {
    $bitmap=[Drawing.Bitmap]::new($Size,$Size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint=[Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($Size/100.0),($Size/100.0))
    $blue=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#0067C0'))
    $white=[Drawing.SolidBrush]::new([Drawing.Color]::White)
    $line=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#0067C0'),4)
    $key=[Drawing.Drawing2D.GraphicsPath]::new()
    $key.AddArc(6,6,22,22,180,90); $key.AddArc(72,6,22,22,270,90)
    $key.AddArc(72,72,22,22,0,90); $key.AddArc(6,72,22,22,90,90); $key.CloseFigure()
    $graphics.FillPath($blue,$key)
    if ($Size -le 24) {
        $graphics.FillRectangle($white,22,28,22,24)
        $graphics.FillRectangle($white,56,48,22,24)
        $graphics.FillRectangle($white,45,36,25,5)
        $graphics.FillRectangle($white,30,60,25,5)
    } else {
        $graphics.FillRectangle($white,19,21,33,35)
        $graphics.FillRectangle($white,48,47,33,35)
        $font=[Drawing.Font]::new('Segoe UI',26,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
        $format=[Drawing.StringFormat]::new()
        $format.Alignment=[Drawing.StringAlignment]::Center
        $format.LineAlignment=[Drawing.StringAlignment]::Center
        $graphics.DrawString('A',$font,$blue,[Drawing.RectangleF]::new(18,20,35,36),$format)
        $graphics.DrawString('Ф',$font,$blue,[Drawing.RectangleF]::new(47,46,35,36),$format)
        $font.Dispose(); $format.Dispose()
    }
    $key.Dispose(); $blue.Dispose(); $white.Dispose(); $line.Dispose(); $graphics.Dispose()
    return $bitmap
}

foreach ($spec in @(@('StoreLogo',50),@('Square44x44Logo',44),@('Square150x150Logo',150))) {
    foreach ($scale in @(100,125,150,200,400)) {
        $size=[int]($spec[1]*$scale/100)
        $bitmap=New-LayoutBitmap $size
        try { $bitmap.Save((Join-Path $assetRoot ($spec[0]+'.scale-'+$scale+'.png')),[Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
    }
    Copy-Item -LiteralPath (Join-Path $assetRoot ($spec[0]+'.scale-100.png')) -Destination (Join-Path $assetRoot ($spec[0]+'.png'))
}
$images=@()
foreach ($size in @(16,24,32,48,64,256)) {
    $bitmap=New-LayoutBitmap $size
    $stream=[IO.MemoryStream]::new()
    try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $images+=,@{Size=$size;Bytes=$stream.ToArray()} }
    finally { $bitmap.Dispose(); $stream.Dispose() }
}
$icon=[IO.File]::Create((Join-Path $assetRoot 'SearchLayout.ico'))
$writer=[IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset=6+16*$images.Count
    foreach ($item in $images) {
        $edge=if($item.Size -eq 256){0}else{$item.Size}
        $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$item.Bytes.Length); $writer.Write([uint32]$offset)
        $offset+=$item.Bytes.Length
    }
    foreach ($item in $images) { $writer.Write([byte[]]$item.Bytes) }
} finally { $writer.Dispose() }
Get-ChildItem -LiteralPath $assetRoot | Select-Object Name,Length
