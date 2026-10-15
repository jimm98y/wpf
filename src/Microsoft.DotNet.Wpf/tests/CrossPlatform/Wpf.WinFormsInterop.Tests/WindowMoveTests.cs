// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A form follows its window. On Windows a form's Location IS its window's, so the user dragging it,
// the system placing it or another program moving it shows in Location, Bounds and the Move event.
// The host never carried a move back to the form: after a real move across monitors a form still
// reported the position it was created at.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class WindowMoveTests
    {
        [Theory]
        [InlineData(SWF.HighDpiMode.PerMonitorV2)]
        [InlineData(SWF.HighDpiMode.SystemAware)]
        [InlineData(SWF.HighDpiMode.DpiUnaware)]
        public void MovingTheWindow_MovesTheForm(SWF.HighDpiMode mode)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window moved by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using (SWF.ScaleHelper.OverrideForTests(mode, (int)GetDpiForSystem())) Run();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            // Off screen and never activated: nothing appears on the desktop.
            using var form = new SWF.Form
            {
                Text = "WindowMoveTests", StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false,
            };
            int moves = 0;
            form.Move += (s, e) => moves++;
            form.Show();
            IntPtr host = IntPtr.Zero;
            for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;)
            {
                Pump(100);
                host = HostOf("WindowMoveTests");
            }
            Assert.NotEqual(IntPtr.Zero, host);
            Pump(300);
            moves = 0;

            // Where Windows puts it, in device pixels; the form sees it in its own units.
            const int px = 8200, py = 160;
            SetWindowPos(host, IntPtr.Zero, px, py, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
            Pump(300);

            double k = SWF.ScaleHelper.HostSourceDpi == 0 ? 1 : GetDpiForSystem() / (double)SWF.ScaleHelper.HostSourceDpi;
            Assert.Equal(new Point((int)Math.Round(px / k), (int)Math.Round(py / k)), form.Location);
            Assert.True(moves > 0, "the form raised no Move for its window's move");
            GetWindowRect(host, out RECT r);
            Assert.Equal(px, r.left);       // and the form's new Location did not move the window again
            Assert.Equal(py, r.top);
            form.Close();
        }

        private static void Pump(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly System.Reflection.MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private static IntPtr HostOf(string title)
        {
            IntPtr found = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var t = new System.Text.StringBuilder(64); InternalGetWindowText(h, t, 64);
                if (t.ToString() != title) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
        [DllImport("user32")] private static extern uint GetDpiForSystem();
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    }
}
