// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A GDI device context as a GDI+ surface, on Windows only (an HDC exists nowhere else):
//
//   * Graphics.FromHdc / FromHwnd: the DC's visible area is copied into a 32bpp RGB bitmap the
//     managed engine draws on (lazily, the first time anything is drawn), and copied back to the DC
//     on Flush and Dispose -- what GDI+ itself does for every antialiased or alpha-blended draw on a
//     DC (it reads the destination, blends, and writes it back).
//   * Graphics.GetHdc on a bitmap's Graphics: a memory DC holding a copy of the bitmap's pixels,
//     copied back when the DC is released (GpGraphics::GetHdc / ReleaseHdc on a bitmap surface).
//
// gdi32 only, no GDI+.
//

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Drawing.WebGpuBackend.Gdip
{
    [SupportedOSPlatform ("windows")]
    internal sealed class GpHdcSurface : IDisposable
    {
        readonly IntPtr _hdc;
        readonly Rectangle _area;
        IntPtr _memDc, _dib, _oldBitmap, _bits;
        public readonly int Width, Height;

        public IntPtr Hdc => _hdc;
        public Rectangle Area => _area;

        GpHdcSurface (IntPtr hdc, Rectangle area)
        {
            _hdc = hdc;
            _area = area;
            Width = area.Width;
            Height = area.Height;
            _memDc = CreateCompatibleDC (hdc);
            var bi = new BITMAPINFOHEADER {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER> (), biWidth = Width, biHeight = -Height,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _dib = CreateDIBSection (hdc, ref bi, 0, out _bits, IntPtr.Zero, 0);
            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero) {
                Dispose ();
                throw new OutOfMemoryException ();
            }
            _oldBitmap = SelectObject (_memDc, _dib);
        }

        /// <summary>The DC's visible area (GetClipBox), or null when it has none.</summary>
        public static GpHdcSurface ForDc (IntPtr hdc)
        {
            if (GetClipBox (hdc, out RECT r) == 0 || r.Right <= r.Left || r.Bottom <= r.Top)
                return null;
            return new GpHdcSurface (hdc, Rectangle.FromLTRB (r.Left, r.Top, r.Right, r.Bottom));
        }

        /// <summary>A memory DC over a copy of <paramref name="frame"/>'s pixels (a bitmap's GetHdc).</summary>
        public static GpHdcSurface ForFrame (GdipFrame frame)
        {
            IntPtr screen = GetDC (IntPtr.Zero);
            try {
                var s = new GpHdcSurface (screen, new Rectangle (0, 0, frame.Width, frame.Height));
                s.FromFrame (frame);
                return s;
            } finally {
                ReleaseDC (IntPtr.Zero, screen);
            }
        }

        public IntPtr MemoryDc => _memDc;

        public static float DpiOf (IntPtr hdc, bool y) => GetDeviceCaps (hdc, y ? 90 : 88);

        /// <summary>The DC's pixels into the frame (alpha opaque).</summary>
        public void Capture (GdipFrame frame)
        {
            BitBlt (_memDc, 0, 0, Width, Height, _hdc, _area.X, _area.Y, 0x00CC0020);
            GdiFlush ();
            int row = Width * 4;
            var line = new byte [row];
            for (int y = 0; y < Height; y++) {
                Marshal.Copy (_bits + y * row, line, 0, row);
                for (int i = 3; i < row; i += 4) line [i] = 0xff;
                WriteRow (frame, y, line);
            }
        }

        /// <summary>The frame's pixels out to the DC.</summary>
        public void Present (GdipFrame frame)
        {
            ToDib (frame);
            BitBlt (_hdc, _area.X, _area.Y, Width, Height, _memDc, 0, 0, 0x00CC0020);
            GdiFlush ();
        }

        void FromFrame (GdipFrame frame) => ToDib (frame);

        void ToDib (GdipFrame frame)
        {
            int row = Width * 4;
            var argb = new uint [Width];
            var line = new byte [row];
            for (int y = 0; y < Height; y++) {
                GdipPixels.ReadArgb (frame, 0, y, Width, argb, 0);
                Buffer.BlockCopy (argb, 0, line, 0, row);
                Marshal.Copy (line, 0, _bits + y * row, row);
            }
        }

        /// <summary>After GDI drew into the memory DC: what it changed back into the frame, opaque
        /// (GDI does not write alpha).</summary>
        public void CopyBack (GdipFrame frame)
        {
            GdiFlush ();
            int row = Width * 4;
            var before = new uint [Width];
            var after = new uint [Width];
            for (int y = 0; y < Height; y++) {
                GdipPixels.ReadArgb (frame, 0, y, Width, before, 0);
                var bytes = new byte [row];
                Marshal.Copy (_bits + y * row, bytes, 0, row);
                Buffer.BlockCopy (bytes, 0, after, 0, row);
                bool changed = false;
                for (int x = 0; x < Width; x++)
                    if ((after [x] & 0xffffff) != (before [x] & 0xffffff)) { before [x] = after [x] | 0xff000000; changed = true; }
                if (changed) GdipPixels.WriteArgb (frame, 0, y, Width, before, 0);
            }
        }

        static void WriteRow (GdipFrame frame, int y, byte[] bgra)
        {
            var argb = new uint [frame.Width];
            Buffer.BlockCopy (bgra, 0, argb, 0, Math.Min (bgra.Length, argb.Length * 4));
            GdipPixels.WriteArgb (frame, 0, y, frame.Width, argb, 0);
        }

        public void Dispose ()
        {
            if (_memDc != IntPtr.Zero) {
                if (_oldBitmap != IntPtr.Zero) SelectObject (_memDc, _oldBitmap);
                DeleteDC (_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_dib != IntPtr.Zero) { DeleteObject (_dib); _dib = IntPtr.Zero; }
        }

        [StructLayout (LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout (LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport ("gdi32.dll")] static extern int GetClipBox (IntPtr hdc, out RECT r);
        [DllImport ("gdi32.dll")] static extern IntPtr CreateCompatibleDC (IntPtr hdc);
        [DllImport ("gdi32.dll")] static extern IntPtr CreateDIBSection (IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport ("gdi32.dll")] static extern IntPtr SelectObject (IntPtr hdc, IntPtr obj);
        [DllImport ("gdi32.dll")] static extern bool DeleteDC (IntPtr hdc);
        [DllImport ("gdi32.dll")] static extern bool DeleteObject (IntPtr obj);
        [DllImport ("gdi32.dll")] static extern bool BitBlt (IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport ("gdi32.dll")] static extern bool GdiFlush ();
        [DllImport ("gdi32.dll")] static extern int GetDeviceCaps (IntPtr hdc, int index);
        [DllImport ("user32.dll")] internal static extern IntPtr GetDC (IntPtr hwnd);
        [DllImport ("user32.dll")] internal static extern int ReleaseDC (IntPtr hwnd, IntPtr hdc);
    }
}
