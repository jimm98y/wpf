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
