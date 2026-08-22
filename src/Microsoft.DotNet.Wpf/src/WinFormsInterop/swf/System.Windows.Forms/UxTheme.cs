// The real Windows control chrome, drawn by Windows.
//
// Every "make it look like Windows 11" change up to now has been a reconstruction: read a
// screenshot, guess the colour, guess the corner radius, draw something close with FillPath and
// DrawPath. That is a losing game -- there are dozens of parts, each with four or five states, and
// the msstyles they come from encodes gradients, insets and hairlines nobody is going to
// re-derive by eye.
//
// Windows will simply draw them for us. Visual styles are just uxtheme: OpenThemeData names a
// class ("BUTTON", "SCROLLBAR"), DrawThemeBackground paints one part in one state into a DC, and
// what comes out IS the button on the screen next door -- same msstyles, same code. This process
// is themed (IsAppThemed is true), so the only thing in the way is that our Graphics has no HDC:
// the GPU-raster path records a scene instead of rasterising.
//
// So render into a DIB of our own and carry the pixels over. The catch is that a part is drawn
// ONTO whatever the DC already holds -- the rounded corners of a Win11 button are antialiased
// against the background -- so a single render gives no way to tell "opaque white" from "white
// background showing through". Render each part twice, once over black and once over white, and
// the alpha falls out: a pixel that did not change is transparent, one that landed identically on
// both is opaque, and everything between is the blend. Straight RGBA is what the scene recorder
// wants anyway.
//
// Parts are cached by class, part, state and size, because the same button is redrawn every
// frame. A part the current msstyles does not define paints nothing, which shows up as a fully
// transparent recovery -- that is remembered too, and the caller falls back to drawing it by hand.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	internal static class UxTheme
	{
		// ---- the parts and states this stack asks for ----------------------------
		// (uxtheme's own vsstyle.h numbering; only what is used here is named)

		internal const int BP_PUSHBUTTON = 1, BP_RADIOBUTTON = 2, BP_CHECKBOX = 3, BP_GROUPBOX = 4;
		internal const int PBS_NORMAL = 1, PBS_HOT = 2, PBS_PRESSED = 3, PBS_DISABLED = 4, PBS_DEFAULTED = 5;
		// Check boxes and radio buttons share one numbering: unchecked 1-4, checked 5-8, mixed 9-12,
		// each running normal / hot / pressed / disabled.
		internal const int CBS_UNCHECKEDNORMAL = 1, CBS_CHECKEDNORMAL = 5, CBS_MIXEDNORMAL = 9;
		internal const int GBS_NORMAL = 1, GBS_DISABLED = 2;

		internal const int EP_EDITBORDER_NOSCROLL = 6;
		internal const int EPSN_NORMAL = 1, EPSN_HOT = 2, EPSN_FOCUSED = 3, EPSN_DISABLED = 4;

		internal const int CP_DROPDOWNBUTTON = 1, CP_BORDER = 4, CP_READONLY = 5, CP_DROPDOWNBUTTONRIGHT = 6;
		internal const int CBB_NORMAL = 1, CBB_HOT = 2, CBB_PRESSED = 3, CBB_DISABLED = 4;

		internal const int SBP_ARROWBTN = 1, SBP_THUMBBTNHORZ = 2, SBP_THUMBBTNVERT = 3;
		internal const int SBP_LOWERTRACKHORZ = 4, SBP_UPPERTRACKHORZ = 5;
		internal const int SBP_LOWERTRACKVERT = 6, SBP_UPPERTRACKVERT = 7;
		internal const int SBP_GRIPPERHORZ = 8, SBP_GRIPPERVERT = 9;
		internal const int ABS_UPNORMAL = 1, ABS_DOWNNORMAL = 5, ABS_LEFTNORMAL = 9, ABS_RIGHTNORMAL = 13;
		internal const int SCRBS_NORMAL = 1, SCRBS_HOT = 2, SCRBS_PRESSED = 3, SCRBS_DISABLED = 4;

		internal const int HP_HEADERITEM = 1, HP_HEADERITEMLEFT = 2, HP_HEADERITEMRIGHT = 3, HP_HEADERSORTARROW = 4;
		internal const int HIS_NORMAL = 1, HIS_HOT = 2, HIS_PRESSED = 3;

		internal const int TABP_TABITEM = 1, TABP_TABITEMLEFTEDGE = 2, TABP_TABITEMRIGHTEDGE = 3;
		internal const int TABP_TABITEMBOTHEDGE = 4, TABP_TOPTABITEM = 5, TABP_PANE = 9, TABP_BODY = 10;
		internal const int TIS_NORMAL = 1, TIS_HOT = 2, TIS_SELECTED = 3, TIS_DISABLED = 4, TIS_FOCUSED = 5;

		internal const int TMT_TEXTCOLOR = 3803, TMT_FILLCOLOR = 3802, TMT_BORDERCOLOR = 3801;
		private const int TMT_SIZINGMARGINS = 3601;

		// ---- availability --------------------------------------------------------

		// WF_UXTHEME=0 turns this off: the managed drawing underneath stays reachable, which is what
		// the non-Windows heads and the browser use anyway.
		private static readonly bool s_wanted = Environment.GetEnvironmentVariable ("WF_UXTHEME") != "0";
		private static int s_available = -1;      // -1 unknown, 0 no, 1 yes

		/// <summary>Whether Windows will draw parts for us: a themed process on Windows.</summary>
		internal static bool Available {
			get {
				if (s_available < 0) {
					s_available = 0;
					if (s_wanted && OperatingSystem.IsWindows ()) {
						try { s_available = IsThemeActive () && IsAppThemed () ? 1 : 0; }
						catch { s_available = 0; }
					}
				}
				return s_available == 1;
			}
		}

		// ---- drawing -------------------------------------------------------------

		/// <summary>Paint one themed part into <paramref name="bounds"/>. False means Windows has
		/// nothing to draw for it -- no theme, or a part this msstyles does not define -- and the
		/// caller should fall back to drawing it itself.</summary>
		internal static bool Draw (Graphics dc, string cls, int part, int state, Rectangle bounds)
		{
			return Draw (dc, cls, part, state, bounds, false);
		}

		/// <summary>As above, but <paramref name="frameOnly"/> keeps just the border the part draws
		/// and throws its fill away. A control's border has to be painted AFTER the control on this
		/// stack -- there is no non-client area to paint it in first -- and an edit border is a
		/// filled box, so drawing the whole part blanked the text that was already there.</summary>
		internal static bool Draw (Graphics dc, string cls, int part, int state, Rectangle bounds, bool frameOnly)
		{
			if (dc == null || bounds.Width <= 0 || bounds.Height <= 0 || !Available)
				return false;
			// Nothing sane comes of asking uxtheme for a part the size of a wall, and the cache
			// would grow one entry per pixel of width.
			if (bounds.Width > 4096 || bounds.Height > 4096)
				return false;

			byte[] rgba = GetPart (cls, part, state, bounds.Width, bounds.Height, frameOnly);
			if (rgba == null)
				return false;

			// The GPU-raster path takes the pixels straight; a real GDI+ surface needs a Bitmap.
			if (Drawing.WebGpuBackend.GpuRaster.DrawRgba (dc, rgba, bounds.Width, bounds.Height,
								     bounds.X, bounds.Y, bounds.Width, bounds.Height))
				return true;

			using (Bitmap bmp = ToBitmap (rgba, bounds.Width, bounds.Height))
				dc.DrawImage (bmp, bounds.X, bounds.Y);
			return true;
		}

		/// <summary>A colour the theme defines for a part -- text colours, mostly, which have no
		/// pixels to recover them from.</summary>
		internal static bool TryGetColor (string cls, int part, int state, int propId, out Color color)
		{
			color = Color.Empty;
			if (!Available)
				return false;
			IntPtr hTheme = ThemeFor (cls);
			if (hTheme == IntPtr.Zero)
				return false;
			int bgr;
			if (GetThemeColor (hTheme, part, state, propId, out bgr) != 0)
				return false;
			color = Color.FromArgb (bgr & 0xFF, (bgr >> 8) & 0xFF, (bgr >> 16) & 0xFF);   // COLORREF
			return true;
		}

		// ---- the part cache ------------------------------------------------------

		private static readonly Dictionary<string, byte[]> s_parts = new Dictionary<string, byte[]> ();
		private static readonly Dictionary<string, IntPtr> s_themes = new Dictionary<string, IntPtr> ();

		private static byte[] GetPart (string cls, int part, int state, int w, int h, bool frameOnly)
		{
			string key = cls + "/" + part + "/" + state + "/" + w + "x" + h + (frameOnly ? "/f" : "");
			lock (s_parts) {
				byte[] cached;
				if (s_parts.TryGetValue (key, out cached))
					return cached;
				// A resizing window can mint a new size every frame; a plain clear is enough to
				// keep this bounded, since the sizes actually on screen are re-rendered at once.
				if (s_parts.Count > 4096)
					s_parts.Clear ();
				byte[] made = Render (cls, part, state, w, h);
				if (made != null && frameOnly)
					made = KnockOutContent (made, cls, part, state, w, h);
				s_parts[key] = made;
				return made;
			}
		}

		private static IntPtr ThemeFor (string cls)
		{
			lock (s_themes) {
				IntPtr h;
				if (s_themes.TryGetValue (cls, out h))
					return h;
				h = IntPtr.Zero;
				try {
					// This stack is a virtual 96-DPI screen: the host scales the composed frame,
					// so parts have to come back at 96 DPI or every border would be drawn at the
					// monitor's scale and then scaled again. OpenThemeDataForDpi says so outright;
					// where it is missing, the process-wide handle is the best available.
					try { h = OpenThemeDataForDpi (IntPtr.Zero, cls, 96); }
					catch (EntryPointNotFoundException) { }
					if (h == IntPtr.Zero)
						h = OpenThemeData (IntPtr.Zero, cls);
				} catch { h = IntPtr.Zero; }
				s_themes[cls] = h;
				return h;
			}
		}

		// ---- rendering a part into pixels ----------------------------------------

		private static byte[] Render (string cls, int part, int state, int w, int h)
		{
			IntPtr hTheme = ThemeFor (cls);
			if (hTheme == IntPtr.Zero)
				return null;

			IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
			try {
				dc = CreateCompatibleDC (IntPtr.Zero);
				if (dc == IntPtr.Zero)
					return null;

				var bmi = new BITMAPINFO ();
				bmi.bmiHeader.biSize = (uint) Marshal.SizeOf (typeof (BITMAPINFOHEADER));
				bmi.bmiHeader.biWidth = w;
				bmi.bmiHeader.biHeight = -h;          // top-down, so rows match the recorder's order
				bmi.bmiHeader.biPlanes = 1;
				bmi.bmiHeader.biBitCount = 32;
				bmi.bmiHeader.biCompression = 0;      // BI_RGB

				IntPtr bits;
				dib = CreateDIBSection (dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
				if (dib == IntPtr.Zero)
					return null;
				old = SelectObject (dc, dib);

				var rect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
				int count = w * h;

				byte[] onBlack = new byte[count * 4];
				Fill (bits, count, 0x00000000);
				if (DrawThemeBackground (hTheme, dc, part, state, ref rect, IntPtr.Zero) != 0)
					return null;
				Marshal.Copy (bits, onBlack, 0, onBlack.Length);

				byte[] onWhite = new byte[count * 4];
				Fill (bits, count, 0x00FFFFFF);
				if (DrawThemeBackground (hTheme, dc, part, state, ref rect, IntPtr.Zero) != 0)
					return null;
				Marshal.Copy (bits, onWhite, 0, onWhite.Length);

				return Recover (onBlack, onWhite, count);
			} catch {
				return null;
			} finally {
				if (dc != IntPtr.Zero) {
					if (old != IntPtr.Zero) SelectObject (dc, old);
					if (dib != IntPtr.Zero) DeleteObject (dib);
					DeleteDC (dc);
				}
			}
		}

		/// <summary>Undo the two blends. A pixel drawn with straight alpha a over a ground g comes
		/// back as c*a + g*(1-a); the difference between the white and black grounds is exactly
		/// (1-a), so alpha is what is left of 255, and the colour is the black-ground result divided
		/// by it. Null when the part painted nothing at all -- an msstyles that does not define it.
		/// </summary>
		private static byte[] Recover (byte[] onBlack, byte[] onWhite, int count)
		{
			byte[] rgba = new byte[count * 4];
			bool any = false;
			for (int i = 0; i < count; i++) {
				int o = i * 4;
				// The DIB is BGRA; alpha is read from whichever channel the two grounds separate
				// most, because a channel the part left at zero says nothing.
				int ab = 255 - (onWhite[o + 0] - onBlack[o + 0]);
				int ag = 255 - (onWhite[o + 1] - onBlack[o + 1]);
				int ar = 255 - (onWhite[o + 2] - onBlack[o + 2]);
				int a = Math.Max (ab, Math.Max (ag, ar));
				if (a <= 0)
					continue;
				if (a > 255) a = 255;
				any = true;
				rgba[o + 0] = Unblend (onBlack[o + 2], a);   // R
				rgba[o + 1] = Unblend (onBlack[o + 1], a);   // G
				rgba[o + 2] = Unblend (onBlack[o + 0], a);   // B
				rgba[o + 3] = (byte) a;
			}
			return any ? rgba : null;
		}

		/// <summary>Clear the middle of a part, leaving the frame it draws around the edge.
		/// The width of that frame is the part's sizing margins -- the bands of its nine-grid that do
		/// not stretch -- which is the theme's own statement of where its border ends. The content
		/// rectangle would have been the obvious thing to ask for, but an edit border reports its
		/// whole rectangle as content, so knocking that out left nothing at all.</summary>
		private static byte[] KnockOutContent (byte[] rgba, string cls, int part, int state, int w, int h)
		{
			IntPtr hTheme = ThemeFor (cls);
			if (hTheme == IntPtr.Zero)
				return null;

			int left, top, right, bottom;
			MARGINS m;
			if (GetThemeMargins (hTheme, IntPtr.Zero, part, state, TMT_SIZINGMARGINS, IntPtr.Zero, out m) == 0
			    && (m.Left | m.Right | m.Top | m.Bottom) != 0) {
				left = m.Left; right = m.Right; top = m.Top; bottom = m.Bottom;
			} else {
				var bounds = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
				RECT content;
				if (GetThemeBackgroundContentRect (hTheme, IntPtr.Zero, part, state, ref bounds, out content) != 0)
					return null;
				left = content.Left; top = content.Top;
				right = w - content.Right; bottom = h - content.Bottom;
				if ((left | top | right | bottom) == 0)
					return null;              // all content, no frame: nothing here to overlay
			}

			int x0 = left, x1 = w - right, y0 = top, y1 = h - bottom;
			if (x0 >= x1 || y0 >= y1)
				return rgba;                  // the frame is the whole part

			bool any = false;
			for (int y = 0; y < h; y++) {
				bool insideRow = y >= y0 && y < y1;
				for (int x = 0; x < w; x++) {
					int o = (y * w + x) * 4;
					if (insideRow && x >= x0 && x < x1)
						rgba[o + 3] = 0;
					else if (rgba[o + 3] != 0)
						any = true;
				}
			}
			return any ? rgba : null;
		}

		private static byte Unblend (byte overBlack, int alpha)
		{
			int c = overBlack * 255 / alpha;
			return (byte) (c > 255 ? 255 : c);
		}

		private static unsafe void Fill (IntPtr bits, int count, int value)
		{
			uint* p = (uint*) bits;
			uint v = (uint) value;
			for (int i = 0; i < count; i++) p[i] = v;
		}

		private static Bitmap ToBitmap (byte[] rgba, int w, int h)
		{
			var bmp = new Bitmap (w, h, PixelFormat.Format32bppArgb);
			var data = bmp.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly,
						 PixelFormat.Format32bppArgb);
			try {
				byte[] bgra = new byte[data.Stride * h];
				for (int y = 0; y < h; y++)
					for (int x = 0; x < w; x++) {
						int s = (y * w + x) * 4, d = y * data.Stride + x * 4;
						bgra[d + 0] = rgba[s + 2];
						bgra[d + 1] = rgba[s + 1];
						bgra[d + 2] = rgba[s + 0];
						bgra[d + 3] = rgba[s + 3];
					}
				Marshal.Copy (bgra, 0, data.Scan0, bgra.Length);
			} finally {
				bmp.UnlockBits (data);
			}
			return bmp;
		}

		// ---- interop -------------------------------------------------------------

		[DllImport ("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr OpenThemeData (IntPtr hwnd, string classList);

		[DllImport ("uxtheme.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr OpenThemeDataForDpi (IntPtr hwnd, string classList, int dpi);

		[DllImport ("uxtheme.dll")]
		private static extern bool IsThemeActive ();

		[DllImport ("uxtheme.dll")]
		private static extern bool IsAppThemed ();

		[DllImport ("uxtheme.dll")]
		private static extern int DrawThemeBackground (IntPtr hTheme, IntPtr hdc, int partId, int stateId,
							       ref RECT rect, IntPtr clip);

		[DllImport ("uxtheme.dll")]
		private static extern int GetThemeColor (IntPtr hTheme, int partId, int stateId, int propId, out int color);

		[DllImport ("uxtheme.dll")]
		private static extern int GetThemeBackgroundContentRect (IntPtr hTheme, IntPtr hdc, int partId, int stateId,
									 ref RECT bounding, out RECT content);

		[DllImport ("uxtheme.dll")]
		private static extern int GetThemeMargins (IntPtr hTheme, IntPtr hdc, int partId, int stateId,
							   int propId, IntPtr rect, out MARGINS margins);

		[StructLayout (LayoutKind.Sequential)]
		private struct MARGINS { public int Left, Right, Top, Bottom; }

		[DllImport ("gdi32.dll")]
		private static extern IntPtr CreateCompatibleDC (IntPtr hdc);

		[DllImport ("gdi32.dll")]
		private static extern IntPtr CreateDIBSection (IntPtr hdc, ref BITMAPINFO bmi, uint usage,
							       out IntPtr bits, IntPtr section, uint offset);

		[DllImport ("gdi32.dll")]
		private static extern IntPtr SelectObject (IntPtr hdc, IntPtr obj);

		[DllImport ("gdi32.dll")]
		private static extern bool DeleteObject (IntPtr obj);

		[DllImport ("gdi32.dll")]
		private static extern bool DeleteDC (IntPtr hdc);

		[StructLayout (LayoutKind.Sequential)]
		private struct RECT { public int Left, Top, Right, Bottom; }

		[StructLayout (LayoutKind.Sequential)]
		private struct BITMAPINFOHEADER
		{
			public uint biSize;
			public int biWidth, biHeight;
			public ushort biPlanes, biBitCount;
			public uint biCompression, biSizeImage;
			public int biXPelsPerMeter, biYPelsPerMeter;
			public uint biClrUsed, biClrImportant;
		}

		[StructLayout (LayoutKind.Sequential)]
		private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int c1, c2, c3; }
	}
}
