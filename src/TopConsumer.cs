using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarMonitor
{
    /// <summary>The heaviest process in one resource category.</summary>
    internal sealed class TopHit
    {
        public readonly string Name;
        public readonly string Caption;   // "CPU 34%", "RAM 7.8 GB", "DISK 12 MB/s", "GPU 61%"
        public readonly double Level;     // 0..1 within its own category, drives the colour ramp

        public TopHit(string name, string caption, double level)
        {
            Name = name; Caption = caption; Level = level;
        }
    }

    /// <summary>Categories currently worth showing, in fixed order.</summary>
    internal sealed class TopSnapshot
    {
        public readonly TopHit[] Items;
        public TopSnapshot(TopHit[] items) { Items = items; }
    }

    /// <summary>
    /// Ranks processes separately per resource, because "the biggest consumer overall"
    /// is not a real quantity - 30% CPU and 8 GB of RAM cannot be compared on one
    /// scale, and any composite score invents a comparison that does not exist. So each
    /// category keeps its own leader and the widget cycles through them.
    ///
    /// Everything comes from a single NtQuerySystemInformation call (CPU time, working
    /// set and disk transfer counts for every process at once) plus one PDH query for
    /// GPU. That is far cheaper than enumerating System.Diagnostics.Process objects,
    /// which opens a handle per process and allocates heavily.
    ///
    /// Sampling runs on a background thread: a child of Shell_TrayWnd shares an input
    /// queue with Explorer, so the UI thread must never do work like this.
    ///
    /// Per-process NETWORK is deliberately absent. Windows exposes it only through an
    /// ETW kernel session, which requires elevation - Task Manager can show it because
    /// it runs elevated, an unelevated widget cannot. CPU, RAM, disk and GPU are real;
    /// a fabricated network number would not be.
    /// </summary>
    internal sealed class TopConsumer
    {
        private const int SystemProcessInformation = 5;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        // x64 offsets into SYSTEM_PROCESS_INFORMATION. Read positionally rather than
        // through a struct so a field appended in a future Windows release cannot
        // silently shift the values we care about.
        private const int OffNextEntry = 0;
        private const int OffUserTime = 40;
        private const int OffKernelTime = 48;
        private const int OffImageNameLen = 56;
        private const int OffImageNameBuf = 64;
        private const int OffUniqueProcessId = 80;
        private const int OffWorkingSetSize = 144;
        private const int OffReadTransfer = 232;
        private const int OffWriteTransfer = 240;

        private const int Cpu = 0, Ram = 1, Disk = 2, Gpu = 3, CategoryCount = 4;

        // Below these a category has no story to tell and is dropped from the rotation,
        // so the widget never cycles to "DISK 0 B/s". The CPU floor is deliberately low:
        // percentages here are shares of the whole machine, so on a 16-core box even a
        // process pinning a full core only reads about 6%.
        private static readonly double[] Floor = { 0.3, 0.0, 256 * 1024.0, 2.0 };

        // Kernel pseudo-processes. They can legitimately top a category - Memory
        // Compression routinely holds gigabytes - but naming them tells you nothing you
        // can act on, so the rotation skips to the first real process instead.
        private static readonly HashSet<string> Pseudo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Memory Compression", "System", "Registry", "Secure System", "Idle", "vmmem", "vmmemWSL"
        };

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref Native.MEMORYSTATUSEX buf);

        private sealed class Track
        {
            public long CpuTicks;
            public long IoBytes;
            public int Seen;
            public bool Alive;
            public readonly double[] Ema = new double[CategoryCount];
            public readonly double[] Raw = new double[CategoryCount];
        }

        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly GpuCounters _gpu = new GpuCounters();
        private readonly int[] _leaderPid = { -1, -1, -1, -1 };
        private readonly int _cpuCount = Environment.ProcessorCount;
        private readonly Settings _cfg;
        private readonly double _totalPhys;

        private IntPtr _buffer;
        private int _bufferLen;
        private long _prevStamp;

        private volatile TopSnapshot _current;
        public TopSnapshot Current { get { return _current; } }

        public TopConsumer(Settings cfg)
        {
            _cfg = cfg;
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref m);
            _totalPhys = m.ullTotalPhys > 0 ? m.ullTotalPhys : 16.0 * 1024 * 1024 * 1024;
        }

        public void Sample()
        {
            try { SampleCore(); }
            catch { /* never let the sampler thread die */ }
        }

        private void SampleCore()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = _prevStamp == 0 ? 0 : (now - _prevStamp) / (double)Stopwatch.Frequency;
            _prevStamp = now;

            if (!Snapshot()) return;

            Dictionary<int, double> gpuByPid = _cfg.IncludeGpu ? _gpu.Sample() : null;

            foreach (KeyValuePair<int, Track> kv in _tracks) kv.Value.Alive = false;

            // Winner per category: pid, smoothed score, raw value, name.
            int[] bestPid = { -1, -1, -1, -1 };
            double[] bestEma = new double[CategoryCount];
            double[] bestRaw = new double[CategoryCount];
            string[] bestName = new string[CategoryCount];

            // The incumbent's numbers, so hysteresis can compare against them.
            double[] heldEma = new double[CategoryCount];
            double[] heldRaw = new double[CategoryCount];
            string[] heldName = new string[CategoryCount];

            int offset = 0;
            while (true)
            {
                IntPtr entry = IntPtr.Add(_buffer, offset);
                int next = Marshal.ReadInt32(entry, OffNextEntry);
                int pid = (int)Marshal.ReadInt64(entry, OffUniqueProcessId);

                if (pid > 0)
                {
                    long cpuTicks = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
                    long ws = Marshal.ReadInt64(entry, OffWorkingSetSize);
                    long io = Marshal.ReadInt64(entry, OffReadTransfer) + Marshal.ReadInt64(entry, OffWriteTransfer);

                    Track t;
                    if (!_tracks.TryGetValue(pid, out t)) { t = new Track(); _tracks[pid] = t; }
                    t.Alive = true;

                    double cpuPct = 0, ioRate = 0;
                    if (t.Seen > 0 && elapsed > 0)
                    {
                        // CPU times are 100ns units; divide by core count for a share of the whole machine.
                        cpuPct = (cpuTicks - t.CpuTicks) / 1e7 / elapsed / _cpuCount * 100.0;
                        ioRate = Math.Max(0, io - t.IoBytes) / elapsed;
                    }
                    t.CpuTicks = cpuTicks; t.IoBytes = io; t.Seen++;

                    double gpuPct = 0;
                    if (gpuByPid != null) gpuByPid.TryGetValue(pid, out gpuPct);

                    t.Raw[Cpu] = cpuPct;
                    t.Raw[Ram] = ws;
                    t.Raw[Disk] = ioRate;
                    t.Raw[Gpu] = gpuPct;

                    // Normalised only to smooth and colour - never to compare categories.
                    double[] norm =
                    {
                        Clamp01(cpuPct / 100.0),
                        Clamp01(ws / _totalPhys),
                        Clamp01(ioRate / (50.0 * 1024 * 1024)),
                        Clamp01(gpuPct / 100.0)
                    };

                    if (t.Seen > 1 && !IsPseudo(entry))
                    {
                        string name = null;
                        for (int k = 0; k < CategoryCount; k++)
                        {
                            // ~5s of smoothing, so one busy tick cannot swing the readout.
                            t.Ema[k] = t.Ema[k] <= 0 ? norm[k] : t.Ema[k] * 0.6 + norm[k] * 0.4;

                            if (t.Ema[k] > bestEma[k])
                            {
                                if (name == null) name = ReadName(entry);
                                bestEma[k] = t.Ema[k]; bestRaw[k] = t.Raw[k]; bestPid[k] = pid; bestName[k] = name;
                            }
                            if (pid == _leaderPid[k])
                            {
                                if (name == null) name = ReadName(entry);
                                heldEma[k] = t.Ema[k]; heldRaw[k] = t.Raw[k]; heldName[k] = name;
                            }
                        }
                    }
                }

                if (next == 0) break;
                offset += next;
            }

            Prune();
            Publish(bestPid, bestEma, bestRaw, bestName, heldEma, heldRaw, heldName);
        }

        private void Publish(int[] bestPid, double[] bestEma, double[] bestRaw, string[] bestName,
                             double[] heldEma, double[] heldRaw, string[] heldName)
        {
            List<TopHit> hits = new List<TopHit>(CategoryCount);

            for (int k = 0; k < CategoryCount; k++)
            {
                string name = bestName[k];
                double raw = bestRaw[k];
                double ema = bestEma[k];

                // Hysteresis: the incumbent keeps the slot unless clearly beaten, so the
                // widget does not flap between two processes sitting at similar load.
                if (heldName[k] != null && bestPid[k] != _leaderPid[k] && ema < heldEma[k] * 1.25)
                {
                    name = heldName[k]; raw = heldRaw[k]; ema = heldEma[k];
                }
                else
                {
                    _leaderPid[k] = bestPid[k];
                }

                if (name == null || raw < Floor[k]) continue;
                if (k == Ram && raw < 64 * 1024 * 1024) continue;

                hits.Add(new TopHit(name, Caption(k, raw), ema));
            }

            _current = new TopSnapshot(hits.ToArray());
        }

        private static string Caption(int category, double raw)
        {
            switch (category)
            {
                case Cpu: return "CPU " + Math.Round(raw).ToString("0") + "%";
                case Ram: return "RAM " + (raw / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
                case Disk: return "DISK " + Metrics.Rate(raw);
                default: return "GPU " + Math.Round(raw).ToString("0") + "%";
            }
        }

        private bool Snapshot()
        {
            if (_buffer == IntPtr.Zero)
            {
                _bufferLen = 512 * 1024;
                _buffer = Marshal.AllocHGlobal(_bufferLen);
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                int needed;
                int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferLen, out needed);
                if (status == 0) return true;
                if (status != STATUS_INFO_LENGTH_MISMATCH) return false;

                Marshal.FreeHGlobal(_buffer);
                _bufferLen = Math.Max(needed + 64 * 1024, _bufferLen * 2);
                _buffer = Marshal.AllocHGlobal(_bufferLen);
            }
            return false;
        }

        private static bool IsPseudo(IntPtr entry)
        {
            return Pseudo.Contains(ReadName(entry));
        }

        private static string ReadName(IntPtr entry)
        {
            ushort len = (ushort)Marshal.ReadInt16(entry, OffImageNameLen);
            IntPtr buf = Marshal.ReadIntPtr(entry, OffImageNameBuf);
            if (buf == IntPtr.Zero || len == 0) return "System";

            string s = Marshal.PtrToStringUni(buf, len / 2);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        /// <summary>Forget processes that have exited, so the map cannot grow forever.</summary>
        private void Prune()
        {
            List<int> dead = null;
            foreach (KeyValuePair<int, Track> kv in _tracks)
            {
                if (kv.Value.Alive) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _tracks.Remove(dead[i]);
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        /// <summary>
        /// Per-process GPU via PDH. PdhAddEnglishCounter means the counter path does not
        /// have to be translated on non-English Windows.
        /// </summary>
        private sealed class GpuCounters
        {
            private const uint PDH_FMT_DOUBLE = 0x00000200;
            private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

            [DllImport("pdh.dll")] private static extern int PdhOpenQueryW(string src, IntPtr userData, out IntPtr query);
            [DllImport("pdh.dll")] private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
            [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
            [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr items);

            private IntPtr _query, _counter, _items;
            private int _itemsLen;
            private bool _broken;

            public Dictionary<int, double> Sample()
            {
                Dictionary<int, double> result = new Dictionary<int, double>();
                if (_broken) return result;

                try
                {
                    if (_query == IntPtr.Zero)
                    {
                        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _broken = true; return result; }
                        if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                        {
                            _broken = true; return result;   // no GPU counters on this machine
                        }
                        PdhCollectQueryData(_query);
                        return result;   // first collection only primes the rate counter
                    }

                    if (PdhCollectQueryData(_query) != 0) return result;

                    int size = _itemsLen, count;
                    int rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, _items);
                    if (rc == PDH_MORE_DATA)
                    {
                        if (_items != IntPtr.Zero) Marshal.FreeHGlobal(_items);
                        _itemsLen = size + 8192;
                        _items = Marshal.AllocHGlobal(_itemsLen);
                        size = _itemsLen;
                        rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, _items);
                    }
                    if (rc != 0 || count <= 0) return result;

                    // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; <pad>; double } }
                    int stride = IntPtr.Size + 8 + 8;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = IntPtr.Add(_items, i * stride);
                        IntPtr namePtr = Marshal.ReadIntPtr(item);
                        if (namePtr == IntPtr.Zero) continue;

                        int pid = PidFromInstance(Marshal.PtrToStringUni(namePtr));
                        if (pid <= 0) continue;

                        double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                        if (val <= 0) continue;

                        double cur;
                        result[pid] = result.TryGetValue(pid, out cur) ? cur + val : val;
                    }
                }
                catch { _broken = true; }

                return result;
            }

            /// <summary>Instance names look like "pid_1234_luid_0x0_0xABCD_phys_0_eng_0_engtype_3D".</summary>
            private static int PidFromInstance(string name)
            {
                if (name == null || !name.StartsWith("pid_", StringComparison.Ordinal)) return -1;
                int end = name.IndexOf('_', 4);
                if (end < 0) return -1;
                int pid;
                return int.TryParse(name.Substring(4, end - 4), out pid) ? pid : -1;
            }
        }
    }
}
