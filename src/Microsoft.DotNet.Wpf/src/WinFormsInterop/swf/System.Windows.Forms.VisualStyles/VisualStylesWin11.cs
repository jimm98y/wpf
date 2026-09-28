// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The ONE visual-styles backend, on every head: macOS, Linux, Android, iOS, WebAssembly and Windows.
//
// VisualStyleRenderer -- and CheckBoxRenderer, RadioButtonRenderer, ButtonRenderer, ComboBoxRenderer,
// ScrollBarRenderer and the rest, which are built on it -- asks for a themed part by class, part and
// state. Mono answered through uxtheme (Windows, and only through a real device context taken with
// Graphics.GetHdc) or through GTK+ (Linux). Neither works here: a Graphics records into a GPU scene
// and has no device context, so a DataGridView check-box column threw InvalidParameter out of GetHdc
// on its first paint. And two platform backends are two looks to keep in parity with Windows 11.
//
// So every part is drawn by the Windows 11 theme itself (ThemeWin11's Part* methods), in managed
// code, the same way the controls draw it. A part this does not know is left undrawn and reported
// E_NOTIMPL, which VisualStyleRenderer records in LastHResult rather than throwing.
//
using System.Drawing;
using System.Collections.Generic;

namespace System.Windows.Forms.VisualStyles
{
	internal sealed class VisualStylesWin11 : IVisualStyles
	{
		private const int S_OK = 0;
		private const int E_NOTIMPL = unchecked ((int) 0x80004001);
		private const int E_FAIL = unchecked ((int) 0x80004005);

		// A theme "handle" is an index into this list, plus one so that zero stays "no theme".
		private static readonly List<string> s_classes = new List<string> ();

		private static ThemeWin11 s_theme;

		/// <summary>The theme that draws the parts: the current one when it is Windows 11, else a
		/// private instance -- an application that chose the classic look still gets modern parts
		/// from a renderer, which is what Windows gives it with visual styles on.</summary>
		private static ThemeWin11 Theme => ThemeEngine.Current as ThemeWin11 ?? (s_theme ??= new ThemeWin11 ());

		private static string ClassOf (IntPtr hTheme)
		{
			int i = (int) hTheme - 1;
			lock (s_classes)
				return i >= 0 && i < s_classes.Count ? s_classes [i] : null;
		}

		public IntPtr UxThemeOpenThemeData (IntPtr hWnd, string classList)
		{
			if (string.IsNullOrEmpty (classList))
				return IntPtr.Zero;
			// A class list is "first;second;..."; the first is the one asked for.
			string name = classList.Split (';') [0].Trim ().ToUpperInvariant ();
			lock (s_classes) {
				int i = s_classes.IndexOf (name);
				if (i < 0) {
					s_classes.Add (name);
					i = s_classes.Count - 1;
				}
				return (IntPtr) (i + 1);
			}
		}

		public int UxThemeCloseThemeData (IntPtr hTheme) => S_OK;
		public bool UxThemeIsAppThemed () => true;
		public bool UxThemeIsThemeActive () => true;
		/// <summary>Only what this backend draws counts as defined, so a painter that asks first
		/// (ToolStripPainter with a rebar band, say) falls back to its own drawing instead of drawing
		/// nothing.</summary>
		public bool UxThemeIsThemePartDefined (IntPtr hTheme, int iPartId)
		{
			string cls = ClassOf (hTheme);
			if (cls == null)
				return false;
			if (Win11Frames.Get (cls, iPartId, 1) != null || Win11Frames.BorderFill (cls, iPartId, 1) != null)
				return true;
			switch (cls) {
			case "COMBOBOX": case "EDIT": case "SCROLLBAR": case "PROGRESS": case "HEADER": case "SPIN": case "TOOLBAR":
				return true;
			case "TREEVIEW":
				return iPartId == 2;
			}
			return false;
		}
		public bool UxThemeIsThemeBackgroundPartiallyTransparent (IntPtr hTheme, int iPartId, int iStateId)
			=> ClassOf (hTheme) == "BUTTON" && iPartId != 1;

		public int UxThemeDrawThemeBackground (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds)
			=> Draw (hTheme, dc, iPartId, iStateId, bounds, null);

		public int UxThemeDrawThemeBackground (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds, Rectangle clipRectangle)
			=> Draw (hTheme, dc, iPartId, iStateId, bounds, clipRectangle);

