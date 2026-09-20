using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarMonitor
{
    /// <summary>User-tweakable knobs, read from settings.json next to the exe.</summary>
    internal sealed class Settings
    {
        /// <summary>Sampling/redraw period in milliseconds.</summary>
        public int IntervalMs { get; set; } = 1000;

        /// <summary>Gap between the widget and the taskbar edge it is anchored to, in logical px.</summary>
        public int Margin { get; set; } = 14;

        /// <summary>
        /// "Left" sits at the left end of the taskbar, which on Windows 11 is the empty
        /// space beside the centred Start button. "BeforeTray" sits just left of the
        /// notification area, which is where there is room on a Windows 10 taskbar with
        /// Start in the corner. "Auto" picks by Windows version.
        /// </summary>
        public string Anchor { get; set; } = "Auto";

        /// <summary>Also draw on the taskbars of secondary monitors.</summary>
        public bool ShowOnAllTaskbars { get; set; } = true;

        /// <summary>Horizontal space between metric groups, in logical px.</summary>
        public int GroupGap { get; set; } = 18;

        public bool ShowCpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowNetwork { get; set; } = true;

        /// <summary>Thin accent-coloured fill bars under the CPU/RAM readouts.</summary>
        public bool ShowBars { get; set; } = true;

        /// <summary>Show RAM as "9.8/31.7 GB" instead of a percentage.</summary>
        public bool RamAsGigabytes { get; set; } = false;

        /// <summary>Multiplier on the taskbar's native 12px text size.</summary>
        public double FontScale { get; set; } = 1.0;

        /// <summary>Bars/readouts turn orange at or above this percentage.</summary>
        public int WarnPercent { get; set; } = 60;

        /// <summary>Bars/readouts turn red at or above this percentage.</summary>
        public int CriticalPercent { get; set; } = 85;

        /// <summary>Tint the number itself, not just its bar, once past the warn level.</summary>
        public bool ColorizeValues { get; set; } = true;

        /// <summary>Show the heaviest process and what it is consuming.</summary>
        public bool ShowTopConsumer { get; set; } = true;

        /// <summary>Fixed width for the top-consumer group, logical px. Fixed so the
        /// widget does not change width every time the leading process changes.</summary>
        public int TopConsumerWidth { get; set; } = 108;

        /// <summary>How often to rank processes, in milliseconds.</summary>
        public int TopConsumerIntervalMs { get; set; } = 2000;

        /// <summary>How long each resource category stays on screen before the widget
        /// cycles to the next one, in milliseconds.</summary>
        public int TopRotateMs { get; set; } = 3000;

        /// <summary>Include per-process GPU in the ranking (one extra PDH query per sample).</summary>
        public bool IncludeGpu { get; set; } = true;

        public static string Path
        {
            get
            {
                string dir = System.AppContext.BaseDirectory;
                return System.IO.Path.Combine(dir, "settings.json");
            }
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    Settings s = JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsJson.Default.Settings);
                    if (s != null)
                    {
                        if (s.IntervalMs < 200) s.IntervalMs = 200;
                        if (s.FontScale < 0.6) s.FontScale = 0.6;
                        if (s.FontScale > 2.0) s.FontScale = 2.0;
                        if (s.TopConsumerIntervalMs < 500) s.TopConsumerIntervalMs = 500;
                        if (s.TopRotateMs < 1000) s.TopRotateMs = 1000;
                        if (s.CriticalPercent <= s.WarnPercent) s.CriticalPercent = s.WarnPercent + 10;
                        return s;
                    }
                }
            }
            catch { /* fall through to defaults rather than refusing to start */ }

            Settings def = new Settings();
            def.Save();
            return def;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
            }
            catch { }
        }
    }

    /// <summary>
    /// Source generated serialisation. Reflection based JSON does not survive ahead of
    /// time compilation, since the trimmer cannot see which members are used.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Settings))]
    internal partial class SettingsJson : JsonSerializerContext
    {
    }
}
