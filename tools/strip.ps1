param([int]$X=150,[int]$W=250,[int]$N=10,[int]$IntervalMs=1700)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class D { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][D]::SetProcessDPIAware()
Add-Type -AssemblyName System.Drawing
$H=60; $Z=2
$strip = New-Object System.Drawing.Bitmap(($W*$Z),($H*$Z*$N))
$gs = [System.Drawing.Graphics]::FromImage($strip)
$gs.InterpolationMode='NearestNeighbor'; $gs.PixelOffsetMode='Half'
for($i=0;$i -lt $N;$i++){
  $b = New-Object System.Drawing.Bitmap($W,$H)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen($X,1140,0,0,(New-Object System.Drawing.Size($W,$H)))
  $g.Dispose()
  $gs.DrawImage($b,0,($i*$H*$Z),($W*$Z),($H*$Z))
  $b.Dispose()
  if($i -lt ($N-1)){ Start-Sleep -Milliseconds $IntervalMs }
}
$gs.Dispose()
$strip.Save("C:\Users\AhmadAllouch\TaskbarMonitor\strip.png",[System.Drawing.Imaging.ImageFormat]::Png)
$strip.Dispose(); "saved"
