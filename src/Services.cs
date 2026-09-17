using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MachineGauges
{
    /// <summary>Fixed-size history of one reading, oldest first.</summary>
    internal sealed class Series
    {
        private readonly double[] _values;
        private int _start;
        private int _count;

        public Series(int capacity)
        {
            _values = new double[capacity];
        }

        public int Count { get { return _count; } }
        public int Capacity { get { return _values.Length; } }

        public void Add(double v)
        {
            int end = (_start + _count) % _values.Length;
            _values[end] = v;
            if (_count < _values.Length) _count++;
            else _start = (_start + 1) % _values.Length;
        }

        /// <summary>i = 0 is the oldest retained value.</summary>
        public double this[int i]
        {
            get { return _values[(_start + i) % _values.Length]; }
        }
    }

    /// <summary>The last five minutes of every headline reading, sampled even while the panel is closed.</summary>
    internal sealed class History
    {
        public const int Seconds = 300;

        public readonly Series Cpu = new Series(Seconds);
        public readonly Series Ram = new Series(Seconds);
        public readonly Series Gpu = new Series(Seconds);
        public readonly Series Disk = new Series(Seconds);
        public readonly Series NetDown = new Series(Seconds);
        public readonly Series NetUp = new Series(Seconds);

        public void Add(Snapshot s)
        {
            Cpu.Add(s.CpuPercent);
            Ram.Add(s.RamPercent);
            Gpu.Add(s.GpuPercent);
            Disk.Add(s.DiskPercent);
            NetDown.Add(s.NetRecvBitsPerSec);
            NetUp.Add(s.NetSentBitsPerSec);
        }
    }

    /// <summary>Events worth keeping for troubleshooting (alerts, updates, errors). Capped at ~256 KB.</summary>
    internal static class DiagLog
    {
        private static readonly object Lock = new object();

        public static string FilePath { get { return Path.Combine(Config.Dir, "machinegauges.log"); } }

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Config.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Watches readings against the user's thresholds. A condition must hold for the sustain
    /// period before it fires, then stays quiet for the cooldown.
    /// </summary>
    internal sealed class Alerts
    {
        private sealed class Rule
        {
            public string Key;
            public DateTime Since = DateTime.MinValue;
            public DateTime LastFired = DateTime.MinValue;
        }

        private readonly Dictionary<string, Rule> _rules = new Dictionary<string, Rule>();
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan DiskCooldown = TimeSpan.FromHours(6);

        /// <summary>Raised with (title, message).</summary>
        public event Action<string, string> Fired;

        public void Evaluate(Snapshot s, Config cfg)
        {
            if (!cfg.AlertsEnabled) return;
            DateTime now = s.Time;
            TimeSpan sustain = TimeSpan.FromSeconds(Math.Max(0, cfg.AlertSustainSec));
            CultureInfo ic = CultureInfo.InvariantCulture;

            Check("cpu", cfg.AlertCpu > 0 && s.CpuPercent >= cfg.AlertCpu, now, sustain, Cooldown,
                  "High CPU usage", string.Format(ic, "CPU has been at {0:0}% or more for {1}.", cfg.AlertCpu, Duration(sustain)));
            Check("ram", cfg.AlertRam > 0 && s.RamPercent >= cfg.AlertRam, now, sustain, Cooldown,
                  "Memory nearly full", string.Format(ic, "RAM is {0:0}% used ({1:0.0} of {2:0.0} GB).", s.RamPercent, s.RamUsedGB, s.RamTotalGB));
            Check("gpu", cfg.AlertGpu > 0 && s.GpuPercent >= cfg.AlertGpu, now, sustain, Cooldown,
                  "High GPU usage", string.Format(ic, "GPU has been at {0:0}% or more for {1}.", cfg.AlertGpu, Duration(sustain)));
            Check("gputemp", cfg.AlertGpuTemp > 0 && s.GpuTempC >= cfg.AlertGpuTemp, now, sustain, Cooldown,
                  "GPU running hot", string.Format(ic, "GPU temperature is {0} °C.", s.GpuTempC));
            Check("cputemp", cfg.AlertCpuTemp > 0 && s.CpuTempC >= cfg.AlertCpuTemp, now, sustain, Cooldown,
                  "CPU running hot", string.Format(ic, "CPU temperature is {0} °C.", s.CpuTempC));

            if (cfg.AlertDiskFree > 0)
            {
                foreach (VolumeReading v in s.Volumes)
                {
                    double freePct = v.TotalGB > 0 ? v.FreeGB * 100.0 / v.TotalGB : 100;
                    Check("disk:" + v.Name, freePct < cfg.AlertDiskFree, now, TimeSpan.Zero, DiskCooldown,
                          "Drive " + v.Name + " almost full",
                          string.Format(ic, "Only {0:0.0} GB ({1:0}%) free of {2:0} GB.", v.FreeGB, freePct, v.TotalGB));
                }
            }
        }

        private void Check(string key, bool breached, DateTime now, TimeSpan sustain, TimeSpan cooldown, string title, string message)
        {
            Rule r;
            if (!_rules.TryGetValue(key, out r))
            {
                r = new Rule { Key = key };
                _rules[key] = r;
            }

            if (!breached)
            {
                r.Since = DateTime.MinValue;
                return;
            }
            if (r.Since == DateTime.MinValue) r.Since = now;
            if (now - r.Since < sustain || now - r.LastFired < cooldown) return;

            r.LastFired = now;
            DiagLog.Write("alert: " + title + " - " + message);
            Action<string, string> handler = Fired;
            if (handler != null) handler(title, message);
        }

        private static string Duration(TimeSpan t)
        {
            return t.TotalSeconds < 90
                ? ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds"
                : ((int)Math.Round(t.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " minutes";
        }
    }

    /// <summary>Optional CSV log of readings: one file per day, old files pruned.</summary>
    internal sealed class HistoryLog
    {
        private DateTime _lastWrite = DateTime.MinValue;
        private DateTime _lastPrune = DateTime.MinValue;

        public static string Folder { get { return Path.Combine(Config.Dir, "logs"); } }

        private const string Header =
            "time,cpu_pct,cpu_ghz,cpu_temp_c,ram_pct,ram_used_gb,gpu_pct,vram_used_gb,gpu_temp_c,gpu_power_w," +
            "disk_pct,disk_mb_s,net_down_mbps,net_up_mbps,fps,ping_ms";

        public void Write(Snapshot s, Config cfg)
        {
            if (!cfg.LogEnabled) return;
            if ((s.Time - _lastWrite).TotalSeconds < Math.Max(1, cfg.LogIntervalSec) - 0.05) return;
            _lastWrite = s.Time;

            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, s.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                CultureInfo ic = CultureInfo.InvariantCulture;
                var line = new StringBuilder();
                if (!File.Exists(path)) line.AppendLine(Header);
                line.AppendFormat(ic, "{0:yyyy-MM-dd HH:mm:ss},{1:0.0},{2:0.00},{3},{4:0.0},{5:0.00},{6:0.0},{7:0.00},{8},{9},{10:0.0},{11:0.00},{12:0.00},{13:0.00},{14},{15}",
                    s.Time, s.CpuPercent, s.CpuGhz, Blank(s.CpuTempC), s.RamPercent, s.RamUsedGB,
                    s.GpuPercent, s.VramUsedGB, Blank(s.GpuTempC),
                    s.GpuPowerW >= 0 ? s.GpuPowerW.ToString("0.0", ic) : "",
                    s.DiskPercent, s.DiskBytesPerSec / 1048576.0, s.NetRecvBitsPerSec / 1e6, s.NetSentBitsPerSec / 1e6,
                    s.Fps >= 0 ? s.Fps.ToString("0", ic) : "", Blank(s.PingMs));
                line.AppendLine();
                File.AppendAllText(path, line.ToString());
            }
            catch { }

            if ((s.Time - _lastPrune).TotalHours >= 6)
            {
                _lastPrune = s.Time;
                Prune(cfg.LogKeepDays);
            }
        }

        private static string Blank(int v)
        {
            return v >= 0 ? v.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static void Prune(int keepDays)
        {
            try
            {
                DateTime cutoff = DateTime.Today.AddDays(-Math.Max(1, keepDays));
                foreach (string f in Directory.GetFiles(Folder, "*.csv"))
                {
                    DateTime day;
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd",
                                               CultureInfo.InvariantCulture, DateTimeStyles.None, out day) && day < cutoff)
                        File.Delete(f);
                }
            }
            catch { }
        }
    }

    /// <summary>Decides whether the strip should be visible given the foreground app.</summary>
    internal static class VisibilityGuard
    {
        private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        /// <summary>True when the foreground window covers its whole monitor (games, video, slideshows).</summary>
        public static bool IsFullscreenAppInFront(IntPtr ownWindow1, IntPtr ownWindow2)
        {
            try
            {
                int state;
                if (Win32.SHQueryUserNotificationState(out state) == 0 && (state == 3 || state == 4))
                    return true;   // exclusive Direct3D fullscreen, or presentation mode

                IntPtr fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == ownWindow1 || fg == ownWindow2) return false;

                var cls = new StringBuilder(64);
                Win32.GetClassNameW(fg, cls, cls.Capacity);
                if (Array.IndexOf(ShellClasses, cls.ToString()) >= 0) return false;

                Win32.RECT r;
                if (!Win32.GetWindowRect(fg, out r)) return false;
                var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MONITORINFO)) };
                if (!Win32.GetMonitorInfoW(Win32.MonitorFromWindow(fg, 2), ref mi)) return false;

                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }
    }
}
