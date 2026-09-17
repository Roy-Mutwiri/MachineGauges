using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MachineGauges
{
    internal sealed class OverlayForm : Form
    {
        // Layered + transparent makes the overlay click-through, so it can never be
        // dragged, focused, alt-tabbed or shown in the taskbar.
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint LWA_ALPHA = 0x2;
        private const int HotkeyId = 1;

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32.POINT pt);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly Color BgTop = Color.FromArgb(24, 26, 32);
        private static readonly Color BgBottom = Color.FromArgb(11, 12, 15);
        private static readonly Color BorderColor = Color.FromArgb(48, 51, 60);
        private static readonly Color LabelColor = Color.FromArgb(132, 138, 150);
        private static readonly Color DetailColor = Color.FromArgb(150, 156, 168);
        private static readonly Color TrackColor = Color.FromArgb(44, 47, 56);
        private static readonly Color SepColor = Color.FromArgb(40, 42, 50);

        // Animation tuning (seconds).
        private const double NeedleTau = 0.16;      // needle easing; settles in ~0.6 s
        private const double IntroRise = 0.55;      // start-up sweep to full scale
        private const double FadeOutTau = 0.05;     // vanish quickly when hovered
        private const double FadeInTau = 0.16;
        private const double FadeInDelay = 0.35;    // wait after the mouse leaves before returning

        private sealed class Seg
        {
            public string Id;
            public string Label;
            public bool HasGauge = true;
            public bool LoadColored = true;     // colour follows the load ramp; otherwise the accent
            public bool Inverted;               // battery: low is bad
            public int ValueChars;
            public int DetailChars;
            public string Value = "";
            public string Detail = "";
            public double Target;               // 0-100
            public double Anim;                 // eased toward Target every frame
            public Rectangle Bounds;
            public int ColW;
        }

        private Config _cfg;
        private readonly Sampler _sampler;
        private readonly Timer _sampleTimer;
        private readonly Timer _frameTimer;
        private readonly NotifyIcon _tray;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private List<Seg> _segs = new List<Seg>();

        private readonly History _history = new History();
        private readonly Alerts _alerts = new Alerts();
        private readonly HistoryLog _log = new HistoryLog();
        private readonly PingMonitor _ping = new PingMonitor();
        private readonly CpuTempReader _cpuTemp = new CpuTempReader();
        private readonly FpsMonitor _fps = new FpsMonitor();

        private Font _big, _small;
        private int _cwBig, _cwSmall, _hBig, _hSmall, _lineGap, _gauge, _padX, _padY, _gapG, _sepW, _radius;
        private float _thick;
        private double _scale = 1;

        private double _lastFrame;
        private bool _hasSample;
        private bool _dirty = true;
        private double _fade = 1.0;              // 1 = shown, 0 = fully transparent
        private double _lastInside = -10;
        private int _alphaByte = -1;
        private double _topmostAt;
        private double _visibilityAt = -1;
        private bool _allowedVisible = true;
        private DateTime _gameSince = DateTime.MinValue;
        private Snapshot _last;

        private IntPtr _trayIconHandle = IntPtr.Zero;
        private int _trayCpu = -1;

        private DetailPanel _panel;
        private SettingsForm _settings;
        private ToolStripMenuItem _updateItem;
        private UpdateInfo _update;
        private System.Threading.Timer _updateTimer;
        private string _notifiedUpdate;

        public bool HotkeyRegistered { get; private set; }

        /// <param name="live">false builds a static instance for rendering previews (no tray, no timers).</param>
        public OverlayForm(Config cfg, double baseMhz, bool live)
        {
            _cfg = cfg;
            _sampler = new Sampler(baseMhz);
            GaugeArt.SetTheme(_cfg.Theme);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BgBottom;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Text = Installer.AppName;

            BuildSegments();
            BuildLayout();
            if (!live) return;

            _sampler.Ping = _ping;
            _sampler.CpuTemp = _cpuTemp;
            _sampler.Fps = _fps;
            _sampler.PreferredGpu = _cfg.Gpu;
            ApplyMonitors();

            _alerts.Fired += (title, message) => BeginInvoke(new MethodInvoker(() =>
                _tray.ShowBalloonTip(10000, title, message, ToolTipIcon.Warning)));

            _tray = BuildTray();
            SetTrayGauge(0, Installer.AppName);
            Reposition();

            _sampleTimer = new Timer { Interval = Math.Max(250, _cfg.IntervalMs) };
            _sampleTimer.Tick += OnSample;
            _sampleTimer.Start();

            _frameTimer = new Timer { Interval = 16 };
            _frameTimer.Tick += OnFrame;
            _frameTimer.Start();

            // First update check a minute after start, then daily.
            _updateTimer = new System.Threading.Timer(delegate { BackgroundUpdateCheck(); }, null,
                                                      TimeSpan.FromSeconds(60), TimeSpan.FromHours(24));

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _alphaByte = -1;
            ApplyAlpha();
            RegisterHotkey();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleDetails();
                return;
            }
            base.WndProc(ref m);
        }

        // Window alpha is driven directly rather than through Form.Opacity, which drops
        // the layered style at 100% and can't reach a true 0.
        private void ApplyAlpha()
        {
            if (!IsHandleCreated) return;
            int a = (int)Math.Round(255 * _cfg.Opacity * _fade);
            a = a < 0 ? 0 : (a > 255 ? 255 : a);
            if (a == _alphaByte) return;
            if (_alphaByte == 0) _dirty = true;  // repaint with fresh values as it reappears
            _alphaByte = a;
            SetLayeredWindowAttributes(Handle, 0, (byte)a, LWA_ALPHA);
        }

        // ---------- configuration ----------

        public Config CurrentConfig { get { return _cfg; } }
        public Sampler Sampler { get { return _sampler; } }
        public FpsState FpsStatus { get { return _fps.State; } }
        public string CpuTempSource { get { return _cpuTemp.Source; } }
        public int CpuTempNow { get { return _cpuTemp.LatestC; } }

        /// <summary>Applies settings from the Settings window, and saves them.</summary>
        public void ApplyConfig(Config cfg)
        {
            _cfg = cfg;
            _cfg.Save();
            GaugeArt.SetTheme(_cfg.Theme);
            _sampler.PreferredGpu = _cfg.Gpu;
            if (_sampleTimer != null) _sampleTimer.Interval = Math.Max(250, _cfg.IntervalMs);

            BuildSegments();
            if (_last != null) Fill(_last);
            BuildLayout();
            Reposition();
            _alphaByte = -1;
            ApplyAlpha();
            RegisterHotkey();
            ApplyMonitors();
            _visibilityAt = -1;
            if (_hoverItem != null) _hoverItem.Checked = _cfg.HoverHide;
            Invalidate();
        }

        private void ApplyMonitors()
        {
            if (_cfg.Has("ping")) _ping.Start(_cfg.PingHost); else _ping.Stop();
            if (_cfg.CpuTempEnabled) _cpuTemp.Start(); else _cpuTemp.Stop();
            if (_cfg.FpsEnabled)
            {
                _fps.Start();
                if (_fps.State != FpsState.Running)
                    DiagLog.Write("fps: " + _fps.State + (_fps.Error != null ? " - " + _fps.Error : ""));
            }
            else
            {
                _fps.Stop();
            }
        }

        private void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            Win32.UnregisterHotKey(Handle, HotkeyId);
            HotkeyRegistered = false;

            uint mods, vk;
            if (!ParseHotkey(_cfg.Hotkey, out mods, out vk)) return;
            HotkeyRegistered = Win32.RegisterHotKey(Handle, HotkeyId, mods | Win32.MOD_NOREPEAT, vk);
            if (!HotkeyRegistered) DiagLog.Write("hotkey " + _cfg.Hotkey + " is already used by another app");
        }

        public static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(text) || text.Equals("Off", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string raw in text.Split('+'))
            {
                string p = raw.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_CONTROL;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_SHIFT;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_ALT;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= Win32.MOD_WIN;
                else
                {
                    Keys key;
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                    if (!Enum.TryParse(p, true, out key)) return false;
                    vk = (uint)key;
                }
            }
            return vk != 0 && mods != 0;
        }

        // ---------- segments & layout ----------

        private void BuildSegments()
        {
            var old = new Dictionary<string, Seg>();
            foreach (Seg s in _segs) old[s.Id] = s;

            var list = new List<Seg>();
            foreach (string id in _cfg.SegmentList())
            {
                Seg seg = MakeSegment(id);
                Seg prev;
                if (old.TryGetValue(id, out prev))
                {
                    seg.Anim = prev.Anim;
                    seg.Target = prev.Target;
                    seg.Value = prev.Value;
                    seg.Detail = prev.Detail;
                }
                list.Add(seg);
            }
            _segs = list;
        }

        private static Seg MakeSegment(string id)
        {
            switch (id)
            {
                case "cpu": return new Seg { Id = id, Label = "CPU", ValueChars = 4, DetailChars = 13 };
                case "ram": return new Seg { Id = id, Label = "RAM", ValueChars = 4, DetailChars = 12 };
                case "gpu": return new Seg { Id = id, Label = "GPU", ValueChars = 4, DetailChars = 16 };
                case "disk": return new Seg { Id = id, Label = "DSK", ValueChars = 4, DetailChars = 8 };
                case "net": return new Seg { Id = id, Label = "NET", ValueChars = 7, DetailChars = 11, LoadColored = false };
                case "fps": return new Seg { Id = id, Label = "FPS", ValueChars = 4, DetailChars = 10, LoadColored = false };
                case "ping": return new Seg { Id = id, Label = "PING", ValueChars = 6, DetailChars = 12 };
                case "battery": return new Seg { Id = id, Label = "BAT", ValueChars = 4, DetailChars = 11, Inverted = true };
                case "clock": return new Seg { Id = id, Label = "TIME", ValueChars = 5, DetailChars = 10, HasGauge = false, LoadColored = false };
                default: return new Seg { Id = "uptime", Label = "UP", ValueChars = 7, DetailChars = 6, HasGauge = false, LoadColored = false };
            }
        }

        private bool Compact { get { return _cfg.Style == "Compact"; } }
        private bool Vertical { get { return _cfg.Orientation == "Vertical"; } }

        private Screen TargetScreen()
        {
            Screen[] all = Screen.AllScreens;
            int i = _cfg.Monitor;
            return i >= 0 && i < all.Length ? all[i] : Screen.PrimaryScreen;
        }

        private void BuildLayout()
        {
            Screen scr = TargetScreen();
            uint dpi = Win32.GetDpiAt(scr.Bounds.Left + scr.Bounds.Width / 2, scr.Bounds.Top + 8);
            double s = _cfg.Scale * (dpi / 96.0);
            _scale = s;

            if (_big != null) _big.Dispose();
            if (_small != null) _small.Dispose();
            _big = MakeMonoFont((float)Math.Max(9.0, 15.0 * s), FontStyle.Bold);
            _small = MakeMonoFont((float)Math.Max(8.0, 12.0 * s), FontStyle.Regular);

            MeasureMono(_big, out _cwBig, out _hBig);
            MeasureMono(_small, out _cwSmall, out _hSmall);

            _lineGap = (int)Math.Round(1 * s);
            _gauge = _hBig + _lineGap + _hSmall + (int)Math.Round(6 * s);
            _thick = (float)Math.Max(2.0, _gauge * 0.105);
            _padX = (int)Math.Round((Compact ? 10 : 12) * s);
            _padY = (int)Math.Round((Compact ? 4 : 5) * s);
            _gapG = (int)Math.Round(7 * s);
            _sepW = (int)Math.Round((Compact ? 14 : 18) * s);
            _radius = (int)Math.Round((Compact ? 8 : 10) * s);
            int vGap = (int)Math.Round(8 * s);

            // Natural size of each segment.
            int maxW = 0, maxH = 0;
            foreach (Seg seg in _segs)
            {
                int w, h;
                if (Compact)
                {
                    seg.ColW = (seg.Label.Length + 1 + seg.ValueChars) * _cwBig;
                    w = seg.ColW;
                    h = _hBig;
                }
                else
                {
                    seg.ColW = Math.Max((seg.Label.Length + 1 + seg.ValueChars) * _cwBig, seg.DetailChars * _cwSmall);
                    w = (seg.HasGauge ? _gauge + _gapG : 0) + seg.ColW;
                    h = _gauge;
                }
                seg.Bounds = new Rectangle(0, 0, w, h);
                maxW = Math.Max(maxW, w);
                maxH = Math.Max(maxH, h);
            }

            int x = _padX, y = _padY;
            for (int i = 0; i < _segs.Count; i++)
            {
                Seg seg = _segs[i];
                if (Vertical)
                {
                    seg.Bounds = new Rectangle(_padX, y, maxW, seg.Bounds.Height);
                    y += seg.Bounds.Height + (i < _segs.Count - 1 ? vGap : 0);
                }
                else
                {
                    seg.Bounds = new Rectangle(x, _padY, seg.Bounds.Width, maxH);
                    x += seg.Bounds.Width + (i < _segs.Count - 1 ? _sepW : 0);
                }
            }

            Size = Vertical
                ? new Size(_padX * 2 + maxW, y + _padY)
                : new Size(x + _padX, _padY * 2 + maxH);

            using (GraphicsPath path = GaugeArt.Rounded(new RectangleF(0, 0, Width, Height), _radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
            _dirty = true;
        }

        private static void MeasureMono(Font f, out int charW, out int height)
        {
            Size probe = TextRenderer.MeasureText("0000000000", f, new Size(int.MaxValue, int.MaxValue),
                                                  TextFormatFlags.NoPadding);
            charW = Math.Max(1, (int)Math.Round(probe.Width / 10.0));
            height = probe.Height;
        }

        private static Font MakeMonoFont(float px, FontStyle style)
        {
            foreach (string name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
            {
                try
                {
                    var f = new Font(name, px, style, GraphicsUnit.Pixel);
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericMonospace, px, style, GraphicsUnit.Pixel);
        }

        /// <summary>Pins the strip to its configured spot on the target monitor.</summary>
        private void Reposition()
        {
            Screen scr = TargetScreen();
            Rectangle b = scr.Bounds, work = scr.WorkingArea;
            int margin = (int)Math.Round(8 * _scale);
            int x, y;

            string pos = _cfg.Position;
            if (pos.EndsWith("Left", StringComparison.Ordinal)) x = b.Left + margin;
            else if (pos.EndsWith("Right", StringComparison.Ordinal)) x = b.Right - Width - margin;
            else x = b.Left + (b.Width - Width) / 2;

            if (pos.StartsWith("Bottom", StringComparison.Ordinal)) y = work.Bottom - Height - margin - _cfg.YOffset;
            else y = b.Top + _cfg.YOffset;

            if (Location.X != x || Location.Y != y) Location = new Point(x, y);
        }

        // ---------- sampling (once per interval) ----------

        private void OnSample(object sender, EventArgs e)
        {
            try
            {
                _sampler.DetailRequested = _panel != null && _panel.Visible;
                Snapshot s = _sampler.Sample();
                _last = s;
                _history.Add(s);
                Fill(s);
                _hasSample = true;
                _dirty = true;
                UpdateGameState(s);

                _alerts.Evaluate(s, _cfg);
                _log.Write(s, _cfg);
                if (_panel != null && _panel.Visible) _panel.UpdateSnapshot(s);

                SetTrayGauge(s.CpuPercent, string.Format(CultureInfo.InvariantCulture,
                    "CPU {0:0}%  RAM {1:0}%  GPU {2:0}%", s.CpuPercent, s.RamPercent, s.GpuPercent));

                Reposition();
                double now = _clock.Elapsed.TotalSeconds;
                if (now - _topmostAt >= 5)
                {
                    // Other topmost windows can end up above the overlay; reclaim the top.
                    _topmostAt = now;
                    Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                                       Win32.SWP_NOACTIVATE | 0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write("sample error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void UpdateGameState(Snapshot s)
        {
            // A game = the foreground app is presenting frames, or keeps a GPU 3D engine busy.
            double gpu3d;
            bool busy = s.Fps > 0 ||
                        (s.ForegroundPid > 0 && s.Gpu3DByPid.TryGetValue(s.ForegroundPid, out gpu3d) && gpu3d >= 10);
            if (!busy) _gameSince = DateTime.MinValue;
            else if (_gameSince == DateTime.MinValue) _gameSince = s.Time;
        }

        private bool GameInFront
        {
            get { return _gameSince != DateTime.MinValue && (DateTime.Now - _gameSince).TotalSeconds >= 2; }
        }

        private void Fill(Snapshot s)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            foreach (Seg seg in _segs)
            {
                switch (seg.Id)
                {
                    case "cpu":
                        seg.Target = s.CpuPercent;
                        seg.Detail = (s.CpuGhz > 0 ? F(s.CpuGhz, "0.00") + "GHz" : "") +
                                     (s.CpuTempC >= 0 ? " " + s.CpuTempC.ToString(ic) + "°C" : "");
                        break;
                    case "ram":
                        seg.Target = s.RamPercent;
                        seg.Detail = s.RamTotalGB > 0 ? F(s.RamUsedGB, "0.0") + "/" + F(s.RamTotalGB, "0.0") + " GB" : "";
                        break;
                    case "gpu":
                        seg.Target = s.GpuPercent;
                        string gpu = s.VramTotalGB > 0
                            ? F(s.VramUsedGB, "0.0") + "/" + F(s.VramTotalGB, "0.0") + "G"
                            : (s.VramUsedGB > 0 ? F(s.VramUsedGB, "0.0") + "G" : "");
                        if (s.GpuTempC >= 0) gpu += " " + s.GpuTempC.ToString(ic) + "°C";
                        seg.Detail = gpu;
                        break;
                    case "disk":
                        seg.Target = s.DiskPercent;
                        seg.Detail = ByteRate(s.DiskBytesPerSec);
                        break;
                    case "net":
                        // No natural 100%: the dial is log-scaled so 1 Mb/s and 1 Gb/s both read.
                        double mbps = s.NetBitsPerSec / 1e6;
                        seg.Target = Math.Min(100.0, 100.0 * Math.Log10(1 + mbps) / Math.Log10(1 + 1000));
                        seg.Value = BitRate(s.NetBitsPerSec);
                        seg.Detail = "↓" + ShortBits(s.NetRecvBitsPerSec) + " ↑" + ShortBits(s.NetSentBitsPerSec);
                        break;
                    case "fps":
                        if (s.Fps > 0)
                        {
                            seg.Target = Math.Min(100.0, s.Fps * 100.0 / 240.0);
                            seg.Value = ((int)Math.Round(s.Fps)).ToString(ic);
                            seg.Detail = F(1000.0 / s.Fps, "0.0") + " ms";
                        }
                        else
                        {
                            seg.Target = 0;
                            seg.Value = "--";
                            seg.Detail = FpsDetail();
                        }
                        break;
                    case "ping":
                        if (s.PingMs >= 0)
                        {
                            seg.Target = Math.Min(100.0, s.PingMs / 2.0);   // 200 ms = full scale
                            seg.Value = s.PingMs.ToString(ic) + "ms";
                        }
                        else
                        {
                            seg.Target = 100;
                            seg.Value = "--";
                        }
                        seg.Detail = Truncate(_cfg.PingHost, 7) + " " + s.PingLossPct.ToString(ic) + "%";
                        break;
                    case "battery":
                        if (s.BatteryPct >= 0)
                        {
                            seg.Target = s.BatteryPct;
                            seg.Value = s.BatteryPct.ToString(ic) + "%";
                            seg.Detail = s.BatteryCharging ? "charging"
                                       : s.BatteryMinutesLeft > 0 ? (s.BatteryMinutesLeft / 60) + "h" + (s.BatteryMinutesLeft % 60).ToString("00", ic) + "m left"
                                       : "on battery";
                        }
                        else
                        {
                            seg.Target = 0;
                            seg.Value = "--";
                            seg.Detail = "no battery";
                        }
                        break;
                    case "clock":
                        seg.Value = s.Time.ToString("HH:mm", ic);
                        seg.Detail = s.Time.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                        break;
                    case "uptime":
                        TimeSpan u = s.Uptime;
                        seg.Value = u.TotalDays >= 1
                            ? ((int)u.TotalDays).ToString(ic) + "d " + u.Hours.ToString("00", ic) + "h"
                            : u.Hours.ToString(ic) + "h " + u.Minutes.ToString("00", ic) + "m";
                        seg.Detail = "uptime";
                        break;
                }
            }
        }

        private string FpsDetail()
        {
            if (!_cfg.FpsEnabled) return "turned off";
            switch (_fps.State)
            {
                case FpsState.NeedsPermission: return "needs setup";
                case FpsState.Failed: return "unavailable";
                case FpsState.Running: return "no game";
                default: return "off";
            }
        }

        private static string Truncate(string s, int max)
        {
            s = s ?? "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static string F(double v, string fmt)
        {
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        internal static string ByteRate(double bps)
        {
            if (bps >= 1000.0 * 1024 * 1024) return F(bps / (1024.0 * 1024 * 1024), "0.0") + "GB/s";
            if (bps >= 1024 * 1024) return F(bps / (1024.0 * 1024), "0") + "MB/s";
            return F(bps / 1024.0, "0") + "KB/s";
        }

        internal static string BitRate(double bps)
        {
            if (bps >= 1e9) return F(bps / 1e9, "0.0") + "Gb/s";
            if (bps >= 1e6) return F(bps / 1e6, "0") + "Mb/s";
            return F(bps / 1e3, "0") + "Kb/s";
        }

        private static string ShortBits(double bps)
        {
            if (bps >= 1e9) return F(bps / 1e9, "0.0") + "G";
            if (bps >= 1e7) return F(bps / 1e6, "0") + "M";
            if (bps >= 1e6) return F(bps / 1e6, "0.0") + "M";
            return F(bps / 1e3, "0") + "K";
        }

        // ---------- animation (every frame) ----------

        private void OnFrame(object sender, EventArgs e)
        {
            try
            {
                double now = _clock.Elapsed.TotalSeconds;
                double dt = Math.Min(0.1, Math.Max(0, now - _lastFrame));
                _lastFrame = now;

                UpdateVisibility(now);
                UpdateHoverFade(now, dt);

                // Start-up: needles sweep to full scale, then settle on the first reading.
                bool intro = !_hasSample || now < IntroRise;
                double k = 1 - Math.Exp(-dt / NeedleTau);
                bool moving = false;
                foreach (Seg seg in _segs)
                {
                    if (!seg.HasGauge) continue;
                    double target = intro ? 100.0 : seg.Target;
                    double d = target - seg.Anim;
                    if (Math.Abs(d) > 0.05) { seg.Anim += d * k; moving = true; }
                    else seg.Anim = target;
                    if (Glows(seg)) moving = true;   // keep the red glow pulsing
                }

                if (_alphaByte > 0 && (moving || _dirty))
                {
                    _dirty = false;
                    Invalidate();
                }
            }
            catch { }
        }

        private bool Glows(Seg seg)
        {
            return seg.HasGauge && seg.LoadColored && !Compact && LoadOf(seg) >= 90;
        }

        private static double LoadOf(Seg seg)
        {
            return seg.Inverted ? 100 - seg.Anim : seg.Anim;
        }

        private void UpdateVisibility(double now)
        {
            if (now - _visibilityAt < 0.25) return;
            _visibilityAt = now;
            IntPtr panel = _panel != null && _panel.IsHandleCreated ? _panel.Handle : IntPtr.Zero;
            switch (_cfg.Visibility)
            {
                case "HideFullscreen":
                    _allowedVisible = !VisibilityGuard.IsFullscreenAppInFront(Handle, panel);
                    break;
                case "GamesOnly":
                    _allowedVisible = GameInFront;
                    break;
                default:
                    _allowedVisible = true;
                    break;
            }
        }

        private void UpdateHoverFade(double now, double dt)
        {
            bool inside = false;
            if (_cfg.HoverHide)
            {
                Win32.POINT p;
                if (GetCursorPos(out p))
                {
                    Rectangle r = Bounds;
                    r.Inflate(4, 4);
                    inside = r.Contains(p.X, p.Y);
                }
            }
            if (inside) _lastInside = now;

            double target = !_allowedVisible || now - _lastInside < FadeInDelay ? 0.0 : 1.0;
            double tau = target < _fade ? FadeOutTau : FadeInTau;
            _fade += (target - _fade) * (1 - Math.Exp(-dt / tau));
            if (Math.Abs(target - _fade) < 0.01) _fade = target;
            ApplyAlpha();
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            try { BuildLayout(); Reposition(); Invalidate(); }
            catch { }
        }

        // ---------- painting ----------

        protected override void OnPaintBackground(PaintEventArgs e) { /* handled in OnPaint */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintTo(e.Graphics);
        }

        private void PaintTo(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = GaugeArt.Rounded(rect, _radius))
            using (var bg = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), BgTop, BgBottom,
                                                   LinearGradientMode.Vertical))
            using (var pen = new Pen(BorderColor))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }

            double t = _clock.Elapsed.TotalSeconds;
            for (int i = 0; i < _segs.Count; i++)
            {
                Seg seg = _segs[i];
                if (Compact) PaintCompact(g, seg);
                else PaintGauge(g, seg, t);

                if (i < _segs.Count - 1)
                {
                    using (var sp = new Pen(SepColor))
                    {
                        if (Vertical)
                        {
                            int sy = seg.Bounds.Bottom + (int)Math.Round(4 * _scale);
                            g.DrawLine(sp, _padX, sy, Width - _padX, sy);
                        }
                        else
                        {
                            int sx = seg.Bounds.Right + _sepW / 2;
                            g.DrawLine(sp, sx, _padY + 4, sx, Height - _padY - 4);
                        }
                    }
                }
            }
        }

        private Color ColorOf(Seg seg)
        {
            if (seg.Value == "--" && (seg.Id == "fps" || seg.Id == "battery")) return LabelColor;   // no data, not a warning
            return seg.LoadColored ? GaugeArt.LoadColor(LoadOf(seg)) : GaugeArt.Accent;
        }

        private string ValueText(Seg seg)
        {
            if (!_hasSample) return "--";
            bool percent = seg.Id == "cpu" || seg.Id == "ram" || seg.Id == "gpu" || seg.Id == "disk";
            return percent ? ((int)Math.Round(seg.Anim)).ToString(CultureInfo.InvariantCulture) + "%" : seg.Value;
        }

        private void PaintGauge(Graphics g, Seg seg, double t)
        {
            Rectangle b = seg.Bounds;
            Color color = ColorOf(seg);
            int cx = b.X;

            if (seg.HasGauge)
            {
                float glow = Glows(seg) ? (float)(0.55 + 0.45 * Math.Sin(t * 2 * Math.PI * 1.1)) : 0f;
                GaugeArt.DrawGauge(g, new RectangleF(b.X, b.Y + (b.Height - _gauge) / 2, _gauge, _gauge),
                                   seg.Anim / 100.0, color, TrackColor, _thick, glow, true, false);
                cx += _gauge + _gapG;
            }

            int colW = b.Right - cx;
            int textH = _hBig + _lineGap + _hSmall;
            int y1 = b.Y + (b.Height - textH) / 2;
            int y2 = y1 + _hBig + _lineGap;

            DrawText(g, seg.Label, _big, cx, y1, LabelColor);
            string value = ValueText(seg);
            DrawText(g, value, _big, cx + colW - value.Length * _cwBig, y1, color);
            DrawText(g, seg.Detail, _small, cx, y2, DetailColor);
        }

        private void PaintCompact(Graphics g, Seg seg)
        {
            Rectangle b = seg.Bounds;
            int y = b.Y + (b.Height - _hBig) / 2;
            DrawText(g, seg.Label, _big, b.X, y, LabelColor);
            string value = ValueText(seg);
            DrawText(g, value, _big, b.Right - value.Length * _cwBig, y, ColorOf(seg));
        }

        private static void DrawText(Graphics g, string text, Font font, int x, int y, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, new Point(x, y), color,
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                  TextFormatFlags.PreserveGraphicsTranslateTransform);
        }

        /// <summary>
        /// Renders the strip with live readings onto a backdrop, at 2x, as a PNG. Settings come from
        /// config.ini, overridden by "key=value" arguments.
        /// </summary>
        public static void SavePreview(string path, double baseMhz, IEnumerable<string> overrides)
        {
            Config cfg = Config.Load();
            cfg.Scale = 2.0;
            cfg.YOffset = 0;
            foreach (string o in overrides)
            {
                int eq = o.IndexOf('=');
                if (eq > 0) cfg.Set(o.Substring(0, eq).Trim().ToLowerInvariant(), o.Substring(eq + 1).Trim());
            }

            using (var ping = new PingMonitor())
            using (var form = new OverlayForm(cfg, baseMhz, false))
            {
                if (cfg.Has("ping"))
                {
                    ping.Start(cfg.PingHost);
                    form._sampler.Ping = ping;
                }
                System.Threading.Thread.Sleep(2200);
                Snapshot s = form._sampler.Sample();
                form.Fill(s);
                form._hasSample = true;
                foreach (Seg seg in form._segs) seg.Anim = seg.Target;

                const int margin = 48;
                using (var bmp = new Bitmap(form.Width + margin * 2, form.Height + margin * 2, PixelFormat.Format32bppRgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    var all = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    using (var backdrop = new LinearGradientBrush(all, Color.FromArgb(62, 72, 96), Color.FromArgb(24, 27, 38),
                                                                 LinearGradientMode.ForwardDiagonal))
                        g.FillRectangle(backdrop, all);
                    g.TranslateTransform(margin, margin);
                    form.PaintTo(g);
                    bmp.Save(path, ImageFormat.Png);
                }
            }
        }

        // ---------- details panel & settings ----------

        public void ToggleDetails()
        {
            ToggleDetails(false);
        }

        /// <param name="showOnly">true opens (or re-focuses) the panel without ever closing it.</param>
        public void ToggleDetails(bool showOnly)
        {
            if (_panel == null || _panel.IsDisposed)
            {
                _panel = new DetailPanel(this, _history);
                _panel.VisibleChanged += delegate
                {
                    _sampler.DetailRequested = _panel.Visible;
                    if (_panel.Visible && _last != null) _panel.UpdateSnapshot(_last);
                };
            }

            if (_panel.Visible && !showOnly) _panel.Hide();
            else if (_panel.Visible) _panel.Activate();
            else _panel.ShowOn(TargetScreen(), Bounds);
        }

        public void ShowSettings()
        {
            if (_settings != null && !_settings.IsDisposed)
            {
                _settings.Activate();
                return;
            }
            _settings = new SettingsForm(this);
            _settings.Show();
            _settings.Activate();
        }

        public IList<string> GpuNames()
        {
            var names = new List<string>();
            if (_last != null)
                foreach (GpuReading g in _last.Gpus) names.Add(g.Name);
            return names;
        }

        // ---------- updates ----------

        private void BackgroundUpdateCheck()
        {
            if (!_cfg.UpdateCheck) return;
            try
            {
                UpdateInfo info = Updater.CheckForNewer();
                if (info != null) BeginInvoke(new MethodInvoker(() => OnUpdateFound(info, false)));
            }
            catch (Exception ex)
            {
                DiagLog.Write("update check failed: " + ex.Message);
            }
        }

        /// <summary>"Check for updates" from the menu or Settings: always reports the outcome.</summary>
        public void CheckForUpdatesInteractive()
        {
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    UpdateInfo info = Updater.CheckForNewer();
                    BeginInvoke(new MethodInvoker(() =>
                    {
                        if (info != null) OnUpdateFound(info, true);
                        else MessageBox.Show("You have the latest version of MachineGauges (" +
                                             Installer.VersionText(Installer.CurrentVersion) + ").",
                                             Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new MethodInvoker(() =>
                        MessageBox.Show("Couldn't check for updates:\n\n" + ex.Message, Installer.AppName,
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning)));
                }
            }) { IsBackground = true };
            worker.Start();
        }

        private void OnUpdateFound(UpdateInfo info, bool askNow)
        {
            _update = info;
            string v = Installer.VersionText(info.Version);
            _updateItem.Text = "Install update " + v + "...";
            _updateItem.Visible = true;
            DiagLog.Write("update available: " + v);

            if (askNow)
            {
                PromptUpdate();
            }
            else if (_notifiedUpdate != v)
            {
                _notifiedUpdate = v;
                _tray.ShowBalloonTip(10000, "MachineGauges " + v + " is available",
                    "Click here, or use the tray menu, to update.", ToolTipIcon.Info);
            }
        }

        private void PromptUpdate()
        {
            if (_update == null) return;
            UpdateInfo info = _update;
            DialogResult r = MessageBox.Show(
                "Update MachineGauges to version " + Installer.VersionText(info.Version) + "?\n\n" +
                "It downloads from GitHub, checks the file against its published checksum, and restarts " +
                "the gauges. Your settings are kept.",
                Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            var worker = new System.Threading.Thread(() =>
            {
                string error;
                try { error = Updater.DownloadAndInstall(info); }
                catch (Exception ex) { error = ex.Message; }
                if (error != null)
                {
                    BeginInvoke(new MethodInvoker(() =>
                        MessageBox.Show("The update couldn't be installed:\n\n" + error + "\n\nYou can download it from:\n" +
                                        info.PageUrl, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning)));
                }
            }) { IsBackground = true };
            worker.Start();
        }

        public void ShowWelcome()
        {
            if (_tray == null) return;
            _tray.ShowBalloonTip(8000, "MachineGauges is running",
                "Your gauges are at the top of the screen. Press " + _cfg.Hotkey + " for details, " +
                "and right-click this icon for settings.", ToolTipIcon.Info);
        }

        public void ShowUpdated()
        {
            if (_tray == null) return;
            _tray.ShowBalloonTip(8000, "MachineGauges updated to " + Installer.VersionText(Installer.CurrentVersion),
                "Press " + _cfg.Hotkey + " for the new details panel, or right-click this icon for settings.",
                ToolTipIcon.Info);
        }

        /// <summary>Asks Windows (via an elevation prompt) to let this user run the FPS counter.</summary>
        public void RequestFpsPermission()
        {
            DialogResult r = MessageBox.Show(
                "The FPS counter reads frame timing from Windows event tracing, which Windows only allows for " +
                "administrators and members of the \"Performance Log Users\" group.\n\n" +
                "Add your account to that group now? Windows will ask for administrator approval, and the change " +
                "takes effect after you sign out and back in.",
                Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            string account;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) account = id.Name;
            try
            {
                var psi = new ProcessStartInfo(Installer.CurrentExe, "--grant-fps \"" + account + "\"")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                        MessageBox.Show("Done. Sign out of Windows and back in, then the FPS counter will work.",
                                        Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user declined the elevation prompt.
            }
        }

        // ---------- tray ----------

        /// <summary>The tray icon is a live mini gauge of CPU load.</summary>
        private void SetTrayGauge(double cpuPercent, string tooltip)
        {
            _tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;

            int cpu = (int)Math.Round(cpuPercent);
            if (cpu == _trayCpu) return;
            _trayCpu = cpu;

            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            IntPtr handle;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    GaugeArt.DrawAppIcon(g, size, cpu / 100.0, false);
                handle = bmp.GetHicon();
            }

            Icon previous = _tray.Icon;
            IntPtr previousHandle = _trayIconHandle;
            _tray.Icon = Icon.FromHandle(handle);
            _trayIconHandle = handle;
            if (previous != null) previous.Dispose();
            if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
        }

        private ToolStripMenuItem _hoverItem;

        private NotifyIcon BuildTray()
        {
            var menu = new ContextMenuStrip();

            menu.Items.Add(new ToolStripMenuItem(Installer.AppName + " " + Installer.VersionText(Installer.CurrentVersion))
            {
                Enabled = false
            });
            menu.Items.Add(new ToolStripSeparator());

            var details = new ToolStripMenuItem("Show details") { Font = new Font(menu.Font, FontStyle.Bold) };
            details.Click += delegate { ToggleDetails(); };
            menu.Items.Add(details);

            var settings = new ToolStripMenuItem("Settings...");
            settings.Click += delegate { ShowSettings(); };
            menu.Items.Add(settings);

            _hoverItem = new ToolStripMenuItem("Fade out when mouse is over it") { Checked = _cfg.HoverHide };
            _hoverItem.Click += delegate
            {
                Config c = _cfg.Clone();
                c.HoverHide = !c.HoverHide;
                ApplyConfig(c);
            };
            menu.Items.Add(_hoverItem);

            var startup = new ToolStripMenuItem("Start with Windows");
            startup.Click += delegate
            {
                Installer.SetStartup(!Installer.IsStartupEnabled(), Installer.CurrentExe);
                startup.Checked = Installer.IsStartupEnabled();
            };
            menu.Items.Add(startup);

            var logs = new ToolStripMenuItem("Open history log folder");
            logs.Click += delegate
            {
                Directory.CreateDirectory(HistoryLog.Folder);
                Process.Start(new ProcessStartInfo(HistoryLog.Folder) { UseShellExecute = true });
            };
            menu.Items.Add(logs);

            menu.Items.Add(new ToolStripSeparator());

            _updateItem = new ToolStripMenuItem("Install update...") { Visible = false };
            _updateItem.Click += delegate { PromptUpdate(); };
            menu.Items.Add(_updateItem);

            var check = new ToolStripMenuItem("Check for updates");
            check.Click += delegate { CheckForUpdatesInteractive(); };
            menu.Items.Add(check);

            var project = new ToolStripMenuItem("Project page on GitHub");
            project.Click += delegate { Installer.OpenProjectPage(); };
            menu.Items.Add(project);

            ToolStripMenuItem uninstall = null;
            if (Installer.IsRunningInstalledCopy())
            {
                uninstall = new ToolStripMenuItem("Uninstall...");
                uninstall.Click += delegate { Installer.Launch(Installer.CurrentExe, "--uninstall"); };
                menu.Items.Add(uninstall);
            }

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            menu.Opening += delegate
            {
                startup.Checked = Installer.IsStartupEnabled();
                _hoverItem.Checked = _cfg.HoverHide;
                logs.Visible = _cfg.LogEnabled || Directory.Exists(HistoryLog.Folder);
                details.Text = "Show details" + (HotkeyRegistered ? "\t" + _cfg.Hotkey : "");
            };

            var tray = new NotifyIcon { Text = Installer.AppName, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleDetails(); };
            tray.BalloonTipClicked += delegate { if (_update != null) PromptUpdate(); };
            return tray;
        }

        protected override void Dispose(bool disposing)
        {
            // Preview instances are never closed, so release their sampler here.
            if (disposing && _tray == null) _sampler.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                if (IsHandleCreated) Win32.UnregisterHotKey(Handle, HotkeyId);
                if (_updateTimer != null) _updateTimer.Dispose();
                if (_sampleTimer != null) _sampleTimer.Stop();
                if (_frameTimer != null) _frameTimer.Stop();
                if (_panel != null && !_panel.IsDisposed) _panel.Close();
                if (_settings != null && !_settings.IsDisposed) _settings.Close();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
                _ping.Dispose();
                _cpuTemp.Dispose();
                _fps.Dispose();
                _sampler.Dispose();
                if (_big != null) _big.Dispose();
                if (_small != null) _small.Dispose();
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}
