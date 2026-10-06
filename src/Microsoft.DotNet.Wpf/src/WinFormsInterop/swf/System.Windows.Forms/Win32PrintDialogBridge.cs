// The print dialog Windows itself shows, as .NET's PrintDialog shows it: PrintDlg (the classic
// dialog) by default and PrintDlgEx (the property-sheet one) when UseEXDialog is set, the flags from
// the dialog's properties (GetFlags), the printer settings handed over and taken back as DEVMODE and
// DEVNAMES blocks (UpdatePrinterSettings), the classic dialog centred by the CommonDialog hook.
// Plain platform-gated DllImports of comdlg32; no COM (PrintDlgEx's callback is not used).

using System;
using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	/// <summary>A platform print dialog. Null from Show means there is none to show here, and the
	/// managed dialog runs.</summary>
	internal interface IPrintDialogBridge
	{
		bool? Show (PrintDialog dialog, PrinterSettings settings, PageSettings page);
	}

	internal sealed class Win32PrintDialogBridge : IPrintDialogBridge
	{
		internal static readonly IPrintDialogBridge Default =
			OperatingSystem.IsWindows () ? new Win32PrintDialogBridge () : null;

		private const int PD_ALLPAGES = 0, PD_SELECTION = 0x1, PD_PAGENUMS = 0x2, PD_NOSELECTION = 0x4,
			PD_NOPAGENUMS = 0x8, PD_COLLATE = 0x10, PD_PRINTTOFILE = 0x20, PD_ENABLEPRINTHOOK = 0x1000,
			PD_SHOWHELP = 0x800, PD_DISABLEPRINTTOFILE = 0x80000, PD_NONETWORKBUTTON = 0x200000,
			PD_CURRENTPAGE = 0x400000, PD_NOCURRENTPAGE = 0x800000, PD_USEDEVMODECOPIESANDCOLLATE = 0x40000;
		private const int PrintRangeMask = PD_ALLPAGES | PD_PAGENUMS | PD_SELECTION | PD_CURRENTPAGE;
		private const int START_PAGE_GENERAL = -1, PD_RESULT_PRINT = 1, PD_RESULT_CANCEL = 0;

		// PRINTDLGW with the natural 64-bit layout (32-bit packs it to 1, which this port does not ship).
		[StructLayout (LayoutKind.Sequential)]
		private struct PRINTDLGW
		{
			public int lStructSize;
			public IntPtr hwndOwner, hDevMode, hDevNames, hDC;
			public int Flags;
			public short nFromPage, nToPage, nMinPage, nMaxPage, nCopies;
			public IntPtr hInstance, lCustData, lpfnPrintHook, lpfnSetupHook, lpPrintTemplateName,
				lpSetupTemplateName, hPrintTemplate, hSetupTemplate;
		}

		[StructLayout (LayoutKind.Sequential)]
		private struct PRINTDLGEXW
		{
			public int lStructSize;
			public IntPtr hwndOwner, hDevMode, hDevNames, hDC;
			public int Flags, Flags2, ExclusionFlags, nPageRanges, nMaxPageRanges;
			public IntPtr lpPageRanges;
			public int nMinPage, nMaxPage, nCopies;
			public IntPtr hInstance, lpPrintTemplateName, lpCallback;
			public int nPropertyPages;
			public IntPtr lphPropertyPages;
			public int nStartPage, dwResultAction;
		}

		[StructLayout (LayoutKind.Sequential)]
		private struct PRINTPAGERANGE { public int nFromPage, nToPage; }

		private delegate IntPtr HookProc (IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		[DllImport ("comdlg32.dll", CharSet = CharSet.Unicode)]
		private static extern bool PrintDlgW (ref PRINTDLGW pd);
		[DllImport ("comdlg32.dll", CharSet = CharSet.Unicode)]
		private static extern int PrintDlgExW (ref PRINTDLGEXW pd);
		[DllImport ("user32.dll")]
		private static extern IntPtr GetActiveWindow ();
		[DllImport ("user32.dll")]
		private static extern IntPtr SetFocus (IntPtr hwnd);
		[DllImport ("user32.dll")]
		private static extern bool GetWindowRect (IntPtr hwnd, out RECT r);
		[DllImport ("user32.dll")]
		private static extern bool SetWindowPos (IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
		[DllImport ("user32.dll")]
		private static extern bool GetCursorPos (out POINT p);
		[DllImport ("user32.dll")]
		private static extern IntPtr MonitorFromPoint (POINT p, int flags);
		[DllImport ("user32.dll")]
		private static extern bool GetMonitorInfoW (IntPtr monitor, ref MONITORINFO info);

		[StructLayout (LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
		[StructLayout (LayoutKind.Sequential)] private struct POINT { public int x, y; }
		[StructLayout (LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

		/// <summary>PrintDialog.GetFlags.</summary>
		private static int Flags (PrintDialog d, PrinterSettings settings, bool ex)
		{
			int flags = PD_ALLPAGES;
			if (!ex) flags |= PD_ENABLEPRINTHOOK;
			if (!d.AllowCurrentPage) flags |= PD_NOCURRENTPAGE;
			if (!d.AllowSomePages) flags |= PD_NOPAGENUMS;
			if (!d.AllowPrintToFile) flags |= PD_DISABLEPRINTTOFILE;
			if (!d.AllowSelection) flags |= PD_NOSELECTION;
			flags |= (int) settings.PrintRange;
			if (d.PrintToFile) flags |= PD_PRINTTOFILE;
			if (d.ShowHelp) flags |= PD_SHOWHELP;
			if (!d.ShowNetwork) flags |= PD_NONETWORKBUTTON;
			if (settings.Collate) flags |= PD_COLLATE;
			return flags;
		}

		public bool? Show (PrintDialog d, PrinterSettings settings, PageSettings page)
		{
			IntPtr devMode = IntPtr.Zero, devNames = IntPtr.Zero;
			try {
				devMode = page == null ? settings.GetHdevmode () : settings.GetHdevmode (page);
				devNames = settings.GetHdevnames ();
			} catch (InvalidPrinterException) {
				// Leave them null; Windows fills them in.
				devMode = devNames = IntPtr.Zero;
			}

			if (d.AllowSomePages) {
				if (settings.FromPage < settings.MinimumPage || settings.FromPage > settings.MaximumPage)
					throw new ArgumentException ("FromPage out of range");
				if (settings.ToPage < settings.MinimumPage || settings.ToPage > settings.MaximumPage)
					throw new ArgumentException ("ToPage out of range");
				if (settings.ToPage < settings.FromPage)
					throw new ArgumentException ("FromPage out of range");
			}

			IntPtr cookie = Win32ThemingScope.Enter ();
			try {
				return d.UseEXDialog ? ShowEx (d, settings, page, ref devMode, ref devNames)
					: ShowClassic (d, settings, page, ref devMode, ref devNames);
			} finally {
				Win32ThemingScope.Leave (cookie);
				if (devMode != IntPtr.Zero) DevModeGlobals.Free (devMode);
				if (devNames != IntPtr.Zero) DevModeGlobals.Free (devNames);
			}
		}

		private bool ShowClassic (PrintDialog d, PrinterSettings settings, PageSettings page, ref IntPtr devMode, ref IntPtr devNames)
		{
			IntPtr focus = IntPtr.Zero;
			HookProc hook = (hWnd, msg, wParam, lParam) => {
				// CommonDialog.HookProc: centred on the screen the mouse is on, its control focused.
				if (msg == 0x0110) {   // WM_INITDIALOG
					MoveToScreenCenter (hWnd);
					focus = wParam;
					SetFocus (wParam);
				}
				return IntPtr.Zero;
			};
			var pd = new PRINTDLGW {
				lStructSize = Marshal.SizeOf<PRINTDLGW> (),
				hwndOwner = Win32DialogOwner.Get (),
				hDevMode = devMode,
				hDevNames = devNames,
				Flags = Flags (d, settings, ex: false),
				nFromPage = 1, nToPage = 1, nMaxPage = 9999,
				nCopies = settings.Copies,
				lpfnPrintHook = Marshal.GetFunctionPointerForDelegate (hook),
			};
			if (d.AllowSomePages) {
				pd.nFromPage = (short) settings.FromPage;
				pd.nToPage = (short) settings.ToPage;
				pd.nMinPage = (short) settings.MinimumPage;
				pd.nMaxPage = (short) settings.MaximumPage;
			}
			bool ok = PrintDlgW (ref pd);
			GC.KeepAlive (hook);
			devMode = pd.hDevMode;
			devNames = pd.hDevNames;
			if (!ok)
				return false;
			Update (pd.hDevMode, pd.hDevNames, pd.nCopies, pd.Flags, settings, page);
			d.PrintToFile = (pd.Flags & PD_PRINTTOFILE) != 0;
			settings.PrintToFile = d.PrintToFile;
			if (d.AllowSomePages) {
				settings.FromPage = pd.nFromPage;
				settings.ToPage = pd.nToPage;
			}
			// Without PD_USEDEVMODECOPIESANDCOLLATE the copies and collation are the dialog's own.
			if ((pd.Flags & PD_USEDEVMODECOPIESANDCOLLATE) == 0) {
				settings.Copies = pd.nCopies;
				settings.Collate = (pd.Flags & PD_COLLATE) != 0;
			}
			return true;
		}

		private bool ShowEx (PrintDialog d, PrinterSettings settings, PageSettings page, ref IntPtr devMode, ref IntPtr devNames)
		{
			IntPtr range = Marshal.AllocHGlobal (Marshal.SizeOf<PRINTPAGERANGE> ());
			try {
				var pd = new PRINTDLGEXW {
					lStructSize = Marshal.SizeOf<PRINTDLGEXW> (),
					hwndOwner = Win32DialogOwner.Get (),
					hDevMode = devMode,
					hDevNames = devNames,
					// PD_SHOWHELP and PD_NONETWORKBUTTON do not work with PrintDlgEx (.NET clears them).
					Flags = Flags (d, settings, ex: true) & ~(PD_SHOWHELP | PD_NONETWORKBUTTON),
					nMinPage = 0, nMaxPage = 9999, nCopies = 1,
					nStartPage = START_PAGE_GENERAL,
					lpPageRanges = range,
				};
				if (d.AllowSomePages) {
					pd.nMinPage = settings.MinimumPage;
					pd.nMaxPage = settings.MaximumPage;
					pd.nPageRanges = 1;
					pd.nMaxPageRanges = 1;
					Marshal.StructureToPtr (new PRINTPAGERANGE { nFromPage = settings.FromPage, nToPage = settings.ToPage }, range, false);
				}
				int hr = PrintDlgExW (ref pd);
				if (hr < 0)
					Console.Error.WriteLine ($"[printdialog] PrintDlgEx failed: 0x{hr:X8}");
				devMode = pd.hDevMode;
				devNames = pd.hDevNames;
				if (hr < 0 || pd.dwResultAction == PD_RESULT_CANCEL)
					return false;
				Update (pd.hDevMode, pd.hDevNames, (short) pd.nCopies, pd.Flags, settings, page);
				d.PrintToFile = (pd.Flags & PD_PRINTTOFILE) != 0;
				settings.PrintToFile = d.PrintToFile;
				if (d.AllowSomePages && pd.nPageRanges > 0) {
					var r = Marshal.PtrToStructure<PRINTPAGERANGE> (range);
					settings.FromPage = r.nFromPage;
					settings.ToPage = r.nToPage;
				}
				if ((pd.Flags & PD_USEDEVMODECOPIESANDCOLLATE) == 0) {
					settings.Copies = (short) pd.nCopies;
					settings.Collate = (pd.Flags & PD_COLLATE) != 0;
				}
				return pd.dwResultAction == PD_RESULT_PRINT;
			} finally {
				Marshal.FreeHGlobal (range);
			}
		}

		/// <summary>PrintDialog.UpdatePrinterSettings.</summary>
		private static void Update (IntPtr devMode, IntPtr devNames, short copies, int flags, PrinterSettings settings, PageSettings page)
		{
			settings.SetHdevmode (devMode);
			settings.SetHdevnames (devNames);
			page?.SetHdevmode (devMode);
			if (settings.Copies == 1)
				settings.Copies = copies;
			settings.PrintRange = (PrintRange) (flags & PrintRangeMask);
		}

		private static void MoveToScreenCenter (IntPtr hWnd)
		{
			GetWindowRect (hWnd, out RECT r);
			GetCursorPos (out POINT p);
			var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO> () };
			if (!GetMonitorInfoW (MonitorFromPoint (p, 2), ref info))
				return;
			RECT s = info.rcWork;
			SetWindowPos (hWnd, IntPtr.Zero, s.left + (s.right - s.left - (r.right - r.left)) / 2,
				s.top + (s.bottom - s.top - (r.bottom - r.top)) / 3, 0, 0, 0x0001 | 0x0004 | 0x0010);
		}

		private static class DevModeGlobals
		{
			[DllImport ("kernel32.dll")]
			private static extern IntPtr GlobalFree (IntPtr h);
			internal static void Free (IntPtr h) => GlobalFree (h);
		}
	}
}
