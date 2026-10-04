//
// System.Drawing.FontFamily.cs
//
// Author:
//   Dennis Hayes (dennish@Raytek.com)
//   Alexandre Pigolkine (pigolkine@gmx.de)
//   Peter Dennis Bartok (pbartok@novell.com)
//
// Copyright (C) 2002/2004 Ximian, Inc http://www.ximian.com
// Copyright (C) 2004 - 2006 Novell, Inc (http://www.novell.com)
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

using System.Drawing.Text;
using System.Text;
using System.Runtime.InteropServices;

namespace System.Drawing {

	// A managed font family: its name as the font declares it, and its metrics read from the font
	// file (WebGpuBackend.Gdip.GpFontFamily) -- the em height, the usWin cell ascent and descent and
	// DirectWrite's line spacing, which is what GDI+ 1.1 reports.
	public sealed class FontFamily : MarshalByRefObject, IDisposable 
	{
		private string name;
		private FontCollection collection;

		// A family the font stack resolves by name without validating it (a Font's family: the
		// managed text stack substitutes for what is not installed, as Windows' font linking would).
		internal FontFamily (string managedName, bool managed)
		{
			name = string.IsNullOrEmpty (managedName) ? WebGpuBackend.Gdip.GpFontFamily.SansSerif : managedName;
		}

		internal IntPtr NativeObject => IntPtr.Zero;

		// For CoreFX compatibility
		internal IntPtr NativeFamily => IntPtr.Zero;

		public FontFamily (GenericFontFamilies genericFamily) 
		{
			switch (genericFamily) {
				case GenericFontFamilies.SansSerif:
					name = WebGpuBackend.Gdip.GpFontFamily.SansSerif;
					break;
				case GenericFontFamilies.Serif:
					name = WebGpuBackend.Gdip.GpFontFamily.Serif;
					break;
				case GenericFontFamilies.Monospace:
				default:	// Undocumented default 
					name = WebGpuBackend.Gdip.GpFontFamily.Monospace;
					break;
			}
			name = WebGpuBackend.Gdip.GpFontFamily.Canonical (name) ?? name;
		}
		
		public FontFamily (string name) : this (name, null)
		{			
		}

		// GdipCreateFontFamilyFromName: the family looked up without regard to case in the installed
		// fonts (or the given collection); FontFamilyNotFound when there is none.
		public FontFamily (string name, FontCollection fontCollection) 
		{
			if (name == null)
				throw new ArgumentException ("Parameter is not valid.");
			string canonical = null;
			if (fontCollection != null) {
				if (!fontCollection.Contains (name, out canonical))
					throw new ArgumentException (string.Format ("Font '{0}' cannot be found.", name));
				collection = fontCollection;
			} else {
				canonical = WebGpuBackend.Gdip.GpFontFamily.Canonical (name);
				if (canonical == null && !WebGpuBackend.Gdip.GpFontFamily.Resolvable (name))
					throw new ArgumentException (string.Format ("Font '{0}' cannot be found.", name));
			}
			this.name = canonical ?? name;
		}
		
		public string Name => name;

		public static FontFamily GenericMonospace => new FontFamily (GenericFontFamilies.Monospace);
		
		public static FontFamily GenericSansSerif => new FontFamily (GenericFontFamilies.SansSerif);
		
		public static FontFamily GenericSerif => new FontFamily (GenericFontFamilies.Serif);

		WebGpuBackend.Gdip.GpFontFamily.Metrics M (FontStyle style)
			=> WebGpuBackend.Gdip.GpFontFamily.Get (name, style)
			   ?? WebGpuBackend.Gdip.GpFontFamily.Get (name, FontStyle.Regular)
			   ?? new WebGpuBackend.Gdip.GpFontFamily.Metrics (2048, 1854, 434, 2355);
		
		public int GetCellAscent (FontStyle style) => M (style).Ascent;
		
		public int GetCellDescent (FontStyle style) => M (style).Descent;
		
		public int GetEmHeight (FontStyle style) => M (style).Em;
		
		public int GetLineSpacing (FontStyle style) => M (style).LineSpacing;

		// GDI+ synthesizes bold and italic from the regular face, so every style of a family that
		// can be drawn at all is available.
		public bool IsStyleAvailable (FontStyle style) => WebGpuBackend.Gdip.GpFontFamily.Get (name, FontStyle.Regular) != null;
		
		public void Dispose ()
		{
			GC.SuppressFinalize (this);
		}
		
		public override bool Equals (object obj)
		{
			FontFamily o = (obj as FontFamily);
			if (o == null)
				return false;

			return string.Equals (Name, o.Name, StringComparison.OrdinalIgnoreCase);
		}
		
		public override int GetHashCode ()
		{
			return StringComparer.OrdinalIgnoreCase.GetHashCode (Name);
		}
			
		public static FontFamily[] Families => new InstalledFontCollection ().Families;
		
		public static FontFamily[] GetFamilies (Graphics graphics)
		{
			if (graphics == null)
				throw new ArgumentNullException ("graphics");
			return new InstalledFontCollection ().Families;
		}
		
		public string GetName (int language)
		{
			return Name;
		}
		
		public override string ToString ()
		{
			return String.Concat ("[FontFamily: Name=", Name, "]");
		}
	}
}
