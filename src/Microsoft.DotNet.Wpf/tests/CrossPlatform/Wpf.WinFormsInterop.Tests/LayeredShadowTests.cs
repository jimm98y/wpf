// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A drop shadow bitmap the way DotNetBar's PopupShadow paints one: a clip of an infinite region
// XORed with the menu rectangle, cleared to black, and soft edge lines drawn with SourceCopy. What
// GDI+ leaves in it is what UpdateLayeredWindow composites, so the menu area has to stay transparent.
//

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class LayeredShadowTests
    {
        [Fact]
        public void ClearUnderAnXoredInfiniteRegion_LeavesTheRectangleAlone()
        {
            using var bmp = new Bitmap(40, 30, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                using var region = new Region();
                using var path = new GraphicsPath();
                path.AddRectangle(new Rectangle(0, 0, 35, 25));
                region.Xor(path);
                g.SetClip(region, CombineMode.Replace);
                g.Clear(Color.Transparent);
                g.Clear(Color.Black);
            }
            Assert.Equal(0, bmp.GetPixel(10, 10).A);         // inside the rectangle: untouched
            Assert.Equal(255, bmp.GetPixel(37, 27).A);       // outside it: cleared black
        }

        /// <summary>PopupShadow.PaintShadow, step for step, on a shadow the size DotNetBar gives one
        /// (the menu less two, put five down and right of it): the strip it leaves is the five
        /// pixels along the right and bottom edges, and everything left of and above them is
        /// transparent, under the menu.</summary>
        [Fact]
        public void PopupShadowsBitmap_IsAFivePixelStripOnTheRightAndBottom()
        {
            const int W = 138, H = 90;
            using var bmp = PaintPopupShadow(W, H);
            var row = new System.Text.StringBuilder();
            for (int x = W - 8; x < W; x++) row.Append(bmp.GetPixel(x, H / 2).A).Append(' ');
            var col = new System.Text.StringBuilder();
            for (int y = H - 8; y < H; y++) col.Append(bmp.GetPixel(W / 2, y).A).Append(' ');
            Assert.True(bmp.GetPixel(W - 6, H / 2).A == 0 && bmp.GetPixel(W - 5, H / 2).A > 0
                && bmp.GetPixel(W / 2, H - 6).A == 0 && bmp.GetPixel(W / 2, H - 5).A > 0, $"row: {row} col: {col}");
        }

        private static Bitmap PaintPopupShadow(int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var region = new Region();
            var path = new GraphicsPath();
            var r = new Rectangle(0, 0, width, height);
            path.StartFigure();
            path.AddLine(r.Right - 5, r.Top, r.Right, r.Top);
            path.AddLine(r.Right, r.Top, r.Right, r.Top + 5);
            path.AddLine(r.Right, r.Top + 5, r.Right - 5, r.Top);
            path.CloseFigure();
            path.StartFigure();
            path.AddLine(r.Left, r.Bottom - 5 - 1, r.Left, r.Bottom);
            path.AddLine(r.Left, r.Bottom, r.Left + 5, r.Bottom);
            path.AddLine(r.Left + 5, r.Bottom, r.Left, r.Bottom - 5);
            path.CloseFigure();
            path.StartFigure();
            path.AddRectangle(new Rectangle(0, 0, r.Width - 5, r.Height - 5));
            path.CloseFigure();
            region.Xor(path);
            g.SetClip(region, CombineMode.Replace);
            path.Dispose();
            region.Dispose();
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);
            r.Width--;
            r.Height--;
            Color[] colors = { Color.FromArgb(14, Color.Black), Color.FromArgb(43, Color.Black), Color.FromArgb(84, Color.Black), Color.FromArgb(113, Color.Black), Color.FromArgb(128, Color.Black) };
            for (int i = 0; i < 4; i++)
            {
                using var pen = new Pen(colors[i], 1);
                using (var p = EdgePath(r, 4 - i)) g.DrawPath(pen, p);
                r.Inflate(-1, -1);
            }
            return bmp;
        }

        private static GraphicsPath EdgePath(Rectangle r, int d)
        {
            var p = new GraphicsPath();
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d, r.Right, r.Bottom - d);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddLine(r.Right - d, r.Bottom, r.X, r.Bottom);
            p.AddLine(r.X, r.Bottom, r.X, r.Top);
            return p;
        }

        [Fact]
        public void GetHbitmap_OverTransparentBlack_KeepsTheAlphaPremultiplied()
        {
            // What UpdateLayeredWindow is handed: the colour composited over the background and
            // the pixel's own alpha in the fourth byte (premultiplied ARGB over transparent black).
            if (!OperatingSystem.IsWindows()) return;
            using var bmp = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(128, 200, 100, 50));
            bmp.SetPixel(1, 0, Color.FromArgb(255, 10, 20, 30));
            IntPtr hbm = bmp.GetHbitmap(Color.FromArgb(0));
            try
            {
                using var back = Image.FromHbitmap(hbm);
                // FromHbitmap reads the colour only; read the raw bits instead.
                var bits = new int[2];
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = 2, biHeight = -1, biPlanes = 1, biBitCount = 32 };
                IntPtr dc = GetDC(IntPtr.Zero);
                try { Assert.NotEqual(0, GetDIBits(dc, hbm, 0, 1, bits, ref bi, 0)); }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                uint p0 = (uint)bits[0], p1 = (uint)bits[1];
                Assert.Equal(128u, p0 >> 24);
                Assert.Equal((200 * 128 + 127) / 255, (int)(p0 >> 16 & 0xff));
                Assert.Equal(255u, p1 >> 24);
                Assert.Equal(10u, p1 >> 16 & 0xff);
            }
            finally { DeleteObject(hbm); }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biX, biY, biClrUsed, biClrImportant; }
        [System.Runtime.InteropServices.DllImport("gdi32")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, int[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [System.Runtime.InteropServices.DllImport("gdi32")] private static extern bool DeleteObject(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    }
}
