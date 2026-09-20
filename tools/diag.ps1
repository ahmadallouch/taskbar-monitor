Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class W {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h,int i);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string w);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
 public static string Txt(IntPtr h){ var sb=new StringBuilder(256); GetWindowText(h,sb,256); return sb.ToString(); }
 public static uint Pid(IntPtr h){ uint p; GetWindowThreadProcessId(h, out p); return p; }
 public static List<IntPtr> All(){ var l=new List<IntPtr>(); EnumWindows((h,p)=>{ l.Add(h); return true; }, IntPtr.Zero); return l; }
 public static List<IntPtr> Kids(IntPtr par){ var l=new List<IntPtr>(); EnumChildWindows(par,(h,p)=>{ l.Add(h); return true; }, IntPtr.Zero); return l; }
 public static string Info(IntPtr h){
   RECT r; GetWindowRect(h, out r);
   string pn = "?";
   try { pn = System.Diagnostics.Process.GetProcessById((int)Pid(h)).ProcessName; } catch {}
   return Cls(h).PadRight(44) + " \"" + Txt(h) + "\" rect=" + r.Left + "," + r.Top + " " + (r.Right-r.Left) + "x" + (r.Bottom-r.Top)
     + " vis=" + IsWindowVisible(h) + " style=0x" + GetWindowLong(h,-16).ToString("X8") + " parent=" + GetParent(h) + " proc=" + pn;
 }
}
'@
[void][W]::SetProcessDPIAware()
$me = (Get-Process TaskbarMonitor -ErrorAction SilentlyContinue).Id
"our pid = $me"
""
"=== windows owned by TaskbarMonitor (EnumWindows top-level) ==="
foreach($h in [W]::All()){ if([W]::Pid($h) -eq $me){ "  " + [W]::Info($h) } }
""
$tray = [W]::FindWindow('Shell_TrayWnd',$null)
"=== Shell_TrayWnd = $tray ==="
"  " + [W]::Info($tray)
""
"=== EnumChildWindows(Shell_TrayWnd) ==="
$kids = [W]::Kids($tray)
"  count = " + $kids.Count
foreach($h in $kids){ "  " + [W]::Info($h) }
