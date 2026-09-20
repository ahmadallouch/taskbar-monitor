using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace TaskbarMonitor
{
    internal sealed class Sample
    {
        public double CpuPercent;
        public double RamPercent;
        public double RamUsedGb;
        public double RamTotalGb;
        public double DownBytesPerSec;
        public double UpBytesPerSec;
    }

    /// <summary>
    /// Cheap, locale-independent counters. Everything here is a raw syscall or a
    /// managed stat read, so a tick costs microseconds and never blocks the UI thread
    /// (important: our window shares an input queue with Explorer).
    /// </summary>
    internal sealed class Metrics
    {
        // Virtual/loopback adapters we never want in the throughput total. The WSL2
        // and Hyper-V switches carry the same traffic as the physical NIC, so counting
        // them would double every number.
        private static readonly Regex VirtualAdapter = new Regex(
            @"hyper-v|virtual|vmware|virtualbox|loopback|wsl|tap-|tunnel|pseudo|bluetooth",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Enumerating adapters allocates heavily (GetAdaptersAddresses under the hood),
        // so the list is cached and only re-scanned occasionally. The cached objects
        // still return live counters.
        private NetworkInterface[] _nics;
        private double _nicStamp = double.NegativeInfinity;
        private const double NicRescanSeconds = 30.0;

        private long _prevIdle, _prevKernel, _prevUser;
        private long _prevRx, _prevTx;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastNetTicks;
        private bool _primed;

        public Sample Read()
        {
            Sample s = new Sample();
            ReadCpu(s);
            ReadRam(s);
            ReadNet(s);
            _primed = true;
            return s;
        }

        private void ReadCpu(Sample s)
        {
            long idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return;

            // kernelTime already includes idleTime, so total = kernel + user.
            long dIdle = idle - _prevIdle;
            long dKernel = kernel - _prevKernel;
            long dUser = user - _prevUser;
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

            if (!_primed) return;

            long total = dKernel + dUser;
            if (total <= 0) return;

            double busy = (double)(total - dIdle) / total * 100.0;
            s.CpuPercent = Clamp(busy, 0, 100);
        }

        private void ReadRam(Sample s)
        {
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>();
            if (!Native.GlobalMemoryStatusEx(ref m)) return;

            const double Gb = 1024.0 * 1024.0 * 1024.0;
            s.RamTotalGb = m.ullTotalPhys / Gb;
            s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / Gb;
            s.RamPercent = Clamp(m.dwMemoryLoad, 0, 100);
        }

        private void ReadNet(Sample s)
        {
            double stamp = _clock.Elapsed.TotalSeconds;
            if (_nics == null || stamp - _nicStamp > NicRescanSeconds)
            {
                _nics = ScanAdapters();
                _nicStamp = stamp;
            }

            long rx = 0, tx = 0;
            try
            {
                for (int i = 0; i < _nics.Length; i++)
                {
                    IPInterfaceStatistics st = _nics[i].GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch
            {
                // An adapter vanished (docking, VPN up/down); force a rescan next tick.
                _nics = null;
                return;
            }

            double now = _clock.Elapsed.TotalSeconds;
            double elapsed = now - _lastNetTicks;
            long dRx = rx - _prevRx;
            long dTx = tx - _prevTx;
            _prevRx = rx; _prevTx = tx; _lastNetTicks = now;

            if (!_primed || elapsed <= 0) return;

            // Counters reset when an adapter disappears; negative deltas are noise.
            s.DownBytesPerSec = dRx > 0 ? dRx / elapsed : 0;
            s.UpBytesPerSec = dTx > 0 ? dTx / elapsed : 0;
        }

        private static NetworkInterface[] ScanAdapters()
        {
            List<NetworkInterface> keep = new List<NetworkInterface>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (VirtualAdapter.IsMatch(ni.Description) || VirtualAdapter.IsMatch(ni.Name)) continue;
                    keep.Add(ni);
                }
            }
            catch { }
            return keep.ToArray();
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Formats a rate the way Task Manager does: 3 significant-ish digits, fixed unit width.</summary>
        public static string Rate(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1) return "0 KB/s";
            if (kb < 1000) return Math.Round(kb).ToString("0") + " KB/s";
            double mb = kb / 1024.0;
            if (mb < 100) return mb.ToString("0.0") + " MB/s";
            return Math.Round(mb).ToString("0") + " MB/s";
        }
    }
}
