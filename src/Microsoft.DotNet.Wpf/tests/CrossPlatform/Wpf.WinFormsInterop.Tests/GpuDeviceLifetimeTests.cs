// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Every window gets a GPU device of its own and, on the GL backend, a GL context behind a hidden
// "wgpu Device Class" window and a GL surface on the window itself.
//
// A window that closed kept all of it. So did a window that Windows destroyed together with the
// thread it belonged to: a form disposed on a worker thread that then ended was left for some other
// thread's frame loop to take down, and by then its window, with the GL surface still on it, was
// gone. The driver never gets such a surface back. On this VM's GL driver about 256 of them in, every
// window the process opens afterwards is a white rectangle: its frames are drawn and presented, and
// never reach the screen (GDI drawn into it does). Late in a full test run, dialogs came up white.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class GpuDeviceLifetimeTests
    {
        [Fact]
        public void ClosingAWindow_ReleasesItsGpuDevice()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "counts the GL backend's device windows");
            OnSta(() =>
            {
                // One window first, so whatever is made once per process (an adapter probe) is made.
                OpenAndClose(0);
                int before = DeviceWindows();
                const int Windows = 12;
                for (int i = 0; i < Windows; i++) OpenAndClose(i + 1);
                int after = DeviceWindows();
                Assert.True(after - before <= 1, $"{after - before} GPU device windows left behind by {Windows} windows that closed ({before} -> {after})");
            });
        }

        /// <summary>A form shown on a thread of its own and disposed just before that thread ends --
        /// nothing pumps the thread after it. Its window has to give its GPU side back THEN, on its
        /// own thread: once the thread is gone, Windows has destroyed the window under the surface.</summary>
        [Fact]
        public void AFormDisposedJustBeforeItsThreadEnds_GivesItsSurfaceBackOnThatThread()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            for (int i = 0; i < 4; i++)
            {
                object? host = null;
                bool hadGpu = false, releasedOnThread = false;
                Exception? failure = null;
                int n = i;
                var t = new Thread(() =>
                {
                    try
                    {
                        var f = new SWF.Form { Text = "Ends " + n, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 30), ClientSize = new Size(160, 100), ShowInTaskbar = false };
                        f.Show();
                        for (var end = DateTime.UtcNow.AddSeconds(5); DateTime.UtcNow < end && (host = HostFor(f)) is null;) Pump(20);
                        Pump(100);
                        Assert.NotNull(host);
                        hadGpu = Gpu(host!) != null;
                        IntPtr hwnd = (IntPtr)host!.GetType().GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                        f.Dispose();
                        // Still on the thread, nothing pumped since: the GPU side is already gone, and
                        // the window was taken down here rather than left for the thread's end.
                        releasedOnThread = Gpu(host!) == null && !IsWindow(hwnd);
                    }
                    catch (Exception e) { failure = e; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.True(hadGpu, "the form never had a GPU surface");
                Assert.True(releasedOnThread, $"window {i}: disposing the form did not take its window and its GPU surface down on its own thread");
            }
        }

        /// <summary>Threads that show a form and simply end, never closing it (a worker-thread splash
        /// screen, a dialog thread that returns): Windows takes the windows down with the threads and
        /// nothing of ours runs there. Their GPU devices go back once the threads are found dead --
        /// each kept a device, a context and a hidden window for good, and on D3D12 a few hundred of
        /// them left no device for the next window at all.</summary>
        [Fact]
        public void ThreadsThatEndWithTheirFormOpen_GiveTheirDevicesBack()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "counts the GL backend's device windows");
            OnSta(() => OpenAndClose(0));      // whatever is made once per process
            int before = DeviceWindows();
            const int Threads = 12;
            var abandoned = new (IntPtr Handle, Point Centre)[Threads];
            for (int i = 0; i < Threads; i++)
            {
                int n = i;
                var t = new Thread(() =>
                {
                    var f = new SWF.Form { Text = "Abandoned " + n, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000 + n * 130, 30), ClientSize = new Size(120, 80), ShowInTaskbar = false };
                    f.Show();
                    Pump(60);
                    abandoned[n] = (f.Handle, f.PointToScreen(new Point(60, 40)));
                    Assert.Equal(f.Handle, WindowAtPoint(abandoned[n].Centre));   // there while its thread lives
                    // ...and the thread ends, the form never closed.
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
            }
            int after = DeviceWindows();
            bool ghosts = true;
            for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && (after - before > 1 || ghosts); Thread.Sleep(100))
            {
                after = DeviceWindows();
                ghosts = Array.Exists(abandoned, a => WindowAtPoint(a.Centre) == a.Handle);
            }
            Assert.True(after - before <= 1, $"{after - before} GPU device windows still held for {Threads} threads that ended ({before} -> {after})");
            // Windows destroyed those windows with their threads: nothing may still find them under a point.
            Assert.False(ghosts, "a window whose thread has ended is still found under the point where it was");
        }

        private static IntPtr WindowAtPoint(Point screen)
        {
            object driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            return (IntPtr)driver.GetType().GetMethod("WindowAtPoint", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(driver, new object[] { screen.X, screen.Y })!;
        }

        private static object? Gpu(object host)
            => host.GetType().GetField("_wgpu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);

        private static object? HostFor(SWF.Control form)
            => typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(null, new object[] { form });

        private static void OnSta(Action body)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        internal static void OpenAndClose(int i)
        {
            using var f = new SWF.Form { Text = "Lifetime " + i, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(30, 30), ClientSize = new Size(200, 120), ShowInTaskbar = false };
            f.Controls.Add(new SWF.Label { Text = "window " + i, AutoSize = true });
            f.Show();
            Pump(120);
            f.Close();
            Pump(60);
        }

        internal static void Pump(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(10);
            }
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>This process's top-level windows whose class is the GL backend's device window.</summary>
        internal static int DeviceWindows()
        {
            int n = 0;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var c = new System.Text.StringBuilder(64); GetClassNameW(h, c, 64);
                if (c.ToString().StartsWith("wgpu Device Class", StringComparison.Ordinal)) n++;
                return true;
            }, IntPtr.Zero);
            return n;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32")] private static extern bool IsWindow(IntPtr h);
    }
}
