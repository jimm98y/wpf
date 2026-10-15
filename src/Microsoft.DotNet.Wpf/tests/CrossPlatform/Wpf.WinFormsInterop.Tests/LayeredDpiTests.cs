// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window an application draws itself through UpdateLayeredWindow (a splash screen), in an
// application that is not per-monitor aware, reaches the screen the way Windows' DPI virtualisation
// shows it: stretched by the monitor's scale, every pixel repeated (measured on stock Windows at
// 200%: a 64x48 picture is 128x96 on screen, nearest neighbour). The host is per-monitor aware, so
// it has to do that itself; it used to show the bits 1:1, and a splash came out half size, at
// half its coordinates.
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class LayeredDpiTests
    {
        private sealed class LayeredForm : SWF.Form
        {
            protected override SWF.CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000; return cp; }   // WS_EX_LAYERED
            }
        }

        [Fact]
        public void UnawareAppsLayeredBits_AreStretchedToTheMonitorsScale() => RunIn(SWF.HighDpiMode.DpiUnaware);

        /// <summary>A system-aware application's pixels are the system DPI's: shown 1:1 on a display
        /// at that DPI (GwxConfigApp's splash, drawn by the app at 1200x720 for 192 dpi), stretched
        /// only on a display at another.</summary>
        [Fact]
        public void SystemAwareAppsLayeredBits_AreShownAtTheSystemDpi() => RunIn(SWF.HighDpiMode.SystemAware);

        private static void RunIn(SWF.HighDpiMode mode)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' layered windows");
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
            const int W = 64, H = 48;
            using var form = new LayeredForm
            {
                Text = "LayeredDpi", FormBorderStyle = SWF.FormBorderStyle.None, StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), Size = new Size(W, H), ShowInTaskbar = false,
            };
            form.Show();
            // What UpdateLayeredWindow hands the driver (WpfMac.Win32's shim does exactly this):
            // the straight picture for the scene, and the premultiplied bits for the host.
            var rgba = new byte[W * H * 4];
            var bgra = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    byte v = (byte)((x & 1) == 0 ? 0 : 255);
                    int i = (y * W + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = v; rgba[i + 3] = 255;
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = v; bgra[i + 3] = 255;
                }
            var driver = XplatUIWebGpu.GetInstance();
            driver.SetLayeredImage(form.Handle, rgba, W, H, 1f);
            driver.SetLayeredBits(form.Handle, bgra, W, H, 255);
            form.Invalidate();

            IntPtr host = IntPtr.Zero;
            RECT r = default;
            int dpi = 0;
            for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end;)
            {
                Pump(100);
                host = HostOf("LayeredDpi");
                if (host == IntPtr.Zero) continue;
                dpi = (int)GetDpiForWindow(host);
                GetWindowRect(host, out r);
                if ((GetWindowLongPtrW(host, -20).ToInt64() & 0x00080000) != 0 && r.right - r.left == (int)Math.Round(W * dpi / (double)SWF.ScaleHelper.HostSourceDpi)) break;
            }
            // A few more frames: the size the host's own window had before the first layered
            // present is the scaled one already, so read it after the bits have been put up.
            Pump(600);
            GetWindowRect(host, out r);
            // The application's pixels' DPI: 96 for an unaware one, the system DPI for a system-aware one.
            double source = SWF.ScaleHelper.HostSourceDpi;
            Assert.NotEqual(IntPtr.Zero, host);
            Assert.Equal((int)Math.Round(W * dpi / source), r.right - r.left);
            Assert.Equal((int)Math.Round(H * dpi / source), r.bottom - r.top);
            // And where it is: the application's point in its own units, at the SYSTEM's scale --
            // a virtualised application's coordinates are the desktop's over the system DPI, on
            // every monitor (only the window's size follows the monitor it is on).
            double sys = GetDpiForSystem();
            Assert.Equal((int)Math.Round(8000 * sys / source), r.left);
            Assert.Equal((int)Math.Round(80 * sys / source), r.top);
            form.Close();
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

        private static IntPtr HostOf(string title)
        {
            IntPtr found = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var t = new System.Text.StringBuilder(64); InternalGetWindowText(h, t, 64);   // no WM_GETTEXT: another test's UI thread may be busy
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
        [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32")] private static extern uint GetDpiForSystem();
        [DllImport("user32")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    }
}
