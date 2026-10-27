// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A pen made from a gradient brush strokes on a window as GDI+ strokes it: the outline it sweeps, filled
// with the brush. The recorder took every stroke's one colour from Pen.Color, which a pen without one
// refuses ("Parameter is not valid"), and app's gradient dial failed to paint.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class BrushPenStrokeTests
    {
        private sealed class Dial : SWF.UserControl
        {
            public Exception? Error;
            protected override void OnPaint(SWF.PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                try
                {
                    using var lg = new LinearGradientBrush(new Rectangle(2, 2, 40, 40), Color.White, Color.Navy, 45f);
                    using var pen = new Pen(lg) { Width = 3 };
                    g.DrawLine(pen, new Point(5, 5), new Point(40, 30));
                    g.DrawEllipse(pen, 4, 4, 30, 30);
                    g.DrawRectangle(pen, 6, 6, 20, 20);
                    g.DrawBezier(pen, 2, 2, 10, 30, 30, 10, 40, 40);
                }
                catch (Exception ex) { Error = ex; }
            }
        }

        [Fact]
        public void AGradientPen_StrokesOnAWindow()
        {
            using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 80), ClientSize = new Size(100, 100), ShowInTaskbar = false };
            var d = new Dial { Bounds = new Rectangle(5, 5, 45, 45) };
            form.Controls.Add(d);
            form.Show();
            d.Invalidate(); d.Update();
            SWF.Application.DoEvents();
            Assert.True(d.Error == null, d.Error?.ToString());
        }
    }
}
