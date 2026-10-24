//
// Copyright (C) 2005, 2007 Novell, Inc (http://www.novell.com)
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
// Authors:
//	Jordi Mas i Hernandez <jordimash@gmail.com>
//	Sebastien Pouliot  <sebastien@ximian.com>
//


namespace System.Drawing {

	public sealed class SystemFonts {
		
		static SystemFonts ()
		{
		}

		private SystemFonts()
		{
		}

		public static Font GetFontByName (string systemFontName)
		{
			if (systemFontName == "CaptionFont")
				return CaptionFont;

			if (systemFontName == "DefaultFont")
				return DefaultFont;

			if (systemFontName == "DialogFont")
				return DialogFont;	

			if (systemFontName == "IconTitleFont")
				return IconTitleFont;

			if (systemFontName == "MenuFont")
				return MenuFont;

			if (systemFontName == "MessageBoxFont")
				return MessageBoxFont;

			if (systemFontName == "SmallCaptionFont")
				return SmallCaptionFont;

			if (systemFontName == "StatusFont")
				return StatusFont;			
			
			return null;
		}

		// Windows has not used Microsoft Sans Serif for its shell since XP: everything from the
		// window caption to a dialog's body text is Segoe UI at 9 point, which is also what the
		// WinForms that ships with .NET reports here. Saying otherwise made every control on this
		// stack a different size and shape from the same control next door. Elsewhere the same,
		// wherever Segoe UI can be had -- the stack's Selawik is its metric-compatible stand-in
		// (GpFontFamily.Canonical) -- and the old answer only where it cannot: a TextBox there was
		// 20 pixels high where Windows' is 23, and every form laid out from the font with it.
		static bool HasShellFamily => OperatingSystem.IsWindows () || WebGpuBackend.Gdip.GpFontFamily.Canonical ("Segoe UI") != null;
		static string ShellFamily => HasShellFamily ? "Segoe UI" : "Microsoft Sans Serif";
		static float ShellSize => HasShellFamily ? 9f : 8.25f;

		public static Font CaptionFont { 
			get { return new Font (ShellFamily, ShellSize, "CaptionFont"); }
		}

		// Two that are not the shell's: stock's DefaultFont is GDI's DEFAULT_GUI_FONT (MS Shell Dlg,
		// so Microsoft Sans Serif 8.25 -- in the charset of the system's locale), and its DialogFont
		// is MS Shell Dlg 2, Tahoma 8. Apps lay out against these: a DotNetBar ribbon draws its tab
		// and group captions in DefaultFont, and in Segoe UI they came out a different shape and width.
		public static Font DefaultFont  {
			get {
				if (OperatingSystem.IsWindows () && DefaultGuiFont (out string face, out float points, out byte charSet))
					return new Font (face, points, charSet, "DefaultFont");
				// DEFAULT_GUI_FONT where there is no GDI to ask: MS Shell Dlg, Microsoft Sans Serif 8.25
				return new Font ("Microsoft Sans Serif", 8.25f, "DefaultFont");
			}
		}

		public static Font DialogFont  {
			get { return OperatingSystem.IsWindows () || WebGpuBackend.Gdip.GpFontFamily.Canonical ("Tahoma") != null ? new Font ("Tahoma", 8f, "DialogFont") : new Font (ShellFamily, ShellSize, "DialogFont"); }
		}

		[System.Runtime.InteropServices.StructLayout (System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
		struct LOGFONTW
		{
			public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
			public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
			[System.Runtime.InteropServices.MarshalAs (System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
			public string lfFaceName;
		}

		[System.Runtime.InteropServices.DllImport ("gdi32.dll")]
		static extern IntPtr GetStockObject (int index);

		[System.Runtime.InteropServices.DllImport ("gdi32.dll", EntryPoint = "GetObjectW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
		static extern int GetObject (IntPtr handle, int size, ref LOGFONTW font);

		/// <summary>DEFAULT_GUI_FONT's face, size in points (its height in pixels at 96 dpi, as stock
		/// converts it) and charset.</summary>
		static bool DefaultGuiFont (out string face, out float points, out byte charSet)
		{
			face = null; points = 0; charSet = 1;
			try {
				var lf = new LOGFONTW ();
				if (GetObject (GetStockObject (17 /* DEFAULT_GUI_FONT */), System.Runtime.InteropServices.Marshal.SizeOf<LOGFONTW> (), ref lf) == 0)
					return false;
				// MS Shell Dlg is a substitute name; GDI+ resolves it to Microsoft Sans Serif, as stock reports.
				face = string.Equals (lf.lfFaceName, "MS Shell Dlg", StringComparison.OrdinalIgnoreCase) ? "Microsoft Sans Serif" : lf.lfFaceName;
				points = Math.Abs (lf.lfHeight) * 72f / 96f;
				charSet = lf.lfCharSet;
				return !string.IsNullOrEmpty (face) && points > 0;
			} catch (DllNotFoundException) {
				return false;
			} catch (EntryPointNotFoundException) {
				return false;
			}
		}

		public static Font IconTitleFont  { 
			get { return new Font (ShellFamily, ShellSize, "IconTitleFont"); }
		}

		public static Font MenuFont  { 
			get { return new Font (ShellFamily, ShellSize, "MenuFont"); }
		}

		public static Font MessageBoxFont  { 
			get { return new Font (ShellFamily, ShellSize, "MessageBoxFont"); }
		}

		public static Font SmallCaptionFont  { 
			get { return new Font (ShellFamily, ShellSize, "SmallCaptionFont"); }
		}

		public static Font StatusFont  { 
			get { return new Font (ShellFamily, ShellSize, "StatusFont"); }
		}	      
	}
}

