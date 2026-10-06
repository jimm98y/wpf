//
// System.Drawing.Imaging.Metafile.cs
//
// Authors:
//	Christian Meyer, eMail: Christian.Meyer@cs.tum.edu
//	Dennis Hayes (dennish@raytek.com)
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004,2006-2007 Novell, Inc (http://www.novell.com)
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
// A METAFILE IS MANAGED, on every platform, Windows included: GDI+'s GpMetafile ported
// (gdiplus.dll 10.0.26100, arm64, public PDB). Its bytes and header are a GpMetafileData
// (backend/gdip/GpMetafileFormat.cs), played by GpMetafilePlayer onto any Graphics and recorded by
// GpMetafileRecorder, which writes EMF+ as gdiplus.dll's MetafileRecorder does. The only native code
// is gdi32 on Windows, for the handles only Windows has (an HENHMETAFILE / HMETAFILE handed in or
// asked for, a reference HDC's metrics).
//
// GDI+'s GpMetafile states (+0xb0) are kept: 0 invalid, 1 corrupt, 2 recording, 3 playable. Once
// GetHenhmetafile hands the handle out the metafile is invalid again, as in GDI+.
//

using System.IO;
using System.ComponentModel;
using System.Drawing.WebGpuBackend.Gdip;
using System.Runtime.InteropServices;

namespace System.Drawing.Imaging {

	[Serializable]
	[Editor ("System.Drawing.Design.MetafileEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
	public sealed class Metafile : Image {

		internal enum MetafileState { Invalid = 0, Corrupt = 1, Recording = 2, Playable = 3 }

		internal MetafileState state;
		internal GpMetafileData data;
		// The header GDI+ keeps (this+0x20): read from the file, or built up by a recording.
		internal GpMetafileHeader header;
		internal GpMetafileRecorder recorder;
		// The playback this metafile is being enumerated in (PlayRecord plays into it).
		internal GpMetafilePlayer.Session playback;
		// A handle handed to the constructor that GDI+ would delete when the metafile goes.
		IntPtr ownedHandle;
		bool ownedIsWmf;

		// ---- the recorder hook Graphics.FromImage(metafile) uses ------------------------------------

		/// <summary>Graphics.FromImage(metafile): the recorder, once (GDI+'s
		/// GpMetafile::GetGraphicsContext answers OutOfMemory to a second call and to a metafile
		/// that is not being recorded).</summary>
		internal GpMetafileRecorder TakeRecorder ()
		{
			if (state != MetafileState.Recording || recorder == null || recorder.GraphicsTaken)
				throw new OutOfMemoryException ("Out of memory.");
			recorder.GraphicsTaken = true;
			return recorder;
		}

		// ---- constructors: from a GDI+ status ---------------------------------------------------------

		internal static Exception StatusException (GpMetafileFormat.ReadStatus s)
		{
			switch (s) {
			case GpMetafileFormat.ReadStatus.InvalidParameter: return new ArgumentException ("Parameter is not valid.");
			case GpMetafileFormat.ReadStatus.OutOfMemory: return new OutOfMemoryException ("Out of memory.");
			default: return new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
			}
		}

		internal static ExternalException GenericError () => new ExternalException ("A generic error occurred in GDI+.", unchecked ((int) 0x80004005));
		internal static ArgumentException InvalidParameter () => new ArgumentException ("Parameter is not valid.");

		void Load (GpMetafileData d)
		{
			data = d;
			header = d.Header.Clone ();
			state = MetafileState.Playable;
		}

		// GdipCreateMetafileFromStream: the stream is read from where it stands; anything GDI+ cannot
		// make a playable metafile of is a generic error.
		void LoadBytes (byte[] bytes)
		{
			var st = GpMetafileFormat.Read (bytes, 0, out GpMetafileData d, out bool _);
			if (st != GpMetafileFormat.ReadStatus.Ok)
				throw GenericError ();
			Load (d);
		}

		internal Metafile (GpMetafileData d)
		{
			Load (d);
		}

		public Metafile (Stream stream)
		{
			if (stream == null)
				throw new ArgumentException ("Value of 'null' is not valid for 'stream'.");
			LoadBytes (ReadAll (stream));
		}

		public Metafile (string filename)
		{
			if (filename == null)
				throw new ArgumentNullException ("path");
			if (filename.Length == 0)
				throw new ArgumentException ("The path is not of a legal form.");
			byte[] bytes;
			try {
				bytes = File.ReadAllBytes (filename);
			} catch (IOException) {
				throw GenericError ();
			} catch (UnauthorizedAccessException) {
				throw GenericError ();
			}
			LoadBytes (bytes);
		}

		// GdipCreateMetafileFromEmf: the handle's bytes, and GDI+'s header of them (GpMetafile::InitEmf).
		public Metafile (IntPtr henhmetafile, bool deleteEmf)
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HENHMETAFILE exists only on Windows.");
			if (henhmetafile == IntPtr.Zero)
				throw InvalidParameter ();
			byte[] emf = GpWindowsMetafile.EnhMetaFileBits (henhmetafile);
			if (emf == null || !GpMetafileFormat.HeaderFromEmf (emf, out GpMetafileHeader h)) {
				if (deleteEmf)
					GpWindowsMetafile.DeleteEmf (henhmetafile);
				throw GenericError ();
			}
			Load (new GpMetafileData { Header = h, Emf = emf });
			if (deleteEmf) {
				ownedHandle = henhmetafile;
				ownedIsWmf = false;
			}
		}

		public Metafile (IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader) : this (hmetafile, wmfHeader, false)
		{
		}

		// GdipCreateMetafileFromWmf -> GpMetafile::InitWmf: a valid placeable header keeps the WMF,
		// anything else turns it into an EMF the way GDI+ does (GetEmfFromWmfData).
		public Metafile (IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader, bool deleteWmf)
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HMETAFILE exists only on Windows.");
			if (hmetafile == IntPtr.Zero)
				throw InvalidParameter ();
			byte[] wmf = GpWindowsMetafile.MetaFileBits (hmetafile);
			GpPlaceable? p = wmfHeader == null ? (GpPlaceable?) null : GpPlaceable.FromPublic (wmfHeader);
			GpMetafileData d = wmf == null ? null : FromWmf (wmf, p);
			if (d == null) {
				if (deleteWmf)
					GpWindowsMetafile.DeleteWmf (hmetafile);
				throw GenericError ();
			}
			Load (d);
			if (deleteWmf) {
				ownedHandle = hmetafile;
				ownedIsWmf = true;
			}
		}

		internal static GpMetafileData FromWmf (byte[] wmf, GpPlaceable? placeable)
		{
			if (!GpMetafileFormat.WmfHeaderIsValid (wmf, 0, wmf.Length))
				return null;
			if (placeable.HasValue && placeable.Value.IsValid) {
				GpPlaceable p = placeable.Value;
				return new GpMetafileData { Header = GpMetafileFormat.HeaderFromWmf (wmf, p), Wmf = wmf, Placeable = p, HasPlaceable = true };
			}
			byte[] emf = GpWmfToEmf.Convert (wmf, null, out GpMetafileHeader h);
			return emf == null ? null : new GpMetafileData { Header = h, Emf = emf };
		}

		// ---- recording ------------------------------------------------------------------------------

		public Metafile (IntPtr referenceHdc, EmfType emfType) :
			this (referenceHdc, emfType, null)
		{
		}

		public Metafile (IntPtr referenceHdc, Rectangle frameRect) :
			this (referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (IntPtr referenceHdc, RectangleF frameRect) :
			this (referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc) :
			this (stream, referenceHdc, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc) :
			this (fileName, referenceHdc, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (IntPtr referenceHdc, EmfType emfType, string description)
		{
			Record (null, null, referenceHdc, null, MetafileFrameUnit.GdiCompatible, emfType, description);
		}

		public Metafile (IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) :
			this (referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) :
			this (referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, EmfType type) :
			this (stream, referenceHdc, type, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, Rectangle frameRect) :
			this (stream, referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, RectangleF frameRect) :
			this (stream, referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, EmfType type) :
			this (fileName, referenceHdc, type, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, Rectangle frameRect) :
			this (fileName, referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, RectangleF frameRect) :
			this (fileName, referenceHdc, frameRect, MetafileFrameUnit.GdiCompatible, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type) :
			this (referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type) :
			this (referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, EmfType type, string description)
		{
			if (stream == null)
				throw new NullReferenceException ();
			Record (stream, null, referenceHdc, null, MetafileFrameUnit.GdiCompatible, type, description);
		}

		public Metafile (Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) :
			this (stream, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) :
			this (stream, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, EmfType type, string description)
		{
			CheckFileName (fileName);
			Record (null, fileName, referenceHdc, null, MetafileFrameUnit.GdiCompatible, type, description);
		}

		public Metafile (string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) :
			this (fileName, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) :
			this (fileName, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, null)
		{
		}

		// System.Drawing's Rectangle overloads pass no frame at all for an empty rectangle (GDI then
		// takes the frame from what is drawn); the RectangleF ones always pass theirs.
		public Metafile (IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type,
			string desc)
		{
			Record (null, null, referenceHdc, frameRect.IsEmpty ? (RectangleF?) null : frameRect,
				frameRect.IsEmpty ? MetafileFrameUnit.GdiCompatible : frameUnit, type, desc);
		}

		public Metafile (IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type,
			string description)
		{
			Record (null, null, referenceHdc, frameRect, frameUnit, type, description);
		}

		public Metafile (Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit,
			EmfType type) : this (stream, referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit,
			EmfType type) : this (stream, referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit,
			EmfType type) : this (fileName, referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit,
			string description) : this (fileName, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual, description)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit,
			EmfType type) : this (fileName, referenceHdc, frameRect, frameUnit, type, null)
		{
		}

		public Metafile (string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit,
			string desc) : this (fileName, referenceHdc, frameRect, frameUnit, EmfType.EmfPlusDual,
			desc)
		{
		}

		public Metafile (Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit,
			EmfType type, string description)
		{
			if (stream == null)
				throw new NullReferenceException ();
			Record (stream, null, referenceHdc, frameRect.IsEmpty ? (RectangleF?) null : frameRect,
				frameRect.IsEmpty ? MetafileFrameUnit.GdiCompatible : frameUnit, type, description);
		}

		public Metafile (Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit,
			EmfType type, string description)
		{
			if (stream == null)
				throw new NullReferenceException ();
			Record (stream, null, referenceHdc, frameRect, frameUnit, type, description);
		}

		public Metafile (string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit,
			EmfType type, string description)
		{
			CheckFileName (fileName);
			Record (null, fileName, referenceHdc, frameRect.IsEmpty ? (RectangleF?) null : frameRect,
				frameRect.IsEmpty ? MetafileFrameUnit.GdiCompatible : frameUnit, type, description);
		}

		public Metafile (string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit,
			EmfType type, string description)
		{
			CheckFileName (fileName);
			Record (null, fileName, referenceHdc, frameRect, frameUnit, type, description);
		}

		static void CheckFileName (string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException ("path");
		}

		// GdipRecordMetafile* -> GpMetafile::InitForRecording. GDI+ refuses a NULL reference HDC; here
		// none means a 96-dpi display, the same on every platform. A real HDC exists only on Windows.
		void Record (Stream stream, string fileName, IntPtr referenceHdc, RectangleF? frame, MetafileFrameUnit frameUnit,
			EmfType type, string description)
		{
			if ((int) type < 3 || (int) type > 5 || (int) frameUnit < 2 || (int) frameUnit > 7)
				throw InvalidParameter ();
			GpRefDevice dev;
			if (referenceHdc == IntPtr.Zero)
				dev = GpRefDevice.Default;
			else if (OperatingSystem.IsWindows ())
				dev = GpWindowsMetafile.Device (referenceHdc);
			else
				throw new PlatformNotSupportedException ("An HDC exists only on Windows.");
			var rec = GpMetafileRecorder.Create (this, dev, type, frame, frameUnit, description, stream, fileName);
			if (rec == null)
				throw GenericError ();
			recorder = rec;
			header = rec.Header;
			state = MetafileState.Recording;
		}

		/// <summary>GpMetafileRecorder.End: the recording is over and the metafile plays.</summary>
		internal void RecordingEnded (GpMetafileData d)
		{
			recorder = null;
			if (d == null) {
				state = MetafileState.Invalid;
				return;
			}
			data = d;
			header = d.Header.Clone ();
			state = MetafileState.Playable;
		}

		// ---- disposal -------------------------------------------------------------------------------

		protected override void Dispose (bool disposing)
		{
			if (ownedHandle != IntPtr.Zero && OperatingSystem.IsWindows ()) {
				if (ownedIsWmf)
					GpWindowsMetafile.DeleteWmf (ownedHandle);
				else
					GpWindowsMetafile.DeleteEmf (ownedHandle);
			}
			ownedHandle = IntPtr.Zero;
			data = null;
			recorder = null;
			state = MetafileState.Invalid;
			base.Dispose (disposing);
		}

		// ---- methods --------------------------------------------------------------------------------

		/// <summary>GdipGetHemfFromMetafile: the handle (an HMETAFILE for a WMF), after which the
		/// metafile is invalid.</summary>
		public IntPtr GetHenhmetafile ()
		{
			if (state != MetafileState.Playable)
				throw InvalidParameter ();
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HENHMETAFILE exists only on Windows.");
			IntPtr h = data.IsWmf ? GpWindowsMetafile.ToHmetafile (data.Wmf) : GpWindowsMetafile.ToHenhmetafile (data.Emf);
			data = null;
			state = MetafileState.Invalid;
			return h;
		}

		/// <summary>GdipGetMetafileHeaderFromMetafile: valid while recording too.</summary>
		public MetafileHeader GetMetafileHeader ()
		{
			if (state != MetafileState.Playable && state != MetafileState.Recording)
				throw InvalidParameter ();
			return new MetafileHeader (header);
		}

		public static MetafileHeader GetMetafileHeader (IntPtr henhmetafile)
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HENHMETAFILE exists only on Windows.");
			if (henhmetafile == IntPtr.Zero)
				throw InvalidParameter ();
			byte[] emf = GpWindowsMetafile.EnhMetaFileBits (henhmetafile);
			if (emf == null || !GpMetafileFormat.HeaderFromEmf (emf, out GpMetafileHeader h))
				throw InvalidParameter ();
			return new MetafileHeader (h);
		}

		// GetMetafileHeader(stream): GDI+ reads the header where the stream stands, and leaves the
		// stream where it found it.
		public static MetafileHeader GetMetafileHeader (Stream stream)
		{
			if (stream == null)
				throw new NullReferenceException ();
			long pos = stream.CanSeek ? stream.Position : -1;
			byte[] bytes = ReadAll (stream);
			if (pos >= 0)
				stream.Position = pos;
			return HeaderOf (bytes);
		}

		public static MetafileHeader GetMetafileHeader (string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException ("path");
			byte[] bytes;
			try {
				bytes = File.ReadAllBytes (fileName);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
				throw InvalidParameter ();
			}
			return HeaderOf (bytes);
		}

		static MetafileHeader HeaderOf (byte[] bytes)
		{
			var st = GpMetafileFormat.Read (bytes, 0, out GpMetafileData d, out bool _);
			if (st != GpMetafileFormat.ReadStatus.Ok)
				throw InvalidParameter ();
			return new MetafileHeader (d.Header);
		}

		// GdipGetMetafileHeaderFromWmf: the placeable header decides it (GetMetafileHeader(HMETAFILE,
		// placeable)); an invalid one is InvalidParameter.
		public static MetafileHeader GetMetafileHeader (IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader)
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HMETAFILE exists only on Windows.");
			if (hmetafile == IntPtr.Zero || wmfHeader == null)
				throw InvalidParameter ();
			GpPlaceable p = GpPlaceable.FromPublic (wmfHeader);
			if (!p.IsValid)
				throw InvalidParameter ();
			byte[] wmf = GpWindowsMetafile.MetaFileBits (hmetafile);
			if (wmf == null)
				throw InvalidParameter ();
			if (!GpMetafileFormat.WmfHeaderIsValid (wmf, 0, wmf.Length)) {
				// GDI+ builds a header of its own from the size of the bits.
				var fake = new byte [Math.Max (18, wmf.Length)];
				Le.W16 (fake, 0, 1); Le.W16 (fake, 2, 9); Le.W16 (fake, 4, 0x300);
				Le.W32 (fake, 6, wmf.Length >> 1);
				wmf = fake;
			}
			return new MetafileHeader (GpMetafileFormat.HeaderFromWmf (wmf, p));
		}

		/// <summary>GdipPlayMetafileRecord: plays one record, during an EnumerateMetafile callback,
		/// onto the Graphics being enumerated to.</summary>
		public void PlayRecord (EmfPlusRecordType recordType, int flags, int dataSize, byte[] data)
		{
			if (state != MetafileState.Playable)
				throw InvalidParameter ();
			GpMetafilePlayer.PlayRecord (this, recordType, flags, dataSize, data);
		}

		// ---- what Image answers for a metafile (GpMetafile's GpImage overrides) ---------------------

		internal void CheckPlayable ()
		{
			if (state != MetafileState.Playable)
				throw InvalidParameter ();
		}

		// GetImageInfo: the header's Width and Height, playable metafiles only.
		internal int MetafileWidth { get { CheckPlayable (); return header.Width; } }
		internal int MetafileHeight { get { CheckPlayable (); return header.Height; } }
		internal float MetafileDpiX { get { CheckPlayable (); return header.DpiX; } }
		internal float MetafileDpiY { get { CheckPlayable (); return header.DpiY; } }

		// GetImageInfo answers DontCare for a recording; a playable metafile is 32bppRGB.
		internal PixelFormat MetafilePixelFormat {
			get {
				if (state == MetafileState.Recording) return PixelFormat.DontCare;
				CheckPlayable ();
				return PixelFormat.Format32bppRgb;
			}
		}

		internal ImageFormat MetafileRawFormat {
			get {
				CheckPlayable ();
				return header.IsWmf ? ImageFormat.Wmf : ImageFormat.Emf;
			}
		}

		// ImageInfo.Flags: Scalable | HasAlpha | ReadOnly | 0x40000.
		internal int MetafileFlags { get { CheckPlayable (); return 0x50003; } }

		/// <summary>GetPhysicalDimension, in 0.01 mm: an EMF's frame plus one pixel, a WMF's size at
		/// its own dpi.</summary>
		internal SizeF MetafilePhysicalDimension {
			get {
				if (state != MetafileState.Playable && state != MetafileState.Recording)
					throw InvalidParameter ();
				GpMetafileHeader h = header;
				if ((int) h.Type < 3)
					return new SizeF ((float) h.Width / h.DpiX * 2540f, (float) h.Height / h.DpiY * 2540f);
				return new SizeF ((float) (h.FrameRight - h.FrameLeft) + 2540f / h.DpiX,
					(float) (h.FrameBottom - h.FrameTop) + 2540f / h.DpiY);
			}
		}

		/// <summary>GetRealBounds: an EMF's frame in pixels at its dpi (one pixel wider and taller), a
		/// WMF's placeable box.</summary>
		internal RectangleF RealBounds {
			get {
				GpMetafileHeader h = header;
				if ((int) h.Type < 3)
					return new RectangleF (h.X, h.Y, h.Width, h.Height);
				float sx = h.DpiX / 2540f, sy = h.DpiY / 2540f;
				return new RectangleF (sx * h.FrameLeft, sy * h.FrameTop,
					(float) (h.FrameRight - h.FrameLeft) * sx + 1f, (float) (h.FrameBottom - h.FrameTop) * sy + 1f);
			}
		}

		internal RectangleF MetafileBounds (ref GraphicsUnit unit)
		{
			if (state != MetafileState.Playable && state != MetafileState.Recording)
				throw InvalidParameter ();
			unit = GraphicsUnit.Pixel;
			return RealBounds;
		}

		/// <summary>GpMetafile::GetBitmap: the metafile drawn into a 32bpp bitmap of its own size (a
		/// WMF's at the desktop's dpi), nearest-neighbour, which is what saving or thumbnailing a
		/// metafile hands to the encoder.</summary>
		internal Bitmap ToBitmap (int width, int height)
		{
			CheckPlayable ();
			RectangleF b = RealBounds;
			if (width < 1 || height < 1) {
				// A WMF's box is in its own units: its inches at the desktop's 96 dpi.
				float w = b.Width, h = b.Height;
				if ((int) header.Type < 3) {
					if (header.DpiX <= 0f || header.DpiY <= 0f)
						throw GenericError ();
					w = w / header.DpiX * 96f;
					h = h / header.DpiY * 96f;
				}
				width = GpMetafileFormat.Round (w);
				height = GpMetafileFormat.Round (h);
				if (width < 1 || height < 1)
					throw GenericError ();
			}
			var bmp = new Bitmap (width, height, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage (bmp)) {
				g.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
				GpMetafilePlayer.Play (g, this, new [] { new PointF (0, 0), new PointF (width, 0), new PointF (0, height) },
					b, GraphicsUnit.Pixel, null);
			}
			return bmp;
		}

		internal Metafile CloneMetafile ()
		{
			CheckPlayable ();
			// GpMetafile::Clone: a WMF is rebuilt with a placeable header made from its bounds and
			// its dpi rounded; an EMF is copied.
			if (data.IsWmf) {
				var p = new GpPlaceable {
					Key = GpMetafileFormat.PlaceableKey,
					Left = (short) header.X, Top = (short) header.Y,
					Right = (short) (header.X + header.Width), Bottom = (short) (header.Y + header.Height),
					Inch = (short) GpMetafileFormat.Round (header.DpiX),
				};
				byte[] b = p.ToBytes ();
				ushort x = 0;
				for (int i = 0; i < 10; i++) x ^= Le.U16 (b, i * 2);
				p.Checksum = (short) x;
				var d = FromWmf (data.Wmf, p);
				if (d == null) throw GenericError ();
				return new Metafile (d);
			}
			if (!GpMetafileFormat.HeaderFromEmf (data.Emf, out GpMetafileHeader h))
				throw GenericError ();
			return new Metafile (new GpMetafileData { Header = h, Emf = data.Emf });
		}
	}
}
