// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// uxtheme for native callers. An application that P/Invokes uxtheme itself -- DotNetBar's themed
// parts, a control that calls DrawThemeBackground on the HDC of its paint Graphics -- reaches wpfrun's
// Win32 implementations (its P/Invokes rewritten), not this assembly. They answer through here, so a theme handle a native
// caller holds is this backend's (VisualStylesWin11) and what it draws is what WinForms draws: one
// Windows 11 look, whichever way a part is asked for.
//
// Public, so the shim calls it directly (no reflection, which the browser's AOT compiler cannot bind);
// only public types cross, and nothing of the backend's internals.

using System.Drawing;

namespace System.Windows.Forms.VisualStyles
{
	public static class UxThemeBridge
	{
		private const int TMT_TEXTCOLOR = 3803;

		private static IVisualStyles VS => VisualStylesEngine.Instance;

		/// <summary>The theme for the first class of a class list ("BUTTON;EDIT", "Explorer::TreeView")
		/// that this backend draws, else zero: what Windows answers for a class its theme does not
		/// have, so the caller draws classically rather than through a theme that draws nothing.</summary>
		public static IntPtr Open (string classList)
		{
			if (string.IsNullOrEmpty (classList))
				return IntPtr.Zero;
			foreach (string entry in classList.Split (';')) {
				string name = entry.Trim ();
				int sub = name.IndexOf ("::", StringComparison.Ordinal);
				if (sub >= 0)
					name = name.Substring (sub + 2);
				if (name.Length == 0)
					continue;
				IntPtr theme = VS.UxThemeOpenThemeData (IntPtr.Zero, name);
				for (int part = 0; part <= 32; part++)
					if (VS.UxThemeIsThemePartDefined (theme, part))
						return theme;
			}
			return IntPtr.Zero;
		}

		public static bool IsPartDefined (IntPtr theme, int part) => VS.UxThemeIsThemePartDefined (theme, part);

		public static bool IsPartiallyTransparent (IntPtr theme, int part, int state) => VS.UxThemeIsThemeBackgroundPartiallyTransparent (theme, part, state);

		public static int DrawBackground (IntPtr theme, Graphics g, int part, int state, Rectangle bounds, Rectangle clip, bool clipped)
			=> clipped ? VS.UxThemeDrawThemeBackground (theme, g, part, state, bounds, clip)
				   : VS.UxThemeDrawThemeBackground (theme, g, part, state, bounds);

		/// <summary>DrawThemeText: in the part's text colour when the theme has one, else the colour
		/// the caller gives (DTTOPTS, the DC's), else the control text colour; in the DC's font.</summary>
		public static int DrawText (IntPtr theme, Graphics g, int part, int state, string text, int flags, Rectangle bounds,
					    Font font, int argb, bool hasColor, bool grayed)
		{
			Color color;
			if (grayed)
				color = SystemColors.GrayText;
			else if (hasColor)
				color = Color.FromArgb (argb);
			else if (VS.UxThemeGetThemeColor (theme, part, state, (ColorProperty) TMT_TEXTCOLOR, out Color themed) == 0)
				color = themed;
			else
				color = SystemColors.ControlText;
			TextRenderer.DrawText (g, text, font ?? SystemFonts.MessageBoxFont, bounds, color, (TextFormatFlags) flags);
			return 0;
		}

		/// <summary>DT_CALCRECT: the text's extent within the bounds' width.</summary>
		public static Size MeasureText (Graphics g, string text, int flags, Rectangle bounds, Font font)
			=> TextRenderer.MeasureText (g, text, font ?? SystemFonts.MessageBoxFont,
						     new Size (bounds.Width > 0 ? bounds.Width : int.MaxValue, int.MaxValue), (TextFormatFlags) flags);

		public static int DrawEdge (IntPtr theme, Graphics g, int part, int state, Rectangle bounds, int edge, int flags, out Rectangle content)
			=> VS.UxThemeDrawThemeEdge (theme, g, part, state, bounds, (Edges) (flags & 0x1F), (EdgeStyle) edge, (EdgeEffects) (flags & 0xF800), out content);

		public static int DrawParentBackground (Graphics g, Rectangle bounds, Control child)
			=> VS.UxThemeDrawThemeParentBackground (g, bounds, child);

		public static int PartSize (IntPtr theme, Graphics g, int part, int state, Rectangle bounds, bool hasBounds, int type, out Size size)
			=> hasBounds ? VS.UxThemeGetThemePartSize (theme, g, part, state, bounds, (ThemeSizeType) type, out size)
				     : VS.UxThemeGetThemePartSize (theme, g, part, state, (ThemeSizeType) type, out size);

		public static int Margins (IntPtr theme, Graphics g, int part, int state, int property, out Padding margins)
			=> VS.UxThemeGetThemeMargins (theme, g, part, state, (MarginProperty) property, out margins);

		public static int ContentRect (IntPtr theme, Graphics g, int part, int state, Rectangle bounds, out Rectangle content)
			=> VS.UxThemeGetThemeBackgroundContentRect (theme, g, part, state, bounds, out content);

		public static int Extent (IntPtr theme, Graphics g, int part, int state, Rectangle content, out Rectangle extent)
			=> VS.UxThemeGetThemeBackgroundExtent (theme, g, part, state, content, out extent);

		public static int ThemeColor (IntPtr theme, int part, int state, int property, out int argb)
		{
			int hr = VS.UxThemeGetThemeColor (theme, part, state, (ColorProperty) property, out Color c);
			argb = c.ToArgb ();
			return hr;
		}
	}
}
