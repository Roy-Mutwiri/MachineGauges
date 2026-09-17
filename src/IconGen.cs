using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MachineGauges
{
    /// <summary>Build-time tool: renders the gauge art into a multi-resolution .ico.</summary>
    internal static class IconGen
    {
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        private static int Main(string[] args)
        {
            string icoPath = args.Length > 0 ? args[0] : "app.ico";
            string previewPath = args.Length > 1 ? args[1] : null;
            const double needle = 0.72;

            var images = new List<byte[]>();
            foreach (int size in Sizes)
            {
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        GaugeArt.DrawAppIcon(g, size, needle, true);
                    images.Add(size >= 128 ? Png(bmp) : Dib(bmp));

                    if (size == 256 && previewPath != null)
                        bmp.Save(previewPath, ImageFormat.Png);
                }
            }

            using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);             // reserved
                w.Write((ushort)1);             // type: icon
                w.Write((ushort)Sizes.Length);

                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int s = Sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);           // palette colours
                    w.Write((byte)0);           // reserved
                    w.Write((ushort)1);         // planes
                    w.Write((ushort)32);        // bpp
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] img in images) w.Write(img);
            }

            Console.WriteLine("Wrote " + icoPath);
            return 0;
        }

        private static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // Classic 32-bit DIB icon entry: header, bottom-up BGRA pixels, then an empty AND mask.
        private static byte[] Dib(Bitmap bmp)
        {
            int size = bmp.Width;
            var rect = new Rectangle(0, 0, size, size);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new byte[size * size * 4];
            try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
            finally { bmp.UnlockBits(data); }

            int maskRow = ((size + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);                    // biSize
                w.Write(size);                  // biWidth
                w.Write(size * 2);              // biHeight (XOR + AND)
                w.Write((ushort)1);             // biPlanes
                w.Write((ushort)32);            // biBitCount
                w.Write(0);                     // biCompression = BI_RGB
                w.Write(pixels.Length + maskRow * size);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);

                for (int y = size - 1; y >= 0; y--)
                    w.Write(pixels, y * size * 4, size * 4);
                w.Write(new byte[maskRow * size]);
                return ms.ToArray();
            }
        }
    }
}
