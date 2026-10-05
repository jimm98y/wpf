// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a RECORDING-ONLY Graphics (GpuRaster.NewRecording: no GDI+ object behind it) holds as state,
// and what makes one a printer's Graphics.
//
// GDI+ keeps a world transform, a page unit and a page scale. A recording Graphics kept none of
// them: TranslateTransform was turned into a nested scene container and every other transform verb
// returned early, PageUnit read back Display and could not be set, and Transform was a silent
// identity. A window got away with that -- WinForms controls translate and little else -- but a
// printed page is drawn in hundredths of an inch, or millimetres, or points, by code that scales
// and rotates freely. So the transform is held here, in GDI+'s own terms, and the scene is given
// the product of world and page transforms each time either changes (IGpuSceneRecorder
// .SetWorldTransform). That is the whole of it for a window: Pixel and Display are one pixel each,
// so nothing there moves.
//
// A printer's Graphics (print_mode) differs in four ways, all of them GDI+'s own:
//   * its "Display" unit is a hundredth of an inch, which is also the unit the page is recorded in,
//     and its Pixel unit is the printer's dot;
//   * DpiX/DpiY are the printer's, and VisibleClipBounds is the printable area;
//   * it draws for paper, not for a 96 dpi screen: strokes are strokes (with their pen's joins,
//     caps and dashes), curves stay curves, gradients are smooth, text is laid out from the face's
//     design metrics (PrintText) and recorded as glyphs rather than as GDI+'s screen ClearType;
//   * SetClip replaces and ResetClip resets, as GDI+ does -- a window's paint Graphics starts with
//     a clip of the driver's that stands in for the window DC's own and must survive both, a page
//     has none.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace System.Drawing
{
	public sealed partial class Graphics
	{
		// GDI+'s world transform (m11 m12 m21 m22 dx dy), page unit and page scale.
		float [] rec_world = { 1f, 0f, 0f, 1f, 0f, 0f };
		GraphicsUnit rec_unit = GraphicsUnit.Display;
		float rec_page_scale = 1f;
		readonly List<RecState> rec_saved = new List<RecState> ();
		readonly Stack<float []> rec_snapshot_world = new Stack<float []> ();

		/// <summary>This Graphics draws a printed page (see the file comment).</summary>
		internal bool print_mode;
		internal float print_dpi_x = 96f, print_dpi_y = 96f;
		/// <summary>The printable area in hundredths of an inch, in the page's own coordinates.</summary>
		internal RectangleF print_visible = new RectangleF (0, 0, 850, 1100);

		/// <summary>A recording Graphics for a printed page: origin at the printable area's top
		/// left, a hundredth of an inch per unit, the printer's resolution.</summary>
		internal static Graphics NewPrintRecording (float dpiX, float dpiY, RectangleF visible)
		{
			Graphics g = WebGpuBackend.GpuRaster.NewRecording ();
			g.print_mode = true;
			g.print_dpi_x = dpiX > 0 ? dpiX : 600f;
			g.print_dpi_y = dpiY > 0 ? dpiY : 600f;
			g.print_visible = visible;
			return g;
		}

		bool ManagedState => nativeObject == IntPtr.Zero;

		// Recording units per inch: a page's hundredths, a bitmap's own resolution, a screen's 96.
		float BasePerInch => print_mode ? 100f : mf_rec != null ? mf_rec.DpiX : image_target != null ? image_target.HorizontalResolution : 96f;

		// Recording units per page unit.
		float UnitScale (GraphicsUnit u)
		{
			switch (u) {
			case GraphicsUnit.Pixel: return print_mode ? 100f / print_dpi_x : 1f;
			case GraphicsUnit.Point: return BasePerInch / 72f;
			case GraphicsUnit.Inch: return BasePerInch;
			case GraphicsUnit.Document: return BasePerInch / 300f;
			case GraphicsUnit.Millimeter: return BasePerInch / 25.4f;
			default: return 1f;   // Display, World
			}
		}

		float PageFactor => UnitScale (rec_unit) * rec_page_scale;

		float WorldScale ()
		{
			float det = rec_world [0] * rec_world [3] - rec_world [1] * rec_world [2];
			return (float) Math.Sqrt (Math.Abs (det));
		}

		// World, page and container: world-to-recording-units.
		float [] RecordingMatrix ()
		{
			float s = PageFactor;
			float [] w = rec_world;
			var m = new float [] { w [0] * s, w [1] * s, w [2] * s, w [3] * s, w [4] * s, w [5] * s };
			if (rec_in_container) MatMul (m, rec_container, m);
			return m;
		}

		void PushRecordedTransform ()
		{
			if (GpuRecorder == null) return;
			float [] m = RecordingMatrix ();
			GpuRecorder.SetWorldTransform (m [0], m [1], m [2], m [3], m [4], m [5]);
		}

		// r = a * b (a applied first), in GDI+'s operand order (GpMatrix::MultiplyMatrix).
		static void MatMul (float [] a, float [] b, float [] r)
		{
			var ga = new WebGpuBackend.Gdip.GpMatrix (a [0], a [1], a [2], a [3], a [4], a [5]);
			var gb = new WebGpuBackend.Gdip.GpMatrix (b [0], b [1], b [2], b [3], b [4], b [5]);
			WebGpuBackend.Gdip.GpMatrix m = WebGpuBackend.Gdip.GpMatrix.Multiply (ga, gb);
			r [0] = m.M11; r [1] = m.M12; r [2] = m.M21; r [3] = m.M22; r [4] = m.Dx; r [5] = m.Dy;
		}

		void RecordedCombine (float [] m, MatrixOrder order)
		{
			if (order == MatrixOrder.Prepend) MatMul (m, rec_world, rec_world);
			else MatMul (rec_world, m, rec_world);
			PushRecordedTransform ();
		}

		// Everything Save keeps besides the recorder's own clip and transform stack.
		sealed class RecState
		{
			public float [] World, Container;
			public bool InContainer;
			public GraphicsUnit Unit;
			public float Scale;
			public SmoothingMode Smoothing;
			public TextRenderingHint TextHint;
			public int TextContrast;
			public CompositingMode Compositing;
			public CompositingQuality Quality;
			public InterpolationMode Interpolation;
			public PixelOffsetMode PixelOffset;
			public Point Origin;
		}

		RecState CaptureState () => new RecState {
			World = (float []) rec_world.Clone (), Container = (float []) rec_container.Clone (), InContainer = rec_in_container,
			Unit = rec_unit, Scale = rec_page_scale, Smoothing = gpu_smoothing, TextHint = recorded_text_hint,
			TextContrast = recorded_text_contrast, Compositing = _compositingMode, Quality = _compositingQuality,
			Interpolation = _interpolation, PixelOffset = _pixelOffset, Origin = _renderingOrigin,
		};

		void RecordedSave (int token)
		{
			while (rec_saved.Count < token - 1) rec_saved.Add (CaptureState ());
			if (rec_saved.Count >= token) rec_saved.RemoveRange (token - 1, rec_saved.Count - (token - 1));
			rec_saved.Add (CaptureState ());
		}

		void RecordedRestore (int token)
		{
			int k = token - 1;
			if (k < 0 || k >= rec_saved.Count) return;
			RecState st = rec_saved [k];
			rec_saved.RemoveRange (k, rec_saved.Count - k);
			rec_world = st.World; rec_container = st.Container; rec_in_container = st.InContainer;
			rec_unit = st.Unit; rec_page_scale = st.Scale; gpu_smoothing = st.Smoothing;
			recorded_text_hint = st.TextHint; recorded_text_contrast = st.TextContrast;
			_compositingMode = st.Compositing; _compositingQuality = st.Quality;
			_interpolation = st.Interpolation; _pixelOffset = st.PixelOffset; _renderingOrigin = st.Origin;
			GpuRecorder?.SetCompositingMode (_compositingMode == CompositingMode.SourceCopy);
		}

		void RecordedBeginSnapshot () => rec_snapshot_world.Push ((float []) rec_world.Clone ());

		void RecordedEndSnapshot ()
		{
			if (rec_snapshot_world.Count > 0) rec_world = rec_snapshot_world.Pop ();
		}

		// World -> page -> device, as GDI+'s TransformPoints sees the three spaces.
		float [] SpaceMatrix (CoordinateSpace from, CoordinateSpace to)
		{
			float s = PageFactor * (print_mode ? print_dpi_x / 100f : 1f);
			float sy = PageFactor * (print_mode ? print_dpi_y / 100f : 1f);
			float dx = print_mode ? print_dpi_x / 100f : 1f, dy = print_mode ? print_dpi_y / 100f : 1f;
			float [] toDevice (CoordinateSpace c)
			{
				var m = new float [] { 1, 0, 0, 1, 0, 0 };
				if (c == CoordinateSpace.World) m = (float []) rec_world.Clone ();
				if (c != CoordinateSpace.Device) {
					if (!rec_in_container) MatMul (m, new float [] { s, 0, 0, sy, 0, 0 }, m);
					else {
						// world -> recording units through the container, then the device's dots
						float pf = PageFactor;
						MatMul (m, new float [] { pf, 0, 0, pf, 0, 0 }, m);
						MatMul (m, rec_container, m);
						MatMul (m, new float [] { dx, 0, 0, dy, 0, 0 }, m);
					}
				}
				return m;
			}
			float [] a = toDevice (from), b = toDevice (to);
			// a * inverse(b)
			float det = b [0] * b [3] - b [1] * b [2];
			if (det == 0) return a;
			var inv = new float [] { b [3] / det, -b [1] / det, -b [2] / det, b [0] / det,
				(b [2] * b [5] - b [3] * b [4]) / det, (b [1] * b [4] - b [0] * b [5]) / det };
			var r = new float [6];
			MatMul (a, inv, r);
			return r;
		}

		void RecordedTransformPoints (CoordinateSpace dest, CoordinateSpace src, PointF [] pts)
		{
			float [] m = SpaceMatrix (src, dest);
			for (int i = 0; i < pts.Length; i++) {
				float x = pts [i].X, y = pts [i].Y;
				pts [i] = new PointF (x * m [0] + y * m [2] + m [4], x * m [1] + y * m [3] + m [5]);
			}
		}

		// The printable area as the caller sees it: in world units.
		RectangleF RecordedVisibleBounds ()
		{
			// A bitmap's surface is its pixels; a page's, its printable area.
			RectangleF v = image_target != null ? new RectangleF (0, 0, image_target.Width, image_target.Height) : print_visible;
			var pts = new [] { new PointF (v.Left, v.Top), new PointF (v.Right, v.Top), new PointF (v.Left, v.Bottom), new PointF (v.Right, v.Bottom) };
			// Recording units are device / (dpi/100); go page-unit-free through the device space.
			if (image_target == null)
				for (int i = 0; i < 4; i++) pts [i] = new PointF (pts [i].X * print_dpi_x / 100f, pts [i].Y * print_dpi_y / 100f);
			RecordedTransformPoints (CoordinateSpace.World, CoordinateSpace.Device, pts);
			float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
			foreach (PointF p in pts) { x0 = Math.Min (x0, p.X); y0 = Math.Min (y0, p.Y); x1 = Math.Max (x1, p.X); y1 = Math.Max (y1, p.Y); }
			return RectangleF.FromLTRB (x0, y0, x1, y1);
		}

		/// <summary>A font's em in this Graphics' world units, as GDI+ sizes it: points through the
		/// page unit; a World font as it stands; a Pixel font in device pixels.</summary>
		internal float FontEmWorld (Font font)
		{
			switch (font.Unit) {
			case GraphicsUnit.World: return font.Size;
			case GraphicsUnit.Pixel: return font.Size * (print_mode ? 100f / print_dpi_y : 1f) / PageFactor;
			default: return font.SizeInPoints / 72f * BasePerInch / PageFactor;
			}
		}

		// ---- strokes and fills on a page -------------------------------------------------------

		// The thinnest line GDI+ draws on a device is one of its pixels, whatever the pen says.
		float PrintPenWidth (Pen pen)
		{
			float devicePerWorld = PageFactor * WorldScale () * print_dpi_x / 100f;
			float w = pen.Width;
			if (devicePerWorld > 0 && w * devicePerWorld < 1f) w = 1f / devicePerWorld;
			return w;
		}

		internal bool PrintStroke (Pen pen, GraphicsPath path)
		{
			if (!print_mode || GpuRecorder == null) return false;
			if (pen == null) throw new ArgumentNullException ("pen");
			PointF [] pts; byte [] types;
			try { pts = path.PathPoints; types = path.PathTypes; } catch (Exception) { return true; }
			if (pts.Length < 2) return true;
			int cap = pen.StartCap == LineCap.Square || pen.StartCap == LineCap.SquareAnchor ? 1
				: pen.StartCap == LineCap.Round || pen.StartCap == LineCap.RoundAnchor ? 2 : 0;
			int join = pen.LineJoin == LineJoin.Bevel ? 1 : pen.LineJoin == LineJoin.Round ? 2 : 0;
			float [] dash = DashOf (pen);
			GpuRecorder.StrokePathData (ToXY (pts), types, ArgbOf (pen), PrintPenWidth (pen), dash,
				dash != null ? pen.DashOffset : 0f, cap, join, pen.MiterLimit);
			return true;
		}

		bool PrintStroke (Pen pen, Action<GraphicsPath> build)
		{
			if (!print_mode || GpuRecorder == null) return false;
			using (var gp = new GraphicsPath ()) {
				build (gp);
				return PrintStroke (pen, gp);
			}
		}

		internal bool PrintFill (Brush brush, GraphicsPath path)
		{
			if (!print_mode || GpuRecorder == null) return false;
			if (brush == null) throw new ArgumentNullException ("brush");
			PointF [] pts; byte [] types; bool nonZero;
			try { pts = path.PathPoints; types = path.PathTypes; nonZero = path.FillMode == FillMode.Winding; }
			catch (Exception) { return true; }
			if (pts.Length < 3) return true;
			if (PrintRasterFill (brush, path, false)) return true;
			if (TryHatch (brush, out HatchTile ht)) {
				foreach (PointF [] sub in FlattenSubpaths (path))
					if (sub.Length >= 3)
						if (ht.Texture != null) FillTile (GradientShape.Polygon, 0, 0, 0, 0, ToXY (sub), ht);
						else GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, ToXY (sub), ht.Rgba, ht.W, ht.H, ht.Size * 100f / 96f);
				return true;
			}
			if (TryGradient (brush, out GradientDesc gd)) {
				GpuRecorder.FillPathData (ToXY (pts), types, nonZero, 0, gd);
				return true;
			}
			GpuRecorder.FillPathData (ToXY (pts), types, nonZero, ArgbOf (brush), null);
			return true;
		}

		bool PrintFill (Brush brush, Action<GraphicsPath> build, FillMode mode = FillMode.Alternate)
		{
			if (!print_mode || GpuRecorder == null) return false;
			using (var gp = new GraphicsPath (mode)) {
				build (gp);
				return PrintFill (brush, gp);
			}
		}

		// A rectangle: solid as a rectangle, a gradient smooth rather than in screen bands.
		bool PrintFillRect (Brush brush, float x, float y, float w, float h)
		{
			if (!print_mode || GpuRecorder == null) return false;
			if (brush == null) throw new ArgumentNullException ("brush");
			if (w <= 0 || h <= 0) return true;
			if (brush is SolidBrush) { GpuRecorder.FillRect (x, y, w, h, ArgbOf (brush)); return true; }
			if (brush is HatchBrush || brush is PathGradientBrush || brush is LinearGradientBrush)
				using (var rp = new GraphicsPath ()) {
					rp.AddRectangle (new RectangleF (x, y, w, h));
					if (PrintRasterFill (brush, rp, true)) return true;
				}
			if (TryGradient (brush, out GradientDesc gd)) { GpuRecorder.FillShapeGradientSmooth (GradientShape.Rect, x, y, w, h, gd); return true; }
			return PrintFill (brush, gp => gp.AddRectangle (new RectangleF (x, y, w, h)));
		}

		// DrawImage at a point draws an image at its PHYSICAL size: its pixels at its own resolution.
		SizeF PrintImageSize (Image image)
		{
			float hr = 96f, vr = 96f;
			try { hr = image.HorizontalResolution; vr = image.VerticalResolution; } catch (Exception) { }
			if (!(hr > 0)) hr = 96f;
			if (!(vr > 0)) vr = 96f;
			return new SizeF (image.Width / hr * BasePerInch / PageFactor, image.Height / vr * BasePerInch / PageFactor);
		}

		bool PrintClip (GraphicsPath path, CombineMode mode)
		{
			if (GpuRecorder == null) return false;
			PointF [] pts; byte [] types;
			try { pts = path.PathPoints; types = path.PathTypes; } catch (Exception) { return true; }
			if (print_mode && mode == CombineMode.Replace) GpuRecorder.ResetAllClips ();
			GpuRecorder.SetClipPath (ToXY (pts), types, path.FillMode == FillMode.Winding, mode == CombineMode.Exclude);
			return true;
		}

		// ---- what GDI+'s printer driver rasterizes ---------------------------------------------
		//
		// GDI+ on a printer DC (DriverPrint, gdiplus.dll 10.0.26100 arm64) puts down as GDI vectors
		// only what GDI can say: solid fills and strokes, text, upright images. Everything else it
		// RASTERIZES itself and hands the printer a bitmap clipped to the shape -- and the page looks
		// the way it does because of the resolution it picks and the way it cuts the bitmap up. Each
		// is done here, at record time, so the GDI device and the PDF writer put down the same thing:
		//
		//   DriverPrint::PrivateFillRect @1800cf510, a path gradient (DpBrush type 3): the device
		//     bounds of the fill, w x h, rendered by GpBitmap::CreateBitmapAndFillWithBrush into a
		//     bitmap of min(w, (w - 256) / 5 + 256) capped at 1024 (min(w, 256) when the brush's path
		//     is a rectangle) mapped onto those bounds, locked as 32bpp ARGB and handed to
		//     ConvertBitmapToGdi with no transparency: what lies outside the gradient's own path is
		//     transparent BLACK, and prints black. StretchBlt'd over the bounds inside a path clip
		//     (SetupPathClipping).
		//   the same, any other non-GDI brush (a hatch here): banded. The device bounds snapped to a
		//     grid of s = (int)(dpi / 100) device pixels (100 dpi at 600), a DIB of one pixel per
		//     cell filled by DpDriver::FillRects with the brush -- a hatch keeps one pattern bit per
		//     DIB pixel, aligned to the device origin -- and each band StretchDIBits'd s times over
		//     inside the shape's clip.
		//   DriverPrint::DrawImage @1800ccc30: an image whose device parallelogram is an upright,
		//     unflipped rectangle goes down as StretchBlt of its pixels. Anything else is drawn
		//     (DpDriver::DrawImage, the context's interpolation) into a DIB of s device pixels per
		//     DIB pixel, s = round(device length of one source pixel), capped at round(dpi / 100)
		//     when the image is under 200 x 200 and turned or sheared, then banded and clipped to
		//     the parallelogram.
		//   SetupPrintBanding @1800d1af0: ceil(w * h * 4 / 128000) bands of ceil(h / bands) rows.
		//
		// A translucent brush or image is flattened onto the paper by the device, as everything is.

		static readonly bool s_noPrintRaster = Environment.GetEnvironmentVariable ("WF_PRINT_RASTER") == "0";

		// World to printer device pixels.
		float [] PrintDeviceMatrix ()
		{
			float [] m = RecordingMatrix ();
			float kx = print_dpi_x / 100f, ky = print_dpi_y / 100f;
			return new [] { m [0] * kx, m [1] * ky, m [2] * kx, m [3] * ky, m [4] * kx, m [5] * ky };
		}

		static PointF Apply (float [] m, float x, float y) => new PointF (x * m [0] + y * m [2] + m [4], x * m [1] + y * m [3] + m [5]);

		static float [] Invert (float [] m)
		{
			float det = m [0] * m [3] - m [1] * m [2];
			if (det == 0f || float.IsNaN (det)) return null;
			return new [] { m [3] / det, -m [1] / det, -m [2] / det, m [0] / det,
				(m [2] * m [5] - m [3] * m [4]) / det, (m [1] * m [4] - m [0] * m [5]) / det };
		}

		// a then b.
		static float [] Then (float [] a, float [] b)
		{
			var r = new float [6];
			r [0] = a [0] * b [0] + a [1] * b [2]; r [1] = a [0] * b [1] + a [1] * b [3];
			r [2] = a [2] * b [0] + a [3] * b [2]; r [3] = a [2] * b [1] + a [3] * b [3];
			r [4] = a [4] * b [0] + a [5] * b [2] + b [4]; r [5] = a [4] * b [1] + a [5] * b [3] + b [5];
			return r;
		}

		bool PrintRasterFill (Brush brush, GraphicsPath path, bool rectFill)
		{
			if (s_noPrintRaster || !(brush is HatchBrush || brush is PathGradientBrush || brush is LinearGradientBrush)) return false;
			PointF [] pts; byte [] types;
			try { pts = path.PathPoints; types = path.PathTypes; } catch (Exception) { return false; }
			if (pts.Length == 0) return true;
			float [] dev = PrintDeviceMatrix ();
			if (brush is LinearGradientBrush lgb && !LinearGradientIsBitmap (lgb, dev)) return false;
			float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
			foreach (PointF p in pts) {
				PointF d = Apply (dev, p.X, p.Y);
				x0 = Math.Min (x0, d.X); y0 = Math.Min (y0, d.Y); x1 = Math.Max (x1, d.X); y1 = Math.Max (y1, d.Y);
			}
			if (!(x1 >= x0) || !(y1 >= y0)) return true;
			bool nonZero = path.FillMode == FillMode.Winding;
			if (brush is LinearGradientBrush lg) {
				// A rectangle-gradient bitmap: at most 256 on a side, over the fill's device bounds.
				DeviceBounds (x0, y0, x1, y1, rectFill, out int lx, out int ly, out int lw, out int lh);
				int LW = Math.Max (1, Math.Min (lw, 256)), LH = Math.Max (1, Math.Min (lh, 256));
				float [] toLinear = Then (dev, new [] { LW / (float) lw, 0f, 0f, LH / (float) lh, -lx * LW / (float) lw, -ly * LH / (float) lh });
				byte [] lpx = FillWithBrush (brush, LW, LH, toLinear, Point.Empty);
				if (lpx == null) return false;
				if (LinearGradientIsOpaque (lg)) Opaque (lpx);
				EmitDeviceImage (lpx, LW, LH, new RectangleF (lx, ly, lw, lh), path, nonZero);
				return true;
			}
			if (brush is PathGradientBrush pg) {
				DeviceBounds (x0, y0, x1, y1, rectFill, out int bx, out int by, out int bw, out int bh);
				bool rect = PathGradientIsRectangular (pg);
				int W = GradientBitmapSide (bw, rect), H = GradientBitmapSide (bh, rect);
				// World -> bitmap: world -> device, then the device bounds onto the bitmap.
				float [] toBitmap = Then (dev, new [] { W / (float) bw, 0f, 0f, H / (float) bh, -bx * W / (float) bw, -by * H / (float) bh });
				// On a clone with +0x1b8 = 1.05: the surround colour carried 5% past the rim.
				byte [] rgba;
				using (var inflated = (PathGradientBrush) pg.Clone ()) {
					inflated.Inflate = 1.05f;
					rgba = FillWithBrush (inflated, W, H, toBitmap, Point.Empty);
				}
				if (rgba == null) return false;
				if (PathGradientIsOpaque (pg)) Opaque (rgba);
				EmitDeviceImage (rgba, W, H, new RectangleF (bx, by, bw, bh), path, nonZero);
				return true;
			}
			// Banded: the visible bounds on a grid of s.
			int s = Math.Max (1, (int) (print_dpi_x / 100f)), t = Math.Max (1, (int) (print_dpi_y / 100f));
			DeviceBounds (x0, y0, x1, y1, rectFill, out int vx, out int vy, out int vw, out int vh);
			int gx = FloorDiv (vx, s), gy = FloorDiv (vy, t);
			int gw = 1 + (vx - gx * s + vw) / s, gh = 1 + (vy - gy * t + vh) / t;
			if (gw <= 0 || gh <= 0 || (long) gw * gh > 64L << 20) return false;
			int rows = BandRows (gw, gh);
			// The pattern from the device origin: DIB pixel (i, j) is cell (gx + i, gy + j). The last
			// band's rows past the fill are left as the DIB was made: black.
			byte [] fill = FillWithBrush (brush, gw, gh, null, new Point (-gx, -gy));
			if (fill == null) return false;
			if (brush is HatchBrush hb && hb.ForegroundColor.A == 255 && hb.BackgroundColor.A == 255) Opaque (fill);
			int banded = (gh + rows - 1) / rows * rows;
			var px = new byte [gw * banded * 4];
			Buffer.BlockCopy (fill, 0, px, 0, fill.Length);
			for (int i = fill.Length + 3; i < px.Length; i += 4) px [i] = 255;
			EmitBands (px, gw, banded, rows, gx, gy, s, t, path, nonZero);
			return true;
		}

		// The fill's device bounds as DriverPrint is handed them: a path's corners out to whole
		// pixels, inclusive; a rectangle's two pixels further out on every side (measured: a hatch
		// or path gradient rectangle on Microsoft Print to PDF lands on exactly that grid).
		static void DeviceBounds (float x0, float y0, float x1, float y1, bool rect, out int x, out int y, out int w, out int h)
		{
			int k = rect ? 2 : 0;
			x = (int) Math.Floor (x0) - k; y = (int) Math.Floor (y0) - k;
			w = (int) Math.Ceiling (x1) + (rect ? 2 : 1) - x; h = (int) Math.Ceiling (y1) + (rect ? 2 : 1) - y;
		}

		static int FloorDiv (int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

		static int GradientBitmapSide (int side, bool rect)
		{
			if (rect) return Math.Max (1, Math.Min (side, 256));
			int f = (side - 256) / 5 + 256;
			int n = Math.Min (f, side);
			return Math.Max (1, n < 1024 ? n : 1024);
		}

		// PrivateFillRect's test of the brush's own outline: no edge that runs both across and down.
		static bool PathGradientIsRectangular (PathGradientBrush pg)
		{
			PointF [] p = pg.GpPoints;
			int n = pg.PointCount;
			if (p == null || n < 1) return false;
			if (pg.GpPath != null && n != 4 && !(n == 5 && p [4] == p [0])) return false;
			for (int i = 0; i < n; i++) {
				PointF a = p [i], b = p [(i + 1) % n];
				if (Math.Abs (a.X - b.X) > 1.1920929e-07f && Math.Abs (a.Y - b.Y) > 1.1920929e-07f) return false;
			}
			return true;
		}

		// A linear gradient whose colour runs neither across nor down the device: what
		// PrivateFillRect renders as a bitmap. Straight gradients GDI+ puts down with GDI
		// (PrivateFillGradient); they stay vectors here.
		static bool LinearGradientIsBitmap (LinearGradientBrush lg, float [] dev)
		{
			WebGpuBackend.Gdip.GpMatrix x = lg.Xform;
			float m11 = x.M11 * dev [0] + x.M12 * dev [2], m12 = x.M11 * dev [1] + x.M12 * dev [3];
			float m21 = x.M21 * dev [0] + x.M22 * dev [2], m22 = x.M21 * dev [1] + x.M22 * dev [3];
			float tol = 1e-4f * Math.Max (Math.Max (Math.Abs (m11), Math.Abs (m12)), Math.Max (Math.Abs (m21), Math.Abs (m22)));
			// Horizontal or vertical: GDI's gradient fill. Any other angle: the bitmap (measured: a
			// 30 degree brush, scalable or not, prints as 256 x 256).
			return Math.Abs (m21) > tol && Math.Abs (m22) > tol;
		}

		static bool LinearGradientIsOpaque (LinearGradientBrush lg)
		{
			try {
				if (lg.InterpolationColorsWereSet) {
					foreach (Color c in lg.InterpolationColors.Colors) if (c.A != 255) return false;
					return true;
				}
				return lg.LinearColors [0].A == 255 && lg.LinearColors [1].A == 255;
			} catch (Exception) { return false; }
		}

		static bool PathGradientIsOpaque (PathGradientBrush pg)
		{
			if (((uint) pg.CenterArgb >> 24) != 255) return false;
			int [] s = pg.SurroundArgb;
			if (s != null) foreach (int c in s) if (((uint) c >> 24) != 255) return false;
			if (pg.PresetSet && pg.StoredPresetArgb is int [] pr) foreach (int c in pr) if (((uint) c >> 24) != 255) return false;
			return true;
		}

		// ConvertBitmapToGdi of an opaque brush: the colour channels as they are, the alpha dropped.
		static void Opaque (byte [] rgba)
		{
			for (int i = 3; i < rgba.Length; i += 4) rgba [i] = 255;
		}

		// GpBitmap::CreateBitmapAndFillWithBrush: a W x H 32bpp ARGB bitmap, transparent, the brush
		// over all of it through the world-to-bitmap matrix (null: identity).
		byte [] FillWithBrush (Brush brush, int W, int H, float [] toBitmap, Point origin)
		{
			using (var bmp = new Bitmap (W, H, Imaging.PixelFormat.Format32bppArgb))
			using (Graphics g = FromImage (bmp)) {
				if (g.gp == null) return null;
				g.InterpolationMode = _interpolation == InterpolationMode.Invalid ? InterpolationMode.Bilinear : _interpolation;
				g.PixelOffsetMode = _pixelOffset;
				g.RenderingOrigin = origin;
				PointF [] quad = { new PointF (-1, -1), new PointF (W + 1, -1), new PointF (W + 1, H + 1), new PointF (-1, H + 1) };
				if (toBitmap != null) {
					float [] inv = Invert (toBitmap);
					if (inv == null) return null;
					for (int i = 0; i < 4; i++) quad [i] = Apply (inv, quad [i].X, quad [i].Y);
					g.Transform = new Matrix (toBitmap [0], toBitmap [1], toBitmap [2], toBitmap [3], toBitmap [4], toBitmap [5]);
				}
				g.FillPolygon (brush, quad);
				g.Flush ();
				return GdipPixels.ToRgba (bmp.Data.Frame, new Rectangle (0, 0, W, H));
			}
		}

		// The bitmap over a device rectangle, inside the shape (a world path).
		void EmitDeviceImage (byte [] rgba, int w, int h, RectangleF device, GraphicsPath clip, bool nonZero)
		{
			int st = BeginDeviceDraw (clip, nonZero);
			float kx = 100f / print_dpi_x, ky = 100f / print_dpi_y;
			GpuRecorder.DrawImage (rgba, w, h, device.X * kx, device.Y * ky, device.Width * kx, device.Height * ky);
			EndDeviceDraw (st);
		}

		// SetupPrintBanding: ceil(w * h * 4 / 128000) bands of ceil(h / bands) rows, the last as
		// tall as the others (it runs on past the shape; the clip keeps it in).
		static int BandRows (int w, int h)
		{
			int bands = Math.Max (1, (int) Math.Ceiling (w * (float) h * 4f / 128000f));
			return Math.Max (1, (int) Math.Ceiling (h / (float) bands));
		}

		// A DIB at (gx, gy) on a grid of s x t device pixels, put down band by band inside the shape;
		// h is a whole number of bands.
		void EmitBands (byte [] rgba, int w, int h, int rows, int gx, int gy, int s, int t, GraphicsPath clip, bool nonZero)
		{
			int st = BeginDeviceDraw (clip, nonZero);
			float kx = 100f / print_dpi_x, ky = 100f / print_dpi_y;
			for (int top = 0; top < h; top += rows) {
				int n = Math.Min (rows, h - top);
				var band = new byte [w * n * 4];
				Buffer.BlockCopy (rgba, top * w * 4, band, 0, band.Length);
				GpuRecorder.DrawImage (band, w, n, gx * s * kx, (gy + top) * t * ky, w * s * kx, n * t * ky);
			}
			EndDeviceDraw (st);
		}

		// The clip set in world units, then the transform taken to the recording's own units (the
		// page's hundredths of an inch), where a device rectangle is device pixels times 100 / dpi.
		int BeginDeviceDraw (GraphicsPath clip, bool nonZero)
		{
			int st = GpuRecorder.SaveState ();
			if (clip != null) {
				try { GpuRecorder.SetClipPath (ToXY (clip.PathPoints), clip.PathTypes, nonZero, false); } catch (Exception) { }
			}
			GpuRecorder.SetWorldTransform (1f, 0f, 0f, 1f, 0f, 0f);
			return st;
		}

		void EndDeviceDraw (int st)
		{
			GpuRecorder.RestoreState (st);
			PushRecordedTransform ();
		}

		/// <summary>DriverPrint::DrawImage for a parallelogram that is not an upright rectangle on
		/// the device: drawn into a DIB, banded, clipped (see above). False where GDI+ blits the
		/// image's own pixels.</summary>
		bool PrintImageBands (Bitmap bmp, RectangleF src, RectangleF dest, Imaging.ImageAttributes attrs)
		{
			if (s_noPrintRaster || GpuRecorder == null) return false;
			float [] dev = PrintDeviceMatrix ();
			PointF p0 = Apply (dev, dest.X, dest.Y), p1 = Apply (dev, dest.Right, dest.Y), p2 = Apply (dev, dest.X, dest.Bottom);
			bool turned = Math.Abs (dev [1]) > 1e-6f * Math.Abs (dev [0]) || Math.Abs (dev [2]) > 1e-6f * Math.Abs (dev [3]);
			if (!turned && p0.X < p1.X && p0.Y < p2.Y) return false;
			// Device pixels per source pixel, rounded; held to 100 dpi for a small turned image.
			int s = (int) (Hypot (p1.X - p0.X, p1.Y - p0.Y) / src.Width + 0.5f);
			int t = (int) (Hypot (p2.X - p0.X, p2.Y - p0.Y) / src.Height + 0.5f);
			s = Math.Max (1, s); t = Math.Max (1, t);
			if (s > 5 && t > 5 && bmp.Width < 200 && bmp.Height < 200 && turned) {
				s = Math.Min (s, (int) (print_dpi_x / 100f + 0.5f));
				t = Math.Min (t, (int) (print_dpi_y / 100f + 0.5f));
			}
			// The parallelogram in DIB units, its bounds to whole pixels (28.4, centres in).
			PointF q0 = new PointF (p0.X / s, p0.Y / t), q1 = new PointF (p1.X / s, p1.Y / t), q2 = new PointF (p2.X / s, p2.Y / t);
			var q3 = new PointF (q1.X + q2.X - q0.X, q1.Y + q2.Y - q0.Y);
			int Ceil16 (float v) => ((int) (v * 16f + 0.5f) + 15) >> 4;
			float mnx = Math.Min (Math.Min (q0.X, q1.X), Math.Min (q2.X, q3.X)), mxx = Math.Max (Math.Max (q0.X, q1.X), Math.Max (q2.X, q3.X));
			float mny = Math.Min (Math.Min (q0.Y, q1.Y), Math.Min (q2.Y, q3.Y)), mxy = Math.Max (Math.Max (q0.Y, q1.Y), Math.Max (q2.Y, q3.Y));
			int gx = Ceil16 (mnx), gy = Ceil16 (mny), gw = Ceil16 (mxx) - gx, gh = Ceil16 (mxy) - gy;
			if (gw < 1 || gh < 1) return true;
			if ((long) gw * gh > 64L << 20) return false;
			int rows = BandRows (gw, gh);
			gh = (gh + rows - 1) / rows * rows;
			byte [] px;
			using (var dib = new Bitmap (gw, gh, Imaging.PixelFormat.Format32bppArgb))
			using (Graphics g = FromImage (dib)) {
				if (g.gp == null) return false;
				g.InterpolationMode = _interpolation == InterpolationMode.Invalid ? InterpolationMode.Bilinear : _interpolation;
				g.PixelOffsetMode = _pixelOffset;
				// A mirror lands one DIB pixel further along the mirrored axis than the engine puts
				// it (measured: a 64 px image flipped at 12 device px per pixel, every column one on).
				float fx = !turned && p1.X < p0.X ? 1f : 0f, fy = !turned && p2.Y < p0.Y ? 1f : 0f;
				var at = new [] { new PointF (q0.X - gx + fx, q0.Y - gy + fy), new PointF (q1.X - gx + fx, q1.Y - gy + fy), new PointF (q2.X - gx + fx, q2.Y - gy + fy) };
				g.DrawImage (bmp, at, src, GraphicsUnit.Pixel, attrs);
				g.Flush ();
				px = GdipPixels.ToRgba (dib.Data.Frame, new Rectangle (0, 0, gw, gh));
			}
			if (attrs == null && OpaquePixels (bmp, src)) ExtendRows (px, gw, gh);
			using (var clip = new GraphicsPath ()) {
				clip.AddPolygon (new [] { new PointF (dest.X, dest.Y), new PointF (dest.Right, dest.Y),
					new PointF (dest.Right, dest.Bottom), new PointF (dest.X, dest.Bottom) });
				EmitBands (px, gw, gh, rows, gx, gy, s, t, clip, false);
			}
			return true;
		}

		// What the band holds where the image is not: stock's DIB has no rim -- every pixel the
		// image touches is the image's colour whole, and each row runs on past the image's span in
		// the colour of its end pixels (measured on the bits gdiplus.dll hands StretchDIBits). The
		// rim the engine antialiased is therefore made opaque and the rows extended; the clip cuts
		// the parallelogram back out of it.
		static void ExtendRows (byte [] px, int w, int h)
		{
			int lastRow = -1;
			for (int y = 0; y < h; y++) {
				int row = y * w * 4, first = -1, last = -1;
				for (int x = 0; x < w; x++)
					if (px [row + x * 4 + 3] != 0) { if (first < 0) first = x; last = x; }
				if (first < 0) {
					if (lastRow >= 0) Buffer.BlockCopy (px, lastRow * w * 4, px, row, w * 4);
					continue;
				}
				for (int x = first; x <= last; x++) px [row + x * 4 + 3] = 255;
				for (int x = 0; x < first; x++) Buffer.BlockCopy (px, row + first * 4, px, row + x * 4, 4);
				for (int x = last + 1; x < w; x++) Buffer.BlockCopy (px, row + last * 4, px, row + x * 4, 4);
				if (lastRow < 0)
					for (int k = 0; k < y; k++) Buffer.BlockCopy (px, row, px, k * w * 4, w * 4);
				lastRow = y;
			}
		}

		static bool OpaquePixels (Bitmap bmp, RectangleF src)
		{
			if ((bmp.PixelFormat & Imaging.PixelFormat.Alpha) == 0 && (bmp.PixelFormat & Imaging.PixelFormat.PAlpha) == 0) return true;
			var r = Rectangle.Intersect (Rectangle.FromLTRB ((int) Math.Floor (src.Left), (int) Math.Floor (src.Top), (int) Math.Ceiling (src.Right), (int) Math.Ceiling (src.Bottom)),
				new Rectangle (0, 0, bmp.Width, bmp.Height));
			if (r.Width <= 0 || r.Height <= 0) return true;
			byte [] p = GdipPixels.ToRgba (bmp.Data.Frame, r);
			for (int i = 3; i < p.Length; i += 4) if (p [i] != 255) return false;
			return true;
		}

		static float Hypot (float x, float y) => (float) Math.Sqrt (x * x + y * y);

		// ---- text on a page --------------------------------------------------------------------

		static int StyleOf (Font font) => (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);

		WebGpuBackend.PrintTextLayout PrintLayout (string s, Font font, RectangleF rect, StringFormat format)
		{
			int flags = 0, align = 0, lineAlign = 0, hotkey = 0;
			bool typographic = false;
			float tab = 0f;
			if (format != null) {
				flags = (int) format.FormatFlags;
				typographic = format.IsTypographic;
				align = (int) format.Alignment;
				lineAlign = (int) format.LineAlignment;
				hotkey = format.HotkeyPrefix == HotkeyPrefix.Show ? 1 : format.HotkeyPrefix == HotkeyPrefix.Hide ? 2 : 0;
				try {
					float [] stops = format.GetTabStops (out float first);
					if (stops != null && stops.Length > 0 && stops [0] > 0) tab = stops [0];
				} catch (Exception) { }
			}
			return WebGpuBackend.PrintText.Layout (s, font.FontFamily?.Name, StyleOf (font), FontEmWorld (font),
				rect.X, rect.Y, rect.Width, rect.Height, flags, typographic, align, lineAlign, hotkey, tab);
		}

		// The realization GDI+ lays printer text out for: grid-fitted, GDI-classic advances (what
		// AntiAliasGridFit takes). Measured against stock GDI+ printing to Microsoft Print to PDF,
		// it puts every glyph within 0.22pt of where stock does; the ClearType (natural) advances
		// the screen uses drift to 0.55pt over a line.
		const int PrintHint = 3;

		// The world-plus-page transform as GDI+ applies it, when it is an upright scale and offset:
		// what lets a string be laid out in DEVICE pixels and the result brought back.
		bool UprightTransform (out float sx, out float sy, out float tx, out float ty)
		{
			float s = PageFactor;
			sx = rec_world [0] * s; sy = rec_world [3] * s; tx = rec_world [4] * s; ty = rec_world [5] * s;
			return rec_world [1] == 0 && rec_world [2] == 0 && sx > 0 && sy > 0 && Math.Abs (sx - sy) <= 1e-4f * sx;
		}

		/// <summary>A single-line string as GDI+'s fast imager lays it out on the PRINTER: its own
		/// algorithm (GdiPlusText.Layout) run at the device's resolution, where GDI+ itself runs it --
		/// hinted advances at the device em, the default format's tracking and margins, the hinting
		/// growth spread so the line keeps its nominal width -- and the glyphs recorded where it puts
		/// them. False where GDI+ would take its full imager (wrapping, tabs, line breaks, complex
		/// scripts); those are laid out by PrintText.</summary>
		bool TryPrintGdiPlusText (string s, Font font, int argb, RectangleF rect, StringFormat format)
		{
			if (font.Underline || font.Strikeout || !UprightTransform (out float sx, out float sy, out float tx, out float ty))
				return false;
			string family = font.FontFamily?.Name;
			int style = StyleOf (font);
			var face = WebGpuBackend.PrintText.Face (family, style);
			if (face == null) return false;
			int flags = 0, align = 0, lineAlign = 0;
			bool typographic = false, hotkey = false;
			if (format != null) {
				if (format.TabStopCount > 0) return false;
				flags = (int) format.FormatFlags;
				typographic = format.IsTypographic;
				if (typographic) flags |= 0x6004;
				align = (int) format.Alignment;
				lineAlign = (int) format.LineAlignment;
				hotkey = format.HotkeyPrefix != HotkeyPrefix.None;
			}
			float dx = print_dpi_x / 100f;
			float dev = sx * dx;   // device pixels per world unit
			float emDevice = FontEmWorld (font) * dev;
			var run = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Layout (face, family, emDevice * 72f / print_dpi_x, s,
				(rect.X * sx + tx) * dx, (rect.Y * sy + ty) * dx, rect.Width * dev, rect.Height * dev,
				flags, typographic, align, lineAlign, hotkey, PrintHint, print_dpi_x);
			if (run == null) return false;
			if (run.Glyphs.Length == 0) return true;
			float [] xs = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.GlyphXs (run, run.OriginX);
			// Back from device pixels to world units.
			float World (float d, float t) => (d / dx - t) / sx;
			float ox = World (xs [0], tx), oy = World (run.OriginY, ty);
			var rel = new float [xs.Length];
			for (int i = 0; i < xs.Length; i++) rel [i] = World (xs [i], tx) - ox;
			// The glyphs are the string's characters less its leading and trailing spaces.
			int lead = 0;
			while (lead < s.Length && s [lead] == ' ') lead++;
			string chars = s.Substring (lead, Math.Min (run.Glyphs.Length, s.Length - lead));
			bool clip = run.HasClip;
			if (clip) GpuRecorder.SetClipRect (World (run.ClipX, tx), World (run.ClipY, ty), run.ClipW / dev, run.ClipH / dev, false);
			GpuRecorder.DrawGlyphs (face, run.Em / dev, run.Glyphs, rel, new float [rel.Length], ox, oy, argb, family, style, chars, null);
			if (clip) GpuRecorder.ClearClip ();
			return true;
		}

		static readonly bool s_noPrintFullText = Environment.GetEnvironmentVariable ("WF_PRINT_FTI") == "0";

		/// <summary>A string the fast imager will not take, as GDI+ lays it out for a printer: its
		/// FullTextImager (the shared port, GpFullTextImager) with the printer as the device -- the
		/// lines broken, aligned and placed by Line Services from advances hinted at the device em,
		/// a centred line's trailing spaces hanging past its end rather than counted in its width --
		/// and each placed run recorded as glyphs where the imager put them.</summary>
		bool TryPrintFullText (string s, Font font, int argb, RectangleF rect, StringFormat format)
		{
			if (s_noPrintFullText) return false;
			string family = font.FontFamily?.Name;
			if (string.IsNullOrEmpty (family)) return false;
			float em = FontEmWorld (font);
			if (!(em > 0f) || rect.Width < 0f || rect.Height < 0f) return false;
			WebGpuBackend.Gdip.GpFullTextImager fti;
			try {
				fti = new WebGpuBackend.Gdip.GpFullTextImager (s, rect.Width, rect.Height, family, (int) font.Style, em,
					WebGpuBackend.Gdip.GpTextFormat.From (format));
			} catch (Exception) { return false; }
			if (!fti.Valid) return false;
			float [] dev = PrintDeviceMatrix ();
			var target = new PrintTextTarget (this, dev, argb);
			if (target.Inverse == null) return false;
			fti.Draw (target, rect.Location);
			return true;
		}

		/// <summary>The full imager's target on a page: the printer is the device (world to device
		/// pixels at its dpi, the realization the printed fast imager takes), each run's glyphs
		/// recorded at their world origins, an underline as the rule it is, the layout rectangle
		/// into the clip.</summary>
		sealed class PrintTextTarget : WebGpuBackend.Gdip.IGpTextTarget
		{
			readonly Graphics _g; readonly float [] _dev; readonly int _argb;
			internal readonly float [] Inverse;

			public PrintTextTarget (Graphics g, float [] dev, int argb)
			{
				_g = g; _dev = dev; _argb = argb; Inverse = Invert (dev);
			}

			public WebGpuBackend.Gdip.GpMatrix? WorldToDevice
				=> new WebGpuBackend.Gdip.GpMatrix (_dev [0], _dev [1], _dev [2], _dev [3], _dev [4], _dev [5]);

			public int RealizationMode (Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont face, string family, float emDevice, bool square)
				=> PrintHint;

			public void DrawPlacedGlyphs (WebGpuBackend.Gdip.GpFullTextImager.Run run, int mode, ushort [] glyphs, PointF [] o,
				string chars, ushort [] map, int flags)
			{
				if (glyphs.Length == 0) return;
				int n = glyphs.Length;
				var xs = new float [n]; var ys = new float [n];
				PointF w0 = Apply (Inverse, o [0].X, o [0].Y);
				for (int i = 0; i < n; i++) {
					PointF w = Apply (Inverse, o [i].X, o [i].Y);
					xs [i] = w.X - w0.X; ys [i] = w.Y - w0.Y;
				}
				// Per glyph, the first character of its cluster (the recorder's cluster map).
				int [] clusters = null;
				if (chars != null && map != null && map.Length == chars.Length) {
					clusters = new int [n];
					for (int g = 0; g < n; g++) clusters [g] = -1;
					for (int k = map.Length - 1; k >= 0; k--) if (map [k] < n) clusters [map [k]] = k;
					int last = 0;
					for (int g = 0; g < n; g++) { if (clusters [g] < 0) clusters [g] = last; last = clusters [g]; }
				}
				_g.GpuRecorder.DrawGlyphs (run.Face, run.Em, glyphs, xs, ys, w0.X, w0.Y, _argb, run.Family, run.Style & 3, chars, clusters);
			}

			public void DrawLine (float devicePenWidth, PointF a, PointF b)
			{
				// GdipLscbkDrawUnderline's line, a pen of devicePenWidth device pixels centred on it.
				float scale = (float) Math.Sqrt (Math.Abs (_dev [0] * _dev [3] - _dev [1] * _dev [2]));
				float th = scale > 0 ? devicePenWidth / scale : 0f;
				if (a.Y == b.Y) _g.GpuRecorder.FillRect (Math.Min (a.X, b.X), a.Y - th / 2f, Math.Abs (b.X - a.X), th, _argb);
				else if (a.X == b.X) _g.GpuRecorder.FillRect (a.X - th / 2f, Math.Min (a.Y, b.Y), th, Math.Abs (b.Y - a.Y), _argb);
			}

			public object PushClip (RectangleF layout)
			{
				_g.GpuRecorder.SetClipRect (layout.X, layout.Y, layout.Width, layout.Height, false);
				return layout;
			}

			public void PopClip (object saved) => _g.GpuRecorder.ClearClip ();
		}

		internal bool PrintDrawString (string s, Font font, Brush brush, RectangleF rect, StringFormat format)
		{
			if (!print_mode || GpuRecorder == null) return false;
			if (string.IsNullOrEmpty (s)) return true;
			int argbFast = ArgbOf (brush);
			if (brush is Drawing2D.LinearGradientBrush lgf) { lgf.GetGpuGradient (out _, out _, out Color c1f, out _); argbFast = c1f.ToArgb (); }
			if (TryPrintGdiPlusText (s, font, argbFast, rect, format)) return true;
			if (TryPrintFullText (s, font, argbFast, rect, format)) return true;
			WebGpuBackend.PrintTextLayout layout = PrintLayout (s, font, rect, format);
			if (layout.Face == null) return true;
			int argb = ArgbOf (brush);
			if (brush is Drawing2D.LinearGradientBrush lg) { lg.GetGpuGradient (out _, out _, out Color c1, out _); argb = c1.ToArgb (); }
			bool clip = rect.Width > 0 && rect.Height > 0
				&& (format == null || (format.FormatFlags & StringFormatFlags.NoClip) == 0);
			if (clip) GpuRecorder.SetClipRect (rect.X, rect.Y, rect.Width, rect.Height, false);
			int style = StyleOf (font);
			WebGpuBackend.FaceMetrics fm = layout.Metrics;
			float unit = layout.Em / fm.UnitsPerEm;
			foreach (WebGpuBackend.PrintTextLayout.Line line in layout.Lines) {
				foreach (WebGpuBackend.PrintTextLayout.Run run in line.Runs)
					GpuRecorder.DrawGlyphs (run.Face, layout.Em, run.Glyphs, run.Xs, new float [run.Glyphs.Length],
						line.X, line.Baseline, argb, run.Family, style, run.Chars, run.Clusters);
				// Underline and strike-out are rules GDI+ draws from the face's own metrics.
				if (font.Underline && line.Width > 0)
					GpuRecorder.FillRect (line.X, line.Baseline - fm.UnderlinePosition * unit - fm.UnderlineThickness * unit / 2f,
						line.Width, Math.Max (fm.UnderlineThickness * unit, 0.01f), argb);
				if (font.Strikeout && line.Width > 0)
					GpuRecorder.FillRect (line.X, line.Baseline - fm.StrikeoutPosition * unit - fm.StrikeoutSize * unit / 2f,
						line.Width, Math.Max (fm.StrikeoutSize * unit, 0.01f), argb);
				if (!float.IsNaN (line.MnemonicX))
					GpuRecorder.FillRect (line.X + line.MnemonicX, line.Baseline - fm.UnderlinePosition * unit - fm.UnderlineThickness * unit / 2f,
						line.MnemonicW, Math.Max (fm.UnderlineThickness * unit, 0.01f), argb);
			}
			if (clip) GpuRecorder.ClearClip ();
			return true;
		}

		SizeF PrintMeasureString (string s, Font font, RectangleF rect, StringFormat format, out int fitted, out int lines)
		{
			WebGpuBackend.PrintTextLayout layout = PrintLayout (s, font, new RectangleF (0, 0, rect.Width, rect.Height), format);
			fitted = layout.CharactersFitted;
			lines = layout.Lines.Count;
			return new SizeF (layout.Width, layout.Height);
		}

		/// <summary>Font.GetHeight on this Graphics: the face's line spacing, in world units.</summary>
		internal bool TryPrintFontHeight (Font font, out float height)
		{
			height = 0f;
			if (!print_mode) return false;
			if (!WebGpuBackend.PrintText.Metrics (font.FontFamily?.Name, StyleOf (font), out WebGpuBackend.FaceMetrics fm)) return false;
			height = fm.LineSpacing * FontEmWorld (font) / fm.UnitsPerEm;
			return true;
		}
	}
}
