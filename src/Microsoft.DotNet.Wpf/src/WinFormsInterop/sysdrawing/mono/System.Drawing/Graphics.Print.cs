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
		readonly List<(float [] World, GraphicsUnit Unit, float Scale)> rec_saved = new List<(float [], GraphicsUnit, float)> ();
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

		float BasePerInch => print_mode ? 100f : 96f;

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

		void PushRecordedTransform ()
		{
			if (GpuRecorder == null) return;
			float s = PageFactor;
			float [] w = rec_world;
			GpuRecorder.SetWorldTransform (w [0] * s, w [1] * s, w [2] * s, w [3] * s, w [4] * s, w [5] * s);
		}

		void RecordedCombine (float [] m, MatrixOrder order)
		{
			if (order == MatrixOrder.Prepend) Matrix.Mul (m, rec_world, rec_world);
			else Matrix.Mul (rec_world, m, rec_world);
			PushRecordedTransform ();
		}

		void RecordedSave (int token)
		{
			while (rec_saved.Count < token - 1) rec_saved.Add (((float []) rec_world.Clone (), rec_unit, rec_page_scale));
			if (rec_saved.Count >= token) rec_saved.RemoveRange (token - 1, rec_saved.Count - (token - 1));
			rec_saved.Add (((float []) rec_world.Clone (), rec_unit, rec_page_scale));
		}

		void RecordedRestore (int token)
		{
			int k = token - 1;
			if (k < 0 || k >= rec_saved.Count) return;
			(float [] w, GraphicsUnit u, float s) = rec_saved [k];
			rec_saved.RemoveRange (k, rec_saved.Count - k);
			rec_world = w; rec_unit = u; rec_page_scale = s;
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
			float [] toDevice (CoordinateSpace c)
			{
				var m = new float [] { 1, 0, 0, 1, 0, 0 };
				if (c == CoordinateSpace.World) m = (float []) rec_world.Clone ();
				if (c != CoordinateSpace.Device) Matrix.Mul (m, new float [] { s, 0, 0, sy, 0, 0 }, m);
				return m;
			}
			float [] a = toDevice (from), b = toDevice (to);
			// a * inverse(b)
			float det = b [0] * b [3] - b [1] * b [2];
			if (det == 0) return a;
			var inv = new float [] { b [3] / det, -b [1] / det, -b [2] / det, b [0] / det,
				(b [2] * b [5] - b [3] * b [4]) / det, (b [1] * b [4] - b [0] * b [5]) / det };
			var r = new float [6];
			Matrix.Mul (a, inv, r);
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
			RectangleF v = print_visible;
			var pts = new [] { new PointF (v.Left, v.Top), new PointF (v.Right, v.Top), new PointF (v.Left, v.Bottom), new PointF (v.Right, v.Bottom) };
			// Recording units are device / (dpi/100); go page-unit-free through the device space.
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
			if (TryHatch (brush, out HatchTile ht)) {
				foreach (PointF [] sub in FlattenSubpaths (path))
					if (sub.Length >= 3)
						GpuRecorder.FillHatch (GradientShape.Polygon, 0, 0, 0, 0, ToXY (sub), ht.Rgba, ht.W, ht.H, ht.Size * 100f / 96f);
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

		internal bool PrintDrawString (string s, Font font, Brush brush, RectangleF rect, StringFormat format)
		{
			if (!print_mode || GpuRecorder == null) return false;
			if (string.IsNullOrEmpty (s)) return true;
			int argbFast = ArgbOf (brush);
			if (brush is Drawing2D.LinearGradientBrush lgf) { lgf.GetGpuGradient (out _, out _, out Color c1f, out _); argbFast = c1f.ToArgb (); }
			if (TryPrintGdiPlusText (s, font, argbFast, rect, format)) return true;
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
