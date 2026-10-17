// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// AnimateWindow(AW_BLEND) is timed by the clock, as user32's is: the fade finishes on its own, and the
// window ends up fully opaque on screen with no input at all -- also while a loop other than
// WinForms' is pumping (Windows' modal loops, WPF's dispatcher) -- and the finished picture is
// presented again after it, since one present does not always reach the screen. DotNetBar's gallery
// popup fades in this way and stayed half transparent until the pointer moved.
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
    public class AnimateWindowBlendTests
    {
        private const int AW_BLEND = 0x80000;

        /// <summary>A popup as DotNetBar makes one: a Control (not a Form) with WS_POPUP, created with
        /// Visible already true but its window hidden, then shown by AnimateWindow.</summary>
        private sealed class PopupControl : SWF.Control
        {
            protected override SWF.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = unchecked((int)0x80000000) | 0x04000000 | 0x02000000;   // WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
                    cp.ExStyle = 0x00000080 | 0x00000008;                                 // WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                    cp.Parent = IntPtr.Zero;
                    return cp;
                }
            }
        }

        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Fact]
        public void APopupFadedIn_IsFullyOpaqueOnScreen_WithoutInput() => Run(popupIsForm: false, foreignLoop: false);

        [Fact]
        public void AFormFadedIn_IsFullyOpaqueOnScreen_WithoutInput() => Run(popupIsForm: true, foreignLoop: false);

        [Fact]
        public void AFadeUnderAnotherMessageLoop_FinishesAndIsPresentedAgain() => Run(popupIsForm: false, foreignLoop: true);

        private static void Run(bool popupIsForm, bool foreignLoop)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            uint presented = 0;
            int presentsAfter = -1;
            string? note = null;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new QuietForm
                    {
                        ClientSize = new Size(300, 200), StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80),
                        ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None, BackColor = Color.White,
                    };
                    SWF.Control popup;
                    if (popupIsForm)
                        popup = new QuietForm
                        {
                            StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8040, 120), ClientSize = new Size(100, 60),
                            ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None, BackColor = Color.Red,
                        };
                    else
                        popup = new PopupControl { Location = new Point(8040, 120), Size = new Size(100, 60), BackColor = Color.Red };
                    int phase = 0, waited = 0;
                    long fadeAt = 0, hostSeenAt = 0;
                    var timer = new SWF.Timer { Interval = 20 };
                    timer.Tick += (s, e) =>
                    {
                        if (phase == 0)
                        {
                            // Let the main window come up and settle, then fade the popup in.
                            if (++waited < 10) return;
                            phase = 1; waited = 0;
                            if (popupIsForm)
                            {
                                // A form shown by AnimateWindow, as an app does by P/Invoke.
                                ((SWF.Form)popup).Opacity = 1.0;
                                popup.CreateControl();
                            }
                            else
                            {
                                popup.CreateControl();
                            }
                            fadeAt = Environment.TickCount64;
                            if (!Animate(popup.Handle, 100, AW_BLEND)) note = "AnimateWindow answered false";
                            if (popupIsForm) ((SWF.Form)popup).Show(form);
                            if (!foreignLoop) return;
                            // The popup paints (WinForms' queue), then 400 ms of a plain Win32 loop --
                            // not WinForms', so nothing of ours runs unless a window message asks --
                            // and a look at what was presented.
                            SWF.Application.DoEvents();
                            object? h0 = HostFor(form, null);
                            var countP = h0?.GetType().GetProperty("PresentCount", BindingFlags.Instance | BindingFlags.NonPublic);
                            int atEnd = -1;
                            while (Environment.TickCount64 - fadeAt < 400)
                            {
                                if (atEnd < 0 && Environment.TickCount64 - fadeAt >= 160) atEnd = (int)countP!.GetValue(h0)!;
                                if (PeekMessageW(out NativeMsg m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessageW(ref m); }
                                else MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 5, 0x04FF);
                            }
                            presentsAfter = (int)countP!.GetValue(h0)! - atEnd;
                            fadeAt = 0;   // read the frame now, before WinForms' loop runs again
                        }
                        // ~250 ms later, nothing at all having happened in between. A form of its own
                        // gets its window on the loop's next round, which on a slow machine is later
                        // than the fade: from then.
                        if (popupIsForm && hostSeenAt == 0)
                        {
                            if (HostFor(form, (SWF.Form)popup) == null) return;
                            hostSeenAt = Environment.TickCount64;
                        }
                        if (fadeAt != 0 && Environment.TickCount64 - Math.Max(fadeAt, hostSeenAt) < 250) return;
                        if (note == null) note = $"diag: {Diag(form, popupIsForm ? (SWF.Form)popup : null)}";
                        timer.Stop();
                        object? host = HostFor(form, popupIsForm ? (SWF.Form)popup : null);
                        if (host == null) { note = "no host"; form.Close(); return; }
                        var render = host.GetType().GetMethod("RenderLastPresented", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        object?[] args = { null, null };
                        var rgba = (byte[]?)render.Invoke(host, args);
                        int w = (int)args[0]!;
                        if (rgba == null) { note = "nothing presented"; form.Close(); return; }
                        // The middle of the popup, in the presenting window's pixels.
                        double scale = form.DeviceDpi / 96.0;
                        int px = popupIsForm ? (int)(50 * scale) : (int)(90 * scale), py = popupIsForm ? (int)(30 * scale) : (int)(70 * scale);
                        int i = (py * w + px) * 4;
                        presented = (uint)(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
                        form.Close();
                    };
                    form.Shown += (s, e) => timer.Start();
                    SWF.Application.Run(form);
                    timer.Dispose();
                    popup.Dispose();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(presented == 0xFF0000, $"the faded-in popup shows 0x{presented:x6} on screen, not pure red ({note})");
            if (foreignLoop) Assert.True(presentsAfter > 0, "the finished fade was not presented again");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32")] private static extern bool PeekMessageW(out NativeMsg m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32")] private static extern bool TranslateMessage(ref NativeMsg m);
        [DllImport("user32")] private static extern IntPtr DispatchMessageW(ref NativeMsg m);
        [DllImport("user32")] private static extern uint MsgWaitForMultipleObjects(uint n, IntPtr handles, bool all, uint ms, uint mask);

        private static string Diag(SWF.Form form, SWF.Form? popup)
        {
            object? h = HostFor(form, popup);
            var pc = h?.GetType().GetProperty("PresentCount", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(h);
            var last = h?.GetType().GetField("_lastPresented", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(h) as System.Collections.ICollection;
            return $"host={h != null} presents={pc} scenes={last?.Count}";
        }

        private static object? HostFor(SWF.Form form, SWF.Form? popup)
        {
            var hostOf = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return hostOf.Invoke(null, new object[] { popup ?? form });
        }

        private static bool Animate(IntPtr handle, int time, int flags)
        {
            object driver = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.XplatUI")!
                .GetField("driver", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var m = driver.GetType().GetMethod("AnimateWindow", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IntPtr), typeof(int), typeof(int) }, null)!;
            return (bool)m.Invoke(driver, new object[] { handle, time, flags })!;
        }
    }
}
