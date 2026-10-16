// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A VisualBrush or DrawingBrush on screen follows its source: when the source grows or changes, the
// brush shows it in the next frame. A live brush source is rendered to a texture and kept across
// frames while the source is unchanged, so this is what proves the cache lets go of a stale picture.
// (GraphWorX draws the element being created or dragged through such a brush.)
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
    public class BrushSourceChangeTests
    {
        [Fact]
        public void AVisualBrushWhoseSourceGrows_ShowsItAtItsNewSize() => Run(drawing: false);

        [Fact]
        public void ADrawingBrushWhoseDrawingGrows_ShowsItAtItsNewSize() => Run(drawing: true);

        private static void Run(bool drawing)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the window back with PrintWindow");
            System.Windows.Window? window = null;
            Rectangle? growing = null;
            RectangleGeometry? growingGeometry = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    Brush brush;
                    if (drawing)
                    {
                        // A drawing as large as the viewbox (a transparent frame) with a red rectangle in it.
                        growingGeometry = new RectangleGeometry(new Rect(0, 0, 4, 4));
                        var group = new DrawingGroup();
                        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 50))));
                        group.Children.Add(new GeometryDrawing(Brushes.Red, null, growingGeometry));
                        brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
                    }
                    else
                    {
                        var canvas = new Canvas { Width = 100, Height = 50, Background = Brushes.Transparent };
                        growing = new Rectangle { Fill = Brushes.Red, Width = 4, Height = 4 };
                        canvas.Children.Add(growing);
                        brush = new VisualBrush(canvas)
                        {
                            Viewbox = new Rect(0, 0, 100, 50), ViewboxUnits = BrushMappingMode.Absolute,
                            Viewport = new Rect(0, 0, 1, 1), ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                            TileMode = TileMode.None, Stretch = Stretch.Fill,
                        };
                        // The source has to be laid out to be drawn: give it a home off to the side.
                        var holder = new Canvas();
                        holder.Children.Add(canvas);
                        Canvas.SetLeft(canvas, -1000);
                        var root0 = new Grid();
                        root0.Children.Add(holder);
                        root0.Children.Add(new Rectangle { Fill = brush, Width = 200, Height = 100 });
                        return Show(root0);
                    }
                    var root = new Grid();
                    root.Children.Add(new Rectangle { Fill = brush, Width = 200, Height = 100 });
                    return Show(root);
                });
                IntPtr hwnd = UiThread.Invoke(() => new System.Windows.Interop.WindowInteropHelper(window!).Handle);

                // Small: the red corner only. The far corner of the target is white (the window).
                Assert.True(UiThread.WaitUntil(() => IsRed(Sample(hwnd, 0.01, 0.02)) && !IsRed(Sample(hwnd, 0.9, 0.9)), 5000),
                    "the brush never showed its small source");

                // Grown to the whole source: the far corner turns red too, within a few frames.
                UiThread.Invoke(() =>
                {
                    if (drawing) growingGeometry!.Rect = new Rect(0, 0, 100, 50);
                    else { growing!.Width = 100; growing.Height = 50; }
                });
                uint after = 0;
                Assert.True(UiThread.WaitUntil(() => IsRed(after = Sample(hwnd, 0.9, 0.9)), 5000),
                    $"after the source grew, the brush still showed the old picture (0x{after:x6})");
            }
            finally
            {
                if (window is not null) UiThread.Invoke(() => { try { window.Close(); } catch { } });
            }
        }

        private static System.Windows.Window Show(UIElement content)
        {
            // Off screen, not activated: nothing appears on the user's desktop.
            var w = new System.Windows.Window
            {
                Title = "Wpf.Window.Tests brushsource",
                Left = 8000, Top = 100, Width = 300, Height = 200,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false,
                Background = Brushes.White, Content = content,
            };
            w.Show();
            return w;
        }

        private static bool IsRed(uint rgb) => ((rgb >> 16) & 0xFF) > 200 && ((rgb >> 8) & 0xFF) < 60 && (rgb & 0xFF) < 60;

        /// <summary>The RGB at a point of the client area given as fractions of its size, by PrintWindow.
        /// The 200x100 target is centred in the client, so (0.5, 0.5) is its centre.</summary>
        private static unsafe uint Sample(IntPtr hwnd, double fx, double fy)
        {
            GetClientRect(hwnd, out RECT cr);
            int w = cr.Right, h = cr.Bottom;
            if (w <= 0 || h <= 0) return 0;
            // Map the fraction of the TARGET (200x100 centred) to client pixels.
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int tx = (int)(w / 2 + (fx - 0.5) * 200 * scale), ty = (int)(h / 2 + (fy - 0.5) * 100 * scale);
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
