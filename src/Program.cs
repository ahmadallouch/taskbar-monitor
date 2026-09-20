using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;
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
            foreach (string a in args)
            {
                string arg = a.TrimStart('-', '/').ToLowerInvariant();
                if (arg == "install") { SetAutoStart(true); return 0; }
                if (arg == "uninstall") { SetAutoStart(false); return 0; }
            }

            bool isNew;
            using (Mutex mutex = new Mutex(true, @"Local\TaskbarMonitor.SingleInstance", out isNew))
            {
                if (!isNew) return 0;   // already running

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayAppContext());
            }
            return 0;
        }

        internal static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        internal static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Owns the message loop. The host window is a normal (never shown) top-level
    /// window rather than a message-only one, because message-only windows do not
    /// receive the "TaskbarCreated" broadcast that tells us Explorer restarted.
    /// </summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private readonly Host _host;
        private readonly Metrics _metrics = new Metrics();
        private readonly WinFormsTimer _tick = new WinFormsTimer();
        private readonly WinFormsTimer _guard = new WinFormsTimer();

        private Settings _cfg;
        private Theme _theme;
        private readonly Dictionary<IntPtr, Overlay> _overlays = new Dictionary<IntPtr, Overlay>();
        private ContextMenuStrip _menu;
        private TopConsumer _top;
        private System.Threading.Timer _topTimer;
        private System.Threading.Timer _trimTimer;
        private int _topBusy;

        public TrayAppContext()
        {
            _cfg = Settings.Load();
            _theme = Theme.Load();

            _host = new Host(this);
            IntPtr force = _host.Handle;   // realize the HWND so broadcasts arrive

            BuildMenu();
            StartTopConsumer();
            EnsureAttached();

            // The .NET runtime commits a lot of start-up scratch that is never touched
            // again. Handing it back keeps the resident set small for a process that
            // will sit on the taskbar for weeks.
            _trimTimer = new System.Threading.Timer(delegate { Trim(); }, null, 8000, 300000);

            _tick.Interval = _cfg.IntervalMs;
            _tick.Tick += delegate { Tick(); };
            _tick.Start();

            // Re-attach after Explorer restarts, DPI/resolution changes, or if the
            // taskbar's XAML island climbs back above us in the child z-order.
            _guard.Interval = 2000;
            _guard.Tick += delegate { EnsureAttached(); };
            _guard.Start();
        }

        private void Tick()
        {
            if (_overlays.Count == 0) return;

            Sample sample = _metrics.Read();
            TopHit hit = CurrentTopHit();

            foreach (KeyValuePair<IntPtr, Overlay> kv in _overlays)
            {
                if (!kv.Value.IsAttached) continue;
                try { kv.Value.Render(sample, hit); }
                catch { /* a transient GDI failure must not kill the loop */ }
            }
        }

        /// <summary>
        /// Cycles through whichever categories currently have something to report.
        /// Driven by the wall clock rather than a counter so the dwell time stays
        /// honest regardless of how often we happen to repaint.
        /// </summary>
        private TopHit CurrentTopHit()
        {
            if (_top == null) return null;

            TopSnapshot snap = _top.Current;
            if (snap == null || snap.Items.Length == 0) return null;

            long slot = Environment.TickCount64 / Math.Max(1000, _cfg.TopRotateMs);
            return snap.Items[(int)(slot % snap.Items.Length)];
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
            _topTimer = new System.Threading.Timer(delegate
            {
                // A slow sample (PDH can occasionally take a while) must not pile up.
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

        private static void Trim()
        {
            try { EmptyWorkingSet(GetCurrentProcess()); }
            catch { }
        }

        private void EnsureAttached()
        {
            List<IntPtr> bars = Taskbars();

            List<IntPtr> gone = null;
            foreach (KeyValuePair<IntPtr, Overlay> kv in _overlays)
            {
                if (bars.Contains(kv.Key)) continue;
                if (gone == null) gone = new List<IntPtr>();
                gone.Add(kv.Key);
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
                Overlay o;

                if (!_overlays.TryGetValue(bar, out o))
                {
                    o = NewOverlay();
                    o.Attach(bar);
                    _overlays[bar] = o;
                    added = true;
                }
                else if (!o.IsAttached)
                {
                    o.Attach(bar);
                    added = true;
                }

                o.BringToFront();
            }

            if (added) Tick();      // paint immediately so there is no blank frame
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
            Native.RECT r;
            if (bar == IntPtr.Zero || !Native.IsWindow(bar) || !Native.GetWindowRect(bar, out r)) return false;
            return r.Width > r.Height;
        }

        private Overlay NewOverlay()
        {
            Overlay o = new Overlay(_cfg, _theme);
            o.RightClicked += delegate { ShowMenu(); };
            o.DoubleClicked += delegate { Launch("taskmgr.exe"); };
            return o;
        }

        private void DisposeOverlays()
        {
            foreach (KeyValuePair<IntPtr, Overlay> kv in _overlays) kv.Value.Dispose();
            _overlays.Clear();
        }

        public void OnTaskbarRecreated()
        {
            DisposeOverlays();
            EnsureAttached();
        }

        public void OnThemeChanged()
        {
            _theme = Theme.Load();
            foreach (KeyValuePair<IntPtr, Overlay> kv in _overlays) kv.Value.ApplyTheme(_theme);
            Tick();
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add("Task Manager", null, delegate { Launch("taskmgr.exe"); });
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows");
            startup.Checked = Program.IsAutoStartEnabled();
            startup.Click += delegate
            {
                bool now = !Program.IsAutoStartEnabled();
                Program.SetAutoStart(now);
                startup.Checked = now;
            };
            _menu.Items.Add(startup);

            _menu.Items.Add("Edit settings…", null, delegate { Launch("notepad.exe", Settings.Path); });
            _menu.Items.Add("Reload settings", null, delegate { ReloadSettings(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, delegate { Shutdown(); });

            _menu.Opening += delegate { startup.Checked = Program.IsAutoStartEnabled(); };
        }

        private void ReloadSettings()
        {
            _cfg = Settings.Load();
            _theme = Theme.Load();
            _tick.Interval = _cfg.IntervalMs;

            DisposeOverlays();
            StartTopConsumer();
            EnsureAttached();
        }

        private void ShowMenu()
        {
            // Without this the menu will not dismiss when you click elsewhere,
            // because our process never becomes foreground on its own.
            SetForegroundWindow(_host.Handle);
            _menu.Show(Cursor.Position);
        }

        private static void Launch(string file, string arg = null)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file);
                if (arg != null) psi.Arguments = "\"" + arg + "\"";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        private void Shutdown()
        {
            _tick.Stop();
            _guard.Stop();
            StopTopConsumer();
            if (_trimTimer != null) { _trimTimer.Dispose(); _trimTimer = null; }
            DisposeOverlays();
            _host.Dispose();
            ExitThread();
        }

        /// <summary>Invisible top-level window: receives shell broadcasts.</summary>
        private sealed class Host : Form
        {
            private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            private readonly TrayAppContext _ctx;

            public Host(TrayAppContext ctx)
            {
                _ctx = ctx;
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Location = new System.Drawing.Point(-32000, -32000);
                Size = new System.Drawing.Size(1, 1);
                Opacity = 0;
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false);   // never actually show
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == TaskbarCreated)
                {
                    _ctx.OnTaskbarRecreated();
                }
                else if (m.Msg == Native.WM_SETTINGCHANGE)
                {
                    string p = m.LParam != IntPtr.Zero ? Marshal.PtrToStringAuto(m.LParam) : null;
                    if (p == "ImmersiveColorSet") _ctx.OnThemeChanged();
                }
                else if (m.Msg == Native.WM_DISPLAYCHANGE || m.Msg == Native.WM_DPICHANGED)
                {
                    _ctx.OnTaskbarRecreated();
                }
                base.WndProc(ref m);
            }
        }
    }
}
