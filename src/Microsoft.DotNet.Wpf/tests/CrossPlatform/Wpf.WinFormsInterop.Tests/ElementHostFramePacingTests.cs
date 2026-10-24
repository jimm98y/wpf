// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF content in an ElementHost animates at the display's rate. Its frames are handed to the WinForms
// host rather than presented, and with no "presented" notice WPF never learnt the refresh rate: it
// paced on its flat 10-ms DispatcherTimer, which fires on the 15.6-ms system tick, so GraphWorX's canvas
// (a drag's ghost, any animation) ran at 56-58 frames a second on a 100-Hz panel.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ElementHostFramePacingTests
    {
        [Fact]
        public void HostedWpfAnimation_RunsAtTheDisplaysRate()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the display rate read from Windows");
            int hz = DisplayHz();
            Assert.SkipUnless(hz >= 75, $"a display of 75 Hz or more tells a 15.6-ms tick from a frame (this one is {hz} Hz)");
            double period = 1000.0 / hz;
            var intervals = new List<double>();
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "HostedPacingTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
                    };
                    var border = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White };
                    form.Controls.Add(new ElementHost { Dock = SWF.DockStyle.Fill, Child = border });
                    var stamps = new List<double>();
                    var clock = Stopwatch.StartNew();
                    int n = 0;
                    EventHandler onRendering = (s, e) =>
                    {
                        n++;
                        border.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)n, (byte)(n * 3), 200));
                        if (clock.ElapsedMilliseconds > 1500 && stamps.Count < 121) stamps.Add(clock.Elapsed.TotalMilliseconds);
                        if (stamps.Count == 121) form.BeginInvoke(new Action(form.Close));
                    };
                    var guard = new SWF.Timer { Interval = 12000 };
                    guard.Tick += (s, e) => { guard.Stop(); form.Close(); };
                    form.Shown += (s, e) => { System.Windows.Media.CompositionTarget.Rendering += onRendering; guard.Start(); };
                    SWF.Application.Run(form);
                    System.Windows.Media.CompositionTarget.Rendering -= onRendering;
                    guard.Dispose();
                    intervals = stamps.Zip(stamps.Skip(1), (a, b) => b - a).ToList();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            Assert.True(intervals.Count >= 100, $"only {intervals.Count + 1} frames");
            intervals.Sort();
            double median = intervals[intervals.Count / 2];
            Assert.True(median < period * 1.3, $"median hosted frame {median:0.0} ms on a {hz}-Hz display ({period:0.0} ms)");
        }

        private static int DisplayHz()
        {
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettingsW(null, -1, ref mode) ? mode.dmDisplayFrequency : 0;
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
