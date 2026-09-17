using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MachineGauges
{
    internal sealed class Snapshot
    {
        public double CpuPercent;        // Task Manager "CPU"
        public double CpuGhz;            // Task Manager "Speed"
        public double RamPercent;
        public double RamUsedGB;
        public double RamTotalGB;
        public double GpuPercent;        // busiest engine on the selected adapter
        public double VramUsedGB;
        public double VramTotalGB;
        public int GpuTempC = -1;
        public double DiskPercent;       // Task Manager "Active time" of the busiest disk
        public double DiskBytesPerSec;
        public double NetBitsPerSec;
        public double NetRecvBitsPerSec;
        public double NetSentBitsPerSec;
    }

    /// <summary>
    /// Reads the same performance counters Task Manager uses, in a single PDH query
    /// so every metric comes from one coherent sample.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        private IntPtr _query;
        private IntPtr _cCpu, _cCpuPerf, _cDiskIdle, _cDiskBytes, _cNet, _cNetRecv, _cNetSent, _cGpuEngine, _cGpuMem;
        private readonly double _baseGhz;
        private IntPtr _nvmlDevice = IntPtr.Zero;
        private bool _nvmlReady;
        private byte[] _buffer = new byte[64 * 1024];
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private double _lastRetry;

        public Sampler(double baseMhz)
        {
            _baseGhz = baseMhz / 1000.0;

            AddMissingCounters();
            InitNvml();

            // Rate counters need a baseline sample before they report anything.
            if (_query != IntPtr.Zero) Pdh.PdhCollectQueryData(_query);
        }

        /// <summary>Adds every counter that isn't registered yet; safe to call repeatedly.</summary>
        private void AddMissingCounters()
        {
            if (_query == IntPtr.Zero && Pdh.PdhOpenQueryW(null, IntPtr.Zero, out _query) != Pdh.ERROR_SUCCESS)
                _query = IntPtr.Zero;

            if (_cCpu == IntPtr.Zero) _cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
            if (_cCpu == IntPtr.Zero) _cCpu = Add(@"\Processor Information(_Total)\% Processor Time");
            if (_cCpuPerf == IntPtr.Zero) _cCpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
            if (_cDiskIdle == IntPtr.Zero) _cDiskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
            if (_cDiskBytes == IntPtr.Zero) _cDiskBytes = Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
            if (_cNet == IntPtr.Zero) _cNet = Add(@"\Network Interface(*)\Bytes Total/sec");
            if (_cNetRecv == IntPtr.Zero) _cNetRecv = Add(@"\Network Interface(*)\Bytes Received/sec");
            if (_cNetSent == IntPtr.Zero) _cNetSent = Add(@"\Network Interface(*)\Bytes Sent/sec");
            if (_cGpuEngine == IntPtr.Zero) _cGpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");
            if (_cGpuMem == IntPtr.Zero) _cGpuMem = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        }

        // Started at sign-in, the app can come up before the GPU driver or some counter
        // sets are ready. Retry anything missing every 15 s for the first 10 minutes.
        private void RetryMissing()
        {
            bool missing = _query == IntPtr.Zero || _cCpu == IntPtr.Zero || _cCpuPerf == IntPtr.Zero
                        || _cDiskIdle == IntPtr.Zero || _cDiskBytes == IntPtr.Zero || _cNet == IntPtr.Zero
                        || _cNetRecv == IntPtr.Zero || _cNetSent == IntPtr.Zero
                        || _cGpuEngine == IntPtr.Zero || _cGpuMem == IntPtr.Zero || !_nvmlReady;
            double now = _uptime.Elapsed.TotalSeconds;
            if (!missing || now > 600 || now - _lastRetry < 15) return;
            _lastRetry = now;

            AddMissingCounters();
            if (!_nvmlReady) InitNvml();
        }

        private IntPtr Add(string path)
        {
            if (_query == IntPtr.Zero) return IntPtr.Zero;
            IntPtr counter;
            return Pdh.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out counter) == Pdh.ERROR_SUCCESS
                ? counter : IntPtr.Zero;
        }

        private void InitNvml()
        {
            try
            {
                if (Nvml.Init() != 0) return;
                IntPtr dev;
                if (Nvml.GetHandleByIndex(0, out dev) != 0) return;
                _nvmlDevice = dev;
                _nvmlReady = true;
            }
            catch { _nvmlReady = false; }
        }

        public Snapshot Sample()
        {
            RetryMissing();
            var s = new Snapshot();
            ReadMemory(s);

            if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == Pdh.ERROR_SUCCESS)
            {
                s.CpuPercent = Clamp(Single(_cCpu), 0, 100);
                double perf = Single(_cCpuPerf);
                s.CpuGhz = perf > 0 ? _baseGhz * perf / 100.0 : _baseGhz;

                ReadDisk(s);
                ReadNetwork(s);
                ReadGpu(s);
            }

            ReadNvml(s);
            return s;
        }

        private static void ReadMemory(Snapshot s)
        {
            var m = new MEMORYSTATUSEX();
            if (!Win32.GlobalMemoryStatusEx(m) || m.ullTotalPhys == 0) return;
            ulong used = m.ullTotalPhys - m.ullAvailPhys;
            s.RamTotalGB = m.ullTotalPhys / 1073741824.0;
            s.RamUsedGB = used / 1073741824.0;
            s.RamPercent = used * 100.0 / m.ullTotalPhys;
        }

        private void ReadDisk(Snapshot s)
        {
            // Task Manager's "Active time" is 100 - % Idle Time. Report the busiest
            // physical disk, which is what Task Manager's graph peaks at.
            double busiest = 0;
            foreach (var kv in Array(_cDiskIdle))
            {
                if (kv.Key == "_Total") continue;
                double active = 100.0 - kv.Value;
                if (active > busiest) busiest = active;
            }
            s.DiskPercent = Clamp(busiest, 0, 100);
            s.DiskBytesPerSec = Math.Max(0, Single(_cDiskBytes));
        }

        private void ReadNetwork(Snapshot s)
        {
            // Like Task Manager, report one adapter (the busiest), not a sum of all of them.
            double busiest = 0;
            string busiestName = null;
            foreach (var kv in Array(_cNet))
            {
                string n = kv.Key.ToLowerInvariant();
                if (n.Contains("loopback") || n.Contains("isatap") || n.Contains("teredo")) continue;
                if (busiestName == null || kv.Value > busiest) { busiest = kv.Value; busiestName = kv.Key; }
            }
            s.NetBitsPerSec = busiest * 8.0;
            if (busiestName == null) return;

            foreach (var kv in Array(_cNetRecv))
                if (kv.Key == busiestName) { s.NetRecvBitsPerSec = kv.Value * 8.0; break; }
            foreach (var kv in Array(_cNetSent))
                if (kv.Key == busiestName) { s.NetSentBitsPerSec = kv.Value * 8.0; break; }
        }

        private void ReadGpu(Snapshot s)
        {
            // Task Manager's GPU % = for each engine type, sum the utilization of every
            // process using it, then take the busiest engine type on that adapter.
            var perEngine = new Dictionary<string, double>();
            foreach (var kv in Array(_cGpuEngine))
            {
                string adapter = AdapterOf(kv.Key);
                if (adapter == null) continue;
                string key = adapter + "|" + EngineTypeOf(kv.Key);
                double cur;
                perEngine[key] = perEngine.TryGetValue(key, out cur) ? cur + kv.Value : kv.Value;
            }

            var perAdapter = new Dictionary<string, double>();
            foreach (var kv in perEngine)
            {
                string adapter = kv.Key.Substring(0, kv.Key.IndexOf("|", StringComparison.Ordinal));
                double cur;
                if (!perAdapter.TryGetValue(adapter, out cur) || kv.Value > cur)
                    perAdapter[adapter] = kv.Value;
            }

            // Pick the adapter holding the most dedicated video memory: on a hybrid
            // system that is the discrete card, not the iGPU or a virtual display.
            string best = null;
            double bestMem = -1;
            foreach (var kv in Array(_cGpuMem))
            {
                if (kv.Value > bestMem) { bestMem = kv.Value; best = kv.Key; }
            }

            if (best != null)
            {
                s.VramUsedGB = bestMem / 1073741824.0;
                double util;
                if (perAdapter.TryGetValue(best, out util)) s.GpuPercent = Clamp(util, 0, 100);
            }
            else
            {
                foreach (var kv in perAdapter)
                    if (kv.Value > s.GpuPercent) s.GpuPercent = Clamp(kv.Value, 0, 100);
            }
        }

        private void ReadNvml(Snapshot s)
        {
            if (!_nvmlReady) return;
            try
            {
                uint temp;
                if (Nvml.GetTemperature(_nvmlDevice, 0, out temp) == 0) s.GpuTempC = (int)temp;
                Nvml.Memory mem;
                if (Nvml.GetMemoryInfo(_nvmlDevice, out mem) == 0 && mem.total > 0)
                {
                    s.VramTotalGB = mem.total / 1073741824.0;
                    if (s.VramUsedGB <= 0) s.VramUsedGB = mem.used / 1073741824.0;
                }
            }
            catch { _nvmlReady = false; }
        }

        // "pid_1234_luid_0x00000000_0x0000D3A5_phys_0_eng_3_engtype_3D" -> "luid_0x00000000_0x0000D3A5_phys_0"
        private static string AdapterOf(string instance)
        {
            int li = instance.IndexOf("luid_", StringComparison.Ordinal);
            if (li < 0) return null;
            int ei = instance.IndexOf("_eng_", StringComparison.Ordinal);
            return ei > li ? instance.Substring(li, ei - li) : instance.Substring(li);
        }

        private static string EngineTypeOf(string instance)
        {
            int ti = instance.IndexOf("engtype_", StringComparison.Ordinal);
            return ti < 0 ? "" : instance.Substring(ti + 8);
        }

        private double Single(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return 0;
            uint type;
            Pdh.PDH_FMT_COUNTERVALUE value;
            uint rc = Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, out type, out value);
            if (rc != Pdh.ERROR_SUCCESS || value.CStatus != 0) return 0;
            return double.IsNaN(value.doubleValue) || double.IsInfinity(value.doubleValue) ? 0 : value.doubleValue;
        }

        private List<KeyValuePair<string, double>> Array(IntPtr counter)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (counter == IntPtr.Zero) return result;

            uint size = (uint)_buffer.Length, count;
            GCHandle handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                uint rc = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100,
                                                           ref size, out count, handle.AddrOfPinnedObject());
                if (rc == Pdh.PDH_MORE_DATA)
                {
                    handle.Free();
                    _buffer = new byte[Math.Max(size + 4096, (uint)_buffer.Length * 2)];
                    handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                    size = (uint)_buffer.Length;
                    rc = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100,
                                                          ref size, out count, handle.AddrOfPinnedObject());
                }
                if (rc != Pdh.ERROR_SUCCESS) return result;

                IntPtr baseAddr = handle.AddrOfPinnedObject();
                int stride = Marshal.SizeOf(typeof(Pdh.PDH_FMT_COUNTERVALUE_ITEM_W));
                for (int i = 0; i < count; i++)
                {
                    var item = (Pdh.PDH_FMT_COUNTERVALUE_ITEM_W)Marshal.PtrToStructure(
                        new IntPtr(baseAddr.ToInt64() + (long)i * stride), typeof(Pdh.PDH_FMT_COUNTERVALUE_ITEM_W));
                    if (item.value.CStatus != 0) continue;
                    double v = item.value.doubleValue;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    string name = item.szName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(item.szName);
                    result.Add(new KeyValuePair<string, double>(name ?? "", v));
                }
            }
            catch { }
            finally { if (handle.IsAllocated) handle.Free(); }
            return result;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
            if (_nvmlReady) { try { Nvml.Shutdown(); } catch { } _nvmlReady = false; }
        }
    }
}
