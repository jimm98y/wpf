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
		// GpStringFormat's state. A wrapper of one of GDI+'s two generic formats shares it with every
		// other wrapper of that format: GenericDefault and GenericTypographic hand out a new wrapper
		// each time, and what one changes the next sees.
		sealed class State
		{
			internal int language = GDIPlus.LANG_NEUTRAL;
			internal StringAlignment _align, _lineAlign;
			internal StringFormatFlags _flags;
			// GpStringFormat's defaults: Trimming Character.
			internal StringTrimming _trimming = StringTrimming.Character;
			internal HotkeyPrefix _hotkey;
			internal StringDigitSubstitute _digitSubstitute = StringDigitSubstitute.User;
			internal int _digitLanguage = GDIPlus.LANG_NEUTRAL;
			internal float _firstTabOffset;
			internal float [] _tabStops = new float [0];
			// GDI+ has no getter for the measurable character ranges, and Graphics.MeasureCharacterRanges
			// lays the ranges out itself, so keep a managed copy.
			internal CharacterRange [] _measurableRanges;
			internal bool IsTypographic;
			internal int TabStopCount;
		}

		readonly State _s = new State ();

		StringFormat (State shared)
		{
			_s = shared;
		}

		static State s_genericDefault, s_genericTypographic;

		public StringFormat() : this (0, GDIPlus.LANG_NEUTRAL)
		{
		}

		public StringFormat(StringFormatFlags options, int language)
		{
			_s.language = language;
			_s._flags = options;
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
			_disposed = true;
		}

		// GDI+'s object is gone after Dispose: every call System.Drawing makes with its handle (zero)
		// comes back InvalidParameter.
		bool _disposed;
		internal bool IsDisposed => _disposed;

		internal void Live ()
		{
			if (_disposed)
				throw new ArgumentException ("Parameter is not valid.");
		}


		public StringFormat (StringFormat format)
		{
			if (format == null)
				throw new ArgumentNullException ("format");
			format.Live ();

			_s.language = format._s.language;
			_s._align = format._s._align; _s._lineAlign = format._s._lineAlign;
			_s._flags = format._s._flags; _s._trimming = format._s._trimming; _s._hotkey = format._s._hotkey;
			_s._digitSubstitute = format._s._digitSubstitute; _s._digitLanguage = format._s._digitLanguage;
			_s._firstTabOffset = format._s._firstTabOffset; _s._tabStops = (float []) format._s._tabStops.Clone ();
			// AND THE TYPOGRAPHIC FLAG, which is a field rather than one of the FormatFlags and so
			// was not copied by a copy constructor. A caller writing
			// new StringFormat (StringFormat.GenericTypographic) got a format that was no longer
			// typographic, and the margin it was asking to be rid of came straight back.
			IsTypographic = format.IsTypographic;
			TabStopCount = format.TabStopCount;
			_s._measurableRanges = (format._s._measurableRanges == null)
				? null : (CharacterRange []) format._s._measurableRanges.Clone ();
		}

		public StringFormat (StringFormatFlags options)
		{
			_s._flags = options;
		}

		public StringAlignment Alignment {
			get { Live (); return _s._align; }
			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("value", (int) value, typeof (StringAlignment));
				Live ();
				_s._align = value;
			}
		}

		public StringAlignment LineAlignment {
			get { Live (); return _s._lineAlign; }
			set {
				if ((value < StringAlignment.Near) || (value > StringAlignment.Far))
					throw new InvalidEnumArgumentException ("value", (int) value, typeof (StringAlignment));
				Live ();
				_s._lineAlign = value;
			}
		}

		public StringFormatFlags FormatFlags {
			get { Live (); return _s._flags; }
			set { Live (); _s._flags = value; }
		}

		public HotkeyPrefix HotkeyPrefix {
			get { Live (); return _s._hotkey; }
			set {
				if ((value < HotkeyPrefix.None) || (value > HotkeyPrefix.Hide))
					throw new InvalidEnumArgumentException ("value", (int) value, typeof (HotkeyPrefix));
				Live ();
				_s._hotkey = value;
			}
		}

		public StringTrimming Trimming {
			get { Live (); return _s._trimming; }
			set {
				if ((value < StringTrimming.None) || (value > StringTrimming.EllipsisPath))
					throw new InvalidEnumArgumentException ("value", (int) value, typeof (StringTrimming));
				Live ();
				_s._trimming = value;
			}
		}

		public static StringFormat GenericDefault => new StringFormat (s_genericDefault ??= new StringFormat ()._s);

		public int DigitSubstitutionLanguage
		{
			get
			{
				Live ();
				return _s._digitLanguage;
			}
		}

		/// <summary>Whether this is the typographic format, which asks for the text to be laid
		/// out with no margin around it -- a caller placing glyphs itself wants exactly the run
		/// it asked for, and nothing either side of it.</summary>
		internal bool IsTypographic { get => _s.IsTypographic; set => _s.IsTypographic = value; }

		/// <summary>GDI+'s typographic format: FitBlackBox | LineLimit | NoClip (0x6004), no trimming,
		/// no margins or tracking (GpStringFormat::GenericTypographic).</summary>
		public static StringFormat GenericTypographic
			=> new StringFormat (s_genericTypographic ??= new StringFormat (StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip) { IsTypographic = true, Trimming = StringTrimming.None }._s);

		public StringDigitSubstitute DigitSubstitutionMethod
		{
			get
			{
				Live ();
				return _s._digitSubstitute;
			}
		}

		public void SetMeasurableCharacterRanges (CharacterRange [] ranges)
		{
			if (ranges == null)
				throw new ArgumentNullException ("ranges");
			Live ();
			// GdipSetStringFormatMeasurableCharacterRanges: no more than 32 (ValueOverflow).
			if (ranges.Length > 32)
				throw new OverflowException ("Overflow error.");
			_s._measurableRanges = (ranges == null) ? null : (CharacterRange []) ranges.Clone ();
		}

		/// <summary>The ranges last passed to <see cref="SetMeasurableCharacterRanges"/>, or null.</summary>
		internal CharacterRange [] MeasurableCharacterRanges => _s._measurableRanges;

		internal int GetMeasurableCharacterRangeCount () => (_s._measurableRanges == null) ? 0 : _s._measurableRanges.Length;

		public object Clone ()
		{
			Live ();
			return new StringFormat (this);
		}

		public override string ToString()
		{
			Live ();
			return "[StringFormat, FormatFlags=" + this.FormatFlags.ToString() + "]";
		}

		internal IntPtr NativeObject => IntPtr.Zero;

		// For CoreFX compat
		internal IntPtr nativeFormat => IntPtr.Zero;

		/// <summary>How many tab stops were set: a format with tab stops is one GDI+'s fast text
		/// imager refuses.</summary>
		internal int TabStopCount { get => _s.TabStopCount; set => _s.TabStopCount = value; }

		public void SetTabStops (float firstTabOffset, float[] tabStops)
		{
			if (firstTabOffset < 0)
				throw new ArgumentException (SR.Format (SR.InvalidArgument, "firstTabOffset", firstTabOffset));
			if (tabStops == null)
				throw new ArgumentNullException ("tabStops");
			Live ();
			// GpStringFormat::SetTabStops refuses a negative stop (NotImplemented).
			foreach (float t in tabStops)
				if (t < 0)
					throw new NotImplementedException ("Not implemented.");
			_s._firstTabOffset = firstTabOffset;
			_s._tabStops = (float []) tabStops.Clone ();
			TabStopCount = tabStops.Length;
		}

		public void SetDigitSubstitution (int language, StringDigitSubstitute substitute)
		{
			Live ();
			_s._digitLanguage = language;
			_s._digitSubstitute = substitute;
		}

		public float[] GetTabStops (out float firstTabOffset)
		{
			Live ();
			firstTabOffset = _s._firstTabOffset;
			return (float []) _s._tabStops.Clone ();
		}
	}
}
