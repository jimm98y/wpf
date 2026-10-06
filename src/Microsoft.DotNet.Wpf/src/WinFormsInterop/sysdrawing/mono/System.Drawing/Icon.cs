//
// System.Drawing.Icon.cs
//
// Authors:
//   Gary Barnett (gary.barnett.mono@gmail.com)
//   Dennis Hayes (dennish@Raytek.com)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//   Sanjay Gupta (gsanjay@novell.com)
//   Peter Dennis Bartok (pbartok@novell.com)
//   Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2002 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2004-2008 Novell, Inc (http://www.novell.com)
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
// An Icon as .NET's System.Drawing has it: the file's bytes, and the one entry of its directory it
// stands for, chosen by Windows' own rules (nearest size, then the deepest colour depth the display
// can show). Its pictures come from the managed ICO decoder (shared with WPF); nothing here asks
// GDI+. Handle is the OS's own HICON on Windows (CreateIconFromResourceEx, user32) and a managed
// token elsewhere (backend/WindowsImaging.cs), so FromHandle round-trips on every platform.
//

using System.Collections;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace System.Drawing
{
	[Serializable]
#if !MONOTOUCH
	[Editor ("System.Drawing.Design.IconEditor, " + Consts.AssemblySystem_Drawing_Design, typeof (System.Drawing.Design.UITypeEditor))]
#endif
	[TypeConverter(typeof(IconConverter))]

	public sealed class Icon : MarshalByRefObject, ISerializable, ICloneable, IDisposable
	{
		// What .NET compares an entry's depth against: a 32-bit display.
		const int DisplayBitDepth = 32;

		private byte [] iconData;
		private Size iconSize;
		private ManagedImageDecoder.IcoEntry best;
		private int bestBitDepth;
		private bool hasBest;

		private IntPtr handle = IntPtr.Zero;
		private bool ownHandle = true;
		private bool undisposable;
		private bool disposed;
		private Bitmap bitmap;   // GetInternalBitmap's cache

		private Icon ()
		{
		}

#if !MONOTOUCH
		private Icon (IntPtr handle)
		{
			this.handle = handle;
			ownHandle = false;
			if (OperatingSystem.IsWindows () && !WebGpuBackend.WindowsImaging.IsManagedHandle (handle) && !WebGpuBackend.WindowsImaging.IsIcon (handle))
				throw new ArgumentException (Locale.GetText ("Handle doesn't represent an ICON."));
			iconSize = WebGpuBackend.WindowsImaging.IconSize (handle);
		}
#endif

		public Icon (Icon original, int width, int height)
			: this (original, new Size (width, height))
		{
		}

		public Icon (Icon original, Size size)
		{
			if (original == null)
				throw new ArgumentNullException ("original");
			iconData = original.iconData;
			if (iconData != null) {
				Initialize (size.Width, size.Height);
			} else {
				iconSize = original.iconSize;
				bitmap = original.ToBitmap ();
			}
		}

		public Icon (Stream stream) : this (stream, 0, 0)
		{
		}

		public Icon (Stream stream, int width, int height)
		{
			InitFromStreamWithSize (stream, width, height);
		}

		public Icon (string fileName) : this (fileName, 0, 0)
		{
		}

		public Icon (Type type, string resource)
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
				InitFromStreamWithSize (s, 0, 0);
			}
		}

		private Icon (SerializationInfo info, StreamingContext context)
		{
			byte [] data = null;
			Size size = Size.Empty;
			foreach (SerializationEntry serEnum in info) {
				if (String.Compare(serEnum.Name, "IconData", true) == 0)
					data = (byte []) serEnum.Value;
				if (String.Compare(serEnum.Name, "IconSize", true) == 0)
					size = (Size) serEnum.Value;
			}
			if (data != null) {
				iconData = data;
				Initialize (size.Width, size.Height);
			}
		}

		internal Icon (string resourceName, bool undisposable)
		{
			using (Stream s = typeof (Icon).GetTypeInfo ().Assembly.GetManifestResourceStream (resourceName)) {
				if (s == null) {
					string msg = Locale.GetText ("Resource '{0}' was not found.", resourceName);
					throw new FileNotFoundException (msg);
				}
				InitFromStreamWithSize (s, 0, 0);
			}
			this.undisposable = true;
		}

		void ISerializable.GetObjectData(SerializationInfo si, StreamingContext context)
		{
			MemoryStream ms = new MemoryStream ();
			Save (ms);
			si.AddValue ("IconData", ms.ToArray ());
			si.AddValue ("IconSize", this.Size, typeof (Size));
		}

		public Icon (Stream stream, Size size) :
			this (stream, size.Width, size.Height)
		{
		}

		public Icon (string fileName, int width, int height)
		{
			using (FileStream fs = File.OpenRead (fileName)) {
				InitFromStreamWithSize (fs, width, height);
			}
		}

		public Icon (string fileName, Size size) : this (fileName, size.Width, size.Height)
		{
		}

		public static Icon ExtractAssociatedIcon (string filePath)
		{
			if (String.IsNullOrEmpty (filePath))
				throw new ArgumentException (Locale.GetText ("Null or empty path."), "filePath");
			if (!File.Exists (filePath))
				throw new FileNotFoundException (Locale.GetText ("Couldn't find specified file."), filePath);
			// Windows knows which icon a file shows (shell32); elsewhere there is no such association.
			if (OperatingSystem.IsWindows ()) {
				var path = new System.Text.StringBuilder (filePath, 260);
				ushort index = 0;
				IntPtr hicon = ExtractAssociatedIconW (IntPtr.Zero, path, ref index);
				if (hicon != IntPtr.Zero) {
					var icon = new Icon (hicon);
					icon.ownHandle = true;
					return icon;
				}
			}
			return (Icon) SystemIcons.WinLogo.Clone ();
		}

		[DllImport ("shell32.dll", CharSet = CharSet.Unicode)]
		static extern IntPtr ExtractAssociatedIconW (IntPtr hinst, System.Text.StringBuilder path, ref ushort index);

		public void Dispose ()
		{
			// SystemIcons requires this
			if (undisposable)
				return;

			if (!disposed) {
				if (handle != IntPtr.Zero && ownHandle)
					WebGpuBackend.WindowsImaging.DestroyIcon (handle);
				handle = IntPtr.Zero;
				if (bitmap != null) {
					bitmap.Dispose ();
					bitmap = null;
				}
				GC.SuppressFinalize (this);
			}
			disposed = true;
		}

		public object Clone ()
		{
			return new Icon (this, Size);
		}

