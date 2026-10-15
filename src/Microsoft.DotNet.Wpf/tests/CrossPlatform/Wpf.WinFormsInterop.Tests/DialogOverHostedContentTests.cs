// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A second window opened over a form that hosts WPF content (an ElementHost canvas), as a login
// dialog is shown over the main window. The hosted content belongs to the main window's host; the
// dialog's host drew it too, last, over its own controls -- the dialog came up as a white window
// with only its caret. And now and then a window that had just come up showed a white client area
// for good: what was presented as it appeared did not reach the screen. Read back from the real screen.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class DialogOverHostedContentTests
    {
        [Fact]
        public void DialogDrawsItsOwnContentNotTheHostedCanvas()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the real screen");
            Exception? failure = null;
            var t = new Thread(() => { try { Run(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var main = new SWF.Form { Text = "HostedMain", StartPosition = SWF.FormStartPosition.Manual, Location = new Point(40, 40), ClientSize = new Size(500, 400), TopMost = true };
            main.Controls.Add(new ElementHost { Dock = SWF.DockStyle.Fill, Child = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White } });
            main.Show();
            Pump(1500);
            using var dialog = new SWF.Form { Text = "Dialog", FormBorderStyle = SWF.FormBorderStyle.FixedDialog, StartPosition = SWF.FormStartPosition.Manual, Location = new Point(140, 140), Size = new Size(240, 180), BackColor = Color.Red, TopMost = true, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false };
            dialog.Show(main);                 // owned, as a dialog over its main window is
            Color c = Color.Empty;
            for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && c.ToArgb() != Color.Red.ToArgb();)
            {
                Pump(100);
                c = DialogCentre();
            }
            string wins = Windows();
            dialog.Close();
            main.Close();
            Assert.True(c.ToArgb() == Color.Red.ToArgb(), $"dialog pixel {c}; windows: {wins}");
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

        /// <summary>The screen pixel at the middle of the dialog's window, wherever the host put it
        /// (an application that is not per-monitor aware is placed at the display's scale).</summary>
        private static Color DialogCentre()
        {
            IntPtr dlg = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var t = new System.Text.StringBuilder(64); InternalGetWindowText(h, t, 64);
                if (t.ToString() != "Dialog") return true;
                dlg = h;
                return false;
            }, IntPtr.Zero);
            if (dlg == IntPtr.Zero || !GetWindowRect(dlg, out RECT r)) return Color.Empty;
            return ScreenPixel((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        }

        private static Color ScreenPixel(int x, int y)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try { uint v = GetPixel(dc, x, y); return Color.FromArgb((int)(v & 0xff), (int)(v >> 8 & 0xff), (int)(v >> 16 & 0xff)); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private static string Windows()
        {
            var sb = new System.Text.StringBuilder();
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var c = new System.Text.StringBuilder(64); GetClassNameW(h, c, 64);
                GetWindowRect(h, out RECT r);
                sb.Append($"[{c} {r.left},{r.top},{r.right},{r.bottom}] ");
                return true;
            }, IntPtr.Zero);
            return sb.ToString();
        }
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    }
}
