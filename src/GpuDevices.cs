using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MachineGauges
{
    internal sealed class GpuDevice
    {
        public string Name;
        public string LuidKey;          // "luid_0x00000000_0x0001026f", as used in PDH GPU instance names
        public double DedicatedTotalGB;
        public uint VendorId;
        public IntPtr Nvml = IntPtr.Zero;
    }

    /// <summary>
    /// Every GPU in the machine: DXGI for names, LUIDs and memory size (any vendor), plus
    /// NVML for power, fan, clocks and temperature on NVIDIA cards.
    /// </summary>
    internal sealed class GpuDevices : IDisposable
    {
        private const uint VendorNvidia = 0x10DE;
        private const uint VendorMicrosoft = 0x1414;
        private const double GB = 1073741824.0;

        private List<GpuDevice> _devices = new List<GpuDevice>();
        private bool _nvmlReady;
        private bool _hasClockInfo = true;
        private bool _hasFan = true;

        public IList<GpuDevice> Devices { get { return _devices; } }

        public bool NeedsRetry
        {
            get
            {
                if (_devices.Count == 0) return true;
                foreach (GpuDevice d in _devices)
                    if (d.VendorId == VendorNvidia && d.Nvml == IntPtr.Zero) return true;
                return false;
            }
        }

        public void Refresh()
        {
            try { EnumerateDxgi(); } catch { }
            try { AttachNvml(); } catch { _nvmlReady = false; }
        }

        private void EnumerateDxgi()
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            IDXGIFactory1 factory;
            if (Dxgi.CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null) return;

            var list = new List<GpuDevice>();
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(i, out adapter) != 0 || adapter == null) break;
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (adapter.GetDesc1(out d) != 0) continue;
                        if ((d.Flags & Dxgi.DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == VendorMicrosoft) continue;

                        string luid = string.Format("luid_0x{0:x8}_0x{1:x8}", unchecked((uint)d.LuidHigh), d.LuidLow);
                        if (list.Exists(x => x.LuidKey == luid)) continue;

                        list.Add(new GpuDevice
                        {
                            Name = (d.Description ?? "GPU").Trim(),
                            LuidKey = luid,
                            DedicatedTotalGB = d.DedicatedVideoMemory.ToUInt64() / GB,
                            VendorId = d.VendorId
                        });
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }

            // Keep NVML handles already matched to adapters that are still present.
            foreach (GpuDevice fresh in list)
            {
                GpuDevice old = _devices.Find(x => x.LuidKey == fresh.LuidKey);
                if (old != null) fresh.Nvml = old.Nvml;
            }
            _devices = list;
        }

        private void AttachNvml()
        {
            if (!_nvmlReady)
            {
                try { _nvmlReady = Nvml.Init() == 0; }
                catch { _nvmlReady = false; }
            }
            if (!_nvmlReady) return;

            uint count;
            if (Nvml.GetCount(out count) != 0) return;

            for (uint i = 0; i < count; i++)
            {
                IntPtr handle;
                if (Nvml.GetHandleByIndex(i, out handle) != 0) continue;
                if (_devices.Exists(x => x.Nvml == handle)) continue;

                string name = NvmlName(handle);
                GpuDevice match = _devices.Find(x => x.VendorId == VendorNvidia && x.Nvml == IntPtr.Zero &&
                                                     string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    match = _devices.Find(x => x.VendorId == VendorNvidia && x.Nvml == IntPtr.Zero);
                if (match != null) match.Nvml = handle;
            }
        }

        private static string NvmlName(IntPtr handle)
        {
            var buf = new byte[96];
            if (Nvml.GetName(handle, buf, (uint)buf.Length) != 0) return "";
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n).Trim();
        }

        /// <summary>Fills the vendor-library readings (temperature, power, fan, clocks) for one GPU.</summary>
        public void ReadNvml(GpuDevice device, GpuReading r)
        {
            if (!_nvmlReady || device.Nvml == IntPtr.Zero) return;
            IntPtr h = device.Nvml;
            try
            {
                uint v;
                if (Nvml.GetTemperature(h, 0, out v) == 0) r.TempC = (int)v;
                if (Nvml.GetPowerUsage(h, out v) == 0) r.PowerW = v / 1000.0;
                if (Nvml.GetEnforcedPowerLimit(h, out v) == 0) r.PowerLimitW = v / 1000.0;

                // NVML's total is the figure Task Manager rounds from; DXGI reports a little less.
                Nvml.Memory mem;
                if (Nvml.GetMemoryInfo(h, out mem) == 0 && mem.total > 0) r.VramTotalGB = mem.total / GB;

                if (_hasFan)
                {
                    try { if (Nvml.GetFanSpeed(h, out v) == 0) r.FanPct = (int)v; }
                    catch (EntryPointNotFoundException) { _hasFan = false; }
                }
                // Core clock only: on current drivers the memory clock query returns the core
                // clock too, which would be a wrong number.
                if (_hasClockInfo)
                {
                    try { if (Nvml.GetClockInfo(h, Nvml.ClockGpu, out v) == 0) r.CoreMhz = (int)v; }
                    catch (EntryPointNotFoundException) { _hasClockInfo = false; }
                }
            }
            catch (DllNotFoundException) { _nvmlReady = false; }
            catch { }
        }

        public void Dispose()
        {
            if (_nvmlReady)
            {
                try { Nvml.Shutdown(); } catch { }
                _nvmlReady = false;
            }
        }
    }
}
