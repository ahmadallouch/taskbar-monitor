param([int]$X=0,[int]$Y=1140,[int]$W=700,[int]$H=60,[string]$Out="C:\Users\AhmadAllouch\TaskbarMonitor\shot.png",[int]$Zoom=2)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][D]::SetProcessDPIAware()
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($W,$H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X,$Y,0,0,(New-Object System.Drawing.Size($W,$H)))
$g.Dispose()
if($Zoom -gt 1){
  $big = New-Object System.Drawing.Bitmap(($W*$Zoom),($H*$Zoom))
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = 'NearestNeighbor'
  $g2.PixelOffsetMode = 'Half'
  $g2.DrawImage($bmp,0,0,($W*$Zoom),($H*$Zoom))
  $g2.Dispose(); $bmp.Dispose(); $bmp = $big
}
$bmp.Save($Out,[System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out"
