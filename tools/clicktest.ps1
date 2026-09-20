param([int]$X=40,[int]$Y=1175,[string]$Label="glyph")
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class C {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string w);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
 [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
 public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
 public static int CountMenus(){ int n=0; EnumWindows((h,p)=>{ if(Cls(h)=="#32768" && IsWindowVisible(h)) n++; return true; }, IntPtr.Zero); return n; }
}
'@
[void][C]::SetProcessDPIAware()
$orig = New-Object C+POINT; [void][C]::GetCursorPos([ref]$orig)

$pt = New-Object C+POINT; $pt.X=$X; $pt.Y=$Y
$under = [C]::WindowFromPoint($pt)
"[$Label] point ($X,$Y) -> window class: " + [C]::Cls($under)

[void][C]::SetCursorPos($X,$Y)
Start-Sleep -Milliseconds 250
[C]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero)   # RIGHTDOWN
Start-Sleep -Milliseconds 60
[C]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero)   # RIGHTUP

$seen = 0
for($i=0;$i -lt 8;$i++){ Start-Sleep -Milliseconds 250; if([C]::CountMenus() -gt 0){ $seen=1; break } }
"[$Label] menu appeared: " + [bool]$seen

[C]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); [C]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 400
[void][C]::SetCursorPos($orig.X,$orig.Y)
