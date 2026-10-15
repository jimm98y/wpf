// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrintWindow of a WPF window gets what the window shows.
//
// A managed-composition window presents through a GPU swap chain, which GDI cannot read: PrintWindow
// (and anything else that captures through WM_PRINT) got a black rectangle. HwndTarget now answers
// WM_PRINTCLIENT with the composed frame, read back from the backend.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Window.Tests
{
    public class PrintWindowTests
    {
        [Theory]
        [InlineData(0u)]                // WM_PRINT through DefWindowProc
        [InlineData(1u)]                // PW_CLIENTONLY
        [InlineData(2u)]                // PW_RENDERFULLCONTENT
        public void PrintWindow_CapturesTheComposedFrame(uint flags)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "PrintWindow is a Win32 API");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    // Off screen, not activated: nothing appears on the user's desktop.
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests printwindow",
                        Left = 8000, Top = 100, Width = 300, Height = 200,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        ShowActivated = false, ShowInTaskbar = false,
                        Background = new SolidColorBrush(Color.FromRgb(0x20, 0xA0, 0x40)),
                    };
                    w.Show();
                    return w;
                });
                IntPtr hwnd = UiThread.Invoke(() => new System.Windows.Interop.WindowInteropHelper(window!).Handle);

                uint centre = 0;
                bool green = UiThread.WaitUntil(() =>
                {
                    centre = CaptureCentre(hwnd, flags);
                    return centre == 0x20A040;
                }, 5000);
                Assert.True(green, $"PrintWindow(flags {flags}) read 0x{centre:x6} at the client centre, not the window's 0x20a040 background");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        /// <summary>PrintWindow into a 32bpp DIB the window's size; the RGB at its client centre.</summary>
        private static unsafe uint CaptureCentre(IntPtr hwnd, uint flags)
        {
            GetWindowRect(hwnd, out RECT wr);
            GetClientRect(hwnd, out RECT cr);
            var origin = new POINT();
            ClientToScreen(hwnd, ref origin);
            bool clientOnly = (flags & 1) != 0;
            int w = clientOnly ? cr.Right : wr.Right - wr.Left, h = clientOnly ? cr.Bottom : wr.Bottom - wr.Top;
            int cx = (clientOnly ? 0 : origin.X - wr.Left) + cr.Right / 2;
            int cy = (clientOnly ? 0 : origin.Y - wr.Top) + cr.Bottom / 2;

            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bm = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(dc, bm);
            try
            {
                PrintWindow(hwnd, dc, flags);
                GdiFlush();
                return ((uint*)bits)[cy * w + cx] & 0xFFFFFF;
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(bm);
                DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