		public void VisualStyleRendererDrawBackgroundExcludingArea (IntPtr theme, IDeviceContext dc, int part, int state, Rectangle bounds, Rectangle excludedArea)
		{
			if (dc is not Graphics g)
				return;
			Region saved = g.Clip;
			g.ExcludeClip (excludedArea);
			Draw (theme, dc, part, state, bounds, null);
			g.Clip = saved;
		}

		private static int Draw (IntPtr hTheme, IDeviceContext dc, int part, int state, Rectangle bounds, Rectangle? clip)
		{
			if (dc is not Graphics g || bounds.Width <= 0 || bounds.Height <= 0)
				return E_FAIL;
			string cls = ClassOf (hTheme);
			if (cls == null)
				return E_FAIL;
			Region saved = null;
			if (clip is Rectangle c) {
				saved = g.Clip;
				g.IntersectClip (c);
			}
			try {
				return DrawPart (g, cls, part, state, bounds) ? S_OK : E_NOTIMPL;
			} finally {
				if (saved != null)
					g.Clip = saved;
			}
		}

		private static bool DrawPart (Graphics g, string cls, int part, int state, Rectangle r)
		{
			// An image part is its frame, blitted the way uxtheme blits it.
			if (Win11Frames.Get (cls, cls == "BUTTON" && part == 5 ? 1 : part, state) is Win11Frames.Frame frame) {
				Win11Frames.Draw (g, frame, r);
				return true;
			}
			if (Win11Frames.BorderFill (cls, part, state) is var (size, border, fill)) {
				Win11Frames.DrawBorderFill (g, (size, border, fill), r);
				return true;
			}
			ThemeWin11 t = Theme;
			switch (cls) {
			case "BUTTON":
				// PUSHBUTTON, RADIOBUTTON, CHECKBOX and USERBUTTON are frames, drawn above.
				switch (part) {
				case 4:     // GROUPBOX
					t.CPDrawBorder3D (g, r, Border3DStyle.Etched, Border3DSide.All);
					return true;
				}
				return false;
			case "COMBOBOX":
				switch (part) {
				case 1:     // DROPDOWNBUTTON: 1 normal, 2 hot, 3 pressed, 4 disabled
				case 6:     // DROPDOWNBUTTONRIGHT
				case 7:     // DROPDOWNBUTTONLEFT
					t.CPDrawComboButton (g, r, state == 4 ? ButtonState.Inactive : state == 3 ? ButtonState.Pushed : ButtonState.Normal);
					return true;
				default:    // BACKGROUND, TRANSPARENTBACKGROUND, BORDER, READONLY
					t.PartField (g, r, state != 4);
					return true;
				}
			case "EDIT":
				t.PartField (g, r, state != 4 && state != 3);
				return true;
			case "SCROLLBAR":
				switch (part) {
				case 1: {   // ARROWBTN: 1-4 up, 5-8 down, 9-12 left, 13-16 right
					ArrowDirection d = state <= 4 ? ArrowDirection.Up : state <= 8 ? ArrowDirection.Down
							 : state <= 12 ? ArrowDirection.Left : ArrowDirection.Right;
					t.PartScrollArrow (g, r, d, (state - 1) % 4 != 3);
					return true;
				}
				case 2: t.PartScrollThumb (g, r, false); return true;   // THUMBBTNHORZ
				case 3: t.PartScrollThumb (g, r, true); return true;    // THUMBBTNVERT
				default: t.PartScrollTrack (g, r); return true;          // tracks, grippers, size box
				}
			case "PROGRESS":
				t.PartProgress (g, r, part == 3 || part == 4 || part == 5 || part == 6);   // CHUNK*, FILL*
				return true;
			case "HEADER":
				t.PartHeader (g, r, state == 3);
				return true;
			case "TREEVIEW":
				if (part == 2) {    // GLYPH: 1 closed, 2 opened
					t.PartScrollArrow (g, r, state == 2 ? ArrowDirection.Down : ArrowDirection.Right, true);
					return true;
				}
				return false;
			case "SPIN":
				t.PartScrollArrow (g, r, part == 1 ? ArrowDirection.Up : part == 2 ? ArrowDirection.Down
							  : part == 3 ? ArrowDirection.Right : ArrowDirection.Left, state != 4);
				return true;
			case "TOOLBAR":
				// aero.msstyles, Toolbar: part 0 is a border fill of #F0F0F0 with no border.
				if (part == 0) {
					using (var b = new SolidBrush (Color.FromArgb (0xF0, 0xF0, 0xF0)))
						g.FillRectangle (b, r);
					return true;
				}
				// Parts 5 and 6, the separators: a 6x5 (5x6) image, sizing margins 4,1,2,2 (2,2,4,1),
				// whose one stretched row (column) is black at alpha 115 then white at alpha 77, two
				// pixels in from the leading edge -- the margins keep the two rows (columns) at each
				// end transparent.
				if (part == 5 || part == 6) {
					using (var dark = new SolidBrush (Color.FromArgb (115, 0, 0, 0)))
					using (var light = new SolidBrush (Color.FromArgb (77, 255, 255, 255))) {
						if (part == 5 && r.Width >= 4 && r.Height > 4) {
							g.FillRectangle (dark, r.X + 2, r.Y + 2, 1, r.Height - 4);
							g.FillRectangle (light, r.X + 3, r.Y + 2, 1, r.Height - 4);
						} else if (part == 6 && r.Height >= 4 && r.Width > 4) {
							g.FillRectangle (dark, r.X + 2, r.Y + 2, r.Width - 4, 1);
							g.FillRectangle (light, r.X + 2, r.Y + 3, r.Width - 4, 1);
						}
					}
					return true;
				}
				if (state == 2 || state == 3 || state == 5 || state == 6)   // hot, pressed, checked, hot-checked
					t.PartPushButton (g, r, state == 3 ? 3 : 2);
				return true;
			case "TAB":
			case "REBAR":
			case "STATUS":
			case "WINDOW":
				return false;
			}
			return false;
		}

