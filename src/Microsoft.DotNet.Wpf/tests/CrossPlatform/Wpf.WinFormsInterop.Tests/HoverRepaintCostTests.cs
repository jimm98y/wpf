// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a repaint costs must not depend on how many repaints came before it. Two things made
// GraphWorX's ribbon slower with every hover:
//  - an image drawn 1:1 with antialiasing (every DotNetBar ribbon icon) was a fresh pixel array per
//    draw, and the renderer makes a texture per array: a texture upload per icon per frame;
//  - a double-buffered control kept a running region of what was invalid that only ever grew, a
//    combine node per invalidate, and every invalidate cloned all of it.
//

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class HoverRepaintCostTests
    {
        private sealed class IconPanel : SWF.Panel
        {
            public Bitmap Icon = null!;
            public int Paints;
            public IconPanel() { DoubleBuffered = true; }
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                Paints++;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < 6; i++)
                {
                    e.Graphics.DrawImage(Icon, 4 + i * 40, 10);                                                          // 1:1 at a whole position
                    e.Graphics.DrawImage(Icon, new Rectangle(4 + i * 40, 60, 32, 32), new Rectangle(0, 0, 32, 32), GraphicsUnit.Pixel);
                }
            }
        }

        /// <summary>Repainting the same icons uploads no new textures: the same bitmap drawn again is the
        /// same pixel array, both through DrawImage(image, x, y) and DrawImage(image, dest, src, unit).</summary>
        [Fact]
        public void RepaintingTheSameIcons_UploadsNoNewTextures()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a presented Win32 window");
            Run("IconTextureTest", (form, panel) =>
            {
                FieldInfo perf = RendererType().GetField("PerfTextures", BindingFlags.NonPublic | BindingFlags.Static)!;   // [ThreadStatic]: this UI thread's
                for (int i = 0; i < 3; i++) { panel.Invalidate(); Pump(150); }      // first uploads
                int paints = panel.Paints, before = (int)perf.GetValue(null)!;
                for (int i = 0; i < 12; i++) { panel.Invalidate(); Pump(120); }
                int made = (int)perf.GetValue(null)! - before;
                Assert.True(panel.Paints - paints >= 10, $"only {panel.Paints - paints} repaints");
                // 12 icon draws per repaint: a texture each was 120 and more.
                Assert.True(made < 12, $"{made} textures made over {panel.Paints - paints} repaints of the same icons");
            });
        }

        /// <summary>Invalidating and repainting a double-buffered control over and over leaves nothing
        /// behind that grows with the count.</summary>
        [Fact]
        public void ManyInvalidates_OfADoubleBufferedControl_LeaveNothingGrowing()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a presented Win32 window");
            Run("InvalidRegionTest", (form, panel) =>
            {
                FieldInfo bb = typeof(SWF.Control).GetField("backbuffer", BindingFlags.NonPublic | BindingFlags.Instance)!;
                int Size()
                {
                    object? buffer = bb.GetValue(panel);
                    if (buffer == null) return 0;
                    var region = (Region?)buffer.GetType().GetField("InvalidRegion")!.GetValue(buffer);
                    return region?.GetRegionData()?.Data.Length ?? 0;
                }
                Pump(200);
                int first = Size();
                for (int i = 0; i < 150; i++)
                {
                    // a hover moving across items: the old one and the new one
                    panel.Invalidate(new Rectangle((i % 6) * 40, 10, 40, 40));
                    panel.Invalidate(new Rectangle(((i + 1) % 6) * 40, 10, 40, 40));
                    if (i % 10 == 0) Pump(20);
                }
                Pump(200);
                Assert.True(Size() <= Math.Max(first, 64), $"the invalid region grew from {first} to {Size()} bytes");
            });
        }

        private static Type RendererType()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Microsoft.Wpf.Interop.WebGpu")
                    return a.GetType("Microsoft.Wpf.Interop.WebGpu.Composition.WgpuSceneRenderer")!;
            throw new InvalidOperationException("the renderer is not loaded");
        }

        private static void Run(string title, Action<SWF.Form, IconPanel> body)
        {
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = title, StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ClientSize = new Size(300, 120), ShowInTaskbar = false,
                    };
                    using var icon = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(icon)) { g.Clear(Color.SteelBlue); g.FillEllipse(Brushes.Gold, 4, 4, 24, 24); }
                    var panel = new IconPanel { Dock = SWF.DockStyle.Fill, Icon = icon };
                    form.Controls.Add(panel);
                    form.Show();
                    IntPtr host = IntPtr.Zero;
                    for (var end = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < end && host == IntPtr.Zero;) { Pump(100); host = HostOf(title); }
                    Assert.NotEqual(IntPtr.Zero, host);
                    Pump(1500);
                    body(form, panel);
                    form.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

        private static readonly MethodInfo? s_tick = typeof(SWF.Control).Assembly.GetType("System.Windows.Forms.PresentationHost")
            ?.GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Static);

        private static IntPtr HostOf(string title)
        {
            IntPtr found = IntPtr.Zero;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me) return true;
                var sb = new System.Text.StringBuilder(256);
                InternalGetWindowText(h, sb, 256);
                if (sb.ToString() == title) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    }
}