#if !MONOTOUCH
		public static Icon FromHandle (IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				throw new ArgumentException ("handle");

			return new Icon (handle);
		}
#endif

		public void Save (Stream outputStream)
		{
			if (outputStream == null)
				throw new ArgumentNullException ("outputStream");
			// The file it was read from, every entry (.NET writes its icon data back verbatim).
			if (iconData != null) {
				outputStream.Write (iconData, 0, iconData.Length);
				return;
			}
			SaveBitmapAsIcon (outputStream, ToBitmap ());
		}

		// An icon made from a handle has no file: a single 32-bit entry, its alpha the shape.
		static void SaveBitmapAsIcon (Stream s, Bitmap bmp)
		{
			int w = bmp.Width, h = bmp.Height;
			int xorSize = w * 4 * h, andStride = ((w + 31) / 32) * 4, andSize = andStride * h;
			var w2 = new BinaryWriter (s);
			w2.Write ((ushort) 0); w2.Write ((ushort) 1); w2.Write ((ushort) 1);
			w2.Write ((byte) (w >= 256 ? 0 : w)); w2.Write ((byte) (h >= 256 ? 0 : h)); w2.Write ((byte) 0); w2.Write ((byte) 0);
			w2.Write ((ushort) 1); w2.Write ((ushort) 32); w2.Write ((uint) (40 + xorSize + andSize)); w2.Write ((uint) 22);
			w2.Write (40); w2.Write (w); w2.Write (h * 2); w2.Write ((ushort) 1); w2.Write ((ushort) 32);
			w2.Write (0); w2.Write (xorSize + andSize); w2.Write (0); w2.Write (0); w2.Write (0); w2.Write (0);
			uint[] argb = GdipPixels.ToArgb (bmp.Data.Frame, new Rectangle (0, 0, w, h));
			for (int y = h - 1; y >= 0; y--)
				for (int x = 0; x < w; x++) w2.Write (argb [y * w + x]);
			for (int y = h - 1; y >= 0; y--) {
				var row = new byte [andStride];
				for (int x = 0; x < w; x++) if ((argb [y * w + x] >> 24) == 0) row [x >> 3] |= (byte) (0x80 >> (x & 7));
				w2.Write (row);
			}
			w2.Flush ();
		}

