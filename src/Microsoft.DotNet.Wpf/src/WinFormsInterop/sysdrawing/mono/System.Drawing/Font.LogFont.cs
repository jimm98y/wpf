// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET 8's typed LOGFONT (System.Drawing.Interop.LOGFONT, the face name a fixed buffer) and the Font overloads
// that take it -- the same GDI+ calls as the object overloads, so here the same code: the typed structure is
// the internal one, field for field.

using System.Runtime.InteropServices;

namespace System.Drawing.Interop
{
	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public unsafe struct LOGFONT
	{
		public int lfHeight;
		public int lfWidth;
		public int lfEscapement;
		public int lfOrientation;
		public int lfWeight;
		public byte lfItalic;
		public byte lfUnderline;
		public byte lfStrikeOut;
		public byte lfCharSet;
		public byte lfOutPrecision;
		public byte lfClipPrecision;
		public byte lfQuality;
		public byte lfPitchAndFamily;
		private fixed char _lfFaceName [32];

		[System.Diagnostics.CodeAnalysis.UnscopedRef]
		public Span<char> lfFaceName => MemoryMarshal.CreateSpan (ref _lfFaceName [0], 32);

		internal readonly string FaceName
		{
			get {
				fixed (char* p = _lfFaceName) {
					var span = new ReadOnlySpan<char> (p, 32);
					int end = span.IndexOf ('\0');
					return new string (end < 0 ? span : span [..end]);
				}
			}
		}
	}
}

namespace System.Drawing
{
	public sealed partial class Font
	{
		public static Font FromLogFont (in Interop.LOGFONT logFont) => FromLogFont (logFont, IntPtr.Zero);

		public static Font FromLogFont (in Interop.LOGFONT logFont, IntPtr hdc)
		{
			var lf = new LOGFONT {
				lfHeight = logFont.lfHeight,
				lfWidth = (uint) logFont.lfWidth,
				lfEscapement = (uint) logFont.lfEscapement,
				lfOrientation = (uint) logFont.lfOrientation,
				lfWeight = (uint) logFont.lfWeight,
				lfItalic = logFont.lfItalic,
				lfUnderline = logFont.lfUnderline,
				lfStrikeOut = logFont.lfStrikeOut,
				lfCharSet = logFont.lfCharSet,
				lfOutPrecision = logFont.lfOutPrecision,
				lfClipPrecision = logFont.lfClipPrecision,
				lfQuality = logFont.lfQuality,
				lfPitchAndFamily = logFont.lfPitchAndFamily,
				lfFaceName = logFont.FaceName,
			};
			return FromLogFont ((object) lf, hdc);
		}

		public void ToLogFont (out Interop.LOGFONT logFont)
		{
			// The screen's 96-dpi metrics, as the object overload takes them.
			using var img = new Bitmap (1, 1, Imaging.PixelFormat.Format32bppArgb);
			using var g = Graphics.FromImage (img);
			ToLogFont (out logFont, g);
		}

		public void ToLogFont (out Interop.LOGFONT logFont, Graphics graphics)
		{
			ArgumentNullException.ThrowIfNull (graphics);
			Live ();
			LOGFONT lf = BuildLogFont (graphics);
			logFont = new Interop.LOGFONT {
				lfHeight = lf.lfHeight,
				lfWidth = (int) lf.lfWidth,
				lfEscapement = (int) lf.lfEscapement,
				lfOrientation = (int) lf.lfOrientation,
				lfWeight = (int) lf.lfWeight,
				lfItalic = lf.lfItalic,
				lfUnderline = lf.lfUnderline,
				lfStrikeOut = lf.lfStrikeOut,
				// System.Drawing: GDI+'s 0 charset is the font's own
				lfCharSet = lf.lfCharSet == 0 ? _gdiCharSet : lf.lfCharSet,
				lfOutPrecision = lf.lfOutPrecision,
				lfClipPrecision = lf.lfClipPrecision,
				lfQuality = lf.lfQuality,
				lfPitchAndFamily = lf.lfPitchAndFamily,
			};
			// a GDI vertical font's face is its name with '@' before it
			string face = (_gdiVerticalFont && !(lf.lfFaceName ?? "").StartsWith ('@') ? "@" : "") + (lf.lfFaceName ?? "");
			Span<char> name = logFont.lfFaceName;
			face.AsSpan (0, Math.Min (face.Length, 31)).CopyTo (name);
		}
	}
}
