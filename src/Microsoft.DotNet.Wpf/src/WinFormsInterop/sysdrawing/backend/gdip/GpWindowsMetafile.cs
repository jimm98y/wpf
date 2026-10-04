// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The only GDI a metafile touches, and only on Windows, and only for what exists only there: an
// HENHMETAFILE or HMETAFILE handed in or asked for, and a reference HDC whose metrics a recording
// takes. The metafile itself is bytes everywhere; these calls only move bytes in and out of a GDI
// handle and read a device's caps. Elsewhere a handle cannot exist, and the callers throw
// PlatformNotSupportedException before reaching here.
//

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>The metrics of a recording's reference device, as GetDeviceCaps reports them.</summary>
    internal struct GpRefDevice
    {
        public int HorzRes, VertRes;       // HORZRES / VERTRES: the device in pixels
        public int HorzSize, VertSize;     // HORZSIZE / VERTSIZE: the device in millimetres
        public int LogPixelsX, LogPixelsY; // LOGPIXELSX / LOGPIXELSY
        public bool IsDisplay;             // TECHNOLOGY == DT_RASDISPLAY
        public float DesktopDpiX, DesktopDpiY;

        /// <summary>No HDC: a 96-dpi display, the same on every platform (1920 x 1440 pixels on a
        /// 508 x 381 mm screen, which is exactly 96 pixels an inch both ways).</summary>
        public static GpRefDevice Default => new GpRefDevice
        {
            HorzRes = 1920, VertRes = 1440, HorzSize = 508, VertSize = 381,
            LogPixelsX = 96, LogPixelsY = 96, IsDisplay = true, DesktopDpiX = 96f, DesktopDpiY = 96f,
        };
    }

    [SupportedOSPlatform("windows")]
    internal static class GpWindowsMetafile
    {
        [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr hemf, uint cb, byte[] data);
        [DllImport("gdi32.dll")] static extern uint GetMetaFileBitsEx(IntPtr hmf, uint cb, byte[] data);
        [DllImport("gdi32.dll")] static extern IntPtr SetEnhMetaFileBits(uint cb, byte[] data);
        [DllImport("gdi32.dll")] static extern IntPtr SetMetaFileBitsEx(uint cb, byte[] data);
        [DllImport("gdi32.dll")] static extern int DeleteEnhMetaFile(IntPtr hemf);
        [DllImport("gdi32.dll")] static extern int DeleteMetaFile(IntPtr hmf);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int GetObjectType(IntPtr h);

        const int OBJ_METAFILE = 9, OBJ_ENHMETAFILE = 13;

        public static byte[] EnhMetaFileBits(IntPtr hemf)
        {
            if (hemf == IntPtr.Zero || GetObjectType(hemf) != OBJ_ENHMETAFILE) return null;
            uint n = GetEnhMetaFileBits(hemf, 0, null);
            if (n == 0) return null;
            var b = new byte[n];
            return GetEnhMetaFileBits(hemf, n, b) == n ? b : null;
        }

        public static byte[] MetaFileBits(IntPtr hmf)
        {
            if (hmf == IntPtr.Zero || GetObjectType(hmf) != OBJ_METAFILE) return null;
            uint n = GetMetaFileBitsEx(hmf, 0, null);
            if (n == 0) return null;
            var b = new byte[n];
            return GetMetaFileBitsEx(hmf, n, b) == n ? b : null;
        }

        public static IntPtr ToHenhmetafile(byte[] emf) => SetEnhMetaFileBits((uint)emf.Length, emf);
        public static IntPtr ToHmetafile(byte[] wmf) => SetMetaFileBitsEx((uint)wmf.Length, wmf);
        public static void DeleteEmf(IntPtr h) { if (h != IntPtr.Zero) DeleteEnhMetaFile(h); }
        public static void DeleteWmf(IntPtr h) { if (h != IntPtr.Zero) DeleteMetaFile(h); }

        /// <summary>A reference DC's metrics (GetEmfDpi / InitForRecording read these four), and
        /// the desktop's logical dpi, which is what a metafile's Graphics reports.</summary>
        public static GpRefDevice Device(IntPtr hdc)
        {
            var d = new GpRefDevice
            {
                HorzSize = GetDeviceCaps(hdc, 4), VertSize = GetDeviceCaps(hdc, 6),
                HorzRes = GetDeviceCaps(hdc, 8), VertRes = GetDeviceCaps(hdc, 10),
                LogPixelsX = GetDeviceCaps(hdc, 88), LogPixelsY = GetDeviceCaps(hdc, 90),
                IsDisplay = GetDeviceCaps(hdc, 2) == 1,
            };
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                d.DesktopDpiX = GetDeviceCaps(screen, 88);
                d.DesktopDpiY = GetDeviceCaps(screen, 90);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            if (d.DesktopDpiX <= 0) d.DesktopDpiX = 96f;
            if (d.DesktopDpiY <= 0) d.DesktopDpiY = 96f;
            return d;
        }
    }
}
