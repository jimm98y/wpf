// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing without GDI+.
//
// A page is drawn through the same managed Graphics recorder a window is (GpuRaster.NewRecording,
// in print mode -- see Graphics.Print.cs): its verbs build a scene, in hundredths of an inch, and
// nothing native is involved. What happens to the finished page depends only on the platform:
//
//   * Windows: the scene is replayed as vector GDI on the printer DC (SceneGdiDevice) between
//     StartPage and EndPage, page by page as they are drawn, because the spooler takes GDI;
//   * everywhere else: the scenes are kept until the document ends, written as one vector PDF
//     (ScenePdfDocument) and handed to the platform's print system (PrintSystem), or written to
//     PrinterSettings.PrintFileName when the job prints to a file;
//   * a preview keeps the scenes as page images (PrintPreviewPageImage) that the preview control
//     draws as nested scenes, on the GPU like any other.
//
// The page's coordinate system is GDI+'s for a printer: origin at the top left of the PRINTABLE
// area (the printer's hard margins inside the paper's edge), unless OriginAtMargins moves it to the
// margins; a hundredth of an inch per unit until the page changes PageUnit.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing.WebGpuBackend;

namespace System.Drawing.Printing
{
	/// <summary>The geometry GDI+ gives a printed page.</summary>
	internal static class PrintPageGeometry
	{
		/// <summary>The paper turned for the orientation, hundredths of an inch.</summary>
		internal static SizeF Paper (PageSettings page)
		{
			PaperSize p = page.paperSize;
			if (p == null) return new SizeF (850, 1100);
			return page.landscape ? new SizeF (p.Height, p.Width) : new SizeF (p.Width, p.Height);
		}

		/// <summary>A page's resolution and printable area (in paper coordinates, hundredths of an
		/// inch): the printer's own on Windows; the print system's imageable area, at the nominal PDF
		/// resolution, elsewhere.</summary>
		internal static void Device (PageSettings page, out float dpiX, out float dpiY, out RectangleF printable)
		{
			SizeF paper = Paper (page);
			PrinterSettings settings = page.PrinterSettings;
			if (OperatingSystem.IsWindows () && settings != null && !string.IsNullOrEmpty (settings.PrinterName)
			    && PrintingServicesWin32.DescribePage (settings, page, out dpiX, out dpiY, out printable))
				return;

			int res = page.printerResolution != null && page.printerResolution.X > 0 ? page.printerResolution.X : PrintingServicesManaged.NominalDpi;
			dpiX = dpiY = res;
			printable = new RectangleF (0, 0, paper.Width, paper.Height);
			if (OperatingSystem.IsWindows () || settings == null)
				return;
			PrintSystemPrinter p = PrintSystem.Find (settings.PrinterName);
			if (p == null || p.Imageable.Width <= 0 || p.Imageable.Height <= 0)
				return;
			RectangleF im = p.Imageable;
			// The imageable area is the printer's for its default orientation; turned with the page.
			float left = im.X, top = im.Y, right = Math.Max (0, (p.PaperWidth > 0 ? p.PaperWidth : paper.Width) - im.Right),
				bottom = Math.Max (0, (p.PaperHeight > 0 ? p.PaperHeight : paper.Height) - im.Bottom);
			if (page.landscape) (left, top, right, bottom) = (top, right, bottom, left);
			printable = RectangleF.FromLTRB (left, top, Math.Max (left, paper.Width - right), Math.Max (top, paper.Height - bottom));
		}

		/// <summary>PrintPageEventArgs' two rectangles, as .NET forms them: the whole page, and it
		/// less the margins.</summary>
		internal static void Bounds (PageSettings page, out Rectangle pageBounds, out Rectangle marginBounds)
		{
			pageBounds = page.Bounds;
			Margins m = page.MarginsUnchecked;
			marginBounds = new Rectangle (m.Left, m.Top, pageBounds.Width - (m.Left + m.Right), pageBounds.Height - (m.Top + m.Bottom));
		}
	}

