// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ControlPaint's reversible drawing, on the screen, as Windows' XOR does it: drawn once it is there, drawn
// again it is gone -- a splitter's line, a selection's frame, a filled band. Only DrawReversibleRectangle did
// anything; the line, the fill and the frame drew nothing.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ReversibleDrawingTests
    {
        [Fact]
        public void TheLineTheFillAndTheFrame_ComeAndGoLikeXor()
        {
            SWF.XplatUIWebGpu driver = SWF.XplatUIWebGpu.GetInstance();
            int before = ((long[])driver.GetReversibleRects()).Length / 5;

            SWF.ControlPaint.DrawReversibleLine(new Point(100, 50), new Point(100, 250), Color.Black);
            SWF.ControlPaint.FillReversibleRectangle(new Rectangle(300, 300, 40, 20), Color.Black);
            SWF.ControlPaint.DrawReversibleFrame(new Rectangle(500, 100, 80, 60), Color.Black, SWF.FrameStyle.Thick);
            long[] shown = (long[])driver.GetReversibleRects();
            Assert.Equal(before + 3, shown.Length / 5);
            Assert.Contains((100L, 50L, 1L, 201L, 1L), Bands(shown));    // the line: a pixel wide
            Assert.Contains((300L, 300L, 40L, 20L, 10L), Bands(shown));  // the fill: wide enough to fill
            Assert.Contains((500L, 100L, 80L, 60L, 3L), Bands(shown));   // a thick frame

            SWF.ControlPaint.DrawReversibleLine(new Point(100, 50), new Point(100, 250), Color.Black);
            SWF.ControlPaint.FillReversibleRectangle(new Rectangle(300, 300, 40, 20), Color.Black);
            SWF.ControlPaint.DrawReversibleFrame(new Rectangle(500, 100, 80, 60), Color.Black, SWF.FrameStyle.Thick);
            Assert.Equal(before, ((long[])driver.GetReversibleRects()).Length / 5);
        }

        private static System.Collections.Generic.List<(long, long, long, long, long)> Bands(long[] raw)
        {
            var list = new System.Collections.Generic.List<(long, long, long, long, long)>();
            for (int i = 0; i + 4 < raw.Length; i += 5) list.Add((raw[i], raw[i + 1], raw[i + 2], raw[i + 3], raw[i + 4]));
            return list;
        }
    }
}
