//
// System.Drawing.Fonts.cs
//
// Authors:
//	Alexandre Pigolkine (pigolkine@gmx.de)
//	Miguel de Icaza (miguel@ximian.com)
//	Todd Berman (tberman@sevenl.com)
//	Jordi Mas i Hernandez (jordi@ximian.com)
//	Ravindra (rkumar@novell.com)
//
// Copyright (C) 2004 Ximian, Inc. (http://www.ximian.com)
// Copyright (C) 2004, 2006 Novell, Inc (http://www.novell.com)
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

using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace System.Drawing
{
	[Serializable]
	[ComVisible (true)]
	[Editor ("System.Drawing.Design.FontEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
	[TypeConverter (typeof (FontConverter))]
	public sealed class Font : MarshalByRefObject, ISerializable, ICloneable, IDisposable
	{
		private IntPtr	fontObject = IntPtr.Zero;
		private string  systemFontName;
		private string  originalFontName;
		private float _size;
		private object olf;

		private const byte DefaultCharSet = 1;
		private static int CharSetOffset = -1;

		// WebGPU GPU-raster: build a managed-only Font (no native FontFamily, no GdipCreateFont) — the
		// last libgdiplus dependency in the paint path. Rendering uses the recorder's font and
		// measurement is managed, so the native font/family are never needed. fontObject stays Zero.
		// On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
		static readonly bool s_gpuRasterMode = Environment.GetEnvironmentVariable ("WF_GPU_RASTER") != "0"
			&& Environment.GetEnvironmentVariable ("WF_WEBGPU") != "0";

		// Skip the native font only where there is no GDI+ to make one with (the browser). GPU raster
		// alone is not a reason: the recorder path never needs it, but a Graphics WITHOUT a recorder
		// -- one over a plain Bitmap, which is how an app draws its own splash screen -- still goes to
		// GdipDrawString, and handing that a null font failed the whole call:
		//
		//   System.ArgumentException: A null reference or invalid value was found
		//   [GDI+ status: InvalidParameter]   at System.Drawing.Graphics.DrawString(...)
		//
		// Measurement stays managed either way, so text still matches what the WGSL renderer draws.
				// Always managed: there is no GDI+ font object behind any Font.
		static bool ManagedOnlyFont => true;

		private void CreateFont (string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte charSet, bool isVertical)
		{
			originalFontName = familyName;
			if (ManagedOnlyFont) {
				SetPropertiesManaged (familyName, emSize, style, unit, charSet, isVertical);
				return;   // no native font
			}
                        FontFamily family;
			// NOTE: If family name is null, empty or invalid,
			// MS creates Microsoft Sans Serif font.
			try {
				family = new FontFamily (familyName);
			}
			catch (Exception){
				family = FontFamily.GenericSansSerif;
			}

						setProperties (family, emSize, style, unit, charSet, isVertical);
		}

       		private Font (SerializationInfo info, StreamingContext context)
		{
			string		name;
			float		size;
			FontStyle	style;
			GraphicsUnit	unit;

			name = (string)info.GetValue("Name", typeof(string));
			size = (float)info.GetValue("Size", typeof(float));
			style = (FontStyle)info.GetValue("Style", typeof(FontStyle));
			unit = (GraphicsUnit)info.GetValue("Unit", typeof(GraphicsUnit));
 
			CreateFont(name, size, style, unit, DefaultCharSet, false);
		}

		void ISerializable.GetObjectData(SerializationInfo si, StreamingContext context)
		{
			si.AddValue("Name", Name);
			si.AddValue ("Size", Size);
			si.AddValue ("Style", Style);
			si.AddValue ("Unit", Unit);
		}

		~Font()
		{
			Dispose ();
		}

		public void Dispose ()
		{
						fontObject = IntPtr.Zero;
			GC.SuppressFinalize (this);
		}

		internal void SetSystemFontName (string newSystemFontName)
		{
			systemFontName = newSystemFontName;
		}

		internal void unitConversion (GraphicsUnit fromUnit, GraphicsUnit toUnit, float nSrc, out float nTrg)
		{
			float inchs = 0;
			nTrg = 0;
			
			switch (fromUnit) {
			case GraphicsUnit.Display:
				inchs = nSrc / 75f;
				break;
			case GraphicsUnit.Document:
				inchs = nSrc / 300f;
				break;
			case GraphicsUnit.Inch:
				inchs = nSrc;
				break;
			case GraphicsUnit.Millimeter:
				inchs = nSrc / 25.4f;
				break;
			case GraphicsUnit.Pixel:
			case GraphicsUnit.World:
				inchs = nSrc / Graphics.systemDpiX;
				break;
			case GraphicsUnit.Point:
				inchs = nSrc / 72f;
				break;
			default:
				throw new ArgumentException("Invalid GraphicsUnit");
			}

			switch (toUnit) {
			case GraphicsUnit.Display:
				nTrg = inchs * 75;
				break;
			case GraphicsUnit.Document:
				nTrg = inchs * 300;
				break;
			case GraphicsUnit.Inch:
				nTrg = inchs;
				break;
			case GraphicsUnit.Millimeter:
				nTrg = inchs * 25.4f;
				break;
			case GraphicsUnit.Pixel:
			case GraphicsUnit.World:
				nTrg = inchs * Graphics.systemDpiX;
				break;
			case GraphicsUnit.Point:
				nTrg = inchs * 72;
				break;
			default:
				throw new ArgumentException("Invalid GraphicsUnit");
			}
		}

		// Managed-only property init (no FontFamily): used in GPU-raster mode where there is no native
		// font/family. Mirrors setProperties minus family.Name (uses the requested name directly).
		void SetPropertiesManaged (string name, float emSize, FontStyle style, GraphicsUnit unit, byte charSet, bool isVertical)
		{
						// GDI+: a family that is not installed fails, and System.Drawing then uses Microsoft Sans
			// Serif; one that is takes the name as the font declares it.
			_name = (string.IsNullOrEmpty (name) ? null : WebGpuBackend.Gdip.GpFontFamily.Canonical (name)) ?? "Microsoft Sans Serif";
			_fontFamily = new FontFamily (_name, true);   // managed-only family (no libgdiplus)
			_size = emSize;
			_unit = unit;
			_style = style;
			_gdiCharSet = charSet;
			_gdiVerticalFont = isVertical;
			unitConversion (unit, GraphicsUnit.Point, emSize, out _sizeInPoints);
			_bold = (style & FontStyle.Bold) != 0;
			_italic = (style & FontStyle.Italic) != 0;
			_strikeout = (style & FontStyle.Strikeout) != 0;
			_underline = (style & FontStyle.Underline) != 0;
		}

		void setProperties (FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte charSet, bool isVertical)
		{
			_name = family.Name;
			_fontFamily = family;			
			_size = emSize;

			// MS throws ArgumentException, if unit is set to GraphicsUnit.Display
			_unit = unit;
			_style = style;
			_gdiCharSet = charSet;
			_gdiVerticalFont = isVertical;
			
			unitConversion (unit, GraphicsUnit.Point, emSize, out  _sizeInPoints);
						
			_bold = _italic = _strikeout = _underline = false;

                        if ((style & FontStyle.Bold) == FontStyle.Bold)
                                _bold = true;
				
                        if ((style & FontStyle.Italic) == FontStyle.Italic)
                               _italic = true;

                        if ((style & FontStyle.Strikeout) == FontStyle.Strikeout)
                                _strikeout = true;

                        if ((style & FontStyle.Underline) == FontStyle.Underline)
                                _underline = true;                  
		}

		public static Font FromHfont (IntPtr hfont)
		{
			// Sanity. Should we throw an exception?
			if (hfont == IntPtr.Zero)
				return new Font ("Arial", (float)10.0, FontStyle.Regular);
			if (!OperatingSystem.IsWindows ())
				throw new ArgumentException ("Only TrueType fonts are supported. This is not a TrueType font.");
			// An HFONT is GDI's: its LOGFONT, read back with GetObject, makes the font.
			LOGFONT lf = new LOGFONT ();
			if (GetObject (hfont, Marshal.SizeOf (typeof (LOGFONT)), ref lf) == 0)
				throw new ArgumentException ("Only TrueType fonts are supported. This is not a TrueType font.");
			return FromLogFont (lf, IntPtr.Zero);
		}

		[DllImport ("gdi32.dll", CharSet = CharSet.Auto)]
		static extern int GetObject (IntPtr h, int size, ref LOGFONT lf);

		public IntPtr ToHfont ()
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HFONT is a Windows GDI object.");
			if (olf == null) {
				olf = new LOGFONT ();
				ToLogFont(olf);
			}
			LOGFONT lf = (LOGFONT)olf;
			return GDIPlus.CreateFontIndirect (ref lf);
		}

		internal Font (IntPtr newFontObject, string familyName, FontStyle style, float size)
		{
			FontFamily fontFamily;			
			
			try {
				fontFamily = new FontFamily (familyName);
			}
			catch (Exception){
				fontFamily = FontFamily.GenericSansSerif;
			}
			
			setProperties (fontFamily, size, style, GraphicsUnit.Pixel, 0, false);
			fontObject = newFontObject;
		}

		public Font (Font prototype, FontStyle newStyle)
		{
			// no null checks, MS throws a NullReferenceException if original is null
						setProperties (prototype.FontFamily, prototype.Size, newStyle, prototype.Unit, prototype.GdiCharSet, prototype.GdiVerticalFont);
		}

		public Font (FontFamily family, float emSize,  GraphicsUnit unit)
			: this (family, emSize, FontStyle.Regular, unit, DefaultCharSet, false)
		{
		}

		public Font (string familyName, float emSize,  GraphicsUnit unit)
			: this (new FontFamily (familyName), emSize, FontStyle.Regular, unit, DefaultCharSet, false)
		{
		}

		public Font (FontFamily family, float emSize)
			: this (family, emSize, FontStyle.Regular, GraphicsUnit.Point, DefaultCharSet, false)
		{
		}

		public Font (FontFamily family, float emSize, FontStyle style)
			: this (family, emSize, style, GraphicsUnit.Point, DefaultCharSet, false)
		{
		}

		public Font (FontFamily family, float emSize, FontStyle style, GraphicsUnit unit)
			: this (family, emSize, style, unit, DefaultCharSet, false)
		{
		}

		public Font (FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
			: this (family, emSize, style, unit, gdiCharSet, false)
		{
		}

		public Font (FontFamily family, float emSize, FontStyle style,
				GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
		{
			if (family == null)
				throw new ArgumentNullException ("family");

						setProperties (family, emSize, style, unit, gdiCharSet,  gdiVerticalFont );
		}

		public Font (string familyName, float emSize)
			: this (familyName, emSize, FontStyle.Regular, GraphicsUnit.Point, DefaultCharSet, false)
		{
		}

		public Font (string familyName, float emSize, FontStyle style)
			: this (familyName, emSize, style, GraphicsUnit.Point, DefaultCharSet, false)
		{
		}

		public Font (string familyName, float emSize, FontStyle style, GraphicsUnit unit)
			: this (familyName, emSize, style, unit, DefaultCharSet, false)
		{
		}

		public Font (string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
			: this (familyName, emSize, style, unit, gdiCharSet, false)
		{
		}

		public Font (string familyName, float emSize, FontStyle style,
				GraphicsUnit unit, byte gdiCharSet, bool  gdiVerticalFont )
		{
			CreateFont (familyName, emSize, style, unit, gdiCharSet,  gdiVerticalFont );
		}
		internal Font (string familyName, float emSize, string systemName)
			: this (familyName, emSize, FontStyle.Regular, GraphicsUnit.Point, DefaultCharSet, false)
		{
			systemFontName = systemName;
		}
		public object Clone ()
		{
			return new Font (this, Style);
		}

		internal IntPtr NativeObject {            
			get {
				return fontObject;
			}
		}

		private bool _bold;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool Bold {
			get {
				return _bold;
			}
		}

		private FontFamily _fontFamily;

		[Browsable (false)]
		public FontFamily FontFamily {
			get {
				return _fontFamily;
			}
		}

		private byte _gdiCharSet;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public byte GdiCharSet {
			get {
				return _gdiCharSet;
			}
		}

		private bool _gdiVerticalFont;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool GdiVerticalFont {
			get {
				return _gdiVerticalFont;
			}
		}

		[Browsable (false)]
		public int Height {
			get {
				return (int) Math.Ceiling (GetHeight ());
			}
		}

		[Browsable(false)]
		public bool IsSystemFont {
			get {
				return !string.IsNullOrEmpty (systemFontName);
			}
		}

		private bool _italic;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool Italic {
			get {
				return _italic;
			}
		}

		private string _name;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[Editor ("System.Drawing.Design.FontNameEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
		[TypeConverter (typeof (FontConverter.FontNameConverter))]
		public string Name {
			get {
				return _name;
			}
		}
		
		public float Size {
			get {
				return _size;			
			}
		}

		private float _sizeInPoints;

		[Browsable (false)]
		public float SizeInPoints {
			get {
				return _sizeInPoints;
			}
		}

		private bool _strikeout;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool Strikeout {
			get {
				return _strikeout;
			}
		}
		
		private FontStyle _style;

		[Browsable (false)]
		public FontStyle Style {
			get {
				return _style;
			}
		}

		[Browsable(false)]
		public string SystemFontName {
			get {
				return systemFontName;
			}
		}

		[Browsable(false)]
		public string OriginalFontName {
			get {
				return originalFontName;
			}
		}
		private bool _underline;

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool Underline {
			get {
				return _underline;
			}
		}

		private GraphicsUnit _unit;

		[TypeConverter (typeof (FontConverter.FontUnitConverter))]
		public GraphicsUnit Unit {
			get {
				return _unit;
			}
		}

		public override bool Equals (object obj)
		{
			Font fnt = (obj as Font);
			if (fnt == null)
				return false;

			if (fnt.FontFamily.Equals (FontFamily) && fnt.Size == Size &&
			    fnt.Style == Style && fnt.Unit == Unit &&
			    fnt.GdiCharSet == GdiCharSet && 
			    fnt.GdiVerticalFont == GdiVerticalFont)
				return true;
			else
				return false;
		}

		private int _hashCode;

		public override int GetHashCode ()
		{
			if (_hashCode == 0) {
				_hashCode = 17;
				unchecked {
					_hashCode = _hashCode * 23 + _name.GetHashCode();
					_hashCode = _hashCode * 23 + FontFamily.GetHashCode();
					_hashCode = _hashCode * 23 + _size.GetHashCode();
					_hashCode = _hashCode * 23 + _unit.GetHashCode();
					_hashCode = _hashCode * 23 + _style.GetHashCode();
					_hashCode = _hashCode * 23 + _gdiCharSet;
					_hashCode = _hashCode * 23 + _gdiVerticalFont.GetHashCode();
				}
			}

			return _hashCode;
		}

		[MonoTODO ("The hdc parameter has no direct equivalent in libgdiplus.")]
		public static Font FromHdc (IntPtr hdc)
		{
			throw new NotImplementedException ();
		}

		// GdipCreateFontFromLogfont: the face named, the em the LOGFONT's height asks for (a negative
		// height IS the em, a positive one the cell, which the face's ascent and descent divide), in
		// points at the 96-dpi screen the stack lays out on.
		public static Font FromLogFont (object lf, IntPtr hdc)
		{
			LOGFONT o = (LOGFONT)lf;
			string face = string.IsNullOrEmpty (o.lfFaceName) ? "Microsoft Sans Serif" : o.lfFaceName;
			FontStyle style = FontStyle.Regular;
			if (o.lfWeight > 550) style |= FontStyle.Bold;
			if (o.lfItalic != 0) style |= FontStyle.Italic;
			if (o.lfUnderline != 0) style |= FontStyle.Underline;
			if (o.lfStrikeOut != 0) style |= FontStyle.Strikeout;
			float px;
			if (o.lfHeight < 0) px = -o.lfHeight;
			else if (o.lfHeight > 0) {
				var m = WebGpuBackend.Gdip.GpFontFamily.Get (face, style);
				px = m is WebGpuBackend.Gdip.GpFontFamily.Metrics fm && fm.Ascent + fm.Descent > 0
					? o.lfHeight * (float) fm.Em / (fm.Ascent + fm.Descent) : o.lfHeight;
			} else px = 12f;
			return new Font (face, px * 72f / 96f, style, GraphicsUnit.Point, o.lfCharSet, face.StartsWith ("@"));
		}

		public float GetHeight ()
		{
			return GetHeight (MetricsDpi (Graphics.systemDpiY));
		}

		/// <summary>The DPI font metrics are taken at.</summary>
		/// <remarks>
		/// This stack is a VIRTUAL 96-DPI screen: the host scales the whole composed frame to device
		/// pixels when it presents, so WinForms lays out in 96-DPI units throughout (see
		/// TextRenderer.GetDpi). Asking GDI+ at the monitor's real DPI made Font.Height 25px for an
		/// 8.25pt font instead of 13 -- and a single-line TextBox forces its height from that, so
		/// the About page's version and build boxes came out 32 tall where the code asked for 20 and
		/// covered the line beneath them.
		/// </remarks>
		static float MetricsDpi (float actual)
		{
			return s_gpuRasterMode ? 96f : actual;
		}

				public static Font FromLogFont (object lf) => FromLogFont (lf, IntPtr.Zero);

		public void ToLogFont (object logFont)
		{
						// The screen's 96-dpi metrics: a bitmap's Graphics is the same device to a LOGFONT.
			using (Bitmap img = new Bitmap (1, 1, Imaging.PixelFormat.Format32bppArgb)) {
				using (Graphics g = Graphics.FromImage (img)) {
					ToLogFont (logFont, g);
				}
			}
		}

		public void ToLogFont (object logFont, Graphics graphics)
		{
			if (graphics == null)
				throw new ArgumentNullException ("graphics");

			if (logFont == null) {
				throw new AccessViolationException ("logFont");
			}

			Type st = logFont.GetType ();
			if (!st.GetTypeInfo ().IsLayoutSequential)
				throw new ArgumentException ("logFont", Locale.GetText ("Layout must be sequential."));

			// note: there is no exception if 'logFont' isn't big enough
			Type lf = typeof (LOGFONT);
			int size = Marshal.SizeOf (logFont);
			if (size >= Marshal.SizeOf (lf)) {
								Status status = Status.Ok;
				LogFontInto (logFont, graphics);


				if (CharSetOffset == -1) {
					// not sure why this methods returns an IntPtr since it's an offset
					// anyway there's no issue in downcasting the result into an int32
					CharSetOffset = (int) Marshal.OffsetOf (lf, "lfCharSet");
				}

				// Round-trip through unmanaged memory to patch lfCharSet.
				//
				// This used to pin `logFont` and poke the byte in place, with a note that
				// Marshal.WriteByte(object, ...) was unimplemented on Mono. Pinning cannot work
				// here on .NET: LOGFONT carries lfFaceName, so the type contains references and
				// GCHandle.Alloc(..., Pinned) throws
				//     ArgumentException: Object contains references. (Parameter 'value')
				// Every TextRenderer.MeasureText call reaches this method, so the whole of WinForms
				// text measurement failed on it - MessageBox and TextBox included.
				//
				// Marshalling out, patching, and marshalling back is the same edit without pinning,
				// and it is what the backup copy above already does in the failure path.
				IntPtr patch = Marshal.AllocHGlobal (size);
				try {
					Marshal.StructureToPtr (logFont, patch, false);
					// if GDI+ lfCharSet is 0, then we return (S.D.) 1, otherwise the value is unchanged
					if (Marshal.ReadByte (patch, CharSetOffset) == 0) {
						// set lfCharSet to 1
						Marshal.WriteByte (patch, CharSetOffset, 1);
						Marshal.PtrToStructure (patch, logFont);
					}
				}
				finally {
					Marshal.DestroyStructure (patch, st);
					Marshal.FreeHGlobal (patch);
				}

								// now we can throw, if required
				GDIPlus.CheckStatus (status);
			}
		}

		// GpFont::GetLogFontW @180086a28: the em in device pixels rounded (v + 0.5 truncated) and
		// negated, the escapement from the world rotation, weight 400 or 700, the style bits, the face
		// name.
		void LogFontInto (object logFont, Graphics graphics)
		{
			float px = _sizeInPoints * 96f / 72f;
			if (_unit == GraphicsUnit.Pixel || _unit == GraphicsUnit.World) px = _size;
			float[] e = graphics.Transform.Elements;
			float sy = (float) Math.Sqrt (e[2] * e[2] + e[3] * e[3]);
			if (sy > 0) px *= sy;
			int esc = (int) (Math.Atan2 (e[1], e[0]) * 1800.0 / Math.PI);
			esc = esc == 0 ? 0 : 3600 - esc;
			var lf = new LOGFONT ();
			lf.lfHeight = -(int) (px + 0.5f);
			lf.lfEscapement = (uint) esc;
			lf.lfOrientation = (uint) esc;
			lf.lfWeight = (uint) (_bold ? 700 : 400);
			lf.lfItalic = (byte) (_italic ? 1 : 0);
			lf.lfUnderline = (byte) (_underline ? 1 : 0);
			lf.lfStrikeOut = (byte) (_strikeout ? 1 : 0);
			lf.lfCharSet = _gdiCharSet;
			lf.lfFaceName = _name;
			IntPtr mem = Marshal.AllocHGlobal (Math.Max (Marshal.SizeOf (logFont), Marshal.SizeOf (typeof (LOGFONT))));
			try {
				Marshal.StructureToPtr (lf, mem, false);
				Marshal.PtrToStructure (mem, logFont);
			} finally { Marshal.FreeHGlobal (mem); }
		}

		public float GetHeight (Graphics graphics)
		{
			if (graphics == null)
				throw new ArgumentNullException ("graphics");
			// A printer's Graphics answers in its own units from the face's line spacing.
			if (graphics.TryPrintFontHeight (this, out float printed))
				return printed;
						return GetHeight (MetricsDpi (graphics.DpiY));
		}

		public float GetHeight (float dpi)
		{
			// A managed font: GDI+'s own rule, the face's line spacing over its em, at the em in pixels
			// (Segoe UI 9pt at 96 dpi: 2724 / 2048 * 12 = 15.96). The face's metrics come from its file.
						int style = (_bold ? 1 : 0) | (_italic ? 2 : 0);
			var face = WebGpuBackend.PrintText.Face (_name, style);
			float em = _sizeInPoints * dpi / 72f;
			if (face != null) {
				WebGpuBackend.FaceMetrics fm = WebGpuBackend.FaceMetrics.Of (face);
				if (fm.UnitsPerEm > 0 && fm.LineSpacing > 0)
					return fm.LineSpacing * em / fm.UnitsPerEm;
			}
			return em * 1.16f;
		}

		public override String ToString ()
		{
			return String.Format ("[Font: Name={0}, Size={1}, Units={2}, GdiCharSet={3}, GdiVerticalFont={4}]", _name, Size, (int)_unit, _gdiCharSet, _gdiVerticalFont);
		}
	}
}
