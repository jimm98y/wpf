// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Mixed hosting at the monitor's DPI, held to stock .NET 10. A per-monitor application's hosted
// WinForms lays out at the monitor's DPI -- a WindowsFormsHost's child is its DIP size times the
// DPI scale in pixels, with its fonts at that DPI -- and an ElementHost's WPF tree is the control's
// pixels in DIPs at the same DPI. HostedDpiScenario builds both, off screen; stock's dump of the same
// code is Fixtures/Dpi/stock-hosted-<system dpi>.txt (recorded by the scratch probe "hostdpi" with a
// PerMonitorV2 manifest). Then the WPF window is told it moved to 96 dpi and back, the way Windows
// tells it: the hosted controls follow (as a child window's do on a real move) and come back to
// exactly the layout they started with.
//

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;
using SWF = System.Windows.Forms;
using WPF = System.Windows;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class HostedDpiTests
    {
        private const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        [DllImport("user32")] private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr c);
        [DllImport("user32")] private static extern uint GetDpiForSystem();
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, ref RECT l);

        [Fact]
        public void HostedLayouts_AreStocksAtTheMonitorsDpi()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the DPI moves are Windows' messages to a real WPF window");
            SetProcessDpiAwarenessContext((IntPtr)(-4));          // as the application's manifest makes it
            int systemDpi = (int)GetDpiForSystem();
            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dpi", $"stock-hosted-{systemDpi}.txt");
            Assert.SkipUnless(File.Exists(fixture), $"no stock recording for a {systemDpi}-dpi display (record one with the hostdpi probe)");
            string[] stock = File.ReadAllText(fixture).Replace("\r\n", "\n").Split("== elementhost\n");

            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using (SWF.ScaleHelper.OverrideForTests(SWF.HighDpiMode.PerMonitorV2, systemDpi))
                        Run(stock[0].Replace("== wfh\n", ""), stock[1], systemDpi);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run(string stockWfh, string stockElementHost, int systemDpi)
        {
            var (window, host) = HostedDpiScenario.BuildWpfWindow();
            window.Show();
            Pump(1200);
            try
            {
                Assert.Equal(stockWfh, HostedDpiScenario.DumpWpfWindow(host));

                // Moved to 96 dpi: the WinForms child is its DIP size in pixels, everything in it at 96.
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                Move(hwnd, 96);
                Pump(800);
                string at96 = HostedDpiScenario.DumpWpfWindow(host);
                Assert.Contains("panel bounds={X=0,Y=0,Width=300,Height=150}", at96);
                Assert.True(systemDpi == 96 || !at96.Contains($"dpi={systemDpi}"), at96);

                // And back: exactly the layout it started with.
                Move(hwnd, systemDpi);
                Pump(800);
                Assert.Equal(stockWfh, HostedDpiScenario.DumpWpfWindow(host));
            }
            finally { window.Close(); }

            var (form, eh, root) = HostedDpiScenario.BuildForm();
            form.Show();
            Pump(1200);
            try { Assert.Equal(stockElementHost, HostedDpiScenario.DumpForm(eh, root)); }
            finally { form.Close(); }
        }

        /// <summary>WM_DPICHANGED with the window's rectangle scaled by the change, as Windows sends it.</summary>
        private static void Move(IntPtr hwnd, int dpi)
        {
            GetWindowRect(hwnd, out RECT r);
            SendMessageW(hwnd, WM_DPICHANGED, (IntPtr)((dpi << 16) | dpi), ref r);
        }

        private static void Pump(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }
        }
    }
}
