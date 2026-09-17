using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace MachineGauges
{
    internal enum FpsState { Off, Running, NeedsPermission, Failed }

    /// <summary>
    /// Experimental FPS counter: a real-time ETW session counting Direct3D present calls per
    /// process (DXGI covers Direct3D 10/11/12, plus Direct3D 9). OpenGL and Vulkan titles that
    /// don't present through DXGI aren't counted.
    ///
    /// Windows only lets administrators and members of "Performance Log Users" start such a
    /// session, so this reports <see cref="FpsState.NeedsPermission"/> until the user grants it.
    /// </summary>
    internal sealed class FpsMonitor : IDisposable
    {
        private const string SessionName = "MachineGauges-FPS";
        private const uint ErrorAccessDenied = 5;
        private const uint ErrorAlreadyExists = 183;
        private const uint WnodeFlagTracedGuid = 0x00020000;
        private const uint EventTraceRealTimeMode = 0x00000100;
        private const uint ProcessTraceModeRealTime = 0x00000100;
        private const uint ProcessTraceModeEventRecord = 0x10000000;
        private const uint EventTraceControlStop = 1;
        private const uint EventControlCodeEnableProvider = 1;
        private const int PropertiesSize = 120;
        private const int LogfileSize = 448;
        private static readonly long InvalidTraceHandle = -1;

        // Provider GUID first fields, and the Present_Start event IDs.
        private static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        private static readonly Guid D3D9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        private const uint DxgiData1 = 0xCA11C036;
        private const uint D3D9Data1 = 0x783ACA0A;
        private const ushort DxgiPresentStart = 42;
        private const ushort D3D9PresentStart = 1;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EventRecordCallback(IntPtr eventRecord);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint StartTraceW(out long sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ControlTraceW(long sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll")]
        private static extern uint EnableTraceEx2(long sessionHandle, ref Guid providerId, uint controlCode, byte level,
                                                  ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr parameters);

        [DllImport("advapi32.dll")]
        private static extern long OpenTraceW(IntPtr logfile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(long[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(long traceHandle);

        private readonly object _lock = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Dictionary<int, int> _counts = new Dictionary<int, int>();
        private Dictionary<int, double> _fps = new Dictionary<int, double>();
        private double _lastTick;

        private EventRecordCallback _callback;   // must stay referenced while tracing
        private IntPtr _logfile = IntPtr.Zero;
        private IntPtr _loggerName = IntPtr.Zero;
        private long _session;
        private long _trace = InvalidTraceHandle;
        private Thread _thread;

        public FpsState State { get; private set; }
        public string Error { get; private set; }

        public void Start()
        {
            if (State == FpsState.Running) return;
            Error = null;

            uint rc = StartSession();
            if (rc == ErrorAlreadyExists)
            {
                // A previous run didn't shut down cleanly: stop its session and retry.
                StopSessionByName();
                rc = StartSession();
            }
            if (rc == ErrorAccessDenied)
            {
                State = FpsState.NeedsPermission;
                return;
            }
            if (rc != 0)
            {
                Fail("Could not start the trace session (error " + rc + ").");
                return;
            }

            Guid dxgi = DxgiProvider, d3d9 = D3D9Provider;
            EnableTraceEx2(_session, ref dxgi, EventControlCodeEnableProvider, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_session, ref d3d9, EventControlCodeEnableProvider, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);

            _callback = OnEvent;
            _loggerName = Marshal.StringToHGlobalUni(SessionName);
            _logfile = Marshal.AllocHGlobal(LogfileSize);
            ZeroMemory(_logfile, LogfileSize);
            Marshal.WriteIntPtr(_logfile, 8, _loggerName);                                          // LoggerName
            Marshal.WriteInt32(_logfile, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
            Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));   // EventRecordCallback

            _trace = OpenTraceW(_logfile);
            if (_trace == InvalidTraceHandle)
            {
                StopSessionByName();
                Fail("Could not open the trace session.");
                return;
            }

            _thread = new Thread(() =>
            {
                try { ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero); } catch { }
            }) { IsBackground = true, Name = "MachineGauges fps" };
            _thread.Start();
            _lastTick = _clock.Elapsed.TotalSeconds;
            State = FpsState.Running;
        }

        private void Fail(string message)
        {
            Error = message;
            State = FpsState.Failed;
            FreeBuffers();
        }

        private void OnEvent(IntPtr record)
        {
            // EVENT_RECORD begins with EVENT_HEADER: ProcessId @12, ProviderId @24, EventDescriptor.Id @40.
            uint data1 = unchecked((uint)Marshal.ReadInt32(record, 24));
            ushort id = unchecked((ushort)Marshal.ReadInt16(record, 40));
            if (!((data1 == DxgiData1 && id == DxgiPresentStart) || (data1 == D3D9Data1 && id == D3D9PresentStart)))
                return;

            int pid = Marshal.ReadInt32(record, 12);
            lock (_lock)
            {
                int c;
                _counts.TryGetValue(pid, out c);
                _counts[pid] = c + 1;
            }
        }

        /// <summary>Turns the present counts since the last call into frames per second.</summary>
        public void Tick()
        {
            if (State != FpsState.Running) return;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastTick;
            if (dt < 0.25) return;
            _lastTick = now;

            Dictionary<int, int> counts;
            lock (_lock)
            {
                counts = _counts;
                _counts = new Dictionary<int, int>();
            }
            var fps = new Dictionary<int, double>(counts.Count);
            foreach (KeyValuePair<int, int> kv in counts) fps[kv.Key] = kv.Value / dt;
            _fps = fps;
        }

        /// <summary>Frames per second presented by a process, or -1 if it isn't presenting.</summary>
        public double GetFps(int pid)
        {
            double v;
            return _fps.TryGetValue(pid, out v) && v >= 1 ? v : -1;
        }

        public void Stop()
        {
            if (State == FpsState.Running)
            {
                StopSessionByName();
                if (_trace != InvalidTraceHandle) CloseTrace(_trace);
                if (_thread != null) _thread.Join(3000);
            }
            _trace = InvalidTraceHandle;
            _thread = null;
            FreeBuffers();
            _fps = new Dictionary<int, double>();
            State = FpsState.Off;
        }

        private uint StartSession()
        {
            IntPtr props = NewProperties();
            try { return StartTraceW(out _session, SessionName, props); }
            finally { Marshal.FreeHGlobal(props); }
        }

        private static void StopSessionByName()
        {
            IntPtr props = NewProperties();
            try { ControlTraceW(0, SessionName, props, EventTraceControlStop); }
            finally { Marshal.FreeHGlobal(props); }
        }

        private static IntPtr NewProperties()
        {
            int size = PropertiesSize + 2 * 1024;
            IntPtr p = Marshal.AllocHGlobal(size);
            ZeroMemory(p, size);
            Marshal.WriteInt32(p, 0, size);                                        // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                                          // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, unchecked((int)WnodeFlagTracedGuid));        // Wnode.Flags
            Marshal.WriteInt32(p, 64, unchecked((int)EventTraceRealTimeMode));     // LogFileMode
            Marshal.WriteInt32(p, 68, 1);                                          // FlushTimer (s)
            Marshal.WriteInt32(p, 116, PropertiesSize);                            // LoggerNameOffset
            return p;
        }

        private void FreeBuffers()
        {
            if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
            if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        }

        private static void ZeroMemory(IntPtr p, int size)
        {
            Marshal.Copy(new byte[size], 0, p, size);
        }

        public void Dispose()
        {
            Stop();
        }

        // ---------- permission ----------

        private const string PerformanceLogUsersSid = "S-1-5-32-559";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOCALGROUP_MEMBERS_INFO_3
        {
            public string DomainAndName;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupAddMembers(string server, string groupName, int level,
                                                          ref LOCALGROUP_MEMBERS_INFO_3 buffer, int count);

        public static bool IsElevated()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Adds a user to "Performance Log Users". Must run elevated; takes effect after signing
        /// out and back in. Returns null on success, otherwise an error message.
        /// </summary>
        public static string GrantPermission(string account)
        {
            try
            {
                string group = new SecurityIdentifier(PerformanceLogUsersSid).Translate(typeof(NTAccount)).Value;
                int slash = group.IndexOf('\\');
                if (slash >= 0) group = group.Substring(slash + 1);

                var member = new LOCALGROUP_MEMBERS_INFO_3 { DomainAndName = account };
                int rc = NetLocalGroupAddMembers(null, group, 3, ref member, 1);
                if (rc == 0 || rc == 1378 /* ERROR_MEMBER_IN_ALIAS: already a member */) return null;
                return "Windows returned error " + rc + ".";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
