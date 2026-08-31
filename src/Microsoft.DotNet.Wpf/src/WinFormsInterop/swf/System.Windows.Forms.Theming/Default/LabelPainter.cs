// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Copyright (c) 2007 Novell, Inc.
//
// Authors:
//	Everaldo Canuto (ecanuto@novell.com)

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace System.Windows.Forms.Theming.Default
{
	internal class LabelPainter
	{
		public LabelPainter ()
		{
		}

		public virtual void Draw (Graphics dc, Rectangle client_rectangle, Label label) 
		{
			Rectangle rect = label.PaddingClientRectangle;

			label.DrawImage (dc, label.Image, rect, label.ImageAlign);

			rect.Height = Math.Max(rect.Height, label.Font.Height);
			// THE SITE CORRECTION for drawing a caption with DrawString instead of DrawText, which is
			// what TextRenderer.PadDrawStringRectangle describes and what every control that goes
			// through TextRenderer already gets. A Label does not go through it -- it calls DrawString
			// straight -- so it never got either half, and measured against a stock Label its text sat
			// one row LOW and one column LEFT: ours on rows 4..12 where Windows draws 3..11.
			// Down the page, DrawString lays a line on the font's own ascent where DrawText uses the
			// metric height. Across, DrawText leaves a margin that DrawString has already spent on the
			// glyph overhang. Both are one pixel at nine point, which is every caption in this suite;
			// if a face or a size ever needs a different number this is the place that has to learn it.
			rect.Offset (1, -1);

			if (label.Enabled) {
				dc.DrawString (label.Text, label.Font,
					ThemeEngine.Current.ResPool.GetSolidBrush (label.ForeColor),
					rect, label.string_format);
			} else {
				ControlPaint.DrawStringDisabled (
					dc, label.Text, label.Font, label.BackColor, rect, label.string_format);
			}
		}

		public virtual Size DefaultSize {
			get { return new Size (100, 23); }
		}
	}
}