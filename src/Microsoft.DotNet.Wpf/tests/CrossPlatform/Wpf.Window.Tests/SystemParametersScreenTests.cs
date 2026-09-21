// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SystemParameters describes the screens that are attached.
//
// These come through UnsafeNativeMethods.GetSystemMetrics, which off Windows used to answer with a
// flat 1920x1080 desktop containing one monitor at the origin -- so PrimaryScreenWidth,
// FullPrimaryScreenWidth, MaximizedPrimaryScreenWidth and the whole VirtualScreen family described a
// screen nobody had. They are public API and reachable from XAML through SystemResourceKey, so an
// application can lay itself out against them.
//
// Asserted against what the head reports rather than against numbers: this has to hold on any
// machine, at any resolution, with any number of displays.
//

using System;
using System.Windows;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Window.Tests
{
    public class SystemParametersScreenTests
    {
        private static bool DisplayAvailable =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        /// <summary>
        /// The primary screen is the one the head describes, not a remembered default.
        /// </summary>
        [Fact]
        public void ThePrimaryScreenIsTheOneThatIsAttached()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");
            Assert.SkipUnless(
                PlatformWindow.GetPrimaryScreenPixels(
                    out int ml, out int mt, out int mr, out int mb,
                    out int wl, out int wt, out int wr, out int wb),
                "the head does not report screen bounds");

            // SystemParameters converts device pixels to DIPs, so compare as a ratio rather than
            // expecting the two to be numerically equal on a scaled display.
            double widthDips = UiThread.Invoke(() => SystemParameters.PrimaryScreenWidth);
            double heightDips = UiThread.Invoke(() => SystemParameters.PrimaryScreenHeight);
            Assert.True(widthDips > 0 && heightDips > 0, "the primary screen has no size");

            double aspect = (double)(mr - ml) / (mb - mt);
            double reportedAspect = widthDips / heightDips;
            Assert.True(Math.Abs(aspect - reportedAspect) < 0.02,
                $"the head reports a {mr - ml}x{mb - mt}px primary screen (aspect {aspect:0.000}) but "
                + $"SystemParameters describes {widthDips:0}x{heightDips:0} DIPs (aspect {reportedAspect:0.000})");

            // The full-screen and maximized metrics are the WORK area, so they cannot exceed the
            // monitor and must not be the whole of it when something is reserved (a menu bar, a
            // taskbar, a panel).
            double fullHeight = UiThread.Invoke(() => SystemParameters.FullPrimaryScreenHeight);
            Assert.True(fullHeight > 0 && fullHeight <= heightDips + 1,
                $"the full-screen height {fullHeight:0} is not within the screen height {heightDips:0}");

            Assert.True(wb - wt <= mb - mt, "the head's own work area is taller than its monitor");
        }

        /// <summary>
        /// The virtual screen covers every display. Its ORIGIN is the part that used to be wrong: a
        /// hardcoded zero, which is only right when no display sits left of or above the primary.
        /// </summary>
        [Fact]
        public void TheVirtualScreenCoversEveryDisplay()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");
            Assert.SkipUnless(
                PlatformWindow.GetVirtualScreenPixels(out int vl, out int vt, out int vw, out int vh),
                "the head does not report a virtual screen");

            Assert.True(vw > 0 && vh > 0, $"the virtual screen is {vw}x{vh}");

            // Every display has to fit inside it -- that is what makes it the bounding box.
            int count = PlatformWindow.GetMonitorCount();
            for (int i = 0; i < count; i++)
            {
                if (!PlatformWindow.GetMonitorPixels(i, out int ml, out int mt, out int mr, out int mb,
                                                     out _, out _, out _, out _, out _))
                {
                    continue;
                }

                Assert.True(ml >= vl && mt >= vt && mr <= vl + vw && mb <= vt + vh,
                    $"display {i} ({ml},{mt})-({mr},{mb}) is not inside the virtual screen "
                    + $"({vl},{vt}) {vw}x{vh}");
            }

            // And it is at least as big as the primary, which is the cheapest way to catch a virtual
            // screen that has quietly become one default-sized rectangle again.
            PlatformWindow.GetPrimaryScreenPixels(out int pl, out int pt, out int pr, out int pb,
                                                  out _, out _, out _, out _);
            Assert.True(vw >= pr - pl && vh >= pb - pt,
                $"the virtual screen {vw}x{vh} is smaller than the primary display {pr - pl}x{pb - pt}");

            double reported = UiThread.Invoke(() => SystemParameters.VirtualScreenWidth);
            Assert.True(reported > 0, "SystemParameters.VirtualScreenWidth is zero");
        }

        /// <summary>SystemParameters agrees with the head about how many displays there are.</summary>
        [Fact]
        public void TheDisplayCountIsReported()
        {
            Assert.SkipUnless(DisplayAvailable, "requires a display server");

            int heads = PlatformWindow.GetMonitorCount();
            Assert.True(heads >= 1, $"the head reports {heads} displays");

            // MonitorCount is not a SystemParameters property, so this reads the metric the same way
            // WPF does. A fabricated single monitor answers 1 however many are plugged in.
            int metric = MS.Win32.UnsafeNativeMethods.GetSystemMetrics(SM.CMONITORS);
            Assert.True(metric == heads,
                $"SM_CMONITORS says {metric} displays, the head says {heads}");
        }
    }
}
