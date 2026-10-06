// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrinterSettings off Windows: the printers the platform's print system knows (PrintSystem), in place
// of Mono's PrintingServicesUnix, which read CUPS through libcups and printed through libgdiplus'
// PostScript surface. Neither exists on most heads this port runs on -- iOS, Android and the browser
// have no CUPS, and no head has libgdiplus -- and the one that does (Linux) is reached through WPF's
// print system like the others, which already knows its desktop portal and its CUPS.
//
// A print system describes a printer by name and default paper and little else, so the paper list is
// the standard sizes a page can be laid out for, with the printer's own default selected; the
// resolution is the nominal 600 dpi a PDF is written for (it has none of its own, and 600 is what a
// GDI+ page reports on a typical laser printer, which is what layout code that asks DpiX expects).
//

using System;
using System.Collections.Generic;

namespace System.Drawing.Printing
{
	internal class PrintingServicesManaged : PrintingServices
	{
		internal const int NominalDpi = 600;

		internal override string DefaultPrinter {
			get {
				PrintSystemPrinter [] printers = PrintSystem.Printers ();
				foreach (PrintSystemPrinter p in printers)
					if (p.IsDefault)
						return p.Name;
				return printers.Length > 0 ? printers [0].Name : string.Empty;
			}
		}

		internal override bool IsPrinterValid (string printer) => PrintSystem.Find (printer) != null;

		internal override void LoadPrinterSettings (string printer, PrinterSettings settings)
		{
			PrintSystemPrinter p = PrintSystem.Find (printer);
			settings.maximum_copies = 999;
			settings.can_duplex = false;
			settings.supports_color = true;
			settings.landscape_angle = 90;
			settings.is_plotter = false;

			if (settings.paper_sizes == null)
				settings.paper_sizes = new PrinterSettings.PaperSizeCollection (new PaperSize [0]);
			else
				settings.paper_sizes.Clear ();
			foreach (PaperSize size in StandardSizes ())
				settings.paper_sizes.Add (size);

			if (settings.paper_sources == null)
				settings.paper_sources = new PrinterSettings.PaperSourceCollection (new PaperSource [0]);
			else
				settings.paper_sources.Clear ();
			settings.paper_sources.Add (new PaperSource (PaperSourceKind.AutomaticFeed, "Automatically Select"));
			settings.DefaultPageSettings.PaperSource = settings.paper_sources [0];

			PaperSize paper = DefaultSize (p);
			settings.DefaultPageSettings.PaperSize = paper;
			settings.DefaultPageSettings.PrinterResolution = new PrinterResolution (PrinterResolutionKind.Custom, NominalDpi, NominalDpi);
		}

		internal override void LoadPrinterResolutions (string printer, PrinterSettings settings)
		{
			settings.PrinterResolutions.Clear ();
			LoadDefaultResolutions (settings.PrinterResolutions);
			settings.PrinterResolutions.Add (new PrinterResolution (PrinterResolutionKind.Custom, NominalDpi, NominalDpi));
		}

		internal override void GetPrintDialogInfo (string printer, ref string port, ref string type, ref string status, ref string comment)
		{
			PrintSystemPrinter p = PrintSystem.Find (printer);
			port = string.Empty;
			type = p?.DisplayName ?? string.Empty;
			status = p != null ? "Ready" : string.Empty;
			comment = string.Empty;
		}

		// The printer's default paper: one of the standard sizes when it matches one (to a tenth of
		// a millimetre or so), a custom size otherwise, and the locale's usual paper when unknown.
		static PaperSize DefaultSize (PrintSystemPrinter p)
		{
			List<PaperSize> sizes = StandardSizes ();
			if (p != null && p.PaperWidth > 0 && p.PaperHeight > 0) {
				int w = (int) Math.Round (p.PaperWidth), h = (int) Math.Round (p.PaperHeight);
				foreach (PaperSize s in sizes)
					if (Math.Abs (s.Width - w) <= 1 && Math.Abs (s.Height - h) <= 1)
						return s;
				return new PaperSize ("Custom", w, h);
			}
			string region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
			bool letter = region == "US" || region == "CA" || region == "MX" || region == "PH" || region == "CL" || region == "CO";
			return letter ? sizes [0] : sizes [2];
		}

		static List<PaperSize> StandardSizes ()
		{
			var list = new List<PaperSize> ();
			void Add (PaperKind kind, string name, int w, int h)
			{
				var s = new PaperSize (name, w, h);
				s.RawKind = (int) kind;
				list.Add (s);
			}
			Add (PaperKind.Letter, "Letter", 850, 1100);
			Add (PaperKind.Legal, "Legal", 850, 1400);
			Add (PaperKind.A4, "A4", 827, 1169);
			Add (PaperKind.A3, "A3", 1169, 1654);
			Add (PaperKind.A5, "A5", 583, 827);
			Add (PaperKind.B5, "B5 (JIS)", 717, 1012);
			Add (PaperKind.Executive, "Executive", 725, 1050);
			Add (PaperKind.Tabloid, "Tabloid", 1100, 1700);
			return list;
		}
	}

	internal class GlobalPrintingServicesManaged : GlobalPrintingServices
	{
		internal override PrinterSettings.StringCollection InstalledPrinters {
			get {
				var names = new List<string> ();
				foreach (PrintSystemPrinter p in PrintSystem.Printers ())
					names.Add (p.Name);
				return new PrinterSettings.StringCollection (names.ToArray ());
			}
		}

		// There is no device context off Windows: pages are recorded and written as a document.
		internal override IntPtr CreateGraphicsContext (PrinterSettings settings, PageSettings page_settings) => IntPtr.Zero;
		internal override bool StartDoc (GraphicsPrinter gr, string doc_name, string output_file) => true;
		internal override bool StartPage (GraphicsPrinter gr) => true;
		internal override bool EndPage (GraphicsPrinter gr) => true;
		internal override bool EndDoc (GraphicsPrinter gr) => true;
	}
}
