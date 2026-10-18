// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window that animates every frame draws at the display's rate. Stock WPF paces its frames by the
// render thread's presentation notifications (DWM vsync); this compositor has none, so the wait for
// the rest of a refresh period was a DispatcherTimer -- whose deadlines are whole 15.6-ms system
// ticks on Windows -- and WPF drew at 50-55 frames a second on a 100-Hz display.
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

            Assert.True(paced < ticked - 1.0 && paced < period * 1.5,
                $"median frame interval {paced:0.0} ms paced, {ticked:0.0} ms on the system tick, on a {hz}-Hz display ({period:0.0} ms)");
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
