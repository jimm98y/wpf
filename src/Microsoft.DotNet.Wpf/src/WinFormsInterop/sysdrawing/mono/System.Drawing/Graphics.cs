//
// System.Drawing.Graphics.cs
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com) (stubbed out)
//      Alexandre Pigolkine(pigolkine@gmx.de)
//	Jordi Mas i Hernandez (jordi@ximian.com)
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2003 Ximian, Inc. (http://www.ximian.com)
// Copyright (C) 2004-2006 Novell, Inc. (http://www.novell.com)
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

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing.WebGpuBackend;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing
{
	public sealed partial class Graphics : MarshalByRefObject, IDisposable
	, IDeviceContext
	{
		internal IntPtr nativeObject = IntPtr.Zero;

		// GPU-rasterization seam: when set, the core drawing verbs record WebGPU scene primitives
		// instead of calling libgdiplus (see backend/IGpuSceneRecorder.cs). Attached by the driver
		// for window paints. Only SolidBrush fills / solid Pen lines / text are rerouted; anything
		// else falls through to libgdiplus so nothing regresses.
		internal IGpuSceneRecorder GpuRecorder;

		// The bitmap this Graphics draws into (FromImage): its drawing is a recorded scene the
		// bitmap renders onto its pixels when they are needed (Bitmap.FlushDrawing).
		internal Bitmap image_target;

		/// <summary>What has been recorded so far, handed over (a SceneVisual, or null when nothing
		/// was), the state -- transform, clips, saved states -- carried on for what comes next.</summary>
		internal object TakeRecordedScene ()
			=> GpuRecorder is WebGpuBackend.SceneRecorder r ? r.TakeScene () : null;

		// GPU-raster mode: route ALL text measurement to the managed metrics (no libgdiplus) — for
		// both layout (control sizing) and paint. Correct because we render every run with the same
		// font, so controls are sized to fit what's actually drawn. A browser prerequisite.
		// On unless switched off; see XplatUIWebGpu.s_gpuRaster for why it cannot be opt-in.
		static readonly bool s_gpuRasterMode = Environment.GetEnvironmentVariable ("WF_GPU_RASTER") != "0"
			&& Environment.GetEnvironmentVariable ("WF_WEBGPU") != "0";

		// WF_TRACE_TEXT=1: every text run the recorder is given, with where it lands.
		static readonly bool s_traceText = Environment.GetEnvironmentVariable ("WF_TRACE_TEXT") == "1";

		static int ArgbOf (Brush b)
		{
			if (b is SolidBrush sb) return sb.Color.ToArgb ();
			// A HatchBrush (e.g. the Percent50 dither the theme uses for pressed radio/checkbox glyphs)
			// has no single colour; approximate it as the average of fore/back so it records as a solid
			// instead of falling through to libgdiplus (which throws with no native Graphics).
			if (b is Drawing2D.HatchBrush hb) return Blend (hb.ForegroundColor, hb.BackgroundColor);
			return 0;
		}
		static int ArgbOf (Pen p)
		{
			if (p == null) return 0;
			if (p.Brush is SolidBrush sb) return sb.Color.ToArgb ();
			if (p.Brush is Drawing2D.HatchBrush hb) return Blend (hb.ForegroundColor, hb.BackgroundColor);
			return p.Color.ToArgb ();
		}
		// A pen's dash pattern, in GDI+ units (multiples of the pen width), or null when solid.
		// The built-in styles have fixed patterns; only a custom one has to be read back from the pen.
		static float [] DashOf (Pen p)
		{
			if (p == null)
				return null;
			switch (p.DashStyle) {
			case Drawing2D.DashStyle.Solid:      return null;
			case Drawing2D.DashStyle.Dash:       return new float [] { 3f, 1f };
			case Drawing2D.DashStyle.Dot:        return new float [] { 1f, 1f };
			case Drawing2D.DashStyle.DashDot:    return new float [] { 3f, 1f, 1f, 1f };
			case Drawing2D.DashStyle.DashDotDot: return new float [] { 3f, 1f, 1f, 1f, 1f, 1f };
			default:
				try { return p.DashPattern; } catch { return null; }
			}
		}

		// True when this pen draws a dashed line, which the recorder has to keep as a stroke: the
		// solid path collapses a line to a filled 1px rect and the pattern would be lost.
		static bool RecordDash (Pen p, out float [] pattern)
		{
			pattern = DashOf (p);
			return pattern != null && pattern.Length > 0;
		}

		// A path, flattened into its subpaths' points. The recorder draws polygons and lines, not
		// curves, so the curves are approximated here -- by GDI+, which owns the path's geometry --
		// rather than each caller having to avoid GraphicsPath. Without this FillPath and DrawPath
		// went straight to a libgdiplus surface that does not exist in GPU-raster mode and silently
		// drew nothing at all: a rounded button came out with no face and no border.
		static List<PointF []> FlattenSubpaths (Drawing2D.GraphicsPath path)
		{
			var subpaths = new List<PointF []> ();
			if (path == null || path.PointCount == 0)
				return subpaths;

			Drawing2D.GraphicsPath flat;
			try {
				flat = (Drawing2D.GraphicsPath) path.Clone ();
				flat.Flatten ();
			} catch {
				return subpaths;
			}

			using (flat) {
				PointF [] points;
				byte [] types;
				try {
					points = flat.PathPoints;
					types = flat.PathTypes;
				} catch {
					return subpaths;
				}

				const byte TypeMask = 0x07, TypeStart = 0x00;
				var current = new List<PointF> ();
				for (int i = 0; i < points.Length; i++) {
					if ((types [i] & TypeMask) == TypeStart && current.Count > 0) {
						subpaths.Add (current.ToArray ());
						current = new List<PointF> ();
					}
					current.Add (points [i]);
				}
				if (current.Count > 0)
					subpaths.Add (current.ToArray ());
			}

			return subpaths;
		}

		static float [] ToXY (PointF [] points)
		{
			var xy = new float [points.Length * 2];
			for (int i = 0; i < points.Length; i++) {
				xy [i * 2] = points [i].X;
				xy [i * 2 + 1] = points [i].Y;
			}
			return xy;
		}

		static int Blend (Color a, Color b) =>
			Color.FromArgb ((a.A + b.A) / 2, (a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2).ToArgb ();
		/// <summary>
		/// CheckStatus for a drawing primitive. A recording-only Graphics (GPU-raster paint) has no
		/// native surface: anything the recorder does not know how to record -- a TextureBrush fill,
		/// say -- still reaches GDI+, which rejects the null handle with InvalidParameter. There is
		/// nothing to draw on, so that is "not drawn", not an error. Throwing turned a missing
		/// background into an ArgumentException on every single paint.
		/// </summary>
		void CheckDrawStatus (Status status)
		{
			if (nativeObject == IntPtr.Zero && status == Status.InvalidParameter)
				return;
			
		}

		bool RecordSolid (Brush b) => GpuRecorder != null && b is SolidBrush;

		// A HatchBrush rendered as a real repeating tile (fore/back pattern), recorded as a tiling
		// ImageBrush so the GPU reproduces the actual hatch — horizontal/diagonal/cross lines and the
		// Percent* dithers (e.g. the Percent50 the theme fills pressed radio/checkbox glyphs with).
		internal readonly struct HatchTile
		{
			public readonly byte[] Rgba; public readonly int W, H; public readonly float Size;
			// A TextureBrush's tile: its pixels one unit each, under the brush transform.
			public readonly float[] Texture;
			public HatchTile (byte[] rgba, int w, int h, float size, float[] texture = null) { Rgba = rgba; W = w; H = h; Size = size; Texture = texture; }
		}
		bool TryHatch (Brush b, out HatchTile tile)
		{
			tile = default;
			if (GpuRecorder == null) return false;
			if (b is TextureBrush tb) {
				byte[] rgba = tb.TileRgba (out int tw, out int th);
				tile = new HatchTile (rgba, tw, th, 0f, tb.TransformElements);
				return true;
			}
			if (!(b is Drawing2D.HatchBrush hb)) return false;
			tile = BuildHatchTile (hb.HatchStyle, hb.ForegroundColor, hb.BackgroundColor);
			return true;
		}

		// A tiled fill: a hatch's pattern, or a texture's image repeated from the origin of its brush
		// transform at the transform's scale (its rotation and shear are not carried).
		void FillTile (GradientShape shape, float x, float y, float w, float h, float [] polyXY, HatchTile t)
		{
			if (t.Texture == null) { GpuRecorder.FillHatch (shape, x, y, w, h, polyXY, t.Rgba, t.W, t.H, t.Size); return; }
			float [] m = t.Texture;
			float sx = (float) Math.Sqrt (m [0] * m [0] + m [1] * m [1]), sy = (float) Math.Sqrt (m [2] * m [2] + m [3] * m [3]);
			GpuRecorder.FillTexture (shape, x, y, w, h, polyXY, t.Rgba, t.W, t.H, t.W * sx, t.H * sy, m [4], m [5]);
		}
		// 8x8 pattern; Size is its extent in POINTS so cells are ~1 device px (fine dithers read as the
		// intended grey, line hatches stay crisp) and the pattern scales with DPI.
		static HatchTile BuildHatchTile (Drawing2D.HatchStyle style, Color fg, Color bg)
		{
			const int N = 8;
			var px = new byte[N * N * 4];
			for (int y = 0; y < N; y++)
				for (int x = 0; x < N; x++) {
					Color c = HatchHit (style, x, y, N) ? fg : bg;
					int i = (y * N + x) * 4;
					px[i] = c.R; px[i + 1] = c.G; px[i + 2] = c.B; px[i + 3] = c.A;
				}
			return new HatchTile (px, N, N, N / 2f);
		}
		// Foreground test for the hatch styles WinForms themes actually use; anything else falls back to
		// a 50% dither (a reasonable stand-in that still reads as the foreground/background mix).
		static bool HatchHit (Drawing2D.HatchStyle s, int x, int y, int n)
		{
			switch (s) {
				case Drawing2D.HatchStyle.Horizontal: return y % n == 0;
				case Drawing2D.HatchStyle.Vertical: return x % n == 0;
				case Drawing2D.HatchStyle.Cross: return x % n == 0 || y % n == 0;
				case Drawing2D.HatchStyle.ForwardDiagonal: return (x + y) % n == 0;
				case Drawing2D.HatchStyle.BackwardDiagonal: return (x - y + n) % n == 0;
				case Drawing2D.HatchStyle.DiagonalCross: return (x + y) % n == 0 || (x - y + n) % n == 0;
				case Drawing2D.HatchStyle.Percent25: return ((x & 1) == 0) && ((y & 1) == 0);
				case Drawing2D.HatchStyle.Percent75: return !(((x & 1) == 1) && ((y & 1) == 1));
				default: return ((x + y) & 1) == 0;   // Percent50 and everything else
			}
		}
		bool RecordPen (Pen p) => GpuRecorder != null && (p?.Brush is SolidBrush || p != null);
		// Resolve a gradient brush (linear multi-stop or path/radial) to a GradientDesc; false = not a
		// gradient in GPU mode (fall through to libgdiplus).
		/// <summary>A LinearGradientBrush fill of a whole-pixel rectangle as GDI+ computes it, pixel for
		/// pixel (GdipLinearGradient, read out of gdiplus.dll): a table of rounded colours blended
		/// with an eight-bit fraction at integer device coordinates. The shader's float lerp at pixel
		/// centres is a level out on a third of a tool strip's rows. Recorded as one rectangle per
		/// row (or column) when the gradient runs straight, else as an image; anything this cannot
		/// reproduce -- a brush transform, gamma, translucency, a point-built brush -- is left to
		/// the shader.</summary>
		bool TryExactLinearGradient (Brush b, float x, float y, float w, float h)
		{
			if (GpuRecorder == null || nativeObject != IntPtr.Zero || print_mode || !(b is Drawing2D.LinearGradientBrush lg) || !lg.exact_known)
				return false;
			if (x != (int) x || y != (int) y || w != (int) w || h != (int) h || w <= 0 || h <= 0 || w * h > 4 << 20)
				return false;
			GpuRecorder.GetTranslation (out float tx, out float ty);
			if (tx != (int) tx || ty != (int) ty)
				return false;
			WebGpuBackend.GdipLinearGradient.Span span;
			try {
				if (lg.GammaCorrection || lg.user_transformed)
					return false;
				Color[] preset = null; float[] presetPos = null, factors = null, positions = null;
				if (lg.InterpolationColorsWereSet) {
					ColorBlend cb = lg.InterpolationColors;
					preset = cb.Colors; presetPos = cb.Positions;
					foreach (Color c in preset) if (c.A != 255) return false;
				} else {
					Blend bl = lg.Blend;
					if (bl != null && bl.Factors != null && !(bl.Factors.Length == 1 && bl.Factors [0] == 1f)) {
						factors = bl.Factors; positions = bl.Positions;
					}
				}
				if (lg.gradient_color1.A != 255 || lg.gradient_color2.A != 255)
					return false;
				span = new WebGpuBackend.GdipLinearGradient.Span (lg.Rectangle, lg.gradient_color1, lg.gradient_color2,
					lg.exact_angle, lg.exact_scalable, lg.WrapMode, factors, positions, preset, presetPos);
			} catch (Exception) {
				return false;
			}
			int x0 = (int) x, y0 = (int) y, iw = (int) w, ih = (int) h, ox = (int) tx, oy = (int) ty;
			if (span.StepX == 0) {
				// Each row one colour; runs of equal rows as one rectangle.
				int start = 0; uint run = span.Pixel (x0 + ox, y0 + oy);
				for (int j = 1; j <= ih; j++) {
					uint c = j < ih ? span.Pixel (x0 + ox, y0 + oy + j) : ~run;
					if (c == run) continue;
					GpuRecorder.FillRect (x0, y0 + start, iw, j - start, unchecked ((int) run));
					start = j; run = c;
				}
				return true;
			}
			if (span.StepY == 0) {
				int start = 0; uint run = span.Pixel (x0 + ox, y0 + oy);
				for (int i = 1; i <= iw; i++) {
					uint c = i < iw ? span.Pixel (x0 + ox + i, y0 + oy) : ~run;
					if (c == run) continue;
					GpuRecorder.FillRect (x0 + start, y0, i - start, ih, unchecked ((int) run));
					start = i; run = c;
				}
				return true;
			}
			var rgba = new byte [iw * ih * 4];
			for (int j = 0; j < ih; j++)
				for (int i = 0; i < iw; i++) {
					uint c = span.Pixel (x0 + ox + i, y0 + oy + j);
					int d = (j * iw + i) * 4;
					rgba [d] = (byte) (c >> 16); rgba [d + 1] = (byte) (c >> 8); rgba [d + 2] = (byte) c; rgba [d + 3] = (byte) (c >> 24);
				}
			GpuRecorder.DrawImage (rgba, iw, ih, x0, y0, iw, ih);
			return true;
		}

		/// <summary>An antialiased solid fill of GDI+ Beziers as GDI+ rasterizes it (WebGpuBackend.
		/// GdipAntialias, exact against gdiplus.dll): the colour with the coverage as its alpha, drawn
		/// as an image. <paramref name="m11"/>.. is the world matrix the caller would have set; the
		/// recorder's own translation must be whole pixels.</summary>
		internal bool TryFillGdipAntialiased (Brush brush, PointF [] beziers, float m11, float m12, float m21, float m22, float dx, float dy)
		{
			if (GpuRecorder == null || nativeObject != IntPtr.Zero || print_mode || GpuAliased || !(brush is SolidBrush sb))
				return false;
			GpuRecorder.GetTranslation (out float tx, out float ty);
			if (tx != (int) tx || ty != (int) ty)
				return false;
			if (!WebGpuBackend.GdipAntialias.Fill (beziers, m11, m12, m21, m22, dx, dy, out byte [] alpha, out Rectangle r))
				return false;
			Color c = sb.Color;
			var rgba = new byte [r.Width * r.Height * 4];
			for (int i = 0; i < alpha.Length; i++) {
				int d = i * 4;
				rgba [d] = c.R; rgba [d + 1] = c.G; rgba [d + 2] = c.B;
				rgba [d + 3] = (byte) ((alpha [i] * c.A + 127) / 255);
			}
			GpuRecorder.DrawImage (rgba, r.Width, r.Height, r.X, r.Y, r.Width, r.Height);
			return true;
		}

		/// <summary>An antialiased solid fill of a whole path as GDI+ rasterizes it (GdipAntialias.FillPath),
		/// under a world that is at most a whole-pixel translation.</summary>
		internal bool TryFillGdipAntialiasedPath (Brush brush, GraphicsPath path)
		{
			if (GpuRecorder == null || nativeObject != IntPtr.Zero || print_mode || GpuAliased || !(brush is SolidBrush sb))
				return false;
			float [] m = rec_world;
			if (m [0] != 1f || m [1] != 0f || m [2] != 0f || m [3] != 1f || m [4] != (int) m [4] || m [5] != (int) m [5])
				return false;
			GpuRecorder.GetTranslation (out float tx, out float ty);
			if (tx != (int) tx || ty != (int) ty)
				return false;
			Color c = sb.Color;
			int argb = c.ToArgb ();
			PointF [] pts = path.PathPoints;
			byte [] types = path.PathTypes;
			bool winding = path.FillMode == FillMode.Winding;
			// The same shape in the same colour, anywhere at a whole-pixel offset, is the same raster:
			// kept, keyed by its points relative to the shape's whole-pixel origin. Rasterizing on the CPU
			// and uploading every edge strip as a new texture, on every paint, was most of a DotNetBar
			// repaint -- its buttons and panels are rounded, antialiased paths.
			float ox = float.MaxValue, oy = float.MaxValue;
			foreach (PointF q in pts) { if (q.X < ox) ox = q.X; if (q.Y < oy) oy = q.Y; }
			if (pts.Length == 0) return false;
			ox = MathF.Floor (ox); oy = MathF.Floor (oy);
			ulong key = 1469598103934665603UL;
			void Mix (ulong v) { key = (key ^ v) * 1099511628211UL; }
			Mix ((uint) argb); Mix (winding ? 1UL : 2UL);
			for (int i = 0; i < pts.Length; i++) {
				Mix ((uint) BitConverter.SingleToInt32Bits (pts [i].X - ox));
				Mix ((uint) BitConverter.SingleToInt32Bits (pts [i].Y - oy));
				Mix (types [i]);
			}
			AaFill fill;
			lock (s_aaFills) s_aaFills.TryGetValue (key, out fill);
			if (fill == null) {
				var local = new PointF [pts.Length];
				for (int i = 0; i < pts.Length; i++) local [i] = new PointF (pts [i].X - ox, pts [i].Y - oy);
				fill = RasterizeAaFill (local, types, winding, c);
				if (fill == null) return false;
				lock (s_aaFills) {
					if (s_aaFills.Count > 8192) s_aaFills.Clear ();
					s_aaFills [key] = fill;
				}
			}
			int bx = (int) ox, by = (int) oy;
			foreach (Rectangle q in fill.Rects) GpuRecorder.FillRect (bx + q.X, by + q.Y, q.Width, q.Height, argb);
			foreach ((byte [] rgba, Rectangle q) in fill.Images) GpuRecorder.DrawImage (rgba, q.Width, q.Height, bx + q.X, by + q.Y, q.Width, q.Height);
			return true;
		}

		sealed class AaFill
		{
			public readonly List<Rectangle> Rects = new List<Rectangle> ();
			public readonly List<(byte [], Rectangle)> Images = new List<(byte [], Rectangle)> ();
		}
		static readonly Dictionary<ulong, AaFill> s_aaFills = new Dictionary<ulong, AaFill> ();

		/// <summary>GDI+'s antialiased fill of a path (in pixels relative to its own origin): the fully
		/// covered part as rectangles -- rows with the same covered span merged into one -- so that what
		/// is drawn on it later (a caption) sees a solid fill to blend against, as GDI+ blends against the
		/// pixels; only the edge's partial pixels go as images, in strips beside and above and below that
		/// core, so that none of them lies over the caption either.</summary>
		static AaFill RasterizeAaFill (PointF [] pts, byte [] types, bool winding, Color c)
		{
			if (!WebGpuBackend.GdipAntialias.FillPath (pts, types, winding, 1, 0, 0, 1, 0, 0, out byte [] alpha, out Rectangle r))
				return null;
			var fill = new AaFill ();
			int w = r.Width, h = r.Height;
			var strips = new List<Rectangle> ();
			// Each row's fully covered spans; rows with the same spans make one band.
			List<(int A, int B)> Spans (int y)
			{
				var l = new List<(int, int)> ();
				for (int x = 0; x < w; ) {
					if (alpha [y * w + x] != 255) { x++; continue; }
					int x1 = x;
					while (x1 < w && alpha [y * w + x1] == 255) x1++;
					l.Add ((x, x1));
					x = x1;
				}
				return l;
			}
			static bool Same (List<(int A, int B)> u, List<(int A, int B)> v)
			{
				if (u.Count != v.Count) return false;
				for (int k = 0; k < u.Count; k++) if (u [k] != v [k]) return false;
				return true;
			}
			int bandStart = 0;
			List<(int A, int B)> band = h > 0 ? Spans (0) : null;
			for (int y = 1; y <= h; y++) {
				List<(int A, int B)> cur = y < h ? Spans (y) : null;
				if (y < h && Same (cur, band)) continue;
				int n = y - bandStart, prev = 0;
				foreach ((int A, int B) sp in band) {
					fill.Rects.Add (new Rectangle (r.X + sp.A, r.Y + bandStart, sp.B - sp.A, n));
					if (sp.A > prev) strips.Add (new Rectangle (prev, bandStart, sp.A - prev, n));
					prev = sp.B;
				}
				if (prev < w) strips.Add (new Rectangle (prev, bandStart, w - prev, n));
				bandStart = y; band = cur;
			}
			foreach (Rectangle st in strips) {
				var rgba = new byte [st.Width * st.Height * 4];
				bool any = false;
				for (int yy = 0; yy < st.Height; yy++)
					for (int xx = 0; xx < st.Width; xx++) {
						byte al = alpha [(st.Y + yy) * w + st.X + xx];
						if (al == 0) continue;
						any = true;
						int d = (yy * st.Width + xx) * 4;
						rgba [d] = c.R; rgba [d + 1] = c.G; rgba [d + 2] = c.B;
						rgba [d + 3] = (byte) ((al * c.A + 127) / 255);
					}
				if (any)
					fill.Images.Add ((rgba, new Rectangle (r.X + st.X, r.Y + st.Y, st.Width, st.Height)));
			}
			return fill;
		}

		bool TryGradient (Brush b, out GradientDesc g)
		{
			g = default;
			if (GpuRecorder == null) return false;
			if (b is Drawing2D.LinearGradientBrush lg) {
				lg.GetGpuGradient (out PointF s, out PointF e, out _, out _);
				lg.GetGpuStops (out float[] offs, out int[] argb);
				g = new GradientDesc (false, s.X, s.Y, e.X, e.Y, offs, argb);
				return true;
			}
			if (b is Drawing2D.PathGradientBrush pg) {
				pg.GetGpuGradient (out PointF c, out float rx, out float ry, out float[] offs, out int[] argb);
				g = new GradientDesc (true, c.X, c.Y, rx, ry, offs, argb);
				return true;
			}
			return false;
		}
		static float[] Flatten (PointF[] p) { var a = new float[p.Length * 2]; for (int i = 0; i < p.Length; i++) { a[i*2] = p[i].X; a[i*2+1] = p[i].Y; } return a; }
		static float[] Flatten (Point[] p) { var a = new float[p.Length * 2]; for (int i = 0; i < p.Length; i++) { a[i*2] = p[i].X; a[i*2+1] = p[i].Y; } return a; }

		/// <summary>DrawImage at a point: GDI+ draws the image at its PHYSICAL size (GetImageDestPageSize),
		/// its pixels at its own resolution through the surface's -- a 300 dpi resource icon drawn at a
		/// point on a 96 dpi screen is five pixels wide, not sixteen.</summary>
		bool RecordImageAtPoint (Image image, float x, float y)
		{
			SizeF size = PrintImageSize (image);
			return RecordImage (image, x, y, size.Width, size.Height);
		}

		// Record a Bitmap draw as an image quad (whole image -> dest rect). Returns false (fall through
		// to libgdiplus) when not in GPU-raster mode or the image isn't a Bitmap we can read pixels from.
		bool RecordImage (Image image, float dx, float dy, float dw, float dh)
		{
			if (GpuRecorder != null && image is Printing.PrintPreviewPageImage page) {
				GpuRecorder.DrawScene (page.Scene, 0, 0, page.Width, page.Height, dx, dy, dw, dh);
				return true;
			}
			if (GpuRecorder == null || !(image is Bitmap bmp) || bmp.managed == null) return false;
			GfxTrace ("DrawImage", () => $"{bmp.Width}x{bmp.Height} dpi={bmp.HorizontalResolution} -> {dx},{dy} {dw}x{dh} im={InterpolationMode}", new RectangleF (dx, dy, dw, dh));
			int w = bmp.Width, h = bmp.Height;
			if (RecordBitmapScenes (bmp, 0, 0, w, h, dx, dy, dw, dh)) return true;
			if (print_mode && PrintImageBands (bmp, new RectangleF (0, 0, w, h), new RectangleF (dx, dy, dw, dh), null)) return true;
			GdipFrame frame = bmp.Data.Frame;
			byte[] px = CachedRgba (frame, new Rectangle (0, 0, w, h));
			if (image_target != null) px = IntoBitmap (px);
			px = AntialiasedImageEdges (px, w, h, dx, dy, dw, dh);
			GpuRecorder.DrawImage (px, w, h, dx, dy, dw, dh);
			return true;
		}

		// A frame's RGBA as last converted, with what it was converted from. A bitmap drawn again
		// unchanged hands the renderer the SAME array, which is what the renderer keys its texture on:
		// DotNetBar paints its buffered non-client bitmap (a window-sized one) on every repaint, and each
		// conversion was a fresh 8 MB array and a fresh texture upload -- most of a resize frame.
		sealed class RgbaCache { public ulong Hash; public Rectangle Rect; public PixelFormat Format; public byte[] Rgba; }
		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GdipFrame, RgbaCache> s_rgbaCache = new ();

		static byte[] CachedRgba (GdipFrame frame, Rectangle rect)
		{
			ulong hash = ContentHash (frame.Bits);
			if (s_rgbaCache.TryGetValue (frame, out RgbaCache c) && c.Hash == hash && c.Rect == rect && c.Format == frame.Format)
				return c.Rgba;
			byte[] rgba = GdipPixels.ToRgba (frame, rect);
			s_rgbaCache.AddOrUpdate (frame, new RgbaCache { Hash = hash, Rect = rect, Format = frame.Format, Rgba = rgba });
			return rgba;
		}

		/// <summary>A 64-bit hash of every byte (8 at a time): a few hundred microseconds for a
		/// window-sized bitmap, against a conversion and an upload.</summary>
		static ulong ContentHash (byte[] bits)
		{
			ReadOnlySpan<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong> (bits);
			ulong h0 = 0x9E3779B97F4A7C15UL, h1 = 0xC2B2AE3D27D4EB4FUL;
			int i = 0;
			for (; i + 1 < words.Length; i += 2) {
				h0 = (h0 ^ words [i]) * 0x100000001B3UL;
				h1 = (h1 ^ words [i + 1]) * 0x100000001B3UL;
			}
			if (i < words.Length) h0 = (h0 ^ words [i]) * 0x100000001B3UL;
			for (int b = words.Length * 8; b < bits.Length; b++) h1 = (h1 ^ bits [b]) * 0x100000001B3UL;
			return h0 ^ (h1 * 31) ^ (ulong) bits.Length;
		}

		/// <summary>GDI+'s antialiased DrawImage at PixelOffsetMode.None: the destination rectangle's
		/// coverage is taken with pixel centres on the integer coordinates, so an image drawn 1:1 at a
		/// whole position covers its first column and its first row by half (the corner by a quarter),
		/// and the half column past its right and bottom edges samples nothing. DotNetBar draws its
		/// ribbon icons at SmoothingMode.AntiAlias; stock's left edge of every icon is half-strength
		/// (#73 over white lands on #B9), ours was solid. Measured on stock; only the 1:1 case is modelled
		/// (a scaled image's edges are left to the renderer).</summary>
		byte [] AntialiasedImageEdges (byte [] rgba, int w, int h, float dx, float dy, float dw, float dh)
		{
			if (GpuAliased || w <= 0 || h <= 0 || dw != w || dh != h)
				return rgba;
			if (_pixelOffset == PixelOffsetMode.Half || _pixelOffset == PixelOffsetMode.HighQuality)
				return rgba;
			float [] m = RecordingMatrix ();
			if (m [0] != 1f || m [1] != 0f || m [2] != 0f || m [3] != 1f)
				return rgba;
			float ox = dx + m [4], oy = dy + m [5];
			if (ox != MathF.Floor (ox) || oy != MathF.Floor (oy))
				return rgba;
			// The same source array always comes out as the same edged array: a fresh copy per draw was a
			// fresh texture per draw (every ribbon icon, every hover frame).
			if (s_edged.TryGetValue (rgba, out Edged done) && done.W == w && done.H == h)
				return done.Rgba;
			byte [] o = (byte []) rgba.Clone ();
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) {
					if (x != 0 && y != 0) break;
					int cov = x == 0 && y == 0 ? 64 : 128;
					int i = (y * w + x) * 4;
					o [i + 3] = (byte) ((o [i + 3] * cov + 127) / 255);
				}
			s_edged.AddOrUpdate (rgba, new Edged { W = w, H = h, Rgba = o });
			return o;
		}

		sealed class Edged { public int W, H; public byte [] Rgba; }
		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte [], Edged> s_edged = new ();
		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte [], byte []> s_intoBitmap = new ();

		// An image drawn INTO a bitmap goes through GDI+'s premultiplied blend on the way: a
		// translucent pixel comes out of the bitmap as the premultiply's round trip leaves it (a
		// disabled tool-strip image, 70% alpha, reads a level lighter otherwise). Done to the pixels
		// as they are recorded, so the bitmap drawn later as a scene shows what GDI+ would have stored.
		// The CPU rasterizer, which does the premultiplied blend itself, is handed the original
		// (WebGpuBackend.SceneRaster.Original).
		static byte[] IntoBitmap (byte[] rgba)
		{
			if (s_intoBitmap.TryGetValue (rgba, out byte[] was)) return was;
			byte[] stored = null;
			for (int i = 0; i + 3 < rgba.Length; i += 4) {
				int a = rgba [i + 3];
				if (a == 255) continue;
				stored ??= (byte []) rgba.Clone ();
				for (int k = 0; k < 3; k++)
					stored [i + k] = (byte) GdipPixels.Unpremultiply (GdipPixels.Premultiply (rgba [i + k], a), a);
			}
			if (stored != null) WebGpuBackend.SceneRaster.Original.AddOrUpdate (stored, rgba);
			stored ??= rgba;
			s_intoBitmap.AddOrUpdate (rgba, stored);
			return stored;
		}

		// A bitmap that has been drawn into and not read since is drawn as what was drawn: its pixels
		// first (unless it was transparent when drawing began), then each recorded scene, mapped from
		// the bitmap's (sx, sy, sw, sh) onto (dx, dy, dw, dh) and clipped to it -- the same renderer as
		// the rest of the window, rather than a CPU rasterization of it.
		bool RecordBitmapScenes (Bitmap bmp, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh)
		{
			if (bmp == image_target || !bmp.TryGetRecording (out object[] scenes, out bool baseNeeded)) return false;
			if (baseNeeded) {
				GdipFrame frame = bmp.managed.Frame;
				int x0 = Math.Max (0, (int) Math.Floor (sx)), y0 = Math.Max (0, (int) Math.Floor (sy));
				int x1 = Math.Min (frame.Width, (int) Math.Ceiling (sx + sw)), y1 = Math.Min (frame.Height, (int) Math.Ceiling (sy + sh));
				if (x1 > x0 && y1 > y0) {
					float kx = dw / sw, ky = dh / sh;
					GpuRecorder.DrawImage (GdipPixels.ToRgba (frame, new Rectangle (x0, y0, x1 - x0, y1 - y0)), x1 - x0, y1 - y0,
						dx + (x0 - sx) * kx, dy + (y0 - sy) * ky, (x1 - x0) * kx, (y1 - y0) * ky);
				}
			}
			foreach (object scene in scenes)
				GpuRecorder.DrawScene (scene, sx, sy, sw, sh, dx, dy, dw, dh);
			return true;
		}
		static PointF [] ToF (Point [] p)
		{
			if (p == null) return null;
			var r = new PointF [p.Length];
			for (int i = 0; i < p.Length; i++) r [i] = p [i];
			return r;
		}

		// A parallelogram given as three points (upper-left, upper-right, lower-left): an upright
		// rectangle draws as one; a rotated or sheared one draws the unit rectangle under the affine
		// map that takes it onto the three points.
		bool RecordImagePoints (Image image, PointF [] dest, RectangleF? src, GraphicsUnit unit, Imaging.ImageAttributes attrs)
		{
			if (GpuRecorder == null || image == null || dest == null || dest.Length < 3) return false;
			RectangleF source = src ?? new RectangleF (0, 0, image.Width, image.Height);
			if (dest [0].Y == dest [1].Y && dest [0].X == dest [2].X) {
				var rect = new RectangleF (dest [0].X, dest [0].Y, dest [1].X - dest [0].X, dest [2].Y - dest [0].Y);
				return RecordImage (image, rect, source, unit, attrs);
			}
			if (nativeObject != IntPtr.Zero) return false;
			// (0,0) -> p0, (1,0) -> p1, (0,1) -> p2, in the world, prepended to the world transform.
			float [] saved = (float []) rec_world.Clone ();
			float [] map = { dest [1].X - dest [0].X, dest [1].Y - dest [0].Y, dest [2].X - dest [0].X, dest [2].Y - dest [0].Y, dest [0].X, dest [0].Y };
			RecordedCombine (map, MatrixOrder.Prepend);
			try {
				return RecordImage (image, new RectangleF (0, 0, 1, 1), source, unit, attrs);
			} finally {
				rec_world = saved;
				PushRecordedTransform ();
			}
		}

		// Record a Bitmap draw with a source rectangle and image attributes applied to its pixels. False
		// (fall through) when not in GPU-raster mode or the image isn't a Bitmap we can read.
		bool RecordImage (Image image, RectangleF dest, RectangleF src, GraphicsUnit unit, Imaging.ImageAttributes attrs)
		{
			if (GpuRecorder != null && image is Printing.PrintPreviewPageImage page) {
				// The page's pixels are its hundredths of an inch.
				if (unit != GraphicsUnit.Pixel) {
					float k = UnitToPixels (unit, 100f);
					src = new RectangleF (src.X * k, src.Y * k, src.Width * k, src.Height * k);
				}
				GpuRecorder.DrawScene (page.Scene, src.X, src.Y, src.Width, src.Height, dest.X, dest.Y, dest.Width, dest.Height);
				return true;
			}
			if (GpuRecorder == null || !(image is Bitmap bmp) || bmp.managed == null) return false;
			if (unit != GraphicsUnit.Pixel) {
				float ux = UnitToPixels (unit, bmp.HorizontalResolution), uy = UnitToPixels (unit, bmp.VerticalResolution);
				src = new RectangleF (src.X * ux, src.Y * uy, src.Width * ux, src.Height * uy);
			}
			if (src.Width <= 0 || src.Height <= 0 || dest.Width == 0 || dest.Height == 0) return true;
			GfxTrace ("DrawImageRect", () => $"{bmp.Width}x{bmp.Height} dpi={bmp.HorizontalResolution} src={src} -> {dest} attrs={attrs != null} im={InterpolationMode}", dest);
			// Attributes recolour pixels, so a recorded bitmap is rendered first; without them it
			// can be drawn as its recording.
			if (attrs == null && RecordBitmapScenes (bmp, src.X, src.Y, src.Width, src.Height, dest.X, dest.Y, dest.Width, dest.Height))
				return true;
			GdipFrame frame = bmp.Data.Frame;
			int x0 = Math.Max (0, (int) Math.Floor (src.X)), y0 = Math.Max (0, (int) Math.Floor (src.Y));
			int x1 = Math.Min (frame.Width, (int) Math.Ceiling (src.Right)), y1 = Math.Min (frame.Height, (int) Math.Ceiling (src.Bottom));
			int w = x1 - x0, h = y1 - y0;
			if (w <= 0 || h <= 0) return true;
			// Clamping the source to the bitmap shrinks the destination in proportion.
			float kx = dest.Width / src.Width, ky = dest.Height / src.Height;
			var d = new RectangleF (dest.X + (x0 - src.X) * kx, dest.Y + (y0 - src.Y) * ky, w * kx, h * ky);
			if (print_mode && PrintImageBands (bmp, new RectangleF (x0, y0, w, h), d, attrs)) return true;
			// Without attributes the converted pixels are the cached ones (the same array for an unchanged
			// bitmap, which is what the renderer keys its texture on); attributes recolour a fresh copy.
			byte[] rgba = attrs == null ? CachedRgba (frame, new Rectangle (x0, y0, w, h)) : GdipPixels.ToRgba (frame, new Rectangle (x0, y0, w, h));
			attrs?.Apply (rgba, Imaging.ColorAdjustType.Bitmap);
			if (image_target != null) rgba = IntoBitmap (rgba);
			rgba = AntialiasedImageEdges (rgba, w, h, d.X, d.Y, d.Width, d.Height);
			GpuRecorder.DrawImage (rgba, w, h, d.X, d.Y, d.Width, d.Height);
			return true;
		}

		static float UnitToPixels (GraphicsUnit unit, float dpi)
		{
			switch (unit) {
			case GraphicsUnit.Point: return dpi / 72f;
			case GraphicsUnit.Inch: return dpi;
			case GraphicsUnit.Document: return dpi / 300f;
			case GraphicsUnit.Millimeter: return dpi / 25.4f;
			default: return 1f;
			}
		}

		// A recording Graphics has no GDI+ object to hold its smoothing mode, so it is kept here --
		// and it matters: GDI+'s default is ALIASED, which is how every WinForms arrow and glyph is
		// filled, while the recorder's fills are antialiased.
		private SmoothingMode gpu_smoothing = SmoothingMode.None;
		private readonly List<SmoothingMode> gpu_saved_smoothing = new List<SmoothingMode> ();

		private bool GpuAliased => nativeObject == IntPtr.Zero && !print_mode && gpu_smoothing != SmoothingMode.AntiAlias
			&& gpu_smoothing != SmoothingMode.HighQuality && gpu_smoothing != (SmoothingMode) 5 && gpu_smoothing != (SmoothingMode) 6;

		/// <summary>GDI+'s aliased fill at PixelOffsetMode.None: pixel (i, j) is filled when the
		/// point (i, j) -- a pixel's centre sits on the integer coordinate -- is inside, with left and
		/// top edges in and right and bottom edges out. Recorded as one rectangle per run.</summary>
		private void FillPolygonAliased (float [] xy, bool winding, int argb) => FillContoursAliased (new [] { xy }, winding, argb);

		/// <summary>The same over several contours filled together (a widened outline: a figure and the
		/// hole inside it), under one winding rule.</summary>
		private void FillContoursAliased (IList<float []> contours, bool winding, int argb)
		{
			float ymin = float.MaxValue, ymax = float.MinValue;
			foreach (float [] c in contours)
				for (int i = 0; i < c.Length / 2; i++) { ymin = Math.Min (ymin, c [i * 2 + 1]); ymax = Math.Max (ymax, c [i * 2 + 1]); }
			if (ymin > ymax) return;
			var xs = new List<(float X, int Dir)> ();
			for (int y = (int) Math.Ceiling (ymin); y < ymax; y++) {
				xs.Clear ();
				foreach (float [] xy in contours) {
				int n = xy.Length / 2;
				if (n < 3) continue;
				for (int i = 0; i < n; i++) {
					float x0 = xy [i * 2], y0 = xy [i * 2 + 1];
					float x1 = xy [(i + 1) % n * 2], y1 = xy [(i + 1) % n * 2 + 1];
					if (y0 == y1) continue;
					int dir = y1 > y0 ? 1 : -1;
					if (dir < 0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
					if (y < y0 || y >= y1) continue;   // half-open: the top row in, the bottom row out
					xs.Add ((x0 + (y - y0) * (x1 - x0) / (y1 - y0), dir));
				}
				}
				xs.Sort ((a, b) => a.X.CompareTo (b.X));
				int wind = 0;
				for (int k = 0; k < xs.Count - 1; k++) {
					wind += winding ? xs [k].Dir : 1;
					bool inside = winding ? wind != 0 : (wind & 1) != 0;
					if (!inside) continue;
					int a = (int) Math.Ceiling (xs [k].X), b = (int) Math.Ceiling (xs [k + 1].X);
					if (b > a) GpuRecorder.FillRect (a, y, b - a, 1, argb);
				}
			}
		}

		/// <summary>A solid pen's line. GDI+ strokes a pen wider than a pixel as the polygon the pen
		/// sweeps (flat caps: from the first point to the second, half the width either side), and
		/// with the default aliased smoothing that polygon covers whole pixels by the centre rule
		/// (FillPolygonAliased). The recorder's stroke is antialiased, so a two-pixel rule at a whole
		/// coordinate came out as three soft rows a column too long where Windows draws two solid
		/// ones (DotNetBar's selected dock tab). A one-pixel pen keeps the recorder's exact rectangle.</summary>
		private void RecordSolidLine (Pen pen, float x1, float y1, float x2, float y2)
		{
			float w = pen.Width;
			if (GpuAliased && w > 1.01f && (x1 != x2 || y1 != y2)) {
				float dx = x2 - x1, dy = y2 - y1, len = MathF.Sqrt (dx * dx + dy * dy);
				float nx = -dy / len * w * 0.5f, ny = dx / len * w * 0.5f;
				FillPolygonAliased (new [] { x1 + nx, y1 + ny, x2 + nx, y2 + ny, x2 - nx, y2 - ny, x1 - nx, y1 - ny }, false, ArgbOf (pen));
				return;
			}
			if (GpuAliased && w <= 1.01f && x1 != x2 && y1 != y2 && x1 == MathF.Floor (x1) && y1 == MathF.Floor (y1)
			    && x2 == MathF.Floor (x2) && y2 == MathF.Floor (y2)) {
				RecordAliasedHairline ((int) x1, (int) y1, (int) x2, (int) y2, ArgbOf (pen));
				return;
			}
			// Antialiased: the swept polygon (at least a pixel wide) through GDI+'s own 8x4 filler,
			// as GpGraphics strokes it -- the recorder's quad sat half a pixel off.
			if (!GpuAliased && (x1 != x2 || y1 != y2) && pen.Color.A == 255) {
				float dx = x2 - x1, dy = y2 - y1, len = MathF.Sqrt (dx * dx + dy * dy);
				float hw = Math.Max (w, 1f) * 0.5f, nx = -dy / len * hw, ny = dx / len * hw;
				// A pen of a pixel or less is a device hairline: a pixel thick across the MINOR axis
				// (a row per column of an x-major line), not across the line -- measured, a 45-degree
				// stock line puts one pixel of ink in each row. (Stock's end pixels carry about a
				// quarter more ink than this; lengthening the segment to match moves the interior.)
				if (w <= 1.01f) {
					if (Math.Abs (dx) >= Math.Abs (dy)) { nx = 0f; ny = 0.5f; }
					else { nx = 0.5f; ny = 0f; }
				}
				using (var path = new GraphicsPath (FillMode.Winding)) {
					path.AddPolygon (new [] { new PointF (x1 + nx, y1 + ny), new PointF (x2 + nx, y2 + ny), new PointF (x2 - nx, y2 - ny), new PointF (x1 - nx, y1 - ny) });
					using (var b = new SolidBrush (pen.Color))
						if (TryFillGdipAntialiasedPath (b, path))
							return;
				}
			}
			GpuRecorder.DrawLine (x1, y1, x2, y2, ArgbOf (pen), w);
		}

		/// <summary>GDI+'s aliased one-pixel line between whole points: one pixel per step of the
		/// major axis, both ends included, walked from the end with the smaller major coordinate; the
		/// minor coordinate rounds a half toward where the walk started (measured on stock: eight
		/// segments, every pixel). The recorder's diagonal is an antialiased quad, which turned
		/// DotNetBar's crisp two-pixel close cross into a grey smudge.</summary>
		private void RecordAliasedHairline (int x0, int y0, int x1, int y1, int argb)
		{
			bool xMajor = Math.Abs (x1 - x0) >= Math.Abs (y1 - y0);
			if (xMajor ? x1 < x0 : y1 < y0) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
			int n = xMajor ? x1 - x0 : y1 - y0;
			int dm = xMajor ? y1 - y0 : x1 - x0, sign = Math.Sign (dm), adm = Math.Abs (dm);
			int runStart = 0, runMinor = 0;
			for (int i = 0; i <= n + 1; i++) {
				int minor = i <= n ? (xMajor ? y0 : x0) + sign * (int) Math.Ceiling ((double) adm * i / n - 0.5) : int.MinValue;
				if (i == 0) { runMinor = minor; continue; }
				if (minor != runMinor) {
					int len = i - runStart;
					if (xMajor) GpuRecorder.FillRect (x0 + runStart, runMinor, len, 1, argb);
					else GpuRecorder.FillRect (runMinor, y0 + runStart, 1, len, argb);
					runStart = i; runMinor = minor;
				}
			}
		}

		private void RecordFillPolygon (float [] xy, bool winding, int argb)
		{
			if (GpuAliased) { FillPolygonAliased (xy, winding, argb); return; }
			if (IsConvex (xy)) GpuRecorder.FillPolygon (xy, argb);
			else GpuRecorder.FillContours (new [] { xy }, winding, argb);
		}

		// Convex, and wound one way: what a fan triangulates correctly. Collinear runs are allowed.
		static bool IsConvex (float [] xy)
		{
			int n = xy.Length / 2;
			if (n < 4) return true;
			int sign = 0;
			for (int i = 0; i < n; i++) {
				float ax = xy [i * 2], ay = xy [i * 2 + 1];
				float bx = xy [(i + 1) % n * 2], by = xy [(i + 1) % n * 2 + 1];
				float cx = xy [(i + 2) % n * 2], cy = xy [(i + 2) % n * 2 + 1];
				float cross = (bx - ax) * (cy - by) - (by - ay) * (cx - bx);
				if (cross == 0) continue;
				int sg = cross > 0 ? 1 : -1;
				if (sign == 0) sign = sg;
				else if (sg != sign) return false;
			}
			return true;
		}

		internal IMacContext maccontext;
		private bool disposed = false;
		private static float defDpiX = 0;
		private static float defDpiY = 0;
		private IntPtr deviceContextHdc;
		

		public delegate bool EnumerateMetafileProc (EmfPlusRecordType recordType,
							    int flags,
							    int dataSize,
							    IntPtr data,
							    PlayRecordCallback callbackData);
		
		public delegate bool DrawImageAbort (IntPtr callbackdata);

		internal Graphics (IntPtr nativeGraphics)
		{
			nativeObject = nativeGraphics;
		}

				internal Graphics(IntPtr nativeGraphics, Image image) : this(nativeGraphics)
		{
		}

		/// <summary>The resolution a point-sized font's em is taken at on this surface (its DpiY,
		/// as GDI+ converts): the screen's for a window, a bitmap's own for a bitmap.</summary>
		float EmDpiY => mf_rec != null ? mf_rec.DpiY : print_mode ? print_dpi_y
			: image_target != null ? image_target.VerticalResolution : WebGpuBackend.DisplayDpi.Y;

		~Graphics ()
		{
			Dispose ();			
		}		

		// A bitmap made for measuring has the screen's resolution (WebGpuBackend.DisplayDpi): 96 dpi
		// for an application that is not DPI-aware, the system DPI for one that is.
		static internal float systemDpiX => WebGpuBackend.DisplayDpi.X;

		static internal float systemDpiY => WebGpuBackend.DisplayDpi.Y;

		// For CoreFX compatibility
		internal IntPtr NativeGraphics {
			get {
				return nativeObject;
			}
		}

		internal IntPtr NativeObject {
			get {
				return nativeObject;
			}

			set {
				nativeObject = value;
			}
		}
		
		public void AddMetafileComment (byte [] data)
		{
			if (data == null)
				throw new ArgumentNullException ("data");
			CheckUse ();
			// Only a Graphics recording a metafile keeps comments; GdipComment on any other is
			// InvalidParameter.
			if (mf_rec == null)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			mf_rec.Comment (data);
		}

		// GDI+'s container and state ids share one counter (DpContext's Uniqueness and depth).
		int _stateCounter;

		public GraphicsContainer BeginContainer ()
		{
			CheckUse ();
			int token = BeginContainerState (null, RectangleF.Empty, RectangleF.Empty, GraphicsUnit.Pixel);
			return new GraphicsContainer ((uint) token);
		}

		public GraphicsContainer BeginContainer (Rectangle dstrect, Rectangle srcrect, GraphicsUnit unit)
		{
			CheckUse ();
			return BeginContainer ((RectangleF) dstrect, (RectangleF) srcrect, unit);
		}

		public GraphicsContainer BeginContainer (RectangleF dstrect, RectangleF srcrect, GraphicsUnit unit)
		{
			CheckUse ();
			// GdipBeginContainer: the source unit must be a real one (not World or Display).
			if (unit < GraphicsUnit.Pixel || unit > GraphicsUnit.Millimeter)
				throw new ArgumentException ("Parameter is not valid.");
			int token = BeginContainerState (true, dstrect, srcrect, unit);
			return new GraphicsContainer ((uint) token);
		}

		/// <summary>GpGraphics::BeginContainer: state as Save keeps it, then a fresh context whose
		/// world is the identity, its page unit Display, its quality settings the defaults, mapped
		/// through the container transform (<paramref name="rects"/>: src in unit onto dst under the
		/// old world-to-device; otherwise the old page multipliers undone).</summary>
		int BeginContainerState (bool? rects, RectangleF dst, RectangleF src, GraphicsUnit unit)
		{
			float [] o = RecordingMatrix ();
			var outer = new GpMatrix (o [0], o [1], o [2], o [3], o [4], o [5]);
			GpMatrix container;
			if (rects == true) {
				float mx = UnitScale (unit);
				var s2 = new RectangleF (src.X * mx, src.Y * mx, src.Width * mx, src.Height * mx);
				if (!GpGraphics.InferAffine (dst, s2, out GpMatrix c))
					return 0;
				container = GpMatrix.Multiply (c, outer);
			} else {
				GpMatrix c = GpMatrix.CreateIdentity ();
				float f = PageFactor;
				c.Scale (1f / f, 1f / f, false);
				container = GpMatrix.Multiply (c, outer);
			}
			int token = SaveState (true);
			if (mf_rec != null) {
				if (rects == true) mf_rec.BeginContainer (dst, src, unit, (uint) token);
				else mf_rec.BeginContainerNoParams ((uint) token);
			}
			if (gp != null) {
				if (rects == true) gp.BeginContainer (token, dst, src, unit);
				else gp.BeginContainer (token);
			}
			print_clip?.PushClipContainer (token);
			rec_clip?.PushClipContainer (token);
			// The recorder: the container's transform becomes the base every world transform is
			// composed onto, and the quality settings start over.
			rec_container = new float [] { container.M11, container.M12, container.M21, container.M22, container.Dx, container.Dy };
			rec_in_container = true;
			rec_world = new float [] { 1f, 0f, 0f, 1f, 0f, 0f };
			rec_unit = GraphicsUnit.Display;
			rec_page_scale = 1f;
			gpu_smoothing = SmoothingMode.None;
			recorded_text_hint = TextRenderingHint.SystemDefault;
			recorded_text_contrast = 4;
			_compositingMode = CompositingMode.SourceOver;
			GpuRecorder?.SetCompositingMode (false);
			_compositingQuality = CompositingQuality.Default;
			_interpolation = InterpolationMode.Bilinear;
			_pixelOffset = PixelOffsetMode.Default;
			PushRecordedTransform ();
			return token;
		}

		/// <summary>GpGraphics::Save: the recorder's transform and clip stack, the managed state and
		/// the engine's context, all under one token.</summary>
		int SaveState (bool container)
		{
			int token = GpuRecorder != null ? GpuRecorder.SaveState () : ++_stateCounter;
			if (mf_rec != null && !container) mf_rec.Save ((uint) token);
			RecordedSave (token);
			if (gp != null) {
				SyncEngine ();
				if (!container) gp.Save (token);
			}
			if (!container) print_clip?.Save (token);
			if (!container) rec_clip?.Save (token);
			return token;
		}

		void RestoreState (int token)
		{
			GpuRecorder?.RestoreState (token);
			RecordedRestore (token);
			gp?.Restore (token);
			print_clip?.Restore (token);
			rec_clip?.Restore (token);
		}

		public void Clear (Color color)
		{
			CheckUse ();
			CheckTarget ();
						if (EngineClear (color)) return;
			if (image_target != null && GpuRecorder is WebGpuBackend.SceneRecorder rec) {
				// A bitmap's every pixel BECOMES the colour (transparent included) -- a replace, not a
				// blend. With nothing clipping it that is done to the pixels now, and whatever was
				// drawn before is gone; under a clip it is recorded as a SourceCopy fill.
				if (!rec.HasClip) { image_target.ClearTo (color); return; }
				const float Big = 1 << 20;
				rec.SetCompositingMode (true);
				rec.FillRect (-Big, -Big, Big * 2, Big * 2, color.ToArgb ());
				rec.SetCompositingMode (_compositingMode == CompositingMode.SourceCopy);
				return;
			}
			if (GpuRecorder != null) {
				// Clear fills the ENTIRE surface. A recorder has no surface of its own, but a
				// window's scene is clipped to that window, so a rectangle large enough to cover
				// anything comes out as exactly the window. Skipping it left every control that
				// clears its background transparent -- a ToolStripDropDown paints itself that way,
				// so a context menu came up with its items floating over whatever was behind it.
				const float Big = 1 << 20;
								GpuRecorder.FillRect (-Big, -Big, Big * 2, Big * 2, color.ToArgb ());
				return;
			}
		}
		[MonoLimitation ("Works on Win32 and on X11 (but not on Cocoa and Quartz)")]
		public void CopyFromScreen (Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize)
		{
			CheckUse ();
			CopyFromScreen (upperLeftSource.X, upperLeftSource.Y, upperLeftDestination.X, upperLeftDestination.Y,
				blockRegionSize, CopyPixelOperation.SourceCopy);				
		}

		[MonoLimitation ("Works on Win32 and (for CopyPixelOperation.SourceCopy only) on X11 but not on Cocoa and Quartz")]
		public void CopyFromScreen (Point upperLeftSource, Point upperLeftDestination, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			CheckUse ();
			CopyFromScreen (upperLeftSource.X, upperLeftSource.Y, upperLeftDestination.X, upperLeftDestination.Y,
				blockRegionSize, copyPixelOperation);
		}
		
		[MonoLimitation ("Works on Win32 and on X11 (but not on Cocoa and Quartz)")]
		public void CopyFromScreen (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize)
		{
			CheckUse ();
			CopyFromScreen (sourceX, sourceY, destinationX, destinationY, blockRegionSize,
				CopyPixelOperation.SourceCopy);
		}

		[MonoLimitation ("Works on Win32 and (for CopyPixelOperation.SourceCopy only) on X11 but not on Cocoa and Quartz")]
		public void CopyFromScreen (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			CheckUse ();
			if (!Enum.IsDefined (typeof (CopyPixelOperation), copyPixelOperation))
				throw new InvalidEnumArgumentException (Locale.GetText ("Enum argument value '{0}' is not valid for CopyPixelOperation", copyPixelOperation));

			if (GDIPlus.UseX11Drawable) {
				CopyFromScreenX11 (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else if (GDIPlus.UseCarbonDrawable) {
				CopyFromScreenMac (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else if (GDIPlus.UseCocoaDrawable) {
				CopyFromScreenMac (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			} else {
				CopyFromScreenWin32 (sourceX, sourceY, destinationX, destinationY, blockRegionSize, copyPixelOperation);
			}
		}

		private void CopyFromScreenWin32 (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			IntPtr window = GDIPlus.GetDesktopWindow ();
			IntPtr srcDC = GDIPlus.GetDC (window);
			IntPtr dstDC = GetHdc ();
			GDIPlus.BitBlt (dstDC, destinationX, destinationY, blockRegionSize.Width,
				blockRegionSize.Height, srcDC, sourceX, sourceY, (int) copyPixelOperation);

			GDIPlus.ReleaseDC (IntPtr.Zero, srcDC);
			ReleaseHdc (dstDC);			
		}
		
		private void CopyFromScreenMac (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			throw new NotImplementedException ();
		}

		private void CopyFromScreenX11 (int sourceX, int sourceY, int destinationX, int destinationY, Size blockRegionSize, CopyPixelOperation copyPixelOperation)
		{
			IntPtr window, image, defvisual, vPtr;
			int AllPlanes = ~0, nitems = 0, pixel;

			if (copyPixelOperation != CopyPixelOperation.SourceCopy)
				throw new NotImplementedException ("Operation not implemented under X11");
		
			if (GDIPlus.Display == IntPtr.Zero) {
				GDIPlus.Display = GDIPlus.XOpenDisplay (IntPtr.Zero);
			}

			window = GDIPlus.XRootWindow (GDIPlus.Display, 0);
			defvisual = GDIPlus.XDefaultVisual (GDIPlus.Display, 0);
			XVisualInfo visual = new XVisualInfo ();

			/* Get XVisualInfo for this visual */
			visual.visualid = GDIPlus.XVisualIDFromVisual(defvisual);
			vPtr = GDIPlus.XGetVisualInfo (GDIPlus.Display, 0x1 /* VisualIDMask */, ref visual, ref nitems);
			visual = (XVisualInfo) Marshal.PtrToStructure(vPtr, typeof (XVisualInfo));
#if false
			Console.WriteLine ("visual\t{0}", visual.visual);
			Console.WriteLine ("visualid\t{0}", visual.visualid);
			Console.WriteLine ("screen\t{0}", visual.screen);
			Console.WriteLine ("depth\t{0}", visual.depth);
			Console.WriteLine ("klass\t{0}", visual.klass);
			Console.WriteLine ("red_mask\t{0:X}", visual.red_mask);
			Console.WriteLine ("green_mask\t{0:X}", visual.green_mask);
			Console.WriteLine ("blue_mask\t{0:X}", visual.blue_mask);
			Console.WriteLine ("colormap_size\t{0}", visual.colormap_size);
			Console.WriteLine ("bits_per_rgb\t{0}", visual.bits_per_rgb);
#endif
			image = GDIPlus.XGetImage (GDIPlus.Display, window, sourceX, sourceY, blockRegionSize.Width,
				blockRegionSize.Height, AllPlanes, 2 /* ZPixmap*/);
			if (image == IntPtr.Zero) {
				string s = String.Format ("XGetImage returned NULL when asked to for a {0}x{1} region block", 
					blockRegionSize.Width, blockRegionSize.Height);
				throw new InvalidOperationException (s);
			}
				
			Bitmap bmp = new Bitmap (blockRegionSize.Width, blockRegionSize.Height);
			int red, blue, green;
			int red_mask = (int) visual.red_mask;
			int blue_mask = (int) visual.blue_mask;
			int green_mask = (int) visual.green_mask;
			for (int y = 0; y < blockRegionSize.Height; y++) {
				for (int x = 0; x < blockRegionSize.Width; x++) {
					pixel = GDIPlus.XGetPixel (image, x, y);

					switch (visual.depth) {
						case 16: /* 16bbp pixel transformation */
							red = (int) ((pixel & red_mask ) >> 8) & 0xff;
							green = (int) (((pixel & green_mask ) >> 3 )) & 0xff;
							blue = (int) ((pixel & blue_mask ) << 3 ) & 0xff;
							break;
						case 24:
						case 32:
							red = (int) ((pixel & red_mask ) >> 16) & 0xff;
							green = (int) (((pixel & green_mask ) >> 8 )) & 0xff;
							blue = (int) ((pixel & blue_mask )) & 0xff;
							break;
						default:
							string text = Locale.GetText ("{0}bbp depth not supported.", visual.depth);
							throw new NotImplementedException (text);
					}
						
					bmp.SetPixel (x, y, Color.FromArgb (255, red, green, blue));							 
				}
			}

			DrawImage (bmp, destinationX, destinationY);
			bmp.Dispose ();
			GDIPlus.XDestroyImage (image);
			GDIPlus.XFree (vPtr);
		}

		/// <summary>Called once when this Graphics is disposed, before the recorder is detached.
		/// The GPU-raster driver uses it to fold what was drawn through CreateGraphics into the
		/// window's scene -- otherwise that drawing has nowhere to go.</summary>
		internal Action<Graphics> DisposeHook;

		private void RunDisposeHook ()
		{
			Action<Graphics> hook = DisposeHook;
			if (hook == null)
				return;
			DisposeHook = null;      // once only, even if Dispose is called twice
			hook (this);
		}

		// ---- what GDI+ says before it does anything ------------------------------------------------
		//
		// System.Drawing hands GDI+ its Graphics' handle on every call. Disposed, there is none and GDI+
		// answers InvalidParameter; while GetHdc's DC is out the Graphics is locked and it answers
		// ObjectBusy. An argument that was disposed has no handle either: InvalidParameter again.

		void CheckUse ()
		{
			if (disposed)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (deviceContextHdc != IntPtr.Zero)
				throw new InvalidOperationException (SR.GdiplusObjectBusy);
		}

		// GDI+'s own checks of an arc's or pie's ellipse.
		static void CheckArc (float width, float height)
		{
			if (!(width > 0) || !(height > 0))
				throw new ArgumentException (SR.GdiplusInvalidParameter);
		}

		// A bitmap whose bits are locked cannot be drawn from (WrongState); a metafile still being
		// recorded draws nothing, without complaint.
		static bool SkipSourceImage (Image image)
		{
			if (image is Bitmap b && b.IsLocked)
				throw new InvalidOperationException (SR.GdiplusWrongState);
			return image is Metafile m && m.IsRecording;
		}

		// A bitmap whose bits are locked cannot be drawn on: GDI+ fails the draw (GenericError).
		void CheckTarget ()
		{
			if (image_target != null && image_target.IsLocked)
				throw new ExternalException (SR.GdiplusGenericError, unchecked ((int) 0x80004005));
		}

		static void CheckCombine (CombineMode mode)
		{
			if ((uint) mode > (uint) CombineMode.Complement)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
		}

		/// <summary>What GDI+ checks before any call on this Graphics (disposed, HDC out).</summary>
		internal void CheckUsable () => CheckUse ();

		void CheckUse (object a)
		{
			CheckUse ();
			CheckLive (a);
		}

		void CheckUse (object a, object b)
		{
			CheckUse ();
			CheckLive (a);
			CheckLive (b);
		}

		void CheckUse (object a, object b, object c)
		{
			CheckUse ();
			CheckLive (a);
			CheckLive (b);
			CheckLive (c);
		}

		internal static bool IsDisposedArg (object o)
		{
			switch (o) {
			case null: return false;
			case Pen p: return p.IsDisposed;
			case Brush b: return b.IsDisposed;
			case Image i: return i.disposed;
			case Font f: return f.IsDisposed;
			case Region r: return r.IsDisposed;
			case GraphicsPath gp: return gp.IsDisposed;
			case Matrix m: return m.IsDisposed;
			case Graphics g: return g.disposed;
			case StringFormat sf: return sf.IsDisposed;
			default: return false;
			}
		}

		/// <summary>An argument System.Drawing hands GDI+ by its handle, after Dispose: InvalidParameter.</summary>
		internal static void CheckLive (object o)
		{
			if (IsDisposedArg (o))
				throw new ArgumentException (SR.GdiplusInvalidParameter);
		}

		public void Dispose ()
		{
			if (disposed) return;
			if (mf_rec != null && mf_borrowed) {
				mf_rec = null;
			} else if (mf_rec != null) {
				// GpGraphics::~GpGraphics on a metafile: the recording ends (EndRecording).
				GpMetafileRecorder rec = mf_rec;
				mf_rec = null;
				if (deviceContextHdc != IntPtr.Zero) {
					rec.ReleaseHdc ();
					lock (s_pseudoHdc) s_pseudoHdc.Remove (deviceContextHdc);
					deviceContextHdc = IntPtr.Zero;
				}
				rec.End ();
			}
			if (deviceContextHdc != IntPtr.Zero) {
				try { EndHdc (); } catch (Exception) { }
				deviceContextHdc = IntPtr.Zero;
			}
			RunDisposeHook ();
			if (image_target != null) {
				Bitmap target = image_target;
				if (hdc_surface != null) PresentHdc ();
				image_target = null;
				target.TakeDrawing (this, detach: true);
				if (hdc_surface != null) {
					ForgetOverDc (hdc_surface.Hdc, this);
					// The DC's bitmap was only ever this Graphics' surface.
					if (OperatingSystem.IsWindows ()) {
						hdc_surface.Dispose ();
						if (hdc_owned != IntPtr.Zero) WebGpuBackend.Gdip.GpHdcSurface.ReleaseDC (hdc_window, hdc_owned);
					}
					hdc_surface = null;
					target.Dispose ();
				}
			}
			disposed = true;
			GpuRecorder = null;
			gp = null;
			GC.SuppressFinalize (this);
		}

		/// <summary>TransformPoints in place: through the engine's matrices when it draws, else the
		/// recorded state's.</summary>
		void TransformPointsF (CoordinateSpace dest, CoordinateSpace src, PointF [] pts)
		{
			if (gp == null) { RecordedTransformPoints (dest, src, pts); return; }
			SyncEngine ();
			GpMatrix ToDevice (CoordinateSpace c)
			{
				if (c == CoordinateSpace.Device) return GpMatrix.CreateIdentity ();
				GpMatrix w2d = gp.WorldToDevice;
				if (c == CoordinateSpace.World) return w2d;
				GpMatrix inv = gp.World;
				if (!inv.Invert ()) return w2d;
				return GpMatrix.Multiply (inv, w2d);   // page -> world -> device
			}
			GpMatrix a = ToDevice (src), b = ToDevice (dest);
			if (!b.Invert ()) return;
			GpMatrix m = GpMatrix.Multiply (a, b);
			m.Transform (pts);
		}

		
		public void DrawArc (Pen pen, Rectangle rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (rect.Width, rect.Height);
			DrawArc (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}

		
		public void DrawArc (Pen pen, RectangleF rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (rect.Width, rect.Height);
			DrawArc (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}

		
		public void DrawArc (Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			GfxTrace ("DrawArc", () => $"{x},{y} {width}x{height} pen={pen?.Color} w={pen?.Width}", new RectangleF (x, y, width, height));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (EngineDrawArc (pen, x, y, width, height, startAngle, sweepAngle)) return;
			if (PrintStroke (pen, gp => gp.AddArc (x, y, width, height, startAngle, sweepAngle))) return;
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, startAngle, sweepAngle, ArgbOf (pen), pen.Width); return; }
			/*GDIP*/;
			
		}

		// Microsoft documentation states that the signature for this member should be
		// public void DrawArc( Pen pen,  int x,  int y,  int width,  int height,   int startAngle,
   		// int sweepAngle. However, GdipDrawArcI uses also float for the startAngle and sweepAngle params
   		public void DrawArc (Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (EngineDrawArc (pen, x, y, width, height, startAngle, sweepAngle)) return;
			if (PrintStroke (pen, gp => gp.AddArc (x, y, width, height, startAngle, sweepAngle))) return;
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, startAngle, sweepAngle, ArgbOf (pen), pen.Width); return; }
			/*GDIP*/;
			
		}

		public void DrawBezier (Pen pen, PointF pt1, PointF pt2, PointF pt3, PointF pt4)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawBeziers (pen, new [] { pt1, pt2, pt3, pt4 })) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddBezier (pt1, pt2, pt3, pt4); DrawPath (pen, gp); } return; }
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			/*GDIP*/;
			
		}

		public void DrawBezier (Pen pen, Point pt1, Point pt2, Point pt3, Point pt4)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawBeziers (pen, new PointF [] { pt1, pt2, pt3, pt4 })) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddBezier (pt1, pt2, pt3, pt4); DrawPath (pen, gp); } return; }
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			/*GDIP*/;
			
		}

		public void DrawBezier (Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawBeziers (pen, new [] { new PointF (x1, y1), new PointF (x2, y2), new PointF (x3, y3), new PointF (x4, y4) })) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddBezier (x1, y1, x2, y2, x3, y3, x4, y4); DrawPath (pen, gp); } return; }
			Status status;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			/*GDIP*/;
			
		}

		public void DrawBeziers (Pen pen, Point [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawBeziers (pen, ToF (points))) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddBeziers (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
                        int length = points.Length;
			Status status;

                        if (length < 4)
                                return;

			for (int i = 0; i < length - 1; i += 3) {
                                Point p1 = points [i];
                                Point p2 = points [i + 1];
                                Point p3 = points [i + 2];
                                Point p4 = points [i + 3];

                                /*GDIP*/;
				
                        }
		}

		public void DrawBeziers (Pen pen, PointF [] points)
		{
			if (points == null)
				throw new ArgumentNullException ("points");
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawBeziers (pen, points)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddBeziers (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			int length = points.Length;
			Status status;

                        if (length < 4)
                                return;

			for (int i = 0; i < length - 1; i += 3) {
                                PointF p1 = points [i];
                                PointF p2 = points [i + 1];
                                PointF p3 = points [i + 2];
                                PointF p4 = points [i + 3];

                                /*GDIP*/;
				
                        }
		}

		
		public void DrawClosedCurve (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawClosedCurve (pen, points, 0.5f)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddClosedCurve (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
		
		public void DrawClosedCurve (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawClosedCurve (pen, ToF (points), 0.5f)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddClosedCurve (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
 			
		// according to MSDN fillmode "is required but ignored" which makes _some_ sense since the unmanaged 
		// GDI+ call doesn't support it (issue spotted using Gendarme's AvoidUnusedParametersRule)
		public void DrawClosedCurve (Pen pen, Point [] points, float tension, FillMode fillmode)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawClosedCurve (pen, ToF (points), tension)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddClosedCurve (points, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}

		// according to MSDN fillmode "is required but ignored" which makes _some_ sense since the unmanaged 
		// GDI+ call doesn't support it (issue spotted using Gendarme's AvoidUnusedParametersRule)
		public void DrawClosedCurve (Pen pen, PointF [] points, float tension, FillMode fillmode)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawClosedCurve (pen, points, tension)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddClosedCurve (points, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
		
		public void DrawCurve (Pen pen, Point [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (points != null && EngineDrawCurve (pen, ToF (points), 0.5f, 0, points.Length - 1)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
		
		public void DrawCurve (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (points != null && EngineDrawCurve (pen, points, 0.5f, 0, points.Length - 1)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
		
		public void DrawCurve (Pen pen, PointF [] points, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (points != null && EngineDrawCurve (pen, points, tension, 0, points.Length - 1)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}
		
		public void DrawCurve (Pen pen, Point [] points, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (points != null && EngineDrawCurve (pen, ToF (points), tension, 0, points.Length - 1)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;		
			
		}
		
		public void DrawCurve (Pen pen, PointF [] points, int offset, int numberOfSegments)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawCurve (pen, points, 0.5f, offset, numberOfSegments)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points, offset, numberOfSegments, 0.5f); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}

		public void DrawCurve (Pen pen, Point [] points, int offset, int numberOfSegments, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawCurve (pen, ToF (points), tension, offset, numberOfSegments)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points, offset, numberOfSegments, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}

		public void DrawCurve (Pen pen, PointF [] points, int offset, int numberOfSegments, float tension)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawCurve (pen, points, tension, offset, numberOfSegments)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddCurve (points, offset, numberOfSegments, tension); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			
			Status status;
			/*GDIP*/;
			
		}

		public void DrawEllipse (Pen pen, Rectangle rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			
			DrawEllipse (pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawEllipse (Pen pen, RectangleF rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			DrawEllipse (pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawEllipse (Pen pen, int x, int y, int width, int height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawEllipse (pen, x, y, width, height)) return;
			if (PrintStroke (pen, gp => gp.AddEllipse (x, y, width, height))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			// An ellipse outline is a full circle of arc, and the recorder has an arc primitive. It
			// had no path here at all, so the call went to gdiplus with no surface behind it and drew
			// nothing: a radio button lost its ring and kept only the dot inside it, because the dot
			// is a FILL and fills were already recorded.
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, 0f, 360f, ArgbOf (pen), pen.Width); return; }
			Status status;
			/*GDIP*/;
			
		}

		public void DrawEllipse (Pen pen, float x, float y, float width, float height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawEllipse (pen, x, y, width, height)) return;
			if (PrintStroke (pen, gp => gp.AddEllipse (x, y, width, height))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) { GpuRecorder.DrawArc (x, y, width, height, 0f, 360f, ArgbOf (pen), pen.Width); return; }
			/*GDIP*/;
			
		}

		public void DrawIcon (Icon icon, Rectangle targetRect)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");
			CheckUse ();
			CheckTarget ();

			DrawImage (icon.GetInternalBitmap (), targetRect);
		}

		public void DrawIcon (Icon icon, int x, int y)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");
			CheckUse ();
			CheckTarget ();

			DrawImage (icon.GetInternalBitmap (), x, y);
		}

		public void DrawIconUnstretched (Icon icon, Rectangle targetRect)
		{
			if (icon == null)
				throw new ArgumentNullException ("icon");
			CheckUse ();
			CheckTarget ();

			DrawImageUnscaled (icon.GetInternalBitmap (), targetRect);
		}
		
		public void DrawImage (Image image, RectangleF rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, rect)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, rect.X, rect.Y, rect.Width, rect.Height)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, PointF point)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, point.X, point.Y)) return;
			if (print_mode && image != null) { SizeF ps = PrintImageSize (image); DrawImage (image, point.X, point.Y, ps.Width, ps.Height); return; }
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImageAtPoint (image, point.X, point.Y)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point [] destPoints)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, EnginePoints (destPoints), null, GraphicsUnit.Pixel, null)) return;
			if (RecordImagePoints (image, ToF (destPoints), null, GraphicsUnit.Pixel, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point point)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, point.X, point.Y)) return;
			if (print_mode && image != null) { DrawImage (image, (float) point.X, (float) point.Y); return; }
			if (image == null)
				throw new ArgumentNullException ("image");
			DrawImage (image, point.X, point.Y);
		}

		public void DrawImage (Image image, Rectangle rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, rect)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			DrawImage (image, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawImage (Image image, PointF [] destPoints)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destPoints, null, GraphicsUnit.Pixel, null)) return;
			if (RecordImagePoints (image, destPoints, null, GraphicsUnit.Pixel, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, int x, int y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y)) return;
			if (print_mode && image != null) { SizeF ps = PrintImageSize (image); DrawImage (image, x, y, ps.Width, ps.Height); return; }
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImageAtPoint (image, x, y)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, float x, float y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y)) return;
			if (print_mode && image != null) { SizeF ps = PrintImageSize (image); DrawImage (image, x, y, ps.Width, ps.Height); return; }
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImageAtPoint (image, x, y)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, srcRect, srcUnit, null)) return;
			if (RecordImage (image, destRect, srcRect, srcUnit, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, RectangleF destRect, RectangleF srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, srcRect, srcUnit, null)) return;
			if (RecordImage (image, destRect, srcRect, srcUnit, null)) return;			
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, EnginePoints (destPoints), srcRect, srcUnit, null)) return;
			if (RecordImagePoints (image, ToF (destPoints), srcRect, srcUnit, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destPoints, srcRect, srcUnit, null)) return;
			if (RecordImagePoints (image, destPoints, srcRect, srcUnit, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, 
                                ImageAttributes imageAttr)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, EnginePoints (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, ToF (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, float x, float y, float width, float height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, new RectangleF (x, y, width, height))) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, width, height)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, 
                                ImageAttributes imageAttr)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, int x, int y, Rectangle srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y, srcRect, srcUnit)) return;
			if (RecordImage (image, new RectangleF (x, y, srcRect.Width, srcRect.Height), srcRect, srcUnit, null)) return;			
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, int x, int y, int width, int height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, new RectangleF (x, y, width, height))) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (RecordImage (image, x, y, width, height)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, float x, float y, RectangleF srcRect, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y, srcRect, srcUnit)) return;
			if (RecordImage (image, new RectangleF (x, y, srcRect.Width, srcRect.Height), srcRect, srcUnit, null)) return;			
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (IntPtr.Zero))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (IntPtr.Zero))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, EnginePoints (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, ToF (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Point [] destPoints, Rectangle srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback, int callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback ((IntPtr) callbackData))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, EnginePoints (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, ToF (destPoints), srcRect, srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");

			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, null)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, PointF [] destPoints, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback, int callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			if (destPoints == null)
				throw new ArgumentNullException ("destPoints");
			if (destPoints != null && destPoints.Length != 3 && destPoints.Length != 4)
				throw new ArgumentException (SR.GdiplusDestPointsInvalidLength);
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback ((IntPtr) callbackData))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			if (RecordImagePoints (image, destPoints, srcRect, srcUnit, imageAttr)) return;
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, null)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, null)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttr)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttr)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttr)) return;			
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttr, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (IntPtr.Zero))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttr)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttr)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}
		
		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (IntPtr.Zero))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, float srcX, float srcY, float srcWidth, float srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback, IntPtr callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (callbackData))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}

		public void DrawImage (Image image, Rectangle destRect, int srcX, int srcY, int srcWidth, int srcHeight, GraphicsUnit srcUnit, ImageAttributes imageAttrs, DrawImageAbort callback, IntPtr callbackData)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			// GDI+ asks the abort callback as it draws; one that answers true ends the call.
			if (callback != null && callback (callbackData))
				throw new ExternalException (SR.GdiplusAborted, unchecked ((int) 0x80004004));
			if (EngineDrawImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (RecordImage (image, destRect, new RectangleF (srcX, srcY, srcWidth, srcHeight), srcUnit, imageAttrs)) return;
			if (image == null)
				throw new ArgumentNullException ("image");
			/*GDIP*/;
			
		}		
		
		public void DrawImageUnscaled (Image image, Point point)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			DrawImageUnscaled (image, point.X, point.Y);
		}
		
		public void DrawImageUnscaled (Image image, Rectangle rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			DrawImageUnscaled (image, rect.X, rect.Y, rect.Width, rect.Height);
		}
		
		public void DrawImageUnscaled (Image image, int x, int y)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y)) return;
			// GDI+'s DrawImageUnscaled is DrawImage at a point: the image's physical size.
			DrawImage (image, x, y);
		}

		public void DrawImageUnscaled (Image image, int x, int y, int width, int height)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;
			if (EngineDrawImage (image, x, y)) return;

			// avoid creating an empty, or negative w/h, bitmap...
			if ((width <= 0) || (height <= 0))
				return;

			using (Image tmpImg = new Bitmap (width, height)) {
				using (Graphics g = FromImage (tmpImg)) {
					g.DrawImage (image, 0, 0, image.Width, image.Height);
					DrawImage (tmpImg, x, y, width, height);
				}
			}
		}

		public void DrawImageUnscaledAndClipped (Image image, Rectangle rect)
		{
			if (image == null)
				throw new ArgumentNullException ("image");
			CheckUse (image);
			CheckTarget ();
			if (SkipSourceImage (image)) return;

			int width = (image.Width > rect.Width) ? rect.Width : image.Width;
			int height = (image.Height > rect.Height) ? rect.Height : image.Height;
			if (EngineDrawImage (image, rect, new RectangleF (0, 0, width, height), GraphicsUnit.Pixel, null)) return;

			DrawImageUnscaled (image, rect.X, rect.Y, width, height);			
		}

		public void DrawLine (Pen pen, PointF pt1, PointF pt2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawLine (pen, pt1.X, pt1.Y, pt2.X, pt2.Y)) return;
			if (GpuRecorder != null) { DrawLine (pen, pt1.X, pt1.Y, pt2.X, pt2.Y); return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
                        /*GDIP*/;
			
		}

		public void DrawLine (Pen pen, Point pt1, Point pt2)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawLine (pen, pt1.X, pt1.Y, pt2.X, pt2.Y)) return;
			if (PrintStroke (pen, gp => gp.AddLine (pt1, pt2))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				if (RecordDash (pen, out float [] dash))
					GpuRecorder.DrawDashedLine (pt1.X, pt1.Y, pt2.X, pt2.Y, ArgbOf (pen), pen.Width, dash);
				else
					RecordSolidLine (pen, pt1.X, pt1.Y, pt2.X, pt2.Y);
				return;
			}
                        /*GDIP*/;
			
		}

		public void DrawLine (Pen pen, int x1, int y1, int x2, int y2)
		{
			GfxTrace ("DrawLine", () => $"{x1},{y1}-{x2},{y2} pen={pen?.Color} w={pen?.Width}", RectangleF.FromLTRB (Math.Min (x1, x2), Math.Min (y1, y2), Math.Max (x1, x2) + 1, Math.Max (y1, y2) + 1));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawLine (pen, x1, y1, x2, y2)) return;
			if (PrintStroke (pen, gp => gp.AddLine (x1, y1, x2, y2))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				if (RecordDash (pen, out float [] dash))
					GpuRecorder.DrawDashedLine (x1, y1, x2, y2, ArgbOf (pen), pen.Width, dash);
				else
					RecordSolidLine (pen, x1, y1, x2, y2);
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawLine (Pen pen, float x1, float y1, float x2, float y2)
		{
			GfxTrace ("DrawLineF", () => $"{x1},{y1}-{x2},{y2} pen={pen?.Color} w={pen?.Width}", RectangleF.FromLTRB (Math.Min (x1, x2), Math.Min (y1, y2), Math.Max (x1, x2) + 1, Math.Max (y1, y2) + 1));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawLine (pen, x1, y1, x2, y2)) return;
			if (PrintStroke (pen, gp => gp.AddLine (x1, y1, x2, y2))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (!float.IsNaN(x1) && !float.IsNaN(y1) &&
			    !float.IsNaN(x2) && !float.IsNaN(y2)) {
				if (RecordPen (pen)) {
					if (RecordDash (pen, out float [] dash))
						GpuRecorder.DrawDashedLine (x1, y1, x2, y2, ArgbOf (pen), pen.Width, dash);
					else
						RecordSolidLine (pen, x1, y1, x2, y2);
					return;
				}
				/*GDIP*/;
				
			}
		}

		public void DrawLines (Pen pen, PointF [] points)
		{
			GfxTrace ("DrawLinesF", () => $"pen={pen?.Color} w={pen?.Width} n={points?.Length}", PointsBox (points));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 2.
			if (points != null && points.Length < 2)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawLines (pen, points, false)) return;
			if (points != null && PrintStroke (pen, gp => gp.AddLines (points))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 1; i < points.Length; i++)
					GpuRecorder.DrawLine (points[i-1].X, points[i-1].Y, points[i].X, points[i].Y, c, pen.Width);
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawLines (Pen pen, Point [] points)
		{
			GfxTrace ("DrawLines", () => $"pen={pen?.Color} w={pen?.Width} n={points?.Length}", PointsBox (points == null ? null : Array.ConvertAll (points, q => (PointF) q)));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 2.
			if (points != null && points.Length < 2)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawLines (pen, points, false)) return;
			if (points != null && PrintStroke (pen, gp => gp.AddLines (points))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 1; i < points.Length; i++)
					GpuRecorder.DrawLine (points[i-1].X, points[i-1].Y, points[i].X, points[i].Y, c, pen.Width);
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawPath (Pen pen, GraphicsPath path)
		{
			GfxTrace ("DrawPath", () => $"pen={pen?.Color} w={pen?.Width} pts={PathDesc (path)}", path?.GetBounds () ?? RectangleF.Empty);
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (path == null)
				throw new ArgumentNullException ("path");
			CheckUse (pen, path);
			CheckTarget ();
			if (mf_rec != null && pen != null && path != null) { mf_rec.DrawPath (pen, path); return; }
			if (path != null && EngineDrawPath (pen, path.gp)) return;
			if (path != null && PrintStroke (pen, path)) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (path == null)
				throw new ArgumentNullException ("path");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				RecordDash (pen, out float [] dash);
				foreach (PointF [] sub in FlattenSubpaths (path)) {
					for (int i = 1; i < sub.Length; i++)
						if (dash != null)
							GpuRecorder.DrawDashedLine (sub [i - 1].X, sub [i - 1].Y, sub [i].X, sub [i].Y, c, pen.Width, dash);
						else
							GpuRecorder.DrawLine (sub [i - 1].X, sub [i - 1].Y, sub [i].X, sub [i].Y, c, pen.Width);
					// A flattened closed figure does not repeat its first point; join it up.
					if (sub.Length > 2 && sub [0] != sub [sub.Length - 1])
						GpuRecorder.DrawLine (sub [sub.Length - 1].X, sub [sub.Length - 1].Y, sub [0].X, sub [0].Y, c, pen.Width);
				}
				return;
			}
			/*GDIP*/;
			
		}
		
		public void DrawPie (Pen pen, Rectangle rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (rect.Width, rect.Height);
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawPie (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}
		
		public void DrawPie (Pen pen, RectangleF rect, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (rect.Width, rect.Height);
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawPie (pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}
		
		public void DrawPie (Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (EngineDrawPie (pen, x, y, width, height, startAngle, sweepAngle)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath ()) { gp.AddPie (x, y, width, height, startAngle, sweepAngle); DrawPath (pen, gp); } return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			/*GDIP*/;
			
		}
		
		// Microsoft documentation states that the signature for this member should be
		// public void DrawPie(Pen pen, int x,  int y,  int width,   int height,   int startAngle
   		// int sweepAngle. However, GdipDrawPieI uses also float for the startAngle and sweepAngle params
   		public void DrawPie (Pen pen, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (EngineDrawPie (pen, x, y, width, height, startAngle, sweepAngle)) return;
			if (GpuRecorder != null) { DrawPie (pen, (float) x, (float) y, (float) width, (float) height, (float) startAngle, (float) sweepAngle); return; }
			if (pen == null)
				throw new ArgumentNullException ("pen");
			/*GDIP*/;
			
		}

		public void DrawPolygon (Pen pen, Point [] points)
		{
			GfxTrace ("DrawPolygon", () => $"pen={pen?.Color} w={pen?.Width} n={points?.Length}", PointsBox (points == null ? null : Array.ConvertAll (points, q => (PointF) q)));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 2.
			if (points != null && points.Length < 2)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawLines (pen, points, true)) return;
			if (points != null && PrintStroke (pen, gp => gp.AddPolygon (points))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 0; i < points.Length; i++) {
					var a = points[i]; var b = points[(i + 1) % points.Length];   // closed
					GpuRecorder.DrawLine (a.X, a.Y, b.X, b.Y, c, pen.Width);
				}
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawPolygon (Pen pen, PointF [] points)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 2.
			if (points != null && points.Length < 2)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawLines (pen, points, true)) return;
			if (points != null && PrintStroke (pen, gp => gp.AddPolygon (points))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				for (int i = 0; i < points.Length; i++) {
					var a = points[i]; var b = points[(i + 1) % points.Length];   // closed
					GpuRecorder.DrawLine (a.X, a.Y, b.X, b.Y, c, pen.Width);
				}
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawRectangle (Pen pen, RectangleF rect)
		{
			GfxTrace ("DrawRectangleF", () => $"{rect} pen={pen?.Color} w={pen?.Width}", rect);
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawRectangle (pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void DrawRoundedRectangle (Pen pen, Rectangle rect, Size radius)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			DrawRoundedRectangle (pen, (RectangleF) rect, (SizeF) radius);
		}

		public void DrawRoundedRectangle (Pen pen, RectangleF rect, SizeF radius)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			using (GraphicsPath path = new GraphicsPath ()) {
				path.AddRoundedRectangle (rect, radius);
				DrawPath (pen, path);
			}
		}

		public void FillRoundedRectangle (Brush brush, Rectangle rect, Size radius)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			FillRoundedRectangle (brush, (RectangleF) rect, (SizeF) radius);
		}

		public void FillRoundedRectangle (Brush brush, RectangleF rect, SizeF radius)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			using (GraphicsPath path = new GraphicsPath ()) {
				path.AddRoundedRectangle (rect, radius);
				FillPath (brush, path);
			}
		}

		public void FillPie (Brush brush, RectangleF rect, float startAngle, float sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			FillPie (brush, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
		}

		public void DrawRectangle (Pen pen, Rectangle rect)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			DrawRectangle (pen, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void DrawRectangle (Pen pen, float x, float y, float width, float height)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawRects (pen, new [] { new RectangleF (x, y, width, height) })) return;
			if (PrintStroke (pen, gp => gp.AddRectangle (new RectangleF (x, y, width, height)))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				if (RecordDash (pen, out float [] dash)) {
					GpuRecorder.DrawDashedLine (x, y, x + width, y, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y + height, x + width, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y, x, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x + width, y, x + width, y + height, c, pen.Width, dash);
					return;
				}
				GpuRecorder.DrawLine (x, y, x + width, y, c, pen.Width);
				GpuRecorder.DrawLine (x, y + height, x + width, y + height, c, pen.Width);
				GpuRecorder.DrawLine (x, y, x, y + height, c, pen.Width);
				GpuRecorder.DrawLine (x + width, y, x + width, y + height, c, pen.Width);
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawRectangle (Pen pen, int x, int y, int width, int height)
		{
			GfxTrace ("DrawRectangle", () => $"{x},{y} {width}x{height} pen={pen?.Color} w={pen?.Width}", new RectangleF (x, y, width + 1, height + 1));
			if (pen == null)
				throw new ArgumentNullException ("pen");
			CheckUse (pen);
			CheckTarget ();
			if (EngineDrawRects (pen, new [] { new RectangleF (x, y, width, height) })) return;
			if (PrintStroke (pen, gp => gp.AddRectangle (new RectangleF (x, y, width, height)))) return;
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (RecordPen (pen)) {
				int c = ArgbOf (pen);
				if (RecordDash (pen, out float [] dash)) {
					GpuRecorder.DrawDashedLine (x, y, x + width, y, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y + height, x + width, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x, y, x, y + height, c, pen.Width, dash);
					GpuRecorder.DrawDashedLine (x + width, y, x + width, y + height, c, pen.Width, dash);
					return;
				}
				GpuRecorder.DrawLine (x, y, x + width, y, c, pen.Width);
				GpuRecorder.DrawLine (x, y + height, x + width, y + height, c, pen.Width);
				GpuRecorder.DrawLine (x, y, x, y + height, c, pen.Width);
				GpuRecorder.DrawLine (x + width, y, x + width, y + height, c, pen.Width);
				return;
			}
			/*GDIP*/;
			
		}

		public void DrawRectangles (Pen pen, RectangleF [] rects)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (rects != null && rects.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (EngineDrawRects (pen, rects)) return;
			if (GpuRecorder != null) { foreach (RectangleF r in rects) DrawRectangle (pen, r.X, r.Y, r.Width, r.Height); return; }
			if (pen == null)
				throw new ArgumentNullException ("image");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			/*GDIP*/;
			
		}

		public void DrawRectangles (Pen pen, Rectangle [] rects)
		{
			if (pen == null)
				throw new ArgumentNullException ("pen");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			CheckUse (pen);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (rects != null && rects.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (rects != null && EngineDrawRects (pen, Array.ConvertAll (rects, r => (RectangleF) r))) return;
			if (GpuRecorder != null) { foreach (Rectangle r in rects) DrawRectangle (pen, r); return; }
			if (pen == null)
				throw new ArgumentNullException ("image");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			/*GDIP*/;
			
		}

		public void DrawString (string s, Font font, Brush brush, RectangleF layoutRectangle)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			DrawString (s, font, brush, layoutRectangle, null);
		}

		public void DrawString (string s, Font font, Brush brush, PointF point)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			DrawString (s, font, brush, new RectangleF (point.X, point.Y, 0, 0), null);
		}

		public void DrawString (string s, Font font, Brush brush, PointF point, StringFormat format)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			// A disposed format hands GDI+ none: the default format.
			if (format != null && format.IsDisposed) format = null;
			DrawString(s, font, brush, new RectangleF(point.X, point.Y, 0, 0), format);
		}

		public void DrawString (string s, Font font, Brush brush, float x, float y)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			DrawString (s, font, brush, new RectangleF (x, y, 0, 0), null);
		}

		public void DrawString (string s, Font font, Brush brush, float x, float y, StringFormat format)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			// A disposed format hands GDI+ none: the default format.
			if (format != null && format.IsDisposed) format = null;
			DrawString (s, font, brush, new RectangleF(x, y, 0, 0), format);
		}

		/// <summary>Remove the hotkey markers from <paramref name="text"/>, reporting where the
		/// mnemonic character ended up. "&amp;&amp;" is a literal ampersand, as in Win32.</summary>
		static string StripHotkeyPrefix (string text, out int index)
		{
			index = -1;
			if (text.IndexOf ('&') < 0) return text;

			var sb = new System.Text.StringBuilder (text.Length);
			for (int i = 0; i < text.Length; i++) {
				if (text[i] != '&') { sb.Append (text[i]); continue; }
				if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append ('&'); i++; continue; }
				if (i + 1 < text.Length && index < 0) index = sb.Length;
			}
			return sb.ToString ();
		}

		/// <summary>Break a string into the lines GDI+ would draw it as: at its own newlines, and
		/// again wherever a line runs past the layout rectangle. The recorder draws a run wherever
		/// it is told and has no idea a rectangle was involved, so a paragraph handed to DrawString
		/// with a width came out as one very long line -- the scrolling credits in SharpDevelop's
		/// About box ran off the side of the dialog instead of filling the column.</summary>
		static string[] WrapLines (string text, float emPx, int sims, string family, float width, StringFormat format)
		{
			string[] hard = text.Split ('\n');
			// A rectangle with no width is a point, not a column; NoWrap is the caller saying so
			// outright. Either way GDI+ lets the line run.
			if (width <= 0 || (format != null && (format.FormatFlags & StringFormatFlags.NoWrap) != 0))
				return hard;

			var outLines = new System.Collections.Generic.List<string> (hard.Length);
			foreach (string raw in hard) {
				string line = raw.TrimEnd ('\r');
				if (line.Length == 0) { outLines.Add (line); continue; }

				float lineWidth;
				WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out lineWidth, out float _);
				if (lineWidth <= width) { outLines.Add (line); continue; }

				// Break at spaces, and only inside a word when a single word is wider than the
				// column -- which is what GDI+ does with a long path or identifier.
				int start = 0;
				while (start < line.Length) {
					int fit = FitCount (line, start, emPx, sims, family, width);
					int brk = -1;
					for (int i = start + fit - 1; i > start; i--)
						if (line[i] == ' ') { brk = i; break; }
					int take = brk > start ? brk - start : fit;
					outLines.Add (line.Substring (start, take));
					start += take;
					while (start < line.Length && line[start] == ' ') start++;
				}
			}
			return outLines.ToArray ();
		}

		/// <summary>How many characters from <paramref name="start"/> fit in <paramref name="width"/>,
		/// at least one so a column narrower than a single glyph still makes progress.</summary>
		static int FitCount (string line, int start, float emPx, int sims, string family, float width)
		{
			int lo = 1, hi = line.Length - start;
			while (lo < hi) {
				int mid = (lo + hi + 1) / 2;
				WebGpuBackend.GpuRaster.MeasureText (line.Substring (start, mid), emPx, sims, family, out float w, out float _);
				if (w <= width) lo = mid; else hi = mid - 1;
			}
			return lo;
		}

		/// <summary>How far down a line box the baseline sits: the font's ascent, truncated the
		/// way Windows truncates a scaled metric. Segoe UI at nine point asks for 12.95 pixels
		/// and Windows uses 12, which is why text drawn on the fraction sat a pixel low.</summary>
		/// <summary>How far below the top of the line box the baseline sits -- the TEXTMETRIC
		/// ascent, which is what decides the row every glyph in the application lands on.
		/// <para>The FACE answers it, because the answer is 'VDMX': the line box is how far the
		/// HINTED outlines reach, and hinting moves them by more than rounding. Scaling the design
		/// ascent and truncating -- what this used to do -- gives 10 for Arial at twelve pixels an
		/// em where GDI gives 12, and 10 for Times New Roman where GDI gives 12. Both faces drew
		/// TWO PIXELS ABOVE Windows', every style and every band of them; it read as a hinting
		/// fault for a long time because a two-pixel shift makes err/ink saturate exactly the way
		/// bad fitting does. The faces that agreed with the old formula -- Segoe UI, Verdana,
		/// Tahoma, Consolas -- are the ones whose truncated design ascent happens to equal their
		/// VDMX ascent, which is why this hid behind four correct faces.</para></summary>
		/// <summary>GDI+'s baseline: the cell ascent in pixels, rounded.</summary>
		static float GdiPlusAscent (Font font, float emPx)
		{
			FontFamily family = font.FontFamily;
			if (family == null)
				return MathF.Round (0.8f * emPx);
			try {
				int em = family.GetEmHeight (font.Style);
				if (em <= 0)
					return MathF.Round (0.8f * emPx);
				return MathF.Round (family.GetCellAscent (font.Style) * emPx / em);
			} catch (Exception) {
				return MathF.Round (0.8f * emPx);
			}
		}

		static float Ascent (Font font, float emPx)
		{
			FontFamily family = font.FontFamily;
			if (family == null)
				return 0.8f * emPx;
			try {
				if (WebGpuBackend.TextMetrics.TryGetGdiLineMetrics (
						family.Name, font.Bold, font.Italic, emPx, out int a, out int _) && a > 0)
					return a;
				int em = family.GetEmHeight (font.Style);
				if (em <= 0)
					return 0.8f * emPx;
				return (float) Math.Floor (family.GetCellAscent (font.Style) * emPx / em);
			} catch (Exception) {
				return 0.8f * emPx;
			}
		}

		/// <summary>The margin a string is laid out inside: a sixth of the font's height, which is
		/// what both GDI+ and Windows leave for a glyph that overhangs its cell. A typographic
		/// format asks for none -- that is what it is for.</summary>
		/// <summary>How far below the baseline the line box reaches, truncated as Windows
		/// truncates it.</summary>
		/// <summary>Whether this drawing is standing in for GDI rather than GDI+ -- set while
		/// TextRenderer draws through here, since the two align a line of text differently.</summary>
		internal bool gdi_text_metrics;

		/// <summary>Set while the port's own controls draw text through DrawString (DrawStringMono).
		/// Mono's controls call DrawString where .NET draws with GDI, so they keep GDI's baseline --
		/// tmAscent below the top -- while a caller of the public DrawString gets GDI+'s.</summary>
		internal bool gdi_ascent;

		// The port's controls: DrawString with GDI's baseline. See gdi_ascent.
		internal void DrawStringMono (string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat format)
		{
			bool saved = gdi_ascent;
			gdi_ascent = true;
			try { DrawString (s, font, brush, layoutRectangle, format); } finally { gdi_ascent = saved; }
		}
		internal void DrawStringMono (string s, Font font, Brush brush, RectangleF layoutRectangle)
			=> DrawStringMono (s, font, brush, layoutRectangle, null);
		internal void DrawStringMono (string s, Font font, Brush brush, PointF point)
			=> DrawStringMono (s, font, brush, new RectangleF (point.X, point.Y, 0, 0), null);
		internal void DrawStringMono (string s, Font font, Brush brush, PointF point, StringFormat format)
			=> DrawStringMono (s, font, brush, new RectangleF (point.X, point.Y, 0, 0), format);
		internal void DrawStringMono (string s, Font font, Brush brush, float x, float y)
			=> DrawStringMono (s, font, brush, new RectangleF (x, y, 0, 0), null);
		internal void DrawStringMono (string s, Font font, Brush brush, float x, float y, StringFormat format)
			=> DrawStringMono (s, font, brush, new RectangleF (x, y, 0, 0), format);

		/// <summary>Set while a control that stands in for a NATIVE Win32 one paints its text.
		/// Windows' own month calendar draws its "Today: ..." line without pair kerning; a WinForms
		/// Label carrying the same string kerns it. See GlyphRunDraw.NoKerningSimulation.</summary>
		internal bool no_kerning;
		/// <summary>This Graphics stands for a memory DC (see Control.GdiTextOnMemorySurface).</summary>
		internal bool memory_surface_text;

		/// <summary>Set while TextOutGdi draws a line for TextRenderer: kern from GPOS.</summary>
		internal bool gpos_kerning;

		/// <summary>TextRenderingHint on a recording Graphics, which has no GDI+ object to hold it.
		/// The single-bit hints draw the run bi-level (GlyphRunDraw.BiLevelSimulation); TextRenderer
		/// reads it too, as .NET's maps it to the font's quality (NONANTIALIASED_QUALITY).</summary>
		TextRenderingHint recorded_text_hint = TextRenderingHint.SystemDefault;

		/// <summary>TextContrast on a recording Graphics (GDI+'s default is 4): the gamma table
		/// GDI+'s text blend goes through (see TryDrawGdiPlusText).</summary>
		int recorded_text_contrast = 4;

		bool BiLevelText => recorded_text_hint == TextRenderingHint.SingleBitPerPixel
			|| recorded_text_hint == TextRenderingHint.SingleBitPerPixelGridFit;

		/// <summary>GDI's TextOut with TA_TOP | TA_LEFT: one run, no wrapping and no margin, its cell's
		/// top-left at (x, y) -- so its baseline is tmAscent below y. The primitive DrawTextEx draws
		/// each of its lines with (see System.Windows.Forms.GdiDrawText).</summary>
		internal void TextOutGdi (string s, Font font, Color color, int x, int y)
		{
			if (string.IsNullOrEmpty (s))
				return;
			// Kerned from GPOS, as DrawText's Uniscribe path kerns (WebGpuBackend.TextMetrics.MeasureGdiRun).
			bool saved = gpos_kerning;
			gpos_kerning = true;
			try {
				using (var brush = new SolidBrush (color))
				using (var format = (StringFormat) StringFormat.GenericTypographic.Clone ()) {
					format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
					format.HotkeyPrefix = Text.HotkeyPrefix.None;
					format.Trimming = StringTrimming.None;
					DrawStringGdi (s, font, brush, new RectangleF (x, y, 1e6f, 1e6f), format);
				}
			} finally {
				gpos_kerning = saved;
			}
		}

		/// <summary>Draw a string the way GDI would place it. TextRenderer means GDI, and the only
		/// difference that reaches this far is which box a centred line is centred in.</summary>
		internal void DrawStringGdi (string s, Font font, Brush brush, RectangleF layoutRectangle,
					     StringFormat format)
		{
			bool saved = gdi_text_metrics;
			gdi_text_metrics = true;
			try {
				DrawString (s, font, brush, layoutRectangle, format);
			} finally {
				gdi_text_metrics = saved;
			}
		}

		static float Descent (Font font, float emPx)
		{
			FontFamily family = font.FontFamily;
			if (family == null)
				return 0.2f * emPx;
			try {
				// From the same place as the ascent, and for the same reason: the two are one box.
				if (WebGpuBackend.TextMetrics.TryGetGdiLineMetrics (
						family.Name, font.Bold, font.Italic, emPx, out int _, out int d) && d > 0)
					return d;
				int em = family.GetEmHeight (font.Style);
				if (em <= 0)
					return 0.2f * emPx;
				return (float) Math.Floor (family.GetCellDescent (font.Style) * emPx / em);
			} catch (Exception) {
				return 0.2f * emPx;
			}
		}

		static float Overhang (Font font, StringFormat format)
		{
			if (format != null && format.IsTypographic)
				return 0f;
			// THIS MARGIN IS THE TEXT'S LEFT ORIGIN, so a one-pixel error moves an entire run, and
			// there IS one -- at 10ppem and only there. Left as it is because every attempt to fix it
			// cost more elsewhere than it gained; written down so the next attempt starts here.
			//
			// Measured on the text specimen (PAIR_SPECIMEN=text) by asking each row for the rigid
			// horizontal shift that best matches Windows: at 10ppem twelve of the twenty-four rows
			// want +1 and the other twelve want 0, and taking it removes 84-99% of those rows'
			// difference -- 6.2M of that size's 9.4M. At 9, 11, 12, 13, 16 and 20ppem every row
			// already wants 0, so ceiling is right there.
			//
			// It is NOT a function of Font.Height, which is what killed the obvious fixes. The same
			// height needs different margins at different sizes:
			//
			//     Verdana   10ppem Height 13 needs 2     11ppem Height 14 needs 3
			//     Arial     10ppem Height 12 needs 2     11ppem Height 13 needs 3
			//     Segoe UI  10ppem Height 14 needs 2     11ppem Height 15 needs 3
			//
			// so Height 13 needs 2 at one size and 3 at the next. Truncation fixes 10ppem and breaks
			// 11/12/16 (total 30.6M -> 110.0M); round-half-up likewise (-> 53.6M). Our Font.Height
			// agrees with Windows' exactly for all six faces at every size checked, so the
			// disagreement is in what GDI+ does with it, not in the metric.
			//
			// Note also that the SAME formula in TextRenderer.GlyphOverhang is not on this path at
			// all -- our Label draws through Graphics.DrawString, not TextRenderer -- so changing it
			// moves nothing here and does move MeasureText, hence AutoSize widths. Change one at a
			// time and re-measure; changing both together is what made the first attempt unreadable.
			// NARROWED FURTHER, and the answer is that GDI+'s margin is not a function of Height.
			// At 16ppem Verdana and Tahoma both have Font.Height 20 -- ours and Windows' agree on that
			// exactly -- and both faces' 'a' has left side bearing 0 in both stacks when drawn at a
			// fixed pen (InkLeft_AgainstGdis). Yet in a Label:
			//
			//     Verdana   our origin 4   Windows' 3
			//     Tahoma    our origin 4   Windows' 4
			//
			// Same height, same lsb, different margin, so no rounding of Height/6 can produce both.
			// Verdana is the TALLER of the two by float line spacing (19.445 against 19.310) and gets
			// the SMALLER margin, so it is not monotone in height either.
			//
			// Worth chasing: it is 64% of the difference at 16ppem (a rigid one-pixel shift of all four
			// Verdana styles, 82-91% of each row's error). The run origin is confirmed as the cause --
			// WGPU_TRACE_TEXT shows both faces emitted at originX 4 -- so the remaining unknown is what
			// GDI+ derives ITS margin from. Try the face's own overhang metrics next (OS/2, hhea, the
			// glyph bounding box against the advance), not the line height.
			// A SIXTH OF GDI'S tmHeight, ROUNDED UP -- not a sixth of GDI+'s Font.Height.
			// This margin is the text's left ORIGIN, so being wrong by one moves an entire run, and it
			// was wrong for some faces at some sizes. TextRenderer wraps an HFONT and takes its height
			// from GDI's TEXTMETRIC; Font.Height comes from GDI+'s line spacing. The two disagree, and
			// per FACE, which is what defeated four attempts to fit a rounding to Font.Height:
			//
			//   16ppem   Arial  Font.Height 19 tmHeight 18 -> 3     Times  19 / 19 -> 4
			//            Verdana            20 /       18 -> 3      Tahoma 20 / 19 -> 4
			//
			// Equal Font.Height, different tmHeight, different margin. Measured against Windows with a
			// stem glyph and a near-black threshold (Probe.Margins, Probe.GdiMetrics), ceil(tmHeight/6)
			// is right in 23 of 24 face-and-size cases; the one miss is Times at 10ppem, which is also
			// the single case the independent displacement check disagreed with, so it is the
			// measurement rather than the rule.
			//
			// The face knows GDI's rule already -- TryGetGdiLineMetrics is the same VDMX-aware path the
			// line box uses -- so this asks it rather than re-deriving it. Font.Height remains the
			// fallback for a family that will not resolve.
			try {
				if (font.FontFamily is FontFamily fam
				    && WebGpuBackend.TextMetrics.TryGetGdiLineMetrics (
					    fam.Name, font.Bold, font.Italic, font.SizeInPoints * WebGpuBackend.DisplayDpi.Y / 72f,
					    out int gdiAsc, out int gdiDesc)
				    && gdiAsc + gdiDesc > 0)
					return (float) Math.Ceiling ((gdiAsc + gdiDesc) / 6f);
			} catch (Exception) {
			}
			return (float) Math.Ceiling (font.Height / 6f);
		}

		/// <summary>Both margins together: the one before the first glyph and the wider one after
		/// the last, which leaves room for an italic's tail. A measurement has to include them,
		/// because they are room the drawing will use.</summary>
		static float Margins (Font font, StringFormat format)
		{
			float left = Overhang (font, format);
			return left == 0f ? 0f : left + (float) Math.Ceiling (font.Height / 6f * 1.5f);
		}

		/// <summary>WF_GDIPLUS_TEXT=0 draws the public DrawString through the GDI pipeline again.</summary>
		static readonly bool s_gdiPlusText = Environment.GetEnvironmentVariable ("WF_GDIPLUS_TEXT") != "0";

		/// <summary>Graphics.DrawString as gdiplus.dll draws it on its fast path (FastTextImager):
		/// its own layout -- natural hinted advances, 1.03 tracking, em/6 margins, an unrounded
		/// baseline -- and its own ClearType (DirectWrite's 6x1 glyph bitmaps through GDI's filter
		/// and GDI+'s TextContrast blend). False where GDI+ would take its FULL imager (Line Services:
		/// tabs, line breaks, wrapping, RTL or vertical formats, tab stops, complex scripts, hot-key
		/// prefixes, italic overhang past the margins) or where the realization is not modelled
		/// (the bi-level ones: SingleBitPerPixel[GridFit], AntiAliasGridFit where the face's gasp
		/// does not grey, ClearType at a size drawn from embedded bitmaps; simulated bold; a
		/// simulated italic under the antialiased hints; underline/strikeout): those keep being
		/// drawn the way they always were, below. The antialiased hints are GDI+'s too: 4x4 glyphs
		/// through its text gamma table.</summary>
		static bool HasControlChar (string s)
		{
			foreach (char c in s)
				if (c < ' ') return true;
			return false;
		}

		// WF_TRACE_GFX="x,y,w,h": every drawing call whose device box meets that device rectangle, with
		// what it draws and the Graphics state, to stderr. A debugging aid for parity work.
		static readonly RectangleF? s_gfxTrace = ParseGfxTrace ();
		static RectangleF? ParseGfxTrace ()
		{
			string v = Environment.GetEnvironmentVariable ("WF_TRACE_GFX");
			if (string.IsNullOrEmpty (v)) return null;
			string[] p = v.Split (',');
			return p.Length == 4 ? new RectangleF (float.Parse (p [0]), float.Parse (p [1]), float.Parse (p [2]), float.Parse (p [3])) : null;
		}
		void GfxTrace (string op, Func<string> what, RectangleF world)
		{
			if (s_gfxTrace is not RectangleF want) return;
			float [] m = rec_world;
			RectangleF dev = new RectangleF (world.X * m [0] + m [4], world.Y * m [3] + m [5], world.Width * m [0], world.Height * m [3]);
			float tx = 0, ty = 0;
			GpuRecorder?.GetTranslation (out tx, out ty);
			if (!dev.IntersectsWith (want) && !new RectangleF (dev.X + tx - m [4], dev.Y + ty - m [5], dev.Width, dev.Height).IntersectsWith (want)) return;
			Console.Error.WriteLine ($"[gfx] {op} {what ()} sm={gpu_smoothing} po={PixelOffsetMode} xf={m [4]},{m [5]} rec=({tx},{ty}) dev={dev}");
			// WF_TRACE_GFX_STACK=1: and who drew it.
			if (Environment.GetEnvironmentVariable ("WF_TRACE_GFX_STACK") == "1") Console.Error.WriteLine (new System.Diagnostics.StackTrace (2, false).ToString ());
		}
		static RectangleF PointsBox (PointF [] p)
		{
			if (p == null || p.Length == 0) return RectangleF.Empty;
			float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
			foreach (PointF q in p) { x0 = Math.Min (x0, q.X); y0 = Math.Min (y0, q.Y); x1 = Math.Max (x1, q.X); y1 = Math.Max (y1, q.Y); }
			return RectangleF.FromLTRB (x0, y0, x1 + 1, y1 + 1);
		}
		static string BrushDesc (Brush b) => b switch {
			SolidBrush sb => "solid " + sb.Color,
			System.Drawing.Drawing2D.LinearGradientBrush lg => $"linear {lg.Rectangle} {lg.LinearColors [0]}->{lg.LinearColors [1]} blend={(lg.InterpolationColors?.Colors?.Length ?? 0)}",
			_ => b?.GetType ().Name };
		static string PathDesc (GraphicsPath p)
		{
			if (p == null) return "-";
			var pts = p.PathPoints; var ty = p.PathTypes;
			var sb = new System.Text.StringBuilder ();
			for (int i = 0; i < pts.Length && i < 40; i++) sb.Append ($"{pts [i].X},{pts [i].Y}:{ty [i]} ");
			return sb.ToString ();
		}

		bool TryDrawGdiPlusText (string s, Font font, Brush brush, RectangleF rect, StringFormat format)
		{
			if (!s_gdiPlusText)
				return false;
			// GpGraphics::DrawString hands FastTextImager the world-to-device transform: a positive
			// axis scale is laid out and realized at the device's size (Initialize@1800393c0, the
			// stretched fits); the renderer draws the run under that scale and no other. What the
			// fast imager refuses under a transform -- a turned or sheared world, the full imager's
			// strings, the style lines -- this port's GDI+ engine lays out and realizes as it does
			// for a bitmap, and the renderer blends its levels (DrawGdiPlusThroughEngine).
			float [] wm = RecordingMatrix ();
			bool identity = wm [0] == 1f && wm [1] == 0f && wm [2] == 0f && wm [3] == 1f;
			bool axis = wm [1] == 0f && wm [2] == 0f && wm [0] > 0f && wm [3] > 0f;
			if (!identity && (!axis || font.Underline || font.Strikeout || (format != null && format.TabStopCount > 0)))
				return DrawGdiPlusThroughEngine (s, font, brush, rect, format);
			if (font.Underline || font.Strikeout)
				return false;
			string family = font.FontFamily?.Name;
			if (string.IsNullOrEmpty (family))
				return false;
			int style = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
			int flags = 0, align = 0, lineAlign = 0;
			bool typographic = false, hotkey = false;
			if (format != null) {
				if (format.TabStopCount > 0)
					return false;
				flags = (int) format.FormatFlags;
				typographic = format.IsTypographic;
				// GenericTypographic's own flags (FitBlackBox | LineLimit | NoClip), which the
				// GDI+-less StringFormat does not carry.
				if (typographic)
					flags |= 0x6004;
				align = (int) format.Alignment;
				lineAlign = (int) format.LineAlignment;
				hotkey = format.HotkeyPrefix != Text.HotkeyPrefix.None;
			}
			float sx = wm [0], sy = wm [3];
			// A string wider than its rectangle under a trimming is the full imager's: it trims it
			// (DotNetBar's dock tabs at 200%, "Explo..."). The fast layout below does not, and drew
			// the whole caption over its neighbours.
			if (format != null && format.Trimming != StringTrimming.None && rect.Width > 0
			    && MeasureString (s, font, PointF.Empty, format).Width > rect.Width + 0.5f)
				return DrawGdiPlusThroughEngine (s, font, brush, rect, format);
			// FastTextImager::Initialize +0xd0: no width to fit for NoWrap without trimming.
			float wrap = format != null && (format.FormatFlags & StringFormatFlags.NoWrap) != 0
				     && format.Trimming == StringTrimming.None ? 0f : rect.Width;
			object layout = WebGpuBackend.TextMetrics.LayoutGdiPlus (s, family, style, font.SizeInPoints,
				rect.X, rect.Y, rect.Width, rect.Height, flags, typographic, align, lineAlign, hotkey,
				(int) recorded_text_hint, recorded_text_contrast, out bool empty, out bool asPath,
				// The renderer draws the bi-level realizations under an axis scale.
				biLevel: axis, sx: sx, sy: sy, wrapWidth: wrap, dpi: EmDpiY);
			if (layout == null && asPath)
				return FillGdiPlusTextOutlines (s, font, brush, rect, format, sx, sy);
			// What the fast imager refuses goes to the full imager in GDI+ (a hot-key prefix, a control
			// character): this port's engine lays such a string out as GDI+ does. Left to the recorder's
			// approximation, DotNetBar's "&File" ribbon tabs sat a pixel high and a pixel narrow.
			if (layout == null)
				return (!identity || hotkey && s.IndexOf ('&') >= 0 && !HasControlChar (s)) && DrawGdiPlusThroughEngine (s, font, brush, rect, format);
			if (!empty)
				GpuRecorder.DrawGdiPlusText (layout, s, ArgbOf (brush), style, family);
			return true;
		}

		/// <summary>A window's DrawString under a transform, drawn the way GpGraphics::DrawString
		/// draws it: this port's GDI+ engine lays the string out and realizes it under the window's
		/// world and page (the fast imager, or FullTextImager::DrawGlyphs @18003b720 with its turned,
		/// stretched and sideways realizations, the path realizations, the style lines) on a scratch
		/// surface whose OutputText hands each run's levels, cut to the clip, to the renderer
		/// instead of blending them (GpGraphics.TextLevelsSink) -- the blend is the window's, against
		/// the paper under it. What the engine fills or strokes instead (a path realization, an
		/// underline) is drawn as the image it made. Modelled outside containers, at a whole-pixel
		/// offset; false otherwise (the caller's old path).</summary>
		bool DrawGdiPlusThroughEngine (string s, Font font, Brush brush, RectangleF rect, StringFormat format)
		{
			if (rec_in_container || font.FontFamily == null || !(brush is SolidBrush))
				return false;
			float [] wm = RecordingMatrix ();
			GpuRecorder.GetTranslation (out float tx, out float ty);
			float ox = tx - wm [4], oy = ty - wm [5];
			if (ox != (int) ox || oy != (int) oy)
				return false;
			float det = wm [0] * wm [3] - wm [1] * wm [2];
			if (!(Math.Abs (det) > 1e-6f))
				return false;
			// The device box the string can reach: the layout rectangle widened by the string's
			// own measure either way (an alignment in an empty rectangle, an overhang) and two ems,
			// through the transform.
			SizeF m;
			using (var mb = new Bitmap (1, 1, PixelFormat.Format32bppArgb))
			using (var mg = FromImage (mb)) {
				mg.PageUnit = rec_unit == GraphicsUnit.Display ? GraphicsUnit.Pixel : rec_unit;
				mg.PageScale = rec_page_scale;
				m = mg.MeasureString (s, font, rect.Width > 0f ? new SizeF (rect.Width, Math.Max (rect.Height, 0f)) : SizeF.Empty, format);
			}
			float em = font.SizeInPoints * DpiY / 72f;
			float pad = 2f * em / Math.Max (PageFactor, 1e-6f);
			float x0 = rect.X - m.Width - pad, y0 = rect.Y - m.Height - pad;
			float x1 = rect.X + Math.Max (rect.Width, 0f) + m.Width + pad, y1 = rect.Y + Math.Max (rect.Height, 0f) + m.Height + pad;
			float dx0 = float.MaxValue, dy0 = float.MaxValue, dx1 = float.MinValue, dy1 = float.MinValue;
			foreach ((float px, float py) in new [] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) }) {
				float X = wm [0] * px + wm [2] * py + wm [4], Y = wm [1] * px + wm [3] * py + wm [5];
				dx0 = Math.Min (dx0, X); dy0 = Math.Min (dy0, Y); dx1 = Math.Max (dx1, X); dy1 = Math.Max (dy1, Y);
			}
			if (!(dx1 > dx0) || !(dy1 > dy0) || dx1 - dx0 > 8192f || dy1 - dy0 > 8192f)
				return false;
			int bx = (int) Math.Floor (dx0), by = (int) Math.Floor (dy0);
			int bw = (int) Math.Ceiling (dx1) - bx + 1, bh = (int) Math.Ceiling (dy1) - by + 1;
			var runs = new System.Collections.Generic.List<(Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Levels Lv, int Mode, int Contrast)> ();
			using (var bmp = new Bitmap (bw, bh, PixelFormat.Format32bppArgb)) {
				using (var g = FromImage (bmp)) {
					if (g.gp == null || g.image_target == null)
						return false;
					// The window's page and world, then the scratch surface's offset (in page units).
					g.PageUnit = rec_unit == GraphicsUnit.Display ? GraphicsUnit.Pixel : rec_unit;
					g.PageScale = rec_page_scale;
					float pf = PageFactor;
					g.SetRecWorld (RecWorld);
					g.RecordedCombine (new [] { 1f, 0f, 0f, 1f, -bx / pf, -by / pf }, Drawing2D.MatrixOrder.Append);
					g.recorded_text_hint = recorded_text_hint;
					g.recorded_text_contrast = recorded_text_contrast;
					g.gpu_smoothing = gpu_smoothing;
					g.gp.TextLevelsSink = (lv, mode, contrast) => runs.Add ((lv, mode, contrast));
					try {
						if (!g.EngineDrawString (s, font, brush, rect, format))
							return false;
					} finally {
						g.gp.TextLevelsSink = null;
					}
				}
				string family = font.FontFamily.Name;
				int style = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
				int argb = ArgbOf (brush);
				// Device pixels, relative to the recording's origin: drawn with no world at all.
				GpuRecorder.SetWorldTransform (1f, 0f, 0f, 1f, 0f, 0f);
				try {
					foreach ((var lv, int mode, int contrast) in runs) {
						var at = new Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Levels {
							Grey = lv.Grey, Left = lv.Left + bx, Top = lv.Top + by, Width = lv.Width, Height = lv.Height, Index = lv.Index };
						var run = new Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Run {
							Pre = at, PreKey = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.LevelsKey (at),
							Mode = mode, Hint = (int) recorded_text_hint, Contrast = contrast,
							Em = em * (float) Math.Sqrt (Math.Abs (det)), OriginX = at.Left, OriginY = at.Top + at.Height,
						};
						GpuRecorder.DrawGdiPlusText (run, s, argb, style, family);
					}
					RecordEngineImage (bmp, bx, by);
				} finally {
					PushRecordedTransform ();
				}
			}
			return true;
		}

		/// <summary>What a scratch engine surface drew besides text (a path realization's fill, a
		/// style line), as an image at its device place under the current transform.</summary>
		void RecordEngineImage (Bitmap bmp, int x, int y)
		{
			int bw = bmp.Width, bh = bmp.Height;
			BitmapData d = bmp.LockBits (new Rectangle (0, 0, bw, bh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
			var bgra = new byte [bw * bh * 4];
			for (int j = 0; j < bh; j++)
				Marshal.Copy (d.Scan0 + j * d.Stride, bgra, j * bw * 4, bw * 4);
			bmp.UnlockBits (d);
			for (int i = 0; i < bgra.Length; i += 4)
				(bgra [i], bgra [i + 2]) = (bgra [i + 2], bgra [i]);
			RecordCoverage (bgra, bw, bh, x, y);
		}

		/// <summary>An RGBA coverage image at a device place, as bands of the rows that hold ink, each
		/// cropped to its ink: transparent pixels write nothing, and a box that claims them hides
		/// the paper from text drawn beside the ink (WgpuSceneRenderer.PaperUnder knows no image).</summary>
		void RecordCoverage (byte [] rgba, int w, int h, int x, int y)
		{
			int bandTop = -1, b0 = 0, b1 = 0;
			void Emit (int top, int bottom, int c0, int c1)
			{
				int cw = c1 - c0 + 1, ch = bottom - top + 1;
				var part = new byte [cw * ch * 4];
				for (int r = 0; r < ch; r++)
					Buffer.BlockCopy (rgba, ((top + r) * w + c0) * 4, part, r * cw * 4, cw * 4);
				GpuRecorder.DrawImage (part, cw, ch, x + c0, y + top, cw, ch);
			}
			for (int r = 0; r <= h; r++) {
				int c0 = -1, c1 = -1;
				if (r < h)
					for (int c = 0; c < w; c++)
						if (rgba [(r * w + c) * 4 + 3] != 0) { if (c0 < 0) c0 = c; c1 = c; }
				// A row with no ink, or ink that strays far from the band's, ends the band.
				bool ends = bandTop >= 0 && (c0 < 0 || c0 < b0 - 8 || c0 > b0 + 8 || c1 < b1 - 8 || c1 > b1 + 8);
				if (ends) { Emit (bandTop, r - 1, b0, b1); bandTop = -1; }
				if (c0 < 0) continue;
				if (bandTop < 0) { bandTop = r; b0 = c0; b1 = c1; }
				else { b0 = Math.Min (b0, c0); b1 = Math.Max (b1, c1); }
			}
		}

		/// <summary>A string whose realization GDI+ switches to outlines (GpFaceRealization::Realize's
		/// SwitchToPath @1801ed568: a device box over 100 pixels tall under ClearType, over 200 under
		/// the antialiased hints, over 800 otherwise): FastTextImager answers status 6 and
		/// FullTextImager::DrawGlyphs @18003b720 adds each glyph's outline at the design-realization
		/// layout and fills the lot with GpGraphics::FillPath -- the layout GraphicsPath.AddString
		/// makes, filled with the graphics' own smoothing, through the world's axis scale
		/// (<paramref name="sx"/>, <paramref name="sy"/>). Modelled for a pixel page; false
		/// otherwise (the caller's old path).</summary>
		bool FillGdiPlusTextOutlines (string s, Font font, Brush brush, RectangleF rect, StringFormat format,
					      float sx = 1f, float sy = 1f)
		{
			if (PageUnit != GraphicsUnit.Pixel && PageUnit != GraphicsUnit.Display)
				return false;
			if (PageScale != 1f || font.FontFamily == null)
				return false;
			float em = font.SizeInPoints * DpiY / 72f;
			using (var path = new GraphicsPath ()) {
				path.AddString (s, font.FontFamily, (int) font.Style & 3, em, rect, format);
				if (path.PointCount == 0)
					return true;
				// GpGraphics::FillPath through GDI+'s own rasterizer: the path filled into a
				// bitmap Graphics (this port's GDI+ engine) at its device position, the coverage
				// drawn as an image. SetTextLinesAntialiasMode::SetAAMode @1800eb708 for a path
				// realization: antialiased for AntiAlias and AntiAliasGridFit, aliased otherwise.
				GpuRecorder.GetTranslation (out float tx, out float ty);
				RectangleF wb = path.GetBounds ();
				// The device box relative to the world's translation: the world box through the scale.
				var bounds = RectangleF.FromLTRB (wb.Left * sx, wb.Top * sy, wb.Right * sx, wb.Bottom * sy);
				if (tx != (int) tx || ty != (int) ty || bounds.Width <= 0 || bounds.Height <= 0
				    || bounds.Width > 8192 || bounds.Height > 8192)
					return false;
				int bx = (int) Math.Floor (bounds.X) - 1, by = (int) Math.Floor (bounds.Y) - 1;
				int bw = (int) Math.Ceiling (bounds.Right) + 1 - bx, bh = (int) Math.Ceiling (bounds.Bottom) + 1 - by;
				TextRenderingHint h = recorded_text_hint;
				using (var bmp = new Bitmap (bw, bh, PixelFormat.Format32bppArgb)) {
					using (var g = FromImage (bmp)) {
						g.SmoothingMode = h == TextRenderingHint.AntiAlias || h == TextRenderingHint.AntiAliasGridFit
							? SmoothingMode.AntiAlias : SmoothingMode.None;
						g.TranslateTransform (-bx, -by);
						g.ScaleTransform (sx, sy);
						// FullTextImager::Draw @1800f0360: the layout rectangle clips unless NoClip.
						if (rect.Width != 0f && rect.Height != 0f
						    && (format == null || (format.FormatFlags & StringFormatFlags.NoClip) == 0))
							g.SetClip (rect);
						g.FillPath (brush, path);
					}
					BitmapData d = bmp.LockBits (new Rectangle (0, 0, bw, bh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
					var bgra = new byte [bw * bh * 4];
					for (int j = 0; j < bh; j++)
						Marshal.Copy (d.Scan0 + j * d.Stride, bgra, j * bw * 4, bw * 4);
					bmp.UnlockBits (d);
					for (int i = 0; i < bgra.Length; i += 4)
						(bgra [i], bgra [i + 2]) = (bgra [i + 2], bgra [i]);
					// (The bands of it that hold ink: RecordCoverage.)
					// The coverage is the device's: drawn under the world's translation alone.
					bool scaled = sx != 1f || sy != 1f;
					if (scaled) {
						float [] wm = RecordingMatrix ();
						GpuRecorder.SetWorldTransform (1f, 0f, 0f, 1f, wm [4], wm [5]);
					}
					try {
						RecordCoverage (bgra, bw, bh, bx, by);
					} finally {
						if (scaled)
							PushRecordedTransform ();
					}
				}
			}
			return true;
		}

		public void DrawString (string s, Font font, Brush brush, RectangleF layoutRectangle, StringFormat format)
		{
			// System.Drawing: the brush first, then nothing to draw, then the font.
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (string.IsNullOrEmpty (s))
				return;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font, brush);
			CheckTarget ();
			// A disposed format hands GDI+ none: the default format.
			if (format != null && format.IsDisposed) format = null;
			if (print_mode && font != null && brush != null && PrintDrawString (s, font, brush, layoutRectangle, format)) return;
			if (font == null)
				throw new ArgumentNullException ("font");
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (s == null || s.Length == 0)
				return;
			if (mf_rec != null) { mf_rec.DrawString (s, font, layoutRectangle, format, brush); return; }
			// A bitmap's Graphics: GDI+'s own pipeline, into the pixels (Graphics.Engine.cs).
			if (!gdi_text_metrics && !gdi_ascent && !memory_surface_text
			    && EngineDrawString (s, font, brush, layoutRectangle, format))
				return;
			if (GpuRecorder != null && brush is SolidBrush) {
				// The public DrawString is GDI+'s, and GDI+ text is its own pipeline: where its fast
				// imager would draw the string, draw it that way (see TryDrawGdiPlusText). The port's
				// own controls stand in for GDI text and keep the path below.
				if (!gdi_text_metrics && !gdi_ascent && !memory_surface_text
				    && TryDrawGdiPlusText (s, font, brush, layoutRectangle, format))
					return;
				// GDI+ DrawString positions by the layout rect's top-left; em size in pixels ~ points*96/72.
				// Honour the StringFormat alignment (buttons/labels centre text in a rect) by measuring
				// the run and offsetting within the layout rectangle. MeasureString uses libgdiplus for
				// MEASUREMENT only — the glyphs are still rasterized by WGSL at present time.
				float emPx = font.SizeInPoints * EmDpiY / 72f;
				// The style the caller asked for, in the terms the renderer states them
				// (WPF's StyleSimulations: 1 = bold, 2 = italic). Without carrying this the run
				// arrives with nothing but a size and a colour, and every piece of bold text in
				// every hosted control -- a property grid's modified values, a group heading --
				// came out regular.
				int sims = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0)
					   | (no_kerning ? WebGpuBackend.TextMetrics.NoKerning : 0)
					   | (gpos_kerning && !no_kerning ? WebGpuBackend.TextMetrics.GposKerning : 0)
					   | (BiLevelText ? WebGpuBackend.TextMetrics.BiLevel : 0)
					   | (memory_surface_text ? WebGpuBackend.TextMetrics.MemorySurface : 0);
				// The family the caller asked for. A run used to arrive at the renderer with
				// nothing but a size, a colour and a style, so everything came out in one
				// hard-coded face -- and a fixed-width font could not be had at all.
				string family = font.FontFamily?.Name;
				int argb = ArgbOf (brush);

				// Multi-line strings arrive here whole -- a message box's text, a multi-line Label.
				// The recorder draws ONE run, so every newline became a glyph and the whole message
				// one very long line: an exception message came out about 15000px wide, past the
				// GPU's maximum texture dimension, and the window presented nothing at all. Draw a
				// run per line, and align each line within the layout rectangle on its own.
				// GDI+ confines a string to its layout rectangle; the recorder draws a run wherever it
				// is told. WinForms leans on that: a ListView hands each subitem its column bounds as
				// the layout rect and expects the text to stop there. Unclipped, a value wider than
				// its column painted straight over the next ones -- one long assembly name covered
				// every other column of the version list.
				// "&Copy" means a C with an underline and Alt+C to press it -- StringFormat.HotkeyPrefix
				// is how WinForms asks for that, and the recorder path ignored it, so buttons and
				// menu items showed their ampersands raw.
				string text = s;
				int mnemonic = -1;
				Text.HotkeyPrefix prefix = format == null ? Text.HotkeyPrefix.None : format.HotkeyPrefix;
				if (prefix != Text.HotkeyPrefix.None)
					text = StripHotkeyPrefix (s, out mnemonic);

				// NoClip means the caller accepts overhang; otherwise GDI+ confines the string to its
				// layout rectangle, and WinForms leans on that: a ListView hands each subitem its
				// column bounds and expects the text to stop there.
				bool clipToLayout = layoutRectangle.Width > 0 && layoutRectangle.Height > 0
					&& (format == null || (format.FormatFlags & StringFormatFlags.NoClip) == 0);
				if (clipToLayout)
					GpuRecorder.SetClipRect (layoutRectangle.X, layoutRectangle.Y,
						layoutRectangle.Width, layoutRectangle.Height, false);
				// The margin left before the first glyph, so that one which overhangs its cell is not
				// clipped by the rectangle it was asked to fit in. GDI+ leaves it and Windows leaves it
				// -- a sixth of the font's height -- and this path left none, so every caption in the
				// application sat three pixels to the left of the same caption in Windows. Centred text
				// is left alone: the margins either side of it very nearly cancel.
				float overhang = Overhang (font, format);
				string[] lines = WrapLines (text, emPx, sims, family, layoutRectangle.Width, format);
				// A line of text is taller than its em square: the font's line height adds the
				// descender and its leading. Stepping by the em size instead packed every
				// multi-line run tighter than the same text drawn by Windows.
				float lineHeight = font.GetHeight ();
				if (lineHeight <= 0) lineHeight = emPx;
				// Where the glyphs sit INSIDE that line box: on a baseline as far down as the font's own
				// ascent, truncated to whole pixels exactly as Windows truncates it. Guessing at four
				// fifths of the line box came out a fraction low, and a fraction low rounds to a whole
				// pixel: every caption in the application sat one pixel below the same caption in
				// Windows. The recorder turns the y it is given into a baseline by dropping 0.8 of the
				// em size, so what it wants is the ascent less that.
				// GDI+ itself puts the baseline at the font's CELL ascent scaled to the em, snapped to
				// the nearest pixel (Segoe UI at 12 px: 12.95, so 13) -- a row below GDI's tmAscent
				// (12), which is what TextRenderer and the port's own controls stand in for. Measured
				// on stock .NET: an owner-drawn list's DrawString text (the colour and cursor editors)
				// sat a row higher in ours.
				float baseline = (gdi_text_metrics || gdi_ascent ? Ascent (font, emPx) : GdiPlusAscent (font, emPx)) - 0.8f * emPx;
				float ty = layoutRectangle.Y;
				if (format != null && layoutRectangle.Height > 0) {
					// What is being aligned depends on which drawing this is standing in for. GDI+ centres its
					// LINE SPACING -- the leading included, rounded up -- and that is what a caller of DrawString
					// gets. GDI centres the box the GLYPHS occupy, the ascent and the descent each truncated,
					// which is a pixel shorter; every caption drawn through TextRenderer (a menu title, a tool
					// bar button, a grid cell) came out a pixel above Windows' until this told the two apart.
					// Extra lines step by the spacing either way.
					float firstLine = gdi_text_metrics ? Ascent (font, emPx) + Descent (font, emPx) : lineHeight;
					float totalH = firstLine + lineHeight * (lines.Length - 1);
					// AND THE OFFSET IS TRUNCATED, exactly as the horizontal one below is and for the
					// same reason: DT_VCENTER is integer arithmetic on whole pixels, where this
					// carried the fraction and let the renderer round it. Half the rectangles in an
					// application leave an odd number of pixels over, and every one of those put its
					// caption a pixel below Windows'. It was hidden for a long time by an
					// unconditional "lift everything by a row" in TextRenderer, which cancelled it
					// for the odd cases and broke the even ones (the status strip's caption sat a row
					// high). GDI+ does centre in float, so only the GDI-metric path truncates.
					if (format.LineAlignment == StringAlignment.Center) {
						float voff = (layoutRectangle.Height - totalH) / 2f;
						ty += gdi_text_metrics ? MathF.Floor (voff) : voff;
					}
					else if (format.LineAlignment == StringAlignment.Far) ty += layoutRectangle.Height - totalH;
				}
				for (int i = 0; i < lines.Length; i++) {
					string line = lines[i].TrimEnd ('\r');
					if (line.Length == 0) continue;
					float tx = layoutRectangle.X;
					if (format != null && layoutRectangle.Width > 0 && format.Alignment != StringAlignment.Near) {
						// Managed measurement (no libgdiplus) with the renderer's font -> exact centring.
						WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out float mw, out float mh);
						// GDI TRUNCATES the centring offset -- DT_CENTER is integer arithmetic on whole
						// pixels -- where this carried the fraction and let the renderer round it. Half
						// the strings in an application round up, and every one of those sat a pixel to
						// the right of the same caption in Windows: a button's "Button" started at 15
						// where stock starts it at 14. GDI+ does centre in float, so only the GDI-metric
						// path (TextRenderer, which is what every control caption goes through) rounds
						// the same way GDI does.
						if (format.Alignment == StringAlignment.Center) {
							float off = (layoutRectangle.Width - mw) / 2f;
							tx += gdi_text_metrics ? MathF.Floor (off) : off;
						}
						else tx += layoutRectangle.Width - mw - overhang;
					} else {
						tx += overhang;
					}
					// Underline the mnemonic, if it falls on this line.
					if (prefix == Text.HotkeyPrefix.Show && mnemonic >= 0)
					{
						int lineStart = 0;
						for (int j = 0; j < i; j++) lineStart += lines[j].Length + 1;
						int col = mnemonic - lineStart;
						if (col >= 0 && col < line.Length)
						{
							float ux = 0f, uw, unused2;
							if (col > 0)
								WebGpuBackend.GpuRaster.MeasureText (line.Substring (0, col), emPx, sims, family, out ux, out unused2);
							WebGpuBackend.GpuRaster.MeasureText (line.Substring (col, 1), emPx, sims, family, out uw, out unused2);
							float uy = ty + i * lineHeight + baseline + emPx;
							// uw wide, so it ENDS at one less -- DrawLine's end point is inclusive.
							// The same off-by-one as the underline below, found there against
							// Windows' LinkLabel; this one only shows when the mnemonics are
							// displayed, which is why the window never caught it.
							GpuRecorder.DrawLine (tx + ux, uy, tx + ux + uw - 1f, uy, argb);
						}
					}

					if (s_traceText)
						Console.Error.WriteLine ($"drawtext '{line}' at ({tx},{ty + i * emPx}) em={emPx} rect={layoutRectangle} align={(format == null ? "-" : format.Alignment.ToString ())}");
					GpuRecorder.DrawText (line, tx, ty + i * lineHeight + baseline, emPx, argb, sims, family);

					// Underline and strike-out are part of the run for GDI+, but this stack draws a run
					// as glyphs and nothing else -- the style never travelled with it, so a LinkLabel
					// came out as plain text with no rule under it. Draw the rules ourselves, on the
					// font's own scale so they hold at any size.
					if (font.Underline || font.Strikeout) {
						WebGpuBackend.GpuRaster.MeasureText (line, emPx, sims, family, out float rw, out float _);
						if (rw > 0f) {
							float top = ty + i * lineHeight + baseline;
							// ON a pixel row, and on its CENTRE. A rule left at whatever fraction the
							// arithmetic lands on is spread over two rows and fills neither, which came
							// out as a pale line a row below the solid one Windows draws.
							// The rule is rw pixels wide, so it ENDS at tx + rw - 1: DrawLine takes
							// an inclusive end point -- it fills |x2-x1|+1 columns, which is GDI+'s
							// own semantics -- and passing tx + rw drew one column too many. The
							// LinkLabel's underline ran 15..65 where Windows draws 15..64, which is
							// a whole extra pixel of saturated colour and one of the twenty worst
							// pixels in the entire control window.
							if (font.Underline) {
								float uy = MathF.Floor (top + emPx) - 1f;
								GpuRecorder.DrawLine (tx, uy, tx + rw - 1f, uy, argb);
							}
							if (font.Strikeout) {
								float sy = MathF.Floor (top + emPx * 0.55f);
								GpuRecorder.DrawLine (tx, sy, tx + rw - 1f, sy, argb);
							}
						}
					}
				}

				if (clipToLayout) GpuRecorder.ClearClip ();
				return;
			}

			/*GDIP*/;
			
		}

		public void EndContainer (GraphicsContainer container)
		{
						if (container == null)
				throw new ArgumentNullException ("container");
			CheckUse ();
			// GpGraphics::EndContainer: Restore with the container's id.
			mf_rec?.EndContainer (container.NativeObject);
			RestoreState ((int) container.NativeObject);
			
		}

		public void ExcludeClip (Rectangle rect)
		{
			CheckUse ();
						EngineClipRect (rect, CombineMode.Exclude);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, true); return; }
			
		}

		public void ExcludeClip (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			CheckUse (region);
						EngineClipRegion (region, CombineMode.Exclude);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) { RecordRegionClip (region, true); return; }
			
		}

		
		public void FillClosedCurve (Brush brush, PointF [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillClosedCurve (brush, points, 0.5f, FillMode.Alternate)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath (FillMode.Alternate)) { gp.AddClosedCurve (points); FillPath (brush, gp); } return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			
		}
		
		public void FillClosedCurve (Brush brush, Point [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillClosedCurve (brush, ToF (points), 0.5f, FillMode.Alternate)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath (FillMode.Alternate)) { gp.AddClosedCurve (points); FillPath (brush, gp); } return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			
		}

		
		public void FillClosedCurve (Brush brush, PointF [] points, FillMode fillmode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			FillClosedCurve (brush, points, fillmode, 0.5f);
		}
		
		public void FillClosedCurve (Brush brush, Point [] points, FillMode fillmode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			FillClosedCurve (brush, points, fillmode, 0.5f);
		}

		public void FillClosedCurve (Brush brush, PointF [] points, FillMode fillmode, float tension)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillClosedCurve (brush, points, tension, fillmode)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath (fillmode)) { gp.AddClosedCurve (points, tension); FillPath (brush, gp); } return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			
		}

		public void FillClosedCurve (Brush brush, Point [] points, FillMode fillmode, float tension)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillClosedCurve (brush, ToF (points), tension, fillmode)) return;
			if (GpuRecorder != null) { using (var gp = new GraphicsPath (fillmode)) { gp.AddClosedCurve (points, tension); FillPath (brush, gp); } return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			
		}

		public void FillEllipse (Brush brush, Rectangle rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			FillEllipse (brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void FillEllipse (Brush brush, RectangleF rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			FillEllipse (brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		public void FillEllipse (Brush brush, float x, float y, float width, float height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillEllipse (brush, x, y, width, height)) return;
			if (PrintFill (brush, gp => gp.AddEllipse (x, y, width, height))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			// GDI+ antialiases at PixelOffsetMode.None with each pixel's CENTRE on the integer
			// coordinate; the recorder's pixel i spans [i, i + 1], so the outline moves half a pixel.
			if (RecordSolid (brush) && !GpuAliased) {
				if (TryFillGdipAntialiased (brush, WebGpuBackend.GdipAntialias.Ellipse (x, y, width, height), 1, 0, 0, 1, 0, 0))
					return;
				GpuRecorder.FillEllipse (x + 0.5f, y + 0.5f, width, height, ArgbOf (brush));
				return;
			}
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { FillTile (GradientShape.Ellipse, x, y, width, height, null, eh); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			
		}

		public void FillEllipse (Brush brush, int x, int y, int width, int height)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillEllipse (brush, x, y, width, height)) return;
			if (PrintFill (brush, gp => gp.AddEllipse (x, y, width, height))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { FillTile (GradientShape.Ellipse, x, y, width, height, null, eh); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			
		}

		public void FillPath (Brush brush, GraphicsPath path)
		{
			GfxTrace ("FillPath", () => $"brush={BrushDesc (brush)} pts={PathDesc (path)}", path?.GetBounds () ?? RectangleF.Empty);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (path == null)
				throw new ArgumentNullException ("path");
			CheckUse (brush, path);
			CheckTarget ();
			if (mf_rec != null) { mf_rec.FillPath (brush, path); return; }
			if (EngineFillPath (brush, path.gp)) return;
			if (PrintFill (brush, path)) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (path == null)
				throw new ArgumentNullException ("path");
			if (EngineFillPath (brush, path.gp)) return;
			if (GpuRecorder != null) {
				// Every subpath together, under the path's own fill mode: a figure inside another is a
				// hole under Alternate, and a concave figure is not a fan.
				var contours = new List<float []> ();
				foreach (PointF [] sub in FlattenSubpaths (path))
					if (sub.Length >= 3)
						contours.Add (ToXY (sub));
				if (contours.Count == 0)
					return;
				bool nonZero = path.FillMode == FillMode.Winding;
				if (TryHatch (brush, out HatchTile ht)) {
					foreach (float [] xy in contours)
						FillTile (GradientShape.Polygon, 0, 0, 0, 0, xy, ht);
					return;
				}
				if (TryGradient (brush, out GradientDesc gd)) {
					GpuRecorder.FillContoursGradient (contours.ToArray (), nonZero, gd);
					return;
				}
				// GDI+'s default smoothing is aliased: a path filled without antialiasing covers whole pixels
				// (DotNetBar outlines its forms with a widened path filled so; antialiased, the outline came out
				// as a half-strength line straddling two pixels).
				if (!GpuAliased && TryFillGdipAntialiasedPath (brush, path))
					return;
				if (GpuAliased && brush is SolidBrush) {
					FillContoursAliased (contours, nonZero, ArgbOf (brush));
					return;
				}
				GpuRecorder.FillContours (contours.ToArray (), nonZero, ArgbOf (brush));
				return;
			}
			
		}

		public void FillPie (Brush brush, Rectangle rect, float startAngle, float sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (rect.Width, rect.Height);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (EngineFillPie (brush, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle)) return;
			if (PrintFill (brush, gp => gp.AddPie (rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			// Approximated as a full ellipse, as the other two overloads already were. Without
			// this the call reached gdiplus with no surface behind it and drew nothing at all:
			// the month calendar fills the selected day with a pie and then writes the day
			// number over it in the background colour, so today's date came out invisible.
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (rect.X, rect.Y, rect.Width, rect.Height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { FillTile (GradientShape.Ellipse, rect.X, rect.Y, rect.Width, rect.Height, null, ph); return; }
			if (TryGradient (brush, out GradientDesc pg)) { GpuRecorder.FillGradient (GradientShape.Ellipse, rect.X, rect.Y, rect.Width, rect.Height, null, pg); return; }
			
		}

		public void FillPie (Brush brush, int x, int y, int width, int height, int startAngle, int sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (EngineFillPie (brush, x, y, width, height, startAngle, sweepAngle)) return;
			if (PrintFill (brush, gp => gp.AddPie (x, y, width, height, startAngle, sweepAngle))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			// Approximate as a full ellipse fill (covers the common full-circle case, e.g. a radio dot).
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { FillTile (GradientShape.Ellipse, x, y, width, height, null, eh); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			
		}

		public void FillPie (Brush brush, float x, float y, float width, float height, float startAngle, float sweepAngle)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			// GpGraphics::DrawArc / DrawPie / FillPie: an empty ellipse is InvalidParameter.
			CheckArc (width, height);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (EngineFillPie (brush, x, y, width, height, startAngle, sweepAngle)) return;
			if (PrintFill (brush, gp => gp.AddPie (x, y, width, height, startAngle, sweepAngle))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillEllipse (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile eh)) { FillTile (GradientShape.Ellipse, x, y, width, height, null, eh); return; }
			if (TryGradient (brush, out GradientDesc ge)) { GpuRecorder.FillGradient (GradientShape.Ellipse, x, y, width, height, null, ge); return; }
			
		}

		public void FillPolygon (Brush brush, PointF [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (EngineFillPolygon (brush, points, FillMode.Alternate)) return;
			if (points != null && PrintFill (brush, gp => gp.AddPolygon (points))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { RecordFillPolygon (Flatten (points), false, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { FillTile (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			
		}

		public void FillPolygon (Brush brush, Point [] points)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (EngineFillPolygon (brush, ToF (points), FillMode.Alternate)) return;
			if (points != null && PrintFill (brush, gp => gp.AddPolygon (points))) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { RecordFillPolygon (Flatten (points), false, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { FillTile (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			
		}

		public void FillPolygon (Brush brush, Point [] points, FillMode fillMode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (EngineFillPolygon (brush, ToF (points), fillMode)) return;
			if (points != null && PrintFill (brush, gp => gp.AddPolygon (points), fillMode)) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { RecordFillPolygon (Flatten (points), fillMode == FillMode.Winding, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { FillTile (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			
		}

		public void FillPolygon (Brush brush, PointF [] points, FillMode fillMode)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (points != null && points.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (EngineFillPolygon (brush, points, fillMode)) return;
			if (points != null && PrintFill (brush, gp => gp.AddPolygon (points), fillMode)) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (points == null)
				throw new ArgumentNullException ("points");
			if (RecordSolid (brush)) { RecordFillPolygon (Flatten (points), fillMode == FillMode.Winding, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile ph)) { FillTile (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), ph); return; }
			if (TryGradient (brush, out GradientDesc gp)) { GpuRecorder.FillGradient (GradientShape.Polygon, 0, 0, 0, 0, Flatten (points), gp); return; }
			
		}

		public void FillRectangle (Brush brush, RectangleF rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
                        FillRectangle (brush, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void FillRectangle (Brush brush, Rectangle rect)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			if (rect == null)
				throw new ArgumentNullException ("rect");
				
                        FillRectangle (brush, rect.Left, rect.Top, rect.Width, rect.Height);
		}

		public void FillRectangle (Brush brush, int x, int y, int width, int height)
		{
			GfxTrace ("FillRectangle", () => $"{x},{y} {width}x{height} brush={BrushDesc (brush)}", new RectangleF (x, y, width, height));
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillRects (brush, new [] { new RectangleF (x, y, width, height) })) return;
			if (PrintFillRect (brush, x, y, width, height)) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillRect (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile rh)) { FillTile (GradientShape.Rect, x, y, width, height, null, rh); return; }
			if (TryExactLinearGradient (brush, x, y, width, height)) return;
			if (TryGradient (brush, out GradientDesc gd)) { GpuRecorder.FillGradient (GradientShape.Rect, x, y, width, height, null, gd); return; }

			
		}

		public void FillRectangle (Brush brush, float x, float y, float width, float height)
		{
			GfxTrace ("FillRectangleF", () => $"{x},{y} {width}x{height} brush={BrushDesc (brush)}", new RectangleF (x, y, width, height));
			if (brush == null)
				throw new ArgumentNullException ("brush");
			CheckUse (brush);
			CheckTarget ();
			if (EngineFillRects (brush, new [] { new RectangleF (x, y, width, height) })) return;
			if (PrintFillRect (brush, x, y, width, height)) return;
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (RecordSolid (brush)) { GpuRecorder.FillRect (x, y, width, height, ArgbOf (brush)); return; }
			if (TryHatch (brush, out HatchTile rh)) { FillTile (GradientShape.Rect, x, y, width, height, null, rh); return; }
			if (TryExactLinearGradient (brush, x, y, width, height)) return;
			if (TryGradient (brush, out GradientDesc gd)) { GpuRecorder.FillGradient (GradientShape.Rect, x, y, width, height, null, gd); return; }

			
		}

		public void FillRectangles (Brush brush, Rectangle [] rects)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (rects != null && rects.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			var rf = new RectangleF [rects.Length];
			for (int i = 0; i < rf.Length; i++) rf [i] = rects [i];
			if (EngineFillRects (brush, rf)) return;
			if (PrintFillRects (brush, rf)) return;
			if (GpuRecorder != null) { foreach (Rectangle r in rects) FillRectangle (brush, r); return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");

			
		}

		public void FillRectangles (Brush brush, RectangleF [] rects)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			CheckUse (brush);
			CheckTarget ();
			// GDI+ refuses fewer than 1.
			if (rects != null && rects.Length < 1)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");
			if (EngineFillRects (brush, rects)) return;
			if (PrintFillRects (brush, rects)) return;
			if (GpuRecorder != null) { foreach (RectangleF r in rects) FillRectangle (brush, r); return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (rects == null)
				throw new ArgumentNullException ("rects");

			
		}

		
		public void FillRegion (Brush brush, Region region)
		{
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (region == null)
				throw new ArgumentNullException ("region");
			CheckUse (brush, region);
			CheckTarget ();
			if (EngineFillRegion (brush, region)) return;
			if (PrintFillRegion (brush, region)) return;
			if (GpuRecorder != null) { foreach (RectangleF r in region.GetRegionScans (new Matrix ())) FillRectangle (brush, r.X, r.Y, r.Width, r.Height); return; }
			if (brush == null)
				throw new ArgumentNullException ("brush");
			if (region == null)
				throw new ArgumentNullException ("region");
			
                        
		}

		
		public void Flush ()
		{
			CheckUse ();
			Flush (FlushIntention.Flush);
		}

		
		public void Flush (FlushIntention intention)
		{
			CheckUse ();
			mf_rec?.Flush (intention);
			if (image_target != null) {
				image_target.TakeDrawing (this, detach: false);
				if (hdc_surface != null) PresentHdc ();
			}
		}

		// ---- device contexts ---------------------------------------------------------------------

		// FromHdc / FromHwnd (Windows): the DC's area as a bitmap the engine draws on.
		WebGpuBackend.Gdip.GpHdcSurface hdc_surface;
		bool hdc_captured;
		IntPtr hdc_window = IntPtr.Zero, hdc_owned = IntPtr.Zero;
		// GetHdc: a memory DC over the bitmap (Windows), or a handle FromHdc maps back to this.
		WebGpuBackend.Gdip.GpHdcSurface gethdc_surface;
		static readonly Dictionary<IntPtr, Graphics> s_pseudoHdc = new Dictionary<IntPtr, Graphics> ();
		static long s_pseudoNext = 0x7EF00000;

		void EnsureHdcCaptured ()
		{
			if (hdc_surface == null || hdc_captured) return;
			hdc_captured = true;
			if (OperatingSystem.IsWindows ())
				hdc_surface.Capture (image_target.managed.Frame);
		}

		void PresentHdc ()
		{
			if (!OperatingSystem.IsWindows () || image_target == null) return;
			bool recorded = GpuRecorder is WebGpuBackend.SceneRecorder r && r.HasContent;
			if (!hdc_captured && !recorded) return;
			EnsureHdcCaptured ();
			image_target.FlushDrawing ();
			hdc_surface.Present (image_target.managed.Frame);
		}

		// Every live Graphics drawing into a DC, by DC. GDI+ draws into a DC as it goes, so GDI
		// reading or writing that DC sees each drawing the moment it is made; this engine draws a copy
		// and writes it back on Flush and Dispose. DotNetBar draws a combo box with GDI+ into a memory
		// DC and BitBlts that DC to the window while the Graphics is still open: what it blitted was the
		// untouched DIB, a black box. Whoever stands between an app and gdi32 (wpfmac does) calls
		// System.Drawing.FlushHdc with each DC a GDI call is about to use.
		static readonly Dictionary<IntPtr, List<Graphics>> s_overDc = new Dictionary<IntPtr, List<Graphics>> ();
		static volatile int s_overDcCount;

		static void RememberOverDc (IntPtr hdc, Graphics g)
		{
			lock (s_overDc) {
				if (!s_overDc.TryGetValue (hdc, out var list))
					s_overDc [hdc] = list = new List<Graphics> ();
				list.Add (g);
				s_overDcCount++;
			}
			if (AppContext.GetData ("System.Drawing.FlushHdc") == null)
				AppContext.SetData ("System.Drawing.FlushHdc", (Action<IntPtr>)FlushHdc);
		}

		static void ForgetOverDc (IntPtr hdc, Graphics g)
		{
			lock (s_overDc) {
				if (s_overDc.TryGetValue (hdc, out var list) && list.Remove (g)) {
					s_overDcCount--;
					if (list.Count == 0) s_overDc.Remove (hdc);
				}
			}
		}

		/// <summary>Before GDI uses <paramref name="hdc"/>: what each Graphics over it has drawn is
		/// written to it, and each starts again from what the DC holds afterwards (GDI may draw on
		/// it next).</summary>
		static void FlushHdc (IntPtr hdc)
		{
			if (s_overDcCount == 0 || hdc == IntPtr.Zero) return;
			Graphics[] over;
			lock (s_overDc) {
				if (!s_overDc.TryGetValue (hdc, out var list)) return;
				over = list.ToArray ();
			}
			foreach (Graphics g in over) {
				if (g.disposed || g.hdc_surface == null || g.image_target == null) continue;
				g.image_target.TakeDrawing (g, detach: false);
				g.PresentHdc ();
				g.hdc_captured = false;
			}
		}

		/// <summary>A Graphics drawing into a GDI DC (Windows): the managed engine on a copy of the
		/// DC's visible area, written back on Flush and Dispose.</summary>
		[System.Runtime.InteropServices.DllImport ("gdi32.dll")]
		static extern uint GetObjectType (IntPtr h);

		static Graphics OverDc (IntPtr hdc)
		{
			if (!OperatingSystem.IsWindows ()) {
				// Off Windows a DC only exists if something emulates GDI (a launcher running a Windows
				// app, say). It may say what the DC stands for: a Graphics to draw with as is, or an
				// Image to draw into. Without one, a DC is what this platform does not have.
				if (AppContext.GetData ("System.Drawing.DcSurfaceResolver") is Func<IntPtr, object> resolve) {
					switch (resolve (hdc)) {
					case Graphics given: return given;
					case Image image: return FromImage (image);
					}
				}
				throw new PlatformNotSupportedException ("A device context is a Windows GDI object.");
			}
			// GdipCreateFromHDC of what is not a DC: OutOfMemory.
			uint type = GetObjectType (hdc);
			if (type != 3 && type != 4 && type != 10 && type != 12)   // OBJ_DC, OBJ_METADC, OBJ_MEMDC, OBJ_ENHMETADC
				throw SafeNativeMethods.Gdip.OutOfMemoryException ();
			WebGpuBackend.Gdip.GpHdcSurface surface = WebGpuBackend.Gdip.GpHdcSurface.ForDc (hdc);
			int w = surface?.Width ?? 1, h = surface?.Height ?? 1;
			var bmp = new Bitmap (w, h, PixelFormat.Format32bppRgb);
			bmp.SetResolution (WebGpuBackend.Gdip.GpHdcSurface.DpiOf (hdc, false), WebGpuBackend.Gdip.GpHdcSurface.DpiOf (hdc, true));
			Graphics g = FromImage (bmp);
			if (surface == null) return g;
			g.hdc_surface = surface;
			RememberOverDc (hdc, g);
			Rectangle area = surface.Area;
			if (area.X != 0 || area.Y != 0) {
				// The bitmap starts at the DC's visible area: everything is drawn moved by its origin.
				g.rec_container = new float [] { 1f, 0f, 0f, 1f, -area.X, -area.Y };
				g.rec_in_container = true;
				g.gp?.SetSurfaceOrigin (-area.X, -area.Y);
				g.PushRecordedTransform ();
			}
			return g;
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHdc (IntPtr hdc)
		{
			if (hdc == IntPtr.Zero)
				throw new ArgumentNullException ("hdc");
			lock (s_pseudoHdc) {
				if (s_pseudoHdc.TryGetValue (hdc, out Graphics owner))
					return owner.SharedForHdc ();
			}
			return OverDc (hdc);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHdc (IntPtr hdc, IntPtr hdevice)
		{
			// GdipCreateFromHDC2, with no check of its own: no DC is OutOfMemory.
			if (hdc == IntPtr.Zero)
				throw SafeNativeMethods.Gdip.OutOfMemoryException ();
			return FromHdc (hdc);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHdcInternal (IntPtr hdc)
		{
			return FromHdc (hdc);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHwnd (IntPtr hwnd)
		{
			// A window the WinForms driver made (a Control.Handle) is no GDI window on any platform,
			// Windows included: GetDC of it fails, and FromHwnd threw where stock draws on the window.
			// The driver says what drawing on it means (what Control.CreateGraphics gives).
			if (hwnd != IntPtr.Zero && AppContext.GetData ("System.Drawing.HwndGraphicsResolver") is Func<IntPtr, object> resolve
			    && resolve (hwnd) is Graphics owned)
				return owned;
			// A window's DC is Windows'; anywhere else a window has no surface of its own to draw on
			// outside a paint, and the Graphics measures (and records into nothing).
			if (!OperatingSystem.IsWindows ())
				return WebGpuBackend.GpuRaster.NewRecording ();
			IntPtr dc = WebGpuBackend.Gdip.GpHdcSurface.GetDC (hwnd);
			if (dc == IntPtr.Zero)
				throw SafeNativeMethods.Gdip.OutOfMemoryException ();
			Graphics g = OverDc (dc);
			g.hdc_window = hwnd;
			g.hdc_owned = dc;
			return g;
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public static Graphics FromHwndInternal (IntPtr hwnd)
		{
			return FromHwnd (hwnd);
		}

		public static Graphics FromImage (Image image)
		{
			if (image == null)
				throw new ArgumentNullException ("image");

			// A managed bitmap: the managed GDI+ engine draws on its pixels; what the engine leaves
			// to the recorder (GDI text) is recorded and rendered onto them before the engine's next
			// verb, or drawn as a nested scene when the bitmap is drawn first.
			// A metafile being recorded: its one Graphics (GpMetafile::GetGraphicsContext).
			// A disposed image hands GDI+ none: InvalidParameter.
			if (image.disposed)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			if (image is Metafile mf)
				return ForMetafile (mf);
			if (image is Bitmap bitmap && bitmap.managed != null) {
				// System.Drawing's own check, before GDI+ is asked.
				if ((bitmap.PixelFormat & PixelFormat.Indexed) != 0)
					throw new ArgumentException (SR.GdiplusCannotCreateGraphicsFromIndexedPixelFormat, "image");
				// GDI+ has no graphics context for these two (GpBitmap::GetGraphicsContext: OutOfMemory).
				if (bitmap.PixelFormat == PixelFormat.Format16bppArgb1555 || bitmap.PixelFormat == PixelFormat.Format16bppGrayScale)
					throw SafeNativeMethods.Gdip.OutOfMemoryException ();
				Graphics g = WebGpuBackend.GpuRaster.NewRecording ();
				g.image_target = bitmap;
				GdipFrame frame = bitmap.managed.Frame;
				if (WebGpuBackend.Gdip.GpScan.Supports (frame.Format))
					g.gp = new GpGraphics (frame);
				bitmap.AttachGraphics (g);
				return g;
			}
			// Neither a bitmap nor a metafile (a print preview's page): nothing to draw on.
			return new Graphics (IntPtr.Zero, image);
		}

		internal static Graphics FromXDrawable (IntPtr drawable, IntPtr display)
		{
			return WebGpuBackend.GpuRaster.NewRecording ();
		}

		// GdipCreateHalftonePalette: GDI's halftone palette for the screen, a new HPALETTE the caller
		// deletes. A palette is a Windows GDI object; there is none elsewhere.
		public static IntPtr GetHalftonePalette ()
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("An HPALETTE is a Windows GDI object.");
			IntPtr dc = GDIPlus.GetDC (IntPtr.Zero);
			try {
				return CreateHalftonePalette (dc);
			} finally {
				GDIPlus.ReleaseDC (IntPtr.Zero, dc);
			}
		}

		[System.Runtime.InteropServices.DllImport ("gdi32.dll")]
		static extern IntPtr CreateHalftonePalette (IntPtr hdc);

				public IntPtr GetHdc ()
		{
			CheckUse ();
			// GDI+ hands out one DC at a time.
			if (deviceContextHdc != IntPtr.Zero)
				throw new InvalidOperationException ("Object is currently in use elsewhere.");
			if (mf_rec != null) {
				// GpGraphics::GetHdc on a metafile: the recording's own DC, after an EmfPlusGetDC. Where
				// there is no GDI the handle is this Graphics' own: FromHdc of it draws into the recording.
				deviceContextHdc = mf_rec.GetHdc ();
				if (deviceContextHdc == IntPtr.Zero) {
					lock (s_pseudoHdc) {
						deviceContextHdc = (IntPtr) (++s_pseudoNext);
						s_pseudoHdc [deviceContextHdc] = this;
					}
				}
				return deviceContextHdc;
			}
			if (hdc_surface != null) {
				// A DC's Graphics hands back the DC, with what was drawn so far on it.
				PresentHdc ();
				deviceContextHdc = hdc_surface.Hdc;
				return deviceContextHdc;
			}
			if (OperatingSystem.IsWindows () && gp != null && image_target != null) {
				// GpGraphics::GetHdc on a bitmap: a memory DC over a copy of its pixels.
				image_target.FlushDrawing ();
				gethdc_surface = WebGpuBackend.Gdip.GpHdcSurface.ForFrame (image_target.managed.Frame);
				deviceContextHdc = gethdc_surface.MemoryDc;
				return deviceContextHdc;
			}
			if (OperatingSystem.IsWindows () && GpuRecorder != null && image_target == null && !print_mode
			    && RecordingSurface.Width > 0 && RecordingSurface.Height > 0) {
				// A window's picture: a real DC GDI can draw into (see GpRecordingDc).
				recording_dc = WebGpuBackend.Gdip.GpRecordingDc.Create (RecordingSurface);
				if (recording_dc != null) {
					deviceContextHdc = recording_dc.Hdc;
					return deviceContextHdc;
				}
			}
			lock (s_pseudoHdc) {
				deviceContextHdc = (IntPtr) (++s_pseudoNext);
				s_pseudoHdc [deviceContextHdc] = this;
			}
			return deviceContextHdc;
		}

		/// <summary>The device size of what a recording Graphics draws (a window's client area), when
		/// whoever made it knows: what GetHdc's DC covers.</summary>
		internal Size RecordingSurface;
		WebGpuBackend.Gdip.GpRecordingDc recording_dc;

		/// <summary>ReleaseHdc of a recording's DC: what GDI drew there goes into the recording, at the
		/// device position it was drawn at.</summary>
		void EndRecordingDc ()
		{
			var dc = recording_dc;
			recording_dc = null;
			deviceContextHdc = IntPtr.Zero;   // drawing again: the DC is no longer out
			try {
				FlushHdc (dc.Hdc);
				using Bitmap drawn = dc.TakeDrawn (out Point at);
				if (drawn == null) return;
				var pts = new [] { new PointF (at.X, at.Y), new PointF (at.X + drawn.Width, at.Y), new PointF (at.X, at.Y + drawn.Height) };
				RecordedTransformPoints (CoordinateSpace.World, CoordinateSpace.Device, pts);
				InterpolationMode mode = InterpolationMode;
				InterpolationMode = InterpolationMode.NearestNeighbor;
				DrawImage (drawn, pts);
				InterpolationMode = mode;
			} finally {
				dc.Dispose ();
			}
		}

		void EndHdc ()
		{
			if (recording_dc != null) {
				EndRecordingDc ();
				return;
			}
			if (gethdc_surface != null) {
				if (OperatingSystem.IsWindows ()) {
					gethdc_surface.CopyBack (image_target.managed.Frame);
					gethdc_surface.Dispose ();
				}
				gethdc_surface = null;
			} else if (hdc_surface != null && deviceContextHdc == hdc_surface.Hdc) {
				// GDI drew on the DC itself: start again from what it shows now.
				hdc_captured = false;
			}
			lock (s_pseudoHdc) s_pseudoHdc.Remove (deviceContextHdc);
		}

		/// <summary>FromHdc of a handle this Graphics gave out: a Graphics on the same target.</summary>
		Graphics SharedForHdc ()
		{
			if (mf_rec != null) return SharedForMetafileHdc ();
			if (image_target != null) {
				Graphics g = FromImage (image_target);
				return g;
			}
			Graphics r = WebGpuBackend.GpuRaster.NewRecording ();
			// Drawn into the owner's scene when it is disposed, where it was drawn. DrawScene maps a
			// source rectangle onto a destination and clips to it, so both are the whole plane: a unit
			// square cut every blit into such a DC down to the pixel at its origin (DotNetBar's combo
			// boxes, drawn in a memory DC and blitted to the window, showed as a single dot).
			Graphics owner = this;
			const float Far = 1e6f;
			r.DisposeHook = gg => { object scene = gg.TakeRecordedScene (); if (scene != null) owner.GpuRecorder?.DrawScene (scene, -Far, -Far, 2 * Far, 2 * Far, -Far, -Far, 2 * Far, 2 * Far); };
			return r;
		}
		
				public Color GetNearestColor (Color color)
		{
			CheckUse ();
						// GpGraphics::GetNearestColor: a 16-bit surface rounds the colour down to its own levels
			// (each channel's top bits, shifted back up); a 32-bit one keeps it.
			uint c = (uint) color.ToArgb ();
			PixelFormat f = image_target != null ? image_target.PixelFormat : PixelFormat.Format32bppArgb;
			if (f == PixelFormat.Format16bppRgb565)
				c = 0xff000000u | (c & 0xf80000) | (c & 0xfc00) | (c & 0xf8);
			else if (f == PixelFormat.Format16bppRgb555 || f == PixelFormat.Format16bppArgb1555)
				c = 0xff000000u | (c & 0xf80000) | (c & 0xf800) | (c & 0xf8);
			return Color.FromArgb (unchecked ((int) c));
		}

		
		public void IntersectClip (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
			CheckUse (region);
						EngineClipRegion (region, CombineMode.Intersect);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) { RecordRegionClip (region, false); return; }
			
		}
		
				public void IntersectClip (RectangleF rect)
		{
			CheckUse ();
			EngineClipRect (rect, CombineMode.Intersect);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, false); return; }
			
		}

		public void IntersectClip (Rectangle rect)
		{			
			CheckUse ();
			// The recorder too, as the RectangleF overload does: this one had no recording path, so
			// every integer clip -- TextRenderer's, a theme part's -- was dropped under the GPU.
						EngineClipRect (rect, CombineMode.Intersect);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) { GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, false); return; }
			
		}

		public bool IsVisible (Point point)
		{
			CheckUse ();
			return IsVisible ((PointF) point);
		}

		public bool IsVisible (RectangleF rect)
		{
			CheckUse ();
			// GpGraphics::IsVisible(rect): the rectangle's device bounds meet the visible clip.
			if (gp == null) return true;
			SyncEngine ();
			var pts = new [] { new PointF (rect.Left, rect.Top), new PointF (rect.Right, rect.Top), new PointF (rect.Left, rect.Bottom), new PointF (rect.Right, rect.Bottom) };
			gp.WorldToDevice.Transform (pts);
			float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
			foreach (PointF p in pts) { x0 = Math.Min (x0, p.X); y0 = Math.Min (y0, p.Y); x1 = Math.Max (x1, p.X); y1 = Math.Max (y1, p.Y); }
			var d = Rectangle.FromLTRB (GpMatrix.RasterizerCeiling (x0), GpMatrix.RasterizerCeiling (y0), GpMatrix.RasterizerCeiling (x1), GpMatrix.RasterizerCeiling (y1));
			return gp.IsVisibleDevice (d);
		}

		public bool IsVisible (PointF point)
		{
			CheckUse ();
			// GpGraphics::IsVisible(point): the device point rounded, inside the visible clip.
			if (gp == null) return true;
			SyncEngine ();
			PointF p = gp.WorldToDevice.Transform (point);
			return gp.IsVisibleDevice ((int) MathF.Floor (p.X + 0.5f), (int) MathF.Floor (p.Y + 0.5f));
		}

		public bool IsVisible (Rectangle rect)
		{
			CheckUse ();
			return IsVisible ((RectangleF) rect);
		}

		public bool IsVisible (float x, float y)
		{
			CheckUse ();
			return IsVisible (new PointF (x, y));
		}
		
		public bool IsVisible (int x, int y)
		{
			CheckUse ();
			return IsVisible (new Point (x, y));
		}
		
		public bool IsVisible (float x, float y, float width, float height)
		{
			CheckUse ();
			// GpRectF::IsEmptyArea: no width or height is nothing to see.
			if (!(width > 0) || !(height > 0))
				return false;
			return IsVisible (new RectangleF (x, y, width, height));
		}

		
		public bool IsVisible (int x, int y, int width, int height)
		{
			CheckUse ();
			if (width <= 0 || height <= 0)
				return false;
			return IsVisible (new Rectangle (x, y, width, height));
		}

		
		public Region[] MeasureCharacterRanges (string text, Font font, RectangleF layoutRect, StringFormat stringFormat)
		{
			if ((text == null) || (text.Length == 0))
				return new Region [0];

			if (font == null)
				throw new ArgumentNullException ("font");

			// GdipGetStringFormatMeasurableCharacterRangeCount of no format (or a disposed one) is
			// InvalidParameter; with no ranges System.Drawing answers without asking the Graphics.
			if (stringFormat == null)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			stringFormat.Live ();

			int regcount = stringFormat.GetMeasurableCharacterRangeCount ();
			if (regcount == 0)
				return new Region[0];
			CheckUse (font);

			Region[] gpRegions = GdiPlusCharacterRanges (text, font, layoutRect, stringFormat, regcount);
			if (gpRegions != null)
				return gpRegions;
			if (s_gpuRasterMode)
				return MeasureCharacterRangesManaged (text, font, layoutRect, stringFormat, regcount);

			IntPtr[] native_regions = new IntPtr [regcount];
			Region[] regions = new Region [regcount];
			
			for (int i = 0; i < regcount; i++) {
				regions[i] = new Region ();
				native_regions[i] = regions[i].NativeObject;
			}
			
			/*GDIP*/; 
							

			return regions;							
		}

		// Managed character-range measurement for GPU-raster mode. The Font carries no native GDI+
		// handle there (see Font.s_gpuRasterMode), so GdipMeasureCharacterRanges would fail with
		// InvalidParameter. Lay the text out on a single line - the in-tree callers (TabControl tab
		// sizing, LinkLabel link hit-testing) all measure with NoWrap - and turn each requested
		// range into its bounding rectangle, using the same metrics the WGSL renderer draws with.
		private Region[] MeasureCharacterRangesManaged (string text, Font font, RectangleF layoutRect,
			StringFormat stringFormat, int regcount)
		{
			CharacterRange[] ranges = stringFormat.MeasurableCharacterRanges;
			Region[] regions = new Region [regcount];
			float em = font.SizeInPoints * EmDpiY / 72f;

			for (int i = 0; i < regcount; i++) {
				CharacterRange range = (ranges != null && i < ranges.Length)
					? ranges [i] : new CharacterRange (0, text.Length);

				int first = Math.Max (0, Math.Min (range.First, text.Length));
				int length = Math.Max (0, Math.Min (range.Length, text.Length - first));

				float x = 0f, unused;
				if (first > 0)
					WebGpuBackend.GpuRaster.MeasureText (text.Substring (0, first), em, out x, out unused);

				float w = 0f, h;
				WebGpuBackend.GpuRaster.MeasureText (length > 0 ? text.Substring (first, length) : "I", em, out w, out h);
				h = font.GetHeight () > 0 ? font.GetHeight () : h;
				if (length == 0)
					w = 0f;

				regions [i] = new Region (new RectangleF (layoutRect.X + x, layoutRect.Y, w, h));
			}

			return regions;
		}

		private unsafe SizeF GdipMeasureString (IntPtr graphics, string text, Font font, ref RectangleF layoutRect,
			IntPtr stringFormat, StringFormat managedFormat = null)
		{
			if ((text == null) || (text.Length == 0))
				return SizeF.Empty;

			// "&File" is a File with a line under the F: the ampersand names the key that reaches
			// it and is no part of the caption. Windows does not measure it, and neither does the
			// drawing below -- but this did, so every menu came out about a character wider than
			// the same menu in Windows.
			if (s_gpuRasterMode && managedFormat != null
				&& managedFormat.HotkeyPrefix != Text.HotkeyPrefix.None) {
				int ignored;
				text = StripHotkeyPrefix (text, out ignored);
			}

			if (font == null)
				throw new ArgumentNullException ("font");

			if (print_mode)
				return PrintMeasureString (text, font, layoutRect, managedFormat, out _, out _);

			// GDI+'s own measurement (FullTextImager's nominal layout, GpTextLayout).
			if (GdiPlusMeasure (text, font, layoutRect, managedFormat, out SizeF gpSize, out _, out _))
				return gpSize;

			if (s_gpuRasterMode) {
				// Managed measurement (no libgdiplus), consistent with the WGSL-rendered font.
				// A layout width means "wrap here", and the caller wants the height that wrapping
				// produces -- SharpDevelop's About box measures its credits exactly this way, to know
				// how far it has to scroll them.
				float em = font.SizeInPoints * EmDpiY / 72f;
				int simulations = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
				// Measured in the family it will be DRAWN in: a run measured with one face and drawn
				// with another does not fit where the caller was told it would.
				string measureFamily = font.FontFamily?.Name;
				// The height a caller gets back has to be the height the text will occupy, which is
				// the font's line height and not its em size. ListBox asks exactly this question to
				// size its rows, so answering 12 where Windows answers 16 made every list, and every
				// other control that measures a line, a quarter tighter than the real thing.
				float lineHeight = font.GetHeight ();
				if (lineHeight <= 0) lineHeight = em;
				// The line box a caller is told about is a whole number of pixels, and callers truncate
				// what they get back: a list sizes its rows by it. Windows answers fifteen for a font
				// whose design line spacing is 15.96 -- its own metric truncates -- but its rows are
				// sixteen even so, and it is the rows that everything here is measured against. Rounding
				// up gives those rows; answering the smaller number made every list a pixel tight.
				lineHeight = (float) Math.Ceiling (lineHeight);
				// The margin the text will be drawn inside -- see Overhang. A caller that sizes a
				// control to what it is told here and then draws the text in that space needs the
				// margin counted in, or the last letter or two run out of the room measured for them.
				float margin = Margins (font, managedFormat);
				if (layoutRect.Width > 0) {
						string[] wrapped = WrapLines (text, em, simulations, measureFamily, layoutRect.Width, null);
						float widest = 0f;
						foreach (string line in wrapped) {
							WebGpuBackend.GpuRaster.MeasureText (line, em, simulations, measureFamily, out float lw, out float _);
							if (lw > widest) widest = lw;
						}
						return new SizeF (widest + margin, lineHeight * Math.Max (1, wrapped.Length));
				}
				WebGpuBackend.GpuRaster.MeasureText (text, em, simulations, measureFamily, out float mw, out float mh);
				return new SizeF (mw + margin, lineHeight * Math.Max (1, mh / Math.Max (1f, em)));
			}

			RectangleF boundingBox = new RectangleF ();

			/*GDIP*/;
			

			return new SizeF (boundingBox.Width, boundingBox.Height);
		}

		public SizeF MeasureString (string text, Font font)
		{
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			return MeasureString (text, font, SizeF.Empty);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea)
		{
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);
			return GdipMeasureString (nativeObject, text, font, ref rect, IntPtr.Zero);
		}

		public SizeF MeasureString (string text, Font font, int width)
		{				
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			RectangleF rect = new RectangleF (0, 0, width, Int32.MaxValue);
			return GdipMeasureString (nativeObject, text, font, ref rect, IntPtr.Zero);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea, StringFormat stringFormat)
		{
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			// A disposed format hands GDI+ none: the default format.
			if (stringFormat != null && stringFormat.IsDisposed) stringFormat = null;
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);
			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, format, stringFormat);
		}

		public SizeF MeasureString (string text, Font font, int width, StringFormat format)
		{
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			// A disposed format hands GDI+ none: the default format.
			if (format != null && format.IsDisposed) format = null;
			RectangleF rect = new RectangleF (0, 0, width, Int32.MaxValue);
			IntPtr stringFormat = (format == null) ? IntPtr.Zero : format.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, stringFormat, format);
		}

		public SizeF MeasureString (string text, Font font, PointF origin, StringFormat stringFormat)
		{
			if (string.IsNullOrEmpty (text))
				return SizeF.Empty;
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			// A disposed format hands GDI+ none: the default format.
			if (stringFormat != null && stringFormat.IsDisposed) stringFormat = null;
			RectangleF rect = new RectangleF (origin.X, origin.Y, 0, 0);
			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;
			return GdipMeasureString (nativeObject, text, font, ref rect, format, stringFormat);
		}

		public SizeF MeasureString (string text, Font font, SizeF layoutArea, StringFormat stringFormat, 
			out int charactersFitted, out int linesFilled)
		{	
			if (string.IsNullOrEmpty (text)) {
				charactersFitted = 0;
				linesFilled = 0;
				return SizeF.Empty;
			}
			if (font == null)
				throw new ArgumentNullException ("font");
			CheckUse (font);
			// A disposed format hands GDI+ none: the default format.
			if (stringFormat != null && stringFormat.IsDisposed) stringFormat = null;
			charactersFitted = 0;
			linesFilled = 0;

			if ((text == null) || (text.Length == 0))
				return SizeF.Empty;

			if (font == null)
				throw new ArgumentNullException ("font");

			RectangleF boundingBox = new RectangleF ();
			RectangleF rect = new RectangleF (0, 0, layoutArea.Width, layoutArea.Height);

			if (print_mode)
				return PrintMeasureString (text, font, rect, stringFormat, out charactersFitted, out linesFilled);

			if (GdiPlusMeasure (text, font, rect, stringFormat, out SizeF gpSize, out charactersFitted, out linesFilled))
				return gpSize;

			IntPtr format = (stringFormat == null) ? IntPtr.Zero : stringFormat.NativeObject;

			unsafe {
				fixed (int* pc = &charactersFitted, pl = &linesFilled) {
					/*GDIP*/;
					
				}
			}
			return new SizeF (boundingBox.Width, boundingBox.Height);
		}

		public void MultiplyTransform (Matrix matrix)
		{
			MultiplyTransform (matrix, MatrixOrder.Prepend);
		}

		public void MultiplyTransform (Matrix matrix, MatrixOrder order)
		{
			if (matrix == null)
				throw new ArgumentNullException ("matrix");
			CheckUse ();
			// A disposed matrix hands GDI+ no matrix, and GdipMultiplyWorldTransform does nothing.
			if (matrix.IsDisposed)
				return;
			// It refuses a singular one.
			if (!matrix.Gp.IsInvertible)
				throw new ArgumentException (SR.GdiplusInvalidParameter);

						// GpMatrix::Multiply onto the world transform, prepended or appended.
			mf_rec?.MultiplyWorldTransform (matrix, order);
			GpMatrix w = RecWorld;
			w.Multiply (matrix.Gp, order == MatrixOrder.Append);
			SetRecWorld (w);
			
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public void ReleaseHdc (IntPtr hdc)
		{
			ReleaseHdcInternal (hdc);
		}

		public void ReleaseHdc ()
		{
			ReleaseHdcInternal (deviceContextHdc);
		}

		[MonoLimitation ("Can only be used when hdc was provided by Graphics.GetHdc() method")]
		[EditorBrowsable (EditorBrowsableState.Never)]
				public void ReleaseHdcInternal (IntPtr hdc)
		{
			// GdipReleaseDC: no DC, or none out, is InvalidParameter; otherwise the DC that is out is
			// released whichever handle is passed.
			if (hdc == IntPtr.Zero || deviceContextHdc == IntPtr.Zero)
				throw new ArgumentException ("Parameter is not valid.");
			if (mf_rec != null) {
				mf_rec.ReleaseHdc ();
				lock (s_pseudoHdc) s_pseudoHdc.Remove (deviceContextHdc);
				deviceContextHdc = IntPtr.Zero;
				return;
			}
			EndHdc ();
			deviceContextHdc = IntPtr.Zero;
		}
		
				public void ResetClip ()
		{
			CheckUse ();
			EngineResetClip ();
			if (GpuRecorder != null) {
				if (print_mode) GpuRecorder.ResetAllClips ();
								else GpuRecorder.ClearClip ();
				return;
			}
		}

		public void ResetTransform ()
		{
			CheckUse ();
			mf_rec?.ResetWorldTransform ();
			rec_world = new float [] { 1f, 0f, 0f, 1f, 0f, 0f };
			PushRecordedTransform ();
		}

		public void Restore (GraphicsState gstate)
		{
			if (gstate == null)
				throw new NullReferenceException ();
			CheckUse ();
			// the possible NRE thrown by gstate.nativeState match MS behaviour
			int token = gstate.nativeState;
			mf_rec?.Restore ((uint) token);
			RestoreState (token);
		}

		public void RotateTransform (float angle)
		{
			CheckUse ();
			RotateTransform (angle, MatrixOrder.Prepend);
		}

		public void RotateTransform (float angle, MatrixOrder order)
		{
			CheckUse ();
			mf_rec?.RotateWorldTransform (angle, order);
			GpMatrix w = RecWorld;
			w.Rotate (angle, order == MatrixOrder.Append);
			SetRecWorld (w);
			
		}

		public GraphicsState Save ()
		{
			CheckUse ();
			return new GraphicsState (SaveState (false));
		}

		public void ScaleTransform (float sx, float sy)
		{
			CheckUse ();
			ScaleTransform (sx, sy, MatrixOrder.Prepend);
		}

		public void ScaleTransform (float sx, float sy, MatrixOrder order)
		{
			CheckUse ();
			GpMatrix w = RecWorld;
			w.Scale (sx, sy, order == MatrixOrder.Append);
			// GdipScaleWorldTransform refuses a world transform it cannot invert.
			if (!w.IsInvertible)
				throw new ArgumentException (SR.GdiplusInvalidParameter);
			mf_rec?.ScaleWorldTransform (sx, sy, order);
			SetRecWorld (w);
			
		}

		
		public void SetClip (RectangleF rect)
		{
			CheckUse ();
                        SetClip (rect, CombineMode.Replace);
		}

		
		public void SetClip (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			CheckUse (path);
			SetClip (path, CombineMode.Replace);
		}

		
		public void SetClip (Rectangle rect)
		{
			CheckUse ();
			SetClip (rect, CombineMode.Replace);
		}

		
		public void SetClip (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			CheckUse (g);
			SetClip (g, CombineMode.Replace);
		}

		
		public void SetClip (Graphics g, CombineMode combineMode)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			CheckUse (g);
			CheckCombine (combineMode);
			
			// The other Graphics' clip, in device space.
			if (mf_rec != null) mf_rec.SetClipRegion (g.Clip, combineMode);
			if (gp != null) {
				SyncEngine ();
				GpRegion other = g.gp?.AppClip;
				gp.CombineDeviceClip (other, combineMode);
			}
			if (GpuRecorder != null) { GpuRecorder.ClearClip (); return; }
			
		}

		
				public void SetClip (Rectangle rect, CombineMode combineMode)
		{
			CheckUse ();
			CheckCombine (combineMode);
			EngineClipRect (rect, combineMode);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) {
				// Replace discards the whole clip, as GDI+'s does -- every clip set so far, not just
				// the last (on screen as in print).
				if (combineMode == CombineMode.Replace) GpuRecorder.ResetAllClips ();
								GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, combineMode == CombineMode.Exclude);
				return;
			}
			
		}

		
				public void SetClip (RectangleF rect, CombineMode combineMode)
		{
			CheckUse ();
			CheckCombine (combineMode);
			EngineClipRect (rect, combineMode);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) {
				// Replace discards the whole clip, as GDI+'s does -- every clip set so far, not just
				// the last (on screen as in print).
				if (combineMode == CombineMode.Replace) GpuRecorder.ResetAllClips ();
								GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, combineMode == CombineMode.Exclude);
				return;
			}
			
		}

		
		/// <summary>The bounding box of a region, without needing a Graphics to ask against.
		/// Region.GetBounds wants one, and the whole point here is that there is not a usable
		/// one -- a recording Graphics has no GDI+ surface at all.</summary>
		static RectangleF RegionBounds (Region region)
		{
			RectangleF[] scans = region.GetRegionScans (new Drawing2D.Matrix ());
			if (scans.Length == 0)
				return RectangleF.Empty;
			RectangleF bounds = scans[0];
			for (int i = 1; i < scans.Length; i++)
				bounds = RectangleF.Union (bounds, scans[i]);
			return bounds;
		}

		// A region as a recorded clip: one rectangle as a rectangle, several as a path of them, none
		// as an empty clip.
		void RecordRegionClip (Region region, bool exclude)
		{
			if (region.IsInfinite (this)) {
				if (exclude) GpuRecorder.SetClipRect (0, 0, 0, 0, false);
				return;
			}
			RectangleF[] scans = region.GetRegionScans (new Matrix ());
			if (scans.Length == 1) {
				GpuRecorder.SetClipRect (scans [0].X, scans [0].Y, scans [0].Width, scans [0].Height, exclude);
				return;
			}
			if (scans.Length == 0) {
				if (!exclude) GpuRecorder.SetClipRect (0, 0, 0, 0, false);
				return;
			}
			var xy = new float [scans.Length * 8];
			var types = new byte [scans.Length * 4];
			for (int i = 0; i < scans.Length; i++) {
				RectangleF r = scans [i];
				float [] c = { r.Left, r.Top, r.Right, r.Top, r.Right, r.Bottom, r.Left, r.Bottom };
				Array.Copy (c, 0, xy, i * 8, 8);
				types [i * 4] = 0; types [i * 4 + 1] = 1; types [i * 4 + 2] = 1; types [i * 4 + 3] = 0x81;
			}
			GpuRecorder.SetClipPath (xy, types, true, exclude);
		}

		public void SetClip (Region region, CombineMode combineMode)
		{
						if (region == null)
				throw new ArgumentNullException ("region");
			CheckUse (region);
			CheckCombine (combineMode);
			EngineClipRegion (region, combineMode);
			if (PrintRecordClip ()) return;
			if (GpuRecorder != null) {
				// Replacing the clip means "from here on, draw inside this instead". It does NOT
				// mean "go back to drawing where whatever came before went" -- but that is what
				// clearing the recorder's clip and stopping there did. The recorder nests a
				// container per clip and renders a container's own content before its children, so
				// popping out of the container a control had been painting into recorded everything
				// after it BEFORE that container rather than after it. LinkLabel assigns
				// Graphics.Clip immediately before drawing its text, so the text was recorded
				// underneath the control's own background and never appeared at all -- emitted at
				// the right place, in the right colour, and painted over.
				//
				// Replace discards the WHOLE clip, as GDI+'s does, not just the clip set last. The
				// usual save-and-restore -- `Region old = g.Clip; g.SetClip(path); ...; g.Clip = old`
				// -- relies on it: a recording Graphics answers Clip with an infinite region, so the
				// restore is a Replace with "everything", and removing only the last clip left any
				// set in between in force. DotNetBar's ribbon sets two while painting its quick-access
				// toolbar, and every tab after it was clipped to the toolbar's outline: gone.
				// Intersect and Exclude narrow the clip there is; Union and Xor are approximated by
				// replacing the last clip, as before.
				if (print_mode || combineMode == CombineMode.Replace) GpuRecorder.ResetAllClips ();
				else if (combineMode != CombineMode.Intersect && combineMode != CombineMode.Exclude) GpuRecorder.ClearClip ();
				if (combineMode == CombineMode.Exclude) RecordRegionClip (region, true);
								else if (!region.IsInfinite (this)) RecordRegionClip (region, false);
				return;
			} 
			
		}

		
		public void SetClip (GraphicsPath path, CombineMode combineMode)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			CheckUse (path);
			CheckCombine (combineMode);
						EngineClipPath (path, combineMode);
			if (PrintRecordClip ()) return;
			PrintClip (path, combineMode);
			
		}

		
		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, PointF [] pts)
		{
			if (pts == null)
				throw new ArgumentNullException ("pts");
			CheckUse ();
						TransformPointsF (destSpace, srcSpace, pts);
		}


		public void TransformPoints (CoordinateSpace destSpace, CoordinateSpace srcSpace, Point [] pts)
		{						
			if (pts == null)
				throw new ArgumentNullException ("pts");
			CheckUse ();
						var f = new PointF [pts.Length];
			for (int i = 0; i < pts.Length; i++) f [i] = pts [i];
			TransformPointsF (destSpace, srcSpace, f);
			// GpMatrix::TransformPoints(Point*): each coordinate floor(v + 0.5) (frintm).
			for (int i = 0; i < pts.Length; i++) pts [i] = new Point ((int) MathF.Floor (f [i].X + 0.5f), (int) MathF.Floor (f [i].Y + 0.5f));
		}

		
				public void TranslateClip (int dx, int dy)
		{
			CheckUse ();
			TranslateClip ((float) dx, (float) dy);
		}

		
				public void TranslateClip (float dx, float dy)
		{
			CheckUse ();
			// GpGraphics::OffsetClip: the app clip moved by the vector, in device space.
			EngineOffsetClip (dx, dy);
			if (PrintRecordClip ()) return;
		}

		public void TranslateTransform (float dx, float dy)
		{
			CheckUse ();
			TranslateTransform (dx, dy, MatrixOrder.Prepend);
		}

		
		/// <summary>Draw what follows as a picture of <paramref name="source"/> stretched into
		/// <paramref name="dest"/> at <paramref name="opacity"/>, until <see cref="EndSnapshot"/>: GDI's
		/// StretchBlt / AlphaBlend of an off-screen bitmap. Without a GPU recorder the content is drawn
		/// where it is, unstretched and opaque. <paramref name="stretchBlt"/> stretches as StretchBlt does in
		/// a DC's default BLACKONWHITE mode (a shrink ANDs the pixels it drops into those it keeps);
		/// otherwise it is point-sampled, as AlphaBlend stretches. <paramref name="windowBlend"/> blends as
		/// AlphaBlend does onto a WINDOW's surface, where each term is truncated on its own.</summary>
		internal void BeginSnapshot (RectangleF source, RectangleF dest, float opacity, bool stretchBlt = false, bool windowBlend = false)
		{
			if (GpuRecorder == null) return;
			RecordedBeginSnapshot ();
			GpuRecorder.PushSnapshot (source.X, source.Y, source.Width, source.Height, dest.X, dest.Y, dest.Width, dest.Height, opacity, stretchBlt, windowBlend);
		}

		internal void EndSnapshot ()
		{
			if (GpuRecorder == null) return;
			GpuRecorder.PopSnapshot ();
			RecordedEndSnapshot ();
		}

		internal bool CanSnapshot => GpuRecorder != null;

		public void TranslateTransform (float dx, float dy, MatrixOrder order)
		{			
			CheckUse ();
			// WinForms draws a composite control by translating to each part's bounds, drawing it at
			// the origin and resetting -- ToolStrip does exactly this per item. The recorder ignored
			// the transform, so every item was drawn at the same place and their labels sat on top of
			// one another.
			mf_rec?.TranslateWorldTransform (dx, dy, order);
			GpMatrix w = RecWorld;
			w.Translate (dx, dy, order == MatrixOrder.Append);
			SetRecWorld (w);
			
		}

		public Region Clip {
			get {
				CheckUse ();
				// The engine knows the clip; a recording Graphics answers infinite.
				if (gp != null) { SyncEngine (); return new Region (gp.GetClip ()); }
				if (RecordingClipState () is WebGpuBackend.Gdip.GpGraphics rc && rc.AppClip != null) return new Region (rc.GetClip ());
				return new Region ();
			}
			set {
				if (value == null)
					throw new ArgumentNullException ("region");
				CheckUse ();
				SetClip (value, CombineMode.Replace);
			}
		}

		public RectangleF ClipBounds {
			get {
				CheckUse ();
								if (gp != null) {
					SyncEngine ();
					// GpGraphics::GetClipBounds: the app clip's DEVICE region bounds, back in world units.
					GpRegion app = gp.AppClip;
					if (app == null) return new Region (gp.GetClip ()).Bounds ();
					Rectangle d = app.Device (GpMatrix.CreateIdentity ()).Bounds;
					if (!gp.DeviceToWorld (out GpMatrix inv)) return RectangleF.Empty;
					var pts = new [] { new PointF (d.Left, d.Top), new PointF (d.Right, d.Top), new PointF (d.Left, d.Bottom), new PointF (d.Right, d.Bottom) };
					inv.Transform (pts);
					float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
					foreach (PointF p in pts) { x0 = Math.Min (x0, p.X); y0 = Math.Min (y0, p.Y); x1 = Math.Max (x1, p.X); y1 = Math.Max (y1, p.Y); }
					return RectangleF.FromLTRB (x0, y0, x1, y1);
				}
				return print_mode || image_target != null ? RecordedVisibleBounds () : new RectangleF (0, 0, 1 << 20, 1 << 20);
			}
		}

		// Mirrored in managed state so it survives on a RECORDING Graphics. Previously the
		// setter returned early there, so CompositingMode did not even round-trip, and
		// SourceCopy silently alpha-blended.
		private CompositingMode _compositingMode = CompositingMode.SourceOver;

		public CompositingMode CompositingMode {
			get { CheckUse (); return _compositingMode; }
			set {
				if (value < CompositingMode.SourceOver || value > CompositingMode.SourceCopy)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (CompositingMode));
				CheckUse ();
				_compositingMode = value;
				mf_rec?.SetCompositingMode (value);
				GpuRecorder?.SetCompositingMode (value == CompositingMode.SourceCopy);
			}
		}

		public CompositingQuality CompositingQuality {
			get { CheckUse (); return _compositingQuality; }
			set {
				if (value < CompositingQuality.Invalid || value > CompositingQuality.AssumeLinear)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (CompositingQuality));
				CheckUse ();
				_compositingQuality = value;
				mf_rec?.SetCompositingQuality (value);
			}
		}

		public float DpiX {
			get { CheckUse (); return mf_rec != null ? mf_rec.DpiX : print_mode ? print_dpi_x : image_target != null ? image_target.HorizontalResolution : WebGpuBackend.DisplayDpi.X; }
		}

		public float DpiY {
			get { CheckUse (); return mf_rec != null ? mf_rec.DpiY : print_mode ? print_dpi_y : image_target != null ? image_target.VerticalResolution : WebGpuBackend.DisplayDpi.Y; }
		}

		public InterpolationMode InterpolationMode {
			get { CheckUse (); return _interpolation; }
			set {
				if (value < InterpolationMode.Invalid || value > InterpolationMode.HighQualityBicubic)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (InterpolationMode));
				CheckUse ();
				// GdipSetInterpolationMode: Default and Low are Bilinear, High is HighQualityBicubic.
				if ((uint) value >= 8)
					throw new ArgumentException ("Parameter is not valid.");
				if (value == InterpolationMode.Default || value == InterpolationMode.Low) value = InterpolationMode.Bilinear;
				else if (value == InterpolationMode.High) value = InterpolationMode.HighQualityBicubic;
				_interpolation = value;
				mf_rec?.SetInterpolationMode (value);
			}
		}

		public bool IsClipEmpty {
			get {
				CheckUse ();
				if (gp == null) return false;
				SyncEngine ();
				GpRegion c = gp.AppClip;
				return c != null && c.Device (GpMatrix.CreateIdentity ()).IsEmpty;
			}
		}

		public bool IsVisibleClipEmpty {
			get {
				CheckUse ();
				if (gp == null) return false;
				SyncEngine ();
				return gp.VisibleClipEmpty;
			}
		}

		public float PageScale {
			get { CheckUse (); return rec_page_scale; }
			set {
				CheckUse ();
				// GpGraphics::SetPageTransform: 1e-9 to 1e9.
				if ((double) value < 1e-9 || value > 1000000000f)
					throw new ArgumentException ("Parameter is not valid.");
				rec_page_scale = value;
				mf_rec?.SetPageTransform (rec_unit, rec_page_scale);
				PushRecordedTransform ();
			}
		}

		public GraphicsUnit PageUnit {
			get { CheckUse (); return rec_unit; }
			set {
				if (value < GraphicsUnit.World || value > GraphicsUnit.Millimeter)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (GraphicsUnit));
				CheckUse ();
				if (value == GraphicsUnit.World)
					throw new ArgumentException ("Parameter is not valid.");
				rec_unit = value;
				mf_rec?.SetPageTransform (rec_unit, rec_page_scale);
				PushRecordedTransform ();
			}
		}

		public PixelOffsetMode PixelOffsetMode {
			get { CheckUse (); return _pixelOffset; }
			set {
				if (value < PixelOffsetMode.Invalid || value > PixelOffsetMode.Half)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (PixelOffsetMode));
				CheckUse ();
				if ((uint) value >= 5)
					throw new ArgumentException ("Parameter is not valid.");
				_pixelOffset = value;
				mf_rec?.SetPixelOffsetMode (value);
			}
		}

		public Point RenderingOrigin {
			get { CheckUse (); return _renderingOrigin; }
			set { CheckUse (); _renderingOrigin = value; mf_rec?.SetRenderingOrigin (value.X, value.Y); }
		}

		public SmoothingMode SmoothingMode {
			get { CheckUse (); return gpu_smoothing; }
			set {
				if (value < SmoothingMode.Invalid || value > SmoothingMode.AntiAlias)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (SmoothingMode));
				CheckUse ();
				if ((uint) value >= 6)
					throw new ArgumentException ("Parameter is not valid.");
				// GdipSetSmoothingMode records the mode as the caller gave it.
				mf_rec?.SetAntiAliasMode (value);
				// GDI+ stores Default and HighSpeed as None, HighQuality as AntiAlias.
				gpu_smoothing = value == SmoothingMode.Default || value == SmoothingMode.HighSpeed ? SmoothingMode.None
					: value == SmoothingMode.HighQuality ? SmoothingMode.AntiAlias : value;
			}
		}

		public int TextContrast {
			get { CheckUse (); return recorded_text_contrast; }
			set {
				CheckUse ();
				// GpGraphics::SetTextContrast: 0 to 12.
				if ((uint) value > 12)
					throw new ArgumentException ("Parameter is not valid.");
				recorded_text_contrast = value;
				mf_rec?.SetTextContrast (value);
			}
		}

		public TextRenderingHint TextRenderingHint {
			get { CheckUse (); return recorded_text_hint; }
			set {
				if (value < TextRenderingHint.SystemDefault || value > TextRenderingHint.ClearTypeGridFit)
					throw new System.ComponentModel.InvalidEnumArgumentException ("value", (int) value, typeof (TextRenderingHint));
				CheckUse ();
				recorded_text_hint = value;
				mf_rec?.SetTextRenderingHint (value);
			}
		}

		public System.Numerics.Matrix3x2 TransformElements {
			get {
				CheckUse ();
				return new System.Numerics.Matrix3x2 (rec_world [0], rec_world [1], rec_world [2], rec_world [3], rec_world [4], rec_world [5]);
			}
			set {
				using (Matrix m = new Matrix (value))
					Transform = m;
			}
		}

		public Matrix Transform {
			get {
				CheckUse ();
				return new Matrix (rec_world [0], rec_world [1], rec_world [2], rec_world [3], rec_world [4], rec_world [5]);
			}
			set {
				// System.Drawing hands GDI+ value.NativeMatrix unchecked: null is a NullReferenceException.
				if (value == null)
					throw new NullReferenceException ();
				CheckUse (value);
				// GdipSetWorldTransform refuses a singular matrix.
				if (!value.Gp.IsInvertible)
					throw new ArgumentException ("Parameter is not valid.");
				mf_rec?.SetWorldTransform (value);
				SetRecWorld (value.Gp);
			}
		}

		public RectangleF VisibleClipBounds {
			get {
				CheckUse ();
				if (gp != null) {
					// The visible clip's device bounds, back in world units.
					SyncEngine ();
					Rectangle d = gp.VisibleDeviceBounds;
					if (!gp.DeviceToWorld (out GpMatrix inv)) return RectangleF.Empty;
					var pts = new [] { new PointF (d.Left, d.Top), new PointF (d.Right, d.Top), new PointF (d.Left, d.Bottom), new PointF (d.Right, d.Bottom) };
					inv.Transform (pts);
					float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
					foreach (PointF p in pts) { x0 = Math.Min (x0, p.X); y0 = Math.Min (y0, p.Y); x1 = Math.Max (x1, p.X); y1 = Math.Max (y1, p.Y); }
					return RectangleF.FromLTRB (x0, y0, x1, y1);
				}
				return print_mode || image_target != null ? RecordedVisibleBounds () : new RectangleF (0, 0, 1 << 20, 1 << 20);
			}
		}

		// .NET: { the current clip (an infinite one for none), the current transform } -- this Graphics' own state;
		// it is not a container of a printing Graphics, so there is no outer context to accumulate.
		[EditorBrowsable (EditorBrowsableState.Never)]
		public object GetContextInfo ()
		{
			Region clip = Clip;
			if (clip.IsInfinite (this)) {
				clip.Dispose ();
				clip = new Region ();
			}
			return new object [] { clip, Transform };
		}
	}
}