		public int UxThemeDrawThemeEdge (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds, Edges edges, EdgeStyle style, EdgeEffects effects, out Rectangle result)
		{
			result = Rectangle.Inflate (bounds, -1, -1);
			if (dc is Graphics g)
				Theme.CPDrawBorder3D (g, bounds, Border3DStyle.Etched, Border3DSide.All);
			return S_OK;
		}

		public int UxThemeDrawThemeParentBackground (IDeviceContext dc, Rectangle bounds, Control childControl)
		{
			if (dc is Graphics g && childControl?.Parent is Control parent)
				g.FillRectangle (ThemeEngine.Current.ResPool.GetSolidBrush (parent.BackColor), bounds);
			return S_OK;
		}

		public int UxThemeDrawThemeText (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, string text, TextFormatFlags textFlags, Rectangle bounds)
		{
			bool disabled = iStateId == 4 || (ClassOf (hTheme) == "BUTTON" && iPartId != 1 && (iStateId - 1) % 4 == 3);
			TextRenderer.DrawText (dc, text, SystemFonts.MessageBoxFont, bounds, disabled ? SystemColors.GrayText : SystemColors.ControlText, textFlags);
			return S_OK;
		}

		public int UxThemeGetThemeBackgroundContentRect (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds, out Rectangle result)
		{
			result = ClassOf (hTheme) == "BUTTON" && iPartId == 1 ? Rectangle.Inflate (bounds, -3, -3) : Rectangle.Inflate (bounds, -1, -1);
			return S_OK;
		}

		public int UxThemeGetThemeBackgroundExtent (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle contentBounds, out Rectangle result)
		{
			result = Rectangle.Inflate (contentBounds, ClassOf (hTheme) == "BUTTON" && iPartId == 1 ? 3 : 1, ClassOf (hTheme) == "BUTTON" && iPartId == 1 ? 3 : 1);
			return S_OK;
		}

		public int UxThemeGetThemeBackgroundRegion (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds, out Region result)
		{
			result = new Region (bounds);
			return S_OK;
		}

		/// <summary>Part sizes as uxtheme reports them at 96 dpi: the 13x13 check and radio glyphs,
		/// the 17-pixel scroll arrow and drop-down button, the tree view's 16-pixel glyph.</summary>
		public int UxThemeGetThemePartSize (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, ThemeSizeType type, out Size result)
		{
			result = PartSize (ClassOf (hTheme), iPartId);
			return result.IsEmpty ? E_NOTIMPL : S_OK;
		}

		public int UxThemeGetThemePartSize (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, Rectangle bounds, ThemeSizeType type, out Size result)
		{
			result = PartSize (ClassOf (hTheme), iPartId);
			if (result.IsEmpty && type == ThemeSizeType.Draw)
				result = bounds.Size;
			return result.IsEmpty ? E_NOTIMPL : S_OK;
		}

