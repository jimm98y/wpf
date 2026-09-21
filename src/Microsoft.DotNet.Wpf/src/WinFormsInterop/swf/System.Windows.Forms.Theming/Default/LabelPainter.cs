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

		/// <summary>NOTE: this ignores Label.UseCompatibleTextRendering, and that is a real
		/// difference from Windows rather than a detail.
		/// <para>The property defaults to FALSE, and a WinForms Label with it false draws through
		/// TextRenderer.DrawText -- GDI -- while this always calls Graphics.DrawString, which is
		/// GDI+. The two are different rasterizers AND different layouts: they leave different
		/// margins before the first glyph and place the run on different sub-pixel phases.</para>
		/// <para>That is why no single margin constant reconciles the two. Measured directly out of
		/// GDI+ (Probe.Margins draws one glyph twice, once with GenericTypographic which has no
		/// padding, and subtracts, so the bearing cancels), GDI+'s margin is round(em/6) and is the
		/// SAME for all six faces at a size:</para>
		/// <code>
		///   ppem          9  10  11  12  13  16  20  24
		///   GDI+          2   2   2   2   2   3   3   4
		///   ours ceil(H/6) 2  2-3 3   3   3   4  4-5 5-6
		/// </code>
		/// <para>But adopting round(em/6) took the text specimen from 37.4M to 122.2M, because the
		/// STOCK app is not on the GDI+ path either -- its Label is on TextRenderer's, whose padding
		/// is a different rule again. Matching GDI+ exactly is matching the wrong thing.</para>
		/// <para>So the fix is not a constant, it is this branch: honour
		/// UseCompatibleTextRendering and route the false case to TextRenderer.DrawText, mapping
		/// TextAlign to TextFormatFlags. Left undone deliberately -- it changes every Label in the
		/// port and the flag mapping has to be right before it can be measured, which is more than a
		/// one-line change deserves at the end of a session.</para></summary>
		/// <summary>This ignores Label.UseCompatibleTextRendering, and ROUTING IT TO TextRenderer
		/// DOES NOT HELP -- tried, measured, reverted, recorded here so it is not tried again.
		/// <para>The property defaults to false, and a Windows Label with it false draws through
		/// TextRenderer, that is GDI. Adding that branch here changes nothing at all in this port:
		/// XplatUI.RunningOnUnix returns TRUE under the GPU rasterizer by design -- the Graphics a
		/// control is handed records into a WebGPU scene and has no GDI behind it, so GetHdc fails --
		/// and TextRenderer.DrawTextInternal therefore falls back to DrawStringGdi anyway. The text
		/// specimen moved from 37,412,409 to 37,418,235, which is capture noise.</para>
		/// <para>So we cannot BE on GDI's path; the difference has to be emulated. What is left is a
		/// one-pixel margin, and both attempts to measure the target failed for the same reason:</para>
		/// <para>Probe.Margins draws a glyph twice, once with padding and once without, and subtracts,
		/// so the bearing cancels. Against GDI+ it looks clean -- round(em/6), uniform across all six
		/// faces. Against TextRenderer, which is the one that matters, it is NOISY: the padding comes
		/// back different for faces at the SAME Font.Height (10ppem Arial 12 -> 3 but Times 12 -> 2;
		/// 20ppem Verdana 25 -> 5 but Tahoma 25 -> 4). The two draws land on different integer
		/// positions and the fringe column moves with them, so the instrument carries the same one
		/// pixel of error as the thing being measured.</para>
		/// <para>NEXT: measure the padding without moving the glyph -- draw at the SAME position both
		/// times and read the difference out of the layout rectangle rather than the ink, or compare
		/// MeasureText against MeasureText with NoPadding, which is integer arithmetic and cannot
		/// carry a fringe. Do not fit another rounding of Font.Height to the ink table above.</para>
		/// </summary>
		public virtual void Draw (Graphics dc, Rectangle client_rectangle, Label label) 
		{
			Rectangle rect = label.PaddingClientRectangle;

			label.DrawImage (dc, label.Image, rect, label.ImageAlign);

			rect.Height = Math.Max(rect.Height, label.Font.Height);

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