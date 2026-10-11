//
// System.Drawing.StandardPrintController.cs
//
// Author:
//   Dennis Hayes (dennish@Raytek.com)
//   Herve Poussineau (hpoussineau@fr.st)
//   Jordi Mas i Hernandez (jordimash@gmail.com)
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

namespace System.Drawing.Printing
{
	/// <summary>Prints to the printer: each page recorded through the managed Graphics, then put on
	/// the printer DC as vector GDI (Windows) or written into a PDF for the platform's print system
	/// (everywhere else). No GDI+ is involved; see RecordedPrinting.cs.</summary>
	public class StandardPrintController : PrintController
	{
		RecordedPrintJob job;

		public StandardPrintController()
		{
		}

		public override void OnStartPrint (PrintDocument document, PrintEventArgs e)
		{
			base.OnStartPrint (document, e);
			job = new RecordedPrintJob (document);
			job.Start ();
			e.GraphicsContext = job.Context;
		}

		public override Graphics OnStartPage (PrintDocument document, PrintPageEventArgs e)
		{
			base.OnStartPage (document, e);
			return job?.StartPage (e);
		}

		public override void OnEndPage (PrintDocument document, PrintPageEventArgs e)
		{
			job?.EndPage ();
			base.OnEndPage (document, e);
		}

		public override void OnEndPrint (PrintDocument document, PrintEventArgs e)
		{
			RecordedPrintJob ending = job;
			job = null;
			ending?.End (e.Cancel);
			base.OnEndPrint (document, e);
		}
	}
}
