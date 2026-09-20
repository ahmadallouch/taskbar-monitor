Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class H {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string w);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h,int i);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
 public static IntPtr FindOurs(IntPtr tray){
   IntPtr f=IntPtr.Zero;
   EnumChildWindows(tray,(h,p)=>{ if(Cls(h)=="TaskbarMonitorWindow"){ f=h; return false; } return true; }, IntPtr.Zero);
   return f;
 }
}
'@
[void][H]::SetProcessDPIAware()
$tray=[H]::FindWindow('Shell_TrayWnd',$null)
$ours=[H]::FindOurs($tray)
$r = New-Object H+RECT; [void][H]::GetWindowRect($ours,[ref]$r)
"widget rect     : $($r.Left),$($r.Top) to $($r.Right),$($r.Bottom)"
"exstyle         : 0x{0:X8}" -f [H]::GetWindowLong($ours,-20)
"topmost child   : " + ([H]::GetWindow($tray,5) -eq $ours)
""
"scanning widget area, counting points that hit our window:"
$hits=0; $total=0; $classes=@{}
for($y=$r.Top+4; $y -lt $r.Bottom-4; $y+=4){
  for($x=$r.Left+2; $x -lt $r.Right-2; $x+=4){
    $p = New-Object H+POINT; $p.X=$x; $p.Y=$y
    $w = [H]::WindowFromPoint($p)
    $c = [H]::Cls($w)
    $classes[$c] = 1 + $(if($classes.ContainsKey($c)){$classes[$c]}else{0})
    $total++
    if($w -eq $ours){ $hits++ }
  }
}
"  points tested : $total"
"  hits on us    : $hits"
"  classes seen  :"
$classes.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "    $($_.Value)`t$($_.Key)" }
