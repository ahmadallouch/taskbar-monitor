using System;
using System.Drawing;
using Microsoft.Win32;

namespace TaskbarMonitor
{
    /// <summary>
    /// Pulls the same colours Explorer uses so the widget reads as part of the shell
    /// rather than as something painted on top of it.
    /// </summary>
    internal sealed class Theme
    {
        public bool LightTaskbar;
        public Color Primary;     // metric values - matches taskbar label text
        public Color Secondary;   // metric captions - the dimmer tier Explorer uses
        public Color Accent;      // system accent, used for the CPU/RAM fill
        public Color TrackColor;  // unfilled part of a bar

        // Level ramp. Tuned per taskbar theme: the dark-taskbar set is brighter and
        // less saturated so it stays legible against acrylic, the light-taskbar set
        // is darkened so it does not glow.
        public Color Good;
        public Color Warn;
        public Color Crit;

        public static Theme Load()
        {
            Theme t = new Theme();
            t.LightTaskbar = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;

            if (t.LightTaskbar)
            {
                t.Primary = Color.FromArgb(232, 0, 0, 0);
                t.Secondary = Color.FromArgb(150, 0, 0, 0);
                t.TrackColor = Color.FromArgb(46, 0, 0, 0);
            }
            else
            {
                t.Primary = Color.FromArgb(242, 255, 255, 255);
                t.Secondary = Color.FromArgb(155, 255, 255, 255);
                t.TrackColor = Color.FromArgb(52, 255, 255, 255);
            }

            t.Accent = ReadAccent(t.LightTaskbar);

            if (t.LightTaskbar)
            {
                t.Good = Color.FromArgb(255, 26, 141, 74);
                t.Warn = Color.FromArgb(255, 186, 116, 0);
                t.Crit = Color.FromArgb(255, 196, 43, 36);
            }
            else
            {
                t.Good = Color.FromArgb(255, 78, 201, 126);
                t.Warn = Color.FromArgb(255, 245, 174, 60);
                t.Crit = Color.FromArgb(255, 240, 93, 76);
            }
            return t;
        }

        /// <summary>Green below warn, orange up to crit, red above.</summary>
        public Color ForLevel(double percent, double warnAt, double critAt)
        {
            if (percent >= critAt) return Crit;
            if (percent >= warnAt) return Warn;
            return Good;
        }

        /// <summary>
        /// Text tinted by level, but only once it matters: at normal load the readout
        /// keeps the shell's own text colour so the widget stays visually quiet.
        /// </summary>
        public Color TextForLevel(double percent, double warnAt, double critAt)
        {
            if (percent >= critAt) return Crit;
            if (percent >= warnAt) return Warn;
            return Primary;
        }

        /// <summary>DWM stores the accent as 0x00BBGGRR.</summary>
        private static Color ReadAccent(bool light)
        {
            int fallback = light ? unchecked((int)0x00D57A00) : unchecked((int)0x00FF9E60);
            int abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", fallback);

            int r = abgr & 0xFF;
            int g = (abgr >> 8) & 0xFF;
            int b = (abgr >> 16) & 0xFF;
            Color c = Color.FromArgb(255, r, g, b);

            // A very dark accent on a dark taskbar (or vice versa) disappears; nudge it
            // toward the readable end instead of letting the bars vanish.
            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            if (!light && lum < 0.35) c = Lighten(c, 0.45);
            if (light && lum > 0.75) c = Darken(c, 0.35);
            return c;
        }

        private static Color Lighten(Color c, double amount)
        {
            return Color.FromArgb(c.A,
                (int)(c.R + (255 - c.R) * amount),
                (int)(c.G + (255 - c.G) * amount),
                (int)(c.B + (255 - c.B) * amount));
        }

        private static Color Darken(Color c, double amount)
        {
            return Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));
        }

        private static int ReadDword(string subKey, string name, int fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (k == null) return fallback;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }
    }
}
