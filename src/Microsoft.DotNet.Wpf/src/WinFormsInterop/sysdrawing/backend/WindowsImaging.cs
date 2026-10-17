// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The OS-object conversions of a managed image: HBITMAP and HICON <-> Bitmap.
//
// On Windows those handles are the OS's own GDI and USER objects, so this asks gdi32 and user32 --
// GetObject / GetDIBits / CreateDIBSection for bitmaps, GetIconInfo / CreateIconIndirect for icons
// -- which is the OS being the subject, not GDI+ (gdiplus.dll is never involved). Everywhere else
// there are no such objects, and a handle is a token for a copy of the pixels kept here, so that the
// idioms that pass pictures around as handles (Icon.FromHandle(bitmap.GetHicon()), a cursor from a
// bitmap) still work.
//

using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Drawing.WebGpuBackend
{
    internal static class WindowsImaging
    {
        // ---- the off-Windows handle table -------------------------------------------------------

        private static readonly Dictionary<IntPtr, GdipFrame> s_handles = new Dictionary<IntPtr, GdipFrame> ();
        private static long s_next = 0x4D490000;   // "MI" -- managed image

        static WindowsImaging ()
        {
            // Off Windows an HBITMAP from GetHbitmap is a handle into this table, and the only code
            // that can make use of one is this assembly. Something that emulates GDI (a launcher
            // running a Windows app) gets selected into a DC exactly such a handle, so it is told
            // how to see the pixels behind it. Nothing reads this unless such a thing is present.
            if (!OperatingSystem.IsWindows ())
                AppContext.SetData ("System.Drawing.ManagedHbitmapImage", (Func<IntPtr, Image>) ImageOfHandle);
        }

        private static Image ImageOfHandle (IntPtr handle)
        {
            lock (s_handles) {
                if (!s_handles.TryGetValue (handle, out GdipFrame f)) return null;
                return new Bitmap (new GdipImageData (f.Clone ()));
            }
        }

        private static IntPtr Register (GdipFrame frame)
        {
            lock (s_handles) {
                var h = (IntPtr) (s_next += 4);
                s_handles [h] = frame;
                return h;
            }
        }

        private static GdipFrame Lookup (IntPtr handle)
        {
            lock (s_handles) {
                if (s_handles.TryGetValue (handle, out GdipFrame f)) return f.Clone ();
            }
            throw new ArgumentException ("Parameter is not valid.");
        }

        /// <summary>Destroys an icon handle this produced (off Windows; DestroyIcon on Windows).</summary>
        internal static void DestroyIcon (IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            if (OperatingSystem.IsWindows ()) { NativeDestroyIcon (handle); return; }
            lock (s_handles) s_handles.Remove (handle);
        }

        internal static bool IsManagedHandle (IntPtr handle)
        {
            lock (s_handles) return s_handles.ContainsKey (handle);
        }

        // ---- HBITMAP ----------------------------------------------------------------------------

        /// <summary>GdipCreateBitmapFromHBITMAP: the bitmap's pixels, 24bppRgb from a 24-bit bitmap and
        /// 32bppRgb otherwise -- the alpha of a 32-bit one ignored, as GDI+ ignores it.</summary>
        internal static GdipImageData FromHbitmap (IntPtr hbitmap, IntPtr hpalette)
        {
            if (!OperatingSystem.IsWindows ())
                return new GdipImageData (Lookup (hbitmap));
            if (hbitmap == IntPtr.Zero || GetObject (hbitmap, Marshal.SizeOf<BITMAP> (), out BITMAP bm) == 0)
                throw new ExternalException ("A generic error occurred in GDI+.");
            int w = bm.bmWidth, h = Math.Abs (bm.bmHeight);
            uint[] argb = ReadDib (hbitmap, w, h);
            PixelFormat pf = bm.bmBitsPixel == 24 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppRgb;
            var frame = new GdipFrame (w, h, pf);
            for (int i = 0; i < argb.Length; i++) argb [i] |= 0xff000000u;
            for (int y = 0; y < h; y++) GdipPixels.WriteArgb (frame, 0, y, w, argb, y * w);
            return new GdipImageData (frame) { Flags = 0 };
        }

        /// <summary>GdipCreateHBITMAPFromBitmap: a 32-bit DIB section with every pixel's colour
        /// composited over <paramref name="background"/> and its own alpha kept in the fourth byte.
        /// Over a transparent black background that is the premultiplied ARGB UpdateLayeredWindow
        /// takes -- how DotNetBar's drop shadow and every soft-edged layered splash get their bits;
        /// with the alpha forced opaque the shadow was a black box.</summary>
        internal static IntPtr GetHbitmap (GdipFrame f, Color background)
        {
            if (!OperatingSystem.IsWindows ())
                return Register (f.Clone ());
            uint[] argb = GdipPixels.ToArgb (f, new Rectangle (0, 0, f.Width, f.Height));
            int br = background.R, bg = background.G, bb = background.B;
            for (int i = 0; i < argb.Length; i++) {
                uint c = argb [i];
                int a = (int) (c >> 24);
                if (a == 255) continue;
                int r = ((int) (c >> 16 & 0xff) * a + br * (255 - a) + 127) / 255;
                int g = ((int) (c >> 8 & 0xff) * a + bg * (255 - a) + 127) / 255;
                int b = ((int) (c & 0xff) * a + bb * (255 - a) + 127) / 255;
                argb [i] = (uint) a << 24 | (uint) r << 16 | (uint) g << 8 | (uint) b;
            }
            return CreateDib (argb, f.Width, f.Height);
        }

        /// <summary>
        /// A monochrome bitmap of <paramref name="bitmap"/>'s transparency, as ControlPaint.CreateHBitmapTransparencyMask
        /// makes one: 1 (white) where a pixel's alpha is zero, 0 (black) elsewhere. On Windows a real 1bpp GDI
        /// bitmap (CreateBitmap, word-aligned rows, the leftmost pixel in the high bit); elsewhere a handle to the
        /// same black-and-white picture.
        /// </summary>
        internal static IntPtr CreateTransparencyMask (Bitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            uint[] argb = GdipPixels.ToArgb (bitmap.Data.Frame, new Rectangle (0, 0, w, h));
            if (!OperatingSystem.IsWindows ()) {
                var mask = new GdipFrame (w, h, PixelFormat.Format32bppArgb);
                var row = new uint [w];
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) row [x] = (argb [y * w + x] >> 24) == 0 ? 0xffffffffu : 0xff000000u;
                    GdipPixels.WriteArgb (mask, 0, y, w, row, 0);
                }
                return Register (mask);
            }
            int stride = (w + 7) / 8;
            if ((stride & 1) != 0) stride++;   // scan lines on word boundaries
            byte[] bits = new byte [stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if ((argb [y * w + x] >> 24) == 0)
                        bits [stride * y + x / 8] |= (byte) (0x80 >> (x % 8));
            return CreateBitmap (w, h, 1, 1, bits);
        }

        /// <summary>
        /// ControlPaint.CreateHBitmapColorMask: <paramref name="bitmap"/>'s HBITMAP with its colour cleared to black
        /// wherever <paramref name="monochromeMask"/> is set (GDI's DSna blit of the mask: each pixel ANDed with the
        /// inverse of the mask's white), the alpha byte untouched.
        /// </summary>
        internal static IntPtr CreateColorMask (Bitmap bitmap, IntPtr monochromeMask)
        {
            int w = bitmap.Width, h = bitmap.Height;
            uint[] mask;
            if (OperatingSystem.IsWindows ()) {
                mask = ReadDib (monochromeMask, w, h);
            } else {
                GdipFrame m = Lookup (monochromeMask);
                mask = GdipPixels.ToArgb (m, new Rectangle (0, 0, Math.Min (w, m.Width), Math.Min (h, m.Height)));
                if (m.Width != w || m.Height != h) {
                    var full = new uint [w * h];
                    for (int y = 0; y < Math.Min (h, m.Height); y++)
                        Array.Copy (mask, y * Math.Min (w, m.Width), full, y * w, Math.Min (w, m.Width));
                    mask = full;
                }
            }
            uint[] argb = GdipPixels.ToArgb (bitmap.Data.Frame, new Rectangle (0, 0, w, h));
            // GetHbitmap's own compositing over its default background first, then the mask.
            Color background = Color.LightGray;
            for (int i = 0; i < argb.Length; i++) {
                uint c = argb [i];
                int a = (int) (c >> 24);
                if (a != 255) {
                    int r = ((int) (c >> 16 & 0xff) * a + background.R * (255 - a) + 127) / 255;
                    int g = ((int) (c >> 8 & 0xff) * a + background.G * (255 - a) + 127) / 255;
                    int b = ((int) (c & 0xff) * a + background.B * (255 - a) + 127) / 255;
                    c = (uint) a << 24 | (uint) r << 16 | (uint) g << 8 | (uint) b;
                }
                if ((mask [i] & 0x00ffffff) != 0) c &= 0xff000000u;
                argb [i] = c;
            }
            if (!OperatingSystem.IsWindows ()) {
                var frame = new GdipFrame (w, h, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) GdipPixels.WriteArgb (frame, 0, y, w, argb, y * w);
                return Register (frame);
            }
            return CreateDib (argb, w, h);
        }

        [DllImport ("gdi32.dll")]
        private static extern IntPtr CreateBitmap (int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

        internal static GdipImageData FromResource (IntPtr hinstance, string name)
        {
            if (!OperatingSystem.IsWindows ())
                throw new PlatformNotSupportedException ("Bitmap resources of a native module exist only on Windows.");
            IntPtr hbm = LoadImage (hinstance, name, 0 /* IMAGE_BITMAP */, 0, 0, 0x2000 /* LR_CREATEDIBSECTION */);
            if (hbm == IntPtr.Zero)
                throw new ArgumentException ("Parameter is not valid.");
            try { return FromHbitmap (hbm, IntPtr.Zero); }
            finally { DeleteObject (hbm); }
        }

        // ---- HICON ------------------------------------------------------------------------------

        /// <summary>GdipCreateBitmapFromHICON: 32bppArgb, from the icon's alpha when its colour
        /// bitmap carries any, else from its AND mask; a monochrome icon from its two masks.</summary>
        internal static GdipImageData FromHicon (IntPtr hicon)
        {
            if (!OperatingSystem.IsWindows ())
                return new GdipImageData (Lookup (hicon)) { Flags = (int) ImageFlags.HasAlpha };
            if (hicon == IntPtr.Zero || !GetIconInfo (hicon, out ICONINFO ii))
                throw new ArgumentException ("Parameter is not valid.");
            try {
                if (GetObject (ii.hbmMask, Marshal.SizeOf<BITMAP> (), out BITMAP mbm) == 0)
                    throw new ArgumentException ("Parameter is not valid.");
                int w = mbm.bmWidth, h = Math.Abs (mbm.bmHeight);
                uint[] argb;
                if (ii.hbmColor != IntPtr.Zero) {
                    argb = ReadDib (ii.hbmColor, w, h);
                    bool alpha = false;
                    foreach (uint c in argb) if ((c >> 24) != 0) { alpha = true; break; }
                    if (!alpha) {
                        uint[] mask = ReadDib (ii.hbmMask, w, h);
                        for (int i = 0; i < argb.Length; i++)
                            argb [i] = (mask [i] & 0xffffff) != 0 ? 0u : argb [i] | 0xff000000u;
                    }
                } else {
                    // Monochrome: the mask bitmap is twice the height, AND above XOR.
                    h /= 2;
                    uint[] both = ReadDib (ii.hbmMask, w, h * 2);
                    argb = new uint [w * h];
                    for (int i = 0; i < argb.Length; i++) {
                        bool and = (both [i] & 0xffffff) != 0, xor = (both [i + w * h] & 0xffffff) != 0;
                        argb [i] = and ? 0u : xor ? 0xffffffffu : 0xff000000u;
                    }
                }
                var frame = new GdipFrame (w, h, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) GdipPixels.WriteArgb (frame, 0, y, w, argb, y * w);
                return new GdipImageData (frame) { Flags = (int) ImageFlags.HasAlpha };
            } finally {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject (ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject (ii.hbmMask);
            }
        }

        /// <summary>GdipCreateHICONFromBitmap: an icon of the bitmap's ARGB, the alpha carrying the
        /// shape (the AND mask left clear).</summary>
        internal static IntPtr GetHicon (GdipFrame f)
        {
            if (!OperatingSystem.IsWindows ())
                return Register (GdipPixels.Convert (f, new Rectangle (0, 0, f.Width, f.Height), PixelFormat.Format32bppArgb, false));
            uint[] argb = GdipPixels.ToArgb (f, new Rectangle (0, 0, f.Width, f.Height));
            IntPtr color = CreateDib (argb, f.Width, f.Height);
            IntPtr mask = CreateBitmap (f.Width, f.Height, 1, 1, IntPtr.Zero);
            try {
                var ii = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
                IntPtr icon = CreateIconIndirect (ref ii);
                if (icon == IntPtr.Zero)
                    throw new ExternalException ("A generic error occurred in GDI+.");
                return icon;
            } finally {
                DeleteObject (color);
                DeleteObject (mask);
            }
        }

        /// <summary>The size an icon handle stands for (GetIconInfo on Windows).</summary>
        internal static Size IconSize (IntPtr hicon)
        {
            if (!OperatingSystem.IsWindows ()) {
                lock (s_handles)
                    if (s_handles.TryGetValue (hicon, out GdipFrame f)) return new Size (f.Width, f.Height);
                throw new ArgumentException ("Parameter is not valid.");
            }
            if (!GetIconInfo (hicon, out ICONINFO ii))
                throw new ArgumentException ("Parameter is not valid.");
            try {
                GetObject (ii.hbmMask, Marshal.SizeOf<BITMAP> (), out BITMAP bm);
                return new Size (bm.bmWidth, ii.hbmColor == IntPtr.Zero ? Math.Abs (bm.bmHeight) / 2 : Math.Abs (bm.bmHeight));
            } finally {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject (ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject (ii.hbmMask);
            }
        }

        /// <summary>Whether an icon handle is an icon (not a cursor).</summary>
        internal static bool IsIcon (IntPtr hicon)
        {
            if (!OperatingSystem.IsWindows ()) return IsManagedHandle (hicon);
            if (!GetIconInfo (hicon, out ICONINFO ii)) return false;
            if (ii.hbmColor != IntPtr.Zero) DeleteObject (ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject (ii.hbmMask);
            return ii.fIcon;
        }

        // ---- gdi32 / user32 ---------------------------------------------------------------------

        // A bitmap's pixels as 32-bit top-down BGRA words.
        private static uint[] ReadDib (IntPtr hbitmap, int w, int h)
        {
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER> (), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new uint [w * h];
            IntPtr dc = GetDC (IntPtr.Zero);
            try {
                var info = new BITMAPINFO { bmiHeader = bmi, bmiColors = new uint [256] };
                if (GetDIBits (dc, hbitmap, 0, (uint) h, pixels, ref info, 0) == 0)
                    throw new ExternalException ("A generic error occurred in GDI+.");
            } finally {
                ReleaseDC (IntPtr.Zero, dc);
            }
            return pixels;
        }

        private static IntPtr CreateDib (uint[] argb, int w, int h)
        {
            var info = new BITMAPINFO {
                bmiHeader = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER> (), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 },
                bmiColors = new uint [256],
            };
            IntPtr hbm = CreateDIBSection (IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hbm == IntPtr.Zero)
                throw new ExternalException ("A generic error occurred in GDI+.");
            Marshal.Copy ((int[]) (object) argb, 0, bits, argb.Length);
            return hbm;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs (UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] bmiColors;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs (UnmanagedType.Bool)] public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport ("gdi32.dll", EntryPoint = "GetObjectW")]
        private static extern int GetObject (IntPtr h, int size, out BITMAP bm);
        [DllImport ("gdi32.dll")]
        private static extern int GetDIBits (IntPtr dc, IntPtr hbm, uint start, uint lines, [Out] uint[] bits, ref BITMAPINFO info, uint usage);
        [DllImport ("gdi32.dll")]
        private static extern IntPtr CreateDIBSection (IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport ("gdi32.dll")]
        private static extern IntPtr CreateBitmap (int w, int h, uint planes, uint bitCount, IntPtr bits);
        [DllImport ("gdi32.dll")]
        private static extern bool DeleteObject (IntPtr h);
        [DllImport ("user32.dll")]
        private static extern IntPtr GetDC (IntPtr hwnd);
        [DllImport ("user32.dll")]
        private static extern int ReleaseDC (IntPtr hwnd, IntPtr dc);
        // An icon is read as the application sees it: asked from a per-monitor-aware thread, Windows
        // hands back an icon made at 96 dpi scaled to the display (a 32-pixel icon's bitmaps at 64).
        private static bool GetIconInfo (IntPtr hicon, out ICONINFO info)
        {
            using var asApp = DisplayDpi.AsApplication ();
            return NativeGetIconInfo (hicon, out info);
        }
        [DllImport ("user32.dll", EntryPoint = "GetIconInfo")]
        private static extern bool NativeGetIconInfo (IntPtr hicon, out ICONINFO info);
        [DllImport ("user32.dll")]
        private static extern IntPtr CreateIconIndirect (ref ICONINFO info);
        [DllImport ("user32.dll", EntryPoint = "DestroyIcon")]
        private static extern bool NativeDestroyIcon (IntPtr hicon);
        [DllImport ("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage (IntPtr hinst, string name, uint type, int cx, int cy, uint flags);
    }
}
