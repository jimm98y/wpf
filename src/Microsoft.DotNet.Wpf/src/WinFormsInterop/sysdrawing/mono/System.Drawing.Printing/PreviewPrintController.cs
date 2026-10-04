//
// System.Drawing.PreviewPrintController.cs
//
// Author:
//   Dennis Hayes (dennish@Raytek.com)
//
// (C) 2002 Ximian, Inc
//

//
// Copyright (C) 2004 Novell, Inc (http://www.novell.com)
//
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

using System;
using System.Collections;
using System.Drawing.Imaging;

namespace System.Drawing.Printing
{
	/// <summary>Records each page for a preview. The pages are kept as their recorded scenes
	/// (PrintPreviewPageImage) -- what the printer would have been given -- rather than drawn into
	/// bitmaps through GDI+, and the preview control draws them as nested scenes.</summary>
	public class PreviewPrintController : PrintController
	{
		bool useantialias;
		ArrayList pageInfoList;
		Graphics current;
		Size currentSize;

		public PreviewPrintController()
		{
			pageInfoList = new ArrayList ();
		}

		public override bool IsPreview {
			get { return true; }
		}

		public override void OnStartPrint(PrintDocument document, PrintEventArgs e)
		{
			if (!document.PrinterSettings.IsValid)
				throw new InvalidPrinterException(document.PrinterSettings);

			foreach (PreviewPageInfo pi in pageInfoList)
				pi.Image.Dispose ();

			pageInfoList.Clear ();
			base.OnStartPrint (document, e);
		}

		public override Graphics OnStartPage(PrintDocument document, PrintPageEventArgs e)
		{
			base.OnStartPage (document, e);
			// As .NET previews a page: the whole paper, origin at its top left, a hundredth of an
			// inch per unit -- and, for OriginAtMargins, the origin moved by the margins less the
			// printer's hard margins, exactly as the printed page moves it.
			Size size = e.PageBounds.Size;
			PrintPageGeometry.Device (e.PageSettings, out float dpiX, out float dpiY, out RectangleF printable);
			current = Graphics.NewPrintRecording (dpiX, dpiY, new RectangleF (0, 0, size.Width, size.Height));
			currentSize = size;
			// Paper is white, and a preview shows paper.
			using (var white = new SolidBrush (Color.White))
				current.FillRectangle (white, 0, 0, size.Width, size.Height);
			if (document.OriginAtMargins) {
				current.TranslateTransform (-printable.X, -printable.Y);
				current.TranslateTransform (document.DefaultPageSettings.MarginsUnchecked.Left, document.DefaultPageSettings.MarginsUnchecked.Top);
			}
			return current;
		}

		public override void OnEndPage(PrintDocument document, PrintPageEventArgs e)
		{
			if (current != null) {
				object scene = WebGpuBackend.GpuRaster.EndScene (current);
				current.Dispose ();
				current = null;
				pageInfoList.Add (new PreviewPageInfo (new PrintPreviewPageImage (scene, currentSize), currentSize));
			}
			base.OnEndPage (document, e);
		}

		public override void OnEndPrint(PrintDocument document, PrintEventArgs e)
		{
			if (current != null) OnEndPage (document, null);
			base.OnEndPrint (document, e);
		}

		public virtual bool UseAntiAlias {
			get{ return useantialias; }
			set{ useantialias = value; }
		}

		public PreviewPageInfo [] GetPreviewPageInfo()
		{
			PreviewPageInfo [] pi = new PreviewPageInfo[pageInfoList.Count];
			pageInfoList.CopyTo (pi);
			return pi;
		}

	}
}
