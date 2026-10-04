//
// System.Drawing.Bitmap.cs
//
// Copyright (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004 Novell, Inc.  http://www.novell.com
//
// Authors:
//	Alexandre Pigolkine (pigolkine@gmx.de)
//	Christian Meyer (Christian.Meyer@cs.tum.edu)
//	Miguel de Icaza (miguel@ximian.com)
//	Jordi Mas i Hernandez (jmas@softcatala.org)
//	Ravindra (rkumar@novell.com)
//

//
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
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
// A MANAGED bitmap (see Image.cs): pixels in a GdipImageData, never a GDI+ object.
//
// Drawing INTO a bitmap (Graphics.FromImage) records a scene, as drawing on a window does. The scene
// stays recorded until something needs the bitmap's pixels -- GetPixel, LockBits, Save, a clone, an
// ImageAttributes draw -- and is then rasterized onto them on the CPU (WebGpuBackend.SceneRaster).
// Drawn onto another Graphics with nothing in between that needs pixels, the recording is drawn as
// the nested scene it is, so a bitmap used as a back buffer reaches the screen through the same
// renderer as the window around it.
//

using System.Collections.Generic;
using System.IO;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace System.Drawing
{
	[Serializable]
	[ComVisible (true)]
	[Editor ("System.Drawing.Design.BitmapEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
	public sealed class Bitmap : Image
	{
		#region constructors
		// constructors

		// required for XmlSerializer (#323246)
		private Bitmap ()
		{
		}

		internal Bitmap (GdipImageData data)
		{
			managed = data;
		}

		public Bitmap (int width, int height) : this (width, height, PixelFormat.Format32bppArgb)
		{
		}

		public Bitmap (int width, int height, Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			// GdipCreateBitmapFromGraphics: premultiplied, at the Graphics' resolution.
			Create (width, height, PixelFormat.Format32bppPArgb);
			managed.Frame.DpiX = g.DpiX;
			managed.Frame.DpiY = g.DpiY;
		}

		public Bitmap (int width, int height, PixelFormat format)
		{
			Create (width, height, format);
		}

		void Create (int width, int height, PixelFormat format)
		{
			if (width <= 0 || height <= 0 || !GdipPixels.Creatable (format))
				throw new ArgumentException ("Parameter is not valid.");
			managed = new GdipImageData (new GdipFrame (width, height, format)) { Flags = GdipImageData.NewFlags (format) };
			// A new bitmap of an alpha format is transparent black: nothing to draw under a recording.
			blank = Image.IsAlphaPixelFormat (format);
		}

		public Bitmap (Image original) : this (original, original.Width, original.Height) {}

		public Bitmap (Stream stream)  : this (stream, false) {}

		public Bitmap (string filename) : this (filename, false) {}

		public Bitmap (Image original, Size newSize)  : this(original, newSize.Width, newSize.Height) {}

		public Bitmap (Stream stream, bool useIcm)
		{
			if (stream == null)
				throw new ArgumentNullException ("stream");
			managed = GdipCodecs.Decode (ReadAll (stream));
		}

		public Bitmap (string filename, bool useIcm)
		{
			if (filename == null)
				throw new ArgumentNullException ("filename");
			byte [] data = File.ReadAllBytes (filename);
			managed = GdipCodecs.Decode (data);
		}

		public Bitmap (Type type, string resource)
		{
			if (resource == null)
				throw new ArgumentException ("resource");

			// For compatibility with the .NET Framework
			if (type == null)
				throw new NullReferenceException();

			using (Stream s = type.GetTypeInfo ().Assembly.GetManifestResourceStream (type, resource)) {
				if (s == null) {
					string msg = Locale.GetText ("Resource '{0}' was not found.", resource);
					throw new FileNotFoundException (msg);
				}
				managed = GdipCodecs.Decode (ReadAll (s));
			}
		}

		public Bitmap (Image original, int width, int height)  : this(width, height, PixelFormat.Format32bppArgb)
		{
			if (original == null)
				throw new ArgumentNullException ("original");
			using (Graphics graphics = Graphics.FromImage (this))
				graphics.DrawImage (original, 0, 0, width, height);
		}

		public Bitmap (int width, int height, int stride, PixelFormat format, IntPtr scan0)
		{
			Create (width, height, format);
			if (scan0 == IntPtr.Zero)
				return;
			// GDI+ draws straight into the caller's memory; this copies it in, once.
			GdipFrame f = managed.Frame;
			int rowBytes = (width * Image.GetPixelFormatSize (format) + 7) / 8;
			for (int y = 0; y < height; y++)
				Marshal.Copy (scan0 + y * stride, f.Bits, y * f.Stride, rowBytes);
			blank = false;
		}

		private Bitmap (SerializationInfo info, StreamingContext context)
			: base (info, context)
		{
		}

		#endregion

		// ---- recorded drawing -------------------------------------------------------------------

		// Graphics drawing into this bitmap (FromImage), and scenes they finished, not yet rasterized.
		readonly List<Graphics> drawing = new List<Graphics> ();
		readonly List<object> pending = new List<object> ();
		// The pixels are transparent black and have never been written: a recording drawn as a nested
		// scene needs nothing under it.
		bool blank;

		internal void AttachGraphics (Graphics g)
		{
			lock (drawing) drawing.Add (g);
		}

		/// <summary>A Graphics on this bitmap finished (Flush or Dispose): its scene so far joins the
		/// pending ones. <paramref name="detach"/> when it is gone for good.</summary>
		internal void TakeDrawing (Graphics g, bool detach)
		{
			lock (drawing) {
				object scene = g.TakeRecordedScene ();
				if (scene != null) pending.Add (scene);
				if (detach) drawing.Remove (g);
			}
		}

		// Every open Graphics' drawing so far, moved to the pending list.
		void CollectDrawing ()
		{
			lock (drawing) {
				foreach (Graphics g in drawing) {
					object scene = g.TakeRecordedScene ();
					if (scene != null) pending.Add (scene);
				}
			}
		}

		/// <summary>Renders whatever has been drawn into this bitmap onto its pixels. Everything that
		/// reads or writes pixels goes through here first.</summary>
		internal void FlushDrawing ()
		{
			if (managed == null) return;
			object[] scenes;
			lock (drawing) {
				if (drawing.Count == 0 && pending.Count == 0) return;
				CollectDrawing ();
				scenes = pending.ToArray ();
				pending.Clear ();
			}
			if (scenes.Length == 0) return;
			WebGpuBackend.SceneRaster.Render (managed.Frame, scenes);
			blank = false;
		}

		/// <summary>Clear() on a Graphics of this bitmap with nothing clipping it: every pixel becomes
		/// <paramref name="color"/>, and whatever was drawn before is overwritten with them.</summary>
		internal void ClearTo (Color color)
		{
			lock (drawing) {
				CollectDrawing ();
				pending.Clear ();
			}
			GdipFrame f = managed.Frame;
			var row = new uint [f.Width];
			uint argb = (uint) color.ToArgb ();
			for (int i = 0; i < row.Length; i++) row [i] = argb;
			for (int y = 0; y < f.Height; y++) GdipPixels.WriteArgb (f, 0, y, f.Width, row, 0);
			blank = color.A == 0 && Image.IsAlphaPixelFormat (f.Format);
		}

		/// <summary>For drawing this bitmap as a scene: true with the scenes recorded into it (oldest
		/// first) when there are any and none of them needs the pixels under it to be right --
		/// <paramref name="baseNeeded"/> says whether its pixels must be drawn first.</summary>
		internal bool TryGetRecording (out object[] scenes, out bool baseNeeded)
		{
			scenes = null;
			baseNeeded = !blank;
			lock (drawing) {
				if (drawing.Count == 0 && pending.Count == 0) return false;
				CollectDrawing ();
				bool text = false;
				foreach (object s in pending) {
					if (WebGpuBackend.SceneRaster.NeedsDestination (s)) return false;
					text |= WebGpuBackend.SceneRaster.HasText (s);
				}
				// Without text the CPU rasterizer draws it as GDI+ does, blends and all.
				if (!text) return false;
				scenes = pending.ToArray ();
				return scenes.Length > 0;
			}
		}

		// methods
		public Color GetPixel (int x, int y) {
			GdipFrame f = Data.Frame;
			CheckBounds (f, x, y);
			if (!GdipPixels.Convertible (f.Format))
				throw new ArgumentException ("Parameter is not valid.");
			var px = new uint [1];
			GdipPixels.ReadArgb (f, x, y, 1, px, 0);
			return Color.FromArgb (unchecked ((int) px [0]));
		}

		public void SetPixel (int x, int y, Color color)
		{
			GdipFrame f = Data.Frame;
			if (f.IsIndexed)
				throw new InvalidOperationException ("SetPixel is not supported for images with indexed pixel formats.");
			CheckBounds (f, x, y);
			if (!GdipPixels.Convertible (f.Format))
				throw new ArgumentException ("Parameter is not valid.");
			GdipPixels.WriteArgb (f, x, y, 1, new [] { (uint) color.ToArgb () }, 0);
			blank = false;
		}

		static void CheckBounds (GdipFrame f, int x, int y)
		{
			if (x < 0 || x >= f.Width)
				throw new ArgumentOutOfRangeException ("x", "Parameter must be positive and < Width.");
			if (y < 0 || y >= f.Height)
				throw new ArgumentOutOfRangeException ("y", "Parameter must be positive and < Height.");
		}

		public Bitmap Clone (Rectangle rect, PixelFormat format)
		{
			GdipFrame f = Data.Frame;
			if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > f.Width || rect.Bottom > f.Height
			    || !GdipPixels.Convertible (format) || !GdipPixels.Convertible (f.Format))
				throw new OutOfMemoryException ();
			GdipFrame copy = GdipPixels.Convert (f, rect, format, conversionPalette: !f.IsIndexed || format != f.Format);
			// GDI+ keeps the source's alpha flag on a clone, whatever the clone's format; and the
			// bytes the source was read from when the clone is all of it in its own format
			// (CopyOnWriteBitmap::Clone @18007dd98).
			bool whole = rect.X == 0 && rect.Y == 0 && rect.Width == f.Width && rect.Height == f.Height && format == f.Format;
			return new Bitmap (new GdipImageData (copy) { Flags = GdipImageData.NewFlags (format) | (managed.Flags & (int) ImageFlags.HasAlpha),
				SourceBytes = whole ? managed.SourceBytes : null });
		}

		public Bitmap Clone (RectangleF rect, PixelFormat format)
		{
			return Clone (new Rectangle ((int) rect.X, (int) rect.Y, (int) rect.Width, (int) rect.Height), format);
		}

		public static Bitmap FromHicon (IntPtr hicon)
		{
			return new Bitmap (WebGpuBackend.WindowsImaging.FromHicon (hicon));
		}

		public static Bitmap FromResource (IntPtr hinstance, string bitmapName)
		{
			return new Bitmap (WebGpuBackend.WindowsImaging.FromResource (hinstance, bitmapName));
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHbitmap ()
		{
			return GetHbitmap(Color.LightGray);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHbitmap (Color background)
		{
			return WebGpuBackend.WindowsImaging.GetHbitmap (Data.Frame, background);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHicon ()
		{
			return WebGpuBackend.WindowsImaging.GetHicon (Data.Frame);
		}

		public BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat format)
		{
			BitmapData result = new BitmapData();
			return LockBits (rect, flags, format, result);
		}

		// ---- LockBits ---------------------------------------------------------------------------
		//
		// In the bitmap's own format, with whole bytes to the row's start, a lock is the bitmap's own
		// memory, pinned, at the bitmap's stride -- what GDI+ hands out, writes visible at once. In
		// any other format it is a copy converted out (unless the lock is write-only) and converted
		// back on unlock (unless it is read-only); a ReadWrite lock writes back even untouched, and
		// loses what the round trip loses, as GDI+'s does.

		sealed class LockState
		{
			internal Rectangle Rect;
			internal ImageLockMode Mode;
			internal PixelFormat Format;
			internal GCHandle Pin;
			internal IntPtr Buffer;
			internal int Stride;
		}

		LockState locked;

		public
		BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat format, BitmapData bitmapData)
		{
			if (bitmapData == null)
				throw new ArgumentException ("Parameter is not valid.");
			GdipFrame f = Data.Frame;
			if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > f.Width || rect.Bottom > f.Height)
				throw new ArgumentException ("Parameter is not valid.");
			if ((flags & (ImageLockMode.ReadOnly | ImageLockMode.WriteOnly)) == 0)
				throw new ArgumentException ("Parameter is not valid.");
			if (locked != null)
				throw new InvalidOperationException ("Bitmap region is already locked.");
			bool direct = format == f.Format && (rect.X * f.BitsPerPixel) % 8 == 0;
			if (!direct && (!GdipPixels.Convertible (format) || !GdipPixels.Convertible (f.Format)))
				throw new ArgumentException ("Parameter is not valid.");

			var state = new LockState { Rect = rect, Mode = flags, Format = format };
			if (direct) {
				state.Pin = GCHandle.Alloc (f.Bits, GCHandleType.Pinned);
				state.Stride = f.Stride;
				state.Buffer = state.Pin.AddrOfPinnedObject () + rect.Y * f.Stride + rect.X * f.BitsPerPixel / 8;
			} else {
				int bpp = Image.GetPixelFormatSize (format);
				state.Stride = ((rect.Width * bpp + 31) / 32) * 4;
				state.Buffer = Marshal.AllocHGlobal (state.Stride * rect.Height);
				var bytes = new byte [state.Stride];
				var argb = new uint [rect.Width];
				Color[] palette = (format & PixelFormat.Indexed) != 0 ? (f.IsIndexed ? f.Palette : GdipPixels.DefaultPalette (format, out _)) : null;
				var cache = new Dictionary<uint, int> ();
				for (int y = 0; y < rect.Height; y++) {
					if ((flags & ImageLockMode.ReadOnly) != 0) {
						GdipPixels.ReadArgb (f, rect.X, rect.Y + y, rect.Width, argb, 0);
						GdipPixels.WriteArgb (bytes, 0, format, palette, 0, rect.Width, argb, 0, cache);
					}
					Marshal.Copy (bytes, 0, state.Buffer + y * state.Stride, state.Stride);
				}
			}
			bitmapData.Width = rect.Width; bitmapData.Height = rect.Height; bitmapData.Stride = state.Stride;
			bitmapData.PixelFormat = format; bitmapData.Scan0 = state.Buffer;
			bitmapData.Reserved = (int) flags;
			locked = state;
			if ((flags & ImageLockMode.WriteOnly) != 0) blank = false;
			return bitmapData;
		}

		public void MakeTransparent ()
		{
			// .NET's: the BOTTOM-left pixel's colour (LightGray for an empty bitmap), and nothing at all
			// when that pixel is already translucent -- proceeding would key out some unrelated colour.
			Color clr = Color.LightGray;
			if (Width > 0 && Height > 0)
				clr = GetPixel (0, Height - 1);
			if (clr.A < 255)
				return;
			MakeTransparent (clr);
		}

		public void MakeTransparent (Color transparentColor)
		{
			// GDI+ draws the bitmap through a colour key into a new 32bppArgb one: what matches goes
			// transparent black, and everything else takes the premultiplied round trip of a draw.
			GdipFrame f = Data.Frame;
			var dst = new GdipFrame (f.Width, f.Height, PixelFormat.Format32bppArgb);
			var row = new uint [f.Width];
			uint key = (uint) transparentColor.ToArgb () & 0xffffff;
			for (int y = 0; y < f.Height; y++) {
				GdipPixels.ReadArgb (f, 0, y, f.Width, row, 0);
				for (int x = 0; x < row.Length; x++)
					row [x] = (row [x] & 0xffffff) == key ? 0u : GdipPixels.UnpremultiplyArgb (GdipPixels.PremultiplyArgb (row [x]));
				GdipPixels.WriteArgb (dst, 0, y, f.Width, row, 0);
			}
			managed = new GdipImageData (dst) { Flags = GdipImageData.NewFlags (PixelFormat.Format32bppArgb) };
			blank = false;
		}

		public void SetResolution (float xDpi, float yDpi)
		{
			if (!(xDpi > 0) || !(yDpi > 0))
				throw new ArgumentException ("Parameter is not valid.");
			GdipFrame f = Data.Frame;
			f.DpiX = xDpi;
			f.DpiY = yDpi;
		}

		public void UnlockBits (BitmapData bitmapdata)
		{
			if (bitmapdata == null)
				throw new ArgumentException ("Parameter is not valid.");
			LockState state = locked;
			if (state == null || bitmapdata.Scan0 != state.Buffer)
				throw new ArgumentException ("Parameter is not valid.");
			locked = null;
			if (state.Pin.IsAllocated) {
				state.Pin.Free ();
			} else {
				GdipFrame f = managed.Frame;
				if ((state.Mode & ImageLockMode.WriteOnly) != 0) {
					Rectangle rect = state.Rect;
					var bytes = new byte [state.Stride];
					var argb = new uint [rect.Width];
					Color[] palette = (state.Format & PixelFormat.Indexed) != 0 ? (f.IsIndexed ? f.Palette : GdipPixels.DefaultPalette (state.Format, out _)) : null;
					var cache = new Dictionary<uint, int> ();
					for (int y = 0; y < rect.Height; y++) {
						Marshal.Copy (state.Buffer + y * state.Stride, bytes, 0, state.Stride);
						GdipPixels.ReadArgb (bytes, 0, state.Format, palette, 0, rect.Width, argb, 0);
						GdipPixels.WriteArgb (f, rect.X, rect.Y + y, rect.Width, argb, 0, cache);
					}
				}
				Marshal.FreeHGlobal (state.Buffer);
			}
			bitmapdata.Scan0 = IntPtr.Zero;
		}

		protected override void Dispose (bool disposing)
		{
			LockState state = locked;
			locked = null;
			if (state != null) {
				if (state.Pin.IsAllocated) state.Pin.Free ();
				else if (state.Buffer != IntPtr.Zero) Marshal.FreeHGlobal (state.Buffer);
			}
			lock (drawing) { drawing.Clear (); pending.Clear (); }
			base.Dispose (disposing);
		}
	}
}
