// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A path geometry draws under its own Transform. The compositor dropped that transform for path
// geometries (StreamGeometry included) while honouring it for groups and combined geometries -- and a
// Shape with Stretch draws its geometry with the stretch AS that transform. So a 12x12 Path,
// Stretch=Uniform, over data 2000 units across drew at 2000 px: app's column-filter funnel
// covered the whole header row of the Fault Viewer.
//

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Xunit;

namespace Wpf.Window.Tests
{
    public class GeometryTransformTests
    {
        // app's funnel glyph, about 1900 x 1980 units.
        private const string Funnel = "M.4181.3086l80 102q64 80 132 174t126 178h2q60 82 116 158 56 78 110 154l108 156q56 78 116 158l6 895.9999 310-255.9999v-640q62-84.0001 120-164 60-80 116-156 56.0001-78 112.0001-152 56-76 110-152 68.0001-94 128.0001-180 62-86 130-176l74-100z";

        [Fact]
        public void AStretchedPathOverStreamGeometry_DrawsAtItsOwnSize() =>
            Run(() => Geometry.Parse(Funnel), stretch: true);

        [Fact]
        public void AStretchedPathOverPathGeometry_DrawsAtItsOwnSize() =>
            Run(() => PathGeometry.CreateFromGeometry(Geometry.Parse("M0,0 L2000,0 L1000,2000 Z")), stretch: true);

        [Fact]
        public void APathGeometryWithItsOwnTransform_DrawsTransformed()
        {
            // No Stretch: the geometry's own Transform does the scaling, applied once.
            ScaleTransform? scale = null;
            Run(() =>
            {
                var g = PathGeometry.CreateFromGeometry(Geometry.Parse("M0,0 L2000,0 L2000,2000 L0,2000 Z"));
                g.Transform = scale = new ScaleTransform(0.006, 0.006);
                return g;
            }, stretch: false, grow: () => { scale!.ScaleX = 0.1; scale.ScaleY = 0.05; });
        }

        /// <summary>A red 12x12 (or 12-unit) shape at the target's top-left corner of a white 200x100
        /// target: its own corner is red, the far corner stays white. With <paramref name="grow"/>, the
        /// transform then changes in place and the shape must follow it to the far corner.</summary>
        private static void Run(Func<Geometry> geometry, bool stretch, Action? grow = null)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the window back with PrintWindow");
            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var path = new Path
                    {
                        Data = geometry(), Fill = Brushes.Red,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    };
                    if (stretch) { path.Width = 12; path.Height = 12; path.Stretch = Stretch.Uniform; }
                    var target = new Grid { Width = 200, Height = 100, Background = Brushes.White, ClipToBounds = true };
                    target.Children.Add(path);
                    var w = new System.Windows.Window
                    {
                        // Off screen, not activated: nothing appears on the user's desktop.
                        Title = "Wpf.Window.Tests geometrytransform",
                        Left = 8000, Top = 100, Width = 300, Height = 200,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        ShowActivated = false, ShowInTaskbar = false,
                        Background = Brushes.White, Content = target,
                    };
                    w.Show();
                    return w;
                });
                IntPtr hwnd = UiThread.Invoke(() => new System.Windows.Interop.WindowInteropHelper(window!).Handle);

                uint near = 0, far = 0;
                Assert.True(UiThread.WaitUntil(() => IsRed(near = Sample(hwnd, 4, 3)) && !IsRed(far = Sample(hwnd, 180, 90)), 5000),
                    $"the shape's corner is 0x{near:x6} and the target's far corner 0x{far:x6}: the shape did not draw at its own size");

                if (grow is not null)
                {
                    UiThread.Invoke(grow);
                    Assert.True(UiThread.WaitUntil(() => IsRed(far = Sample(hwnd, 180, 90)), 5000),
                        $"after its transform changed the shape still drew at the old size (far corner 0x{far:x6})");
                }
            }
            finally
            {
                if (window is not null) UiThread.Invoke(() => { try { window.Close(); } catch { } });
            }
        }

        private static bool IsRed(uint rgb) => ((rgb >> 16) & 0xFF) > 200 && ((rgb >> 8) & 0xFF) < 60 && (rgb & 0xFF) < 60;

        /// <summary>The RGB at (x, y) DIPs from the top-left of the 200x100 target centred in the client.</summary>
        private static unsafe uint Sample(IntPtr hwnd, double x, double y)
        {
            GetClientRect(hwnd, out RECT cr);
            int w = cr.Right, h = cr.Bottom;
            if (w <= 0 || h <= 0) return 0;
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int tx = (int)(w / 2 + (x - 100) * scale), ty = (int)(h / 2 + (y - 50) * scale);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bm = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(dc, bm);
            try
            {
                PrintWindow(hwnd, dc, 1 /* PW_CLIENTONLY */);
                GdiFlush();
                return ((uint*)bits)[Math.Clamp(ty, 0, h - 1) * w + Math.Clamp(tx, 0, w - 1)] & 0xFFFFFF;
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
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
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
