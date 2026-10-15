// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A dialog with a basic and an advanced mode, as a login dialog has: "Advanced" shows a panel, raises
// the minimum size to the advanced size, clears the maximum (0, 0 = none) and sets the size. Clearing
// the maximum used to clear the minimum with it, and the dialog stayed small. Expected values follow
// .NET: an empty maximum (or minimum) bounds nothing.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class FormGrowsWithPanelTests
    {
        private static readonly System.Reflection.MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // The real window's client area in the application's units: an application that is not
        // per-monitor aware is laid out at 96 dpi and its host window is that scaled to the display.
        private static Size RealClient(string title)
        {
            Size found = Size.Empty;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(64); GetWindowTextW(h, sb, 64);
                if (sb.ToString() != title) return true;
                GetClientRect(h, out RECT r);
                found = new Size(r.right - r.left, r.bottom - r.top);
                int dpi = (int)GetDpiForWindow(h);
                if (!SWF.ScaleHelper.IsDeviceScaled && dpi > 0 && dpi != 96)
                    found = new Size((int)Math.Round(found.Width * 96.0 / dpi), (int)Math.Round(found.Height * 96.0 / dpi));
                return false;
            }, IntPtr.Zero);
            return found;
        }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr h);
        [Fact]
        public void ClearingTheMaximum_KeepsTheMinimum()
        {
            using var f = new SWF.Form();
            f.MinimumSize = new Size(300, 200);
            f.MaximumSize = Size.Empty;
            Assert.Equal(new Size(300, 200), f.MinimumSize);
            f.MaximumSize = new Size(250, 400);       // a real maximum below the minimum's width
            Assert.Equal(new Size(250, 200), f.MinimumSize);
        }

        [Fact]
        public void RaisingTheMinimum_RaisesARealMaximum_Only()
        {
            using var f = new SWF.Form();
            f.MaximumSize = new Size(800, 150);
            f.MinimumSize = new Size(400, 300);
            Assert.Equal(new Size(800, 300), f.MaximumSize);
            using var g = new SWF.Form();
            g.MinimumSize = new Size(400, 300);
            Assert.Equal(Size.Empty, g.MaximumSize);
        }

        [Fact]
        public void ModalDialog_GrowsWhenItsAdvancedPanelIsShown()
        {
            Exception? failure = null;
            var t = new Thread(() => { try { Run(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var owner = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ShowInTaskbar = false };
            owner.Show();
            using var dialog = new SWF.Form { Text = "GrowDialog", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(60, 60), ShowInTaskbar = false };
            var panel = new SWF.Panel { Dock = SWF.DockStyle.Bottom, Height = 200, Visible = false };
            dialog.Controls.Add(panel);
            dialog.Size = new Size(580, 400);
            Size advanced = dialog.Size;
            dialog.MinimumSize = new Size(420, 146);
            Size basicMin = dialog.MinimumSize, basicMax = new Size(2000, basicMin.Height);
            dialog.MaximumSize = basicMax;
            dialog.Size = basicMin;
            Size during = Size.Empty, window = Size.Empty, wanted = Size.Empty;
            dialog.Shown += (s, e) =>
            {
                panel.Visible = true;                  // AdvancedMode
                dialog.MinimumSize = advanced;
                dialog.MaximumSize = new Size(0, 0);
                dialog.Size = advanced;
                for (int i = 0; i < 20; i++) { SWF.Application.DoEvents(); s_tick?.Invoke(null, null); Thread.Sleep(15); }
                during = dialog.Size;
                wanted = dialog.ClientSize;
                window = RealClient("GrowDialog");
                dialog.Close();
            };
            dialog.ShowDialog(owner);
            owner.Close();
            Assert.Equal(advanced, during);
            if (OperatingSystem.IsWindows()) Assert.Equal(wanted, window);   // and the real window followed it
        }
    }
}
