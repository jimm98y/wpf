// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A real Window, asked through the Win32 shims which display it is on.
//
// Wpf.Platform.Tests asserts the enumeration; this asserts the CHAIN WPF actually uses --
// SafeNativeMethods.MonitorFromWindow then GetMonitorInfo -- because that is where the answer was
// fabricated. MonitorFromWindow returned one fixed handle and GetMonitorInfo ignored it and
// described the primary, so a window on a second display was told the primary's bounds, work area
// and DPI. Window.CalculateWindowLocation, TransformWorkAreaScreenArea and popup clamping all read
// this pair.
//

using System;
using System.Windows;
using MS.Internal.Interop;
using MS.Win32;
using Xunit;

namespace Wpf.Window.Tests
{
    public class MonitorFromWindowTests
    {
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>
        /// A window on a NON-PRIMARY display is told about that display.
        /// </summary>
        /// <remarks>
        /// It has to be a non-primary one. A window on the primary is on display 0, and a fabricated
        /// single monitor answers 0 for everything -- so the same assertion against a freshly shown
        /// window passes whether the bug is present or not. Verified the hard way: it did.
        ///
        /// The window is walked onto another display rather than placed there in one go. Left and Top
        /// are DIPs and the displays are described in pixels, and the factor between them belongs to
        /// whichever display the window is on -- which is the one it is trying to leave. So each
        /// attempt converts using the scale the window reports NOW, moves, and measures again; a move
        /// that crosses onto a display of a different DPI simply corrects on the next pass. Nothing
        /// here assumes how many displays there are, where they sit, or what they are scaled by.
        /// </remarks>
        [Fact]
        public void AWindowOnASecondDisplayIsToldAboutThatDisplay()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                "requires a head that enumerates displays");

            int count = PlatformWindow.GetMonitorCount();
            Assert.SkipWhen(count < 2, "only one display is attached");

            System.Windows.Window? window = null;
            try
            {
                window = UiThread.Invoke(() =>
                {
                    var w = new System.Windows.Window
                    {
                        Title = "Wpf.Window.Tests monitor",
                        Width = 380,
                        Height = 280,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                    };
                    w.Show();
                    return w;
                });
                UiThread.WaitUntil(() => false, 400);

                int landedOn = -1;
                for (int i = 1; i < count && landedOn < 0; i++)
                {
                    if (WalkOnto(window!, i)) landedOn = i;
                }

                Assert.SkipWhen(landedOn < 0, "could not place a window on a non-primary display");

                (int x, int y) = OriginOf(window!);
                PlatformWindow.GetMonitorPixels(landedOn, out int el, out int et, out int er, out int eb,
                                                out _, out _, out _, out _, out _);

                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX)),
                };
                IntPtr monitor = UiThread.Invoke(() => SafeNativeMethods.MonitorFromWindow(
                    new System.Runtime.InteropServices.HandleRef(
                        null, new System.Windows.Interop.WindowInteropHelper(window!).Handle),
                    MONITOR_DEFAULTTONEAREST));
                SafeNativeMethods.GetMonitorInfo(
                    new System.Runtime.InteropServices.HandleRef(null, monitor), info);

                Assert.True(
                    info.rcMonitor.left == el && info.rcMonitor.top == et
                    && info.rcMonitor.right == er && info.rcMonitor.bottom == eb,
                    $"the window at ({x},{y}) is on display {landedOn}, ({el},{et})-({er},{eb}), but the "
                    + $"shims described ({info.rcMonitor.left},{info.rcMonitor.top})-"
                    + $"({info.rcMonitor.right},{info.rcMonitor.bottom}).");

                Assert.False((info.dwFlags & 1) != 0,
                    "a non-primary display was flagged MONITORINFOF_PRIMARY");
            }
            finally
            {
                if (window is not null)
                {
                    UiThread.Invoke(() => { try { window.Close(); } catch { } });
                }
            }
        }

        /// <summary>
        /// Moves the window until it is on <paramref name="target"/>, or gives up.
        /// </summary>
        /// <remarks>
        /// Iterative because the DIP-to-pixel factor changes underneath the move: it is read from the
        /// window's CURRENT display each pass, so landing on a display of a different scale is simply
        /// corrected next time round rather than being something this test has to know in advance.
        /// </remarks>
        private static bool WalkOnto(System.Windows.Window window, int target)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (MonitorOf(window) == target) return true;

                if (!PlatformWindow.GetMonitorPixels(target, out int ml, out int mt, out int mr, out int mb,
                                                     out _, out _, out _, out _, out _))
                {
                    return false;
                }

                double scale = UiThread.Invoke(() =>
                {
                    IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    double s = PlatformWindow.FromHandle(handle).GetBackingScale();
                    return s > 0 ? s : 1.0;
                });

                double cx = (ml + (mr - ml) / 2.0) / scale;
                double cy = (mt + (mb - mt) / 2.0) / scale;
                UiThread.Invoke(() => { window.Left = cx; window.Top = cy; });
                UiThread.WaitUntil(() => false, 350);
            }

            return MonitorOf(window) == target;
        }

        private static int MonitorOf(System.Windows.Window window)
        {
            (int x, int y) = OriginOf(window);
            return PlatformWindow.MonitorIndexFromPointPixels(x, y);
        }

        private static (int X, int Y) OriginOf(System.Windows.Window window) => UiThread.Invoke(() =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            PlatformWindow.FromHandle(handle).GetWindowScreenOriginPixels(out int sx, out int sy);
            return (sx, sy);
        });
    }
}
