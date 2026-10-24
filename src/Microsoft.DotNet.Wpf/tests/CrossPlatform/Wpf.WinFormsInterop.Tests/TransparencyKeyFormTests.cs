// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A form whose TransparencyKey is its own BackColor (Magenta, the colour key splash and fade windows
// use) is see-through wherever that colour is painted: the window behind it shows there, on screen,
// and what the form draws in any other colour covers it. Read back from the real screen.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TransparencyKeyFormTests
    {
        [Fact]
        public void KeyColouredPixelsShowTheWindowBehind()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the real screen");
            Assert.SkipWhen(IsSessionLocked(), "reads the real screen, and a locked session's screen is the lock screen");
            Exception? failure = null;
            var t = new Thread(() => { try { Run(); } catch (Exception ex) { failure = ex; } });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            var at = new Point(60, 60);
            using var back = new SWF.Form { Text = "KeyBack", FormBorderStyle = SWF.FormBorderStyle.None, StartPosition = SWF.FormStartPosition.Manual, Location = at, Size = new Size(300, 200), BackColor = Color.Blue, TopMost = true, ShowInTaskbar = false };
            using var keyed = new SWF.Form { Text = "Keyed", FormBorderStyle = SWF.FormBorderStyle.None, StartPosition = SWF.FormStartPosition.Manual, Location = at, Size = new Size(300, 200), BackColor = Color.Magenta, TransparencyKey = Color.Magenta, TopMost = true, ShowInTaskbar = false };
            keyed.Controls.Add(new SWF.Panel { Bounds = new Rectangle(125, 75, 50, 50), BackColor = Color.Red });
            back.Show();
            Color alone = Color.Empty;
            for (var e0 = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < e0 && alone.ToArgb() != Color.Blue.ToArgb();) { Pump(100); alone = ScreenPixel(at.X + 20, at.Y + 20); }
            Assert.True(alone.ToArgb() == Color.Blue.ToArgb(), $"the form behind never reached the screen: {alone}; windows: {Windows()}");
            keyed.Show();
            // Wait for both pictures to reach the screen, then read it.
            Color key = Color.Empty, panel = Color.Empty;
            var end = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < end)
            {
                Pump(100);
                key = ScreenPixel(at.X + 20, at.Y + 20);
                panel = ScreenPixel(at.X + 150, at.Y + 100);
                if (key.ToArgb() == Color.Blue.ToArgb() && panel.ToArgb() == Color.Red.ToArgb()) break;
            }
            string wins = Windows();
            keyed.Close();
            back.Close();
            // The key colour is a hole: the form behind shows. (A borderless form behind another
            // used to be hidden as soon as the other was activated -- taken for a drop-down.)
            Assert.True(key.ToArgb() == Color.Blue.ToArgb(), $"key-coloured pixel {key}; windows: {wins}");
            Assert.Equal(Color.Red.ToArgb(), panel.ToArgb());     // anything else the form draws covers it
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

        // A point of the forms (at, in the application's units) on the real screen: where the host
        // put the window behind, scaled as it scaled it -- an application that is not per-monitor
        // aware is laid out at 96 dpi and shown at the display's.
        private static Color ScreenPixel(int x, int y)
        {
            IntPtr back = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var t = new System.Text.StringBuilder(64); GetWindowTextW(h, t, 64);
                if (t.ToString() != "KeyBack") return true;
                back = h;
                return false;
            }, IntPtr.Zero);
            if (back != IntPtr.Zero && GetWindowRect(back, out RECT r) && r.right > r.left)
            {
                double scale = (r.right - r.left) / 300.0;
                x = r.left + (int)Math.Round((x - 60) * scale);
                y = r.top + (int)Math.Round((y - 60) * scale);
            }
            IntPtr dc = GetDC(IntPtr.Zero);
            try { uint c = GetPixel(dc, x, y); return Color.FromArgb((int)(c & 0xff), (int)(c >> 8 & 0xff), (int)(c >> 16 & 0xff)); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private static string Windows()
        {
            var sb = new System.Text.StringBuilder();
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var c = new System.Text.StringBuilder(64); GetClassNameW(h, c, 64);
                GetWindowRect(h, out RECT r);
                sb.Append($"[{c} vis={IsWindowVisible(h)} {r.left},{r.top},{r.right},{r.bottom}] ");
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
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(IntPtr obj, int index, System.Text.StringBuilder info, int length, out int needed);

        // Locked: the input desktop is Winlogon's, which this session cannot open (or is not "Default").
        private static bool IsSessionLocked()
        {
            IntPtr d = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (d == IntPtr.Zero) return true;
            try
            {
                var name = new System.Text.StringBuilder(64);
                return GetUserObjectInformationW(d, 2 /* UOI_NAME */, name, 128, out _) && !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(d); }
        }
    }
}
