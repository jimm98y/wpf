// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The screen's pixels, as GetPixel and a BitBlt from the screen DC read them on Windows: what an eye
// dropper samples (app's colour picker reads GetPixel(GetDC(NULL), x, y) under the pointer), and
// what a magnifier copies. Win32's answer was CLR_INVALID everywhere else, and every colour picked was
// none at all.
//
//   Windows  the desktop, through GDI: a BitBlt from the screen DC, as GetPixel reads it there
//   browser  the screen is the page: its canvases, as stacked, read back (browser-window.js capturePage)
//   macOS    CGWindowListCreateImage, which an application may call for its own windows without the
//            Screen Recording permission (other applications' windows come back as the desktop behind)
//
// One call, the same answer everywhere: what is on the screen in that rectangle.
//
// Coordinates are the screen's device pixels from the primary screen's top-left corner, as
// GetCursorPos reports them on these heads; the answer is BGRA, top row first.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;

namespace MS.Internal.Interop
{
    public static partial class ScreenPixels
    {
        /// <summary>The screen's pixels in the rectangle, BGRA top row first; null where they cannot be read.</summary>
        public static byte[] Capture(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            try
            {
                if (OperatingSystem.IsWindows()) return CaptureWindows(x, y, width, height);
                if (OperatingSystem.IsBrowser()) return CaptureBrowser(x, y, width, height);
                if (OperatingSystem.IsMacOS()) return CaptureMac(x, y, width, height);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
            return null;
        }

        /// <summary>One pixel of the screen as a COLORREF (0x00BBGGRR); -1 (CLR_INVALID) where it cannot be read.</summary>
        public static uint GetPixel(int x, int y)
        {
            byte[] bgra = Capture(x, y, 1, 1);
            return bgra == null || bgra.Length < 4 ? 0xFFFFFFFF : (uint)(bgra[2] | bgra[1] << 8 | bgra[0] << 16);
        }

        private static byte[] CaptureBrowser(int x, int y, int width, int height)
        {
            string base64 = Js.CapturePage(x, y, width, height);
            if (string.IsNullOrEmpty(base64)) return null;
            byte[] bytes = Convert.FromBase64String(base64);
            return bytes.Length == width * height * 4 ? bytes : null;
        }

        private static partial class Js
        {
            [JSImport("capturePage", "wpfBrowserWindow")]
            internal static partial string CapturePage(int x, int y, int width, int height);
        }

        // ---- Windows --------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        private static byte[] CaptureWindows(int x, int y, int width, int height)
        {
            const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
            IntPtr old = SelectObject(memory, bitmap);
            try
            {
                // CAPTUREBLT: layered windows too, as the user sees them
                if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY | CAPTUREBLT)) return null;
                SelectObject(memory, old);
                var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
                var pixels = new byte[width * height * 4];
                return GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == height ? pixels : null;
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---- macOS ----------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double X, Y, Width, Height; }

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const uint kCGWindowListOptionOnScreenOnly = 1, kCGNullWindowID = 0, kCGWindowImageDefault = 0;
        private const uint kCGImageAlphaPremultipliedFirst = 2, kCGBitmapByteOrder32Little = 2 << 12;

        private delegate IntPtr WindowListCreateImage(CGRect bounds, uint options, uint window, uint imageOptions);
        private static WindowListCreateImage s_createImage;
        private static bool s_looked;

        [DllImport(CoreGraphics)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
        [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(IntPtr space);
        [DllImport(CoreGraphics)] private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr space, uint bitmapInfo);
        [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
        [DllImport(CoreGraphics)] private static extern void CGContextRelease(IntPtr context);
        [DllImport(CoreGraphics)] private static extern void CGImageRelease(IntPtr image);

        private static unsafe byte[] CaptureMac(int x, int y, int width, int height)
        {
            // CGWindowListCreateImage is deprecated in favour of ScreenCaptureKit, which has no synchronous
            // call and asks for the Screen Recording permission even for the app's own windows; found by
            // name so that a system without it answers null rather than failing to bind
            if (!s_looked)
            {
                s_looked = true;
                if (NativeLibrary.TryLoad(CoreGraphics, out IntPtr lib) && NativeLibrary.TryGetExport(lib, "CGWindowListCreateImage", out IntPtr fn))
                    s_createImage = Marshal.GetDelegateForFunctionPointer<WindowListCreateImage>(fn);
            }
            if (s_createImage == null) return null;

            // the screen's points, from its device pixels
            double scale = CocoaWindow.PrimaryBackingScale();
            var bounds = new CGRect { X = x / scale, Y = y / scale, Width = width / scale, Height = height / scale };
            IntPtr image = s_createImage(bounds, kCGWindowListOptionOnScreenOnly, kCGNullWindowID, kCGWindowImageDefault);
            if (image == IntPtr.Zero) return null;
            var pixels = new byte[width * height * 4];
            IntPtr space = CGColorSpaceCreateDeviceRGB();
            try
            {
                fixed (byte* p = pixels)
                {
                    IntPtr context = CGBitmapContextCreate((IntPtr)p, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little);
                    if (context == IntPtr.Zero) return null;
                    // drawn at the size asked for: one pixel each, whatever the display's backing scale
                    CGContextDrawImage(context, new CGRect { Width = width, Height = height }, image);
                    CGContextRelease(context);
                }
            }
            finally
            {
                CGColorSpaceRelease(space);
                CGImageRelease(image);
            }
            return pixels;
        }
    }
}
