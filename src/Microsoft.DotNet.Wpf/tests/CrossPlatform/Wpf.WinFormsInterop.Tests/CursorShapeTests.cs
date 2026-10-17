// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The pointer takes the shape the thing under it asks for, as on Windows: a WinForms control's
// Cursor, a splitter's double arrow, a hosted WPF element's Cursor (app's resize handles on
// its canvas, a FrameworkElement.Cursor of SizeNWSE). Each is driven the way Windows drives it --
// WM_SETCURSOR, then WM_MOUSEMOVE, to the window under the point -- and read back with GetCursor.
// The real pointer is never moved; every window is off the desktop (x >= 8000).
//

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class CursorShapeTests
    {
        private const int IDC_ARROW = 32512, IDC_SIZENWSE = 32642, IDC_SIZEWE = 32644, IDC_SIZENS = 32645;
        private const int WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200, HTCLIENT = 1;

        [DllImport("user32")] private static extern IntPtr LoadCursorW(IntPtr h, int id);
        [DllImport("user32")] private static extern IntPtr GetCursor();
        [DllImport("user32", EntryPoint = "SetCapture")] private static extern IntPtr SetCaptureW(IntPtr h);
        [DllImport("user32")] private static extern bool ReleaseCapture();
        [DllImport("user32")] private static extern IntPtr SetCursor(IntPtr c);
        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);

        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private static object? HostFor(SWF.Form form)
        {
            var hostOf = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return hostOf.Invoke(null, new object[] { form });
        }

        private static IntPtr RealWindow(object host)
            => (IntPtr)host.GetType().GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

        private static IntPtr Lp(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        /// <summary>The pointer arriving at (x, y) of <paramref name="hwnd"/>'s client area, as Windows
        /// tells the window: WM_SETCURSOR first, then the move.</summary>
        private static void PointAt(IntPtr hwnd, int x, int y)
        {
            SendMessageW(hwnd, WM_SETCURSOR, hwnd, (IntPtr)((WM_MOUSEMOVE << 16) | HTCLIENT));
            SendMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, Lp(x, y));
        }

        /// <summary>Runs <paramref name="test"/> on an STA thread once <paramref name="form"/>'s host window
        /// exists, inside the form's own message loop.</summary>
        private static void WithHost(Func<QuietForm> make, Action<QuietForm, IntPtr> test)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using QuietForm form = make();
                    var timer = new SWF.Timer { Interval = 20 };
                    int ticks = 0;
                    timer.Tick += (o, e) =>
                    {
                        object? host = HostFor(form);
                        if (++ticks < 300 && (host == null || RealWindow(host) == IntPtr.Zero || ticks < 10)) return;
                        timer.Stop();
                        try
                        {
                            Assert.NotNull(host);
                            test(form, RealWindow(host!));
                        }
                        catch (Exception ex) { failure = ex; }
                        form.Close();
                    };
                    form.Shown += (o, e) => timer.Start();
                    SWF.Application.Run(form);
                    timer.Dispose();
                }
                catch (Exception ex) { failure ??= ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the UI thread did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static QuietForm NewForm() => new QuietForm
        {
            StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(300, 200),
            ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None, BackColor = Color.White,
        };

        private static string Name(IntPtr cursor)
        {
            foreach ((int id, string name) in new[] { (IDC_ARROW, "Arrow"), (IDC_SIZENWSE, "SizeNWSE"), (IDC_SIZEWE, "SizeWE"), (IDC_SIZENS, "SizeNS") })
                if (LoadCursorW(IntPtr.Zero, id) == cursor) return name;
            return $"0x{cursor:x}";
        }

        private static void AssertCursor(int idc, string what)
        {
            IntPtr want = LoadCursorW(IntPtr.Zero, idc), got = GetCursor();
            Assert.True(want == got, $"{what}: the pointer is {Name(got)}, not {Name(want)}");
        }

        [Fact]
        public void AControlsCursor_IsThePointersShape()
        {
            SWF.Panel? panel = null;
            WithHost(() =>
            {
                QuietForm f = NewForm();
                panel = new SWF.Panel { Bounds = new Rectangle(100, 50, 100, 100), Cursor = SWF.Cursors.SizeWE, BackColor = Color.Gray };
                f.Controls.Add(panel);
                return f;
            }, (form, hwnd) =>
            {
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                PointAt(hwnd, 150, 100);
                AssertCursor(IDC_SIZEWE, "over a panel with Cursor = SizeWE");
                PointAt(hwnd, 20, 20);
                AssertCursor(IDC_ARROW, "over the form");
            });
        }

        [Fact]
        public void CursorCurrent_LastsUntilThePointerMoves_ThenTheControlsCursorIsBack()
        {
            // Cursor.Current shows a shape NOW; Windows' next WM_SETCURSOR puts the window's own back.
            // An app that resets it (Cursor.Current = Cursors.Default after a wait) must not pin the
            // arrow over every control for the rest of the session.
            WithHost(() =>
            {
                QuietForm f = NewForm();
                f.Controls.Add(new SWF.Panel { Bounds = new Rectangle(100, 50, 100, 100), Cursor = SWF.Cursors.SizeNS, BackColor = Color.Gray });
                return f;
            }, (form, hwnd) =>
            {
                SWF.Cursor.Current = SWF.Cursors.WaitCursor;
                SWF.Cursor.Current = SWF.Cursors.Default;
                PointAt(hwnd, 150, 100);
                AssertCursor(IDC_SIZENS, "over the panel after Cursor.Current = Default");
                // Set from a mouse handler, it holds for that move...
                form.Controls[0].MouseMove += (o, e) => SWF.Cursor.Current = SWF.Cursors.SizeWE;
                PointAt(hwnd, 151, 100);
                AssertCursor(IDC_SIZEWE, "Cursor.Current set by the control's MouseMove");
            });
        }

        [Fact]
        public void ASplitContainersSplitter_ShowsTheDoubleArrow()
        {
            SWF.SplitContainer? split = null;
            WithHost(() =>
            {
                QuietForm f = NewForm();
                split = new SWF.SplitContainer { Dock = SWF.DockStyle.Fill, SplitterDistance = 150, SplitterWidth = 6 };
                f.Controls.Add(split);
                return f;
            }, (form, hwnd) =>
            {
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                int x = split!.SplitterDistance + split.SplitterWidth / 2;
                PointAt(hwnd, x, 100);
                PointAt(hwnd, x, 101);
                AssertCursor(IDC_SIZEWE, "over the splitter of a vertical SplitContainer");
            });
        }

        [Fact]
        public void AHostedWpfElementsCursor_IsThePointersShape()
        {
            ElementHost? eh = null;
            System.Windows.Controls.Border? handle = null;
            WithHost(() =>
            {
                QuietForm f = NewForm();
                var canvas = new System.Windows.Controls.Canvas { Background = System.Windows.Media.Brushes.White };
                // A resize handle as app's selection adorner has: a small element with its own Cursor.
                handle = new System.Windows.Controls.Border
                {
                    Width = 40, Height = 40, Background = System.Windows.Media.Brushes.Blue,
                    Cursor = System.Windows.Input.Cursors.SizeNWSE,
                };
                System.Windows.Controls.Canvas.SetLeft(handle, 60);
                System.Windows.Controls.Canvas.SetTop(handle, 60);
                canvas.Children.Add(handle);
                eh = new ElementHost { Dock = SWF.DockStyle.Fill, Child = canvas };
                f.Controls.Add(eh);
                return f;
            }, (form, hostHwnd) =>
            {
                long until = Environment.TickCount64 + 10000;
                while (!eh!.IsChildLive && Environment.TickCount64 < until) { SWF.Application.DoEvents(); Thread.Sleep(5); }
                Assert.True(eh.IsChildLive, "the hosted tree never rendered");
                var source = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(handle!)!;
                double scale = form.DeviceDpi / 96.0;
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                // WPF ignores a move for a window the real pointer is not over unless that window has
                // the capture (HwndMouseInputProvider: "spurious" moves); the real pointer stays put.
                SetCaptureW(source.Handle);
                PointAt(source.Handle, (int)(70 * scale), (int)(70 * scale));
                PointAt(source.Handle, (int)(80 * scale), (int)(80 * scale));
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                AssertCursor(IDC_SIZENWSE, "over a hosted element with Cursor = SizeNWSE");
                PointAt(source.Handle, (int)(10 * scale), (int)(10 * scale));
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                AssertCursor(IDC_ARROW, "over the hosted canvas");
            });
        }
    }
}
