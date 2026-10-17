// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WinForms window's renderer lets go of textures it has not drawn for a while. Its frames were never
// bracketed (BeginFrame/EndFrame, which is the renderer's clock and where its caches evict), so it kept
// every texture it ever made -- every bitmap a control drew, at every size a resize passed through:
// app's private bytes rose for good with each move and resize.
//

using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class PresenterCacheEvictionTests
    {
        private sealed class QuietForm : SWF.Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        /// <summary>Draws a new bitmap on every paint: a texture the renderer has never seen.</summary>
        private sealed class FreshBitmaps : SWF.Control
        {
            public int Paints;
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                Paints++;
                using var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, Paints % 256, (Paints / 256) % 256, 7));
                e.Graphics.DrawImage(bmp, 0, 0);
            }
        }

        private static object Field(object o, string name)
            => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

        [Fact]
        public void TexturesNotDrawnForAWhile_AreReleased()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows host window and swap chain");
            int cached = -1, paints = 0;
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new QuietForm { ClientSize = new Size(120, 80), StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), ShowInTaskbar = false, FormBorderStyle = SWF.FormBorderStyle.None };
                    var c = new FreshBitmaps { Bounds = new Rectangle(4, 4, 32, 32) };
                    form.Controls.Add(c);
                    var timer = new SWF.Timer { Interval = 15 };
                    int ticks = 0;
                    timer.Tick += (s, e) =>
                    {
                        if (++ticks < 200) { c.Invalidate(); return; }
                        timer.Stop();
                        object host = typeof(SWF.Form).Assembly.GetType("System.Windows.Forms.PresentationHost")!
                            .GetMethod("HostOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, new object[] { form })!;
                        object renderer = Field(Field(host, "_wgpu"), "_renderer");
                        cached = ((ICollection)Field(renderer, "_imgCache")).Count;
                        paints = c.Paints;
                        form.Close();
                    };
                    form.Shown += (s, e) => timer.Start();
                    SWF.Application.Run(form);
                    timer.Dispose();
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)));
            if (failure != null) throw failure;
            Assert.True(paints > 120, $"only {paints} paints");
            // Each paint drew a texture never seen before; only the recent ones may still be held.
            Assert.True(cached <= 70, $"{cached} image textures held after {paints} paints of a new bitmap each");
        }
    }
}
