// The picture of a standard cursor, for the places that DRAW one rather than set it: the cursor
// editor's list, an owner-drawn picker. It was a generated stub returning nothing, so every
// standard cursor drew as an empty box.
//
// On Windows the cursor is the system's own -- LoadCursor through platform-gated DllImports, the
// same way SystemIcons takes Windows' icons -- so it is whatever the user's cursor scheme draws,
// as in stock WinForms. Elsewhere there is no system cursor to read and nothing is drawn, as before.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		[DllImport ("user32.dll", EntryPoint = "LoadCursorW")]
		private static extern IntPtr Win32LoadCursorForImage (IntPtr hInstance, IntPtr lpCursorName);

		[StructLayout (LayoutKind.Sequential)]
		private struct CursorIconInfo { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

		[StructLayout (LayoutKind.Sequential)]
		private struct CursorBitmapInfo {
			public int biSize, biWidth, biHeight;
			public short biPlanes, biBitCount;
			public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
			public int c0, c1;   // room for a monochrome bitmap's two-entry colour table
		}

		[StructLayout (LayoutKind.Sequential)]
		private struct CursorGdiBitmap { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

		[DllImport ("user32.dll", EntryPoint = "GetIconInfo")]
		private static extern bool Win32GetIconInfoForCursor (IntPtr hIcon, out CursorIconInfo info);
		[DllImport ("gdi32.dll", EntryPoint = "GetObjectW")]
		private static extern int Win32GetCursorBitmap (IntPtr h, int size, out CursorGdiBitmap bm);
		[DllImport ("gdi32.dll", EntryPoint = "GetDIBits")]
		private static extern int Win32GetCursorDIBits (IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] int [] bits, ref CursorBitmapInfo bmi, uint usage);
		[DllImport ("user32.dll", EntryPoint = "GetDC")]
		private static extern IntPtr Win32GetScreenDCForCursor (IntPtr hwnd);
		[DllImport ("user32.dll", EntryPoint = "ReleaseDC")]
		private static extern int Win32ReleaseScreenDCForCursor (IntPtr hwnd, IntPtr hdc);
		[DllImport ("gdi32.dll", EntryPoint = "DeleteObject")]
		private static extern bool Win32DeleteCursorObject (IntPtr h);

		/// <summary>The IDC_* resource a standard cursor is on Windows, or 0 for the ones .NET
		/// builds from its own .cur resources (the splitters, the no-move and pan cursors).</summary>
		private static int SystemCursorId (StdCursor id) => id switch {
			StdCursor.AppStarting => 32650,
			StdCursor.Arrow or StdCursor.Default => 32512,
			StdCursor.Cross => 32515,
			StdCursor.Hand => 32649,
			StdCursor.Help => 32651,
			StdCursor.IBeam => 32513,
			StdCursor.No => 32648,
			StdCursor.SizeAll => 32646,
			StdCursor.SizeNESW => 32643,
			StdCursor.SizeNS => 32645,
			StdCursor.SizeNWSE => 32642,
			StdCursor.SizeWE => 32644,
			StdCursor.UpArrow => 32516,
			StdCursor.WaitCursor => 32514,
			_ => 0,
		};

		internal override Bitmap DefineStdCursorBitmap (StdCursor id)
		{
			if (!OperatingSystem.IsWindows ())
				return null;
			int idc = SystemCursorId (id);
			if (idc == 0)
				return null;
			try {
				IntPtr handle = Win32LoadCursorForImage (IntPtr.Zero, (IntPtr) idc);
				return handle == IntPtr.Zero ? null : CursorToBitmap (handle);
			} catch (Exception) {
				return null;
			}
		}

		/// <summary>A cursor's pixels as ARGB, read out of its bitmaps (GDI+'s FromHicon refuses a
		/// cursor). A colour cursor is its colour bitmap, with its alpha if it has one and the AND
		/// mask's transparency if not. A monochrome cursor is its mask, AND over XOR, twice as tall:
		/// AND 0 is the XOR colour, AND 1 with XOR 0 is see-through, and AND 1 with XOR 1 -- the
		/// inverted outline DrawIconEx draws -- is drawn black.</summary>
		private static Bitmap CursorToBitmap (IntPtr handle)
		{
			if (!Win32GetIconInfoForCursor (handle, out CursorIconInfo info))
				return null;
			IntPtr dc = Win32GetScreenDCForCursor (IntPtr.Zero);
			try {
				Win32GetCursorBitmap (info.hbmMask, Marshal.SizeOf<CursorGdiBitmap> (), out CursorGdiBitmap maskBm);
				int w = maskBm.bmWidth;
				bool mono = info.hbmColor == IntPtr.Zero;
				int h = mono ? maskBm.bmHeight / 2 : maskBm.bmHeight;
				if (w <= 0 || h <= 0)
					return null;
				int [] mask = Read (dc, info.hbmMask, w, maskBm.bmHeight);
				int [] color = mono ? null : Read (dc, info.hbmColor, w, h);
				bool hasAlpha = false;
				if (color != null)
					foreach (int c in color)
						if ((c & unchecked ((int) 0xff000000)) != 0) { hasAlpha = true; break; }

				var bmp = new Bitmap (w, h, PixelFormat.Format32bppArgb);
				for (int y = 0; y < h; y++)
					for (int x = 0; x < w; x++) {
						bool and = (mask [y * w + x] & 0xffffff) != 0;
						int argb;
						if (!mono) {
							int c = color [y * w + x];
							argb = hasAlpha ? c : (and ? 0 : c | unchecked ((int) 0xff000000));
						} else {
							bool xor = (mask [(y + h) * w + x] & 0xffffff) != 0;
							argb = !and ? (xor ? unchecked ((int) 0xffffffff) : unchecked ((int) 0xff000000))
								: xor ? unchecked ((int) 0xff000000) : 0;
						}
						bmp.SetPixel (x, y, Color.FromArgb (argb));
					}
				return bmp;
			} finally {
				Win32ReleaseScreenDCForCursor (IntPtr.Zero, dc);
				if (info.hbmMask != IntPtr.Zero) Win32DeleteCursorObject (info.hbmMask);
				if (info.hbmColor != IntPtr.Zero) Win32DeleteCursorObject (info.hbmColor);
			}
		}

		/// <summary>A bitmap's pixels as top-down 32-bit BGRA.</summary>
		private static int [] Read (IntPtr dc, IntPtr hbm, int w, int h)
		{
			var bmi = new CursorBitmapInfo {
				biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
			};
			var bits = new int [w * h];
			Win32GetCursorDIBits (dc, hbm, 0, (uint) h, bits, ref bmi, 0);
			return bits;
		}
	}
}
