using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MachineGauges
{
    internal sealed class ProcReading
    {
        public string Name;
        public double Cpu;      // % of total CPU
        public double MemGB;    // private working set, Task Manager's "Memory" column
        public double Gpu;      // % of the busiest GPU engine
        public int Count;       // processes grouped under this name
    }

    /// <summary>
    /// Per-process CPU, memory and GPU in one NtQuerySystemInformation call (what Task Manager
    /// uses), grouped by executable name. Only sampled while the detail panel is open.
    /// </summary>
    internal sealed class ProcessSampler : IDisposable
    {
        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const double GB = 1073741824.0;

        // x64 SYSTEM_PROCESS_INFORMATION offsets
        private const int OffNext = 0x00;
        private const int OffPrivateWorkingSet = 0x08;
        private const int OffCreateTime = 0x20;
        private const int OffUserTime = 0x28;
        private const int OffKernelTime = 0x30;
        private const int OffImageNameLength = 0x38;
        private const int OffImageNameBuffer = 0x40;
        private const int OffProcessId = 0x50;

        private IntPtr _buffer = IntPtr.Zero;
        private int _bufferLength;
        private Dictionary<long, long> _lastCpuTime = new Dictionary<long, long>();
        private long _lastStamp;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _logicalCpus = Environment.ProcessorCount;

        public List<ProcReading> Sample(IDictionary<int, double> gpuByPid)
        {
            var result = new List<ProcReading>();
            if (!Query()) return result;

            long now = _clock.Elapsed.Ticks;                 // 100 ns, same unit as kernel/user times
            double elapsed = now - _lastStamp;
            bool haveBaseline = _lastStamp > 0 && elapsed > 0;

            var nextCpuTime = new Dictionary<long, long>(_lastCpuTime.Count + 64);
            var byName = new Dictionary<string, ProcReading>(StringComparer.OrdinalIgnoreCase);

            long offset = 0;
            while (true)
            {
                var entry = new IntPtr(_buffer.ToInt64() + offset);
                int next = Marshal.ReadInt32(entry, OffNext);
                long pid = Marshal.ReadIntPtr(entry, OffProcessId).ToInt64();

                if (pid != 0)   // skip the System Idle Process
                {
                    long createTime = Marshal.ReadInt64(entry, OffCreateTime);
                    long cpuTime = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
                    long privateWs = Marshal.ReadInt64(entry, OffPrivateWorkingSet);
                    int nameBytes = (ushort)Marshal.ReadInt16(entry, OffImageNameLength);
                    IntPtr namePtr = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
                    string name = namePtr == IntPtr.Zero || nameBytes == 0
                        ? "System"
                        : Marshal.PtrToStringUni(namePtr, nameBytes / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 4);

                    // PIDs get reused; pair them with the creation time.
                    long key = (createTime * 397) ^ pid;
                    nextCpuTime[key] = cpuTime;

                    double cpu = 0;
                    long previous;
                    if (haveBaseline && _lastCpuTime.TryGetValue(key, out previous))
                        cpu = Math.Max(0, (cpuTime - previous) * 100.0 / (elapsed * _logicalCpus));

                    ProcReading r;
                    if (!byName.TryGetValue(name, out r))
                    {
                        r = new ProcReading { Name = name };
                        byName[name] = r;
                    }
                    r.Cpu += cpu;
                    r.MemGB += privateWs / GB;
                    r.Count++;

                    double gpu;
                    if (gpuByPid != null && gpuByPid.TryGetValue((int)pid, out gpu)) r.Gpu += gpu;
                }

                if (next == 0) break;
                offset += next;
            }

            _lastCpuTime = nextCpuTime;
            _lastStamp = now;

            foreach (ProcReading r in byName.Values)
            {
                r.Cpu = Math.Min(100, r.Cpu);
                r.Gpu = Math.Min(100, r.Gpu);
                result.Add(r);
            }
            return result;
        }

        /// <summary>Clears the CPU baseline so a reopened panel doesn't average over the time it was closed.</summary>
        public void Reset()
        {
            _lastCpuTime.Clear();
            _lastStamp = 0;
        }

        private bool Query()
        {
            if (_buffer == IntPtr.Zero)
            {
                _bufferLength = 1 << 20;
                _buffer = Marshal.AllocHGlobal(_bufferLength);
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int needed;
                int status = Win32.NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferLength, out needed);
                if (status == 0) return true;
                if (status != StatusInfoLengthMismatch) return false;

                Marshal.FreeHGlobal(_buffer);
                _bufferLength = Math.Max(needed, _bufferLength) + (256 << 10);
                _buffer = Marshal.AllocHGlobal(_bufferLength);
            }
            return false;
        }

        public void Dispose()
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }
}
