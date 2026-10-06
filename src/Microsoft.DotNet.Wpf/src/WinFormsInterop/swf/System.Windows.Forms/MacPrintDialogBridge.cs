// macOS's own print dialog for WinForms' PrintDialog: NSPrintPanel run on its own (CocoaDialogs
// .ShowPrintPanel), starting from the PrinterSettings and handing back the printer, copies, page
// range and orientation the user chose -- the choose-first, print-later shape WinForms' dialog has.

using System;
using System.Drawing.Printing;

namespace System.Windows.Forms
{
	internal sealed class MacPrintDialogBridge : IPrintDialogBridge
	{
		internal static readonly IPrintDialogBridge Default =
			OperatingSystem.IsMacOS () ? new MacPrintDialogBridge () : null;

		public bool? Show (PrintDialog d, PrinterSettings settings, PageSettings page)
		{
			string printer = settings.PrinterName;
			int copies = Math.Max ((short) 1, settings.Copies);
			bool all = !(d.AllowSomePages && settings.PrintRange == PrintRange.SomePages);
			int first = settings.FromPage, last = settings.ToPage;
			bool landscape = page?.landscape ?? settings.DefaultPageSettings.landscape;
			bool ok;
			try {
				ok = MS.Internal.Interop.CocoaDialogs.ShowPrintPanel (ref printer, ref copies, ref all, ref first, ref last,
					ref landscape, d.AllowSomePages);
			} catch (Exception) {
				return null;   // AppKit unavailable: the managed dialog
			}
			if (!ok)
				return false;
			if (!string.IsNullOrEmpty (printer))
				settings.PrinterName = printer;
			settings.Copies = (short) Math.Max (1, copies);
			if (d.AllowSomePages && !all) {
				settings.PrintRange = PrintRange.SomePages;
				settings.FromPage = first;
				settings.ToPage = last;
			} else {
				settings.PrintRange = PrintRange.AllPages;
			}
			if (page != null)
				page.landscape = landscape;
			else
				settings.DefaultPageSettings.landscape = landscape;
			return true;
		}
	}
}
