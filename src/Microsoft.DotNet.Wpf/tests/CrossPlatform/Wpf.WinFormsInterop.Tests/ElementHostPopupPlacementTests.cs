// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WPF Popup in a tree hosted by an ElementHost opens where its placement target is on screen,
// as it does in a stock WinForms app. app's editor showed resize feedback in the top-left
// corner of the screen.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;
using WPF = System.Windows;

namespace Wpf.WinFormsInterop.Tests
{
    [Collection(ProcessDpiStateCollection.Name)]
    public class ElementHostPopupPlacementTests
    {
        [Theory]
        [InlineData(WPF.Controls.Primitives.PlacementMode.Bottom, false)]
        [InlineData(WPF.Controls.Primitives.PlacementMode.Relative, false)]
        // ...also with a control over part of the host, which is cut out of what the hosted window takes:
        // a window region did that, and its WM_WINDOWPOSCHANGED left WPF placing popups by a stale rectangle.
        [InlineData(WPF.Controls.Primitives.PlacementMode.Bottom, true)]
        public void APopupInAnElementHost_OpensAtItsTarget(WPF.Controls.Primitives.PlacementMode mode, bool partlyCovered)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real popup windows");
            // What it expects is a DPI-unaware application's layout (the target at its 96-dpi place,
            // scaled by the window's own scale): pinned, whatever the process came up as.
            using var dpiMode = SWF.ScaleHelper.OverrideForTests(SWF.HighDpiMode.DpiUnaware, 96);
            Exception? failure = null;
            string result = "";
            var t = new Thread(() =>
            {
                try
                {
                    // On a monitor other than the primary when there is one (a popup is kept on a
                    // monitor, so it cannot be tested off screen), never activated. The popup is a few
                    // transparent pixels, so even one opened in the wrong place shows nothing.
                    using var form = new SWF.Form
                    {
                        Text = "PopupPlacementTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = TestMonitorPoint(), ClientSize = new Size(400, 300), ShowInTaskbar = false,
                    };
                    var target = new WPF.Controls.Border { Width = 100, Height = 40, Background = WPF.Media.Brushes.LightGray,
                        HorizontalAlignment = WPF.HorizontalAlignment.Left, VerticalAlignment = WPF.VerticalAlignment.Top, Margin = new WPF.Thickness(60, 50, 0, 0) };
                    var grid = new WPF.Controls.Grid();
                    grid.Children.Add(target);
                    var host = new ElementHost { Bounds = new Rectangle(20, 30, 360, 240), Child = grid };
                    form.Controls.Add(host);
                    if (partlyCovered)
                    {
                        var over = new SWF.Button { Bounds = new Rectangle(300, 200, 60, 40) };
                        form.Controls.Add(over);
                        over.BringToFront();
                    }
                    form.Show();
                    Pump(() => host.IsChildLive, 10);

                    var popup = new WPF.Controls.Primitives.Popup
                    {
                        PlacementTarget = target, Placement = mode, AllowsTransparency = true,
                        HorizontalOffset = mode == WPF.Controls.Primitives.PlacementMode.Relative ? 10 : 0,
                        Child = new WPF.Controls.Border { Width = 4, Height = 4, Background = new WPF.Media.SolidColorBrush(WPF.Media.Color.FromArgb(1, 0, 0, 0)) },
                    };
                    popup.IsOpen = true;
                    IntPtr popupWindow = IntPtr.Zero;
                    Pump(() =>
                    {
                        popupWindow = (WPF.PresentationSource.FromVisual(popup.Child) as WPF.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
                        return popupWindow != IntPtr.Zero;
                    }, 5);
                    Pump(() => false, 0.3);
                    Assert.NotEqual(IntPtr.Zero, popupWindow);
                    GetWindowRect(popupWindow, out RECT r);

                    // Where the target is on screen, in device pixels: the host window's client origin,
                    // plus the host control and the target in it, at the window's scale.
                    IntPtr hostWindow = FindWindowByTitle("PopupPlacementTest");
                    var origin = new POINT();
                    ClientToScreen(hostWindow, ref origin);
                    GetClientRect(hostWindow, out RECT cr);
                    double k = cr.right / (double)form.ClientSize.Width;
                    int tx = origin.X + (int)Math.Round((20 + 60) * k), ty = origin.Y + (int)Math.Round((30 + 50) * k);
                    (int ex, int ey) = mode switch
                    {
                        WPF.Controls.Primitives.PlacementMode.Bottom => (tx, ty + (int)Math.Round(40 * k)),
                        WPF.Controls.Primitives.PlacementMode.Relative => (tx + (int)Math.Round(10 * k), ty),
                        _ => (tx, ty),
                    };
                    result = $"popup at ({r.left},{r.top}); target at ({tx},{ty})";
                    popup.IsOpen = false;
                    form.Close();
                    Assert.True(Math.Abs(r.left - ex) <= 2 && Math.Abs(r.top - ey) <= 2, $"{result}; expected ({ex},{ey})");
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A point inside a monitor that is not the primary (the top-left of its working
        /// area plus a margin), or a point on the primary when there is only one.</summary>
        private static Point TestMonitorPoint()
        {
            foreach (SWF.Screen s in SWF.Screen.AllScreens)
                if (!s.Primary) return new Point(s.WorkingArea.Left + 100, s.WorkingArea.Top + 100);
            return new Point(SWF.Screen.PrimaryScreen!.WorkingArea.Right - 500, SWF.Screen.PrimaryScreen.WorkingArea.Top + 100);
        }

        private static IntPtr FindWindowByTitle(string title)
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
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private static void Pump(Func<bool> until, double seconds)
        {
            for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end && !until();)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly System.Reflection.MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    }
}
