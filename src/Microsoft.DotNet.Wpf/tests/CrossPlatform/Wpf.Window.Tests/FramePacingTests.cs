// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window that animates every frame draws at the display's rate. Stock WPF paces its frames by the
// render thread's presentation notifications (DWM vsync); this compositor has none, so the wait for
// the rest of a refresh period was a DispatcherTimer -- whose deadlines are whole 15.6-ms system
// ticks on Windows -- and WPF drew at 50-55 frames a second on a 100-Hz display; and a wait measured
// from each frame's start added every wake-up's lateness to the period (87 a second, stock 99).
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Window.Tests
{
    public class FramePacingTests
    {
        [Fact]
        public void AnAnimatingWindow_DrawsAtTheDisplaysRate_NotTheSystemTick()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the display rate read from Windows");
            int hz = DisplayHz();
            Assert.SkipUnless(hz >= 75, $"a display of 75 Hz or more tells a 15.6-ms tick from a frame (this one is {hz} Hz)");
            // Only a display that is refreshing has frames to pace by. With the monitor off, or the
            // session's console not shown (a locked or minimised virtual machine), the adapter raises
            // no vertical blank and DWM composes a few times a second: presents then block on
            // whatever the driver does instead, and the median says nothing about the pacing.
            Assert.SkipUnless(DisplayIsRefreshing(), "the display is not refreshing (no vertical blank: monitor off, or the console not shown)");
            double period = 1000.0 / hz;

            // The same animation paced both ways, back to back, so the machine's own timing noise
            // (a virtual machine's above all) lands on both: the tick-paced one is the baseline.
            FieldInfo enabled = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.FramePacer")!
                .GetField("Enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
            object was = enabled.GetValue(null)!;
            double ticked, paced;
            try
            {
                enabled.SetValue(null, false);
                ticked = MedianFrameInterval();
                enabled.SetValue(null, true);
                paced = MedianFrameInterval();
            }
            finally { enabled.SetValue(null, was); }

            Assert.True(paced < ticked - 1.0 && paced < period * 1.1,
                $"median frame interval {paced:0.0} ms paced, {ticked:0.0} ms on the system tick, on a {hz}-Hz display ({period:0.0} ms)");
        }

        /// <summary>The beat itself, without a clock: renders that each start a little after they were
        /// due (a wake-up's lateness) still come one refresh period apart on average. Measured from each
        /// start instead, every lateness is added to the period -- 87 frames a second for 99.</summary>
        [Fact]
        public void LateWakeUps_DoNotAddUpAcrossFrames()
        {
            MethodInfo next = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext")!
                .GetMethod("NextFrameDue", BindingFlags.NonPublic | BindingFlags.Static)!;
            const long period = 100_000;       // 10 ms in 100-ns ticks
            var rnd = new Random(5);
            long due = 0, start = 1_000_000, first = start;
            const int frames = 200;
            for (int i = 0; i < frames; i++)
            {
                due = (long)next.Invoke(null, new object[] { due, start, period })!;
                start = due + 5_000 + rnd.Next(10_000);   // the next render starts 0.5-1.5 ms late
            }
            double mean = (start - first) / (double)frames;
            Assert.True(Math.Abs(mean - period) < period * 0.02, $"mean frame interval {mean / 10_000:0.00} ms for a {period / 10_000:0.0}-ms period");

            // A render a period or more late (a long frame) starts a new beat rather than rushing to catch up.
            long d = (long)next.Invoke(null, new object[] { 1_000_000L, 1_000_000L + 3 * period, period })!;
            Assert.Equal(1_000_000L + 4 * period, d);
        }

        private static double MedianFrameInterval()
        {
            var stamps = new List<double>();
            System.Windows.Window? w = null;
            EventHandler? onRendering = null;
            var clock = Stopwatch.StartNew();
            UiThread.Invoke(() =>
            {
                // Off the desktop: nothing appears on screen.
                w = new System.Windows.Window
                {
                    Title = "FramePacingTest", Width = 300, Height = 200, Left = 8000, Top = 100,
                    WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                    Background = Brushes.White,
                };
                int n = 0;
                onRendering = (s, e) =>
                {
                    n++;
                    w.Background = new SolidColorBrush(Color.FromRgb((byte)n, (byte)(n * 3), 200));
                    if (clock.ElapsedMilliseconds > 1000 && stamps.Count < 151) stamps.Add(clock.Elapsed.TotalMilliseconds);
                };
                CompositionTarget.Rendering += onRendering;
                w.Show();
            });
            try
            {
                Assert.True(UiThread.WaitUntil(() => stamps.Count >= 151, 15000), $"only {stamps.Count} frames");
            }
            finally
            {
                UiThread.Invoke(() => { CompositionTarget.Rendering -= onRendering; w?.Close(); });
            }
            List<double> intervals = stamps.Zip(stamps.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
            double median = intervals[intervals.Count / 2];
            Console.WriteLine($"FramePacingTests: median {median:0.00} ms");
            return median;
        }

        /// <summary>Whether the primary display's adapter raises vertical blanks: ten waits succeed and
        /// take at least a few milliseconds (a failing wait returns at once).</summary>
        private static bool DisplayIsRefreshing()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            var open = new D3DKMT_OPENADAPTERFROMHDC { hDc = dc };
            try
            {
                if (D3DKMTOpenAdapterFromHdc(ref open) != 0) return false;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            try
            {
                var wait = new D3DKMT_WAITFORVERTICALBLANKEVENT { hAdapter = open.hAdapter, VidPnSourceId = open.VidPnSourceId };
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++)
                    if (D3DKMTWaitForVerticalBlankEvent(ref wait) != 0) return false;
                return clock.Elapsed.TotalMilliseconds >= 10 * 2.0;   // faster than 500 Hz is no vblank
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_OPENADAPTERFROMHDC { public IntPtr hDc; public uint hAdapter; public long AdapterLuid; public uint VidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_WAITFORVERTICALBLANKEVENT { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
        [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
        [DllImport("gdi32")] private static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC open);
        [DllImport("gdi32")] private static extern int D3DKMTWaitForVerticalBlankEvent(ref D3DKMT_WAITFORVERTICALBLANKEVENT wait);
        [DllImport("gdi32")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);
        [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        private static int DisplayHz()
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettingsW(null, -1 /* ENUM_CURRENT_SETTINGS */, ref mode) ? mode.dmDisplayFrequency : 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE devMode);
    }
}
