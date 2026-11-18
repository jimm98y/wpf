// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A text box's character geometry as Windows' edit controls answer it. GetLineFromCharIndex numbers lines from 0
// (EM_LINEFROMCHAR), the same numbers GetFirstCharIndexFromLine takes. GetPositionFromCharIndex answers where a
// character's top-left is drawn in the box's client coordinates (EM_POSFROMCHAR): inside the frame, and moved by
// the scroll -- a character scrolled off the top is above the box. GetCharIndexFromPosition takes a point in those
// same client coordinates (EM_CHARFROMPOS), so the two undo each other however the box is scrolled. Ours numbered
// lines from 1, added the scroll instead of taking it away, left the frame out, and hit-tested in the document.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TextBoxCharPositionTests
    {
        private const string ThreeLines = "alpha\r\nbeta\r\ngamma";

        private static string ManyLines(int count)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append("\r\n");
                sb.Append("mmm line ").Append(i);
            }
            return sb.ToString();
        }

        [Fact]
        public void TextBox_LinesAreNumberedFromZero()
        {
            using var tb = new SWF.TextBox { Multiline = true, Bounds = new Rectangle(0, 0, 200, 100), Text = ThreeLines };
            tb.CreateControl();
            Assert.Equal(0, tb.GetLineFromCharIndex(0));
            Assert.Equal(0, tb.GetLineFromCharIndex(4));
            Assert.Equal(1, tb.GetLineFromCharIndex(7));
            Assert.Equal(2, tb.GetLineFromCharIndex(13));
            // and they are the numbers GetFirstCharIndexFromLine takes
            for (int i = 0; i < tb.TextLength; i++)
                Assert.True(tb.GetFirstCharIndexFromLine(tb.GetLineFromCharIndex(i)) <= i, $"char {i}");
            Assert.Equal(7, tb.GetFirstCharIndexFromLine(tb.GetLineFromCharIndex(9)));
        }

        [Fact]
        public void SingleLineTextBox_IsLineZero()
        {
            using var tb = new SWF.TextBox { Bounds = new Rectangle(0, 0, 200, 23), Text = "one line" };
            tb.CreateControl();
            Assert.Equal(0, tb.GetLineFromCharIndex(0));
            Assert.Equal(0, tb.GetLineFromCharIndex(5));
        }

        [Fact]
        public void RichTextBox_LinesAreNumberedFromZero()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 200, 100), Text = "alpha\nbeta\ngamma" };
            rtb.CreateControl();
            Assert.Equal(0, rtb.GetLineFromCharIndex(0));
            Assert.Equal(1, rtb.GetLineFromCharIndex(6));
            Assert.Equal(2, rtb.GetLineFromCharIndex(11));
        }

        [Fact]
        public void TextBox_FirstCharacterIsWhereTheTextStartsInsideTheFrame()
        {
            using var tb = new SWF.TextBox { Multiline = true, Bounds = new Rectangle(0, 0, 200, 100), Text = ThreeLines };
            tb.CreateControl();
            Point first = tb.GetPositionFromCharIndex(0);
            // where the box puts its caret for the character (EM_POSFROMCHAR and the caret agree): the frame's inset
            // plus the margin the line starts at
            tb.Select(0, 0);
            Point caret = tb.document.Caret;
            Assert.Equal(new Point(tb.document.OffsetX + caret.X, tb.document.OffsetY + caret.Y), first);
            Assert.True(new Rectangle(Point.Empty, tb.ClientSize).Contains(first), $"{first} in {tb.ClientSize}");
            // a line's first character starts where the first line's does, a line lower
            Point beta = tb.GetPositionFromCharIndex(7);
            Assert.Equal(first.X, beta.X);
            Assert.True(beta.Y > first.Y);
        }

        [Fact]
        public void TextBox_PositionAndIndexUndoEachOther()
        {
            using var tb = new SWF.TextBox { Multiline = true, Bounds = new Rectangle(0, 0, 200, 100), Text = ThreeLines };
            tb.CreateControl();
            foreach (int i in new[] { 0, 2, 7, 9, 13, 15 })
            {
                Point p = tb.GetPositionFromCharIndex(i);
                Assert.Equal(i, tb.GetCharIndexFromPosition(new Point(p.X + 1, p.Y + 1)));
                Assert.Equal(ThreeLines[i], tb.GetCharFromPosition(new Point(p.X + 1, p.Y + 1)));
            }
        }

        [Fact]
        public void ScrolledTextBox_TakesTheScrollAway()
        {
            string text = ManyLines(40);
            using var tb = new SWF.TextBox { Multiline = true, ScrollBars = SWF.ScrollBars.Vertical, Bounds = new Rectangle(0, 0, 200, 80), Text = text };
            tb.CreateControl();
            int line20 = tb.GetFirstCharIndexFromLine(20);
            Point zeroBefore = tb.GetPositionFromCharIndex(0);
            Point twentyBefore = tb.GetPositionFromCharIndex(line20);

            tb.Select(text.Length, 0);
            tb.ScrollToCaret();

            Point zero = tb.GetPositionFromCharIndex(0);
            Point twenty = tb.GetPositionFromCharIndex(line20);
            int scrolled = zeroBefore.Y - zero.Y;
            Assert.True(scrolled > 0, $"the box scrolled down: line 0 at {zero.Y}, was {zeroBefore.Y}");
            // the first line is above the box now, and every character moved up by the same scroll
            Assert.True(zero.Y < 0, $"line 0 at {zero.Y}");
            Assert.Equal(zeroBefore.X, zero.X);
            Assert.Equal(twentyBefore.Y - scrolled, twenty.Y);

            // a point in the box names the character drawn there, the scroll counted in
            int last = tb.GetLineFromCharIndex(text.Length);
            Assert.True(last > 20);
            int lastStart = tb.GetFirstCharIndexFromLine(last);
            Point end = tb.GetPositionFromCharIndex(lastStart);
            Assert.True(new Rectangle(Point.Empty, tb.ClientSize).Contains(end), $"{end} in {tb.ClientSize}");
            Assert.Equal(lastStart, tb.GetCharIndexFromPosition(new Point(end.X + 1, end.Y + 1)));
            Assert.Equal(last, tb.GetLineFromCharIndex(tb.GetCharIndexFromPosition(new Point(end.X + 1, end.Y + 1))));
        }

        [Fact]
        public void ScrolledSingleLineTextBox_TakesTheScrollAway()
        {
            string text = new string('m', 200);
            using var tb = new SWF.TextBox { Bounds = new Rectangle(0, 0, 100, 23), Text = text };
            tb.CreateControl();
            Point before = tb.GetPositionFromCharIndex(0);
            tb.Select(text.Length, 0);
            tb.ScrollToCaret();
            Point after = tb.GetPositionFromCharIndex(0);
            Assert.True(after.X < before.X, $"char 0 at {after.X}, was {before.X}");
            Assert.True(after.X < 0);
            Point lastChar = tb.GetPositionFromCharIndex(text.Length - 1);
            Assert.InRange(lastChar.X, 0, tb.ClientSize.Width);
            Assert.Equal(text.Length - 1, tb.GetCharIndexFromPosition(new Point(lastChar.X + 1, lastChar.Y + 1)));
        }

        [Fact]
        public void ScrolledRichTextBox_TakesTheScrollAway()
        {
            string text = ManyLines(40).Replace("\r\n", "\n");
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 200, 80), Text = text };
            rtb.CreateControl();
            int line20 = rtb.GetFirstCharIndexFromLine(20);
            Point zeroBefore = rtb.GetPositionFromCharIndex(0);
            Point twentyBefore = rtb.GetPositionFromCharIndex(line20);
            Assert.Equal(0, rtb.GetCharIndexFromPosition(new Point(zeroBefore.X + 1, zeroBefore.Y + 1)));

            rtb.Select(text.Length, 0);
            rtb.ScrollToCaret();

            Point zero = rtb.GetPositionFromCharIndex(0);
            int scrolled = zeroBefore.Y - zero.Y;
            Assert.True(scrolled > 0, $"line 0 at {zero.Y}, was {zeroBefore.Y}");
            Assert.True(zero.Y < 0);
            Assert.Equal(twentyBefore.Y - scrolled, rtb.GetPositionFromCharIndex(line20).Y);

            int last = rtb.GetLineFromCharIndex(text.Length);
            int lastStart = rtb.GetFirstCharIndexFromLine(last);
            Point end = rtb.GetPositionFromCharIndex(lastStart);
            Assert.True(new Rectangle(Point.Empty, rtb.ClientSize).Contains(end), $"{end} in {rtb.ClientSize}");
            Assert.Equal(lastStart, rtb.GetCharIndexFromPosition(new Point(end.X + 1, end.Y + 1)));
        }
    }
}
