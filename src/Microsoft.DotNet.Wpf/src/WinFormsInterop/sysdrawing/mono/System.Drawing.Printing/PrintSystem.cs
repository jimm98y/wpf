// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Where a printed WinForms document goes off Windows, and where PrinterSettings learns what printers
// there are.
//
// Every print system but Windows' takes a FINISHED document -- macOS and iOS PDF data, CUPS a file,
// Android a file descriptor, the browser a blob -- so off Windows a PrintDocument is recorded page by
// page, written as one vector PDF (WebGpuBackend.ScenePdfDocument), and handed over whole. Which
// print system receives it depends on how WinForms is running:
//
//   * STANDALONE on macOS (host/CocoaHost): through CocoaPrint, link-compiled into this assembly --
//     NSPrintOperation over PDFKit, the system print panel at submission;
//   * HOSTED in WPF (WindowsFormsHost: Linux, Android, iOS, the browser): through WPF's own
//     PlatformPrint backend, which the host installs here as the Bridge. There is one PlatformPrint
//     per process, in WindowsBase, set up with the platform hosts its backends need; this assembly
//     must not grow a second one that nothing ever hosts.
//
// Windows does not come through here at all: its spooler is a drawing surface, and the pages are
// replayed onto the printer DC (WebGpuBackend.SceneGdiDevice).
//

using System;
using System.Collections.Generic;
using System.IO;

namespace System.Drawing.Printing
{
	/// <summary>A printer as a print system describes it. Sizes in hundredths of an inch.</summary>
	internal sealed class PrintSystemPrinter
	{
		internal string Name;
		internal string DisplayName;
		internal bool IsDefault;
		/// <summary>The default paper, zero when not known.</summary>
		internal float PaperWidth, PaperHeight;
		/// <summary>The markable part of the paper, from its top left; empty when not known.</summary>
		internal RectangleF Imageable;
	}

	/// <summary>What a job asks the print system for. Sizes in hundredths of an inch.</summary>
	internal sealed class PrintSystemJob
	{
		internal string PrinterName;
		internal int Copies = 1;
		internal bool Collate = true;
		internal int FirstPage, LastPage;
		internal bool Landscape;
		internal float PaperWidth, PaperHeight;
		internal string OutputFile;
	}

	/// <summary>A print system that takes finished PDF documents.</summary>
	internal interface IPrintSystem
	{
		PrintSystemPrinter [] EnumeratePrinters ();
		bool Submit (string jobName, Stream pdf, PrintSystemJob job);
	}

	internal static class PrintSystem
	{
		static IPrintSystem s_platform;
		static bool s_platformResolved;

		/// <summary>Installed by a host whose process already has a print system -- WPF's, through
		/// WindowsFormsHost. Wins over the platform's own.</summary>
		internal static IPrintSystem Bridge { get; set; }

		/// <summary>The print system jobs go to off Windows, or null where there is none.</summary>
		internal static IPrintSystem Current {
			get {
				if (Bridge != null)
					return Bridge;
				if (!s_platformResolved) {
					s_platformResolved = true;
					if (OperatingSystem.IsMacOS () && !OperatingSystem.IsIOS () && !OperatingSystem.IsMacCatalyst ())
						s_platform = new CocoaPrintSystem ();
				}
				return s_platform;
			}
		}

		internal static PrintSystemPrinter [] Printers ()
		{
			try {
				return Current?.EnumeratePrinters () ?? Array.Empty<PrintSystemPrinter> ();
			} catch (Exception) {
				// A print system that cannot be asked is a machine with no printers, not a crash in
				// the PrinterSettings constructor of every application that touches printing.
				return Array.Empty<PrintSystemPrinter> ();
			}
		}

		internal static PrintSystemPrinter Find (string name)
		{
			if (string.IsNullOrEmpty (name))
				return null;
			foreach (PrintSystemPrinter p in Printers ())
				if (string.Equals (p.Name, name, StringComparison.Ordinal))
					return p;
			return null;
		}
	}

	/// <summary>The macOS print system for a standalone WinForms app: CocoaPrint (shared with WPF,
	/// link-compiled here), in this seam's vocabulary.</summary>
	[System.Runtime.Versioning.SupportedOSPlatform ("macos")]
	internal sealed class CocoaPrintSystem : IPrintSystem
	{
		readonly MS.Internal.Interop.CocoaPrint backend = new MS.Internal.Interop.CocoaPrint ();

		// PrinterInfo is in WPF units, 96ths of an inch.
		const float WpfToHundredths = 100f / 96f;

		public PrintSystemPrinter [] EnumeratePrinters ()
		{
			var list = new List<PrintSystemPrinter> ();
			foreach (MS.Internal.Interop.PrinterInfo p in backend.EnumeratePrinters ()) {
				list.Add (new PrintSystemPrinter {
					Name = p.Name,
					DisplayName = p.DisplayName,
					IsDefault = p.IsDefault,
					PaperWidth = (float) p.PageWidth * WpfToHundredths,
					PaperHeight = (float) p.PageHeight * WpfToHundredths,
					Imageable = new RectangleF ((float) p.ImageableOriginX * WpfToHundredths, (float) p.ImageableOriginY * WpfToHundredths,
						(float) p.ImageableWidth * WpfToHundredths, (float) p.ImageableHeight * WpfToHundredths),
				});
			}
			return list.ToArray ();
		}

		public bool Submit (string jobName, Stream pdf, PrintSystemJob job)
		{
			return backend.Submit (jobName, pdf, new MS.Internal.Interop.PrintJobSettings {
				PrinterName = job.PrinterName,
				Copies = job.Copies,
				FirstPage = job.FirstPage,
				LastPage = job.LastPage,
				Landscape = job.Landscape,
				PageWidth = job.PaperWidth / WpfToHundredths,
				PageHeight = job.PaperHeight / WpfToHundredths,
				OutputFile = job.OutputFile,
			});
		}
	}
}
