// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a drop-down combo box's edit selects, and how a selection is drawn. Every expected value was
// read off stock WinForms on Windows:
//   - setting the text or the selected item of an unfocused box selects nothing;
//   - a box whose edit shows its current item and that changes size selects all of it, focused or
//     not (a zoom box in a status bar shows "100%" highlighted this way); moved, or showing other
//     text, it keeps its selection;
//   - the edit shows its selection without the focus (ES_NOHIDESEL);
//   - a single-line edit's highlight runs one pixel past the selection's last character.
//

using System;
using System.Drawing;
using System.Threading;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ComboBoxSelectionTests
    {
        private static void OnSta(Action a)
        {
            Exception? failure = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static (SWF.Form form, SWF.ComboBox combo, SWF.TextBox other) Make()
        {
            var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(-3300, 1500), ClientSize = new Size(300, 100), ShowInTaskbar = false };
            var other = new SWF.TextBox { Bounds = new Rectangle(10, 60, 100, 20) };
            var combo = new SWF.ComboBox { Bounds = new Rectangle(10, 10, 120, 21), DropDownStyle = SWF.ComboBoxStyle.DropDown };
            combo.Items.AddRange(new object[] { "50%", "100%", "200%" });
            form.Controls.Add(other);
            form.Controls.Add(combo);
            form.Show();
            other.Focus();
            SWF.Application.DoEvents();
            return (form, combo, other);
        }

        private static string Sel(SWF.ComboBox c) => $"{c.SelectionStart}+{c.SelectionLength}";

        [Fact]
        public void UnfocusedBoxSelectsNothingWhenItsTextIsSet()
        {
            OnSta(() =>
            {
                var (form, c, _) = Make();
                using (form)
                {
                    c.Text = "200%";
                    Assert.Equal("0+0", Sel(c));
                    c.SelectedIndex = 0;
                    Assert.Equal("0+0", Sel(c));
                    c.Focus(); SWF.Application.DoEvents();
                    Assert.Equal("0+3", Sel(c));                  // the focus selects it all
                    form.Controls[0].Focus(); SWF.Application.DoEvents();
                    Assert.Equal("0+0", Sel(c));                  // and losing it deselects
                }
            });
        }

        [Fact]
        public void ResizingABoxShowingItsItemSelectsAllOfIt()
        {
            OnSta(() =>
            {
                var (form, c, _) = Make();
                using (form)
                {
                    c.SelectedIndex = 1;
                    Assert.Equal("0+0", Sel(c));
                    c.Location = new Point(12, 10);
                    Assert.Equal("0+0", Sel(c));                  // only moved
                    c.Width = 130;
                    Assert.Equal("0+4", Sel(c));
                    Assert.False(c.Focused);
                    c.SelectionStart = 2; c.SelectionLength = 0;
                    c.Width = 140;
                    Assert.Equal("0+4", Sel(c));                  // whatever was selected before
                    c.SelectionLength = 0;
                    c.Text = "abc";
                    c.Width = 150;
                    Assert.Equal("0+0", Sel(c));                  // text that is not the item

                    var simple = new SWF.ComboBox { Bounds = new Rectangle(150, 10, 120, 60), DropDownStyle = SWF.ComboBoxStyle.Simple };
                    simple.Items.AddRange(new object[] { "Simple", "Other" });
                    form.Controls.Add(simple);
                    SWF.Application.DoEvents();
                    simple.SelectedIndex = 0;
                    simple.Width = 130;
                    Assert.Equal("0+0", Sel(simple));             // a simple box does not
                }
            });
        }

        [Fact]
        public void SingleLineHighlightRunsOnePixelPastTheSelection()
        {
            OnSta(() =>
            {
                using var form = new SWF.Form { StartPosition = SWF.FormStartPosition.Manual, Location = new Point(-3300, 1500), ClientSize = new Size(300, 100), ShowInTaskbar = false };
                var font = new Font("Segoe UI", 9f);
                var tb = new SWF.TextBox { Bounds = new Rectangle(10, 10, 200, 30), Font = font, Text = "100%", HideSelection = false, BorderStyle = SWF.BorderStyle.None };
                form.Controls.Add(tb);
                form.Show();
                SWF.Application.DoEvents();
                foreach (var (start, length) in new[] { (0, 4), (0, 1), (1, 2), (2, 2) })
                {
                    tb.Select(start, length);
                    SWF.Application.DoEvents();
                    using var bmp = new Bitmap(tb.Width, tb.Height);
                    tb.DrawToBitmap(bmp, new Rectangle(0, 0, tb.Width, tb.Height));
                    Color hl = SystemColors.Highlight;
                    int first = -1, last = -1;
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, 1).ToArgb() == hl.ToArgb()) { if (first < 0) first = x; last = x; }
                    int W(string s) => s.Length == 0 ? 0 : SWF.TextRenderer.MeasureText(s, font, Size.Empty, SWF.TextFormatFlags.NoPadding).Width;
                    Assert.Equal(W(tb.Text.Substring(0, start)), first);
                    Assert.Equal(W(tb.Text.Substring(0, start + length)), last);   // one past the last column of the text
                }
            });
        }
    }
}