	/// <summary>One printing session's pages, wherever they are going.</summary>
	internal sealed class RecordedPrintJob
	{
		readonly PrintDocument document;
		readonly PrinterSettings settings;
		readonly List<(object Scene, SizeF Paper, PointF Origin)> pages = new List<(object, SizeF, PointF)> ();
		Graphics current;
		SizeF currentPaper;
		PointF currentOrigin;

		// Windows: the printer DC and what draws on it.
		GraphicsPrinter dc;
		SceneGdiDevice device;
		PageSettings dcPage;

		// Where the PDF goes when the job is written to a stream rather than printed (PdfPrintController).
		readonly Stream pdfTarget;

		internal RecordedPrintJob (PrintDocument document, Stream pdfTarget = null)
		{
			this.document = document;
			settings = document.PrinterSettings;
			this.pdfTarget = pdfTarget;
		}

		internal GraphicsPrinter Context => dc;

		static string OutputFile (PrinterSettings s) => s.PrintToFile && !string.IsNullOrEmpty (s.PrintFileName) ? s.PrintFileName : null;

		internal void Start ()
		{
			if (pdfTarget != null)
				return;
			if (!settings.IsValid)
				throw new InvalidPrinterException (settings);
			if (!OperatingSystem.IsWindows ())
				return;
			IntPtr hdc = PrintingServicesWin32.CreateGraphicsContext (settings, document.DefaultPageSettings);
			if (hdc == IntPtr.Zero)
				throw new InvalidPrinterException (settings);
			dc = new GraphicsPrinter (null, hdc);
			dcPage = document.DefaultPageSettings;
			if (!PrintingServicesWin32.StartDoc (dc, document.DocumentName, OutputFile (settings))) {
				PrintingServicesWin32.AbortDoc (dc);
				dc = null;
				throw new System.ComponentModel.Win32Exception (System.Runtime.InteropServices.Marshal.GetLastWin32Error (),
					"The print job could not be started.");
			}
			device = new SceneGdiDevice (hdc);
		}

		internal Graphics StartPage (PrintPageEventArgs e)
		{
			PageSettings page = e.PageSettings;
			float dpiX, dpiY;
			RectangleF printable;
			if (dc != null) {
				// A page whose settings differ from the last one's (QueryPageSettings) gets the
				// DC's mode changed before it starts.
				if (dcPage == null || page.landscape != dcPage.landscape || page.paperSize?.Width != dcPage.paperSize?.Width
				    || page.paperSize?.Height != dcPage.paperSize?.Height) {
					PrintingServicesWin32.ResetForPage (dc, settings, page);
					dcPage = page;
				}
				PrintingServicesWin32.StartPage (dc);
				if (!PrintingServicesWin32.DescribeDevice (dc.Hdc, out dpiX, out dpiY, out printable))
					PrintPageGeometry.Device (page, out dpiX, out dpiY, out printable);
			} else {
				PrintPageGeometry.Device (page, out dpiX, out dpiY, out printable);
			}
			currentPaper = PrintPageGeometry.Paper (page);
			currentOrigin = printable.Location;
			current = Graphics.NewPrintRecording (dpiX, dpiY, new RectangleF (0, 0, printable.Width, printable.Height));
			if (document.OriginAtMargins) {
				Margins m = page.MarginsUnchecked;
				current.TranslateTransform (m.Left - printable.X, m.Top - printable.Y);
			}
			return current;
		}

		internal void EndPage ()
		{
			if (current == null) return;
			object scene = GpuRaster.EndScene (current);
			current.Dispose ();
			current = null;
			if (dc != null) {
				device.DrawPage (scene);
				PrintingServicesWin32.EndPage (dc);
			} else {
				pages.Add ((scene, currentPaper, currentOrigin));
			}
		}

