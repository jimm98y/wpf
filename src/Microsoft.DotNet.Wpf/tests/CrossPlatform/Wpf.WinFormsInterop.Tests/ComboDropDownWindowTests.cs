// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A ComboBox's drop-down list near the bottom of the screen (GraphWorX's zoom box in its status bar).
//
// Stock (comctl32 v6, measured against .NET 10 with a probe that reads the real ComboLBox window): the
// list opens BELOW the combo unless it would reach the bottom of the monitor's work area, and then
// directly ABOVE it; an integral-height list under visual styles shows up to 30 items whatever
// MaxDropDownItems says; and the list is a top-level window of its own, free to extend past the form.
// Ours tested against the whole screen (taskbar included), showed MaxDropDownItems, and drew the list
// into the form's own surface -- where the form's edge cut it off. A list that reaches past its host
// window is now presented by a window of its own, over exactly where the list is, and takes the clicks.
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
    public class ComboDropDownWindowTests
    {
        private static Point Place(Rectangle combo, int listHeight, Rectangle work)
        {
            Type t = typeof(SWF.ComboBox).GetNestedType("ComboListBox", BindingFlags.NonPublic)!;
            MethodInfo m = t.GetMethod("DropDownLocation", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return (Point)m.Invoke(null, new object[] { combo, new Point(combo.X, combo.Bottom), listHeight, work })!;
        }

        [Theory]
        // combo (x,y,w,h), list height, work area bottom -> expected list top. The probe's own numbers:
        [InlineData(8018, 1111, 120, 23, 302, 1152, 809)]   // no room below: above (stock 809)
        [InlineData(8018, 1031, 120, 23, 302, 1152, 729)]   // stock 729
        [InlineData(10, 100, 120, 23, 302, 1152, 123)]      // room below: below
        [InlineData(10, 1031, 120, 23, 122, 1152, 909)]     // 1054+122=1176 >= 1152: above, though the SCREEN (1200) had room
        public void TheListOpensAboveWhenTheWorkAreaHasNoRoomBelow(int x, int y, int w, int h, int listH, int workBottom, int expectTop)
        {
            Point p = Place(new Rectangle(x, y, w, h), listH, new Rectangle(0, 0, 3840, workBottom));
            Assert.Equal(expectTop, p.Y);
            Assert.Equal(x, p.X);
        }

        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        [Fact]
        public void AListPastTheFormsEdge_IsAWindowOfItsOwn_AndTakesTheClicks()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    SWF.Application.EnableVisualStyles();
                    using var form = new QuietForm
                    {
                        Text = "ComboDropDownWindow", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40),
                        ClientSize = new Size(200, 60), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.FixedToolWindow,
                    };
                    var cb = new SWF.ComboBox { DropDownStyle = SWF.ComboBoxStyle.DropDownList, Location = new Point(10, 10), Width = 120 };
                    for (int i = 0; i < 20; i++) cb.Items.Add("item " + i);
                    cb.SelectedIndex = 0;
                    form.Controls.Add(cb);
                    form.Show();
                    Assert.True(Pump(() => RealWindow(form) != IntPtr.Zero, 10000), "no host window");
                    Pump(() => false, 300);

                    cb.DroppedDown = true;
                    var list = (SWF.Control)typeof(SWF.ComboBox).GetField("listbox_ctrl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cb)!;
                    IntPtr popupWindow = IntPtr.Zero;
                    Assert.True(Pump(() => (popupWindow = PopupWindowOf(form, list)) != IntPtr.Zero, 3000),
                        $"the list ({list.Bounds}) reaches past the 60-pixel form, so it must be a window of its own");
                    Assert.True(IsWindowVisible(popupWindow));
                    // Exactly where the list is (the test runs at 100 %, so device pixels are form points).
                    GetWindowRect(popupWindow, out RECT pr);
                    GetClientRect(RealWindow(form), out RECT _);
                    var origin = new POINT(); ClientToScreen(RealWindow(form), ref origin);
                    Point formOrigin = form.PointToScreen(Point.Empty);
                    Assert.Equal(list.Height, pr.bottom - pr.top);
                    Assert.Equal(list.Width, pr.right - pr.left);
                    Assert.Equal(origin.y + (list.Top - formOrigin.Y), pr.top);

                    // A click on item 5, given to the popup's window as Windows would.
                    int itemH = cb.ItemHeight;
                    IntPtr lp = (IntPtr)(((5 * itemH + itemH / 2 + 1) << 16) | 20);
                    SendMessageW(popupWindow, 0x0200, IntPtr.Zero, lp);
                    SendMessageW(popupWindow, 0x0201, (IntPtr)1, lp);
                    SendMessageW(popupWindow, 0x0202, IntPtr.Zero, lp);
                    Assert.True(Pump(() => cb.SelectedIndex == 5 && !cb.DroppedDown, 3000),
                        $"the click picks item 5 and closes the list (selected {cb.SelectedIndex}, dropped {cb.DroppedDown})");
                    Assert.True(Pump(() => !IsWindowVisible(popupWindow), 3000), "the list's window goes with the list");
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static object? HostFor(SWF.Form form)
            => typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, new object[] { form });

        private static IntPtr RealWindow(SWF.Form form)
            => HostFor(form) is object h ? (IntPtr)h.GetType().GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h)! : IntPtr.Zero;

        private static IntPtr PopupWindowOf(SWF.Form form, SWF.Control popup)
        {
            object? host = HostFor(form);
            if (host == null || !popup.IsHandleCreated) return IntPtr.Zero;
            var map = host.GetType().GetMethod("PopupWindowHandles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null)
                as System.Collections.Generic.IReadOnlyDictionary<IntPtr, IntPtr>;
            return map != null && map.TryGetValue(popup.Handle, out IntPtr w) ? w : IntPtr.Zero;
        }

        private static bool Pump(Func<bool> done, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                if (done()) return true;
                Thread.Sleep(15);
            }
            return done();
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
