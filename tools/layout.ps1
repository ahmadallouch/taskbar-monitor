Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class L {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string w);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
 public static List<string> Report(){
   var want = new HashSet<string>(new[]{"Start","ReBarWindow32","MSTaskSwWClass","MSTaskListWClass","TrayNotifyWnd","TaskbarMonitorWindow"});
   var outp = new List<string>();
   IntPtr tray = FindWindow("Shell_TrayWnd", null);
   EnumChildWindows(tray,(h,p)=>{
     string c = Cls(h);
     if(want.Contains(c)){
       RECT r; GetWindowRect(h, out r);
       outp.Add(string.Format("  {0,-24} left={1,5}  width={2,5}", c, r.Left, r.Right-r.Left));
     }
     return true;
   }, IntPtr.Zero);
   return outp;
 }
}
'@
[void][L]::SetProcessDPIAware()
[L]::Report() | ForEach-Object { $_ }
