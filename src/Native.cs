using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MachineGauges
{
    // ---------- PDH (Performance Data Helper) ----------
    // Same data source Task Manager reads. English counter names are used so the
    // app behaves identically on localized Windows installs.
    internal static class Pdh
    {
        public const uint PDH_FMT_DOUBLE = 0x00000200;
        public const uint PDH_FMT_NOCAP100 = 0x00008000;
        public const uint PDH_MORE_DATA = 0x800007D2;
        public const uint ERROR_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE_ITEM_W
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }

    // ---------- NVML (NVIDIA driver library, optional) ----------
    internal static class Nvml
    {
        public const int ClockGpu = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct Memory
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        public static extern int Init();

        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        public static extern int Shutdown();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        public static extern int GetCount(out uint count);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int GetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
        public static extern int GetName(IntPtr device, byte[] name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern int GetTemperature(IntPtr device, int sensorType, out uint temp);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        public static extern int GetMemoryInfo(IntPtr device, out Memory memory);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
        public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        public static extern int GetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
        public static extern int GetFanSpeed(IntPtr device, out uint percent);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
        public static extern int GetClockInfo(IntPtr device, int clockType, out uint mhz);
    }

    // ---------- DXGI adapter enumeration ----------
    // Gives every GPU's name, LUID (to match PDH counters) and real dedicated memory size,
    // for any vendor.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig]
        int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIAdapter
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    internal static class Dxgi
    {
        public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
    }

    // ---------- Win32 ----------
    [StructLayout(LayoutKind.Sequential)]
    internal class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    internal static class Win32
    {
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1;
        public const uint MOD_CONTROL = 0x2;
        public const uint MOD_SHIFT = 0x4;
        public const uint MOD_WIN = 0x8;
        public const uint MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);

        // QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        // Makes the window per-monitor DPI aware when the OS supports it.
        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)) != IntPtr.Zero) return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static uint GetDpiAt(int x, int y)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT(x, y), 2 /* MONITOR_DEFAULTTONEAREST */);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0) return dx;
            }
            catch { }
            return 96;
        }
    }
}
