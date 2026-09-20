using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace TaskbarMonitor
{
    internal static class Program
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "TaskbarMonitor";

        [STAThread]
        private static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].TrimStart('-', '/').ToLowerInvariant();
                if (arg == "install") { SetAutoStart(true); return 0; }
                if (arg == "uninstall") { SetAutoStart(false); return 0; }
            }

            bool isNew;
            using (Mutex mutex = new Mutex(true, @"Local\TaskbarMonitor.SingleInstance", out isNew))
            {
                if (!isNew) return 0;

                using (App app = new App()) app.Run();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        internal static string ExecutablePath
        {
            get { return Environment.ProcessPath ?? string.Empty; }
        }

        internal static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        internal static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enable) key.SetValue(RunValue, "\"" + ExecutablePath + "\"");
                    else key.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Owns the message loop and the widget's lifetime.
    ///
    /// The host is a hidden top level window rather than a message only one, because
    /// message only windows do not receive the TaskbarCreated broadcast that tells us
    /// Explorer has restarted.
    /// </summary>
    internal sealed class App : Win32Window
    {
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        private const int StateBusy = 2;
        private const int StatePresentationMode = 4;

        private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        private static readonly IntPtr TickTimer = new IntPtr(1);
        private static readonly IntPtr GuardTimer = new IntPtr(2);
        private static readonly IntPtr TrimTimer = new IntPtr(3);

        private const int CommandTaskManager = 1;
        private const int CommandStartup = 2;
        private const int CommandEditSettings = 3;
        private const int CommandReload = 4;
        private const int CommandExit = 5;

        private readonly Dictionary<IntPtr, Overlay> _overlays = new Dictionary<IntPtr, Overlay>();
        private readonly Metrics _metrics = new Metrics();

        private Settings _cfg;
        private Theme _theme;
        private TopConsumer _top;
        private Timer _topTimer;
        private int _topBusy;
        private volatile bool _suspended;

        public void Run()
        {
            _cfg = Settings.Load();
            _theme = Theme.Load();

            CreateWindow(Native.WS_EX_TOOLWINDOW, 0, IntPtr.Zero, 0, 0, 0, 0, "TaskbarMonitor");

            StartTopConsumer();
            EnsureAttached();

            Native.SetTimer(Handle, TickTimer, (uint)_cfg.IntervalMs, IntPtr.Zero);
            Native.SetTimer(Handle, GuardTimer, 2000, IntPtr.Zero);

            // The runtime commits start up scratch it never touches again. Handing it
            // back keeps the resident set small for a process that runs for weeks.
            Native.SetTimer(Handle, TrimTimer, 60000, IntPtr.Zero);
            Trim();

            Native.MSG msg;
            while (Native.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }

        protected override bool OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref IntPtr result)
        {
            if (message == Native.WM_TIMER)
            {
                long id = wParam.ToInt64();
                if (id == 1) Tick();
                else if (id == 2) EnsureAttached();
                else if (id == 3) Trim();
                return true;
            }

            if (message == TaskbarCreated)
            {
                OnTaskbarRecreated();
                return true;
            }

            if (message == Native.WM_SETTINGCHANGE)
            {
                string changed = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                if (changed == "ImmersiveColorSet") OnThemeChanged();
                return true;
            }

            if (message == Native.WM_DISPLAYCHANGE || message == Native.WM_DPICHANGED)
            {
                OnTaskbarRecreated();
                return true;
            }

            return false;
        }

        private void Tick()
        {
            _suspended = FullScreenAppInFront();
            if (_suspended || _overlays.Count == 0) return;

            Sample sample = _metrics.Read();
            TopHit hit = CurrentTopHit();

            foreach (KeyValuePair<IntPtr, Overlay> pair in _overlays)
            {
                if (!pair.Value.IsAttached) continue;
                try { pair.Value.Render(sample, hit); }
                catch { /* a transient GDI failure must not kill the loop */ }
            }
        }

        /// <summary>
        /// While a game or a fullscreen video is in front, the taskbar is covered and
        /// nothing we draw can be seen, so both the drawing and the process ranking stop
        /// until it goes away.
        /// </summary>
        private static bool FullScreenAppInFront()
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return false;
            return state >= StateBusy && state <= StatePresentationMode;
        }

        /// <summary>
        /// Cycles through whichever resources currently have something to report.
        /// Driven by the wall clock rather than a counter, so the dwell time holds
        /// regardless of how often we happen to repaint.
        /// </summary>
        private TopHit CurrentTopHit()
        {
            if (_top == null) return null;

            TopSnapshot snapshot = _top.Current;
            if (snapshot == null || snapshot.Items.Length == 0) return null;

            long slot = Environment.TickCount64 / Math.Max(1000, _cfg.TopRotateMs);
            return snapshot.Items[(int)(slot % snapshot.Items.Length)];
        }

        /// <summary>
        /// Process ranking runs off the UI thread on purpose: our window is a child of
        /// Shell_TrayWnd and therefore shares an input queue with Explorer, so blocking
        /// here would stall the taskbar itself.
        /// </summary>
        private void StartTopConsumer()
        {
            StopTopConsumer();
            if (!_cfg.ShowTopConsumer) return;

            _top = new TopConsumer(_cfg);
            _topTimer = new Timer(delegate
            {
                if (_suspended) return;
                if (Interlocked.Exchange(ref _topBusy, 1) == 1) return;
                try { _top.Sample(); }
                finally { Interlocked.Exchange(ref _topBusy, 0); }
            }, null, 300, _cfg.TopConsumerIntervalMs);
        }

        private void StopTopConsumer()
        {
            if (_topTimer != null) { _topTimer.Dispose(); _topTimer = null; }
            _top = null;
        }

        private void EnsureAttached()
        {
            List<IntPtr> bars = Taskbars();

            List<IntPtr> gone = null;
            foreach (KeyValuePair<IntPtr, Overlay> pair in _overlays)
            {
                if (bars.Contains(pair.Key)) continue;
                if (gone == null) gone = new List<IntPtr>();
                gone.Add(pair.Key);
            }
            if (gone != null)
            {
                for (int i = 0; i < gone.Count; i++)
                {
                    _overlays[gone[i]].Dispose();
                    _overlays.Remove(gone[i]);
                }
            }

            bool added = false;
            for (int i = 0; i < bars.Count; i++)
            {
                IntPtr bar = bars[i];
                Overlay overlay;

                if (!_overlays.TryGetValue(bar, out overlay))
                {
                    overlay = NewOverlay();
                    overlay.Attach(bar);
                    _overlays[bar] = overlay;
                    added = true;
                }
                else if (!overlay.IsAttached)
                {
                    overlay.Attach(bar);
                    added = true;
                }

                overlay.BringToFront();
            }

            if (added) Tick();
        }

        /// <summary>
        /// The primary taskbar plus one Shell_SecondaryTrayWnd per additional monitor.
        /// Vertical taskbars are skipped: the layout is a row of readings and there is
        /// nowhere sensible to put it on a taskbar docked left or right.
        /// </summary>
        private List<IntPtr> Taskbars()
        {
            List<IntPtr> bars = new List<IntPtr>(2);

            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (IsUsable(primary)) bars.Add(primary);

            if (_cfg.ShowOnAllTaskbars)
            {
                IntPtr secondary = IntPtr.Zero;
                while (true)
                {
                    secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
                    if (secondary == IntPtr.Zero) break;
                    if (IsUsable(secondary)) bars.Add(secondary);
                }
            }

            return bars;
        }

        private static bool IsUsable(IntPtr bar)
        {
            Native.RECT rect;
            if (bar == IntPtr.Zero || !Native.IsWindow(bar) || !Native.GetWindowRect(bar, out rect)) return false;
            return rect.Width > rect.Height;
        }

        private Overlay NewOverlay()
        {
            Overlay overlay = new Overlay(_cfg, _theme);
            overlay.RightClicked += delegate { ShowMenu(); };
            overlay.DoubleClicked += delegate { Launch("taskmgr.exe", null); };
            return overlay;
        }

        private void DisposeOverlays()
        {
            foreach (KeyValuePair<IntPtr, Overlay> pair in _overlays) pair.Value.Dispose();
            _overlays.Clear();
        }

        private void OnTaskbarRecreated()
        {
            DisposeOverlays();
            EnsureAttached();
        }

        private void OnThemeChanged()
        {
            _theme = Theme.Load();
            foreach (KeyValuePair<IntPtr, Overlay> pair in _overlays) pair.Value.ApplyTheme(_theme);
            Tick();
        }

        private void ReloadSettings()
        {
            _cfg = Settings.Load();
            _theme = Theme.Load();

            Native.KillTimer(Handle, TickTimer);
            Native.SetTimer(Handle, TickTimer, (uint)_cfg.IntervalMs, IntPtr.Zero);

            DisposeOverlays();
            StartTopConsumer();
            EnsureAttached();
        }

        private void ShowMenu()
        {
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;

            IntPtr menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            try
            {
                Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(CommandTaskManager), "Task Manager");
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);

                uint startup = Native.MF_STRING | (Program.IsAutoStartEnabled() ? Native.MF_CHECKED : 0);
                Native.AppendMenuW(menu, startup, new IntPtr(CommandStartup), "Start with Windows");
                Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(CommandEditSettings), "Edit settings");
                Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(CommandReload), "Reload settings");
                Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                Native.AppendMenuW(menu, Native.MF_STRING, new IntPtr(CommandExit), "Exit");

                // A popup will not dismiss on an outside click unless its owner is
                // foreground first, and the null post afterwards is the documented
                // companion to that.
                Native.SetForegroundWindow(Handle);
                int command = Native.TrackPopupMenuEx(menu,
                    Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD, cursor.X, cursor.Y, Handle, IntPtr.Zero);
                Native.PostMessageW(Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                RunCommand(command);
            }
            finally
            {
                Native.DestroyMenu(menu);
            }
        }

        private void RunCommand(int command)
        {
            switch (command)
            {
                case CommandTaskManager:
                    Launch("taskmgr.exe", null);
                    break;
                case CommandStartup:
                    Program.SetAutoStart(!Program.IsAutoStartEnabled());
                    break;
                case CommandEditSettings:
                    Launch("notepad.exe", Settings.Path);
                    break;
                case CommandReload:
                    ReloadSettings();
                    break;
                case CommandExit:
                    Shutdown();
                    break;
            }
        }

        private static void Launch(string file, string argument)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(file);
                if (argument != null) info.Arguments = "\"" + argument + "\"";
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch { }
        }

        private static void Trim()
        {
            try { EmptyWorkingSet(GetCurrentProcess()); }
            catch { }
        }

        private void Shutdown()
        {
            Dispose();
            Native.PostQuitMessage(0);
        }

        public override void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                Native.KillTimer(Handle, TickTimer);
                Native.KillTimer(Handle, GuardTimer);
                Native.KillTimer(Handle, TrimTimer);
            }

            StopTopConsumer();
            DisposeOverlays();
            base.Dispose();
        }
    }
}