#if !MONOTOUCH
		/// <summary>The picture Graphics.DrawIcon draws (ToBitmap's, kept).</summary>
		internal Bitmap GetInternalBitmap ()
		{
			if (bitmap == null)
				bitmap = ToBitmap ();
			return bitmap;
		}

		// note: all bitmaps are 32bits ARGB - no matter what the icon format (bitcount) was
		public Bitmap ToBitmap ()
		{
			if (disposed)
				throw new ObjectDisposedException (Locale.GetText ("Icon instance was disposed."));

			if (iconData != null && hasBest) {
				// A PNG entry is that PNG's pixels, in a plain 32bppArgb bitmap of its own.
				if (best.IsPng) {
					using (var ms = new MemoryStream (iconData, best.Offset, Math.Min (best.Size, iconData.Length - best.Offset)))
					using (var png = new Bitmap (ms)) {
						GdipFrame f = GdipPixels.Convert (png.Data.Frame, new Rectangle (0, 0, png.Width, png.Height), PixelFormat.Format32bppArgb, false);
						f.DpiX = f.DpiY = 96f;
						return new Bitmap (new GdipImageData (f) { Flags = GdipImageData.NewFlags (PixelFormat.Format32bppArgb) });
					}
				}
				// A 32-bit entry is copied, its alpha as it stands (.NET reads the bits itself, and an
				// entry whose alpha is all zero stays transparent).
				if (bestBitDepth == 32) {
					var bmp = new Bitmap (iconSize.Width, iconSize.Height, PixelFormat.Format32bppArgb);
					GdipFrame f = bmp.managed.Frame;
					int src = best.Offset + 40, line = iconSize.Width * 4;
					for (int y = 0; y < iconSize.Height; y++) {
						int from = src + (iconSize.Height - 1 - y) * line;
						if (from + line <= iconData.Length)
							Buffer.BlockCopy (iconData, from, f.Bits, y * f.Stride, line);
					}
					return bmp;
				}
				// Anything shallower is the icon drawn: its colours where the AND mask says opaque.
				byte [] bgra = ManagedImageDecoder.DecodeIcoEntry (iconData, best, out int w, out int h, out _);
				for (int i = 0; i + 3 < bgra.Length; i += 4) if (bgra [i + 3] == 0) bgra [i] = bgra [i + 1] = bgra [i + 2] = 0;
				var drawn = new Bitmap (iconSize.Width, iconSize.Height, PixelFormat.Format32bppArgb);
				GdipFrame d = drawn.managed.Frame;
				var row = new uint [iconSize.Width];
				for (int y = 0; y < iconSize.Height; y++) {
					for (int x = 0; x < iconSize.Width; x++) {
						int sx = w == iconSize.Width ? x : x * w / iconSize.Width, sy = h == iconSize.Height ? y : y * h / iconSize.Height;
						int o = (sy * w + sx) * 4;
						row [x] = (uint) bgra [o + 3] << 24 | (uint) bgra [o + 2] << 16 | (uint) bgra [o + 1] << 8 | bgra [o];
					}
					GdipPixels.WriteArgb (d, 0, y, iconSize.Width, row, 0);
				}
				return drawn;
			}
			if (bitmap != null)
				return (Bitmap) bitmap.Clone ();
			if (handle != IntPtr.Zero)
				return Bitmap.FromHicon (handle);
			return new Bitmap (32, 32);
		}
#endif
		public override string ToString ()
		{
			//is this correct, this is what returned by .Net
			return "<Icon>";
		}

