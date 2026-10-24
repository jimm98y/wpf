// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A window's swap chain must outlive anything that happens to the window: app aborted
// (0xC0000409, "Error in wgpuTextureRelease: Surface image is already acquired") when a present
// failed -- the OpenGL backend fails one whenever the window has gone -- and the texture was
// released after it. A present for a window that no longer exists, and a long churn of resizes,
// fades, short-lived popups and repaints over several windows, must leave the process alive.
// Every window is far off the desktop (x >= 8000).
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class SurfaceLifetimeTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        /// <summary>A borderless popup as a splitter's drag bar is one: a few pixels wide, shown and gone.</summary>
        private sealed class BarPopup : SWF.Control
        {
            protected override SWF.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = unchecked((int)0x80000000);                  // WS_POPUP
                    cp.ExStyle = 0x00000080 | 0x08000000;                   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                    cp.Parent = IntPtr.Zero;
                    return cp;
                }
            }
        }

        [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32")] private static extern bool IsWindow(IntPtr hwnd);

        private static object? HostFor(SWF.Control form)
        {
            var hostOf = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return hostOf.Invoke(null, new object[] { form });
        }

        private static IntPtr RealWindow(object host)
            => (IntPtr)host.GetType().GetField("_hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

        private static void OnSta(Action body, int seconds)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(seconds)), "the UI thread did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Pump(Func<bool> done, double seconds)
        {
            long end = Environment.TickCount64 + (long)(seconds * 1000);
            while (!done() && Environment.TickCount64 < end)
            {
                SWF.Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        [Fact]
        public void APresentForAWindowThatHasGone_IsSkipped_AndTheProcessLives()
        {
            OnSta(() =>
            {
                using var form = new QuietForm
                {
                    StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(200, 120),
                    ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None, BackColor = Color.SteelBlue,
                };
                object? host = null;
                bool done = false;
                Exception? inLoop = null;
                var timer = new SWF.Timer { Interval = 20 };
                int ticks = 0;
                timer.Tick += (o, e) =>
                {
                    if (done) return;
                    if (++ticks < 300 && ((host = HostFor(form)) == null || RealWindow(host) == IntPtr.Zero)) return;
                    done = true;
                    timer.Stop();
                    try
                    {
                        Assert.NotNull(host);
                        IntPtr hwnd = RealWindow(host!);
                        // Windows takes a window down under its host -- its owner went, say -- and a
                        // frame is then asked for. The old present path rendered it, failed the
                        // present and released the texture: wgpu-native aborted the process here.
                        Assert.True(DestroyWindow(hwnd));
                        Assert.False(IsWindow(hwnd));
                        form.BackColor = Color.OrangeRed;
                        form.Invalidate();
                        MethodInfo present = host!.GetType().GetMethod("Present", BindingFlags.Instance | BindingFlags.Public)!;
                        // A frame is owed whatever the paint version says.
                        FieldInfo owed = host.GetType().GetField("_repaintAsked", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        owed.SetValue(host, true);
                        present.Invoke(host, null);
                        owed.SetValue(host, true);
                        present.Invoke(host, null);
                    }
                    catch (Exception ex) { inLoop = ex; }
                    form.Close();
                };
                form.Shown += (o, e) => timer.Start();
                SWF.Application.Run(form);
                timer.Dispose();
                if (inLoop != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inLoop).Throw();
                Assert.True(done, "the form never got a host window");
            }, 60);
        }

        [Fact]
        public void ResizesFadesPopupsAndRepaints_OverSeveralWindows_LeaveTheProcessAlive()
        {
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("WF_SURFACE_STRESS_SECONDS"), out int s) ? s : 30;
            int frames = 0, popupsMade = 0, popupsKilled = 0;
            OnSta(() =>
            {
                var rnd = new Random(12345);
                var forms = new List<QuietForm>();
                for (int i = 0; i < 3; i++)
                {
                    var f = new QuietForm
                    {
                        StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000 + i * 420, 80), ClientSize = new Size(360, 240),
                        ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.Sizable, BackColor = Color.White,
                        Text = "SurfaceLifetimeTests " + i,
                    };
                    var panel = new SWF.Panel { Dock = SWF.DockStyle.Fill, BackColor = Color.FromArgb(40 * i, 120, 200) };
                    panel.Paint += (o, e) => e.Graphics.FillRectangle(Brushes.Black, rnd.Next(0, 100), rnd.Next(0, 100), 30, 20);
                    f.Controls.Add(panel);
                    f.Show();
                    forms.Add(f);
                }
                var live = new List<(SWF.Control Popup, long DieAt)>();
                long end = Environment.TickCount64 + seconds * 1000L;
                var timer = new SWF.Timer { Interval = 10 };
                timer.Tick += (o, e) =>
                {
                    frames++;
                    long now = Environment.TickCount64;
                    QuietForm f = forms[rnd.Next(forms.Count)];
                    switch (rnd.Next(6))
                    {
                        case 0:   // a live resize step, down to nothing and back
                            f.ClientSize = new Size(rnd.Next(0, 500), rnd.Next(0, 320));
                            break;
                        case 1:   // a splitter's drag bar: a 3-pixel popup, shown and soon destroyed
                        {
                            var bar = new BarPopup { Bounds = new Rectangle(8000 + rnd.Next(0, 1000), 100, 3, rnd.Next(1, 970)), BackColor = Color.Gray };
                            bar.CreateControl();
                            bar.Visible = true;
                            live.Add((bar, now + rnd.Next(0, 60)));
                            popupsMade++;
                            break;
                        }
                        case 2:   // a fade in, as DotNetBar's gallery does
                        {
                            var pop = new BarPopup { Bounds = new Rectangle(8000 + rnd.Next(0, 1000), 400, 120, 80), BackColor = Color.Red };
                            pop.CreateControl();
                            Animate(pop.Handle, rnd.Next(20, 200), 0x80000);   // AW_BLEND
                            live.Add((pop, now + rnd.Next(0, 300)));
                            popupsMade++;
                            break;
                        }
                        case 3:   // its window pulled out from under its host, as Windows does to owned windows
                            if (live.Count > 0 && HostFor(live[0].Popup) is object h && RealWindow(h) is IntPtr hw && hw != IntPtr.Zero)
                            {
                                DestroyWindow(hw);
                                live[0].Popup.Invalidate();
                            }
                            break;
                        default:
                            f.Controls[0].Invalidate();
                            break;
                    }
                    for (int i = live.Count - 1; i >= 0; i--)
                    {
                        if (live[i].DieAt > now) continue;
                        live[i].Popup.Dispose();
                        live.RemoveAt(i);
                        popupsKilled++;
                    }
                    if (now >= end)
                    {
                        timer.Stop();
                        foreach ((SWF.Control p, long _) in live) p.Dispose();
                        foreach (QuietForm q in forms) q.Close();
                    }
                };
                timer.Start();
                SWF.Application.Run(forms[0]);
                timer.Dispose();
                foreach (QuietForm q in forms) q.Dispose();
            }, seconds + 60);
            Assert.True(frames > seconds * 20, $"only {frames} ticks ran");
            Assert.True(popupsMade > 0 && popupsKilled > 0, $"popups {popupsMade} made, {popupsKilled} destroyed");
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
