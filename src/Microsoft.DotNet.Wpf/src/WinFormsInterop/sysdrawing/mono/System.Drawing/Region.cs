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
// A MANAGED region: no GDI+ object. A region is a set of non-overlapping rectangles kept in GDI's
// canonical band form -- rows of equal height, each a sorted list of x spans, adjacent identical rows
// merged -- which is also exactly what GetRegionScans hands out. Rectangles combine exactly (in
// floats, as GDI+ keeps them); a path becomes the rectangles of the pixels whose centres it covers
// (its fill mode honoured), which is the region GDI+ scans for it. The infinite region is GDI+'s own:
// the rectangle (-4194304, -4194304) of side 8388608.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace System.Drawing
{
	public sealed class Region : MarshalByRefObject, IDisposable
	{
		const float InfiniteOrigin = -4194304f, InfiniteSize = 8388608f;
		static readonly RectangleF InfiniteRect = new RectangleF (InfiniteOrigin, InfiniteOrigin, InfiniteSize, InfiniteSize);

		// The bands: each a [Top, Bottom) row with sorted, disjoint [Left, Right) spans.
		sealed class Band
		{
			public float Top, Bottom;
			public List<float> X = new List<float> ();   // left0, right0, left1, right1, ...
		}

		List<Band> bands = new List<Band> ();

		public Region()
		{
			MakeInfinite ();
		}

		public Region (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			bands = FromPath (path).bands;
		}

		public Region (Rectangle rect)
		{
			bands = FromRect (rect).bands;
		}

		public Region (RectangleF rect)
		{
			bands = FromRect (rect).bands;
		}

		public Region (RegionData rgnData)
		{
			if (rgnData == null)
				throw new ArgumentNullException ("rgnData");
			// a NullReferenceException can be throw for rgnData.Data.Length (if rgnData.Data is null) just like MS
			if (rgnData.Data.Length == 0)
				throw new ArgumentException ("rgnData");
			bands = Deserialize (rgnData.Data).bands;
		}

		Region (List<Band> b)
		{
			bands = b;
		}

		// ---- building ---------------------------------------------------------------------------

		static Region FromRect (RectangleF r)
		{
			var region = new Region (new List<Band> ());
			if (r.Width > 0 && r.Height > 0) {
				var b = new Band { Top = r.Top, Bottom = r.Bottom };
				b.X.Add (r.Left); b.X.Add (r.Right);
				region.bands.Add (b);
			}
			return region;
		}

		// The pixels whose centres the path's fill covers, as rows of spans.
		static Region FromPath (GraphicsPath path)
		{
			var region = new Region (new List<Band> ());
			PointF [] pts;
			byte [] types;
			FillMode mode;
			using (var flat = (GraphicsPath) path.Clone ()) {
				flat.Flatten (null, 0.25f);
				if (flat.PointCount == 0) return region;
				pts = flat.PathPoints;
				types = flat.PathTypes;
				mode = flat.FillMode;
			}
			// Subpaths, each closed.
			var contours = new List<List<PointF>> ();
			List<PointF> cur = null;
			for (int i = 0; i < pts.Length; i++) {
				if ((types [i] & 7) == 0 || cur == null) { cur = new List<PointF> (); contours.Add (cur); }
				cur.Add (pts [i]);
				if ((types [i] & 0x80) != 0) cur = null;
			}
			float ymin = float.MaxValue, ymax = float.MinValue;
			foreach (PointF p in pts) { ymin = Math.Min (ymin, p.Y); ymax = Math.Max (ymax, p.Y); }
			var xs = new List<(float X, int Dir)> ();
			for (int y = (int) Math.Ceiling (ymin - 0.5f); y + 0.5f < ymax; y++) {
				float sy = y + 0.5f;
				xs.Clear ();
				foreach (List<PointF> c in contours) {
					int n = c.Count;
					if (n < 2) continue;
					for (int i = 0; i < n; i++) {
						PointF a = c [i], b = c [(i + 1) % n];
						if (a.Y == b.Y) continue;
						int dir = b.Y > a.Y ? 1 : -1;
						float y0 = Math.Min (a.Y, b.Y), y1 = Math.Max (a.Y, b.Y);
						if (sy < y0 || sy >= y1) continue;
						xs.Add ((a.X + (sy - a.Y) * (b.X - a.X) / (b.Y - a.Y), dir));
					}
				}
				if (xs.Count < 2) continue;
				xs.Sort ((p, q) => p.X.CompareTo (q.X));
				var band = new Band { Top = y, Bottom = y + 1 };
				int wind = 0;
				for (int k = 0; k < xs.Count - 1; k++) {
					wind += mode == FillMode.Winding ? xs [k].Dir : 1;
					bool inside = mode == FillMode.Winding ? wind != 0 : (wind & 1) != 0;
					if (!inside) continue;
					int x0 = (int) Math.Ceiling (xs [k].X - 0.5f), x1 = (int) Math.Ceiling (xs [k + 1].X - 0.5f);
					if (x1 > x0) AddSpan (band.X, x0, x1);
				}
				if (band.X.Count > 0) region.bands.Add (band);
			}
			region.Canonicalize ();
			return region;
		}

		// Appends a span to a row being built left to right, merging it into the last when they touch.
		static void AddSpan (List<float> xs, float l, float r)
		{
			if (r <= l) return;
			int n = xs.Count;
			if (n >= 2 && l <= xs [n - 1]) { xs [n - 1] = Math.Max (xs [n - 1], r); return; }
			xs.Add (l); xs.Add (r);
		}

		// Merges vertically adjacent bands whose spans are identical, and drops empty ones.
		void Canonicalize ()
		{
			var outBands = new List<Band> ();
			foreach (Band b in bands) {
				if (b.X.Count == 0 || b.Bottom <= b.Top) continue;
				if (outBands.Count > 0) {
					Band last = outBands [outBands.Count - 1];
					if (last.Bottom == b.Top && SameSpans (last.X, b.X)) { last.Bottom = b.Bottom; continue; }
				}
				outBands.Add (b);
			}
			bands = outBands;
		}

		static bool SameSpans (List<float> a, List<float> b)
		{
			if (a.Count != b.Count) return false;
			for (int i = 0; i < a.Count; i++) if (a [i] != b [i]) return false;
			return true;
		}

		// ---- the algebra -----------------------------------------------------------------------

		enum Op { Union, Intersect, Xor, Exclude }

		// The spans of a region at a row inside one of its bands (or none).
		static List<float> SpansAt (List<Band> bs, float top, float bottom)
		{
			foreach (Band b in bs)
				if (b.Top <= top && b.Bottom >= bottom) return b.X;
			return null;
		}

		static List<Band> Combine (List<Band> a, List<Band> b, Op op)
		{
			var edges = new SortedSet<float> ();
			foreach (Band x in a) { edges.Add (x.Top); edges.Add (x.Bottom); }
			foreach (Band x in b) { edges.Add (x.Top); edges.Add (x.Bottom); }
			var result = new List<Band> ();
			float? prev = null;
			foreach (float e in edges) {
				if (prev is float top) {
					List<float> sa = SpansAt (a, top, e), sb = SpansAt (b, top, e);
					List<float> spans = CombineSpans (sa, sb, op);
					if (spans.Count > 0) {
						var band = new Band { Top = top, Bottom = e };
						band.X = spans;
						result.Add (band);
					}
				}
				prev = e;
			}
			var r = new Region (result);
			r.Canonicalize ();
			return r.bands;
		}

		static List<float> CombineSpans (List<float> a, List<float> b, Op op)
		{
			var xs = new SortedSet<float> ();
			if (a != null) foreach (float x in a) xs.Add (x);
			if (b != null) foreach (float x in b) xs.Add (x);
			var outSpans = new List<float> ();
			float? prev = null;
			foreach (float x in xs) {
				if (prev is float l) {
					float mid = l + (x - l) / 2f;
					bool ia = In (a, mid), ib = In (b, mid);
					bool keep = op switch {
						Op.Union => ia || ib,
						Op.Intersect => ia && ib,
						Op.Xor => ia != ib,
						_ => ia && !ib,
					};
					if (keep) AddSpan (outSpans, l, x);
				}
				prev = x;
			}
			return outSpans;
		}

		static bool In (List<float> spans, float x)
		{
			if (spans == null) return false;
			for (int i = 0; i + 1 < spans.Count; i += 2)
				if (x >= spans [i] && x < spans [i + 1]) return true;
			return false;
		}

		void Apply (Region other, Op op) => bands = Combine (bands, other.bands, op);

		// Complement: the part of the OTHER region not in this one.
		void ApplyComplement (Region other) => bands = Combine (other.bands, bands, Op.Exclude);

		//
		// Union
		//

		public void Union (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Apply (FromPath (path), Op.Union);
		}

		public void Union (Rectangle rect) => Apply (FromRect (rect), Op.Union);

		public void Union (RectangleF rect) => Apply (FromRect (rect), Op.Union);

		public void Union (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Apply (region, Op.Union);
		}

		//
		// Intersect
		//
		public void Intersect (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Apply (FromPath (path), Op.Intersect);
		}

		public void Intersect (Rectangle rect) => Apply (FromRect (rect), Op.Intersect);

		public void Intersect (RectangleF rect) => Apply (FromRect (rect), Op.Intersect);

		public void Intersect (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Apply (region, Op.Intersect);
		}

		//
		// Complement
		//
		public void Complement (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			ApplyComplement (FromPath (path));
		}

		public void Complement (Rectangle rect) => ApplyComplement (FromRect (rect));

		public void Complement (RectangleF rect) => ApplyComplement (FromRect (rect));

		public void Complement (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			ApplyComplement (region);
		}

		//
		// Exclude
		//
		public void Exclude (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Apply (FromPath (path), Op.Exclude);
		}

		public void Exclude (Rectangle rect) => Apply (FromRect (rect), Op.Exclude);

		public void Exclude (RectangleF rect) => Apply (FromRect (rect), Op.Exclude);

		public void Exclude (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Apply (region, Op.Exclude);
		}

		//
		// Xor
		//
		public void Xor (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			Apply (FromPath (path), Op.Xor);
		}

		public void Xor (Rectangle rect) => Apply (FromRect (rect), Op.Xor);

		public void Xor (RectangleF rect) => Apply (FromRect (rect), Op.Xor);

		public void Xor (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			Apply (region, Op.Xor);
		}

		//
		// GetBounds
		//
		public RectangleF GetBounds (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			return Bounds ();
		}

		internal RectangleF Bounds ()
		{
			if (bands.Count == 0) return RectangleF.Empty;
			float l = float.MaxValue, r = float.MinValue;
			foreach (Band b in bands) { l = Math.Min (l, b.X [0]); r = Math.Max (r, b.X [b.X.Count - 1]); }
			return RectangleF.FromLTRB (l, bands [0].Top, r, bands [bands.Count - 1].Bottom);
		}

		//
		// Translate
		//
		public void Translate (int dx, int dy) => Translate ((float) dx, (float) dy);

		public void Translate (float dx, float dy)
		{
			if (IsInfiniteInternal) return;
			foreach (Band b in bands) {
				b.Top += dy; b.Bottom += dy;
				for (int i = 0; i < b.X.Count; i++) b.X [i] += dx;
			}
		}

		//
		// IsVisible
		//
		public bool IsVisible (int x, int y, Graphics g) => IsVisible ((float) x, (float) y);

		public bool IsVisible (int x, int y, int width, int height) => IsVisible ((float) x, (float) y, (float) width, (float) height);

		public bool IsVisible (int x, int y, int width, int height, Graphics g) => IsVisible ((float) x, (float) y, (float) width, (float) height);

		public bool IsVisible (Point point) => IsVisible ((float) point.X, (float) point.Y);

		public bool IsVisible (PointF point) => IsVisible (point.X, point.Y);

		public bool IsVisible (Point point, Graphics g) => IsVisible ((float) point.X, (float) point.Y);

		public bool IsVisible (PointF point, Graphics g) => IsVisible (point.X, point.Y);

		public bool IsVisible (Rectangle rect) => IsVisible ((float) rect.X, rect.Y, rect.Width, rect.Height);

		public bool IsVisible (RectangleF rect) => IsVisible (rect.X, rect.Y, rect.Width, rect.Height);

		public bool IsVisible (Rectangle rect, Graphics g) => IsVisible ((float) rect.X, rect.Y, rect.Width, rect.Height);

		public bool IsVisible (RectangleF rect, Graphics g) => IsVisible (rect.X, rect.Y, rect.Width, rect.Height);

		public bool IsVisible (float x, float y)
		{
			foreach (Band b in bands)
				if (y >= b.Top && y < b.Bottom && In (b.X, x)) return true;
			return false;
		}

		public bool IsVisible (float x, float y, Graphics g) => IsVisible (x, y);

		// Whether any part of the rectangle is in the region.
		public bool IsVisible (float x, float y, float width, float height)
		{
			if (width <= 0 || height <= 0) return false;
			float r = x + width, bottom = y + height;
			foreach (Band b in bands) {
				if (b.Bottom <= y || b.Top >= bottom) continue;
				for (int i = 0; i + 1 < b.X.Count; i += 2)
					if (b.X [i] < r && b.X [i + 1] > x) return true;
			}
			return false;
		}

		public bool IsVisible (float x, float y, float width, float height, Graphics g) => IsVisible (x, y, width, height);


		//
		// Miscellaneous
		//

		bool IsInfiniteInternal => bands.Count == 1 && bands [0].Top <= InfiniteOrigin && bands [0].Bottom >= InfiniteOrigin + InfiniteSize
			&& bands [0].X.Count == 2 && bands [0].X [0] <= InfiniteOrigin && bands [0].X [1] >= InfiniteOrigin + InfiniteSize;

		public bool IsEmpty(Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			return bands.Count == 0;
		}

		public bool IsInfinite(Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			return IsInfiniteInternal;
		}

		public void MakeEmpty()
		{
			bands = new List<Band> ();
		}

		public void MakeInfinite()
		{
			bands = FromRect (InfiniteRect).bands;
		}

		public bool Equals(Region region, Graphics g)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			if (g == null)
				throw new ArgumentNullException ("g");
			if (bands.Count != region.bands.Count) return false;
			for (int i = 0; i < bands.Count; i++)
				if (bands [i].Top != region.bands [i].Top || bands [i].Bottom != region.bands [i].Bottom || !SameSpans (bands [i].X, region.bands [i].X))
					return false;
			return true;
		}

		public static Region FromHrgn (IntPtr hrgn)
		{
			if (hrgn == IntPtr.Zero)
				throw new ArgumentException ("hrgn");
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HRGN is a Windows GDI object.");
			// RGNDATA: a 32-byte header (dwSize, iType, nCount, nRgnSize, rcBound), then nCount RECTs.
			int size = GetRegionData (hrgn, 0, IntPtr.Zero);
			var region = new Region (new List<Band> ());
			if (size <= 0) return region;
			IntPtr buf = Marshal.AllocHGlobal (size);
			try {
				if (GetRegionData (hrgn, size, buf) == 0)
					throw new ArgumentException ("hrgn");
				int count = Marshal.ReadInt32 (buf, 8);
				for (int i = 0; i < count; i++) {
					IntPtr r = buf + 32 + i * 16;
					int l = Marshal.ReadInt32 (r), t = Marshal.ReadInt32 (r, 4), rr = Marshal.ReadInt32 (r, 8), b = Marshal.ReadInt32 (r, 12);
					region.Union (Rectangle.FromLTRB (l, t, rr, b));
				}
			} finally {
				Marshal.FreeHGlobal (buf);
			}
			return region;
		}


		public IntPtr GetHrgn (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			// An HRGN exists only on Windows: the OS's own region object, built from these rectangles.
			if (!OperatingSystem.IsWindows ())
				return IntPtr.Zero;
			if (IsInfiniteInternal)
				return IntPtr.Zero;   // GDI+ hands back NULL for the infinite region
			IntPtr rgn = CreateRectRgn (0, 0, 0, 0);
			foreach (RectangleF r in GetRegionScans (null)) {
				IntPtr part = CreateRectRgn ((int) Math.Floor (r.Left), (int) Math.Floor (r.Top), (int) Math.Ceiling (r.Right), (int) Math.Ceiling (r.Bottom));
				CombineRgn (rgn, rgn, part, 2 /* RGN_OR */);
				DeleteObject (part);
			}
			return rgn;
		}

		// GDI+'s RegionData: a header (size, checksum, version, node count) and the node tree --
		// here a union chain of rectangle nodes, or the empty / infinite node.
		const int RegionDataRect = 0x10000000, RegionDataEmpty = 0x10000002, RegionDataInfinite = 0x10000003, RegionUnion = 2;
		const int RegionVersion = unchecked ((int) 0xDBC01002);

		public RegionData GetRegionData()
		{
			var body = new System.IO.MemoryStream ();
			var w = new System.IO.BinaryWriter (body);
			RectangleF [] rects = IsInfiniteInternal ? null : GetRegionScans (null);
			w.Write (RegionVersion);
			w.Write (rects == null || rects.Length == 0 ? 0 : rects.Length - 1);
			if (rects == null) w.Write (RegionDataInfinite);
			else if (rects.Length == 0) w.Write (RegionDataEmpty);
			else {
				for (int i = 0; i < rects.Length - 1; i++) w.Write (RegionUnion);
				for (int i = 0; i < rects.Length; i++) {
					w.Write (RegionDataRect);
					w.Write (rects [i].X); w.Write (rects [i].Y); w.Write (rects [i].Width); w.Write (rects [i].Height);
				}
			}
			w.Flush ();
			byte [] payload = body.ToArray ();
			var all = new byte [payload.Length + 8];
			BitConverter.GetBytes (payload.Length + 4).CopyTo (all, 0);
			BitConverter.GetBytes (Crc (payload)).CopyTo (all, 4);
			payload.CopyTo (all, 8);
			return new RegionData (all);
		}

		static uint Crc (byte [] data)
		{
			uint c = 0xffffffff;
			foreach (byte b in data) {
				c ^= b;
				for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
			}
			return ~c;
		}

		static Region Deserialize (byte [] data)
		{
			var region = new Region (new List<Band> ());
			if (data.Length < 16) throw new ArgumentException ("Parameter is not valid.");
			int pos = 16;   // size, checksum, version, child count
			// Every rectangle node in the tree, unioned (what a union chain is; other combinations
			// are read as their rectangles' union).
			while (pos + 4 <= data.Length) {
				int type = BitConverter.ToInt32 (data, pos);
				pos += 4;
				if (type == RegionDataRect && pos + 16 <= data.Length) {
					region.Union (new RectangleF (BitConverter.ToSingle (data, pos), BitConverter.ToSingle (data, pos + 4),
						BitConverter.ToSingle (data, pos + 8), BitConverter.ToSingle (data, pos + 12)));
					pos += 16;
				} else if (type == RegionDataInfinite) {
					region.MakeInfinite ();
				}
			}
			return region;
		}


		public RectangleF[] GetRegionScans(Matrix matrix)
		{
			var scans = new List<RectangleF> ();
			foreach (Band b in bands)
				for (int i = 0; i + 1 < b.X.Count; i += 2)
					scans.Add (RectangleF.FromLTRB (b.X [i], b.Top, b.X [i + 1], b.Bottom));
			if (matrix == null || matrix.IsIdentity || IsInfiniteInternal) return scans.ToArray ();
			// Transformed: each scan's corners, bounded (exact for scales and translations).
			float [] m = matrix.Elements;
			for (int i = 0; i < scans.Count; i++) {
				RectangleF r = scans [i];
				float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
				foreach (PointF p in new [] { new PointF (r.Left, r.Top), new PointF (r.Right, r.Top), new PointF (r.Left, r.Bottom), new PointF (r.Right, r.Bottom) }) {
					float x = p.X * m [0] + p.Y * m [2] + m [4], y = p.X * m [1] + p.Y * m [3] + m [5];
					x0 = Math.Min (x0, x); y0 = Math.Min (y0, y); x1 = Math.Max (x1, x); y1 = Math.Max (y1, y);
				}
				scans [i] = RectangleF.FromLTRB (x0, y0, x1, y1);
			}
			return scans.ToArray ();
		}

		public void Transform(Matrix matrix)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			if (matrix.IsIdentity || IsInfiniteInternal) return;
			float [] m = matrix.Elements;
			if (m [1] == 0 && m [2] == 0) {
				var result = new Region (new List<Band> ());
				foreach (RectangleF r in GetRegionScans (matrix)) result.Union (r);
				bands = result.bands;
				return;
			}
			// Rotated or sheared: the scans as polygons, scanned back to pixels.
			using (var path = new GraphicsPath (FillMode.Winding)) {
				foreach (RectangleF r in GetRegionScans (null)) path.AddRectangle (r);
				path.Transform (matrix);
				bands = FromPath (path).bands;
			}
		}

		public Region Clone()
		{
			var copy = new Region (new List<Band> ());
			foreach (Band b in bands) copy.bands.Add (new Band { Top = b.Top, Bottom = b.Bottom, X = new List<float> (b.X) });
			return copy;
		}

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
