# Rebuild transparent EliteSIP logo assets from the approved crown geometry.
Add-Type -AssemblyName System.Drawing
$assets=Join-Path $PSScriptRoot '../src/EliteSIP.App/Assets/AppIcon'
$sizes=16,24,32,48,64,128,256,512
$frames=@()
foreach($size in $sizes) {
 $bmp=[Drawing.Bitmap]::new(($size*4),($size*4)); $g=[Drawing.Graphics]::FromImage($bmp)
 $g.SmoothingMode='AntiAlias'
 $scale=($size*4*.92)/312
 $g.TranslateTransform(($size*4-312*$scale)/2,($size*4-241*$scale)/2)
 $g.ScaleTransform($scale,$scale); $g.TranslateTransform(-100,-121)
 $brush=[Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,121),[Drawing.Point]::new(0,362),[Drawing.ColorTranslator]::FromHtml('#E5BE69'),[Drawing.ColorTranslator]::FromHtml('#B7872C'))
 $pen=[Drawing.Pen]::new($brush,24); $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
 $points=[Drawing.PointF[]]@([Drawing.PointF]::new(112,156),[Drawing.PointF]::new(182,298),[Drawing.PointF]::new(256,133),[Drawing.PointF]::new(330,298),[Drawing.PointF]::new(400,156),[Drawing.PointF]::new(256,263),[Drawing.PointF]::new(112,156))
 $g.DrawLines($pen,$points); $pen.Width=28
 $g.DrawLine($pen,184,348,232,348); $g.DrawLine($pen,280,348,328,348)
 $final=[Drawing.Bitmap]::new($size,$size); $fg=[Drawing.Graphics]::FromImage($final); $fg.InterpolationMode='HighQualityBicubic'; $fg.DrawImage($bmp,0,0,$size,$size)
 $stream=[IO.MemoryStream]::new(); $final.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $frames+=,@{Size=$size;Bytes=$stream.ToArray()}
 $final.Save((Join-Path $assets "icon_${size}x${size}.png"),[Drawing.Imaging.ImageFormat]::Png)
 $stream.Dispose(); $fg.Dispose(); $final.Dispose(); $pen.Dispose(); $brush.Dispose(); $g.Dispose(); $bmp.Dispose()
}
$out=[IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'EliteSIP.ico')))
try {
 $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$frames.Count)
 $offset=6+16*$frames.Count
 foreach($frame in $frames) { $dim=if($frame.Size -ge 256){0}else{$frame.Size}; $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0); $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$frame.Bytes.Length); $out.Write([uint32]$offset); $offset+=$frame.Bytes.Length }
 foreach($frame in $frames){$out.Write([byte[]]$frame.Bytes)}
} finally {$out.Dispose()}
