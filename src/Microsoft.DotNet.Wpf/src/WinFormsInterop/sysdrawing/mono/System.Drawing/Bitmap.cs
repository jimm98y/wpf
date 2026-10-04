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

		internal Bitmap (IntPtr ptr)
		{
			nativeObject = ptr;
		}

		// Usually called when cloning images that need to have
		// not only the handle saved, but also the underlying stream
		// (when using MS GDI+ and IStream we must ensure the stream stays alive for all the life of the Image)
		internal Bitmap(IntPtr ptr, Stream stream)
		{
			// under Win32 stream is owned by SD/GDI+ code
			if (GDIPlus.RunningOnWindows ())
				this.stream = stream;
			nativeObject = ptr;
		}

		public Bitmap (int width, int height) : this (width, height, PixelFormat.Format32bppArgb)
		{
		}

		public Bitmap (int width, int height, Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");

			IntPtr bmp;
			Status s = GDIPlus.GdipCreateBitmapFromGraphics (width, height, g.nativeObject, out bmp);
			GDIPlus.CheckStatus (s);
			nativeObject = bmp;						
		}

		public Bitmap (int width, int height, PixelFormat format)
		{
			managedWidth = width; managedHeight = height;
			// No libgdiplus (browser; WF_NO_GDIPLUS): the pixels are held here instead -- see
			// Image.managedPixels.
			if (!GDIPlus.Initialized) {
				if (width <= 0 || height <= 0)
					throw new ArgumentException ("Parameter is not valid.");
				managedPixels = new int [checked (width * height)];
				managedFormat = format;
			}
			if (GDIPlus.Initialized) {
				IntPtr bmp;
				Status s = GDIPlus.GdipCreateBitmapFromScan0 (width, height, 0, format, IntPtr.Zero, out bmp);
				GDIPlus.CheckStatus (s);
				nativeObject = bmp;
			}
		}

		public Bitmap (Image original) : this (original, original.Width, original.Height) {}

		public Bitmap (Stream stream)  : this (stream, false) {} 

		public Bitmap (string filename) : this (filename, false) {}

		public Bitmap (Image original, Size newSize)  : this(original, newSize.Width, newSize.Height) {}
		
		public Bitmap (Stream stream, bool useIcm)
		{
			if (!GDIPlus.Initialized) { InitManaged (stream); return; }
			// false: stream is owned by user code
			nativeObject = InitFromStream (stream);
		}

		public Bitmap (string filename, bool useIcm)
		{
			if (filename == null)
				throw new ArgumentNullException ("filename");
			if (!GDIPlus.Initialized) {
				using (var fs = File.OpenRead (filename))
					InitManaged (fs);
				return;
			}

			IntPtr imagePtr;
			Status st;

			if (useIcm)
				st = GDIPlus.GdipCreateBitmapFromFileICM (filename, out imagePtr);
			else
				st = GDIPlus.GdipCreateBitmapFromFile (filename, out imagePtr);

			GDIPlus.CheckStatus (st);
			nativeObject = imagePtr;
		}

		public Bitmap (Type type, string resource)
		{
			if (resource == null)
				throw new ArgumentException ("resource");

			// For compatibility with the .NET Framework
			if (type == null)
				throw new NullReferenceException();

			Stream s = type.GetTypeInfo ().Assembly.GetManifestResourceStream (type, resource);
			if (s == null) {
				string msg = Locale.GetText ("Resource '{0}' was not found.", resource);
				throw new FileNotFoundException (msg);
			}

			if (!GDIPlus.Initialized) { InitManaged (s); return; }
			nativeObject = InitFromStream (s);
			// under Win32 stream is owned by SD/GDI+ code
			if (GDIPlus.RunningOnWindows ())
				stream = s;
		}

		public Bitmap (Image original, int width, int height)  : this(width, height, PixelFormat.Format32bppArgb)
		{
			if (managedPixels != null) { ScaleManaged (original, width, height); return; }
			Graphics graphics = Graphics.FromImage(this);

			graphics.DrawImage(original, 0, 0, width, height);
			graphics.Dispose();
		}

		public Bitmap (int width, int height, int stride, PixelFormat format, IntPtr scan0)
		{
			if (!GDIPlus.Initialized) {
				managedWidth = width; managedHeight = height; managedFormat = format;
				managedPixels = new int [checked (width * height)];
				if (scan0 != IntPtr.Zero)
					ManagedPixels.Read (scan0, stride, format, managedPixels, width, new Rectangle (0, 0, width, height));
				return;
			}
			IntPtr bmp;
				
			Status status = GDIPlus.GdipCreateBitmapFromScan0 (width, height, stride, format, scan0, out bmp);
			GDIPlus.CheckStatus (status);	
			nativeObject = bmp;						 								
		}

		private Bitmap (SerializationInfo info, StreamingContext context)
			: base (info, context)
		{
		}

		#endregion
		// methods
		public Color GetPixel (int x, int y) {
			if (IsManagedPixels) {
				CheckBounds (x, y);
				return Color.FromArgb (managedPixels [y * managedWidth + x]);
			}
			
			int argb;				
			
			Status s = GDIPlus.GdipBitmapGetPixel(nativeObject, x, y, out argb);
			GDIPlus.CheckStatus (s);

			return Color.FromArgb(argb);		
		}

		public void SetPixel (int x, int y, Color color)
		{
			if (IsManagedPixels) {
				CheckBounds (x, y);
				managedPixels [y * managedWidth + x] = color.ToArgb ();
				return;
			}
			Status s = GDIPlus.GdipBitmapSetPixel (nativeObject, x, y, color.ToArgb ());
			if (s == Status.InvalidParameter) {
				// check is done in case of an error only to avoid another
				// unmanaged call for normal (successful) calls
				if ((this.PixelFormat & PixelFormat.Indexed) != 0) {
					string msg = Locale.GetText ("SetPixel cannot be called on indexed bitmaps.");
					throw new InvalidOperationException (msg);
				}
			}
			GDIPlus.CheckStatus (s);
		}

		public Bitmap Clone (Rectangle rect, PixelFormat format)
		{
			if (IsManagedPixels) return CloneManaged (rect, format);
			IntPtr bmp;			
			Status status = GDIPlus.GdipCloneBitmapAreaI (rect.X, rect.Y, rect.Width, rect.Height,
				format, nativeObject, out bmp);
			GDIPlus.CheckStatus (status);
			return new Bitmap (bmp);
       		}
		
		public Bitmap Clone (RectangleF rect, PixelFormat format)
		{
			if (IsManagedPixels) return CloneManaged (Rectangle.Truncate (rect), format);
			IntPtr bmp;			
			Status status = GDIPlus.GdipCloneBitmapArea (rect.X, rect.Y, rect.Width, rect.Height,
				format, nativeObject, out bmp);
			GDIPlus.CheckStatus (status);
			return new Bitmap (bmp);
		}

		public static Bitmap FromHicon (IntPtr hicon)
		{	
			IntPtr bitmap;	
			Status status = GDIPlus.GdipCreateBitmapFromHICON (hicon, out bitmap);
			GDIPlus.CheckStatus (status);
			return new Bitmap (bitmap);
		}

		public static Bitmap FromResource (IntPtr hinstance, string bitmapName)	//TODO: Untested
		{
			IntPtr bitmap;	
			Status status = GDIPlus.GdipCreateBitmapFromResource (hinstance, bitmapName, out bitmap);
			GDIPlus.CheckStatus (status);
			return new Bitmap (bitmap);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHbitmap ()
		{
			return GetHbitmap(Color.Gray);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHbitmap (Color background)
		{
			IntPtr HandleBmp;
			
			Status status = GDIPlus.GdipCreateHBITMAPFromBitmap (nativeObject, out HandleBmp, background.ToArgb ());
			GDIPlus.CheckStatus (status);

			return  HandleBmp;
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public IntPtr GetHicon ()
		{
			IntPtr HandleIcon;
			
			Status status = GDIPlus.GdipCreateHICONFromBitmap (nativeObject, out HandleIcon);
			GDIPlus.CheckStatus (status);

			return  HandleIcon;			
		}

		public BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat format)
		{
			BitmapData result = new BitmapData();
			return LockBits (rect, flags, format, result);
		}

		public
		BitmapData LockBits (Rectangle rect, ImageLockMode flags, PixelFormat format, BitmapData bitmapData)
		{
			if (IsManagedPixels) return LockManaged (rect, flags, format, bitmapData);
			Status status = GDIPlus.GdipBitmapLockBits (nativeObject, ref rect, flags, format, bitmapData);
			//NOTE: scan0 points to piece of memory allocated in the unmanaged space
			GDIPlus.CheckStatus (status);

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
			// We have to draw always over a 32-bitmap surface that supports alpha channel
			Bitmap	bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
			Graphics gr = Graphics.FromImage(bmp);
			Rectangle destRect = new Rectangle(0, 0, Width, Height);
			ImageAttributes imageAttr = new ImageAttributes();
			
			imageAttr.SetColorKey(transparentColor,	transparentColor);

			gr.DrawImage (this, destRect, 0, 0, Width, Height, GraphicsUnit.Pixel, imageAttr);					
			
			IntPtr oldBmp = nativeObject;
			nativeObject = bmp.nativeObject;
			bmp.nativeObject = oldBmp;

			gr.Dispose();
			bmp.Dispose();
			imageAttr.Dispose();
		}

		public void SetResolution (float xDpi, float yDpi)
		{
			Status status = GDIPlus.GdipBitmapSetResolution (nativeObject, xDpi, yDpi);
			GDIPlus.CheckStatus (status);
		}

		public void UnlockBits (BitmapData bitmapdata)
		{
			if (IsManagedPixels) { UnlockManaged (bitmapdata); return; }
			Status status = GDIPlus.GdipBitmapUnlockBits (nativeObject, bitmapdata);
			GDIPlus.CheckStatus (status);
		}
	
		// ---- managed pixels (no GDI+): see Image.managedPixels ----------------------------------

		void CheckBounds (int x, int y)
		{
			if (x < 0 || y < 0 || x >= managedWidth || y >= managedHeight)
				throw new ArgumentOutOfRangeException (x < 0 || x >= managedWidth ? "x" : "y");
		}

		// PNG through the renderer's own decoder; BMP read here. Anything else needs a codec this
		// managed path does not have, and says so.
		void InitManaged (Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException ("stream");
			byte [] data;
			using (var ms = new MemoryStream ()) { stream.CopyTo (ms); data = ms.ToArray (); }
			if (!ManagedPixels.Decode (data, out int w, out int h, out int [] argb, out float dpiX, out float dpiY))
				throw new ArgumentException ("Parameter is not valid: only PNG and BMP images can be read without GDI+.");
			managedWidth = w; managedHeight = h; managedPixels = argb;
			managedDpiX = dpiX; managedDpiY = dpiY;
			managedFormat = PixelFormat.Format32bppArgb;
		}

		void ScaleManaged (Image original, int width, int height)
		{
			if (!(original is Bitmap ob) || !ob.IsManagedPixels) return;
			int sw = ob.managedWidth, sh = ob.managedHeight;
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					managedPixels [y * width + x] = ob.managedPixels [Math.Min (sh - 1, y * sh / height) * sw + Math.Min (sw - 1, x * sw / width)];
		}

		internal Bitmap CloneManaged (Rectangle rect, PixelFormat format)
		{
			if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > managedWidth || rect.Bottom > managedHeight)
				throw new OutOfMemoryException ();
			var b = new Bitmap (rect.Width, rect.Height, format);
			for (int y = 0; y < rect.Height; y++)
				Array.Copy (managedPixels, (rect.Y + y) * managedWidth + rect.X, b.managedPixels, y * rect.Width, rect.Width);
			b.managedDpiX = managedDpiX; b.managedDpiY = managedDpiY;
			return b;
		}

		BitmapData LockManaged (Rectangle rect, ImageLockMode flags, PixelFormat format, BitmapData data)
		{
			if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > managedWidth || rect.Bottom > managedHeight)
				throw new ArgumentException ("Parameter is not valid.");
			int bpp = ManagedPixels.BytesPerPixel (format);
			if (bpp == 0)
				throw new ArgumentException ("Parameter is not valid: pixel format " + format + " is not supported without GDI+.");
			int stride = (rect.Width * bpp + 3) & ~3;
			IntPtr buf = Marshal.AllocHGlobal (stride * rect.Height);
			if ((flags & ImageLockMode.ReadOnly) != 0 || flags == ImageLockMode.ReadWrite || (flags & ImageLockMode.WriteOnly) == 0)
				ManagedPixels.Write (managedPixels, managedWidth, rect, buf, stride, format);
			data.Width = rect.Width; data.Height = rect.Height; data.Stride = stride;
			data.PixelFormat = format; data.Scan0 = buf;
			data.Reserved = (int) flags;
			lock (s_locks) s_locks [buf] = rect;
			return data;
		}

		void UnlockManaged (BitmapData data)
		{
			if (data == null || data.Scan0 == IntPtr.Zero) return;
			Rectangle rect;
			lock (s_locks) {
				if (!s_locks.TryGetValue (data.Scan0, out rect)) return;
				s_locks.Remove (data.Scan0);
			}
			var flags = (ImageLockMode) data.Reserved;
			if ((flags & ImageLockMode.WriteOnly) != 0)
				ManagedPixels.Read (data.Scan0, data.Stride, data.PixelFormat, managedPixels, managedWidth, rect);
			Marshal.FreeHGlobal (data.Scan0);
			data.Scan0 = IntPtr.Zero;
		}

		static readonly System.Collections.Generic.Dictionary<IntPtr, Rectangle> s_locks = new System.Collections.Generic.Dictionary<IntPtr, Rectangle> ();

		internal void SaveManagedPng (Stream stream)
		{
			var rgba = new byte [managedWidth * managedHeight * 4];
			for (int i = 0; i < managedPixels.Length; i++) {
				int v = managedPixels [i];
				rgba [i * 4] = (byte) (v >> 16); rgba [i * 4 + 1] = (byte) (v >> 8); rgba [i * 4 + 2] = (byte) v; rgba [i * 4 + 3] = (byte) (v >> 24);
			}
			ManagedPixels.EncodePng (stream, rgba, managedWidth, managedHeight);
		}
}
}
