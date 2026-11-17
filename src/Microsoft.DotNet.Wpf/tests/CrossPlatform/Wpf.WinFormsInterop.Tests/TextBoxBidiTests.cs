// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A text box lays its lines out as Windows' edit control does: in visual order (the bidi algorithm), a
// right-to-left box's lines as right-to-left paragraphs. A character's position and a click go by where
// the characters show. Laid out in logical order, a Hebrew word read backwards and a right-to-left box read
// left to right.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class TextBoxBidiTests
    {
        [Fact]
        public void AHebrewWordInALeftToRightBox_ReadsRightToLeft()
        {
            using var tb = new SWF.TextBox { Bounds = new Rectangle(0, 0, 300, 23), Text = "ab שלום" };
            tb.CreateControl();
            int shin = tb.Text.IndexOf('ש'), mem = tb.Text.IndexOf('ם');
            // the word's first letter shows at its right end, its last at the left
            Assert.True(tb.GetPositionFromCharIndex(shin).X > tb.GetPositionFromCharIndex(mem).X);
            // and the Latin before it stays on its left, in order
            Assert.True(tb.GetPositionFromCharIndex(0).X < tb.GetPositionFromCharIndex(1).X);
            Assert.True(tb.GetPositionFromCharIndex(1).X < tb.GetPositionFromCharIndex(mem).X);
        }

        [Fact]
        public void ARightToLeftBox_ReadsItsLatinFromTheRight()
        {
            using var tb = new SWF.TextBox { Bounds = new Rectangle(0, 0, 300, 23), RightToLeft = SWF.RightToLeft.Yes, Text = "abc שלום" };
            tb.CreateControl();
            // a right-to-left paragraph: the Latin word first, at the right; the Hebrew after it, to its left
            int a = 0, shin = tb.Text.IndexOf('ש');
            Assert.True(tb.GetPositionFromCharIndex(a).X > tb.GetPositionFromCharIndex(shin).X);
            // a click on a character puts the caret beside it
            Point onShin = tb.GetPositionFromCharIndex(shin);
            int at = tb.GetCharIndexFromPosition(new Point(onShin.X + 1, onShin.Y + 3));
            Assert.InRange(at, shin, shin + 1);
        }

        [Fact]
        public void ALatinLine_IsLaidOutAsBefore()
        {
            using var tb = new SWF.TextBox { Bounds = new Rectangle(0, 0, 300, 23), Text = "abc" };
            tb.CreateControl();
            Assert.True(tb.GetPositionFromCharIndex(0).X < tb.GetPositionFromCharIndex(2).X);
        }
    }
}