#if !MONOTOUCH
		[Browsable (false)]
		public IntPtr Handle {
			get {
				if (disposed)
					throw new ObjectDisposedException (Locale.GetText ("Icon instance was disposed."));
				if (handle == IntPtr.Zero) {
					if (OperatingSystem.IsWindows () && iconData != null && hasBest) {
						// The OS reads the entry itself, as .NET hands it CreateIconFromResourceEx.
						var entry = new byte [Math.Min (best.Size, iconData.Length - best.Offset)];
						Buffer.BlockCopy (iconData, best.Offset, entry, 0, entry.Length);
						handle = CreateIconFromResourceEx (entry, entry.Length, true, 0x00030000, iconSize.Width, iconSize.Height, 0);
					}
					if (handle == IntPtr.Zero)
						using (Bitmap b = ToBitmap ())
							handle = b.GetHicon ();
					ownHandle = true;
				}
				return handle;
			}
		}

		[DllImport ("user32.dll")]
		static extern IntPtr CreateIconFromResourceEx (byte [] bits, int size, bool icon, int version, int cx, int cy, int flags);
#endif
		[Browsable (false)]
		public int Height {
			get {
				return iconSize.Height;
			}
		}

		public Size Size {
			get {
				return iconSize;
			}
		}

		[Browsable (false)]
		public int Width {
			get {
				return iconSize.Width;
			}
		}

		~Icon ()
		{
			Dispose ();
		}

		private void InitFromStreamWithSize (Stream stream, int width, int height)
		{
			if (stream == null)
				throw new ArgumentException ("The argument 'stream' must be a picture that can be used as a Icon", "stream");
			iconData = Image.ReadAll (stream);
			Initialize (width, height);
		}

		// .NET's Icon.Initialize: Windows' rules for picking an icon's image --
		//  1. the closest size;  2. of those, the deepest colour depth not exceeding the display's;
		//  3. if all exceed it, the shallowest. Depths past 8bpp all count as what they say.
		private void Initialize (int width, int height)
		{
			if (iconData.Length < 6 || iconData [0] != 0 || iconData [1] != 0 || iconData [2] != 1 || iconData [3] != 0)
				throw new ArgumentException ("The argument 'picture' must be a picture that can be used as a Icon", "picture");
			// Zero asks for the system's icon size.
			if (width == 0) width = 32;
			if (height == 0) height = 32;
			ManagedImageDecoder.IcoEntry [] entries;
			try {
				entries = ManagedImageDecoder.ReadIcoDirectory (iconData);
			} catch (InvalidDataException) {
				throw new ArgumentException ("The argument 'picture' must be a picture that can be used as a Icon", "picture");
			}
			hasBest = false;
			foreach (var entry in entries) {
				int depth;
				if (entry.ColorCount != 0) {
					depth = 4;
					if (entry.ColorCount < 0x10) depth = 1;
				} else {
					depth = entry.BitCount;
				}
				if (depth == 0) depth = 8;
				bool update;
				if (!hasBest) update = true;
				else {
					int bestDelta = Math.Abs (best.Width - width) + Math.Abs (best.Height - height);
					int thisDelta = Math.Abs (entry.Width - width) + Math.Abs (entry.Height - height);
					update = thisDelta < bestDelta
						|| (thisDelta == bestDelta && ((depth <= DisplayBitDepth && depth > bestBitDepth) || (bestBitDepth > DisplayBitDepth && depth < bestBitDepth)));
				}
				if (update) {
					best = entry;
					bestBitDepth = depth;
					hasBest = true;
				}
			}
			if (!hasBest)
				throw new Win32Exception (0, "No valid icon entry were found.");
			// What the entry really is: a PNG, or a DIB whose header says its depth.
			if (best.IsPng) bestBitDepth = 32;
			else if (best.Offset + 16 <= iconData.Length) {
				int bits = iconData [best.Offset + 14] | (iconData [best.Offset + 15] << 8);
				if (bits == 32) bestBitDepth = 32;
				else if (bestBitDepth == 32) bestBitDepth = bits;
			}
			iconSize = new Size (best.Width, best.Height);
		}
	}
}