		internal void End (bool cancelled)
		{
			if (current != null) EndPage ();
			if (dc != null) {
				device.Dispose ();
				if (cancelled) PrintingServicesWin32.AbortDoc (dc);
				else PrintingServicesWin32.EndDoc (dc);
				dc = null;
				return;
			}
			if (cancelled || pages.Count == 0)
				return;
			Submit ();
		}

		/// <summary>The recorded pages as one PDF; each page's scene placed at its printable origin
		/// on the paper.</summary>
		internal byte [] WritePdf ()
		{
			using var ms = new MemoryStream ();
			using (var pdf = new ScenePdfDocument (ms, leaveOpen: true))
				foreach ((object scene, SizeF paper, PointF origin) in pages)
					pdf.AddPage (Placed (scene, origin), paper.Width, paper.Height);
			return ms.ToArray ();
		}

		static object Placed (object scene, PointF origin)
		{
			if (origin.IsEmpty || scene is not Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual v) return scene;
			var placed = new Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual { Offset = new System.Numerics.Vector2 (origin.X, origin.Y) };
			placed.Children.Add (v);
			return placed;
		}

		void Submit ()
		{
			byte [] pdf = WritePdf ();
			if (pdfTarget != null) {
				pdfTarget.Write (pdf, 0, pdf.Length);
				return;
			}
			string file = OutputFile (settings);
			if (file != null) {
				// Print to file off Windows: the document itself. There is no driver to make it
				// printer-ready data, and a PDF is what every one of these print systems takes.
				File.WriteAllBytes (file, pdf);
				return;
			}
			IPrintSystem system = PrintSystem.Current;
			if (system == null)
				throw new InvalidPrinterException (settings);
			SizeF paper = pages [0].Paper;
			var job = new PrintSystemJob {
				PrinterName = settings.PrinterName,
				Copies = Math.Max (1, (int) settings.Copies),
				Collate = settings.Collate,
				Landscape = document.DefaultPageSettings.landscape,
				PaperWidth = paper.Width,
				PaperHeight = paper.Height,
			};
			if (settings.PrintRange == PrintRange.SomePages) {
				job.FirstPage = settings.FromPage;
				job.LastPage = settings.ToPage;
			}
			using var stream = new MemoryStream (pdf, writable: false);
			if (!system.Submit (document.DocumentName, stream, job))
				throw new InvalidPrinterException (settings);
		}
	}

	/// <summary>Writes a document as the PDF the non-Windows heads hand their print systems, into a
	/// stream: the off-Windows path, runnable anywhere (and what the tests drive).</summary>
	internal sealed class PdfPrintController : PrintController
	{
		readonly Stream output;
		RecordedPrintJob job;

		internal PdfPrintController (Stream output) { this.output = output; }

		public override void OnStartPrint (PrintDocument document, PrintEventArgs e)
		{
			job = new RecordedPrintJob (document, output);
			job.Start ();
		}

		public override Graphics OnStartPage (PrintDocument document, PrintPageEventArgs e) => job?.StartPage (e);

		public override void OnEndPage (PrintDocument document, PrintPageEventArgs e) => job?.EndPage ();

		public override void OnEndPrint (PrintDocument document, PrintEventArgs e)
		{
			RecordedPrintJob ending = job;
			job = null;
			ending?.End (e.Cancel);
		}
	}

	/// <summary>A previewed page: its recorded scene, sized in hundredths of an inch. Drawn by
	/// Graphics.DrawImage as a nested scene (see Graphics.RecordImage), so a preview needs no
	/// bitmap and no GDI+ metafile.</summary>
	internal sealed class PrintPreviewPageImage : Image
	{
		internal readonly object Scene;

		internal PrintPreviewPageImage (object scene, Size size)
		{
			Scene = scene;
			managedWidth = Math.Max (1, size.Width);
			managedHeight = Math.Max (1, size.Height);
			managedDpiX = managedDpiY = 100f;
		}
	}
}
