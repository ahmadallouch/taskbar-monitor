using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TaskbarMonitor
{
    /// <summary>
    /// A WS_CHILD, WS_EX_LAYERED window parented to Shell_TrayWnd.
    ///
    /// Being a genuine child of the taskbar (rather than a topmost window floating
    /// above it) is what makes this feel native: it is shown, hidden, moved and
    /// clipped by Explorer along with the rest of the taskbar, so auto-hide,
    /// fullscreen apps, resolution changes and "hide taskbar" all just work without
    /// us watching for any of them.
    ///
    /// Per-pixel alpha via UpdateLayeredWindow means we composite onto the real
    /// acrylic - no opaque background patch that would give the game away.
    /// </summary>
    internal sealed class Overlay : NativeWindow, IDisposable
    {
        private readonly Settings _cfg;
        private Theme _theme;
        private IntPtr _parent;
        private int _width, _height;
        private int _x = int.MinValue, _y = int.MinValue;
        private double _scale = 1.0;

        private Surface _surface;
        private Bitmap _probe;
        private Graphics _probeG;
        private readonly Dictionary<string, float> _labelWidths = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _valueWidths = new Dictionary<string, float>();
        private string _lastFrame;
        private Font _labelFont, _valueFont;
        private double _fontScaleUsed = -1;

        public event EventHandler RightClicked;
        public event EventHandler DoubleClicked;

        public Overlay(Settings cfg, Theme theme)
        {
            _cfg = cfg;
            _theme = theme;
        }

        public bool IsAttached
        {
            get { return Handle != IntPtr.Zero && Native.IsWindow(Handle) && Native.IsWindow(_parent); }
        }

        public void ApplyTheme(Theme theme) { _theme = theme; }

        public void Attach(IntPtr taskbar)
        {
            Detach();
            _parent = taskbar;

            uint dpi = Native.GetDpiForWindow(taskbar);
            _scale = dpi > 0 ? dpi / 96.0 : 1.0;

            CreateParams cp = new CreateParams();
            cp.ClassName = null;                 // let NativeWindow register a default class
            cp.Caption = "TaskbarMonitorHost";
            cp.Parent = taskbar;
            cp.Style = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            cp.X = 0;
            cp.Y = 0;
            cp.Width = 10;
            cp.Height = 10;

            _lastFrame = null;
            _x = _y = int.MinValue;
            _width = _height = 0;
            CreateHandle(cp);
        }

        public void Detach()
        {
            if (Handle != IntPtr.Zero)
            {
                try { DestroyHandle(); } catch { }
            }
            _parent = IntPtr.Zero;
        }

        /// <summary>
        /// Lift above the taskbar's XAML island, which otherwise covers us.
        ///
        /// Checked before it is changed: an unconditional SetWindowPos every couple of
        /// seconds makes Explorer revalidate and repaint that strip of the taskbar, which
        /// costs both processes real CPU for no reason. GetWindow is a cheap read.
        /// </summary>
        public void BringToFront()
        {
            if (!IsAttached) return;
            if (Native.GetWindow(_parent, Native.GW_CHILD) == Handle) return;

            Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_RBUTTONUP)
            {
                EventHandler h = RightClicked;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
            if (m.Msg == Native.WM_LBUTTONDBLCLK)
            {
                EventHandler h = DoubleClicked;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        // ----------------------------------------------------------------- render

        private sealed class Column
        {
            public string Top;
            public string Bottom;
            public Color TopColor;
            public Color BottomColor;
            public double Fill = -1;      // 0..1 for a bar, <0 for none
            public Color FillColor;
            public float Width;
            public float LockedWidth;     // >0 pins the width and truncates instead
        }

        public void Render(Sample s, TopHit top)
        {
            if (!IsAttached) return;

            EnsureFonts();

            List<Column> cols = BuildColumns(s, top);
            if (cols.Count == 0) return;

            Graphics pg = MeasureContext();
            float minW = (float)(46 * _scale);
            foreach (Column c in cols)
            {
                if (c.LockedWidth > 0)
                {
                    c.Width = c.LockedWidth;
                    c.Top = Truncate(pg, c.Top, _labelFont, c.Width);
                    c.Bottom = Truncate(pg, c.Bottom, _valueFont, c.Width);
                    continue;
                }
                float cw = Math.Max(MeasureCached(pg, c.Top, _labelFont, _labelWidths),
                                    MeasureCached(pg, c.Bottom, _valueFont, _valueWidths));
                c.Width = Math.Max(cw, minW);
            }

            float gap = (float)(_cfg.GroupGap * _scale);
            float padX = (float)(2 * _scale);
            float total = padX * 2;
            for (int i = 0; i < cols.Count; i++)
            {
                total += cols[i].Width;
                if (i < cols.Count - 1) total += gap;
            }

            Native.RECT client;
            if (!Native.GetClientRect(_parent, out client)) return;

            int w = (int)Math.Ceiling(total);
            int h = client.Height;
            if (w <= 0 || h <= 0) return;

            string signature = FrameSignature(cols, w, h);
            if (signature == _lastFrame) return;
            _lastFrame = signature;

            Surface surf = AcquireSurface(w, h);
            {
                using (Graphics g = Graphics.FromImage(surf.Canvas))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    float labelH = _labelFont.GetHeight(g);
                    float valueH = _valueFont.GetHeight(g);
                    float barH = _cfg.ShowBars ? (float)(2 * _scale) : 0f;
                    float barGap = _cfg.ShowBars ? (float)(3 * _scale) : 0f;
                    float blockH = labelH + valueH + barGap + barH;
                    float originY = (h - blockH) / 2f;

                    float x = padX;
                    foreach (Column c in cols)
                    {
                        DrawText(g, c.Top, _labelFont, c.TopColor, x, originY);
                        DrawText(g, c.Bottom, _valueFont, c.BottomColor, x, originY + labelH);

                        if (_cfg.ShowBars && c.Fill >= 0)
                        {
                            float by = originY + labelH + valueH + barGap;
                            RectangleF track = new RectangleF(x, by, c.Width, barH);
                            using (SolidBrush tb = new SolidBrush(_theme.TrackColor))
                                FillPill(g, tb, track, barH);

                            float fw = (float)(c.Width * Math.Max(0.0, Math.Min(1.0, c.Fill)));
                            if (fw > 0.5f)
                            {
                                using (SolidBrush fb = new SolidBrush(c.FillColor))
                                    FillPill(g, fb, new RectangleF(x, by, fw, barH), barH);
                            }
                        }

                        x += c.Width + gap;
                    }
                }

                Present(surf, w, h, client);
            }
        }

        private List<Column> BuildColumns(Sample s, TopHit top)
        {
            List<Column> cols = new List<Column>();
            double warn = _cfg.WarnPercent;
            double crit = _cfg.CriticalPercent;

            if (_cfg.ShowCpu)
            {
                Column c = new Column();
                c.Top = "CPU";
                c.Bottom = Math.Round(s.CpuPercent).ToString("0") + "%";
                c.Fill = s.CpuPercent / 100.0;
                c.FillColor = _theme.ForLevel(s.CpuPercent, warn, crit);
                c.TopColor = _theme.Secondary;
                c.BottomColor = _cfg.ColorizeValues ? _theme.TextForLevel(s.CpuPercent, warn, crit) : _theme.Primary;
                cols.Add(c);
            }

            if (_cfg.ShowRam)
            {
                Column c = new Column();
                c.Top = "RAM";
                c.Bottom = _cfg.RamAsGigabytes
                    ? s.RamUsedGb.ToString("0.0") + "/" + s.RamTotalGb.ToString("0.0") + " GB"
                    : Math.Round(s.RamPercent).ToString("0") + "%";
                c.Fill = s.RamPercent / 100.0;
                c.FillColor = _theme.ForLevel(s.RamPercent, warn, crit);
                c.TopColor = _theme.Secondary;
                c.BottomColor = _cfg.ColorizeValues ? _theme.TextForLevel(s.RamPercent, warn, crit) : _theme.Primary;
                cols.Add(c);
            }

            if (_cfg.ShowNetwork)
            {
                // Throughput has no "bad" level to colour against - a busy link is not
                // a problem - so this group keeps the shell's own text colour.
                Column c = new Column();
                c.Top = "↓ " + Metrics.Rate(s.DownBytesPerSec);
                c.Bottom = "↑ " + Metrics.Rate(s.UpBytesPerSec);
                c.TopColor = _theme.Secondary;
                c.BottomColor = _theme.Primary;
                cols.Add(c);
            }

            if (_cfg.ShowTopConsumer && top != null)
            {
                double pct = top.Level * 100.0;
                Column c = new Column();
                c.Top = top.Caption;
                c.Bottom = top.Name;
                c.TopColor = _theme.ForLevel(pct, warn, crit);
                c.BottomColor = _theme.Primary;
                c.Fill = top.Level;
                c.FillColor = _theme.ForLevel(pct, warn, crit);
                c.LockedWidth = (float)(_cfg.TopConsumerWidth * _scale);
                cols.Add(c);
            }

            return cols;
        }

        /// <summary>Blit the DIB straight into the layered window - no copy, no conversion.</summary>
        private void Present(Surface surf, int w, int h, Native.RECT parentClient)
        {
            int x = AnchorX(w, parentClient);
            int y = (parentClient.Height - h) / 2;

            // Only move the window when the geometry actually changed - see BringToFront.
            if (x != _x || y != _y || w != _width || h != _height)
            {
                _x = x; _y = y; _width = w; _height = h;
                Native.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }

            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT src = new Native.POINT(0, 0);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                // pptDst = Zero: position stays where SetWindowPos put it.
                Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size,
                    surf.Dc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// Where along the taskbar we sit. Anchoring to the notification area rather
        /// than to the taskbar's right edge keeps us clear of it as it grows and shrinks
        /// with the clock and the icons that come and go.
        /// </summary>
        private int AnchorX(int w, Native.RECT parentClient)
        {
            int margin = (int)Math.Round(_cfg.Margin * _scale);
            if (!UseTrayAnchor()) return margin;

            IntPtr notify = Native.FindWindowEx(_parent, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                Native.RECT nr;
                Native.POINT p = new Native.POINT();
                if (Native.GetWindowRect(notify, out nr))
                {
                    p.X = nr.Left; p.Y = nr.Top;
                    if (Native.ScreenToClient(_parent, ref p)) return Math.Max(0, p.X - w - margin);
                }
            }
            return Math.Max(0, parentClient.Width - w - margin);
        }

        private bool UseTrayAnchor()
        {
            if (string.Equals(_cfg.Anchor, "Left", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(_cfg.Anchor, "BeforeTray", StringComparison.OrdinalIgnoreCase)) return true;
            return !IsWindows11;
        }

        /// <summary>Build 22000 is the first Windows 11 release, and the first with a
        /// centred Start button that leaves the left end of the taskbar empty.</summary>
        internal static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;

        /// <summary>A throwaway 1x1 context kept alive purely for text measurement.</summary>
        private Graphics MeasureContext()
        {
            if (_probeG == null)
            {
                _probe = new Bitmap(1, 1);
                _probeG = Graphics.FromImage(_probe);
                _probeG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
            return _probeG;
        }

        private static void FillPill(Graphics g, Brush b, RectangleF r, float h)
        {
            if (r.Width <= 0) return;
            float radius = h / 2f;
            if (r.Width <= h) { g.FillEllipse(b, r.X, r.Y, r.Width, r.Height); return; }
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, h, h, 90, 180);
                p.AddArc(r.Right - h, r.Y, h, h, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        /// <summary>
        /// A GDI DIB section that GDI+ draws into directly.
        ///
        /// The obvious implementation - draw to a Bitmap, call GetHbitmap, blit, delete -
        /// copies and reallocates the whole surface on every single frame. Here the DIB
        /// is created once and its pixels are shared with a GDI+ Bitmap wrapping the same
        /// memory, so a repaint is just drawing; UpdateLayeredWindow reads the very bytes
        /// GDI+ wrote.
        ///
        /// The wrapper is Format32bppPArgb, not Format32bppArgb: UpdateLayeredWindow
        /// expects premultiplied alpha, which is exactly what GetHbitmap(Color(0)) used
        /// to be doing for us on each frame.
        /// </summary>
        private sealed class Surface : IDisposable
        {
            public readonly int Width, Height;
            public readonly IntPtr Dc;
            public readonly Bitmap Canvas;

            private readonly IntPtr _dib, _oldBitmap;

            public Surface(int w, int h)
            {
                Width = w; Height = h;

                Native.BITMAPINFOHEADER bmi = new Native.BITMAPINFOHEADER();
                bmi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                bmi.biWidth = w;
                bmi.biHeight = -h;          // negative: top-down, matching GDI+ row order
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                bmi.biCompression = Native.BI_RGB;

                IntPtr screen = Native.GetDC(IntPtr.Zero);
                try
                {
                    IntPtr bits;
                    _dib = Native.CreateDIBSection(screen, ref bmi, Native.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                    Dc = Native.CreateCompatibleDC(screen);
                    _oldBitmap = Native.SelectObject(Dc, _dib);
                    Canvas = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bits);
                }
                finally { Native.ReleaseDC(IntPtr.Zero, screen); }
            }

            public void Dispose()
            {
                if (Canvas != null) Canvas.Dispose();
                if (Dc != IntPtr.Zero)
                {
                    Native.SelectObject(Dc, _oldBitmap);
                    Native.DeleteDC(Dc);
                }
                if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            }
        }

        private Surface AcquireSurface(int w, int h)
        {
            if (_surface != null && _surface.Width == w && _surface.Height == h) return _surface;
            if (_surface != null) _surface.Dispose();
            _surface = new Surface(w, h);
            return _surface;
        }

        /// <summary>
        /// Everything that affects the rendered pixels. If it has not changed there is
        /// no point redrawing - which happens more often than you would think, since
        /// CPU and network readouts frequently round to the same text twice running.
        /// </summary>
        private static string FrameSignature(List<Column> cols, int w, int h)
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append(w).Append('x').Append(h);
            foreach (Column c in cols)
            {
                sb.Append('|').Append(c.Top).Append('\u0001').Append(c.Bottom)
                  .Append('\u0001').Append(c.TopColor.ToArgb())
                  .Append('\u0001').Append(c.BottomColor.ToArgb())
                  .Append('\u0001').Append(c.FillColor.ToArgb())
                  .Append('\u0001').Append((int)Math.Round(c.Fill * c.Width));
            }
            return sb.ToString();
        }

        private static string Truncate(Graphics g, string text, Font f, float maxW)
        {
            if (string.IsNullOrEmpty(text) || Measure(g, text, f) <= maxW) return text;

            const string Ellipsis = "…";
            for (int len = text.Length - 1; len > 0; len--)
            {
                string candidate = text.Substring(0, len) + Ellipsis;
                if (Measure(g, candidate, f) <= maxW) return candidate;
            }
            return Ellipsis;
        }

        private static float Measure(Graphics g, string text, Font f)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        /// <summary>MeasureString is the single most expensive call in a repaint, and most
        /// strings we draw repeat from frame to frame ("CPU", "RAM", the same rounded
        /// percentage twice in a row), so results are memoised per font.</summary>
        private float MeasureCached(Graphics g, string text, Font f, Dictionary<string, float> cache)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            float cached;
            if (cache.TryGetValue(text, out cached)) return cached;

            float w = Measure(g, text, f);
            if (cache.Count > 256) cache.Clear();   // bounded: rates produce unbounded strings
            cache[text] = w;
            return w;
        }

        private static void DrawText(Graphics g, string text, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(text, f, b, new PointF(x, y), StringFormat.GenericTypographic);
        }

        private void EnsureFonts()
        {
            if (_labelFont != null && Math.Abs(_fontScaleUsed - _cfg.FontScale * _scale) < 0.001) return;

            DisposeFonts();
            _labelWidths.Clear();
            _valueWidths.Clear();
            _lastFrame = null;
            _fontScaleUsed = _cfg.FontScale * _scale;

            // Windows 11 shell text. Fall back for Windows 10 / stripped font sets.
            float labelPx = (float)(10.5 * _fontScaleUsed);
            float valuePx = (float)(13.0 * _fontScaleUsed);

            _labelFont = MakeFont(labelPx, FontStyle.Regular);
            _valueFont = MakeFont(valuePx, FontStyle.Regular);
        }

        private static Font MakeFont(float px, FontStyle style)
        {
            string[] families = { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" };
            foreach (string fam in families)
            {
                try
                {
                    Font f = new Font(fam, px, style, GraphicsUnit.Pixel);
                    if (string.Equals(f.Name, fam, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (_labelFont != null) { _labelFont.Dispose(); _labelFont = null; }
            if (_valueFont != null) { _valueFont.Dispose(); _valueFont = null; }
        }

        public void Dispose()
        {
            DisposeFonts();
            if (_surface != null) { _surface.Dispose(); _surface = null; }
            if (_probeG != null) { _probeG.Dispose(); _probeG = null; }
            if (_probe != null) { _probe.Dispose(); _probe = null; }
            Detach();
        }
    }
}
