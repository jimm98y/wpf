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
    [Collection(ProcessDpiStateCollection.Name)]
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

        /// <summary>Minimizing a window and restoring it puts the form back where the window is.
        /// Windows parks a minimized window at (-32000, -32000) and, on the way back, moves it before it
        /// says it is restored -- so the move came while the form was still minimized and was ignored.
        /// The form stayed at (-32000, -32000): everything in it was composited that far away, the
        /// window showed only its background (app's whole WinForms UI went white), and the
        /// application saved the parking place as the window's position.</summary>
        [Fact]
        public void AMinimizedWindow_IsRestoredWhereItWas()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window minimized by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "MinimizeRestoreTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false,
                    };
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("MinimizeRestoreTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(200);
                    Point before = form.Location;
                    GetWindowRect(host, out RECT r0);

                    ShowWindow(host, 7);   // SW_SHOWMINNOACTIVE
                    Pump(300);
                    Assert.True(IsIconic(host));
                    Assert.Equal(SWF.FormWindowState.Minimized, form.WindowState);

                    ShowWindow(host, 4);   // SW_SHOWNOACTIVATE: restores without activating
                    Pump(300);
                    Assert.False(IsIconic(host));
                    Assert.Equal(SWF.FormWindowState.Normal, form.WindowState);
                    GetWindowRect(host, out RECT r1);
                    Assert.Equal((r0.left, r0.top), (r1.left, r1.top));
                    Assert.Equal(before, form.Location);
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>After its window is resized the host presents the frame again for a while, with
        /// nothing in it changing. The first frames after a swap chain grows do not always reach the
        /// screen (app maximized on a 240-dpi display stayed black), and an idle application
        /// presented nothing more.</summary>
        [Fact]
        public void AResizedWindow_IsPresentedAgainWhileItSettles()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window sized by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "ResizeRepresentTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false,
                    };
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("ResizeRepresentTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(2500);   // past the settling that follows a first show

                    object hostObj = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                        .GetMethod("HostOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { form })!;
                    var count = hostObj.GetType().GetProperty("PresentCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    int idle0 = (int)count.GetValue(hostObj)!;
                    Pump(800);
                    Assert.Equal(idle0, (int)count.GetValue(hostObj)!);   // idle: nothing presented

                    SetWindowPos(host, IntPtr.Zero, 0, 0, 500, 360, 0x0002 | 0x0004 | 0x0010);   // NOMOVE | NOZORDER | NOACTIVATE
                    Pump(150);
                    int afterResize = (int)count.GetValue(hostObj)!;
                    Pump(1200);
                    Assert.True((int)count.GetValue(hostObj)! >= afterResize + 2,
                        $"presents: {afterResize} after the resize, {(int)count.GetValue(hostObj)!} a second later");
                    Pump(1500);
                    int settled = (int)count.GetValue(hostObj)!;
                    Pump(800);
                    Assert.Equal(settled, (int)count.GetValue(hostObj)!);   // and it stops again
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A system-aware form on a monitor at a higher DPI than the system's keeps its size,
        /// however large the stretched window gets: the stock window is capped at the virtual screen in
        /// the application's own units. Ours was capped in device pixels, so a 1150-tall app
        /// window on a 240-dpi display came up 2166 pixels tall and the form stayed 866 high for good.</summary>
        [Fact]
        public void ASystemAwareForm_OnAHighDpiMonitor_IsNotCappedInDevicePixels()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window sized by the OS");
            uint system = GetDpiForSystem();
            RECT? high = null;
            uint highDpi = 96;
            // Ask as a per-monitor-aware thread: an unaware one is told every monitor is at 96 and
            // given its rectangles scaled (another test may have left this thread so).
            IntPtr was = SetThreadDpiAwarenessContext((IntPtr)(-4));
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr l) =>
                {
                    if (GetDpiForMonitor(m, 0, out uint dx, out _) == 0 && dx > system) { high = r; highDpi = dx; }
                    return true;
                }, IntPtr.Zero);
            }
            finally { SetThreadDpiAwarenessContext(was); }
            Assert.SkipUnless(high.HasValue, "needs a monitor at a higher DPI than the system's");
            Exception? failure = null; bool skipped = false;
            var t = new Thread(() =>
            {
                try
                {
                    using (SWF.ScaleHelper.OverrideForTests(SWF.HighDpiMode.SystemAware, (int)system))
                    {
                        RECT m = high!.Value;
                        // Far off the monitor's free side, so nothing shows; its nearest monitor -- the
                        // one whose DPI the window takes -- is still the high-DPI one.
                        bool leftOfPrimary = m.right <= 0;
                        int x = leftOfPrimary ? m.left - 9000 : m.right + 9000;
                        int tall = (int)Math.Round((m.bottom - m.top) * system / (double)highDpi * 1.4);   // taller than the monitor, in device pixels once stretched
                        using var form = new SWF.Form
                        {
                            Text = "MaxTrackTest", StartPosition = SWF.FormStartPosition.Manual,
                            Location = new Point(x, m.top + 20), Size = new Size(600, tall), ShowInTaskbar = false,
                        };
                        form.Show();
                        IntPtr host = IntPtr.Zero;
                        for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("MaxTrackTest"); }
                        Assert.NotEqual(IntPtr.Zero, host);
                        Pump(300);
                        // The window has to have taken the high-DPI monitor's scale for there to be
                        // anything to cap; another test's DPI state can leave it at the system's.
                        GetWindowRect(host, out RECT r0);
                        if (r0.bottom - r0.top <= tall + 40) { form.Close(); skipped = true; return; }
                        Assert.Equal(tall, form.Height);
                        GetWindowRect(host, out RECT r);
                        Assert.True(r.bottom - r.top > (m.bottom - m.top), $"window {r.bottom - r.top} px tall on a {m.bottom - m.top} px monitor");
                        form.Close();
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.SkipWhen(skipped, "the window did not take the high-DPI monitor's scale in this process");
        }

        [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr m, int type, out uint dx, out uint dy);
        [DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        /// <summary>Mouse moves already waiting in the queue are all handled before a frame goes out,
        /// and that frame shows the last of them. A present per WM_MOUSEMOVE waited for the display
        /// each time, so the queue backed up behind it and a hover highlight trailed the pointer.</summary>
        [Fact]
        public void QueuedMouseMoves_AreHandledBeforeOneFrameShowsTheLast()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real WM_MOUSEMOVEs to a Win32 window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "CoalesceTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
                    };
                    var lastX = -1; int moves = 0;
                    var panel = new SWF.Panel { Dock = SWF.DockStyle.Fill };
                    panel.MouseMove += (s, e) => { moves++; lastX = e.X; panel.BackColor = Color.FromArgb(255, e.X % 256, 0, 0); };
                    form.Controls.Add(panel);
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("CoalesceTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(2500);   // past the settling after the first show

                    object hostObj = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                        .GetMethod("HostOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { form })!;
                    var count = hostObj.GetType().GetProperty("PresentCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    int before = (int)count.GetValue(hostObj)!;
                    double k = SWF.ScaleHelper.HostSourceDpi == 0 ? 1 : GetDpiForSystem() / (double)SWF.ScaleHelper.HostSourceDpi;
                    for (int i = 0; i < 20; i++)
                    {
                        int px = (int)Math.Round((20 + i * 10) * k), py = (int)Math.Round(100 * k);
                        PostMessage(host, 0x0200, IntPtr.Zero, Pack(px, py));
                    }
                    Pump(600);
                    int presents = (int)count.GetValue(hostObj)! - before;
                    Assert.Equal(20, moves);
                    Assert.Equal(20 + 19 * 10, lastX);
                    Assert.InRange(presents, 1, 4);
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A batch of input that takes longer than a frame to handle still shows frames while it
        /// is handled. The frame used to wait for the queue to be empty, and a pointer that keeps moving
        /// while each move takes longer to handle than the next one takes to come never empties it:
        /// app's ribbon showed nothing new until the pointer stopped (a hover one item behind).</summary>
        [Fact]
        public void AnInputBatchLongerThanAFrame_StillShowsFrames()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real WM_MOUSEMOVEs to a Win32 window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "LongBatchTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
                    };
                    var lastX = -1; int moves = 0;
                    var panel = new SWF.Panel { Dock = SWF.DockStyle.Fill };
                    panel.MouseMove += (s, e) =>
                    {
                        moves++; lastX = e.X; panel.BackColor = Color.FromArgb(255, e.X % 256, 0, 0);
                        // a move that takes a while to handle (a ribbon's hover): 4 ms of work, spun --
                        // a Sleep(4) is 15.6 ms whenever nothing on the machine has raised the timer
                        // resolution, and a frame per move is then the right answer
                        var spin = System.Diagnostics.Stopwatch.StartNew();
                        while (spin.Elapsed.TotalMilliseconds < 4) { }
                    };
                    form.Controls.Add(panel);
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("LongBatchTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(2500);   // past the settling after the first show

                    object hostObj = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                        .GetMethod("HostOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { form })!;
                    var count = hostObj.GetType().GetProperty("PresentCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    int before = (int)count.GetValue(hostObj)!;
                    double k = SWF.ScaleHelper.HostSourceDpi == 0 ? 1 : GetDpiForSystem() / (double)SWF.ScaleHelper.HostSourceDpi;
                    for (int i = 0; i < 40; i++)
                    {
                        int px = (int)Math.Round((20 + i * 5) * k), py = (int)Math.Round(100 * k);
                        PostMessage(host, 0x0200, IntPtr.Zero, Pack(px, py));
                    }
                    Pump(1500);
                    int presents = (int)count.GetValue(hostObj)! - before;
                    Assert.Equal(40, moves);                  // every move is still handled
                    Assert.Equal(20 + 39 * 5, lastX);
                    // 40 moves of 4 ms and more: a frame at least every 8 ms of them, not one at the end
                    // -- and still not one per move.
                    Assert.True(presents >= 6, $"{presents} frames for 160 ms and more of input");
                    Assert.True(presents <= 30, $"{presents} frames for 40 moves");
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A resized window shows its last frame at the new size before the application lays
        /// itself out: by the time the form hears about its new size, a frame of that size is on screen.
        /// A resized GL swap chain shows black until something is presented to it, and app's layout
        /// takes 100 ms and more, so every step of a live resize was black.</summary>
        [Fact]
        public void AResizedWindow_ShowsItsLastFrameBeforeTheLayout()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window sized by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "ResizePreviewTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false, BackColor = Color.Gold,
                    };
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("ResizePreviewTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(500);
                    object hostObj = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                        .GetMethod("HostOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { form })!;
                    var previews = hostObj.GetType().GetProperty("ResizePreviews", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var seen = new System.Collections.Generic.List<int>();
                    form.Resize += (s, e) => seen.Add((int)previews.GetValue(hostObj)!);
                    for (int i = 1; i <= 5; i++)
                    {
                        SetWindowPos(host, IntPtr.Zero, 0, 0, 300 + i * 40, 200 + i * 30, 0x0002 | 0x0004 | 0x0010);   // NOMOVE | NOZORDER | NOACTIVATE
                        Pump(100);
                    }
                    Assert.True(seen.Count >= 5, $"only {seen.Count} resizes were seen");
                    // Each size change had shown a frame of the new size before the form was laid out for it.
                    for (int i = 0; i < seen.Count; i++)
                        Assert.True(seen[i] >= i + 1, $"resize {i + 1}: {seen[i]} previews when the form was resized");
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A resize that comes while the GPU is still drawing the last frame lays the form out
        /// first and shows the last frame at the new size after that, before the new frame. The preview
        /// reconfigures the swap chain, a configure waits for the GPU, and straight after a frame that is
        /// most of the frame's GPU time: shown first, every step of a app drag-resize waited
        /// 20-28 ms for it before its layout could start, and the window was black for that wait anyway.</summary>
        [Fact]
        public void AResizeWhileTheGpuIsBusy_LaysOutFirst_AndStillShowsTheLastFrameBeforeTheNewOne()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Win32 window sized by the OS");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                var hostType = typeof(SWF.Control).Assembly.GetType("Win32Host")!;
                var hook = hostType.GetField("GpuIdleForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "ResizeBusyGpuTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(300, 200), ShowInTaskbar = false, BackColor = Color.Gold,
                    };
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf("ResizeBusyGpuTest"); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(500);
                    object hostObj = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                        .GetMethod("HostOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { form })!;
                    var previews = hostObj.GetType().GetProperty("ResizePreviews", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var seen = new System.Collections.Generic.List<int>();
                    form.Resize += (s, e) => seen.Add((int)previews.GetValue(hostObj)!);
                    int start = (int)previews.GetValue(hostObj)!;
                    hook.SetValue(null, (Func<bool>)(() => false));     // the GPU is never done
                    var after = new System.Collections.Generic.List<int>();
                    for (int i = 1; i <= 5; i++)
                    {
                        SetWindowPos(host, IntPtr.Zero, 0, 0, 300 + i * 40, 200 + i * 30, 0x0002 | 0x0004 | 0x0010);   // NOMOVE | NOZORDER | NOACTIVATE
                        after.Add((int)previews.GetValue(hostObj)!);    // the step is over when SetWindowPos returns
                        Pump(100);
                    }
                    Assert.True(seen.Count >= 5, $"only {seen.Count} resizes were seen");
                    for (int i = 0; i < 5; i++)
                    {
                        // laid out before the preview (the GPU had the layout's time to finish) ...
                        Assert.True(seen[i] == start + i, $"resize {i + 1}: {seen[i] - start} previews when the form was resized, expected {i}");
                        // ... and the preview still made, before the step's own frame
                        Assert.True(after[i] == start + i + 1, $"resize {i + 1}: {after[i] - start} previews after the step, expected {i + 1}");
                    }
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { hook.SetValue(null, null); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [DllImport("user32", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

        [DllImport("user32")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32")] private static extern bool IsIconic(IntPtr h);

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
