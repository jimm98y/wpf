//
// System.Drawing.Region.cs
//
// Author:
//	Miguel de Icaza (miguel@ximian.com)
//      Jordi Mas i Hernandez (jordi@ximian.com)
//
// Copyright (C) 2003 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2004,2006 Novell, Inc. http://www.novell.com
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
// A MANAGED region with GDI+'s semantics: the GpRegion tree (WebGpuBackend.Gdip.GpRegion) --
// rectangles and paths kept in the coordinates they were given in, combined as nodes, rasterized to
// device rectangles only when asked (GetRegionScans, IsVisible, GetBounds, drawing) and then through
// GDI+'s own rules. Region data is GDI+'s RegionData serialization of that tree.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing
{
	public sealed class Region : MarshalByRefObject, IDisposable
	{
		internal GpRegion gp;

		public Region ()
		{
			gp = GpRegion.Infinite ();
		}

		public Region (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			gp = FromPath (path);
		}

		public Region (Rectangle rect)
		{
			gp = GpRegion.FromRect (rect);
		}

		public Region (RectangleF rect)
		{
			gp = GpRegion.FromRect (rect);
		}

		public Region (RegionData rgnData)
		{
			if (rgnData == null)
				throw new ArgumentNullException ("rgnData");
			// a NullReferenceException can be throw for rgnData.Data.Length (if rgnData.Data is null) just like MS
			if (rgnData.Data.Length == 0)
				throw new ArgumentException ("rgnData");
			gp = Deserialize (rgnData.Data);
		}

		internal Region (GpRegion region)
		{
			gp = region;
		}

		static GpRegion FromPath (GraphicsPath path)
		{
			PointF [] pts = path.PointCount == 0 ? new PointF [0] : path.PathPoints;
			byte [] types = path.PointCount == 0 ? new byte [0] : path.PathTypes;
			return GpRegion.FromPath (pts, types, path.FillMode);
		}

		// ---- combining --------------------------------------------------------------------------

		void Combine (GpRegion other, CombineMode mode) => gp.Combine (other, mode);

		public void Union (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Combine (FromPath (path), CombineMode.Union);
		}

		public void Union (Rectangle rect) => Combine (GpRegion.FromRect (rect), CombineMode.Union);

		public void Union (RectangleF rect) => Combine (GpRegion.FromRect (rect), CombineMode.Union);

		public void Union (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Combine (region.gp, CombineMode.Union);
		}

		public void Intersect (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Combine (FromPath (path), CombineMode.Intersect);
		}

		public void Intersect (Rectangle rect) => Combine (GpRegion.FromRect (rect), CombineMode.Intersect);

		public void Intersect (RectangleF rect) => Combine (GpRegion.FromRect (rect), CombineMode.Intersect);

		public void Intersect (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Combine (region.gp, CombineMode.Intersect);
		}

		public void Complement (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Combine (FromPath (path), CombineMode.Complement);
		}

		public void Complement (Rectangle rect) => Combine (GpRegion.FromRect (rect), CombineMode.Complement);

		public void Complement (RectangleF rect) => Combine (GpRegion.FromRect (rect), CombineMode.Complement);

		public void Complement (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Combine (region.gp, CombineMode.Complement);
		}

		public void Exclude (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Combine (FromPath (path), CombineMode.Exclude);
		}

		public void Exclude (Rectangle rect) => Combine (GpRegion.FromRect (rect), CombineMode.Exclude);

		public void Exclude (RectangleF rect) => Combine (GpRegion.FromRect (rect), CombineMode.Exclude);

		public void Exclude (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Combine (region.gp, CombineMode.Exclude);
		}

		public void Xor (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Combine (FromPath (path), CombineMode.Xor);
		}

		public void Xor (Rectangle rect) => Combine (GpRegion.FromRect (rect), CombineMode.Xor);

		public void Xor (RectangleF rect) => Combine (GpRegion.FromRect (rect), CombineMode.Xor);

		public void Xor (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Combine (region.gp, CombineMode.Xor);
		}

		// ---- queries ----------------------------------------------------------------------------

		static GpMatrix ToDevice (Graphics g) => g == null ? GpMatrix.CreateIdentity () : g.RegionWorldToDevice ();

		/// <summary>GpRegion::GetBounds: a rectangle's own, a path's, the infinite and empty regions'
		/// fixed answers; a combination's device bounds brought back to world space.</summary>
		public RectangleF GetBounds (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			return Bounds (ToDevice (g));
		}

		internal RectangleF Bounds () => Bounds (GpMatrix.CreateIdentity ());

		RectangleF Bounds (GpMatrix toDevice)
		{
			switch (gp.Type) {
			case GpRegion.NodeRect:
				return gp.Rect;
			case GpRegion.NodePath:
				return PathBounds (gp.Points);
			case GpRegion.NodeInfinite:
				return new RectangleF (-4194304f, -4194304f, 8388608f, 8388608f);
			case GpRegion.NodeEmpty:
				return RectangleF.Empty;
			default: {
				Rectangle b = gp.Device (toDevice).Bounds;
				GpMatrix inv = toDevice;
				if (!inv.Invert ()) return RectangleF.Empty;
				return TransformBounds (inv, b.Left, b.Top, b.Right, b.Bottom);
			}
			}
		}

		static RectangleF PathBounds (PointF [] p)
		{
			if (p == null || p.Length == 0) return RectangleF.Empty;
			float l = p [0].X, t = p [0].Y, r = l, b = t;
			foreach (PointF q in p) { l = Math.Min (l, q.X); t = Math.Min (t, q.Y); r = Math.Max (r, q.X); b = Math.Max (b, q.Y); }
			return RectangleF.FromLTRB (l, t, r, b);
		}

		static RectangleF TransformBounds (GpMatrix m, float l, float t, float r, float b)
		{
			var p = new [] { new PointF (l, t), new PointF (r, t), new PointF (r, b), new PointF (l, b) };
			m.Transform (p);
			return PathBounds (p);
		}

		public void Translate (int dx, int dy) => Translate ((float) dx, (float) dy);

		public void Translate (float dx, float dy) => gp.Offset (dx, dy);

		public bool IsVisible (int x, int y, Graphics g) => IsVisible ((float) x, (float) y, g);

		public bool IsVisible (int x, int y, int width, int height) => IsVisible ((float) x, (float) y, (float) width, (float) height, null);

		public bool IsVisible (int x, int y, int width, int height, Graphics g) => IsVisible ((float) x, (float) y, (float) width, (float) height, g);

		public bool IsVisible (Point point) => IsVisible ((float) point.X, (float) point.Y, null);

		public bool IsVisible (PointF point) => IsVisible (point.X, point.Y, null);

		public bool IsVisible (Point point, Graphics g) => IsVisible ((float) point.X, (float) point.Y, g);

		public bool IsVisible (PointF point, Graphics g) => IsVisible (point.X, point.Y, g);

		public bool IsVisible (Rectangle rect) => IsVisible ((float) rect.X, rect.Y, rect.Width, rect.Height, null);

		public bool IsVisible (RectangleF rect) => IsVisible (rect.X, rect.Y, rect.Width, rect.Height, null);

		public bool IsVisible (Rectangle rect, Graphics g) => IsVisible ((float) rect.X, rect.Y, rect.Width, rect.Height, g);

		public bool IsVisible (RectangleF rect, Graphics g) => IsVisible (rect.X, rect.Y, rect.Width, rect.Height, g);

		public bool IsVisible (float x, float y) => IsVisible (x, y, null);

		/// <summary>GpRegion::IsVisible(PointF): the device region, the point transformed and
		/// truncated after adding a half.</summary>
		public bool IsVisible (float x, float y, Graphics g)
		{
			GpMatrix m = ToDevice (g);
			DpRegion d = gp.Device (m);
			m.Transform (ref x, ref y);
			return d.Contains ((int) MathF.Floor (x + 0.5f), (int) MathF.Floor (y + 0.5f));
		}

		public bool IsVisible (float x, float y, float width, float height) => IsVisible (x, y, width, height, null);

		/// <summary>GpRegion::IsVisible(RectF): an axis-aligned device rectangle, its edges rounded
		/// up (the binary's -(int)-(v) with the Misc optimisation off), tested against the device
		/// region; under a rotation the rectangle as a region.</summary>
		public bool IsVisible (float x, float y, float width, float height, Graphics g)
		{
			GpMatrix m = ToDevice (g);
			DpRegion d = gp.Device (m);
			if (m.IsTranslateScale) {
				float x0 = x, y0 = y, x1 = x + width, y1 = y + height;
				m.Transform (ref x0, ref y0);
				m.Transform (ref x1, ref y1);
				float l = Math.Min (x0, x1), t = Math.Min (y0, y1), w = Math.Abs (x1 - x0), h = Math.Abs (y1 - y0);
				int il = Ceil (l), it = Ceil (t), iw = Ceil (w), ih = Ceil (h);
				return iw > 0 && ih > 0 && d.Intersects (new Rectangle (il, it, iw, ih));
			}
			var rect = GpRegion.FromRect (new RectangleF (x, y, width, height));
			DpRegion r = rect.Device (m);
			return !DpRegion.Combine (d, r, DpRegion.Op.And).IsEmpty;
		}

		static int Ceil (float v) => -(int) Math.Floor (-(double) v);   // frintm of the negation

		public bool IsVisible (float x, float y, float width, float height, Graphics g, bool unused) => IsVisible (x, y, width, height, g);

		public bool IsEmpty (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			if (gp.Type == GpRegion.NodeEmpty) return true;
			return gp.Device (ToDevice (g)).IsEmpty;
		}

		public bool IsInfinite (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			if (gp.Type == GpRegion.NodeInfinite) return true;
			return gp.Device (ToDevice (g)).IsInfinite;
		}

		internal bool IsInfiniteInternal => gp.Type == GpRegion.NodeInfinite;

		public void MakeEmpty () => gp.MakeEmpty ();

		public void MakeInfinite () => gp.MakeInfinite ();

		public bool Equals (Region region, Graphics g)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			if (g == null)
				throw new ArgumentNullException ("g");
			GpMatrix m = ToDevice (g);
			return SameBands (gp.Device (m), region.gp.Device (m));
		}

		static bool SameBands (DpRegion a, DpRegion b)
		{
			if (a.Bands.Count != b.Bands.Count) return false;
			for (int i = 0; i < a.Bands.Count; i++) {
				var x = a.Bands [i]; var y = b.Bands [i];
				if (x.Top != y.Top || x.Bottom != y.Bottom || x.X.Length != y.X.Length) return false;
				for (int k = 0; k < x.X.Length; k++) if (x.X [k] != y.X [k]) return false;
			}
			return true;
		}

		public static Region FromHrgn (IntPtr hrgn)
		{
			if (hrgn == IntPtr.Zero)
				throw new ArgumentException ("hrgn");
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HRGN is a Windows GDI object.");
			// RGNDATA: a 32-byte header (dwSize, iType, nCount, nRgnSize, rcBound), then nCount RECTs --
			// GDI+ makes a path of them (GpPath(HRGN)), one rectangle figure each.
			int size = GetRegionData (hrgn, 0, IntPtr.Zero);
			if (size <= 0) return new Region (GpRegion.Empty ());
			IntPtr buf = Marshal.AllocHGlobal (size);
			try {
				if (GetRegionData (hrgn, size, buf) == 0)
					throw new ArgumentException ("hrgn");
				int count = Marshal.ReadInt32 (buf, 8);
				if (count == 0) return new Region (GpRegion.Empty ());
				if (count == 1) {
					int l = Marshal.ReadInt32 (buf, 32), t = Marshal.ReadInt32 (buf, 36), r = Marshal.ReadInt32 (buf, 40), b = Marshal.ReadInt32 (buf, 44);
					return new Region (GpRegion.FromRect (RectangleF.FromLTRB (l, t, r, b)));
				}
				var pts = new PointF [count * 4];
				var types = new byte [count * 4];
				for (int i = 0; i < count; i++) {
					IntPtr r = buf + 32 + i * 16;
					int l = Marshal.ReadInt32 (r), t = Marshal.ReadInt32 (r, 4), rr = Marshal.ReadInt32 (r, 8), b = Marshal.ReadInt32 (r, 12);
					pts [i * 4] = new PointF (l, t); pts [i * 4 + 1] = new PointF (rr, t);
					pts [i * 4 + 2] = new PointF (rr, b); pts [i * 4 + 3] = new PointF (l, b);
					types [i * 4] = 0; types [i * 4 + 1] = 1; types [i * 4 + 2] = 1; types [i * 4 + 3] = 0x81;
				}
				return new Region (GpRegion.FromPath (pts, types, FillMode.Alternate));
			} finally {
				Marshal.FreeHGlobal (buf);
			}
		}

		public IntPtr GetHrgn (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			// An HRGN exists only on Windows: the OS's own region object, built from these rectangles.
			if (!OperatingSystem.IsWindows ())
				return IntPtr.Zero;
			if (IsInfinite (g))
				return IntPtr.Zero;   // GDI+ hands back NULL for the infinite region
			IntPtr rgn = CreateRectRgn (0, 0, 0, 0);
			foreach (Rectangle r in gp.Device (ToDevice (g)).Rects ()) {
				IntPtr part = CreateRectRgn (r.Left, r.Top, r.Right, r.Bottom);
				CombineRgn (rgn, rgn, part, 2 /* RGN_OR */);
				DeleteObject (part);
			}
			return rgn;
		}

		// ---- RegionData -------------------------------------------------------------------------
		//
		// GpRegion::GetData @1800738c0: the version 0xDBC01002 and the number of combine nodes, then the
		// tree depth first -- each node its type, a rectangle its four floats, a path its size and the
		// path's own serialization (GpPath::GetData). Behind a size and a CRC-32 of what follows them.

		const int RegionVersion = unchecked ((int) 0xDBC01002);

		public RegionData GetRegionData ()
		{
			var body = new MemoryStream ();
			var w = new BinaryWriter (body);
			w.Write (RegionVersion);
						// GpRegion +0xa8: the child nodes, two per combine.
			w.Write (2 * CountCombines (gp));
			WriteNode (w, gp);
			w.Flush ();
			byte [] payload = body.ToArray ();
			var all = new byte [payload.Length + 8];
						// GpObject::GetExternalData: the size of what follows the eight-byte header, and Crc32
			// @1802268c0 of it (table CRC, initial 0, no final inversion).
			BitConverter.GetBytes (payload.Length).CopyTo (all, 0);
			BitConverter.GetBytes (Crc (payload)).CopyTo (all, 4);
			payload.CopyTo (all, 8);
			return new RegionData (all);
		}

		static int CountCombines (GpRegion r) => r.IsLeaf ? 0 : 1 + CountCombines (r.Left) + CountCombines (r.Right);

		static void WriteNode (BinaryWriter w, GpRegion r)
		{
			w.Write (r.Type);
			switch (r.Type) {
			case GpRegion.NodeRect:
				w.Write (r.Rect.X); w.Write (r.Rect.Y); w.Write (r.Rect.Width); w.Write (r.Rect.Height);
				break;
			case GpRegion.NodePath: {
				byte [] p = GraphicsPathData.Serialize (r.Points, r.Types, r.Fill);
				w.Write (p.Length);
				w.Write (p);
				break;
			}
			case GpRegion.NodeEmpty: case GpRegion.NodeInfinite:
				break;
			default:
				WriteNode (w, r.Left);
				WriteNode (w, r.Right);
				break;
			}
		}

		static uint Crc (byte [] data)
		{
						uint c = 0;
			foreach (byte b in data) {
				uint t = (c ^ b) & 0xff;
				for (int k = 0; k < 8; k++) t = (t & 1) != 0 ? 0xedb88320u ^ (t >> 1) : t >> 1;
				c = t ^ (c >> 8);
			}
			return c;
		}

		static GpRegion Deserialize (byte [] data)
		{
			if (data.Length < 16) throw new ArgumentException ("Parameter is not valid.");
			int pos = 16;   // size, checksum, version, combine count
			return ReadNode (data, ref pos);
		}

		static GpRegion ReadNode (byte [] d, ref int pos)
		{
			if (pos + 4 > d.Length) throw new ArgumentException ("Parameter is not valid.");
			int type = BitConverter.ToInt32 (d, pos);
			pos += 4;
			switch (type) {
			case GpRegion.NodeRect: {
				if (pos + 16 > d.Length) throw new ArgumentException ("Parameter is not valid.");
				var r = new RectangleF (BitConverter.ToSingle (d, pos), BitConverter.ToSingle (d, pos + 4), BitConverter.ToSingle (d, pos + 8), BitConverter.ToSingle (d, pos + 12));
				pos += 16;
				return GpRegion.FromRect (r);
			}
			case GpRegion.NodePath: {
				int size = BitConverter.ToInt32 (d, pos);
				pos += 4;
				GraphicsPathData.Deserialize (d, pos, size, out PointF [] pts, out byte [] types, out FillMode fill);
				pos += size;
				return GpRegion.FromPath (pts, types, fill);
			}
			case GpRegion.NodeEmpty: return GpRegion.Empty ();
			case GpRegion.NodeInfinite: return GpRegion.Infinite ();
			default: {
				if (type < 1 || type > 5) throw new ArgumentException ("Parameter is not valid.");
				GpRegion left = ReadNode (d, ref pos), right = ReadNode (d, ref pos);
				return new GpRegion { Type = type, Left = left, Right = right };
			}
			}
		}

		// ---- device rectangles ------------------------------------------------------------------

		public RectangleF [] GetRegionScans (Matrix matrix)
		{
			GpMatrix m = matrix == null ? GpMatrix.CreateIdentity () : matrix.Gp;
			var scans = new List<RectangleF> ();
			foreach (Rectangle r in gp.Device (m).Rects ()) scans.Add (r);
			return scans.ToArray ();
		}

		public void Transform (Matrix matrix)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			gp.Transform (matrix.Gp);
		}

		public Region Clone () => new Region (gp.Clone ());

		public void Dispose ()
		{
			System.GC.SuppressFinalize (this);
		}

		// No GDI+ object: kept for the code that still passes one around (always zero).
		internal IntPtr NativeObject
		{
			get { return IntPtr.Zero; }
			set { }
		}

		// why is this a instance method ? and not static ?
		public void ReleaseHrgn (IntPtr regionHandle)
		{
			if (regionHandle == IntPtr.Zero)
				throw new ArgumentNullException ("regionHandle");
			if (OperatingSystem.IsWindows ())
				DeleteObject (regionHandle);
		}

		[DllImport ("gdi32.dll")]
		static extern IntPtr CreateRectRgn (int l, int t, int r, int b);
		[DllImport ("gdi32.dll")]
		static extern int CombineRgn (IntPtr dst, IntPtr a, IntPtr b, int mode);
		[DllImport ("gdi32.dll")]
		static extern bool DeleteObject (IntPtr h);
		[DllImport ("gdi32.dll")]
		static extern int GetRegionData (IntPtr hrgn, int count, IntPtr data);
	}
}
