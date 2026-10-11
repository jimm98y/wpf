// PrinterSettings / PageSettings to and from the Win32 DEVMODE and DEVNAMES blocks, as .NET's
// System.Drawing does it: GetHdevmode asks the printer's driver for its own DEVMODE
// (DocumentProperties), writes the settings into it and has the driver validate the result; the
// block is a movable HGLOBAL the caller frees with GlobalFree. SetHdevmode reads back what a print
// or page setup dialog chose. Mono had none of these (NotImplementedException), and its printer DC
// was created with no DEVMODE at all, so copies, orientation, duplex and paper never reached the
// printer.
//
// Windows only: these are winspool and kernel32 calls, and every caller is behind
// OperatingSystem.IsWindows().

using System.Runtime.InteropServices;

namespace System.Drawing.Printing
{
	internal static class DevModeInterop
	{
		// DEVMODEW: no pointer fields, so the same offsets on every architecture.
		private const int OffFields = 72, OffOrientation = 76, OffPaperSize = 78, OffPaperLength = 80,
			OffPaperWidth = 82, OffCopies = 86, OffDefaultSource = 88, OffColor = 92, OffDuplex = 94, OffCollate = 100;
		private const int DM_ORIENTATION = 0x1, DM_PAPERSIZE = 0x2, DM_PAPERLENGTH = 0x4, DM_PAPERWIDTH = 0x8,
			DM_COPIES = 0x100, DM_COLOR = 0x800, DM_DUPLEX = 0x1000, DM_COLLATE = 0x8000;
		private const int DM_OUT_BUFFER = 2, DM_IN_BUFFER = 8;
		private const uint GMEM_MOVEABLE = 0x2;

