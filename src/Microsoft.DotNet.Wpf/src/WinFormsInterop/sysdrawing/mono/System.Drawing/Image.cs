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
// (backend/GdipPixels.cs). Only a Metafile is still a GDI+ object (nativeObject); the members below
// that serve it are the only ones left that call into GDI+, and only for it.
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

	// A Metafile's GDI+ object. Zero for every other image.
	internal IntPtr nativeObject = IntPtr.Zero;
	// The pixels and everything else a managed image holds (null for a Metafile).
	internal GdipImageData managed;
	// The dimensions of an image that is neither (a print preview's page, which is a scene).
	internal int managedWidth = -1, managedHeight = -1;
	internal float managedDpiX = 96f, managedDpiY = 96f;
	// when using MS GDI+ and IStream we must ensure the stream stays alive for all the life of the Image
	internal Stream stream;


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
				Save (ms, RawFormat);
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
			throw new OutOfMemoryException ();
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

	// For compatiblity with CoreFX sources
	internal static Image CreateImageObject (IntPtr nativeImage)
	{
		return CreateFromHandle (nativeImage);
	}

	// A GDI+ image object: only a Metafile is one now.
	internal static Image CreateFromHandle (IntPtr handle)
	{
		ImageType type;
		GDIPlus.CheckStatus (GDIPlus.GdipGetImageType (handle, out type));
		switch (type) {
		case ImageType.Metafile:
			return new Metafile (handle);
		default:
			throw new NotSupportedException (Locale.GetText ("Unknown image type."));
		}
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

	// A Metafile's stream, handed to GDI+.
	internal static IntPtr InitFromStream (Stream stream)
	{
		if (stream == null)
			throw new ArgumentException ("stream");

		IntPtr imagePtr;
		Status st;

		// Seeking required
		if (!stream.CanSeek) {
			byte[] buffer = new byte[256];
			int index = 0;
			int count;

			do {
				if (buffer.Length < index + 256) {
					byte[] newBuffer = new byte[buffer.Length * 2];
					Array.Copy(buffer, newBuffer, buffer.Length);
					buffer = newBuffer;
				}
				count = stream.Read(buffer, index, 256);
				index += count;
			}
			while (count != 0);

			stream = new MemoryStream(buffer, 0, index);
		}

		if (GDIPlus.RunningOnUnix ()) {
			// Unix, with libgdiplus
			// We use a custom API for this, because there's no easy way
			// to get the Stream down to libgdiplus.  So, we wrap the stream
			// with a set of delegates.
			GDIPlus.GdiPlusStreamHelper sh = new GDIPlus.GdiPlusStreamHelper (stream, true);

			st = GDIPlus.GdipLoadImageFromDelegate_linux (sh.GetHeaderDelegate, sh.GetBytesDelegate,
				sh.PutBytesDelegate, sh.SeekDelegate, sh.CloseDelegate, sh.SizeDelegate, out imagePtr);
		} else {
			st = GDIPlus.GdipLoadImageFromStream (new ComIStreamWrapper (stream), out imagePtr);
		}

		return st == Status.Ok ? imagePtr : IntPtr.Zero;
	}

	// non-static
	public RectangleF GetBounds (ref GraphicsUnit pageUnit)
	{
		if (managed != null || nativeObject == IntPtr.Zero) {
			pageUnit = GraphicsUnit.Pixel;
			return new RectangleF (0, 0, Width, Height);
		}
		RectangleF source;

		Status status = GDIPlus.GdipGetImageBounds (nativeObject, out source, ref pageUnit);
		GDIPlus.CheckStatus (status);

		return source;
	}

	public EncoderParameters GetEncoderParameterList(Guid encoder)
	{
		if (managed != null) return ManagedEncoderParameters (encoder);
		Status status;
		uint sz;

		status = GDIPlus.GdipGetEncoderParameterListSize (nativeObject, ref encoder, out sz);
		GDIPlus.CheckStatus (status);

		IntPtr rawEPList = Marshal.AllocHGlobal ((int) sz);
		EncoderParameters eps;

		try {
			status = GDIPlus.GdipGetEncoderParameterList (nativeObject, ref encoder, sz, rawEPList);
			eps = EncoderParameters.ConvertFromMemory (rawEPList);
			GDIPlus.CheckStatus (status);
		}
		finally {
			Marshal.FreeHGlobal (rawEPList);
		}

		return eps;
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

	public int GetFrameCount (FrameDimension dimension)
	{
		if (managed != null) {
			if (dimension == null)
				throw new ArgumentNullException ("dimension");
			return dimension.Guid == managed.FrameDimension ? managed.FrameCount : 0;
		}
		uint count;
		Guid guid = dimension.Guid;

		Status status = GDIPlus.GdipImageGetFrameCount (nativeObject, ref guid, out count);
		GDIPlus.CheckStatus (status);

		return (int) count;
	}

	static PropertyItem CopyProperty (PropertyItem p)
		=> GdipCodecs.Property (p.Id, p.Type, p.Len, p.Value == null ? null : (byte[]) p.Value.Clone ());

	public PropertyItem GetPropertyItem(int propid)
	{
		if (managed != null) {
			foreach (PropertyItem p in managed.Properties)
				if (p.Id == propid) return CopyProperty (p);
			throw new ArgumentException ("Property cannot be found.");
		}
		int propSize;
		IntPtr property;
		PropertyItem item = new PropertyItem ();
		GdipPropertyItem gdipProperty = new GdipPropertyItem ();
		Status status;

		status = GDIPlus.GdipGetPropertyItemSize (nativeObject, propid,
									out propSize);
		GDIPlus.CheckStatus (status);

		/* Get PropertyItem */
		property = Marshal.AllocHGlobal (propSize);
		try {
			status = GDIPlus.GdipGetPropertyItem (nativeObject, propid, propSize, property);
			GDIPlus.CheckStatus (status);
			gdipProperty = (GdipPropertyItem) Marshal.PtrToStructure (property,
								typeof (GdipPropertyItem));
			GdipPropertyItem.MarshalTo (gdipProperty, item);
		}
		finally {
			Marshal.FreeHGlobal (property);
		}
		return item;
	}

	public Image GetThumbnailImage (int thumbWidth, int thumbHeight, Image.GetThumbnailImageAbort callback, IntPtr callbackData)
	{
		if ((thumbWidth <= 0) || (thumbHeight <= 0))
			throw new OutOfMemoryException ("Invalid thumbnail size");

		// GDI+ hands a thumbnail back premultiplied, the image drawn scaled into it.
		Bitmap thumbnail = new Bitmap (thumbWidth, thumbHeight, PixelFormat.Format32bppPArgb);
		using (Graphics g = Graphics.FromImage (thumbnail))
			g.DrawImage (this, 0, 0, thumbWidth, thumbHeight);
		return thumbnail;
	}


	public void RemovePropertyItem (int propid)
	{
		if (managed != null) {
			int i = managed.Properties.FindIndex (p => p.Id == propid);
			if (i < 0)
				throw new ArgumentException ("Property cannot be found.");
			managed.Properties.RemoveAt (i);
			return;
		}
		Status status = GDIPlus.GdipRemovePropertyItem (nativeObject, propid);
		GDIPlus.CheckStatus (status);
	}

	public void RotateFlip (RotateFlipType rotateFlipType)
	{
		if (managed != null) {
			GdipImageData d = Data;
			d.Frame = WebGpuBackend.GdipTransform.RotateFlip (d.Frame, rotateFlipType);
			return;
		}
		Status status = GDIPlus.GdipImageRotateFlip (nativeObject, rotateFlipType);
		GDIPlus.CheckStatus (status);
	}

	internal ImageCodecInfo findEncoderForFormat (ImageFormat format)
	{
		ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
		ImageCodecInfo encoder = null;

		if (format.Guid.Equals (ImageFormat.MemoryBmp.Guid))
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
		Save (filename, RawFormat);
	}

	public void Save(string filename, ImageFormat format)
	{
		if (format == null)
			throw new ArgumentNullException ("format");
		// No encoder for the format (an icon, a metafile): GDI+ writes a PNG.
		ImageCodecInfo encoder = findEncoderForFormat (format) ?? findEncoderForFormat (ImageFormat.Png);
		Save (filename, encoder, null);
	}

	public void Save(string filename, ImageCodecInfo encoder, EncoderParameters encoderParams)
	{
		if (filename == null)
			throw new ArgumentNullException ("filename");
		if (encoder == null)
			throw new ArgumentNullException ("encoder");
		if (managed != null) {
			using (var fs = File.Create (filename))
				SaveManaged (fs, encoder, encoderParams);
			return;
		}
		Status st;
		Guid guid = encoder.Clsid;

		if (encoderParams == null) {
			st = GDIPlus.GdipSaveImageToFile (nativeObject, filename, ref guid, IntPtr.Zero);
		} else {
			IntPtr nativeEncoderParams = encoderParams.ConvertToMemory ();
			st = GDIPlus.GdipSaveImageToFile (nativeObject, filename, ref guid, nativeEncoderParams);
			Marshal.FreeHGlobal (nativeEncoderParams);
		}

		GDIPlus.CheckStatus (st);
	}

	public void Save (Stream stream, ImageFormat format)
	{
		if (format == null)
			throw new ArgumentNullException ("format");
		ImageCodecInfo encoder = findEncoderForFormat (format) ?? findEncoderForFormat (ImageFormat.Png);
		Save (stream, encoder, null);
	}

	public void Save(Stream stream, ImageCodecInfo encoder, EncoderParameters encoderParams)
	{
		if (stream == null)
			throw new ArgumentNullException ("stream");
		if (encoder == null)
			throw new ArgumentNullException ("encoder");
		if (managed != null) {
			SaveManaged (stream, encoder, encoderParams);
			return;
		}
		Status st;
		IntPtr nativeEncoderParams;
		Guid guid = encoder.Clsid;

		if (encoderParams == null)
			nativeEncoderParams = IntPtr.Zero;
		else
			nativeEncoderParams = encoderParams.ConvertToMemory ();

		try {
			if (GDIPlus.RunningOnUnix ()) {
				GDIPlus.GdiPlusStreamHelper sh = new GDIPlus.GdiPlusStreamHelper (stream, false);
				st = GDIPlus.GdipSaveImageToDelegate_linux (nativeObject, sh.GetBytesDelegate, sh.PutBytesDelegate,
					sh.SeekDelegate, sh.CloseDelegate, sh.SizeDelegate, ref guid, nativeEncoderParams);
			} else {
				st = GDIPlus.GdipSaveImageToStream (new HandleRef (this, nativeObject),
					new ComIStreamWrapper (stream), ref guid, new HandleRef (encoderParams, nativeEncoderParams));
			}
		}
		finally {
			if (nativeEncoderParams != IntPtr.Zero)
				Marshal.FreeHGlobal (nativeEncoderParams);
		}

		GDIPlus.CheckStatus (st);
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
		GdipCodecs.Encode (frame, encoder.Clsid, ps, stream);
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
		if (managed != null) {
			if (multi_stream == null)
				throw new ArgumentException ("Parameter is not valid.");
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
		Status st;

		IntPtr nativeEncoderParams = encoderParams.ConvertToMemory ();
		st = GDIPlus.GdipSaveAdd (nativeObject, nativeEncoderParams);
		Marshal.FreeHGlobal (nativeEncoderParams);
		GDIPlus.CheckStatus (st);
	}

	public void SaveAdd (Image image, EncoderParameters encoderParams)
	{
		if (image == null)
			throw new ArgumentNullException ("image");
		if (managed != null) {
			if (multi_stream == null)
				throw new ArgumentException ("Parameter is not valid.");
			multi_pages.Add (image.Data.Frame.Clone ());
			WriteMulti (false);
			return;
		}
		Status st;

		IntPtr nativeEncoderParams = encoderParams.ConvertToMemory ();
		st = GDIPlus.GdipSaveAddImage (nativeObject, image.NativeObject, nativeEncoderParams);
		Marshal.FreeHGlobal (nativeEncoderParams);
		GDIPlus.CheckStatus (st);
	}

	public int SelectActiveFrame(FrameDimension dimension, int frameIndex)
	{
		if (managed != null) {
			if (dimension == null)
				throw new ArgumentNullException ("dimension");
			GdipImageData d = Data;
			if (dimension.Guid != d.FrameDimension || frameIndex < 0 || frameIndex >= d.FrameCount)
				throw new ArgumentException ("Parameter is not valid.");
			if (d.Frames != null && frameIndex != d.ActiveFrame) {
				d.Frame = d.Frames [frameIndex].Clone ();
				if (d.FrameProperties != null) d.Properties = d.FrameProperties [frameIndex];
				d.ActiveFrame = frameIndex;
			}
			return frameIndex;
		}
		Guid guid = dimension.Guid;
		Status st = GDIPlus.GdipImageSelectActiveFrame (nativeObject, ref guid, frameIndex);

		GDIPlus.CheckStatus (st);

		return frameIndex;
	}

	public void SetPropertyItem(PropertyItem propitem)
	{
		if (propitem == null)
			throw new ArgumentNullException ("propitem");
		if (managed != null) {
			int i = managed.Properties.FindIndex (p => p.Id == propitem.Id);
			PropertyItem copy = CopyProperty (propitem);
			if (i >= 0) managed.Properties [i] = copy;
			else managed.Properties.Add (copy);
			return;
		}

		int nItemSize =  Marshal.SizeOf (propitem.Value[0]);
		int size = nItemSize * propitem.Value.Length;
		IntPtr dest = Marshal.AllocHGlobal (size);
		try {
			GdipPropertyItem pi = new GdipPropertyItem ();
			pi.id    = propitem.Id;
			pi.len   = propitem.Len;
			pi.type  = propitem.Type;

			Marshal.Copy (propitem.Value, 0, dest, size);
			pi.value = dest;

			unsafe {
				Status status = GDIPlus.GdipSetPropertyItem (nativeObject, &pi);

				GDIPlus.CheckStatus (status);
			}
		}
		finally {
			Marshal.FreeHGlobal (dest);
		}
	}

	// properties
	[Browsable (false)]
	public int Flags {
		get {
			if (managed != null) return managed.Flags;
			if (nativeObject == IntPtr.Zero) return 0;
			int flags;

			Status status = GDIPlus.GdipGetImageFlags (nativeObject, out flags);
			GDIPlus.CheckStatus (status);
			return flags;
		}
	}

	[Browsable (false)]
	public Guid[] FrameDimensionsList {
		get {
			if (managed != null) return new [] { managed.FrameDimension };
			if (nativeObject == IntPtr.Zero) return new [] { FrameDimension.Page.Guid };
			uint found;
			Status status = GDIPlus.GdipImageGetFrameDimensionsCount (nativeObject, out found);
			GDIPlus.CheckStatus (status);
			Guid [] guid = new Guid [found];
			status = GDIPlus.GdipImageGetFrameDimensionsList (nativeObject, guid, found);
			GDIPlus.CheckStatus (status);
			return guid;
		}
	}

	[DefaultValue (false)]
	[Browsable (false)]
	[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
	public int Height {
		get {
			if (managed != null) return managed.Frame.Height;
			if (nativeObject == IntPtr.Zero) return managedHeight < 0 ? 0 : managedHeight;
			uint height;
			Status status = GDIPlus.GdipGetImageHeight (nativeObject, out height);
			GDIPlus.CheckStatus (status);

			return (int)height;
		}
	}

	public float HorizontalResolution {
		get {
			if (managed != null) return managed.Frame.DpiX;
			if (nativeObject == IntPtr.Zero) return managedDpiX;
			float resolution;

			Status status = GDIPlus.GdipGetImageHorizontalResolution (nativeObject, out resolution);
			GDIPlus.CheckStatus (status);

			return resolution;
		}
	}

	[Browsable (false)]
	public ColorPalette Palette {
		get {
			return retrieveGDIPalette();
		}
		set {
			storeGDIPalette(value);
		}
	}

	internal ColorPalette retrieveGDIPalette()
	{
		if (managed != null) {
			GdipFrame f = managed.Frame;
			return new ColorPalette (f.Palette != null ? f.PaletteFlags : 0, f.Palette != null ? (Color []) f.Palette.Clone () : new Color [0]);
		}
		int bytes;
		ColorPalette ret = new ColorPalette ();

		Status st = GDIPlus.GdipGetImagePaletteSize (nativeObject, out bytes);
		GDIPlus.CheckStatus (st);
		IntPtr palette_data = Marshal.AllocHGlobal (bytes);
		try {
			st = GDIPlus.GdipGetImagePalette (nativeObject, palette_data, bytes);
			GDIPlus.CheckStatus (st);
			ret.ConvertFromMemory (palette_data);
			return ret;
		}

		finally {
			Marshal.FreeHGlobal (palette_data);
		}
	}

	internal void storeGDIPalette(ColorPalette palette)
	{
		if (palette == null) {
			throw new ArgumentNullException("palette");
		}
		if (managed != null) {
			GdipFrame f = Data.Frame;
			if (f.IsIndexed) {
				f.Palette = (Color []) palette.Entries.Clone ();
				f.PaletteFlags = palette.Flags;
			}
			return;
		}
		IntPtr palette_data = palette.ConvertToMemory ();
		if (palette_data == IntPtr.Zero) {
			return;
		}

		try {
			Status st = GDIPlus.GdipSetImagePalette (nativeObject, palette_data);
			GDIPlus.CheckStatus (st);
		}

		finally {
			Marshal.FreeHGlobal(palette_data);
		}
	}


	public SizeF PhysicalDimension {
		get {
			// A bitmap's physical dimension is its size in pixels; only a metafile has another.
			if (managed != null || nativeObject == IntPtr.Zero) return new SizeF (Width, Height);
			float width,  height;
			Status status = GDIPlus.GdipGetImageDimension (nativeObject, out width, out height);
			GDIPlus.CheckStatus (status);

			return new SizeF (width, height);
		}
	}

	public PixelFormat PixelFormat {
		get {
			if (managed != null) return managed.Frame.Format;
			if (nativeObject == IntPtr.Zero) return PixelFormat.Format32bppArgb;
			PixelFormat pixFormat;
			Status status = GDIPlus.GdipGetImagePixelFormat (nativeObject, out pixFormat);
			GDIPlus.CheckStatus (status);

			return pixFormat;
		}
	}

	[Browsable (false)]
	public int[] PropertyIdList {
		get {
			if (managed != null) return managed.Properties.ConvertAll (p => p.Id).ToArray ();
			uint propNumbers;

			Status status = GDIPlus.GdipGetPropertyCount (nativeObject,
									out propNumbers);
			GDIPlus.CheckStatus (status);

			int [] idList = new int [propNumbers];
			status = GDIPlus.GdipGetPropertyIdList (nativeObject,
								propNumbers, idList);
			GDIPlus.CheckStatus (status);

			return idList;
		}
	}

	[Browsable (false)]
	public PropertyItem[] PropertyItems {
		get {
			if (managed != null) return managed.Properties.ConvertAll (CopyProperty).ToArray ();
			int propNums, propsSize, propSize;
			IntPtr properties, propPtr;
			PropertyItem[] items;
			GdipPropertyItem gdipProperty = new GdipPropertyItem ();
			Status status;

			status = GDIPlus.GdipGetPropertySize (nativeObject, out propsSize, out propNums);
			GDIPlus.CheckStatus (status);

			items =  new PropertyItem [propNums];

			if (propNums == 0)
				return items;

			/* Get PropertyItem list*/
			properties = Marshal.AllocHGlobal (propsSize * propNums);
			try {
				status = GDIPlus.GdipGetAllPropertyItems (nativeObject, propsSize,
								propNums, properties);
				GDIPlus.CheckStatus (status);

				propSize = Marshal.SizeOf (gdipProperty);
				propPtr = properties;

				for (int i = 0; i < propNums; i++, propPtr = new IntPtr (propPtr.ToInt64 () + propSize)) {
					gdipProperty = (GdipPropertyItem) Marshal.PtrToStructure
						(propPtr, typeof (GdipPropertyItem));
					items [i] = new PropertyItem ();
					GdipPropertyItem.MarshalTo (gdipProperty, items [i]);
				}
			}
			finally {
				Marshal.FreeHGlobal (properties);
			}
			return items;
		}
	}

	public ImageFormat RawFormat {
		get {
			if (managed != null) return new ImageFormat (managed.RawFormat);
			if (nativeObject == IntPtr.Zero) return ImageFormat.MemoryBmp;
			Guid guid;
			Status st = GDIPlus.GdipGetImageRawFormat (nativeObject, out guid);

			GDIPlus.CheckStatus (st);
			return new ImageFormat (guid);
		}
	}

	public Size Size {
		get {
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
			if (managed != null) return managed.Frame.DpiY;
			if (nativeObject == IntPtr.Zero) return managedDpiY;
			float resolution;

			Status status = GDIPlus.GdipGetImageVerticalResolution (nativeObject, out resolution);
			GDIPlus.CheckStatus (status);

			return resolution;
		}
	}

	[DefaultValue (false)]
	[Browsable (false)]
	[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
	public int Width {
		get {
			if (managed != null) return managed.Frame.Width;
			if (nativeObject == IntPtr.Zero) return managedWidth < 0 ? 0 : managedWidth;
			uint width;
			Status status = GDIPlus.GdipGetImageWidth (nativeObject, out width);
			GDIPlus.CheckStatus (status);

			return (int)width;
		}
	}

	internal IntPtr NativeObject{
		get{
			return nativeObject;
		}
		set	{
			nativeObject = value;
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
		if (GDIPlus.GdiPlusToken != 0 && nativeObject != IntPtr.Zero) {
			Status status = GDIPlus.GdipDisposeImage (nativeObject);
			// dispose the stream (set under Win32 only if SD owns the stream) and ...
			if (stream != null) {
				stream.Dispose ();
				stream = null;
			}
			// ... set nativeObject to null before (possibly) throwing an exception
			nativeObject = IntPtr.Zero;
			GDIPlus.CheckStatus (status);
		}
	}

	public object Clone ()
	{
		if (managed != null) return new Bitmap (Data.Clone ());
		if (nativeObject == IntPtr.Zero)
			return MemberwiseClone ();
		if (GDIPlus.RunningOnWindows () && stream != null)
			return CloneFromStream ();

		IntPtr newimage = IntPtr.Zero;
		Status status = GDIPlus.GdipCloneImage (NativeObject, out newimage);
		GDIPlus.CheckStatus (status);

		return new Metafile (newimage);
	}

	// On win32, when cloning images that were originally created from a stream, we need to
	// clone both the image and the stream to make sure the gc doesn't kill it
	// (when using MS GDI+ and IStream we must ensure the stream stays alive for all the life of the Image)
	object CloneFromStream ()
	{
		byte[] bytes = new byte [stream.Length];
		MemoryStream ms = new MemoryStream (bytes);
		int count = (stream.Length < 4096 ? (int) stream.Length : 4096);
		byte[] buffer = new byte[count];
		stream.Position = 0;
		do {
			count = stream.Read (buffer, 0, count);
			ms.Write (buffer, 0, count);
		} while (count == 4096);

		IntPtr newimage = IntPtr.Zero;
		newimage = InitFromStream (ms);

		return new Metafile (newimage, ms);
	}

}

}
