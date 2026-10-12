//
// System.Drawing.Image.cs
//
// Authors: 	Christian Meyer (Christian.Meyer@cs.tum.edu)
// 		Alexandre Pigolkine (pigolkine@gmx.de)
//		Jordi Mas i Hernandez (jordi@ximian.com)
//		Sanjay Gupta (gsanjay@novell.com)
//		Ravindra (rkumar@novell.com)
//		Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004, 2007 Novell, Inc (http://www.novell.com)
// Copyright (C) 2013 Kristof Ralovich, changes are available under the terms of the MIT X11 license
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
//
// THE IMAGE LAYER IS MANAGED. A Bitmap (and an Icon's pictures) never has a GDI+ object behind it,
// on any platform, Windows included: its pixels, palette, frames and properties are a GdipImageData
// (backend/GdipImage.cs), read and written by the codecs WPF uses (shared source, through
// backend/GdipCodecs.cs, which gives them GDI+'s semantics) and converted with GDI+'s own arithmetic
// (backend/GdipPixels.cs). A Metafile is managed too: GDI+'s GpMetafile ported (backend/gdip,
// mono/System.Drawing.Imaging/Metafile.cs), and every member below answers for it what GDI+'s
// GpMetafile answers.
//

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace System.Drawing
{
[Serializable]
[ComVisible (true)]
[Editor ("System.Drawing.Design.ImageEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
[TypeConverter (typeof(ImageConverter))]
[ImmutableObject (true)]
public abstract class Image : MarshalByRefObject, IDisposable , ICloneable, ISerializable
{
	public delegate bool GetThumbnailImageAbort();
	private object tag;

	// No image has a GDI+ object any more; Graphics.cs at HEAD still names the handle, which is zero.
	internal IntPtr nativeObject = IntPtr.Zero;
	// The pixels and everything else a managed image holds (null for a Metafile).
	internal GdipImageData managed;
	// The dimensions of an image that is neither (a print preview's page, which is a scene).
	internal int managedWidth = -1, managedHeight = -1;
	internal float managedDpiX = 96f, managedDpiY = 96f;


	// constructor
	internal  Image()
	{
	}

	internal Image (SerializationInfo info, StreamingContext context)
	{
		foreach (SerializationEntry serEnum in info) {
			if (String.Compare(serEnum.Name, "Data", true) == 0) {
				byte[] bytes = (byte[]) serEnum.Value;
				if (bytes != null)
					managed = GdipCodecs.Decode (bytes);
			}
		}
	}

	void ISerializable.GetObjectData (SerializationInfo si, StreamingContext context)
	{
		using (MemoryStream ms = new MemoryStream ()) {
			// Icon is a decoder-only codec
			if (RawFormat.Equals (ImageFormat.Icon)) {
				Save (ms, ImageFormat.Png);
			} else {
				SaveWithRawFormat (ms);
			}
			si.AddValue ("Data", ms.ToArray ());
		}
	}

	/// <summary>The pixels as they stand, with any drawing still recorded for them rendered in.</summary>
	internal GdipImageData Data {
		get {
			if (managed == null)
				throw new ArgumentException ("Parameter is not valid.");
			if (this is Bitmap b)
				b.FlushDrawing ();
			return managed;
		}
	}

	// ---- loading ---------------------------------------------------------------------------------

	internal static byte[] ReadAll (Stream stream)
	{
		if (stream is MemoryStream ms && ms.Position == 0 && ms.TryGetBuffer (out ArraySegment<byte> seg) && seg.Offset == 0)
			return seg.Count == seg.Array.Length ? seg.Array : ms.ToArray ();
		using (var copy = new MemoryStream ()) {
			stream.CopyTo (copy);
			return copy.ToArray ();
		}
	}

	// An EMF ("ENHMETAFILE" record, " EMF" at 40) or a WMF (placeable, or a bare header).
	internal static bool IsMetafile (byte[] d) =>
		(d.Length > 44 && d [0] == 1 && d [1] == 0 && d [2] == 0 && d [3] == 0 && d [40] == 0x20 && d [41] == 0x45 && d [42] == 0x4d && d [43] == 0x46)
		|| (d.Length > 4 && d [0] == 0xd7 && d [1] == 0xcd && d [2] == 0xc6 && d [3] == 0x9a)
		|| (d.Length > 18 && (d [0] == 1 || d [0] == 2) && d [1] == 0 && d [2] == 9 && d [3] == 0);

	internal static Image FromBytes (byte[] data)
	{
		if (!GdipCodecs.CanDecode (data) && IsMetafile (data))
			return new Metafile (new MemoryStream (data));
		return new Bitmap (GdipCodecs.Decode (data));
	}

	// public methods
	// static
	public static Image FromFile(string filename)
	{
		return FromFile (filename, false);
	}

	public static Image FromFile(string filename, bool useEmbeddedColorManagement)
	{
		if (filename == null)
			throw new ArgumentNullException ("path");
		if (!File.Exists (filename))
			throw new FileNotFoundException (filename);
		byte[] data = File.ReadAllBytes (filename);
		try {
			return FromBytes (data);
		} catch (ArgumentException) {
			// GDI+ reports a file it cannot read as OutOfMemory, and System.Drawing passes that on.
			throw SafeNativeMethods.Gdip.OutOfMemoryException ();
		}
	}

	public static Bitmap FromHbitmap(IntPtr hbitmap)
	{
		return FromHbitmap (hbitmap, IntPtr.Zero);
	}

	public static Bitmap FromHbitmap(IntPtr hbitmap, IntPtr hpalette)
	{
		return new Bitmap (WebGpuBackend.WindowsImaging.FromHbitmap (hbitmap, hpalette));
	}

	// note: FromStream can return either a Bitmap or Metafile instance

	public static Image FromStream (Stream stream)
	{
		return LoadFromStream (stream, false);
	}

	public static Image FromStream (Stream stream, bool useEmbeddedColorManagement)
	{
		return LoadFromStream (stream, false);
	}

	// See http://support.microsoft.com/default.aspx?scid=kb;en-us;831419 for performance discussion
	public static Image FromStream (Stream stream, bool useEmbeddedColorManagement, bool validateImageData)
	{
		return LoadFromStream (stream, false);
	}

	internal static Image LoadFromStream (Stream stream, bool keepAlive)
	{
		if (stream == null)
			throw new ArgumentNullException ("stream");
		return FromBytes (ReadAll (stream));
	}

	public static int GetPixelFormatSize(PixelFormat pixfmt)
	{
		return ((int) pixfmt >> 8) & 0xff;
	}

	public static bool IsAlphaPixelFormat(PixelFormat pixfmt)
	{
		return (pixfmt & PixelFormat.Alpha) != 0;
	}

	public static bool IsCanonicalPixelFormat (PixelFormat pixfmt)
	{
		return ((pixfmt & PixelFormat.Canonical) != 0);
	}

	public static bool IsExtendedPixelFormat (PixelFormat pixfmt)
	{
		return ((pixfmt & PixelFormat.Extended) != 0);
	}

	// non-static
	public RectangleF GetBounds (ref GraphicsUnit pageUnit)
	{
		Live ();
		if (this is Metafile mf)
			return mf.MetafileBounds (ref pageUnit);
		pageUnit = GraphicsUnit.Pixel;
		return new RectangleF (0, 0, Width, Height);
	}

	public EncoderParameters GetEncoderParameterList(Guid encoder)
	{
		Live ();
		// GpMetafile::GetEncoderParameterList asks a 1x1 32bpp bitmap.
		if (this is Metafile mf)
			mf.CheckPlayable ();
		return ManagedEncoderParameters (encoder);
	}

	// What each managed encoder accepts: JPEG its quality, TIFF its compression, colour depth and
	// multi-frame save flag; BMP none (GDI+ reports NotImplemented for it); PNG and GIF nothing.
	static EncoderParameters ManagedEncoderParameters (Guid encoder)
	{
		ImageCodecInfo codec = Array.Find (GdipCodecs.Encoders (), c => c.Clsid == encoder);
		if (codec == null)
			throw new ArgumentException ("Parameter is not valid.");
		if (codec.FormatID == ImageFormat.Bmp.Guid)
			throw new NotImplementedException ();
		if (codec.FormatID == ImageFormat.Jpeg.Guid) {
			var ps = new EncoderParameters (1);
			ps.Param [0] = new EncoderParameter (Imaging.Encoder.Quality, 0L, 100L);
			return ps;
		}
		if (codec.FormatID == ImageFormat.Tiff.Guid) {
			var ps = new EncoderParameters (3);
			ps.Param [0] = new EncoderParameter (Imaging.Encoder.Compression, new long [] { (long) EncoderValue.CompressionNone });
			ps.Param [1] = new EncoderParameter (Imaging.Encoder.ColorDepth, new long [] { 24, 32 });
			ps.Param [2] = new EncoderParameter (Imaging.Encoder.SaveFlag, new long [] { (long) EncoderValue.MultiFrame });
			return ps;
		}
		return new EncoderParameters (0);
	}

	// A bitmap made in memory (not decoded from a file): GDI+ has no decoder behind it, and answers
	// one frame of any dimension and selects any frame.
	bool IsMemoryBitmap => managed != null && managed.Frames == null && managed.RawFormat == ImageFormat.MemoryBmp.Guid;

	public int GetFrameCount (FrameDimension dimension)
	{
		// System.Drawing reads dimension.Guid first.
		if (dimension == null)
			throw new NullReferenceException ();
		Live ();
		if (managed != null) {
			if (IsMemoryBitmap)
				return 1;
			return dimension.Guid == managed.FrameDimension ? managed.FrameCount : 0;
		}
		// GpMetafile::GetFrameCount: one, whatever the dimension.
		if (dimension == null)
			throw new NullReferenceException ();
		((Metafile) this).CheckPlayable ();
		return 1;
	}

	static PropertyItem CopyProperty (PropertyItem p)
		=> GdipCodecs.Property (p.Id, p.Type, p.Len, p.Value == null ? null : (byte[]) p.Value.Clone ());

	public PropertyItem GetPropertyItem(int propid)
	{
		Live ();
		if (managed != null) {
			foreach (PropertyItem p in managed.Properties)
				if (p.Id == propid) return CopyProperty (p);
			throw new ArgumentException ("Property cannot be found.");
		}
		// A metafile has no property items: GDI+'s GpMetafile answers NotImplemented.
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	public Image GetThumbnailImage (int thumbWidth, int thumbHeight, Image.GetThumbnailImageAbort callback, IntPtr callbackData)
	{
		Live ();
		// GpMetafile::GetThumbnail: the metafile drawn into a bitmap of that size, its own size
		// when either is not positive.
		if (this is Metafile mf) {
			if (thumbWidth <= 0 || thumbHeight <= 0) {
				thumbWidth = Width;
				thumbHeight = Height;
			}
			return mf.ToBitmap (thumbWidth, thumbHeight);
		}
		// GpBitmap::GetThumbnail: no size at all is GDI+'s default thumbnail, 120 x 120; one side
		// without is OutOfMemory, and so is a bitmap whose bits are locked.
		if (thumbWidth == 0 && thumbHeight == 0)
			thumbWidth = thumbHeight = 120;
		if ((thumbWidth <= 0) || (thumbHeight <= 0) || (this is Bitmap lb && lb.IsLocked))
			throw SafeNativeMethods.Gdip.OutOfMemoryException ();

		// GDI+ hands a thumbnail back premultiplied, the image drawn scaled into it.
		Bitmap thumbnail = new Bitmap (thumbWidth, thumbHeight, PixelFormat.Format32bppPArgb);
		using (Graphics g = Graphics.FromImage (thumbnail))
			g.DrawImage (this, 0, 0, thumbWidth, thumbHeight);
		return thumbnail;
	}


	public void RemovePropertyItem (int propid)
	{
		Live ();
		if (managed != null) {
			int i = managed.Properties.FindIndex (p => p.Id == propid);
			// GdipRemovePropertyItem of an id there is not: GenericError.
			if (i < 0)
				throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
			managed.Properties.RemoveAt (i);
			return;
		}
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	public void RotateFlip (RotateFlipType rotateFlipType)
	{
		Live ();
		// GdipImageRotateFlip: a type past Rotate270FlipX (7) is InvalidParameter.
		if ((uint) rotateFlipType > 7)
			throw new ArgumentException ("Parameter is not valid.");
		if (managed != null) {
			GdipImageData d = Data;
			d.Frame = WebGpuBackend.GdipTransform.RotateFlip (d.Frame, rotateFlipType);
			// CopyOnWriteBitmap::RotateFlip @1800835b8 releases the source stream and file name.
			d.SourceBytes = null;
			return;
		}
		// GpMetafile::RotateFlip is NotImplemented.
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	internal ImageCodecInfo findEncoderForFormat (ImageFormat format, bool memoryBmpAsPng = true)
	{
		ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
		ImageCodecInfo encoder = null;

		if (memoryBmpAsPng && format.Guid.Equals (ImageFormat.MemoryBmp.Guid))
			format = ImageFormat.Png;

		/* Look for the right encoder for our format*/
		for (int i = 0; i < encoders.Length; i++) {
			if (encoders[i].FormatID.Equals (format.Guid)) {
				encoder = encoders[i];
				break;
			}
		}

		return encoder;
	}

	public void Save (string filename)
	{
		Live ();
		Save (filename, RawFormat);
	}

	public void Save(string filename, ImageFormat format)
	{
		if (filename == null)
			throw new ArgumentNullException ("filename");
		if (format == null)
			throw new ArgumentNullException ("format");
		Live ();
		// No encoder for the format (an icon, a metafile): GDI+ writes a PNG.
		ImageCodecInfo encoder = findEncoderForFormat (format) ?? findEncoderForFormat (ImageFormat.Png);
		Save (filename, encoder, null);
	}

	public void Save(string filename, ImageCodecInfo encoder, EncoderParameters encoderParams)
	{
		// System.Drawing reads encoder.Clsid before anything else.
		if (encoder == null)
			throw new NullReferenceException ();
		if (filename == null)
			throw new ArgumentNullException ("filename");
		// The directory must be there (System.Drawing's own check) ...
		string dir = Path.GetDirectoryName (filename);
		if (!string.IsNullOrEmpty (dir) && !Directory.Exists (dir))
			throw new DirectoryNotFoundException (SR.Format (SR.TargetDirectoryDoesNotExist, dir, filename));
		CheckParams (encoderParams);
		Live ();
		if (managed != null) {
			// ... and a file GDI+ cannot create is GenericError.
			FileStream fs;
			try {
				fs = File.Create (filename);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) {
				throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
			}
			using (fs)
				SaveManaged (fs, encoder, encoderParams);
			return;
		}
		// GpMetafile::SaveToFile: there is no metafile encoder; GDI+ renders the metafile into a
		// bitmap of its size (GetBitmap) and saves that with the encoder asked for.
		if (((Metafile) this).IsRecording)
			throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
		using (Bitmap b = ((Metafile) this).ToBitmap (0, 0))
			b.Save (filename, encoder, encoderParams);
	}

	public void Save (Stream stream, ImageFormat format)
	{
		if (format == null)
			throw new ArgumentNullException ("format");
		// To a stream there is no PNG fallback: a format with no encoder (MemoryBmp, an icon, a
		// metafile) is no encoder.
		ImageCodecInfo encoder = findEncoderForFormat (format, false);
		if (stream == null)
			throw new ArgumentNullException ("stream");
		if (encoder == null)
			throw new ArgumentNullException ("encoder");
		Save (stream, encoder, null);
	}

	/// <summary>System.Drawing's internal Image.Save(Stream): the image's own format's encoder, or
	/// PNG (serialization, ImageConverter).</summary>
	internal void SaveWithRawFormat (Stream stream)
	{
		ImageCodecInfo encoder = findEncoderForFormat (RawFormat, false) ?? findEncoderForFormat (ImageFormat.Png, false);
		Save (stream, encoder, null);
	}

	// EncoderParameters System.Drawing marshals: a disposed set has no parameters to marshal.
	static void CheckParams (EncoderParameters ps)
	{
		if (ps == null)
			return;
		// System.Drawing marshals every parameter: a disposed set, or a null slot, is a
		// NullReferenceException.
		if (ps.Param == null)
			throw new NullReferenceException ();
		foreach (EncoderParameter p in ps.Param)
			if (p == null)
				throw new NullReferenceException ();
	}

	public void Save(Stream stream, ImageCodecInfo encoder, EncoderParameters encoderParams)
	{
		if (stream == null)
			throw new ArgumentNullException ("stream");
		if (encoder == null)
			throw new ArgumentNullException ("encoder");
		CheckParams (encoderParams);
		Live ();
		if (managed != null) {
			SaveManaged (stream, encoder, encoderParams);
			return;
		}
		// GpMetafile::SaveToStream, as SaveToFile; a metafile still being recorded is GenericError.
		if (((Metafile) this).IsRecording)
			throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
		using (Bitmap b = ((Metafile) this).ToBitmap (0, 0))
			b.Save (stream, encoder, encoderParams);
	}

	// ---- saving, managed: one frame, or a multi-frame TIFF built up by SaveAdd -----------------------

	// A multi-frame save in progress (Save with SaveFlag MultiFrame, then SaveAdd... Flush): the
	// pages so far and where the file starts. Each SaveAdd rewrites the file from its start, so it
	// is complete whenever the caller stops; Flush ends the session.
	Stream multi_stream;
	long multi_start;
	Guid multi_encoder;
	List<GdipFrame> multi_pages;

	static long? SaveFlag (EncoderParameters ps)
	{
		if (ps?.Param == null) return null;
		foreach (EncoderParameter p in ps.Param)
			if (p != null && p.Encoder.Guid == Imaging.Encoder.SaveFlag.Guid && p.NumberOfValues > 0) return p.FirstValue;
		return null;
	}

	void SaveManaged (Stream stream, ImageCodecInfo encoder, EncoderParameters ps)
	{
		GdipFrame frame = Data.Frame;
		if (SaveFlag (ps) == (long) EncoderValue.MultiFrame && encoder.FormatID == ImageFormat.Tiff.Guid) {
			multi_stream = stream;
			multi_start = stream.CanSeek ? stream.Position : -1;
			multi_encoder = encoder.Clsid;
			multi_pages = new List<GdipFrame> { frame.Clone () };
			WriteMulti (false);
			return;
		}
		// GDI+ writes through an IStream over the stream and does not report the stream's own
		// failures: a stream that cannot be written (read-only, closed, too small) is left as it is
		// and the save succeeds.
		var buffer = new MemoryStream ();
		GdipCodecs.Encode (frame, encoder.Clsid, ps, buffer);
		try {
			buffer.Position = 0;
			buffer.CopyTo (stream);
		} catch (Exception e) when (e is IOException || e is NotSupportedException || e is ObjectDisposedException) {
		}
	}

	void WriteMulti (bool final)
	{
		if (multi_stream == null) return;
		if (multi_start < 0 && !final) return;   // a stream that cannot seek is written once, at Flush
		if (multi_start >= 0) multi_stream.Position = multi_start;
		var extra = multi_pages.GetRange (1, multi_pages.Count - 1);
		GdipCodecs.Encode (multi_pages [0], multi_encoder, null, multi_stream, extra);
		if (multi_start >= 0) multi_stream.SetLength (multi_stream.Position);
		multi_stream.Flush ();
	}

	public void SaveAdd (EncoderParameters encoderParams)
	{
		if (encoderParams == null)
			throw new ArgumentException ("Parameter is not valid.");
		CheckParams (encoderParams);
		Live ();
		if (managed != null) {
			// GdipSaveAdd with no multi-frame save begun (or a locked bitmap): GenericError.
			if (multi_stream == null || (this is Bitmap lb && lb.IsLocked))
				throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
			long? flag = SaveFlag (encoderParams);
			if (flag == (long) EncoderValue.Flush) {
				WriteMulti (true);
				multi_stream = null; multi_pages = null;
				return;
			}
			multi_pages.Add (Data.Frame.Clone ());
			WriteMulti (false);
			return;
		}
		// GpMetafile::SaveAdd is NotImplemented.
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	public void SaveAdd (Image image, EncoderParameters encoderParams)
	{
		if (image == null)
			throw new ArgumentNullException ("image");
		CheckParams (encoderParams);
		Live ();
		// GdipSaveAddImage of no parameters or a disposed image: InvalidParameter.
		if (encoderParams == null || image.disposed)
			throw new ArgumentException ("Parameter is not valid.");
		if (managed != null) {
			if (multi_stream == null || (this is Bitmap lb && lb.IsLocked))
				throw new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
			multi_pages.Add (image.Data.Frame.Clone ());
			WriteMulti (false);
			return;
		}
		// GpMetafile::SaveAdd is NotImplemented.
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	public int SelectActiveFrame(FrameDimension dimension, int frameIndex)
	{
		// System.Drawing reads dimension.Guid first, and always answers 0.
		if (dimension == null)
			throw new NullReferenceException ();
		Live ();
		if (managed != null) {
			if (IsMemoryBitmap)
				return 0;
			GdipImageData d = Data;
			if (dimension.Guid != d.FrameDimension || frameIndex < 0 || frameIndex >= d.FrameCount)
				throw new ArgumentException ("Parameter is not valid.");
			if (d.Frames != null && frameIndex != d.ActiveFrame) {
				d.Frame = d.Frames [frameIndex].Clone ();
				if (d.FrameProperties != null) d.Properties = d.FrameProperties [frameIndex];
				d.ActiveFrame = frameIndex;
			}
			return 0;
		}
		// GpMetafile::SelectActiveFrame accepts any frame of any dimension; System.Drawing then
		// answers 0.
		if (dimension == null)
			throw new NullReferenceException ();
		((Metafile) this).CheckPlayable ();
		return 0;
	}

	public void SetPropertyItem(PropertyItem propitem)
	{
		// System.Drawing copies the item's fields into native memory before any check.
		if (propitem == null)
			throw new NullReferenceException ();
		Live ();
		if (managed != null) {
			int i = managed.Properties.FindIndex (p => p.Id == propitem.Id);
			PropertyItem copy = CopyProperty (propitem);
			if (i >= 0) managed.Properties [i] = copy;
			else managed.Properties.Add (copy);
			return;
		}

		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}

	// properties
	[Browsable (false)]
	public int Flags {
		get {
			Live ();
			if (managed != null) return managed.Flags;
			if (this is Metafile mf) return mf.MetafileFlags;
			return 0;
		}
	}

	[Browsable (false)]
	public Guid[] FrameDimensionsList {
		get {
			Live ();
			if (managed != null) return new [] { managed.FrameDimension };
			// GpMetafile::GetFrameDimensionsList: one dimension, Page.
			if (this is Metafile mf) mf.CheckPlayable ();
			return new [] { FrameDimension.Page.Guid };
		}
	}

	[DefaultValue (false)]
	[Browsable (false)]
	[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
	public int Height {
		get {
			Live ();
			if (managed != null) return managed.Frame.Height;
			if (this is Metafile mf) return mf.MetafileHeight;
			return managedHeight < 0 ? 0 : managedHeight;
		}
	}

	public float HorizontalResolution {
		get {
			Live ();
			if (managed != null) return managed.Frame.DpiX;
			if (this is Metafile mf) return mf.MetafileDpiX;
			return managedDpiX;
		}
	}

	[Browsable (false)]
	public ColorPalette Palette {
		get {
			Live ();
			return retrieveGDIPalette();
		}
		set {
			// System.Drawing marshals the palette's entries first.
			if (value == null)
				throw new NullReferenceException ();
			Live ();
			storeGDIPalette(value);
		}
	}

	internal ColorPalette retrieveGDIPalette()
	{
		if (managed != null) {
			GdipFrame f = managed.Frame;
			return new ColorPalette (f.Palette != null ? f.PaletteFlags : 0, f.Palette != null ? (Color []) f.Palette.Clone () : new Color [0]);
		}
		// A metafile has no palette: GDI+ reports a generic error.
		((Metafile) this).CheckPlayable ();
		throw Metafile.GenericError ();
	}

	internal void storeGDIPalette(ColorPalette palette)
	{
		// System.Drawing marshals the palette's entries without a check.
		if (palette == null)
			throw new NullReferenceException ();
		if (managed != null) {
			// GpBitmap::SetPalette keeps the palette whatever the format (a direct bitmap only
			// carries it).
			GdipFrame f = Data.Frame;
			f.Palette = (Color []) palette.Entries.Clone ();
			f.PaletteFlags = palette.Flags;
			return;
		}
		((Metafile) this).CheckPlayable ();
		throw new NotImplementedException ("Not implemented.");
	}


	public SizeF PhysicalDimension {
		get {
			Live ();
			// A bitmap's physical dimension is its size in pixels; only a metafile has another.
			if (this is Metafile mf) return mf.MetafilePhysicalDimension;
			return new SizeF (Width, Height);
		}
	}

	public PixelFormat PixelFormat {
		get {
			if (disposed) return PixelFormat.Undefined;
			if (managed != null) return managed.Frame.Format;
			if (this is Metafile mf) return mf.MetafilePixelFormat;
			return PixelFormat.Format32bppArgb;
		}
	}

	[Browsable (false)]
	public int[] PropertyIdList {
		get {
			Live ();
			if (managed != null) return managed.Properties.ConvertAll (p => p.Id).ToArray ();
			// GpMetafile::GetPropertyCount is zero.
			if (this is Metafile mf) mf.CheckPlayable ();
			return new int [0];
		}
	}

	[Browsable (false)]
	public PropertyItem[] PropertyItems {
		get {
			Live ();
			if (managed != null) return managed.Properties.ConvertAll (CopyProperty).ToArray ();
			// GpMetafile::GetPropertySize is NotImplemented.
			if (this is Metafile mf) mf.CheckPlayable ();
			throw new NotImplementedException ("Not implemented.");
		}
	}

	public ImageFormat RawFormat {
		get {
			Live ();
			if (managed != null) return new ImageFormat (managed.RawFormat);
			if (this is Metafile mf) return mf.MetafileRawFormat;
			return ImageFormat.MemoryBmp;
		}
	}

	public virtual Size Size {
		get {
			Live ();
			return new Size(Width, Height);
		}
	}

	[DefaultValue (null)]
	[LocalizableAttribute(false)]
	[BindableAttribute(true)]
	[TypeConverter (typeof (StringConverter))]
	public object Tag {
		get { return tag; }
		set { tag = value; }
	}
	public float VerticalResolution {
		get {
			Live ();
			if (managed != null) return managed.Frame.DpiY;
			if (this is Metafile mf) return mf.MetafileDpiY;
			return managedDpiY;
		}
	}

	[DefaultValue (false)]
	[Browsable (false)]
	[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
	public int Width {
		get {
			Live ();
			if (managed != null) return managed.Frame.Width;
			if (this is Metafile mf) return mf.MetafileWidth;
			return managedWidth < 0 ? 0 : managedWidth;
		}
	}

	internal IntPtr NativeObject{
		get{
			return nativeObject;
		}
	}

	// For compatiblity with CoreFX sources
	internal IntPtr nativeImage {
		get {
			return nativeObject;
		}
	}

	public void Dispose ()
	{
		Dispose (true);
		GC.SuppressFinalize (this);
	}

	~Image ()
	{
		Dispose (false);
	}

	protected virtual void Dispose (bool disposing)
	{
		managed = null;
		disposed = true;
	}

	// GDI+'s image is gone after Dispose: every call System.Drawing makes with its handle (zero)
	// comes back InvalidParameter. Tag is managed-side; PixelFormat swallows the status (Undefined).
	internal bool disposed;

	internal void Live ()
	{
		if (disposed)
			throw new ArgumentException ("Parameter is not valid.");
	}

	public object Clone ()
	{
		Live ();
		if (managed != null) return new Bitmap (Data.Clone ());
		if (this is Metafile mf) return mf.CloneMetafile ();
		return MemberwiseClone ();
	}

}

}
