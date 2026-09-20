param([int]$Seconds=45)
$p = Get-Process TaskbarMonitor
$c0 = $p.TotalProcessorTime; $t0 = Get-Date
Start-Sleep -Seconds $Seconds
$p.Refresh()
$cpu = ($p.TotalProcessorTime-$c0).TotalSeconds; $wall = ((Get-Date)-$t0).TotalSeconds
[math]::Round($cpu/$wall*100,3)