		[DllImport ("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool OpenPrinterW (string name, out IntPtr printer, IntPtr defaults);
		[DllImport ("winspool.drv", SetLastError = true)]
		private static extern bool ClosePrinter (IntPtr printer);
		[DllImport ("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern int DocumentPropertiesW (IntPtr hwnd, IntPtr printer, string device, IntPtr output, IntPtr input, int mode);
		[DllImport ("kernel32.dll", SetLastError = true)]
		internal static extern IntPtr GlobalAlloc (uint flags, UIntPtr bytes);
		[DllImport ("kernel32.dll", SetLastError = true)]
		internal static extern IntPtr GlobalLock (IntPtr h);
		[DllImport ("kernel32.dll", SetLastError = true)]
		internal static extern bool GlobalUnlock (IntPtr h);
		[DllImport ("kernel32.dll", SetLastError = true)]
		internal static extern IntPtr GlobalFree (IntPtr h);
		[DllImport ("kernel32.dll")]
		private static extern UIntPtr GlobalSize (IntPtr h);

		/// <summary>PrinterSettings.GetHdevmode(PageSettings): the driver's DEVMODE with the settings in it.</summary>
		internal static IntPtr GetHdevmode (PrinterSettings settings, PageSettings page)
		{
			string name = settings.PrinterName;
			if (!OpenPrinterW (name, out IntPtr printer, IntPtr.Zero))
				throw new InvalidPrinterException (settings);
			try {
				int size = DocumentPropertiesW (IntPtr.Zero, printer, name, IntPtr.Zero, IntPtr.Zero, 0);
				if (size <= 0)
					throw new InvalidPrinterException (settings);
				IntPtr handle = GlobalAlloc (GMEM_MOVEABLE, (UIntPtr) (uint) size);
				IntPtr dm = GlobalLock (handle);
				try {
					if (DocumentPropertiesW (IntPtr.Zero, printer, name, dm, IntPtr.Zero, DM_OUT_BUFFER) < 0)
						throw new InvalidPrinterException (settings);
					int fields = Marshal.ReadInt32 (dm, OffFields);
					if ((fields & DM_COPIES) != 0)
						Marshal.WriteInt16 (dm, OffCopies, settings.Copies);
					if ((fields & DM_COLLATE) != 0)
						Marshal.WriteInt16 (dm, OffCollate, (short) (settings.Collate ? 1 : 0));
					if ((fields & DM_DUPLEX) != 0 && settings.Duplex != Duplex.Default)
						Marshal.WriteInt16 (dm, OffDuplex, (short) settings.Duplex);
					if (page != null)
						CopyPage (page, dm, fields);
					// The driver checks and completes what was written, in place.
					DocumentPropertiesW (IntPtr.Zero, printer, name, dm, dm, DM_IN_BUFFER | DM_OUT_BUFFER);
				} finally {
					GlobalUnlock (handle);
				}
				return handle;
			} finally {
				ClosePrinter (printer);
			}
		}

		/// <summary>PageSettings.CopyToHdevmode: orientation, paper and colour into a DEVMODE.</summary>
		internal static void CopyToHdevmode (PageSettings page, IntPtr handle)
		{
			IntPtr dm = GlobalLock (handle);
			if (dm == IntPtr.Zero)
				throw new ArgumentException (nameof (handle));
			try {
				CopyPage (page, dm, Marshal.ReadInt32 (dm, OffFields));
			} finally {
				GlobalUnlock (handle);
			}
		}

		private static void CopyPage (PageSettings page, IntPtr dm, int fields)
		{
			if ((fields & DM_ORIENTATION) != 0)
				Marshal.WriteInt16 (dm, OffOrientation, (short) (page.landscape ? 2 : 1));
			if ((fields & DM_COLOR) != 0)
				Marshal.WriteInt16 (dm, OffColor, (short) (page.color ? 2 : 1));
			PaperSize paper = page.paperSize;
			if (paper != null && (fields & DM_PAPERSIZE) != 0) {
				if (paper.RawKind > 0 && paper.Kind != PaperKind.Custom) {
					Marshal.WriteInt16 (dm, OffPaperSize, (short) paper.RawKind);
				} else {
					// A custom size, in tenths of a millimetre.
					Marshal.WriteInt16 (dm, OffPaperSize, 0);
					Marshal.WriteInt16 (dm, OffPaperLength, (short) Math.Round (paper.Height * 0.254));
					Marshal.WriteInt16 (dm, OffPaperWidth, (short) Math.Round (paper.Width * 0.254));
					Marshal.WriteInt32 (dm, OffFields, fields | DM_PAPERLENGTH | DM_PAPERWIDTH);
				}
			}
		}

		/// <summary>PrinterSettings.SetHdevmode: copies, collation and duplex from a DEVMODE.</summary>
		internal static void SetHdevmode (PrinterSettings settings, IntPtr handle)
		{
			IntPtr dm = GlobalLock (handle);
			if (dm == IntPtr.Zero)
				throw new ArgumentException (nameof (handle));
			try {
				int fields = Marshal.ReadInt32 (dm, OffFields);
				if ((fields & DM_COPIES) != 0)
					settings.Copies = Marshal.ReadInt16 (dm, OffCopies);
				if ((fields & DM_COLLATE) != 0)
					settings.Collate = Marshal.ReadInt16 (dm, OffCollate) == 1;
				if ((fields & DM_DUPLEX) != 0)
					settings.Duplex = (Duplex) Marshal.ReadInt16 (dm, OffDuplex);
			} finally {
				GlobalUnlock (handle);
			}
		}

		/// <summary>PageSettings.SetHdevmode: orientation, paper and colour from a DEVMODE.</summary>
		internal static void SetPageHdevmode (PageSettings page, IntPtr handle)
		{
			IntPtr dm = GlobalLock (handle);
			if (dm == IntPtr.Zero)
				throw new ArgumentException (nameof (handle));
			try {
				int fields = Marshal.ReadInt32 (dm, OffFields);
				if ((fields & DM_ORIENTATION) != 0)
					page.landscape = Marshal.ReadInt16 (dm, OffOrientation) == 2;
				if ((fields & DM_COLOR) != 0)
					page.color = Marshal.ReadInt16 (dm, OffColor) == 2;
				if ((fields & DM_PAPERSIZE) != 0) {
					int kind = Marshal.ReadInt16 (dm, OffPaperSize);
					PaperSize found = null;
					PrinterSettings ps = page.PrinterSettings;
					if (ps != null && kind > 0)
						foreach (PaperSize size in ps.PaperSizes)
							if (size.RawKind == kind) { found = size; break; }
					if (found == null && (fields & (DM_PAPERLENGTH | DM_PAPERWIDTH)) == (DM_PAPERLENGTH | DM_PAPERWIDTH)) {
						int w = (int) Math.Round (Marshal.ReadInt16 (dm, OffPaperWidth) / 0.254);
						int h = (int) Math.Round (Marshal.ReadInt16 (dm, OffPaperLength) / 0.254);
						found = new PaperSize ("Custom", w, h) { RawKind = kind };
					}
					if (found != null)
						page.paperSize = found;
				}
			} finally {
				GlobalUnlock (handle);
			}
		}

		/// <summary>PrinterSettings.GetHdevnames: driver, device and port, as .NET writes them.</summary>
		internal static IntPtr GetHdevnames (PrinterSettings settings)
		{
			string driver = "winspool", device = settings.PrinterName ?? "", port = "";
			int chars = 4 + driver.Length + device.Length + port.Length + 3;
			IntPtr handle = GlobalAlloc (GMEM_MOVEABLE, (UIntPtr) (uint) ((chars + 4) * 2));
			IntPtr p = GlobalLock (handle);
			try {
				// DEVNAMES: four WORD offsets, in characters, then the strings.
				short offset = 4;
				Marshal.WriteInt16 (p, 0, offset);
				WriteString (p, offset, driver);
				offset += (short) (driver.Length + 1);
				Marshal.WriteInt16 (p, 2, offset);
				WriteString (p, offset, device);
				offset += (short) (device.Length + 1);
				Marshal.WriteInt16 (p, 4, offset);
				WriteString (p, offset, port);
				Marshal.WriteInt16 (p, 6, 0);
			} finally {
				GlobalUnlock (handle);
			}
			return handle;
		}

		/// <summary>PrinterSettings.SetHdevnames: the device chosen.</summary>
		internal static string ReadDevice (IntPtr handle)
		{
			IntPtr p = GlobalLock (handle);
			if (p == IntPtr.Zero)
				throw new ArgumentException (nameof (handle));
			try {
				int device = Marshal.ReadInt16 (p, 2);
				return Marshal.PtrToStringUni (p + device * 2);
			} finally {
				GlobalUnlock (handle);
			}
		}

		private static void WriteString (IntPtr p, int charOffset, string s)
		{
			char [] chars = (s + "\0").ToCharArray ();
			Marshal.Copy (chars, 0, p + charOffset * 2, chars.Length);
		}
	}
}
