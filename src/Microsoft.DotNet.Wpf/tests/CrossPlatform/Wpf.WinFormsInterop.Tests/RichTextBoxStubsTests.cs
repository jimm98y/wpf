// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// RichTextBox members the Mono port left as stubs. SelectionChanged is RichEdit's EN_SELCHANGE: raised
// whenever the selection or the caret moves, not only on a mouse release. SelectionBullet is a paragraph's
// PFN_BULLET, read from and written to RTF the way RichEdit writes it ({\*\pn\pnlvlblt...}).
//

using System;
using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class RichTextBoxStubsTests
    {
        // RichEdit's own output for two bulleted paragraphs and a plain one
        private const string BulletedRtf =
            @"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Segoe UI;}{\f1\fnil\fcharset2 Symbol;}}" +
            @"\viewkind4\uc1\pard{\pntext\f1\'B7\tab}{\*\pn\pnlvlblt\pnf1\pnindent0{\pntxtb\'B7}}\fi-360\li720\f0\fs18 One\par" +
            @"{\pntext\f1\'B7\tab}Two\par" +
            @"\pard Three\par}";

        [Fact]
        public void SelectionChanged_IsRaisedWheneverTheSelectionMoves()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 200, 100) };
            rtb.CreateControl();
            rtb.Text = "hello world";
            int raised = 0;
            rtb.SelectionChanged += (s, e) => raised++;

            rtb.Select(2, 3);
            Assert.Equal(1, raised);
            rtb.Select(2, 3);
            Assert.Equal(1, raised);   // not moved: nothing to tell
            rtb.SelectionStart = 6;
            Assert.Equal(2, raised);
            rtb.SelectAll();
            Assert.Equal(3, raised);
        }

        [Fact]
        public void Bullets_AreReadFromRichEditsRtf_AndWrittenBack()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 300, 120) };
            rtb.CreateControl();
            rtb.Rtf = BulletedRtf;
            Assert.Equal("One\nTwo\nThree", rtb.Text.TrimEnd('\n'));

            bool BulletAt(SWF.RichTextBox box, string word) { box.Select(box.Text.IndexOf(word), 0); return box.SelectionBullet; }
            Assert.True(BulletAt(rtb, "One"));
            Assert.True(BulletAt(rtb, "Two"));
            Assert.False(BulletAt(rtb, "Three"));

            using var copy = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 300, 120) };
            copy.CreateControl();
            copy.Rtf = rtb.Rtf;
            Assert.Equal(rtb.Text, copy.Text);
            Assert.True(BulletAt(copy, "One"));
            Assert.True(BulletAt(copy, "Two"));
            Assert.False(BulletAt(copy, "Three"));
        }

        [Fact]
        public void SelectionBullet_SetsTheParagraphs_AndTheirHangingIndent()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 300, 120), BulletIndent = 15 };
            rtb.CreateControl();
            rtb.Text = "first\nsecond\nthird";
            rtb.Select(2, 8);   // "rst\nseco": the first two paragraphs
            rtb.SelectionBullet = true;
            Assert.True(rtb.SelectionBullet);
            Assert.Equal(15, rtb.SelectionHangingIndent);
            rtb.Select(rtb.Text.IndexOf("third"), 0);
            Assert.False(rtb.SelectionBullet);
            Assert.Contains(@"\pnlvlblt", rtb.Rtf);
            rtb.SelectAll();
            Assert.False(rtb.SelectionBullet);   // not every paragraph is
        }
    
        [Fact]
        public void ProtectedText_RefusesEdits_RaisesProtected_AndKeepsItThroughRtf()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 300, 120) };
            rtb.CreateControl();
            rtb.Text = "keep this, edit that";
            int refused = 0;
            rtb.Protected += (s, e) => refused++;

            rtb.Select(0, 4);
            rtb.SelectionProtected = true;
            Assert.True(rtb.SelectionProtected);

            rtb.Select(2, 6);   // "ep thi": reaches into the protected word
            rtb.SelectedText = "X";
            Assert.Equal(1, refused);
            Assert.Equal("keep this, edit that", rtb.Text);

            rtb.Select(11, 4);  // "edit": not protected
            Assert.False(rtb.SelectionProtected);
            rtb.SelectedText = "change";
            Assert.Equal(1, refused);
            Assert.Equal("keep this, change that", rtb.Text);

            Assert.Contains(@"\protect", rtb.Rtf);
            using var copy = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 300, 120) };
            copy.CreateControl();
            copy.Rtf = rtb.Rtf;
            copy.Select(0, 4);
            Assert.True(copy.SelectionProtected);
            copy.Select(5, 4);
            Assert.False(copy.SelectionProtected);
        }
    
        private sealed class Mouse : SWF.RichTextBox
        {
            // a press captures the mouse, as WM_LBUTTONDOWN does a control's
            public void Down(int x, int y) { Capture = true; OnMouseDown(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, x, y, 0)); }
            public void Move(int x, int y) => OnMouseMove(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 0, x, y, 0));
            public void Up(int x, int y) { OnMouseUp(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, x, y, 0)); Capture = false; }
        }

        [Fact]
        public void RightMargin_WrapsLinesAtThatWidth()
        {
            using var rtb = new SWF.RichTextBox { Bounds = new Rectangle(0, 0, 500, 200) };
            rtb.CreateControl();
            rtb.Text = "alpha beta gamma delta epsilon zeta eta theta iota kappa";
            int Lines() => rtb.GetLineFromCharIndex(rtb.TextLength - 1) + 1;
            int wide = Lines();
            rtb.RightMargin = 100;
            int narrow = Lines();
            Assert.True(narrow > wide, $"{narrow} lines at a 100 px margin, {wide} at the window's width");
            rtb.RightMargin = 0;
            Assert.Equal(wide, Lines());
            Assert.Throws<ArgumentOutOfRangeException>(() => rtb.RightMargin = -1);
        }

        [Fact]
        public void ShowSelectionMargin_MovesTheText_AndAClickThereSelectsTheLine()
        {
            using var rtb = new Mouse { Bounds = new Rectangle(0, 0, 300, 120) };
            rtb.CreateControl();
            rtb.Text = "first line\nsecond line";
            int x0 = rtb.GetPositionFromCharIndex(0).X;
            rtb.ShowSelectionMargin = true;
            Assert.Equal(x0 + 8, rtb.GetPositionFromCharIndex(0).X);

            Point second = rtb.GetPositionFromCharIndex(rtb.Text.IndexOf("second"));
            rtb.Down(2, second.Y + 3);
            rtb.Up(2, second.Y + 3);
            Assert.Equal("second line", rtb.SelectedText.TrimEnd('\n'));
        }

        [Fact]
        public void AutoWordSelection_ADragAcrossWordsSelectsWholeWords()
        {
            using var rtb = new Mouse { Bounds = new Rectangle(0, 0, 400, 120) };
            rtb.CreateControl();
            rtb.Text = "alpha beta gamma";
            Point P(int index) { Point p = rtb.GetPositionFromCharIndex(index); return new Point(p.X + 1, p.Y + 3); }

            rtb.AutoWordSelection = true;
            Point from = P(2), to = P(13);   // "ph" ... "mm"
            rtb.Down(from.X, from.Y);
            rtb.Move(to.X, to.Y);
            rtb.Up(to.X, to.Y);
            Assert.Equal("alpha beta gamma", rtb.SelectedText);

            // without it, characters (a box of its own: a second press there would be a double-click)
            using var plain = new Mouse { Bounds = new Rectangle(0, 0, 400, 120) };
            plain.CreateControl();
            plain.Text = "alpha beta gamma";
            plain.Down(from.X, from.Y);
            plain.Move(to.X, to.Y);
            plain.Up(to.X, to.Y);
            Assert.Equal("pha beta ga", plain.SelectedText);
        }
    
        private sealed class Keyed : SWF.RichTextBox
        {
            public bool Key(SWF.Keys keys) { var m = new SWF.Message(); return ProcessCmdKey(ref m, keys); }
        }

        [Fact]
        public void RichEditHotKeys_AlignSpaceScriptAndSize_AndTheAlignmentOnesCanBeSwitchedOff()
        {
            using var rtb = new Keyed { Bounds = new Rectangle(0, 0, 300, 120), Font = new Font("Segoe UI", 9f) };
            rtb.CreateControl();
            rtb.Text = "one\ntwo";
            rtb.Select(0, 2);

            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.E));
            Assert.Equal(SWF.HorizontalAlignment.Center, rtb.SelectionAlignment);
            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.R));
            Assert.Equal(SWF.HorizontalAlignment.Right, rtb.SelectionAlignment);

            rtb.RichTextShortcutsEnabled = false;
            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.L));   // swallowed, not acted on
            Assert.Equal(SWF.HorizontalAlignment.Right, rtb.SelectionAlignment);
            rtb.RichTextShortcutsEnabled = true;

            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.D2));
            Assert.Contains(@"\sl480", rtb.Rtf);

            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.Oemplus));
            Assert.Contains(@"\sub", rtb.Rtf);

            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.Shift | SWF.Keys.OemPeriod));
            Assert.Equal(10f, rtb.SelectionFont!.SizeInPoints);
            Assert.True(rtb.Key(SWF.Keys.Control | SWF.Keys.Shift | SWF.Keys.Oemcomma));
            Assert.Equal(9f, rtb.SelectionFont!.SizeInPoints);

            Assert.False(rtb.Key(SWF.Keys.Control | SWF.Keys.B));   // RichEdit leaves bold to a client that asks
            rtb.ReadOnly = true;
            Assert.False(rtb.Key(SWF.Keys.Control | SWF.Keys.E));
        }
    
        private sealed class Dropped : SWF.RichTextBox
        {
            public SWF.DragEventArgs Args(SWF.IDataObject data, Point client, int keys = 0)
            {
                Point screen = PointToScreen(client);
                return new SWF.DragEventArgs(data, keys, screen.X, screen.Y, SWF.DragDropEffects.Copy | SWF.DragDropEffects.Move, SWF.DragDropEffects.None);
            }
            public SWF.DragDropEffects Over(SWF.DragEventArgs e) { OnDragEnter(e); return e.Effect; }
            public void Drop(SWF.DragEventArgs e) => OnDragDrop(e);
            public void Down(int x, int y) { Capture = true; OnMouseDown(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, x, y, 0)); }
            public void Up(int x, int y) { OnMouseUp(new SWF.MouseEventArgs(SWF.MouseButtons.Left, 1, x, y, 0)); Capture = false; }
        }

        [Fact]
        public void EnableAutoDragDrop_InsertsDroppedText_WhereItLands()
        {
            using var rtb = new Dropped { Bounds = new Rectangle(0, 0, 400, 120) };
            rtb.CreateControl();
            rtb.Text = "hello world";
            var data = new SWF.DataObject(SWF.DataFormats.UnicodeText, "big ");
            Point at = rtb.GetPositionFromCharIndex(6);
            at.Offset(1, 3);

            // off: the box offers nothing and inserts nothing
            Assert.Equal(SWF.DragDropEffects.None, rtb.Over(rtb.Args(data, at)));
            rtb.Drop(rtb.Args(data, at));
            Assert.Equal("hello world", rtb.Text);

            rtb.EnableAutoDragDrop = true;
            Assert.Equal(SWF.DragDropEffects.Move, rtb.Over(rtb.Args(data, at)));
            Assert.Equal(SWF.DragDropEffects.Copy, rtb.Over(rtb.Args(data, at, keys: 8)));   // Ctrl copies
            rtb.Drop(rtb.Args(data, at));
            Assert.Equal("hello big world", rtb.Text);
            Assert.Equal("big ", rtb.SelectedText);
        }

        [Fact]
        public void EnableAutoDragDrop_APressOnTheSelectionWithoutADrag_IsAClick()
        {
            using var rtb = new Dropped { Bounds = new Rectangle(0, 0, 400, 120), EnableAutoDragDrop = true };
            rtb.CreateControl();
            rtb.Text = "hello world";
            rtb.Select(0, 5);
            Point p = rtb.GetPositionFromCharIndex(2);
            rtb.Down(p.X + 1, p.Y + 3);
            Assert.Equal(5, rtb.SelectionLength);   // kept while it may become a drag
            rtb.Up(p.X + 1, p.Y + 3);
            Assert.Equal(0, rtb.SelectionLength);
            Assert.Equal(2, rtb.SelectionStart);
        }
    }
}
