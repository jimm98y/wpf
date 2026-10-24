// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A PerMonitorV2 form created at a Location on a display whose DPI is not the primary's, laid out as
// stock .NET 10 lays it out there (measured with a stock app on this machine's 96 + 240 dpi displays):
//
//   - ShowInTaskbar = true: the window is the display's (240). The frame is 240's, the form keeps the
//     window size it was given (416x339, so a 384x251 client) and its fonts are 240's.
//   - ShowInTaskbar = false: .NET owns the window by a hidden TaskbarOwner on the primary, set once the
//     window exists (Form.UpdateHandleWithOwner), and Windows then moves the window to the owner's
//     96 dpi (WM_DPICHANGED 240 -> 96): a 400x300 client and 96-dpi fonts.
//
// The second was the one this port got wrong: a 240-dpi window with 96-dpi layout, its hosted WPF
// content placed for 240 inside a WinForms layout left at 96 -- a popup two and a half times off.
//
// WinForms' mode is one for the whole process, settled by whatever comes first, so the check runs in
// a process of its own, made PerMonitorV2.
//

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class PerMonitorCreationTests
    {
        private const string InnerVariable = "WF_PERMONITOR_CREATION_INNER";

        [Fact]
        public void AFormCreatedOnAnotherDisplay_IsLaidOutAsStockLaysItOut()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real displays");
            Assert.SkipUnless(OtherDisplay(out _, out int dpi) && dpi != PrimaryDpi(), "needs a second display at another DPI than the primary's");
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--filter-method \"*{nameof(Inner_FormsOnAnotherDisplay)}*\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.Environment["WINFORMS_HIGHDPIMODE"] = "PerMonitorV2";
            psi.Environment[InnerVariable] = "1";
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(120_000), "the inner run did not finish");
            output += stderr.Result;
            Assert.True(p.ExitCode == 0 && output.Contains("succeeded: 1"), "the PerMonitorV2 run failed:\n" + output);
        }

        [Fact]
        public void Inner_FormsOnAnotherDisplay()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable(InnerVariable) == "1", "run by AFormCreatedOnAnotherDisplay_IsLaidOutAsStockLaysItOut, in a PerMonitorV2 process");
            Assert.Equal(SWF.HighDpiMode.PerMonitorV2, SWF.ScaleHelper.Mode);
            Assert.True(OtherDisplay(out Point at, out int dpi));
            int primary = PrimaryDpi();
            foreach (bool taskbar in new[] { true, false })
            {
                Exception? failure = null;
                var t = new Thread(() =>
                {
                    try
                    {
                        var f = new SWF.Form { Text = "PerMonitorCreation", StartPosition = SWF.FormStartPosition.Manual, Location = at, ClientSize = new Size(400, 300), ShowInTaskbar = taskbar };
                        var tb = new SWF.TextBox { Bounds = new Rectangle(10, 10, 200, 30), Font = new Font("Segoe UI", 9f), Text = "100%" };
                        f.Controls.Add(tb);
                        Size window = f.Size;
                        f.Show();
                        GpuDeviceLifetimeTests.Pump(600);
                        int expectDpi = taskbar ? dpi : primary;
                        int fontAt96 = new Font("Segoe UI", 9f).Height;
                        string result = $"taskbar={taskbar}: DeviceDpi {f.DeviceDpi} (expected {expectDpi}) Size {f.Size} (given {window}) Client {f.ClientSize} font height {tb.Font.Height} (at 96: {fontAt96})";
                        Assert.True(f.DeviceDpi == expectDpi, result);
                        Assert.True(f.Size == window, result);                                   // the window keeps the size it was given
                        if (taskbar) Assert.True(f.ClientSize.Width < 400 && f.ClientSize.Height < 300, result);   // the display's larger frame
                        else Assert.True(f.ClientSize == new Size(400, 300), result);              // the primary's frame
                        Assert.True(taskbar ? tb.Font.Height > fontAt96 : tb.Font.Height == fontAt96, result);
                        f.Close();
                        GpuDeviceLifetimeTests.Pump(50);
                    }
                    catch (Exception e) { failure = e; }
                });
                t.TrySetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static bool OtherDisplay(out Point at, out int dpi)
        {
            Point found = Point.Empty; int foundDpi = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr dc, ref RECT r, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (foundDpi == 0 && GetMonitorInfoW(mon, ref info) && (info.dwFlags & 1) == 0 && GetDpiForMonitor(mon, 0, out uint x, out _) == 0)
                {
                    found = new Point(info.rcWork.left + 100, info.rcWork.top + 100);
                    foundDpi = (int)x;
                }
                return true;
            }, IntPtr.Zero);
            at = found; dpi = foundDpi;
            return foundDpi > 0;
        }

        private static int PrimaryDpi()
        {
            var r = new RECT { left = 0, top = 0, right = 1, bottom = 1 };
            IntPtr mon = MonitorFromRect(ref r, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            return GetDpiForMonitor(mon, 0, out uint x, out _) == 0 ? (int)x : 96;
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        private delegate bool MonitorEnumProc(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data);
        [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO info);
        [DllImport("user32")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
        [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    }
}
