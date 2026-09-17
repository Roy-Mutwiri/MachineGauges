using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MachineGauges
{
    /// <summary>
    /// The details window (hotkey / tray click): five-minute history charts, per-core CPU,
    /// every GPU and disk, free space, and the top processes.
    /// </summary>
    internal sealed class DetailPanel : Form
    {
        private const int BaseW = 1040;
        private const int BaseH = 800;

        // Surfaces and ink
        private static readonly Color Surface = Color.FromArgb(22, 23, 28);
        private static readonly Color CardBg = Color.FromArgb(30, 32, 38);
        private static readonly Color CardBorder = Color.FromArgb(46, 49, 58);
        private static readonly Color Grid = Color.FromArgb(44, 47, 55);
        private static readonly Color Track = Color.FromArgb(44, 47, 56);
        private static readonly Color TextPrimary = Color.FromArgb(236, 238, 242);
        private static readonly Color TextSecondary = Color.FromArgb(172, 177, 188);
        private static readonly Color TextMuted = Color.FromArgb(122, 128, 140);
        private static readonly Color ChipOn = Color.FromArgb(58, 62, 74);
        private static readonly Color TooltipBg = Color.FromArgb(12, 13, 16);

        // Validated categorical pair on this dark surface (blue, orange).
        private static readonly Color Series1 = ColorTranslator.FromHtml("#3987e5");
        private static readonly Color Series2 = ColorTranslator.FromHtml("#d95926");

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private sealed class Hit
        {
            public Rectangle Rect;
            public Action Click;
            public string Tip;
        }

        private readonly OverlayForm _host;
        private readonly History _history;
        private Snapshot _snap;
        private double _s = 1;
        private Font _title, _h2, _body, _small, _mono, _monoSmall;
        private int _rangeSec = 60;
        private string _procTab = "cpu";
        private Point _mouse = new Point(-1, -1);
        private readonly List<Hit> _hits = new List<Hit>();
        private string _pendingTip;
        private Point _pendingTipAt;

        public DetailPanel(OverlayForm host, History history)
        {
            _host = host;
            _history = history;

            Text = "MachineGauges details";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;          // WS_EX_TOOLWINDOW: no taskbar button, no alt-tab
                cp.ClassStyle |= 0x00020000;       // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2;   // DWMWCP_ROUND (Windows 11)
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        public void ShowOn(Screen screen, Rectangle strip)
        {
            Rectangle work = screen.WorkingArea;
            _s = Win32.GetDpiAt(work.Left + work.Width / 2, work.Top + work.Height / 2) / 96.0;
            double fit = Math.Min(1.0, Math.Min((work.Width - 40) / (BaseW * _s),
                                                (work.Height - 40) / (BaseH * _s)));
            _s *= fit;
            BuildFonts();
            Size = new Size(S(BaseW), S(BaseH));

            int x = work.Left + (work.Width - Width) / 2;
            bool stripOnTop = strip.Top + strip.Height / 2 < work.Top + work.Height / 2;
            int y = stripOnTop ? strip.Bottom + S(12) : strip.Top - S(12) - Height;
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - Height));
            Location = new Point(x, y);

            Show();
            Activate();
        }

        public void UpdateSnapshot(Snapshot s)
        {
            _snap = s;
            Invalidate();
        }

        private int S(double v)
        {
            return (int)Math.Round(v * _s);
        }

        private void BuildFonts()
        {
            foreach (Font f in new[] { _title, _h2, _body, _small, _mono, _monoSmall })
                if (f != null) f.Dispose();
            _title = UiFont(17, FontStyle.Bold);
            _h2 = UiFont(13, FontStyle.Bold);
            _body = UiFont(13, FontStyle.Regular);
            _small = UiFont(11.5f, FontStyle.Regular);
            _mono = MonoFont(13, FontStyle.Bold);
            _monoSmall = MonoFont(11.5f, FontStyle.Regular);
        }

        private Font UiFont(float px, FontStyle style)
        {
            return new Font("Segoe UI", (float)(px * _s), style, GraphicsUnit.Pixel);
        }

        private Font MonoFont(float px, FontStyle style)
        {
            foreach (string name in new[] { "Cascadia Mono", "Consolas" })
            {
                var f = new Font(name, (float)(px * _s), style, GraphicsUnit.Pixel);
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            return new Font(FontFamily.GenericMonospace, (float)(px * _s), style, GraphicsUnit.Pixel);
        }

        // ---------- input ----------

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            Hit hit = HitAt(e.Location);
            Cursor = hit != null && hit.Click != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Hit hit = HitAt(e.Location);
            if (hit != null && hit.Click != null)
            {
                hit.Click();
                Invalidate();
            }
        }

        private Hit HitAt(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].Rect.Contains(p)) return _hits[i];
            return null;
        }

        // ---------- painting ----------

        protected override void OnPaintBackground(PaintEventArgs e) { }

        /// <summary>Renders the panel off-screen at 1x for documentation.</summary>
        public static void SavePreview(string path, OverlayForm host, History history, Snapshot snap)
        {
            using (var panel = new DetailPanel(host, history))
            {
                panel._s = 1.0;
                panel.BuildFonts();
                panel.Size = new Size(BaseW, BaseH);
                panel._snap = snap;
                panel._procTab = "mem";
                using (var bmp = new Bitmap(BaseW, BaseH, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    panel.PaintAll(g);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintAll(e.Graphics);
        }

        private void PaintAll(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Surface);
            _hits.Clear();
            _pendingTip = null;

            using (var border = new Pen(CardBorder))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            int pad = S(20), gap = S(12);
            int innerW = Width - pad * 2;

            PaintHeader(g, new Rectangle(pad, S(14), innerW, S(46)));

            // Row 1: five history charts
            int chartsTop = S(70), chartsH = S(158);
            int cw = (innerW - gap * 4) / 5;
            PaintChart(g, new Rectangle(pad, chartsTop, cw, chartsH), "CPU", _history.Cpu, null, true);
            PaintChart(g, new Rectangle(pad + (cw + gap), chartsTop, cw, chartsH), "Memory", _history.Ram, null, true);
            PaintChart(g, new Rectangle(pad + (cw + gap) * 2, chartsTop, cw, chartsH), "GPU", _history.Gpu, null, true);
            PaintChart(g, new Rectangle(pad + (cw + gap) * 3, chartsTop, cw, chartsH), "Disk", _history.Disk, null, true);
            PaintChart(g, new Rectangle(pad + (cw + gap) * 4, chartsTop, innerW - (cw + gap) * 4, chartsH),
                       "Network", _history.NetDown, _history.NetUp, false);

            // Rows 2 and 3
            int halfW = (innerW - gap) / 2;
            int row2Top = chartsTop + chartsH + gap, rowH = S(250);
            int row3Top = row2Top + rowH + gap;
            PaintCores(g, new Rectangle(pad, row2Top, halfW, rowH));
            PaintGpus(g, new Rectangle(pad + halfW + gap, row2Top, innerW - halfW - gap, rowH));
            PaintDisks(g, new Rectangle(pad, row3Top, halfW, rowH));
            PaintProcesses(g, new Rectangle(pad + halfW + gap, row3Top, innerW - halfW - gap, rowH));

            PaintFooter(g, new Rectangle(pad, row3Top + rowH + S(8), innerW, Height - (row3Top + rowH + S(8)) - S(8)));

            // Hover text last, so it sits above everything.
            if (_pendingTip == null)
            {
                Hit hit = HitAt(_mouse);
                if (hit != null && hit.Tip != null)
                {
                    _pendingTip = hit.Tip;
                    _pendingTipAt = _mouse;
                }
            }
            if (_pendingTip != null) PaintTooltip(g, _pendingTip, _pendingTipAt);
        }

        private void PaintHeader(Graphics g, Rectangle r)
        {
            Draw(g, "MachineGauges", _title, TextPrimary, r.X, r.Y);
            string sub = _host.Sampler.CpuName;
            if (_snap != null) sub += "   ·   up " + Uptime(_snap.Uptime) + "   ·   " +
                                       _snap.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Draw(g, sub, _small, TextMuted, r.X, r.Y + S(25));

            // Right side: range chips, Settings, close
            int x = r.Right;
            int h = S(26), y = r.Y + S(4);

            Rectangle close = new Rectangle(x - h, y, h, h);
            bool hotClose = close.Contains(_mouse);
            if (hotClose) using (var b = new SolidBrush(ChipOn)) FillRound(g, b, close, S(6));
            TextCentered(g, "✕", _body, hotClose ? TextPrimary : TextSecondary, close);
            _hits.Add(new Hit { Rect = close, Click = Hide, Tip = "Close (Esc)" });
            x = close.Left - S(8);

            x = Chip(g, "Settings", false, x, y, h, () => { Hide(); _host.ShowSettings(); }) - S(16);
            x = Chip(g, "5 min", _rangeSec == 300, x, y, h, () => _rangeSec = 300) - S(4);
            Chip(g, "1 min", _rangeSec == 60, x, y, h, () => _rangeSec = 60);
        }

        /// <summary>Draws a right-aligned chip ending at <paramref name="right"/>; returns its left edge.</summary>
        private int Chip(Graphics g, string label, bool on, int right, int y, int h, Action click)
        {
            int w = TextRenderer.MeasureText(label, _small).Width + S(14);
            var rect = new Rectangle(right - w, y, w, h);
            bool hot = rect.Contains(_mouse);
            using (var b = new SolidBrush(on ? ChipOn : (hot ? Color.FromArgb(40, 43, 52) : CardBg)))
                FillRound(g, b, rect, S(6));
            if (!on)
                using (var p = new Pen(CardBorder)) DrawRound(g, p, rect, S(6));
            TextCentered(g, label, _small, on ? TextPrimary : TextSecondary, rect);
            _hits.Add(new Hit { Rect = rect, Click = click });
            return rect.Left;
        }

        private Rectangle Card(Graphics g, Rectangle r, string title, string right)
        {
            using (var b = new SolidBrush(CardBg)) FillRound(g, b, r, S(10));
            using (var p = new Pen(CardBorder)) DrawRound(g, p, r, S(10));
            Draw(g, title, _h2, TextPrimary, r.X + S(14), r.Y + S(10));
            if (!string.IsNullOrEmpty(right))
            {
                int w = TextRenderer.MeasureText(right, _mono, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, right, _mono, TextPrimary, r.Right - S(14) - w, r.Y + S(10), TextFormatFlags.NoPadding);
            }
            return new Rectangle(r.X + S(14), r.Y + S(36), r.Width - S(28), r.Height - S(46));
        }

        private void PaintChart(Graphics g, Rectangle r, string title, Series a, Series b, bool percent)
        {
            string headline = "";
            if (a.Count > 0)
                headline = percent ? Pct(a[a.Count - 1]) : "";
            Rectangle body = Card(g, r, title, headline);

            // Network: a legend line under the title, live values in text ink beside colour swatches.
            int legendH = 0;
            if (b != null)
            {
                legendH = S(18);
                string down = "Down " + (a.Count > 0 ? OverlayForm.BitRate(a[a.Count - 1]) : "-");
                string up = "Up " + (b.Count > 0 ? OverlayForm.BitRate(b[b.Count - 1]) : "-");
                int lx = body.X;
                Swatch(g, Series1, lx, body.Y + S(4));
                Draw(g, down, _small, TextSecondary, lx + S(12), body.Y - S(2));
                lx += S(12) + TextRenderer.MeasureText(down, _small).Width + S(8);
                Swatch(g, Series2, lx, body.Y + S(4));
                Draw(g, up, _small, TextSecondary, lx + S(12), body.Y - S(2));
            }

            int gutterL = S(34), gutterB = S(16);
            var plot = new Rectangle(body.X + gutterL, body.Y + S(4) + legendH, body.Width - gutterL,
                                     body.Height - gutterB - S(4) - legendH);
            int n = Math.Min(_rangeSec, a.Count);

            double max = 100;
            if (!percent)
            {
                max = 1e6;   // at least 1 Mb/s so idle traffic doesn't fill the chart
                for (int i = a.Count - n; i < a.Count; i++)
                {
                    max = Math.Max(max, a[i]);
                    if (b != null) max = Math.Max(max, b[i]);
                }
                max = NiceMax(max);
            }

            // Recessive grid + y labels
            using (var gp = new Pen(Grid) { DashStyle = DashStyle.Dot })
            {
                foreach (double f in new[] { 0.0, 0.5, 1.0 })
                {
                    int gy = plot.Bottom - (int)Math.Round(plot.Height * f);
                    g.DrawLine(gp, plot.Left, gy, plot.Right, gy);
                    string lbl = percent ? ((int)(100 * f)).ToString(CultureInfo.InvariantCulture) + "%" : ShortRate(max * f);
                    int lw = TextRenderer.MeasureText(lbl, _small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    Draw(g, lbl, _small, TextMuted, plot.Left - S(6) - lw, gy - S(8), TextFormatFlags.NoPadding);
                }
            }
            Draw(g, _rangeSec == 60 ? "1 min ago" : "5 min ago", _small, TextMuted, plot.Left, plot.Bottom + S(2));
            int nowW = TextRenderer.MeasureText("now", _small, Size.Empty, TextFormatFlags.NoPadding).Width;
            Draw(g, "now", _small, TextMuted, plot.Right - nowW, plot.Bottom + S(2), TextFormatFlags.NoPadding);

            if (n < 2) return;
            double step = plot.Width / (double)(_rangeSec - 1);

            if (b != null) DrawSeries(g, plot, b, n, step, max, Series2, false);
            DrawSeries(g, plot, a, n, step, max, Series1, b == null);

            // Crosshair
            if (plot.Contains(_mouse))
            {
                int fromEnd = (int)Math.Round((plot.Right - _mouse.X) / step);
                if (fromEnd < n)
                {
                    int idx = a.Count - 1 - fromEnd;
                    int cx = plot.Right - (int)Math.Round(fromEnd * step);
                    using (var cp = new Pen(TextMuted)) g.DrawLine(cp, cx, plot.Top, cx, plot.Bottom);
                    Dot(g, cx, ValueY(plot, a[idx], max), Series1);
                    if (b != null) Dot(g, cx, ValueY(plot, b[idx], max), Series2);

                    string when = fromEnd == 0 ? "now" : fromEnd + " s ago";
                    _pendingTip = percent
                        ? title + "  " + Pct(a[idx]) + "\n" + when
                        : "↓ " + OverlayForm.BitRate(a[idx]) + "   ↑ " + OverlayForm.BitRate(b[idx]) + "\n" + when;
                    _pendingTipAt = new Point(cx, plot.Top);
                }
            }
        }

        private static int ValueY(Rectangle plot, double v, double max)
        {
            return plot.Bottom - (int)Math.Round(plot.Height * Math.Min(1.0, Math.Max(0, v) / max));
        }

        private static void DrawSeries(Graphics g, Rectangle plot, Series series, int n, double step, double max, Color color, bool fill)
        {
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                int idx = series.Count - n + i;
                float x = (float)(plot.Right - (n - 1 - i) * step);
                pts[i] = new PointF(x, ValueY(plot, series[idx], max));
            }
            if (fill)
            {
                var poly = new PointF[n + 2];
                Array.Copy(pts, poly, n);
                poly[n] = new PointF(pts[n - 1].X, plot.Bottom);
                poly[n + 1] = new PointF(pts[0].X, plot.Bottom);
                using (var b = new SolidBrush(Color.FromArgb(46, color))) g.FillPolygon(b, poly);
            }
            using (var p = new Pen(color, 2f) { LineJoin = LineJoin.Round })
                g.DrawLines(p, pts);
        }

        private void Dot(Graphics g, int x, int y, Color color)
        {
            int r = S(4);
            using (var ring = new SolidBrush(CardBg)) g.FillEllipse(ring, x - r - 2, y - r - 2, (r + 2) * 2, (r + 2) * 2);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        }

        private void Swatch(Graphics g, Color color, int x, int y)
        {
            using (var b = new SolidBrush(color)) FillRound(g, b, new Rectangle(x, y, S(8), S(8)), S(2));
        }

        private void PaintCores(Graphics g, Rectangle r)
        {
            double[] cores = _snap != null ? _snap.CoreUtil : new double[0];
            string right = _snap != null ? Pct(_snap.CpuPercent) + "  " + _snap.CpuGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz" : "";
            Rectangle body = Card(g, r, "CPU cores (" + cores.Length + ")", right);
            if (cores.Length == 0) return;

            int labelH = S(16);
            int valueH = S(16);
            var area = new Rectangle(body.X, body.Y + valueH, body.Width, body.Height - labelH - valueH);
            double slot = area.Width / (double)cores.Length;
            int barW = Math.Max(2, (int)(slot - Math.Max(2, S(4))));
            bool labelEvery = slot >= S(22);

            for (int i = 0; i < cores.Length; i++)
            {
                int x = area.X + (int)Math.Round(i * slot + (slot - barW) / 2);
                var trackRect = new Rectangle(x, area.Y, barW, area.Height);
                using (var t = new SolidBrush(Track)) g.FillRectangle(t, trackRect);
                int h = (int)Math.Round(area.Height * cores[i] / 100.0);
                if (h > 0)
                    using (var f = new SolidBrush(Series1))
                        FillTopRounded(g, f, new Rectangle(x, area.Bottom - h, barW, h), Math.Min(S(4), barW / 2));

                if (labelEvery || i % 4 == 0)
                {
                    string idx = i.ToString(CultureInfo.InvariantCulture);
                    TextCentered(g, idx, _small, TextMuted, new Rectangle(x - S(8), area.Bottom + S(1), barW + S(16), labelH));
                }
                if (labelEvery)
                    TextCentered(g, ((int)Math.Round(cores[i])).ToString(CultureInfo.InvariantCulture), _small, TextSecondary,
                                 new Rectangle(x - S(8), area.Y - valueH, barW + S(16), valueH));

                _hits.Add(new Hit
                {
                    Rect = new Rectangle((int)(area.X + i * slot), area.Y - valueH, (int)Math.Ceiling(slot), area.Height + valueH + labelH),
                    Tip = "Core " + i + "  " + Pct(cores[i])
                });
            }
        }

        private void PaintGpus(Graphics g, Rectangle r)
        {
            List<GpuReading> gpus = _snap != null ? _snap.Gpus : new List<GpuReading>();
            Rectangle body = Card(g, r, gpus.Count == 1 ? "Graphics" : "Graphics (" + gpus.Count + ")", null);
            int rowH = S(66);
            int y = body.Y;
            g.SetClip(body);
            for (int i = 0; i < gpus.Count && y + S(40) <= body.Bottom; i++)
            {
                GpuReading gpu = gpus[i];
                bool selected = _snap != null && i == _snap.SelectedGpu;
                Draw(g, gpu.Name + (selected && gpus.Count > 1 ? "  · in strip" : ""), _body, TextPrimary, body.X, y);
                string pct = Pct(gpu.Util);
                int pw = TextRenderer.MeasureText(pct, _mono, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, pct, _mono, TextPrimary, body.Right - pw, y + S(1), TextFormatFlags.NoPadding);

                Bar(g, new Rectangle(body.X, y + S(22), body.Width, S(6)), gpu.Util / 100.0);

                var parts = new List<string>();
                if (gpu.VramTotalGB > 0) parts.Add("VRAM " + gpu.VramUsedGB.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                                                   gpu.VramTotalGB.ToString("0.0", CultureInfo.InvariantCulture) + " GB");
                if (gpu.TempC >= 0) parts.Add(gpu.TempC + " °C");
                if (gpu.PowerW >= 0) parts.Add(gpu.PowerW.ToString("0", CultureInfo.InvariantCulture) + " W" +
                                              (gpu.PowerLimitW > 0 ? " / " + gpu.PowerLimitW.ToString("0", CultureInfo.InvariantCulture) + " W" : ""));
                if (gpu.FanPct >= 0) parts.Add("fan " + gpu.FanPct + "%");
                if (gpu.CoreMhz >= 0) parts.Add(gpu.CoreMhz + " MHz");
                Draw(g, string.Join("   ·   ", parts.ToArray()), _small, TextSecondary, body.X, y + S(34));
                y += rowH;
            }
            g.ResetClip();
        }

        private void PaintDisks(Graphics g, Rectangle r)
        {
            Rectangle body = Card(g, r, "Disks & drives", null);
            if (_snap == null) return;
            g.SetClip(body);
            CultureInfo ic = CultureInfo.InvariantCulture;
            int y = body.Y;

            foreach (DiskReading d in _snap.Disks)
            {
                if (y + S(40) > body.Bottom) break;
                Draw(g, d.Name, _body, TextPrimary, body.X, y);
                string pct = Pct(d.Active) + " active";
                int pw = TextRenderer.MeasureText(pct, _mono, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, pct, _mono, TextPrimary, body.Right - pw, y + S(1), TextFormatFlags.NoPadding);
                Bar(g, new Rectangle(body.X, y + S(22), body.Width, S(6)), d.Active / 100.0);
                Draw(g, "Read " + OverlayForm.ByteRate(d.ReadBps) + "   ·   Write " + OverlayForm.ByteRate(d.WriteBps),
                     _small, TextSecondary, body.X, y + S(32));
                y += S(58);
            }

            foreach (VolumeReading v in _snap.Volumes)
            {
                if (y + S(30) > body.Bottom) break;
                double used = v.TotalGB > 0 ? 1 - v.FreeGB / v.TotalGB : 0;
                string label = v.Name + "  " + Gb(v.FreeGB) + " free of " + Gb(v.TotalGB);
                Draw(g, label, _small, TextSecondary, body.X, y);
                string pct = Pct(used * 100) + " used";
                int pw = TextRenderer.MeasureText(pct, _monoSmall, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, pct, _monoSmall, TextSecondary, body.Right - pw, y + S(1), TextFormatFlags.NoPadding);
                Bar(g, new Rectangle(body.X, y + S(19), body.Width, S(5)), used);
                y += S(32);
            }
            g.ResetClip();
        }

        private void PaintProcesses(Graphics g, Rectangle r)
        {
            Rectangle body = Card(g, r, "Top processes", null);

            int chipY = r.Y + S(8), h = S(24), x = r.Right - S(12);
            x = Chip(g, "GPU", _procTab == "gpu", x, chipY, h, () => _procTab = "gpu") - S(4);
            x = Chip(g, "Memory", _procTab == "mem", x, chipY, h, () => _procTab = "mem") - S(4);
            Chip(g, "CPU", _procTab == "cpu", x, chipY, h, () => _procTab = "cpu");

            if (_snap == null || _snap.Processes == null)
            {
                Draw(g, "Collecting…", _small, TextMuted, body.X, body.Y);
                return;
            }

            var list = new List<ProcReading>(_snap.Processes);
            Comparison<ProcReading> order;
            if (_procTab == "mem") order = (a, b) => b.MemGB.CompareTo(a.MemGB);
            else if (_procTab == "gpu") order = (a, b) => b.Gpu.CompareTo(a.Gpu);
            else order = (a, b) => b.Cpu.CompareTo(a.Cpu);
            list.Sort(order);

            int rowH = S(24);
            int rows = Math.Min(list.Count, Math.Max(1, body.Height / rowH));
            int nameW = (int)(body.Width * 0.42);
            int valueW = S(76);
            CultureInfo ic = CultureInfo.InvariantCulture;
            g.SetClip(body);
            for (int i = 0; i < rows; i++)
            {
                ProcReading p = list[i];
                int y = body.Y + i * rowH;
                string name = p.Name + (p.Count > 1 ? "  ×" + p.Count : "");
                TextRenderer.DrawText(g, name, _body, new Rectangle(body.X, y, nameW, rowH - S(4)), TextPrimary,
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                double frac;
                string value;
                if (_procTab == "mem")
                {
                    frac = _snap.RamTotalGB > 0 ? p.MemGB / _snap.RamTotalGB : 0;
                    value = p.MemGB >= 1 ? p.MemGB.ToString("0.00", ic) + " GB" : (p.MemGB * 1024).ToString("0", ic) + " MB";
                }
                else if (_procTab == "gpu")
                {
                    frac = p.Gpu / 100.0;
                    value = p.Gpu.ToString("0.0", ic) + "%";
                }
                else
                {
                    frac = p.Cpu / 100.0;
                    value = p.Cpu.ToString("0.0", ic) + "%";
                }

                int barX = body.X + nameW + S(8);
                int barW = body.Width - nameW - S(8) - valueW - S(8);
                Bar(g, new Rectangle(barX, y + rowH / 2 - S(4), barW, S(7)), frac);
                int vw = TextRenderer.MeasureText(value, _monoSmall, Size.Empty, TextFormatFlags.NoPadding).Width;
                Draw(g, value, _monoSmall, TextPrimary, body.Right - vw, y + (rowH - S(4) - _monoSmall.Height) / 2, TextFormatFlags.NoPadding);
            }
            g.ResetClip();
        }

        private void PaintFooter(Graphics g, Rectangle r)
        {
            if (_snap == null) return;
            CultureInfo ic = CultureInfo.InvariantCulture;
            Config cfg = _host.CurrentConfig;
            var parts = new List<string>();

            if (_snap.CpuTempC >= 0) parts.Add("CPU " + _snap.CpuTempC + " °C");
            if (_snap.Fps > 0) parts.Add("FPS " + ((int)Math.Round(_snap.Fps)).ToString(ic) + " (" + (1000.0 / _snap.Fps).ToString("0.0", ic) + " ms)");
            else if (cfg.FpsEnabled && _host.FpsStatus == FpsState.NeedsPermission) parts.Add("FPS counter needs setup (Settings › Advanced)");
            if (_snap.PingMs >= 0) parts.Add("Ping " + _snap.PingMs + " ms to " + cfg.PingHost + (_snap.PingLossPct > 0 ? ", " + _snap.PingLossPct + "% loss" : ""));
            if (_snap.BatteryPct >= 0) parts.Add("Battery " + _snap.BatteryPct + "%" + (_snap.BatteryCharging ? " charging" : ""));

            Draw(g, string.Join("     ", parts.ToArray()), _small, TextSecondary, r.X, r.Y);
            string hint = "Esc to close" + (_host.HotkeyRegistered ? "   ·   " + cfg.Hotkey + " to toggle" : "");
            int hw = TextRenderer.MeasureText(hint, _small, Size.Empty, TextFormatFlags.NoPadding).Width;
            Draw(g, hint, _small, TextMuted, r.Right - hw, r.Y, TextFormatFlags.NoPadding);
        }

        private void PaintTooltip(Graphics g, string text, Point at)
        {
            Size sz = TextRenderer.MeasureText(text, _small);
            var rect = new Rectangle(at.X + S(12), at.Y + S(12), sz.Width + S(16), sz.Height + S(10));
            if (rect.Right > Width - S(8)) rect.X = at.X - S(12) - rect.Width;
            if (rect.Bottom > Height - S(8)) rect.Y = at.Y - S(12) - rect.Height;
            using (var b = new SolidBrush(TooltipBg)) FillRound(g, b, rect, S(6));
            using (var p = new Pen(CardBorder)) DrawRound(g, p, rect, S(6));
            TextRenderer.DrawText(g, text, _small, new Rectangle(rect.X + S(8), rect.Y + S(5), sz.Width, sz.Height),
                                  TextPrimary, TextFormatFlags.NoPrefix);
        }

        // ---------- primitives ----------

        private void Bar(Graphics g, Rectangle r, double frac)
        {
            using (var t = new SolidBrush(Track)) FillRound(g, t, r, r.Height / 2);
            int w = (int)Math.Round(r.Width * Math.Min(1.0, Math.Max(0.0, frac)));
            if (w <= 0) return;
            using (var f = new SolidBrush(Series1)) FillRound(g, f, new Rectangle(r.X, r.Y, Math.Max(w, r.Height), r.Height), r.Height / 2);
        }

        private static void Draw(Graphics g, string text, Font font, Color color, int x, int y)
        {
            Draw(g, text, font, color, x, y, TextFormatFlags.Default);
        }

        private static void Draw(Graphics g, string text, Font font, Color color, int x, int y, TextFormatFlags extra)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFormatFlags.NoPrefix | extra);
        }

        private static void TextCentered(Graphics g, string text, Font font, Color color, Rectangle r)
        {
            TextRenderer.DrawText(g, text, font, r, color,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private static void FillRound(Graphics g, Brush b, Rectangle r, int radius)
        {
            if (radius <= 0 || r.Width < 2 || r.Height < 2) { g.FillRectangle(b, r); return; }
            using (GraphicsPath p = GaugeArt.Rounded(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f)))
                g.FillPath(b, p);
        }

        private static void DrawRound(Graphics g, Pen pen, Rectangle r, int radius)
        {
            using (GraphicsPath p = GaugeArt.Rounded(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), radius))
                g.DrawPath(pen, p);
        }

        // Rounded data-end at the top, square at the baseline.
        private static void FillTopRounded(Graphics g, Brush b, Rectangle r, int radius)
        {
            if (radius < 1 || r.Height <= radius) { g.FillRectangle(b, r); return; }
            using (var p = new GraphicsPath())
            {
                int d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        private static string Pct(double v)
        {
            return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static string Gb(double gb)
        {
            return gb >= 1000
                ? (gb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " TB"
                : gb.ToString("0", CultureInfo.InvariantCulture) + " GB";
        }

        private static string Uptime(TimeSpan u)
        {
            return u.TotalDays >= 1
                ? ((int)u.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + u.Hours + "h"
                : u.Hours + "h " + u.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        private static string ShortRate(double bps)
        {
            if (bps <= 0) return "0";
            if (bps >= 1e9) return (bps / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "G";
            if (bps >= 1e6) return (bps / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            return (bps / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K";
        }

        private static double NiceMax(double v)
        {
            double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new[] { 1, 2, 2.5, 5, 10 })
                if (v <= m * exp) return m * exp;
            return 10 * exp;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (Font f in new[] { _title, _h2, _body, _small, _mono, _monoSmall })
                    if (f != null) f.Dispose();
            base.Dispose(disposing);
        }
    }
}
