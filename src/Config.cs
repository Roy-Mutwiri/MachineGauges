using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MachineGauges
{
    /// <summary>Tiny ini-backed settings store in %LOCALAPPDATA%\MachineGauges.</summary>
    internal sealed class Config
    {
        public double Opacity = 0.95;
        public double Scale = 1.00;
        public int YOffset = 0;
        public int Monitor = 0;
        public int IntervalMs = 1000;
        public bool HoverHide = true;

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MachineGauges");
            }
        }

        private static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

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
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "opacity": c.Opacity = ParseD(val, c.Opacity, 0.20, 1.00); break;
                        case "scale": c.Scale = ParseD(val, c.Scale, 0.60, 3.00); break;
                        case "yoffset": c.YOffset = (int)ParseD(val, c.YOffset, -2000, 2000); break;
                        case "monitor": c.Monitor = (int)ParseD(val, c.Monitor, 0, 32); break;
                        case "interval": c.IntervalMs = (int)ParseD(val, c.IntervalMs, 250, 10000); break;
                        case "hoverhide": c.HoverHide = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var lines = new List<string>
                {
                    "# MachineGauges settings",
                    "opacity=" + Opacity.ToString("0.00", CultureInfo.InvariantCulture),
                    "scale=" + Scale.ToString("0.00", CultureInfo.InvariantCulture),
                    "yoffset=" + YOffset.ToString(CultureInfo.InvariantCulture),
                    "monitor=" + Monitor.ToString(CultureInfo.InvariantCulture),
                    "interval=" + IntervalMs.ToString(CultureInfo.InvariantCulture),
                    "hoverhide=" + (HoverHide ? "1" : "0")
                };
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }

        private static double ParseD(string s, double fallback, double lo, double hi)
        {
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
