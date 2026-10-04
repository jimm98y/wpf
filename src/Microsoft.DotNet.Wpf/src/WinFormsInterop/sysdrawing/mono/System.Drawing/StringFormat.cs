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

	// A managed GpStringFormat: every property kept here (there is no GDI+ object behind it).
	public sealed class StringFormat : MarshalByRefObject, IDisposable, ICloneable
	{
		private int language = GDIPlus.LANG_NEUTRAL;

		private StringAlignment _align, _lineAlign;
		private StringFormatFlags _flags;
		private StringTrimming _trimming;
		private HotkeyPrefix _hotkey;
		private StringDigitSubstitute _digitSubstitute = StringDigitSubstitute.User;
		private int _digitLanguage = GDIPlus.LANG_NEUTRAL;
		private float _firstTabOffset;
		private float [] _tabStops = new float [0];

		// GDI+ has no getter for the measurable character ranges, and Graphics.MeasureCharacterRanges
		// lays the ranges out itself, so keep a managed copy.
		private CharacterRange [] _measurableRanges;

		public StringFormat() : this (0, GDIPlus.LANG_NEUTRAL)
		{
		}

		public StringFormat(StringFormatFlags options, int language)
		{
			this.language = language;
			_flags = options;
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
		}

		public StringFormat (StringFormat format)
		{
			if (format == null)
				throw new ArgumentNullException ("format");

			this.language = format.language;
			_align = format._align; _lineAlign = format._lineAlign;
			_flags = format._flags; _trimming = format._trimming; _hotkey = format._hotkey;
			_digitSubstitute = format._digitSubstitute; _digitLanguage = format._digitLanguage;
			_firstTabOffset = format._firstTabOffset; _tabStops = (float []) format._tabStops.Clone ();
			// AND THE TYPOGRAPHIC FLAG, which is a field rather than one of the FormatFlags and so
			// was not copied by a copy constructor. A caller writing
			// new StringFormat (StringFormat.GenericTypographic) got a format that was no longer
			// typographic, and the margin it was asking to be rid of came straight back.
			IsTypographic = format.IsTypographic;
			TabStopCount = format.TabStopCount;
			_measurableRanges = (format._measurableRanges == null)
				? null : (CharacterRange []) format._measurableRanges.Clone ();
		}

		public StringFormat (StringFormatFlags options)
		{
			_flags = options;
		}

		public StringAlignment Alignment {
			get { return _align; }
			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("Alignment");
				_align = value;
			}
		}

		public StringAlignment LineAlignment {
			get { return _lineAlign; }
			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("Alignment");
				_lineAlign = value;
			}
		}

		public StringFormatFlags FormatFlags {
			get { return _flags; }
			set { _flags = value; }
		}

		public HotkeyPrefix HotkeyPrefix {
			get { return _hotkey; }
			set {
				if ((value < HotkeyPrefix.None) || (value > HotkeyPrefix.Hide))
					throw new InvalidEnumArgumentException ("HotkeyPrefix");
				_hotkey = value;
			}
		}

		public StringTrimming Trimming {
			get { return _trimming; }
			set {
				if ((value < StringTrimming.None) || (value > StringTrimming.EllipsisPath))
					throw new InvalidEnumArgumentException ("Trimming");
				_trimming = value;
			}
		}

		public static StringFormat GenericDefault => new StringFormat ();

		public int DigitSubstitutionLanguage => _digitLanguage;

		/// <summary>Whether this is the typographic format, which asks for the text to be laid
		/// out with no margin around it -- a caller placing glyphs itself wants exactly the run
		/// it asked for, and nothing either side of it.</summary>
		internal bool IsTypographic;

		/// <summary>GDI+'s typographic format: FitBlackBox | LineLimit | NoClip (0x6004), no trimming,
		/// no margins or tracking (GpStringFormat::GenericTypographic).</summary>
		public static StringFormat GenericTypographic
			=> new StringFormat (StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip) { IsTypographic = true, Trimming = StringTrimming.None };

		public StringDigitSubstitute DigitSubstitutionMethod => _digitSubstitute;

		public void SetMeasurableCharacterRanges (CharacterRange [] ranges)
		{
			_measurableRanges = (ranges == null) ? null : (CharacterRange []) ranges.Clone ();
		}

		/// <summary>The ranges last passed to <see cref="SetMeasurableCharacterRanges"/>, or null.</summary>
		internal CharacterRange [] MeasurableCharacterRanges => _measurableRanges;

		internal int GetMeasurableCharacterRangeCount () => (_measurableRanges == null) ? 0 : _measurableRanges.Length;

		public object Clone () => new StringFormat (this);

		public override string ToString()
		{
			return "[StringFormat, FormatFlags=" + this.FormatFlags.ToString() + "]";
		}

		internal IntPtr NativeObject => IntPtr.Zero;

		// For CoreFX compat
		internal IntPtr nativeFormat => IntPtr.Zero;

		/// <summary>How many tab stops were set: a format with tab stops is one GDI+'s fast text
		/// imager refuses.</summary>
		internal int TabStopCount;

		public void SetTabStops (float firstTabOffset, float[] tabStops)
		{
			if (tabStops == null)
				throw new ArgumentNullException ("tabStops");
			_firstTabOffset = firstTabOffset;
			_tabStops = (float []) tabStops.Clone ();
			TabStopCount = tabStops.Length;
		}

		public void SetDigitSubstitution (int language, StringDigitSubstitute substitute)
		{
			_digitLanguage = language;
			_digitSubstitute = substitute;
		}

		public float[] GetTabStops (out float firstTabOffset)
		{
			firstTabOffset = _firstTabOffset;
			return (float []) _tabStops.Clone ();
		}
	}
}
