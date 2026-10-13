// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Graphics.GetHdc on a Graphics that records a window's picture (a WinForms paint, CreateGraphics), on
// Windows: a REAL memory DC over a 32-bit DIB the size of the window, so that everything GDI does
// with it -- CreateCompatibleDC of it, BitBlt into it, FillRect, DrawText, uxtheme -- is real gdi32.
// Stock hands out the window's own DC there. When the DC is released, what GDI drew is put into the
// recording as an image at the window's origin.
//
// Only what was drawn is put there: the DIB starts filled with a marker colour, RGB(2,3,4), and
// pixels still holding it (whatever their alpha -- GDI writes 0 there) are left out. Until this
// existed the handle was a number GDI had never seen: DotNetBar's double-buffered controls
// (CreateCompatibleDC(e.Graphics.GetHdc())) got a null DC and died on Graphics.FromHdc.
//
// gdi32 only, no GDI+.
//

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Drawing.WebGpuBackend.Gdip
{
    [SupportedOSPlatform ("windows")]
    internal sealed unsafe class GpRecordingDc : IDisposable
    {
        const uint Marker = 0x00020304;

        IntPtr _dc, _dib, _old;
        uint* _bits;
        public readonly int Width, Height;
        public IntPtr Hdc => _dc;

        GpRecordingDc (int w, int h)
        {
            Width = w; Height = h;
            _dc = CreateCompatibleDC (IntPtr.Zero);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            void* bits;
            _dib = CreateDIBSection (_dc, &bi, 0, &bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _dib == IntPtr.Zero) { Dispose (); return; }
            _bits = (uint*) bits;
            new Span<uint> (_bits, w * h).Fill (Marker | 0x01000000);
            _old = SelectObject (_dc, _dib);
        }

        /// <summary>A DC for a surface of <paramref name="size"/> device pixels; null when GDI cannot make one.</summary>
        public static GpRecordingDc Create (Size size)
        {
            if (size.Width <= 0 || size.Height <= 0 || size.Width > 16384 || size.Height > 16384) return null;
            var dc = new GpRecordingDc (size.Width, size.Height);
            return dc._dc == IntPtr.Zero ? null : dc;
        }

        /// <summary>What GDI drew, as an image, and where it goes in device pixels; null when nothing was.</summary>
        public Bitmap TakeDrawn (out Point at)
        {
            at = Point.Empty;
            GdiFlush ();
            int minX = Width, minY = Height, maxX = -1, maxY = -1;
            for (int y = 0; y < Height; y++) {
                uint* row = _bits + (long) y * Width;
                for (int x = 0; x < Width; x++) {
                    if ((row [x] & 0xFFFFFF) == Marker) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return null;
            int w = maxX - minX + 1, h = maxY - minY + 1;
            var bmp = new Bitmap (w, h, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try {
                for (int y = 0; y < h; y++) {
                    uint* src = _bits + (long) (y + minY) * Width + minX;
                    uint* dst = (uint*) ((byte*) data.Scan0 + (long) y * data.Stride);
                    for (int x = 0; x < w; x++)
                        dst [x] = (src [x] & 0xFFFFFF) == Marker ? 0u : src [x] | 0xFF000000;
                }
            } finally {
                bmp.UnlockBits (data);
            }
            at = new Point (minX, minY);
            return bmp;
        }

        public void Dispose ()
        {
            if (_dc != IntPtr.Zero) {
                if (_old != IntPtr.Zero) SelectObject (_dc, _old);
                DeleteDC (_dc);
                _dc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero) { DeleteObject (_dib); _dib = IntPtr.Zero; }
        }

        [StructLayout (LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport ("gdi32.dll")] static extern IntPtr CreateCompatibleDC (IntPtr hdc);
        [DllImport ("gdi32.dll")] static extern IntPtr CreateDIBSection (IntPtr hdc, BITMAPINFOHEADER* bi, uint usage, void** bits, IntPtr section, uint offset);
        [DllImport ("gdi32.dll")] static extern IntPtr SelectObject (IntPtr hdc, IntPtr obj);
        [DllImport ("gdi32.dll")] static extern bool DeleteDC (IntPtr hdc);
        [DllImport ("gdi32.dll")] static extern bool DeleteObject (IntPtr obj);
        [DllImport ("gdi32.dll")] static extern bool GdiFlush ();
    }
}
