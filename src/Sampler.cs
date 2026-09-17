using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MachineGauges
{
    internal sealed class GpuReading
    {
        public string Name;
        public string LuidKey;
        public double Util;
        public double VramUsedGB;
        public double VramTotalGB;
        public int TempC = -1;
        public double PowerW = -1;
        public double PowerLimitW = -1;
        public int FanPct = -1;
        public int CoreMhz = -1;
    }

    internal sealed class DiskReading
    {
        public string Name;
        public double Active;
        public double ReadBps;
        public double WriteBps;
    }

    internal sealed class VolumeReading
    {
        public string Name;
        public double FreeGB;
        public double TotalGB;
    }

    internal sealed class Snapshot
    {
        public DateTime Time = DateTime.Now;

        public double CpuPercent;        // Task Manager "CPU"
        public double CpuGhz;            // Task Manager "Speed"
        public int CpuTempC = -1;        // from LibreHardwareMonitor, when running
        public double[] CoreUtil = new double[0];

        public double RamPercent;
        public double RamUsedGB;
        public double RamTotalGB;

        // The GPU shown in the strip (see Sampler.PreferredGpu), plus every GPU for the panel.
        public double GpuPercent;
        public double VramUsedGB;
        public double VramTotalGB;
        public int GpuTempC = -1;
        public double GpuPowerW = -1;
        public List<GpuReading> Gpus = new List<GpuReading>();
        public int SelectedGpu = -1;

        public double DiskPercent;       // Task Manager "Active time" of the busiest disk
        public double DiskBytesPerSec;
        public List<DiskReading> Disks = new List<DiskReading>();
        public List<VolumeReading> Volumes = new List<VolumeReading>();

        public double NetBitsPerSec;
        public double NetRecvBitsPerSec;
        public double NetSentBitsPerSec;

        public int ForegroundPid;
        public double Fps = -1;
        public Dictionary<int, double> Gpu3DByPid = new Dictionary<int, double>();
        public List<ProcReading> Processes;   // only while the detail panel is open

        public int PingMs = -1;
        public int PingLossPct;
        public int BatteryPct = -1;
        public bool BatteryCharging;
        public int BatteryMinutesLeft = -1;
        public TimeSpan Uptime;
    }

    /// <summary>
    /// Reads the same performance counters Task Manager uses, in a single PDH query
    /// so every metric comes from one coherent sample.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        private const double GB = 1073741824.0;

        private IntPtr _query;
        private IntPtr _cCpu, _cCpuPerf, _cCores, _cDiskIdle, _cDiskRead, _cDiskWrite, _cDiskBytes,
                       _cNet, _cNetRecv, _cNetSent, _cGpuEngine, _cGpuMem;
        private readonly double _baseGhz;
        private readonly GpuDevices _gpus = new GpuDevices();
        private readonly ProcessSampler _processes = new ProcessSampler();
        private byte[] _buffer = new byte[64 * 1024];
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private double _lastRetry;
        private double _lastVolumes = -1000;
        private List<VolumeReading> _volumes = new List<VolumeReading>();
        private bool _detailWasOn;

        /// <summary>When true, per-process data is collected too (costs a few ms per sample).</summary>
        public bool DetailRequested;
        /// <summary>GPU name to show in the strip; empty picks the card with the most video memory.</summary>
        public string PreferredGpu = "";

        public FpsMonitor Fps;
        public PingMonitor Ping;
        public CpuTempReader CpuTemp;

        public string CpuName { get; private set; }

        public Sampler(double baseMhz)
        {
            _baseGhz = baseMhz / 1000.0;
            CpuName = ReadCpuName();

            AddMissingCounters();
            _gpus.Refresh();

            // Rate counters need a baseline sample before they report anything.
            if (_query != IntPtr.Zero) Pdh.PdhCollectQueryData(_query);
        }

        public IList<GpuDevice> GpuDevices { get { return _gpus.Devices; } }

        private static string ReadCpuName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                {
                    string name = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrEmpty(name)) return name.Trim();
                }
            }
            catch { }
            return "CPU";
        }

        /// <summary>Adds every counter that isn't registered yet; safe to call repeatedly.</summary>
        private void AddMissingCounters()
        {
            if (_query == IntPtr.Zero && Pdh.PdhOpenQueryW(null, IntPtr.Zero, out _query) != Pdh.ERROR_SUCCESS)
                _query = IntPtr.Zero;

            if (_cCpu == IntPtr.Zero) _cCpu = Add(@"\Processor Information(_Total)\% Processor Utility");
            if (_cCpu == IntPtr.Zero) _cCpu = Add(@"\Processor Information(_Total)\% Processor Time");
            if (_cCpuPerf == IntPtr.Zero) _cCpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
            if (_cCores == IntPtr.Zero) _cCores = Add(@"\Processor Information(*)\% Processor Utility");
            if (_cDiskIdle == IntPtr.Zero) _cDiskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");
            if (_cDiskRead == IntPtr.Zero) _cDiskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            if (_cDiskWrite == IntPtr.Zero) _cDiskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
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
                        || _cCores == IntPtr.Zero || _cDiskIdle == IntPtr.Zero || _cDiskRead == IntPtr.Zero
                        || _cDiskWrite == IntPtr.Zero || _cDiskBytes == IntPtr.Zero || _cNet == IntPtr.Zero
                        || _cNetRecv == IntPtr.Zero || _cNetSent == IntPtr.Zero
                        || _cGpuEngine == IntPtr.Zero || _cGpuMem == IntPtr.Zero || _gpus.NeedsRetry;
            double now = _uptime.Elapsed.TotalSeconds;
            if (!missing || now > 600 || now - _lastRetry < 15) return;
            _lastRetry = now;

            AddMissingCounters();
            _gpus.Refresh();
        }

        private IntPtr Add(string path)
        {
            if (_query == IntPtr.Zero) return IntPtr.Zero;
            IntPtr counter;
            return Pdh.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out counter) == Pdh.ERROR_SUCCESS
                ? counter : IntPtr.Zero;
        }

        public Snapshot Sample()
        {
            RetryMissing();
            var s = new Snapshot();
            ReadMemory(s);
            s.Uptime = TimeSpan.FromMilliseconds(Win32.GetTickCount64());
            s.ForegroundPid = ForegroundPid();

            if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == Pdh.ERROR_SUCCESS)
            {
                s.CpuPercent = Clamp(Single(_cCpu), 0, 100);
                double perf = Single(_cCpuPerf);
                s.CpuGhz = perf > 0 ? _baseGhz * perf / 100.0 : _baseGhz;

                ReadCores(s);
                ReadDisks(s);
                ReadNetwork(s);
                ReadGpus(s);
            }

            ReadVolumes(s);
            ReadBattery(s);
            ReadMonitors(s);

            if (DetailRequested)
            {
                if (!_detailWasOn) _processes.Reset();
                s.Processes = _processes.Sample(_gpuByPid);
            }
            _detailWasOn = DetailRequested;
            return s;
        }

        private static int ForegroundPid()
        {
            try
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return 0;
                uint pid;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                return (int)pid;
            }
            catch { return 0; }
        }

        private static void ReadMemory(Snapshot s)
        {
            var m = new MEMORYSTATUSEX();
            if (!Win32.GlobalMemoryStatusEx(m) || m.ullTotalPhys == 0) return;
            ulong used = m.ullTotalPhys - m.ullAvailPhys;
            s.RamTotalGB = m.ullTotalPhys / GB;
            s.RamUsedGB = used / GB;
            s.RamPercent = used * 100.0 / m.ullTotalPhys;
        }

        private void ReadCores(Snapshot s)
        {
            // Instances are "group,index" plus "_Total" and "group,_Total".
            var cores = new List<KeyValuePair<long, double>>();
            foreach (var kv in Array(_cCores))
            {
                int comma = kv.Key.IndexOf(',');
                if (comma < 0) continue;
                int group, index;
                if (!int.TryParse(kv.Key.Substring(0, comma), out group)) continue;
                if (!int.TryParse(kv.Key.Substring(comma + 1), out index)) continue;
                cores.Add(new KeyValuePair<long, double>(((long)group << 32) | (uint)index, kv.Value));
            }
            cores.Sort((a, b) => a.Key.CompareTo(b.Key));
            s.CoreUtil = new double[cores.Count];
            for (int i = 0; i < cores.Count; i++) s.CoreUtil[i] = Clamp(cores[i].Value, 0, 100);
        }

        private void ReadDisks(Snapshot s)
        {
            // Task Manager's "Active time" is 100 - % Idle Time.
            var byName = new Dictionary<string, DiskReading>();
            foreach (var kv in Array(_cDiskIdle))
            {
                if (kv.Key == "_Total") continue;
                byName[kv.Key] = new DiskReading { Name = DiskName(kv.Key), Active = Clamp(100.0 - kv.Value, 0, 100) };
            }
            foreach (var kv in Array(_cDiskRead))
            {
                DiskReading d;
                if (byName.TryGetValue(kv.Key, out d)) d.ReadBps = Math.Max(0, kv.Value);
            }
            foreach (var kv in Array(_cDiskWrite))
            {
                DiskReading d;
                if (byName.TryGetValue(kv.Key, out d)) d.WriteBps = Math.Max(0, kv.Value);
            }

            var keys = new List<string>(byName.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string k in keys)
            {
                s.Disks.Add(byName[k]);
                if (byName[k].Active > s.DiskPercent) s.DiskPercent = byName[k].Active;
            }
            s.DiskBytesPerSec = Math.Max(0, Single(_cDiskBytes));
        }

        // "0 C: D:" -> "Disk 0 (C: D:)"
        private static string DiskName(string instance)
        {
            int space = instance.IndexOf(' ');
            if (space < 0) return "Disk " + instance;
            return "Disk " + instance.Substring(0, space) + " (" + instance.Substring(space + 1).Trim() + ")";
        }

        private void ReadVolumes(Snapshot s)
        {
            double now = _uptime.Elapsed.TotalSeconds;
            if (now - _lastVolumes >= 30)
            {
                _lastVolumes = now;
                var list = new List<VolumeReading>();
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                            list.Add(new VolumeReading
                            {
                                Name = d.Name.TrimEnd('\\'),
                                FreeGB = d.AvailableFreeSpace / GB,
                                TotalGB = d.TotalSize / GB
                            });
                        }
                        catch { }
                    }
                }
                catch { }
                _volumes = list;
            }
            s.Volumes = _volumes;
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

        private Dictionary<int, double> _gpuByPid = new Dictionary<int, double>();

        private void ReadGpus(Snapshot s)
        {
            // Task Manager's GPU % = for each engine type, sum the utilization of every process
            // using it, then take the busiest engine type. The per-process column works the same
            // way within one process.
            var perAdapterEngine = new Dictionary<string, double>();
            var perPidEngine = new Dictionary<string, double>();
            var pid3D = new Dictionary<int, double>();

            foreach (var kv in Array(_cGpuEngine))
            {
                string name = kv.Key;
                string luid = LuidOf(name);
                if (luid == null) continue;
                string engType = EngineTypeOf(name);
                int pid = PidOf(name);

                Accumulate(perAdapterEngine, luid + "|" + engType, kv.Value);
                if (pid > 0)
                {
                    Accumulate(perPidEngine, pid.ToString(CultureInfo.InvariantCulture) + "|" + engType, kv.Value);
                    if (engType.Equals("3D", StringComparison.OrdinalIgnoreCase))
                    {
                        double cur;
                        pid3D[pid] = pid3D.TryGetValue(pid, out cur) ? cur + kv.Value : kv.Value;
                    }
                }
            }

            var utilByLuid = MaxByPrefix(perAdapterEngine);
            var byPidKey = MaxByPrefix(perPidEngine);
            var gpuByPid = new Dictionary<int, double>(byPidKey.Count);
            foreach (var kv in byPidKey)
            {
                int pid;
                if (int.TryParse(kv.Key, out pid)) gpuByPid[pid] = kv.Value;
            }
            _gpuByPid = gpuByPid;
            s.Gpu3DByPid = pid3D;

            var memByLuid = new Dictionary<string, double>();
            foreach (var kv in Array(_cGpuMem))
            {
                string luid = LuidOf(kv.Key);
                if (luid != null) Accumulate(memByLuid, luid, kv.Value);
            }

            if (_gpus.Devices.Count > 0)
            {
                var withCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (GpuDevice d in _gpus.Devices)
                    if (memByLuid.ContainsKey(d.LuidKey) || utilByLuid.ContainsKey(d.LuidKey)) withCounters.Add(d.Name);

                foreach (GpuDevice d in _gpus.Devices)
                {
                    // DXGI can list a phantom second instance of a card (e.g. for remote-display
                    // software). Skip it when a same-named adapter has real counters.
                    bool hasCounters = memByLuid.ContainsKey(d.LuidKey) || utilByLuid.ContainsKey(d.LuidKey);
                    if (!hasCounters && withCounters.Contains(d.Name)) continue;

                    var r = new GpuReading { Name = d.Name, LuidKey = d.LuidKey, VramTotalGB = d.DedicatedTotalGB };
                    double v;
                    if (utilByLuid.TryGetValue(d.LuidKey, out v)) r.Util = Clamp(v, 0, 100);
                    if (memByLuid.TryGetValue(d.LuidKey, out v)) r.VramUsedGB = v / GB;
                    _gpus.ReadNvml(d, r);
                    s.Gpus.Add(r);
                }
            }
            else
            {
                // No DXGI list (very old drivers): fall back to what the counters expose.
                int n = 0;
                foreach (var kv in memByLuid)
                {
                    var r = new GpuReading { Name = "GPU " + n++, LuidKey = kv.Key, VramUsedGB = kv.Value / GB };
                    double v;
                    if (utilByLuid.TryGetValue(kv.Key, out v)) r.Util = Clamp(v, 0, 100);
                    s.Gpus.Add(r);
                }
            }

            s.SelectedGpu = PickGpu(s.Gpus);
            if (s.SelectedGpu >= 0)
            {
                GpuReading g = s.Gpus[s.SelectedGpu];
                s.GpuPercent = g.Util;
                s.VramUsedGB = g.VramUsedGB;
                s.VramTotalGB = g.VramTotalGB;
                s.GpuTempC = g.TempC;
                s.GpuPowerW = g.PowerW;
            }
        }

        private int PickGpu(List<GpuReading> gpus)
        {
            if (gpus.Count == 0) return -1;
            if (!string.IsNullOrEmpty(PreferredGpu))
            {
                int named = gpus.FindIndex(g => string.Equals(g.Name, PreferredGpu, StringComparison.OrdinalIgnoreCase));
                if (named >= 0) return named;
            }
            // The card with the most dedicated memory is the discrete one on hybrid systems.
            int best = 0;
            for (int i = 1; i < gpus.Count; i++)
            {
                double a = Math.Max(gpus[i].VramTotalGB, gpus[i].VramUsedGB);
                double b = Math.Max(gpus[best].VramTotalGB, gpus[best].VramUsedGB);
                if (a > b) best = i;
            }
            return best;
        }

        private static void Accumulate(Dictionary<string, double> map, string key, double value)
        {
            double cur;
            map[key] = map.TryGetValue(key, out cur) ? cur + value : value;
        }

        // "a|x" -> max over x, keyed by "a"
        private static Dictionary<string, double> MaxByPrefix(Dictionary<string, double> map)
        {
            var result = new Dictionary<string, double>();
            foreach (var kv in map)
            {
                string prefix = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                double cur;
                if (!result.TryGetValue(prefix, out cur) || kv.Value > cur) result[prefix] = kv.Value;
            }
            return result;
        }

        private void ReadBattery(Snapshot s)
        {
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 ||
                    ps.BatteryChargeStatus == BatteryChargeStatus.Unknown || ps.BatteryLifePercent > 1.0f)
                    return;
                s.BatteryPct = (int)Math.Round(ps.BatteryLifePercent * 100);
                s.BatteryCharging = ps.PowerLineStatus == PowerLineStatus.Online;
                s.BatteryMinutesLeft = ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining / 60 : -1;
            }
            catch { }
        }

        private void ReadMonitors(Snapshot s)
        {
            if (CpuTemp != null) s.CpuTempC = CpuTemp.LatestC;
            if (Ping != null)
            {
                s.PingMs = Ping.LastMs;
                s.PingLossPct = Ping.LossPct;
            }
            if (Fps != null)
            {
                Fps.Tick();
                s.Fps = Fps.GetFps(s.ForegroundPid);
            }
        }

        // "pid_1234_luid_0x00000000_0x0000D3A5_phys_0_eng_3_engtype_3D" -> "luid_0x00000000_0x0000d3a5"
        private static string LuidOf(string instance)
        {
            int li = instance.IndexOf("luid_", StringComparison.Ordinal);
            if (li < 0) return null;
            int pi = instance.IndexOf("_phys", li, StringComparison.Ordinal);
            string luid = pi > li ? instance.Substring(li, pi - li) : instance.Substring(li);
            return luid.ToLowerInvariant();
        }

        private static string EngineTypeOf(string instance)
        {
            int ti = instance.IndexOf("engtype_", StringComparison.Ordinal);
            return ti < 0 ? "" : instance.Substring(ti + 8);
        }

        private static int PidOf(string instance)
        {
            if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return 0;
            int end = instance.IndexOf('_', 4);
            int pid;
            return end > 4 && int.TryParse(instance.Substring(4, end - 4), out pid) ? pid : 0;
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
            _gpus.Dispose();
            _processes.Dispose();
        }
    }
}
