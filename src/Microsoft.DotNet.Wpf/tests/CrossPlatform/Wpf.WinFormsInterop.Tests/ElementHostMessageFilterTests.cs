// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A click on hosted WPF content passes the application's message filters, as every message the
// thread's loop pumps does on Windows -- whichever loop pumps it. DotNetBar closes its menus from such
// a filter: a ribbon context menu stayed open when the user clicked on GraphWorX's canvas.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms.Integration;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ElementHostMessageFilterTests
    {
        private sealed class Recorder : SWF.IMessageFilter
        {
            public readonly List<(IntPtr Hwnd, int Msg)> Seen = new();
            public bool PreFilterMessage(ref SWF.Message m)
            {
                if (m.Msg is 0x0201 or 0x0204) Seen.Add((m.HWnd, m.Msg));
                return false;
            }
        }

        [Fact]
        public void APressOnHostedWpfContent_PassesTheMessageFilters_AndClosesAMenu()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a real hosted WPF window");
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try { Run(); }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)));
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Run()
        {
            using var form = new SWF.Form
            {
                Text = "ElementHostFilterTest", StartPosition = SWF.FormStartPosition.Manual,
                Location = new Point(8000, 80), ClientSize = new Size(300, 200), ShowInTaskbar = false,
            };
            var canvas = new System.Windows.Controls.Canvas { Background = System.Windows.Media.Brushes.White };
            var host = new ElementHost { Dock = SWF.DockStyle.Fill, Child = canvas };
            form.Controls.Add(host);
            var rec = new Recorder();
            SWF.Application.AddMessageFilter(rec);
            var menu = new SWF.ContextMenuStrip();
            menu.Items.Add("Copy");
            try
            {
                form.Show();
                Pump(() => host.IsChildLive, 10);
                Assert.True(host.IsChildLive, "the hosted tree never rendered");
                IntPtr wpf = (System.Windows.PresentationSource.FromVisual(canvas) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
                Assert.NotEqual(IntPtr.Zero, wpf);

                menu.Show(form, new Point(10, 10));
                Pump(() => menu.Visible, 5);
                Assert.True(menu.Visible);

                PostMessageW(wpf, 0x0201, (IntPtr)1, (IntPtr)((50 << 16) | 50));
                PostMessageW(wpf, 0x0202, IntPtr.Zero, (IntPtr)((50 << 16) | 50));
                Pump(() => rec.Seen.Exists(s => s.Hwnd == wpf) && !menu.Visible, 5);
                Assert.Contains(rec.Seen, s => s.Hwnd == wpf && s.Msg == 0x0201);
                Assert.False(menu.Visible, "the menu stayed open after a press on the hosted content");
                form.Close();
            }
            finally
            {
                SWF.Application.RemoveMessageFilter(rec);
                menu.Dispose();
            }
        }

        private static void Pump(Func<bool> until, double seconds)
        {
            for (var end = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < end && !until();)
            {
                SWF.Application.DoEvents();
                s_tick?.Invoke(null, null);
                Thread.Sleep(15);
            }
        }

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    }
}
