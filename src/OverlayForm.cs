using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
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
        private static readonly Color NetColor = Color.FromArgb(110, 190, 245);

        // Animation tuning (seconds).
        private const double NeedleTau = 0.16;      // needle easing; settles in ~0.6 s
        private const double IntroRise = 0.55;      // start-up sweep to full scale
        private const double FadeOutTau = 0.05;     // vanish quickly when hovered
        private const double FadeInTau = 0.16;
        private const double FadeInDelay = 0.35;    // wait after the mouse leaves before returning

        private sealed class Seg
        {
            public string Label;
            public int ValueChars;
            public int DetailChars;
            public bool IsRate;
            public string Value = "";
            public string Detail = "";
            public double Target;     // 0-100
            public double Anim;       // 0-100, eased toward Target every frame
            public int ColW;
            public int SegW;
        }

        private readonly Config _cfg;
        private readonly Sampler _sampler;
        private readonly Timer _sampleTimer;
        private readonly Timer _frameTimer;
        private readonly NotifyIcon _tray;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Seg> _segs;

        private Font _big, _small;
        private int _cwBig, _cwSmall, _hBig, _hSmall, _lineGap, _gauge, _padX, _padY, _gapG, _sepW, _radius;
        private float _thick;

        private double _lastFrame;
        private bool _hasSample;
        private bool _dirty = true;
        private double _fade = 1.0;              // 1 = shown, 0 = fully transparent
        private double _lastInside = -10;
        private int _alphaByte = -1;
        private double _topmostAt;

        private IntPtr _trayIconHandle = IntPtr.Zero;
        private int _trayCpu = -1;

        /// <param name="live">false builds a static instance for rendering previews (no tray, no timers).</param>
        public OverlayForm(Config cfg, double baseMhz, bool live)
        {
            _cfg = cfg;
            _sampler = new Sampler(baseMhz);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = BgBottom;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Text = Installer.AppName;

            _segs = new List<Seg>
            {
                new Seg { Label = "CPU", ValueChars = 4, DetailChars = 7 },
                new Seg { Label = "RAM", ValueChars = 4, DetailChars = 12 },
                new Seg { Label = "GPU", ValueChars = 4, DetailChars = 16 },
                new Seg { Label = "DSK", ValueChars = 4, DetailChars = 8 },
                new Seg { Label = "NET", ValueChars = 7, DetailChars = 11, IsRate = true }
            };

            BuildLayout();
            if (!live) return;

            _tray = BuildTray();
            SetTrayGauge(0, Installer.AppName);
            Reposition();

            _sampleTimer = new Timer { Interval = Math.Max(250, _cfg.IntervalMs) };
            _sampleTimer.Tick += OnSample;
            _sampleTimer.Start();

            _frameTimer = new Timer { Interval = 16 };
            _frameTimer.Tick += OnFrame;
            _frameTimer.Start();

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

        // ---------- layout ----------

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

            if (_big != null) _big.Dispose();
            if (_small != null) _small.Dispose();
            _big = MakeMonoFont((float)Math.Max(9.0, 15.0 * s), FontStyle.Bold);
            _small = MakeMonoFont((float)Math.Max(8.0, 12.0 * s), FontStyle.Regular);

            MeasureMono(_big, out _cwBig, out _hBig);
            MeasureMono(_small, out _cwSmall, out _hSmall);

            _lineGap = (int)Math.Round(1 * s);
            _gauge = _hBig + _lineGap + _hSmall + (int)Math.Round(6 * s);
            _thick = (float)Math.Max(2.0, _gauge * 0.105);
            _padX = (int)Math.Round(12 * s);
            _padY = (int)Math.Round(5 * s);
            _gapG = (int)Math.Round(7 * s);
            _sepW = (int)Math.Round(18 * s);
            _radius = (int)Math.Round(10 * s);

            int width = _padX * 2;
            for (int i = 0; i < _segs.Count; i++)
            {
                Seg seg = _segs[i];
                seg.ColW = Math.Max((seg.Label.Length + 1 + seg.ValueChars) * _cwBig, seg.DetailChars * _cwSmall);
                seg.SegW = _gauge + _gapG + seg.ColW;
                width += seg.SegW + (i < _segs.Count - 1 ? _sepW : 0);
            }

            Size = new Size(width, _padY * 2 + _gauge);
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

        /// <summary>Pins the overlay to the top centre of the target monitor.</summary>
        private void Reposition()
        {
            Screen scr = TargetScreen();
            int x = scr.Bounds.Left + (scr.Bounds.Width - Width) / 2;
            int y = scr.Bounds.Top + _cfg.YOffset;
            if (Location.X != x || Location.Y != y) Location = new Point(x, y);
        }

        // ---------- sampling (once per interval) ----------

        private void OnSample(object sender, EventArgs e)
        {
            try
            {
                Snapshot s = _sampler.Sample();
                Fill(s);
                _hasSample = true;
                _dirty = true;

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
            catch { /* an overlay must never die on a bad sample */ }
        }

        private void Fill(Snapshot s)
        {
            _segs[0].Target = s.CpuPercent;
            _segs[0].Detail = s.CpuGhz > 0 ? F(s.CpuGhz, "0.00") + "GHz" : "";

            _segs[1].Target = s.RamPercent;
            _segs[1].Detail = s.RamTotalGB > 0 ? F(s.RamUsedGB, "0.0") + "/" + F(s.RamTotalGB, "0.0") + " GB" : "";

            _segs[2].Target = s.GpuPercent;
            string gpu = s.VramTotalGB > 0
                ? F(s.VramUsedGB, "0.0") + "/" + F(s.VramTotalGB, "0.0") + "G"
                : (s.VramUsedGB > 0 ? F(s.VramUsedGB, "0.0") + "G" : "");
            if (s.GpuTempC >= 0) gpu += " " + s.GpuTempC.ToString(CultureInfo.InvariantCulture) + "°C";
            _segs[2].Detail = gpu;

            _segs[3].Target = s.DiskPercent;
            _segs[3].Detail = ByteRate(s.DiskBytesPerSec);

            // Network has no natural 100%: the dial is log-scaled so 1 Mb/s and 1 Gb/s both read.
            double mbps = s.NetBitsPerSec / 1e6;
            _segs[4].Target = Math.Min(100.0, 100.0 * Math.Log10(1 + mbps) / Math.Log10(1 + 1000));
            _segs[4].Value = BitRate(s.NetBitsPerSec);
            _segs[4].Detail = "↓" + ShortBits(s.NetRecvBitsPerSec) + " ↑" + ShortBits(s.NetSentBitsPerSec);
        }

        private static string F(double v, string fmt)
        {
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        private static string ByteRate(double bps)
        {
            if (bps >= 1000.0 * 1024 * 1024) return F(bps / (1024.0 * 1024 * 1024), "0.0") + "GB/s";
            if (bps >= 1024 * 1024) return F(bps / (1024.0 * 1024), "0") + "MB/s";
            return F(bps / 1024.0, "0") + "KB/s";
        }

        private static string BitRate(double bps)
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

                UpdateHoverFade(now, dt);

                // Start-up: needles sweep to full scale, then settle on the first reading.
                bool intro = !_hasSample || now < IntroRise;
                double k = 1 - Math.Exp(-dt / NeedleTau);
                bool moving = false;
                foreach (Seg seg in _segs)
                {
                    double target = intro ? 100.0 : seg.Target;
                    double d = target - seg.Anim;
                    if (Math.Abs(d) > 0.05) { seg.Anim += d * k; moving = true; }
                    else seg.Anim = target;
                    if (!seg.IsRate && seg.Anim >= 90) moving = true;   // keep the red glow pulsing
                }

                if (_alphaByte > 0 && (moving || _dirty))
                {
                    _dirty = false;
                    Invalidate();
                }
            }
            catch { }
        }

        private void UpdateHoverFade(double now, double dt)
        {
            if (_cfg.HoverHide)
            {
                Win32.POINT p;
                if (GetCursorPos(out p))
                {
                    Rectangle r = Bounds;
                    r.Inflate(4, 4);
                    if (r.Contains(p.X, p.Y)) _lastInside = now;
                }
            }
            else
            {
                _lastInside = -10;
            }

            double target = now - _lastInside < FadeInDelay ? 0.0 : 1.0;
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
            int textH = _hBig + _lineGap + _hSmall;
            int y1 = _padY + (_gauge - textH) / 2;
            int y2 = y1 + _hBig + _lineGap;
            int x = _padX;

            for (int i = 0; i < _segs.Count; i++)
            {
                Seg seg = _segs[i];
                Color color = seg.IsRate ? NetColor : GaugeArt.LoadColor(seg.Anim);
                float glow = !seg.IsRate && seg.Anim >= 90
                    ? (float)(0.55 + 0.45 * Math.Sin(t * 2 * Math.PI * 1.1))
                    : 0f;

                GaugeArt.DrawGauge(g, new RectangleF(x, _padY, _gauge, _gauge), seg.Anim / 100.0,
                                   color, TrackColor, _thick, glow, true, false);

                int cx = x + _gauge + _gapG;
                DrawText(g, seg.Label, _big, cx, y1, LabelColor);

                string value = !_hasSample ? "--"
                             : seg.IsRate ? seg.Value
                             : ((int)Math.Round(seg.Anim)).ToString(CultureInfo.InvariantCulture) + "%";
                DrawText(g, value, _big, cx + seg.ColW - value.Length * _cwBig, y1, color);
                DrawText(g, seg.Detail, _small, cx, y2, DetailColor);

                x += seg.SegW;
                if (i < _segs.Count - 1)
                {
                    int sx = x + _sepW / 2;
                    using (var sp = new Pen(SepColor))
                        g.DrawLine(sp, sx, _padY + 4, sx, Height - _padY - 4);
                    x += _sepW;
                }
            }
        }

        private static void DrawText(Graphics g, string text, Font font, int x, int y, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, new Point(x, y), color,
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                  TextFormatFlags.PreserveGraphicsTranslateTransform);
        }

        /// <summary>Renders the overlay with live readings onto a backdrop, at 2x, as a PNG.</summary>
        public static void SavePreview(string path, double baseMhz)
        {
            var cfg = new Config { Scale = 2.0 };
            using (var form = new OverlayForm(cfg, baseMhz, false))
            {
                System.Threading.Thread.Sleep(1000);
                form.Fill(form._sampler.Sample());
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

        public void ShowWelcome()
        {
            if (_tray == null) return;
            _tray.ShowBalloonTip(8000, "MachineGauges is running",
                "Your gauges are at the top of the screen. Hover over them to see through, " +
                "and right-click this icon for settings.", ToolTipIcon.Info);
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

        private NotifyIcon BuildTray()
        {
            var menu = new ContextMenuStrip();

            menu.Items.Add(new ToolStripMenuItem(Installer.AppName + " " + Installer.VersionText(Installer.CurrentVersion))
            {
                Enabled = false
            });
            menu.Items.Add(new ToolStripSeparator());

            var hover = new ToolStripMenuItem("Fade out when mouse is over it") { Checked = _cfg.HoverHide };
            hover.Click += delegate
            {
                _cfg.HoverHide = !_cfg.HoverHide;
                hover.Checked = _cfg.HoverHide;
                _cfg.Save();
            };
            menu.Items.Add(hover);

            var opacity = new ToolStripMenuItem("Opacity");
            foreach (int p in new[] { 50, 65, 80, 90, 95, 100 })
            {
                int pct = p;
                var mi = new ToolStripMenuItem(pct + "%");
                mi.Click += delegate
                {
                    _cfg.Opacity = pct / 100.0;
                    _cfg.Save();
                    ApplyAlpha();
                    MarkChecked(opacity, pct + "%");
                };
                opacity.DropDownItems.Add(mi);
            }
            MarkChecked(opacity, (int)Math.Round(_cfg.Opacity * 100) + "%");
            menu.Items.Add(opacity);

            var size = new ToolStripMenuItem("Size");
            foreach (int p in new[] { 85, 100, 115, 130, 150 })
            {
                int pct = p;
                var mi = new ToolStripMenuItem(pct + "%");
                mi.Click += delegate
                {
                    _cfg.Scale = pct / 100.0;
                    _cfg.Save();
                    BuildLayout();
                    Reposition();
                    MarkChecked(size, pct + "%");
                };
                size.DropDownItems.Add(mi);
            }
            MarkChecked(size, (int)Math.Round(_cfg.Scale * 100) + "%");
            menu.Items.Add(size);

            var nudgeDown = new ToolStripMenuItem("Move down 4 px");
            nudgeDown.Click += delegate { _cfg.YOffset += 4; _cfg.Save(); Reposition(); };
            menu.Items.Add(nudgeDown);

            var nudgeUp = new ToolStripMenuItem("Move up 4 px");
            nudgeUp.Click += delegate { _cfg.YOffset = Math.Max(0, _cfg.YOffset - 4); _cfg.Save(); Reposition(); };
            menu.Items.Add(nudgeUp);

            if (Screen.AllScreens.Length > 1)
            {
                var mon = new ToolStripMenuItem("Monitor");
                Screen[] all = Screen.AllScreens;
                for (int i = 0; i < all.Length; i++)
                {
                    int idx = i;
                    string label = (i + 1) + ": " + all[i].Bounds.Width + "x" + all[i].Bounds.Height +
                                   (all[i].Primary ? " (primary)" : "");
                    var mi = new ToolStripMenuItem(label);
                    mi.Click += delegate
                    {
                        _cfg.Monitor = idx;
                        _cfg.Save();
                        BuildLayout();
                        Reposition();
                        MarkChecked(mon, label);
                    };
                    mon.DropDownItems.Add(mi);
                }
                menu.Items.Add(mon);
            }

            menu.Items.Add(new ToolStripSeparator());

            var startup = new ToolStripMenuItem("Start with Windows") { Checked = Installer.IsStartupEnabled() };
            startup.Click += delegate
            {
                Installer.SetStartup(!Installer.IsStartupEnabled(), Installer.CurrentExe);
                startup.Checked = Installer.IsStartupEnabled();
            };
            menu.Opening += delegate { startup.Checked = Installer.IsStartupEnabled(); };
            menu.Items.Add(startup);

            var project = new ToolStripMenuItem("Project page on GitHub");
            project.Click += delegate { Installer.OpenProjectPage(); };
            menu.Items.Add(project);

            if (Installer.IsRunningInstalledCopy())
            {
                var uninstall = new ToolStripMenuItem("Uninstall...");
                uninstall.Click += delegate { Installer.Launch(Installer.CurrentExe, "--uninstall"); };
                menu.Items.Add(uninstall);
            }

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            return new NotifyIcon { Text = Installer.AppName, Visible = true, ContextMenuStrip = menu };
        }

        private static void MarkChecked(ToolStripMenuItem parent, string text)
        {
            foreach (ToolStripItem item in parent.DropDownItems)
            {
                var mi = item as ToolStripMenuItem;
                if (mi != null) mi.Checked = mi.Text == text;
            }
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
                if (_sampleTimer != null) _sampleTimer.Stop();
                if (_frameTimer != null) _frameTimer.Stop();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
                _sampler.Dispose();
                if (_big != null) _big.Dispose();
                if (_small != null) _small.Dispose();
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}
