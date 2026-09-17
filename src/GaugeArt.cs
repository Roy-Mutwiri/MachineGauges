using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace MachineGauges
{
    /// <summary>
    /// Speedometer-style gauge drawing shared by the overlay, the live tray icon and the
    /// app icon generator. The dial is open at the bottom and fills clockwise over the top.
    /// </summary>
    internal static class GaugeArt
    {
        public const float StartAngle = 150f;   // GDI+ degrees, clockwise from 3 o'clock
        public const float SweepAngle = 240f;

        private static readonly Color NeedleColor = Color.FromArgb(236, 239, 244);
        private static readonly Color TickColor = Color.FromArgb(110, 116, 130);

        // Solid green through normal loads; only shifts once a reading is genuinely high.
        private static readonly double[] StopAt = { 0, 45, 65, 80, 92 };
        private static readonly Color[] StopColor =
        {
            Color.FromArgb(94, 214, 143),   // green
            Color.FromArgb(94, 214, 143),   // green
            Color.FromArgb(240, 202, 96),   // amber
            Color.FromArgb(246, 158, 74),   // orange
            Color.FromArgb(244, 96, 96)     // red
        };

        /// <summary>Smooth green -> amber -> orange -> red ramp for a 0-100 load.</summary>
        public static Color LoadColor(double pct)
        {
            if (double.IsNaN(pct) || pct <= StopAt[0]) return StopColor[0];
            for (int i = 1; i < StopAt.Length; i++)
            {
                if (pct <= StopAt[i])
                    return Lerp(StopColor[i - 1], StopColor[i], (pct - StopAt[i - 1]) / (StopAt[i] - StopAt[i - 1]));
            }
            return StopColor[StopColor.Length - 1];
        }

        public static Color Lerp(Color a, Color b, double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static void DrawGauge(Graphics g, RectangleF box, double frac, Color color, Color track,
                                     float thickness, float glow, bool needle, bool ticks)
        {
            frac = Clamp01(frac);
            RectangleF arc = ArcRect(box, thickness);

            using (var p = RoundPen(track, thickness))
                g.DrawArc(p, arc, StartAngle, SweepAngle);

            if (ticks) DrawTicks(g, arc, thickness);

            float sweep = (float)(SweepAngle * frac);
            if (sweep > 0.5f)
            {
                if (glow > 0)
                {
                    using (var halo = RoundPen(Color.FromArgb((int)(95 * Math.Min(1f, glow)), color), thickness * 1.9f))
                        g.DrawArc(halo, arc, StartAngle, sweep);
                }
                using (var p = RoundPen(color, thickness))
                    g.DrawArc(p, arc, StartAngle, sweep);
            }

            if (needle) DrawNeedle(g, arc, frac, thickness);
        }

        /// <summary>The app icon: a dark rounded plate with a gauge dial.</summary>
        public static void DrawAppIcon(Graphics g, int size, double frac, bool gradientArc)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            bool tiny = size <= 24;
            RectangleF plate = tiny
                ? new RectangleF(0, 0, s, s)
                : new RectangleF(s * 0.03f, s * 0.03f, s * 0.94f, s * 0.94f);

            using (GraphicsPath path = Rounded(plate, s * (tiny ? 0.20f : 0.22f)))
            {
                using (var bg = new LinearGradientBrush(plate, Color.FromArgb(42, 46, 57), Color.FromArgb(11, 12, 16),
                                                        LinearGradientMode.Vertical))
                    g.FillPath(bg, path);
                if (!tiny)
                    using (var border = new Pen(Color.FromArgb(66, 70, 84), Math.Max(1f, s / 96f)))
                        g.DrawPath(border, path);
            }

            float thick = tiny ? Math.Max(1.6f, s * 0.12f) : s * 0.085f;
            float inset = s * (tiny ? 0.05f : 0.12f);
            RectangleF box = RectangleF.Inflate(plate, -inset, -inset);

            if (!gradientArc)
            {
                DrawGauge(g, box, frac, LoadColor(frac * 100), Color.FromArgb(58, 62, 74), thick, 0f, true, false);
                return;
            }

            RectangleF arc = ArcRect(box, thick);
            using (var trackPen = RoundPen(Color.FromArgb(50, 54, 65), thick))
                g.DrawArc(trackPen, arc, StartAngle, SweepAngle);
            if (size >= 48) DrawTicks(g, arc, thick);
            DrawGradientArc(g, arc, Clamp01(frac), thick);
            DrawNeedle(g, arc, frac, thick);
        }

        // ---------- helpers ----------

        private static RectangleF ArcRect(RectangleF box, float thickness)
        {
            // Leave room for the glow halo, and drop the circle a little: the dial is
            // open at the bottom, so this centres what is actually drawn.
            float inset = thickness * 0.95f;
            float d = Math.Max(1f, Math.Min(box.Width, box.Height) - inset * 2);
            float r = d / 2f;
            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f + r * 0.2f;
            return new RectangleF(cx - r, cy - r, d, d);
        }

        private static void DrawGradientArc(Graphics g, RectangleF arc, double frac, float thick)
        {
            float total = (float)(SweepAngle * frac);
            if (total <= 0.5f) return;
            const float step = 3f;
            using (var pen = new Pen(Color.White, thick))
            {
                for (float a = 0; a < total; a += step)
                {
                    float seg = Math.Min(step, total - a);
                    pen.Color = LoadColor(100.0 * (a + seg / 2f) / SweepAngle);
                    g.DrawArc(pen, arc, StartAngle + a, Math.Min(seg + 0.8f, total - a));
                }
            }
            FillDot(g, PointOnArc(arc, 0), thick / 2f, LoadColor(0));
            FillDot(g, PointOnArc(arc, frac), thick / 2f, LoadColor(frac * 100));
        }

        private static void DrawTicks(Graphics g, RectangleF arc, float thick)
        {
            float r = arc.Width / 2f, cx = arc.X + r, cy = arc.Y + r;
            using (var pen = new Pen(TickColor, Math.Max(1f, thick * 0.22f)))
            {
                for (int i = 0; i <= 10; i++)
                {
                    double a = (StartAngle + SweepAngle * i / 10.0) * Math.PI / 180.0;
                    float r0 = r - thick * 0.9f;
                    float r1 = r - thick * (i % 5 == 0 ? 1.75f : 1.35f);
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(a) * r0), cy + (float)(Math.Sin(a) * r0),
                        cx + (float)(Math.Cos(a) * r1), cy + (float)(Math.Sin(a) * r1));
                }
            }
        }

        private static void DrawNeedle(Graphics g, RectangleF arc, double frac, float thick)
        {
            float r = arc.Width / 2f, cx = arc.X + r, cy = arc.Y + r;
            PointF tip = PointOnArc(arc, Clamp01(frac), r - thick * 1.1f);
            using (var pen = RoundPen(NeedleColor, Math.Max(1f, thick * 0.5f)))
                g.DrawLine(pen, cx, cy, tip.X, tip.Y);
            FillDot(g, new PointF(cx, cy), Math.Max(1.5f, thick * 0.6f), NeedleColor);
        }

        private static PointF PointOnArc(RectangleF arc, double frac)
        {
            return PointOnArc(arc, frac, arc.Width / 2f);
        }

        private static PointF PointOnArc(RectangleF arc, double frac, float radius)
        {
            float r = arc.Width / 2f;
            double a = (StartAngle + SweepAngle * frac) * Math.PI / 180.0;
            return new PointF(arc.X + r + (float)(Math.Cos(a) * radius), arc.Y + r + (float)(Math.Sin(a) * radius));
        }

        private static void FillDot(Graphics g, PointF c, float radius, Color color)
        {
            using (var b = new SolidBrush(color))
                g.FillEllipse(b, c.X - radius, c.Y - radius, radius * 2, radius * 2);
        }

        private static Pen RoundPen(Color color, float width)
        {
            var p = new Pen(color, width);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            return p;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Max(0.5f, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static double Clamp01(double v)
        {
            return double.IsNaN(v) ? 0 : (v < 0 ? 0 : (v > 1 ? 1 : v));
        }
    }
}
