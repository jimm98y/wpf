// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows 11's themed marquee, as measured off comctl32 on 100/200/300/600-pixel bars: a chunk 127
// pixels wide whose right edge steps on 6 pixels at a time -- the same in every bar, whatever its
// width -- entering from the left, and starting again (right edge at 5) one step after its left edge
// has passed the far end by more than 5. Ours was a third of the bar, crossing in a fixed time.
//

using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ProgressBarMarqueeTests
    {
        private static readonly MethodInfo s_tick = typeof(SWF.ProgressBar).GetMethod("marquee_timer_Tick", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static (int X0, int X1) Chunk(SWF.ProgressBar bar)
        {
            using var b = new Bitmap(bar.Width, bar.Height);
            bar.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
            int x0 = -1, x1 = -1;
            for (int x = 0; x < b.Width; x++)
            {
                Color c = b.GetPixel(x, b.Height / 2);
                if (c.G > c.R + 30) { if (x0 < 0) x0 = x; x1 = x; }
            }
            return (x0, x1);
        }

        [Fact]
        public void TheChunkIs127PixelsAndMovesTheSameInEveryWidth()
        {
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 100), ClientSize = new Size(640, 80), ShowInTaskbar = false };
                    var narrow = new SWF.ProgressBar { Bounds = new Rectangle(10, 10, 200, 20), Style = SWF.ProgressBarStyle.Marquee };
                    var wide = new SWF.ProgressBar { Bounds = new Rectangle(10, 40, 600, 20), Style = SWF.ProgressBarStyle.Marquee };
                    form.Controls.Add(narrow); form.Controls.Add(wide);
                    form.CreateControl();
                    for (int i = 0; i < 30; i++) { s_tick.Invoke(narrow, new object?[] { null, EventArgs.Empty }); s_tick.Invoke(wide, new object?[] { null, EventArgs.Empty }); }
                    // One 6-pixel step a tick from -1: 30 ticks put the right edge at 179.
                    var (n0, n1) = Chunk(narrow);
                    var (w0, w1) = Chunk(wide);
                    Assert.Equal((w1 + 1) % 6, 0);                 // right edges at 5, 11, 17, ...
                    Assert.Equal(179, w1);
                    if (w1 < 600) Assert.Equal(126, w1 - w0);      // 127 wide while wholly inside
                    if (n1 < 199 && w1 < 199) Assert.Equal((w0, w1), (n0, n1));   // the same pixels in both widths
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // A splash screen's way: the bar made (Style = Marquee) on one thread, its window created and its
        // messages run on another. The marquee is the window's timer, so it steps in that thread's loop.
        [Fact]
        public void TheMarqueeStepsOnTheThreadOfTheBarsWindow()
        {
            SWF.ProgressBar bar = null!;
            var maker = new Thread(() => bar = new SWF.ProgressBar { Bounds = new Rectangle(0, 0, 200, 20), Style = SWF.ProgressBarStyle.Marquee });
            maker.TrySetApartmentState(ApartmentState.STA);
            maker.Start(); maker.Join();

            Exception? failure = null;
            var ui = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 100), ClientSize = new Size(220, 40), ShowInTaskbar = false };
                    form.Controls.Add(bar);
                    FieldInfo pos = typeof(SWF.ProgressBar).GetField("marquee_pos", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    int start = 0, moved = 0, stoppedAt = 0, stoppedLater = 0, ticks = 0;
                    var check = new SWF.Timer { Interval = 30 };
                    check.Tick += (_, _) =>
                    {
                        ticks++;
                        if (ticks == 1) { start = (int)pos.GetValue(bar)!; return; }
                        if (ticks == 20) { moved = (int)pos.GetValue(bar)!; bar.MarqueeAnimationSpeed = 0; stoppedAt = moved; return; }
                        if (ticks == 30) { stoppedLater = (int)pos.GetValue(bar)!; check.Stop(); form.Close(); }
                    };
                    form.Shown += (_, _) => check.Start();
                    SWF.Application.Run(form);
                    Assert.NotEqual(start, moved);                     // it stepped, in this thread's loop
                    Assert.Equal(stoppedAt, stoppedLater);             // a speed of 0 stops it, as PBM_SETMARQUEE(FALSE)
                }
                catch (Exception ex) { failure = ex; }
            });
            ui.TrySetApartmentState(ApartmentState.STA);
            ui.Start(); ui.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void AChunkPastTheEndStartsAgainAtFive()
        {
            Exception? failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    using var bar = new SWF.ProgressBar { Bounds = new Rectangle(0, 0, 100, 20), Style = SWF.ProgressBarStyle.Marquee };
                    FieldInfo pos = typeof(SWF.ProgressBar).GetField("marquee_pos", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    pos.SetValue(bar, 100 + 5 + 126 - 6 + 6);         // left edge exactly 5 past the end: still running
                    var clock = typeof(SWF.ProgressBar).GetField("marquee_clock", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    s_tick.Invoke(bar, new object?[] { null, EventArgs.Empty });
                    int after = (int)pos.GetValue(bar)!;
                    // One step on from 5 past is more than 5 past: reset to 5 (or a few steps from it on a slow machine).
                    Assert.True(after % 6 == 5 && after < 100, $"marquee_pos {after}");
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
