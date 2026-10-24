// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF_WEBGPU_TEST_ORIGIN=x,y offsets every new top-level window before it is first shown, so a test or
// benchmark run never puts a window on the user's primary display -- not even an application's splash
// screen centred there for the moment before the run moves it. Unset, it does nothing.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TestOriginTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Fact]
        public void ACentredWindow_IsCreatedAtTheTestOrigin()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            var saved = Win32Host.s_testOrigin;
            Win32Host.s_testOrigin = (9000, 0);
            try
            {
                RECT r = default; IntPtr host = IntPtr.Zero; Exception? failure = null;
                var t = new Thread(() =>
                {
                    try
                    {
                        using var form = new QuietForm { Text = "TestOriginTest", ClientSize = new Size(300, 200), ShowInTaskbar = false,
                            StartPosition = SWF.FormStartPosition.CenterScreen };
                        var timer = new SWF.Timer { Interval = 50 };
                        int ticks = 0;
                        timer.Tick += (s, e) =>
                        {
                            host = HostOf("TestOriginTest");
                            if (host != IntPtr.Zero || ++ticks > 100)
                            {
                                if (host != IntPtr.Zero) GetWindowRect(host, out r);
                                timer.Stop();
                                form.Close();
                            }
                        };
                        form.Shown += (s, e) => timer.Start();
                        SWF.Application.Run(form);
                        timer.Dispose();
                    }
                    catch (Exception ex) { failure = ex; }
                });
                t.TrySetApartmentState(ApartmentState.STA);
                t.Start();
                Assert.True(t.Join(TimeSpan.FromSeconds(30)));
                if (failure != null) throw failure;
                Assert.NotEqual(IntPtr.Zero, host);
                Assert.True(r.Left >= 9000, $"the window is at {r.Left},{r.Top}, not offset to the test origin");
            }
            finally { Win32Host.s_testOrigin = saved; }
        }

        private static IntPtr HostOf(string title)
        {
            IntPtr found = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var t = new System.Text.StringBuilder(64);
                InternalGetWindowText(h, t, 64);
                if (t.ToString() != title) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    }
}