		private static Size PartSize (string cls, int part)
		{
			switch (cls) {
			case "BUTTON": return part == 2 || part == 3 ? new Size (13, 13) : Size.Empty;
			case "SCROLLBAR": return part == 1 ? new Size (17, 17) : part == 10 ? new Size (17, 17) : Size.Empty;
			case "COMBOBOX": return part == 1 ? new Size (17, 20) : Size.Empty;
			case "TREEVIEW": return part == 2 ? new Size (16, 16) : Size.Empty;
			case "TRACKBAR": return part >= 3 && part <= 8 ? new Size (11, 21) : Size.Empty;
			}
			return Size.Empty;
		}

		public int UxThemeGetThemeTextExtent (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, string textToDraw, TextFormatFlags flags, Rectangle bounds, out Rectangle result)
		{
			Size s = TextRenderer.MeasureText (dc, textToDraw, SystemFonts.MessageBoxFont, bounds.Size, flags);
			result = new Rectangle (bounds.Location, s);
			return S_OK;
		}

		public int UxThemeGetThemeTextExtent (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, string textToDraw, TextFormatFlags flags, out Rectangle result)
		{
			Size s = TextRenderer.MeasureText (dc, textToDraw, SystemFonts.MessageBoxFont, Size.Empty, flags);
			result = new Rectangle (Point.Empty, s);
			return S_OK;
		}

		public int UxThemeGetThemeTextMetrics (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, out TextMetrics result)
		{
			result = new TextMetrics ();
			return E_NOTIMPL;
		}

		public int UxThemeHitTestThemeBackground (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, HitTestOptions options, Rectangle backgroundRectangle, IntPtr hrgn, Point pt, out HitTestCode result)
		{
			result = backgroundRectangle.Contains (pt) ? HitTestCode.Client : HitTestCode.Nowhere;
			return S_OK;
		}

		public int UxThemeGetThemeMargins (IntPtr hTheme, IDeviceContext dc, int iPartId, int iStateId, MarginProperty prop, out Padding result)
		{
			result = ClassOf (hTheme) == "BUTTON" && iPartId == 1 ? new Padding (3) : Padding.Empty;
			return S_OK;
		}

		public int UxThemeGetThemeBool (IntPtr hTheme, int iPartId, int iStateId, BooleanProperty prop, out bool result) { result = false; return E_NOTIMPL; }
		public int UxThemeGetThemeColor (IntPtr hTheme, int iPartId, int iStateId, ColorProperty prop, out Color result) { result = Color.Empty; return E_NOTIMPL; }
		public int UxThemeGetThemeEnumValue (IntPtr hTheme, int iPartId, int iStateId, EnumProperty prop, out int result) { result = 0; return E_NOTIMPL; }
		public int UxThemeGetThemeFilename (IntPtr hTheme, int iPartId, int iStateId, FilenameProperty prop, out string result) { result = string.Empty; return E_NOTIMPL; }
		public int UxThemeGetThemeInt (IntPtr hTheme, int iPartId, int iStateId, IntegerProperty prop, out int result) { result = 0; return E_NOTIMPL; }
		public int UxThemeGetThemePosition (IntPtr hTheme, int iPartId, int iStateId, PointProperty prop, out Point result) { result = Point.Empty; return E_NOTIMPL; }
		public int UxThemeGetThemeString (IntPtr hTheme, int iPartId, int iStateId, StringProperty prop, out string result) { result = string.Empty; return E_NOTIMPL; }

		// VisualStyleInformation: the Windows 11 look, active everywhere.
		public string VisualStyleInformationAuthor => "Microsoft";
		public string VisualStyleInformationColorScheme => "NormalColor";
		public string VisualStyleInformationCompany => "Microsoft Corporation";
		public Color VisualStyleInformationControlHighlightHot => Color.FromArgb (0, 120, 212);
		public string VisualStyleInformationCopyright => string.Empty;
		public string VisualStyleInformationDescription => "Windows 11";
		public string VisualStyleInformationDisplayName => "Aero";
		public string VisualStyleInformationFileName => "aero.msstyles";
		public bool VisualStyleInformationIsSupportedByOS => true;
		public int VisualStyleInformationMinimumColorDepth => 0;
		public string VisualStyleInformationSize => "NormalSize";
		public bool VisualStyleInformationSupportsFlatMenus => true;
		public Color VisualStyleInformationTextControlBorder => Color.FromArgb (141, 141, 141);
		public string VisualStyleInformationUrl => string.Empty;
		public string VisualStyleInformationVersion => "10.0";
	}
}
