//
// System.Drawing.StringFormat.cs
//
// Authors:
//   Dennis Hayes (dennish@Raytek.com)
//   Miguel de Icaza (miguel@ximian.com)
//   Jordi Mas i Hernandez (jordi@ximian.com)
//
// Copyright (C) 2002 Ximian, Inc (http://www.ximian.com)
// Copyright (C) 2004,2006 Novell, Inc (http://www.novell.com)
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

using System.ComponentModel;
using System.Drawing.Text;

namespace System.Drawing {

	public sealed class StringFormat : MarshalByRefObject, IDisposable, ICloneable
	{
//		private static StringFormat genericDefault;
		private IntPtr nativeStrFmt = IntPtr.Zero;
                private int language = GDIPlus.LANG_NEUTRAL;

		// Managed backing for the browser/no-libgdiplus path (GDIPlus.Initialized == false): the theme
		// and TextRenderer create StringFormats and read Alignment/LineAlignment/FormatFlags/Trimming/
		// HotkeyPrefix, all of which the GPU-raster path consumes from managed state (no native object).
		private StringAlignment _align, _lineAlign;
		private StringFormatFlags _flags;
		private StringTrimming _trimming;
		private HotkeyPrefix _hotkey;

		// GDI+ has no getter for the measurable character ranges, and the GPU-raster path in
		// Graphics.MeasureCharacterRanges has to lay the ranges out itself, so keep a managed copy.
		private CharacterRange [] _measurableRanges;

		public StringFormat() : this (0, GDIPlus.LANG_NEUTRAL)
		{
		}

		public StringFormat(StringFormatFlags options, int language)
		{
			this.language = language;
			_flags = options;
			if (GDIPlus.Initialized) {
				Status status = GDIPlus.GdipCreateStringFormat (options, language, out nativeStrFmt);
				GDIPlus.CheckStatus (status);
			}
		}
		
		internal StringFormat(IntPtr native)
		{
			nativeStrFmt = native;
		}
		
		~StringFormat ()
		{	
			Dispose (false);
		}
		
		public void Dispose ()
		{	
			Dispose (true);
			System.GC.SuppressFinalize (this);
		}

		void Dispose (bool disposing)
		{
			if (nativeStrFmt != IntPtr.Zero) {
				Status status = GDIPlus.GdipDeleteStringFormat (nativeStrFmt);
				nativeStrFmt = IntPtr.Zero;
				GDIPlus.CheckStatus (status);
			}
		}

		public StringFormat (StringFormat format)
		{
			if (format == null)
				throw new ArgumentNullException ("format");

			this.language = format.language;
			_align = format._align; _lineAlign = format._lineAlign;
			_flags = format._flags; _trimming = format._trimming; _hotkey = format._hotkey;
			_measurableRanges = (format._measurableRanges == null)
				? null : (CharacterRange []) format._measurableRanges.Clone ();
			if (GDIPlus.Initialized) {
				Status status = GDIPlus.GdipCloneStringFormat (format.NativeObject, out nativeStrFmt);
				GDIPlus.CheckStatus (status);
			}
		}

		public StringFormat (StringFormatFlags options)
		{
			_flags = options;
			if (GDIPlus.Initialized) {
				Status status = GDIPlus.GdipCreateStringFormat (options, GDIPlus.LANG_NEUTRAL, out nativeStrFmt);
				GDIPlus.CheckStatus (status);
			}
		}

		public StringAlignment Alignment {
			get {
				if (!GDIPlus.Initialized) return _align;
                                StringAlignment align;
				Status status = GDIPlus.GdipGetStringFormatAlign (nativeStrFmt, out align);
				GDIPlus.CheckStatus (status);

        			return align;
			}

			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("Alignment");

				_align = value;
				if (!GDIPlus.Initialized) return;
				Status status = GDIPlus.GdipSetStringFormatAlign (nativeStrFmt, value);
				GDIPlus.CheckStatus (status);
			}
		}

