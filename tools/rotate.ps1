Add-Type @'
using System; using System.Runtime.InteropServices;
public static class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][D]::SetProcessDPIAware()
Add-Type -AssemblyName System.Drawing
$W=460; $H=60; $Y=1140; $N=5; $Zoom=2
$strip = New-Object System.Drawing.Bitmap(($W*$Zoom),($H*$Zoom*$N))
$gs = [System.Drawing.Graphics]::FromImage($strip)
$gs.InterpolationMode='NearestNeighbor'; $gs.PixelOffsetMode='Half'
for($i=0; $i -lt $N; $i++){
  $bmp = New-Object System.Drawing.Bitmap($W,$H)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0,$Y,0,0,(New-Object System.Drawing.Size($W,$H)))
  $g.Dispose()
  $gs.DrawImage($bmp,0,($i*$H*$Zoom),($W*$Zoom),($H*$Zoom))
  $bmp.Dispose()
  if($i -lt ($N-1)){ Start-Sleep -Milliseconds 3100 }
}
$gs.Dispose()
$strip.Save("C:\Users\AhmadAllouch\TaskbarMonitor\rotate.png",[System.Drawing.Imaging.ImageFormat]::Png)
$strip.Dispose()
"saved"
