using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MachineGauges
{
    /// <summary>Ini-backed settings in %LOCALAPPDATA%\MachineGauges\config.ini.</summary>
    internal sealed class Config
    {
        public static readonly string[] AllSegments = { "cpu", "ram", "gpu", "disk", "net", "fps", "ping", "battery", "clock", "uptime" };
        public static readonly string[] Positions = { "TopCenter", "TopLeft", "TopRight", "BottomCenter", "BottomLeft", "BottomRight" };
        public static readonly string[] Orientations = { "Horizontal", "Vertical" };
        public static readonly string[] Styles = { "Gauges", "Compact" };
        public static readonly string[] Themes = { "Classic", "Ocean", "Violet", "Mono" };
        public static readonly string[] VisibilityModes = { "Always", "HideFullscreen", "GamesOnly" };

        // Appearance
        public double Opacity = 0.95;
        public double Scale = 1.00;
        public int YOffset = 0;                 // distance from the screen edge, in pixels
        public int Monitor = 0;
        public string Position = "TopCenter";
        public string Orientation = "Horizontal";
        public string Style = "Gauges";
        public string Theme = "Classic";

        // Gauges
        public string Segments = "cpu,ram,gpu,disk,net";
        public string Gpu = "";                 // GPU name for the strip; empty = automatic

        // Behaviour
        public int IntervalMs = 1000;
        public bool HoverHide = true;
        public bool StartupOff = false;         // the user turned "Start with Windows" off
        public string Visibility = "Always";
        public string Hotkey = "Ctrl+Shift+G";
        public string PingHost = "1.1.1.1";

        // Alerts (0 = off)
        public bool AlertsEnabled = true;
        public int AlertCpu = 0;
        public int AlertRam = 90;
        public int AlertGpu = 0;
        public int AlertGpuTemp = 85;
        public int AlertCpuTemp = 90;
        public int AlertDiskFree = 10;
        public int AlertSustainSec = 60;

        // Advanced
        public bool LogEnabled = false;
        public int LogIntervalSec = 5;
        public int LogKeepDays = 14;
        public bool UpdateCheck = true;
        public bool FpsEnabled = false;
        public bool CpuTempEnabled = true;

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MachineGauges");
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public Config Clone()
        {
            return (Config)MemberwiseClone();
        }

        /// <summary>Enabled strip segments in display order, unknown names dropped.</summary>
        public List<string> SegmentList()
        {
            var list = new List<string>();
            foreach (string raw in (Segments ?? "").Split(','))
            {
                string s = raw.Trim().ToLowerInvariant();
                if (Array.IndexOf(AllSegments, s) >= 0 && !list.Contains(s)) list.Add(s);
            }
            if (list.Count == 0) list.Add("cpu");
            return list;
        }

        public bool Has(string segment)
        {
            return SegmentList().Contains(segment);
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    c.Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
            return c;
        }

        /// <summary>Applies one ini key (as written by Save). Unknown keys are ignored.</summary>
        public void Set(string key, string val)
        {
            switch (key)
            {
                case "opacity": Opacity = D(val, Opacity, 0.20, 1.00); break;
                case "scale": Scale = D(val, Scale, 0.60, 3.00); break;
                case "yoffset": YOffset = I(val, YOffset, 0, 2000); break;
                case "monitor": Monitor = I(val, Monitor, 0, 32); break;
                case "position": Position = Pick(val, Positions, Position); break;
                case "orientation": Orientation = Pick(val, Orientations, Orientation); break;
                case "style": Style = Pick(val, Styles, Style); break;
                case "theme": Theme = Pick(val, Themes, Theme); break;
                case "segments": Segments = val; break;
                case "gpu": Gpu = val; break;
                case "interval": IntervalMs = I(val, IntervalMs, 250, 10000); break;
                case "hoverhide": HoverHide = B(val); break;
                case "startupoff": StartupOff = B(val); break;
                case "visibility": Visibility = Pick(val, VisibilityModes, Visibility); break;
                case "hotkey": Hotkey = val; break;
                case "pinghost": PingHost = val; break;
                case "alerts": AlertsEnabled = B(val); break;
                case "alertcpu": AlertCpu = I(val, AlertCpu, 0, 100); break;
                case "alertram": AlertRam = I(val, AlertRam, 0, 100); break;
                case "alertgpu": AlertGpu = I(val, AlertGpu, 0, 100); break;
                case "alertgputemp": AlertGpuTemp = I(val, AlertGpuTemp, 0, 120); break;
                case "alertcputemp": AlertCpuTemp = I(val, AlertCpuTemp, 0, 120); break;
                case "alertdiskfree": AlertDiskFree = I(val, AlertDiskFree, 0, 50); break;
                case "alertsustain": AlertSustainSec = I(val, AlertSustainSec, 0, 3600); break;
                case "log": LogEnabled = B(val); break;
                case "loginterval": LogIntervalSec = I(val, LogIntervalSec, 1, 3600); break;
                case "logdays": LogKeepDays = I(val, LogKeepDays, 1, 365); break;
                case "updates": UpdateCheck = B(val); break;
                case "fps": FpsEnabled = B(val); break;
                case "cputemp": CpuTempEnabled = B(val); break;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                CultureInfo ic = CultureInfo.InvariantCulture;
                var lines = new List<string>
                {
                    "# MachineGauges settings - edit here or from the tray menu > Settings",
                    "opacity=" + Opacity.ToString("0.00", ic),
                    "scale=" + Scale.ToString("0.00", ic),
                    "yoffset=" + YOffset.ToString(ic),
                    "monitor=" + Monitor.ToString(ic),
                    "position=" + Position,
                    "orientation=" + Orientation,
                    "style=" + Style,
                    "theme=" + Theme,
                    "segments=" + string.Join(",", SegmentList()),
                    "gpu=" + Gpu,
                    "interval=" + IntervalMs.ToString(ic),
                    "hoverhide=" + Bit(HoverHide),
                    "startupoff=" + Bit(StartupOff),
                    "visibility=" + Visibility,
                    "hotkey=" + Hotkey,
                    "pinghost=" + PingHost,
                    "alerts=" + Bit(AlertsEnabled),
                    "alertcpu=" + AlertCpu.ToString(ic),
                    "alertram=" + AlertRam.ToString(ic),
                    "alertgpu=" + AlertGpu.ToString(ic),
                    "alertgputemp=" + AlertGpuTemp.ToString(ic),
                    "alertcputemp=" + AlertCpuTemp.ToString(ic),
                    "alertdiskfree=" + AlertDiskFree.ToString(ic),
                    "alertsustain=" + AlertSustainSec.ToString(ic),
                    "log=" + Bit(LogEnabled),
                    "loginterval=" + LogIntervalSec.ToString(ic),
                    "logdays=" + LogKeepDays.ToString(ic),
                    "updates=" + Bit(UpdateCheck),
                    "fps=" + Bit(FpsEnabled),
                    "cputemp=" + Bit(CpuTempEnabled)
                };
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

        private static string Bit(bool b) { return b ? "1" : "0"; }

        private static bool B(string s)
        {
            return s != "0" && !s.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                   !s.Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        private static string Pick(string s, string[] allowed, string fallback)
        {
            foreach (string a in allowed)
                if (a.Equals(s, StringComparison.OrdinalIgnoreCase)) return a;
            return fallback;
        }

        private static double D(string s, double fallback, double lo, double hi)
        {
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static int I(string s, int fallback, int lo, int hi)
        {
            return (int)D(s, fallback, lo, hi);
        }
    }
}
