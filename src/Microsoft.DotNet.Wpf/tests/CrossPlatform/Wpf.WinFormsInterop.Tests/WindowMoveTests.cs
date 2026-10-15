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

        /// <summary>On Windows the screen is the desktop: the real monitors, primary first, through
        /// the same mapping as window positions. It was the main window's client size at the origin,
        /// so a popup dropped from a window placed anywhere else was pushed back into that imaginary
        /// screen (app's property-grid drop-downs opened to the left, away from their cells).</summary>
        [Fact]
        public void TheScreens_AreTheDesktopsMonitors()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 monitors");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form { Text = "ScreensTest", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false };
                    form.Show();
                    Pump(300);
                    var monitors = new System.Collections.Generic.List<RECT>();
                    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr l) => { monitors.Add(r); return true; }, IntPtr.Zero);
                    SWF.Screen[] screens = SWF.Screen.AllScreens;
                    Assert.Equal(monitors.Count, screens.Length);
                    Assert.True(screens[0].Primary);
                    double k = SWF.ScaleHelper.HostSourceDpi == 0 ? 1 : GetDpiForSystem() / (double)SWF.ScaleHelper.HostSourceDpi;
                    foreach (RECT m in monitors)
                    {
                        var expect = Rectangle.FromLTRB((int)Math.Round(m.left / k), (int)Math.Round(m.top / k), (int)Math.Round(m.right / k), (int)Math.Round(m.bottom / k));
                        Assert.Contains(screens, s => s.Bounds == expect);
                    }
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private delegate bool MonitorProc(IntPtr m, IntPtr dc, ref RECT r, IntPtr l);
        [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc p, IntPtr l);

        /// <summary>A form that draws its own frame says where its caption and edges are.</summary>
        private sealed class ChromeForm : SWF.Form
        {
            protected override void WndProc(ref SWF.Message m)
            {
                if (m.Msg == 0x0084)   // WM_NCHITTEST
                {
                    Point p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                    if (p.X >= ClientSize.Width - 8 && p.Y >= ClientSize.Height - 8) { m.Result = (IntPtr)17; return; }   // HTBOTTOMRIGHT
                    if (p.Y < 30) { m.Result = (IntPtr)2; return; }                                                       // HTCAPTION
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>The DotNetBar way: a press on a drawn caption sends the form WM_SYSCOMMAND SC_MOVE |
        /// HTCAPTION, or ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION). Either must start the system's
        /// move of the window; nothing did on Windows, and app's window could not be dragged.</summary>
        [Fact]
        public void ACustomChromeForm_IsDraggedAndSizedByTheSystem()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window moved by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new ChromeForm
                    {
                        Text = "ChromeFormTest", FormBorderStyle = SWF.FormBorderStyle.None, StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false,
                    };
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("ChromeFormTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(200);
                    GetWindowRect(host, out RECT r);
                    // The system asks the window what is under a point: the form's own answers.
                    Assert.Equal(2, (int)SendMessage(host, 0x0084, IntPtr.Zero, Pack(r.left + 100, r.top + 10)));
                    Assert.Equal(17, (int)SendMessage(host, 0x0084, IntPtr.Zero, Pack(r.right - 3, r.bottom - 3)));
                    Assert.Equal(1, (int)SendMessage(host, 0x0084, IntPtr.Zero, Pack(r.left + 100, r.top + 100)));   // HTCLIENT

                    // And the form's own requests reach the system as a move / a size of the window.
                    var driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUIWebGpu")!;
                    var move = driver.GetField("BeginWindowMove", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    var size = driver.GetField("BeginWindowSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    var realMove = move.GetValue(null); var realSize = size.GetValue(null);
                    Assert.NotNull(realMove);   // the Windows host provides it
                    int moves = 0, sizes = 0, edge = 0;
                    move.SetValue(null, new Action<IntPtr>(h => { if (h == form.Handle) moves++; }));
                    size.SetValue(null, new Action<IntPtr, int>((h, e) => { if (h == form.Handle) { sizes++; edge = e; } }));
                    try
                    {
                        SWF.Control fc = form;
                        SendStack(fc, 0x0112, (IntPtr)0xF012, IntPtr.Zero);   // WM_SYSCOMMAND SC_MOVE|HTCAPTION
                        SendStack(fc, 0x00A1, (IntPtr)2, IntPtr.Zero);        // WM_NCLBUTTONDOWN HTCAPTION
                        SendStack(fc, 0x00A1, (IntPtr)17, IntPtr.Zero);       // WM_NCLBUTTONDOWN HTBOTTOMRIGHT
                    }
                    finally { move.SetValue(null, realMove); size.SetValue(null, realSize); }
                    Assert.Equal(2, moves);
                    Assert.Equal(1, sizes);
                    Assert.Equal(8, edge);   // WMSZ_BOTTOMRIGHT
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A double click on a form's own caption (a control that maximizes or restores its form
        /// with WM_SYSCOMMAND from OnDoubleClick, as DotNetBar's ribbon caption does) reaches it and
        /// maximizes the window, then restores it. No double click was ever delivered: a second press
        /// was just another WM_LBUTTONDOWN.</summary>
        [Fact]
        public void ADoubleClickOnADrawnCaption_MaximizesAndRestores()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window maximized by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "CaptionDblClickTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false,
                    };
                    var caption = new SWF.Panel { Dock = SWF.DockStyle.Top, Height = 30 };
                    int doubles = 0;
                    caption.DoubleClick += (s, e) =>
                    {
                        doubles++;
                        int cmd = form.WindowState == SWF.FormWindowState.Normal ? 0xF030 : 0xF120;   // SC_MAXIMIZE / SC_RESTORE
                        SendStack(form, 0x0112, (IntPtr)cmd, IntPtr.Zero);
                    };
                    form.Controls.Add(caption);
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("CaptionDblClickTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(200);

                    void DoubleClickCaption()
                    {
                        var d = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                            .GetField("driver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
                        Point p = caption.PointToScreen(new Point(caption.Width / 2, caption.Height / 2));
                        var dt = d.GetType();
                        var down = dt.GetMethod("InjectMouseDownIn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                        var up = dt.GetMethod("InjectMouseUpIn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                        for (int i = 0; i < 2; i++)
                        {
                            down.Invoke(d, new object[] { form.Handle, p.X, p.Y });
                            up.Invoke(d, new object[] { form.Handle, p.X, p.Y });
                        }
                        Pump(400);
                    }

                    // What reaches the real window is recorded rather than done: a test window
                    // maximized would cover the desktop.
                    var driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUIWebGpu")!;
                    var hook = driver.GetField("WindowStateRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    var real = hook.GetValue(null);
                    Assert.NotNull(real);   // the Windows host makes the window follow
                    var requested = new System.Collections.Generic.List<SWF.FormWindowState>();
                    hook.SetValue(null, new Action<IntPtr, SWF.FormWindowState>((h, st) => { if (h == form.Handle) requested.Add(st); }));
                    try
                    {
                        DoubleClickCaption();
                        Assert.Equal(1, doubles);
                        Assert.Equal(SWF.FormWindowState.Maximized, form.WindowState);
                        DoubleClickCaption();
                        Assert.Equal(2, doubles);
                        Assert.Equal(SWF.FormWindowState.Normal, form.WindowState);
                    }
                    finally { hook.SetValue(null, real); }
                    Assert.Equal(new[] { SWF.FormWindowState.Maximized, SWF.FormWindowState.Normal }, requested);
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [DllImport("user32")] private static extern bool IsZoomed(IntPtr h);

        /// <summary>A message to the form through the stack, as the app's own SendMessage is routed.</summary>
        private static void SendStack(SWF.Control c, int msg, IntPtr w, IntPtr l)
        {
            object m = SWF.Message.Create(c.Handle, msg, w, l);
            c.GetType().GetMethod("WndProc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(SWF.Message).MakeByRefType() }, null)!.Invoke(c, new object[] { m });
        }

        private static IntPtr Pack(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        [DllImport("user32", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

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