		public StringAlignment LineAlignment {
			get {
				if (!GDIPlus.Initialized) return _lineAlign;
				StringAlignment align;
				Status status = GDIPlus.GdipGetStringFormatLineAlign (nativeStrFmt, out align);
				GDIPlus.CheckStatus (status);

                                return align;
			}

			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("Alignment");

				_lineAlign = value;
				if (!GDIPlus.Initialized) return;
				Status status = GDIPlus.GdipSetStringFormatLineAlign (nativeStrFmt, value);
				GDIPlus.CheckStatus (status);
        		}
		}

		public StringFormatFlags FormatFlags {
			get {
				if (!GDIPlus.Initialized) return _flags;
				StringFormatFlags flags;
				Status status = GDIPlus.GdipGetStringFormatFlags (nativeStrFmt, out flags);
				GDIPlus.CheckStatus (status);

        			return flags;
			}

			set {
				_flags = value;
				if (!GDIPlus.Initialized) return;
				Status status = GDIPlus.GdipSetStringFormatFlags (nativeStrFmt, value);
				GDIPlus.CheckStatus (status);
			}
		}

		public HotkeyPrefix HotkeyPrefix {
			get {
				if (!GDIPlus.Initialized) return _hotkey;
				HotkeyPrefix hotkeyPrefix;
				Status status = GDIPlus.GdipGetStringFormatHotkeyPrefix (nativeStrFmt, out hotkeyPrefix);
				GDIPlus.CheckStatus (status);

               			return hotkeyPrefix;
			}

			set {
				if ((value < HotkeyPrefix.None) || (value > HotkeyPrefix.Hide))
					throw new InvalidEnumArgumentException ("HotkeyPrefix");

				_hotkey = value;
				if (!GDIPlus.Initialized) return;
				Status status = GDIPlus.GdipSetStringFormatHotkeyPrefix (nativeStrFmt, value);
				GDIPlus.CheckStatus (status);
			}
		}


		public StringTrimming Trimming {
			get {
				if (!GDIPlus.Initialized) return _trimming;
				StringTrimming trimming;
				Status status = GDIPlus.GdipGetStringFormatTrimming (nativeStrFmt, out trimming);
				GDIPlus.CheckStatus (status);
        			return trimming;
			}

			set {
				if ((value < StringTrimming.None) || (value > StringTrimming.EllipsisPath))
					throw new InvalidEnumArgumentException ("Trimming");

				_trimming = value;
				if (!GDIPlus.Initialized) return;
				Status status = GDIPlus.GdipSetStringFormatTrimming (nativeStrFmt, value);
				GDIPlus.CheckStatus (status);
			}
		}

		public static StringFormat GenericDefault {
			get {
				if (!GDIPlus.Initialized) return new StringFormat ();
				IntPtr ptr;

				Status status = GDIPlus.GdipStringFormatGetGenericDefault (out ptr);
				GDIPlus.CheckStatus (status);

				return new StringFormat (ptr);
			}
		}
		
		
		public int DigitSubstitutionLanguage {
			get{
				return language;
			}
		}

		
		public static StringFormat GenericTypographic {
			get {
				if (!GDIPlus.Initialized) return new StringFormat (StringFormatFlags.NoWrap);
				IntPtr ptr;

				Status status = GDIPlus.GdipStringFormatGetGenericTypographic (out ptr);
				GDIPlus.CheckStatus (status);

				return new StringFormat (ptr);
			}
		}

                public StringDigitSubstitute  DigitSubstitutionMethod  {
			get {
				if (!GDIPlus.Initialized) return StringDigitSubstitute.User;
                                StringDigitSubstitute substitute;

                                Status status = GDIPlus.GdipGetStringFormatDigitSubstitution(nativeStrFmt, language, out substitute);
				GDIPlus.CheckStatus (status);

                                return substitute;
			}
		}


      		public void SetMeasurableCharacterRanges (CharacterRange [] ranges)
		{
			_measurableRanges = (ranges == null) ? null : (CharacterRange []) ranges.Clone ();
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipSetStringFormatMeasurableCharacterRanges (nativeStrFmt,
				ranges.Length,	ranges);

			GDIPlus.CheckStatus (status);
		}

		/// <summary>The ranges last passed to <see cref="SetMeasurableCharacterRanges"/>, or null.</summary>
		internal CharacterRange [] MeasurableCharacterRanges {
			get { return _measurableRanges; }
		}

		internal int GetMeasurableCharacterRangeCount ()
		{
			if (!GDIPlus.Initialized)
				return (_measurableRanges == null) ? 0 : _measurableRanges.Length;
			int cnt;
			Status status = GDIPlus.GdipGetStringFormatMeasurableCharacterRangeCount (nativeStrFmt, out cnt);

			GDIPlus.CheckStatus (status);
			return cnt;
		}
			
		public object Clone()
		{
			if (!GDIPlus.Initialized) return new StringFormat (this);
			IntPtr native;

			Status status = GDIPlus.GdipCloneStringFormat (nativeStrFmt, out native);
			GDIPlus.CheckStatus (status);

			StringFormat clone = new StringFormat (native);
			clone._measurableRanges = (_measurableRanges == null)
				? null : (CharacterRange []) _measurableRanges.Clone ();
			return clone;
		}

		public override string ToString()
		{
			return "[StringFormat, FormatFlags=" + this.FormatFlags.ToString() + "]";
		}
		
		internal IntPtr NativeObject
                {            
			get{
				return nativeStrFmt;
			}
			set	{
				nativeStrFmt = value;
			}
		}

		// For CoreFX compat
		internal IntPtr nativeFormat
                {            
			get{
				return nativeStrFmt;
			}
		}

                public void SetTabStops(float firstTabOffset, float[] tabStops)
                {
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipSetStringFormatTabStops(nativeStrFmt, firstTabOffset, tabStops.Length, tabStops);
			GDIPlus.CheckStatus (status);
                }

                public void SetDigitSubstitution(int language,  StringDigitSubstitute substitute)
                {
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipSetStringFormatDigitSubstitution(nativeStrFmt, this.language, substitute);
			GDIPlus.CheckStatus (status);
                }

                public float[] GetTabStops(out float firstTabOffset)
                {
                        int count = 0;
                        firstTabOffset = 0;
                        if (!GDIPlus.Initialized) return new float[0];

                        Status status = GDIPlus.GdipGetStringFormatTabStopCount(nativeStrFmt, out count);
			GDIPlus.CheckStatus (status);

                        float[] tabStops = new float[count];                        
                        
                        if (count != 0) {                        
                        	status = GDIPlus.GdipGetStringFormatTabStops(nativeStrFmt, count, out firstTabOffset, tabStops);
				GDIPlus.CheckStatus (status);
			}
                        	
                        return tabStops;                        
                }

	}
}
