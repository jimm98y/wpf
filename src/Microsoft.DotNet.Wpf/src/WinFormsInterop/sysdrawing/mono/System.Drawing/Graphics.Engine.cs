// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Graphics on a managed Bitmap draws with the managed GDI+ engine (WebGpuBackend.Gdip.GpGraphics):
// its verbs write the bitmap's pixels as gdiplus.dll would. The Graphics keeps its recorder too,
// for what the engine does not draw itself (GDI text through TextRenderer, say): that is recorded
// and rendered onto the pixels before the engine's next verb, so the order is kept.
//
// The state a caller sets -- world transform, page unit and scale, quality modes -- is held by the
// Graphics (rec_world and friends, Graphics.Print.cs) and handed to the engine before each verb.
// Clips and save/restore/containers are given to both as they happen.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Drawing.WebGpuBackend.Gdip;

namespace System.Drawing
{
	public sealed partial class Graphics
	{
		/// <summary>The managed GDI+ engine drawing into <see cref="image_target"/>; null for a
		/// recording-only Graphics (a window's paint, a printed page).</summary>
		internal GpGraphics gp;

		// Quality settings a recording Graphics has no other home for.
		CompositingQuality _compositingQuality = CompositingQuality.Default;
		InterpolationMode _interpolation = InterpolationMode.Bilinear;
		PixelOffsetMode _pixelOffset = PixelOffsetMode.Default;
		Point _renderingOrigin;

		// The current container's transform (world-to-device of the context it was begun in, mapping
		// its source rectangle onto its destination) and the stack of what Save/BeginContainer hid.
		float [] rec_container = { 1f, 0f, 0f, 1f, 0f, 0f };
		bool rec_in_container;

		GpMatrix RecWorld => new GpMatrix (rec_world [0], rec_world [1], rec_world [2], rec_world [3], rec_world [4], rec_world [5]);

		void SetRecWorld (in GpMatrix m)
		{
			rec_world = new float [] { m.M11, m.M12, m.M21, m.M22, m.Dx, m.Dy };
			PushRecordedTransform ();
		}

		/// <summary>The engine, its state brought up to date and anything recorded before it drawn,
		/// or null when this Graphics has none.</summary>
		GpGraphics Engine ()
		{
			if (gp == null || image_target == null)
				return null;
			image_target.FlushDrawing ();
			SyncEngine ();
			return gp;
		}

		void SyncEngine ()
		{
			gp.SetPixelOffset (_pixelOffset);
			gp.SetPage (rec_unit, rec_page_scale);
			gp.SetWorld (RecWorld);
			gp.Smoothing = gpu_smoothing;
			gp.Compositing = _compositingMode;
			gp.CompositingQuality = _compositingQuality;
			gp.Interpolation = _interpolation;
			gp.TextHint = recorded_text_hint;
			gp.TextContrast = recorded_text_contrast;
			gp.RenderingOrigin = _renderingOrigin;
		}

		/// <summary>The engine's state only (no flush): for clip calls, which are made in world
		/// coordinates of the transform current when they are made.</summary>
		GpGraphics EngineState ()
		{
			if (gp == null) return null;
			SyncEngine ();
			return gp;
		}

		/// <summary>World to device, as a region or path is rasterized against this Graphics.</summary>
		internal GpMatrix RegionWorldToDevice ()
		{
			if (gp != null) { SyncEngine (); return gp.WorldToDevice; }
			float [] m = SpaceMatrix (CoordinateSpace.World, CoordinateSpace.Device);
			return new GpMatrix (m [0], m [1], m [2], m [3], m [4], m [5]);
		}

		// ---- the verbs the engine draws ----------------------------------------------------------

		bool EngineFillPath (Brush brush, GpPath path)
		{
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.FillPath (brush, path.PointArray (), path.TypeArray (), path.FillMode);
		}

		bool EngineFillRects (Brush brush, RectangleF [] rects)
		{
			if (mf_rec != null) { mf_rec.FillRects (brush, rects); return true; }
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.FillRects (brush, rects);
		}

		bool EngineFillPolygon (Brush brush, PointF [] pts, FillMode mode)
		{
			if (mf_rec != null) { mf_rec.FillPolygon (brush, pts, mode); return true; }
			if (gp == null) return false;
			var p = new GpPath (mode);
			p.AddPolygon (pts, pts.Length);
			return EngineFillPath (brush, p);
		}

		bool EngineFillEllipse (Brush brush, float x, float y, float w, float h)
		{
			if (mf_rec != null) { mf_rec.FillEllipse (brush, new RectangleF (x, y, w, h)); return true; }
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddEllipse (x, y, w, h);
			return EngineFillPath (brush, p);
		}

		bool EngineFillPie (Brush brush, float x, float y, float w, float h, float start, float sweep)
		{
			if (mf_rec != null) { mf_rec.FillPie (brush, new RectangleF (x, y, w, h), start, sweep); return true; }
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddPie (x, y, w, h, start, sweep);
			return EngineFillPath (brush, p);
		}

		bool EngineFillClosedCurve (Brush brush, PointF [] pts, float tension, FillMode mode)
		{
			if (mf_rec != null) { mf_rec.FillClosedCurve (brush, pts, tension, mode); return true; }
			if (gp == null) return false;
			var p = new GpPath (mode);
			p.AddClosedCurve (pts, pts.Length, tension);
			return EngineFillPath (brush, p);
		}

		bool EngineFillRegion (Brush brush, Region region)
		{
			if (mf_rec != null) { mf_rec.FillRegion (brush, region); return true; }
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.FillRegion (brush, region.gp);
		}

		bool EngineClear (Color color)
		{
			if (mf_rec != null) { mf_rec.Clear (color); return true; }
			GpGraphics e = Engine ();
			if (e == null) return false;
			e.Clear (color);
			return true;
		}

		// ---- pens: each call builds the path GDI+'s flat API builds for it ---------------------------

		bool EngineDrawPath (Pen pen, GpPath path)
		{
			if (pen == null || path == null || gp == null) return false;
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.DrawPath (pen, path);
		}

		/// <summary>GpGraphics::DrawLines: the points as they are (no repeat dropped), one figure,
		/// closed for a polygon.</summary>
		bool EngineDrawLines (Pen pen, PointF [] pts, bool closed)
		{
			if (pen == null || pts == null) return false;
			if (mf_rec != null) { mf_rec.DrawLines (pen, pts, closed); return true; }
			if (pts.Length < 2 || gp == null) return false;
			var types = new byte [pts.Length];
			for (int i = 1; i < types.Length; i++) types [i] = 1;
			if (closed) types [types.Length - 1] |= 0x80;
			return EngineDrawPath (pen, new GpPath ((PointF []) pts.Clone (), types, FillMode.Alternate));
		}

		bool EngineDrawLines (Pen pen, Point [] pts, bool closed)
			=> pts != null && EngineDrawLines (pen, Array.ConvertAll (pts, p => (PointF) p), closed);

		bool EngineDrawLine (Pen pen, float x1, float y1, float x2, float y2)
			=> EngineDrawLines (pen, new [] { new PointF (x1, y1), new PointF (x2, y2) }, false);

		bool EngineDrawRects (Pen pen, RectangleF [] rects)
		{
			if (pen == null || rects == null) return false;
			if (mf_rec != null) { mf_rec.DrawRects (pen, rects); return true; }
			if (gp == null) return false;
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.DrawRects (pen, rects);
		}

		bool EngineDrawArc (Pen pen, float x, float y, float w, float h, float start, float sweep)
		{
			if (pen == null) return false;
			if (mf_rec != null) { mf_rec.DrawArc (pen, new RectangleF (x, y, w, h), start, sweep); return true; }
			if (gp == null) return false;
			var p = new GpPath ();
			if (!p.AddArc (x, y, w, h, start, sweep)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawEllipse (Pen pen, float x, float y, float w, float h)
		{
			if (pen == null) return false;
			if (mf_rec != null) { mf_rec.DrawEllipse (pen, new RectangleF (x, y, w, h)); return true; }
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddEllipse (x, y, w, h);
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawPie (Pen pen, float x, float y, float w, float h, float start, float sweep)
		{
			if (pen == null) return false;
			if (mf_rec != null) { mf_rec.DrawPie (pen, new RectangleF (x, y, w, h), start, sweep); return true; }
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddPie (x, y, w, h, start, sweep);
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawBeziers (Pen pen, PointF [] pts)
		{
			if (pen == null || pts == null) return false;
			if (mf_rec != null) { mf_rec.DrawBeziers (pen, pts); return true; }
			if (gp == null) return false;
			if (pts.Length < 4) return true;
			var p = new GpPath ();
			if (!p.AddBeziers (pts, pts.Length)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawCurve (Pen pen, PointF [] pts, float tension, int offset, int segments)
		{
			if (pen == null || pts == null) return false;
			if (mf_rec != null) { mf_rec.DrawCurve (pen, pts, offset, segments, tension); return true; }
			if (pts.Length < 2 || gp == null) return false;
			var p = new GpPath ();
			if (!p.AddCurve (pts, pts.Length, tension, offset, segments)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawClosedCurve (Pen pen, PointF [] pts, float tension)
		{
			if (pen == null || pts == null) return false;
			if (mf_rec != null) { mf_rec.DrawClosedCurve (pen, pts, tension); return true; }
			if (pts.Length < 3 || gp == null) return false;
			var p = new GpPath ();
			if (!p.AddClosedCurve (pts, pts.Length, tension)) return false;
			return EngineDrawPath (pen, p);
		}



		// ---- images: each overload as GDI+'s flat API hands it on ----------------------------------

		/// <summary>The managed pixels of <paramref name="image"/> when the engine can draw it (a
		/// managed Bitmap), else null and the caller's own path runs (a Metafile, say).</summary>
		static GdipFrame EngineFrame (Image image)
		{
			if (!(image is Bitmap b) || b.managed == null) return null;
			GdipFrame f = b.Data.Frame;
			return GpGraphics.CanDrawImage (f) ? f : null;
		}

		static GpImageAttr EngineAttr (ImageAttributes ia) => ia?.ToEngine ();

		static void CheckImageUnit (GraphicsUnit unit)
		{
			if ((uint) (unit - GraphicsUnit.Pixel) > 4u) throw new ArgumentException ("Parameter is not valid.");
		}

		/// <summary>GdipDrawImage / GdipDrawImageI: at (x, y), the image's physical size.</summary>
		bool EngineDrawImage (Image image, float x, float y)
		{
			if (MetaDrawImage (image, x, y)) return true;
			if (gp == null || image == null) return false;
			GpGraphics e = Engine ();
			GdipFrame f = e == null ? null : EngineFrame (image);
			if (f == null) return false;
			e.GetImageDestPageSize (f, f.Width, f.Height, GraphicsUnit.Pixel, out float w, out float h);
			return e.DrawImage (f, new RectangleF (x, y, w, h), new RectangleF (0, 0, f.Width, f.Height), GraphicsUnit.Pixel, null);
		}

		/// <summary>GdipDrawImageRect(I).</summary>
		bool EngineDrawImage (Image image, RectangleF dst)
		{
			if (MetaDrawImage (image, dst)) return true;
			if (gp == null || image == null) return false;
			GpGraphics e = Engine ();
			GdipFrame f = e == null ? null : EngineFrame (image);
			if (f == null) return false;
			return e.DrawImage (f, dst, new RectangleF (0, 0, f.Width, f.Height), GraphicsUnit.Pixel, null);
		}

		/// <summary>GdipDrawImageRectRect(I).</summary>
		bool EngineDrawImage (Image image, RectangleF dst, RectangleF src, GraphicsUnit unit, ImageAttributes ia)
		{
			if (MetaDrawImage (image, dst, src, unit, ia)) return true;
			if (gp == null || image == null) return false;
			GpGraphics e = Engine ();
			GdipFrame f = e == null ? null : EngineFrame (image);
			if (f == null) return false;
			CheckImageUnit (unit);
			return e.DrawImage (f, dst, src, unit, EngineAttr (ia));
		}

		/// <summary>GdipDrawImagePointRect(I): at (x, y), the source rectangle's size in its unit.</summary>
		bool EngineDrawImage (Image image, float x, float y, RectangleF src, GraphicsUnit unit)
		{
			if (MetaDrawImage (image, x, y, src, unit)) return true;
			if (gp == null || image == null) return false;
			GpGraphics e = Engine ();
			GdipFrame f = e == null ? null : EngineFrame (image);
			if (f == null) return false;
			CheckImageUnit (unit);
			e.GetImageDestPageSize (f, src.Width, src.Height, unit, out float w, out float h);
			return e.DrawImage (f, new RectangleF (x, y, w, h), src, unit, null);
		}

		/// <summary>GdipDrawImagePoints(I) (src null: the image's bounds, Pixel) and GdipDrawImagePointsRect(I).</summary>
		bool EngineDrawImage (Image image, PointF [] pts, RectangleF? src, GraphicsUnit unit, ImageAttributes ia)
		{
			if (MetaDrawImage (image, pts, src, unit, ia)) return true;
			if (gp == null || image == null || pts == null) return false;
			GpGraphics e = Engine ();
			GdipFrame f = e == null ? null : EngineFrame (image);
			if (f == null) return false;
			if (pts.Length == 0) throw new ArgumentException ("Parameter is not valid.");
			CheckImageUnit (unit);
			if (pts.Length == 4) throw new NotImplementedException ();
			if (pts.Length != 3) throw new ArgumentException ("Parameter is not valid.");
			return e.DrawImage (f, (PointF []) pts.Clone (), src ?? new RectangleF (0, 0, f.Width, f.Height), unit, EngineAttr (ia));
		}

		static PointF [] EnginePoints (Point [] p)
		{
			if (p == null) return null;
			var r = new PointF [p.Length];
			for (int i = 0; i < p.Length; i++) r [i] = new PointF (p [i].X, p [i].Y);
			return r;
		}

		/// <summary>DrawString on a bitmap: GDI+'s own text, laid out and composited by the engine
		/// (GpText). False where the engine does not draw this string.</summary>
		bool EngineDrawString (string s, Font font, Brush brush, RectangleF rect, StringFormat format)
		{
			if (gp == null || image_target == null || !s_gdiPlusText)
				return false;
			GpGraphics e = Engine ();
			if (e == null || !e.CanFill (brush)) return false;
			return e.DrawString (s, font, brush, rect, format);
		}

		/// <summary>DrawDriverString on a bitmap: GDI+'s DriverStringImager, drawn by the engine
		/// (GpGraphics.DrawDriverString). False where the engine does not draw it.</summary>
		internal bool EngineDrawDriverString (ushort [] glyphs, Font font, Brush brush, PointF [] positions, int options, Drawing2D.Matrix matrix)
		{
			if (gp == null || image_target == null || !s_gdiPlusText || font == null || glyphs == null || positions == null)
				return false;
			string family = font.FontFamily?.Name;
			if (string.IsNullOrEmpty (family)) return false;
			int style = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0) | (font.Underline ? 4 : 0) | (font.Strikeout ? 8 : 0);
			GpGraphics e = Engine ();
			if (e == null || !e.CanFill (brush)) return false;
			return e.DrawDriverString (glyphs, family, style, font.SizeInPoints, brush, positions, options, matrix);
		}

		/// <summary>Set on the WinForms controls' shared measuring Graphics (Hwnd.GraphicsContext):
		/// MeasureString and MeasureCharacterRanges there keep the port's own measurement.</summary>
		internal bool port_measure;

		static readonly bool s_noGpMeasure = Environment.GetEnvironmentVariable ("WF_GP_MEASURE") == "0";
		static readonly bool s_noGpRanges = Environment.GetEnvironmentVariable ("WF_GP_RANGES") == "0";

		/// <summary>MeasureString as gdiplus.dll answers it (GpGraphics::MeasureString through
		/// FullTextImager): the nominal, device-independent layout of GpTextLayout. False for the
		/// port's own GDI stand-ins and for a font the managed stack cannot resolve.</summary>
		bool GdiPlusMeasure (string text, Font font, RectangleF rect, StringFormat format, out SizeF size,
				     out int chars, out int lines)
		{
			size = SizeF.Empty; chars = lines = 0;
			if (!s_gdiPlusText || s_noGpMeasure || port_measure || gdi_text_metrics || gdi_ascent || memory_surface_text || font == null)
				return false;
			string family = font.FontFamily?.Name;
			if (string.IsNullOrEmpty (family)) return false;
			int style = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
			var font2 = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face (family, style);
			GpFontFamily.Metrics? m = GpFontFamily.Get (family, (FontStyle) style);
			if (font2 == null || m == null) return false;
			int flags = format == null ? 0 : (int) format.FormatFlags;
			bool typographic = format != null && format.IsTypographic;
			bool hotkey = format != null && format.HotkeyPrefix != Text.HotkeyPrefix.None;
			float dpi = gp != null ? gp.DpiY : 96f;
			float em = font.SizeInPoints * (dpi / 72f);
			GpTextLayout L = GpTextLayout.Build (font2, m.Value, text, em, rect.Width, flags, typographic, hotkey);
			size = L.Measure (rect.Height, flags, out chars, out lines);
			return true;
		}

		/// <summary>MeasureCharacterRanges as gdiplus.dll answers it: the lines GpTextLayout breaks,
		/// each placed at its rounded origin (the margin in ideal units) and advanced by the hinted
		/// advances of the realization the TextRenderingHint asks for; a range's region is its run
		/// on each line it touches, x on whole pixels, y the line box [ceil(top), ceil(bottom)).
		/// Checked against gdiplus.dll over four faces, three sizes, both formats.</summary>
		Region[] GdiPlusCharacterRanges (string text, Font font, RectangleF rect, StringFormat format, int count)
		{
			if (!s_gdiPlusText || s_noGpRanges || port_measure || gdi_text_metrics || gdi_ascent || memory_surface_text) return null;
			string family = font.FontFamily?.Name;
			if (string.IsNullOrEmpty (family)) return null;
			int style = (font.Bold ? 1 : 0) | (font.Italic ? 2 : 0);
			var face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face (family, style);
			GpFontFamily.Metrics? mm = GpFontFamily.Get (family, (FontStyle) style);
			if (face == null || mm == null) return null;
			GpFontFamily.Metrics m = mm.Value;
			int flags = (int) format.FormatFlags;
			bool typographic = format.IsTypographic;
			bool hotkey = format.HotkeyPrefix != Text.HotkeyPrefix.None;
			float dpi = gp != null ? gp.DpiY : 96f;
			float em = font.SizeInPoints * (dpi / 72f);
			GpTextLayout L = GpTextLayout.Build (face, m, text, em, rect.Width, flags, typographic, hotkey);
			int hint = (int) recorded_text_hint;
			if (hint == 0) hint = 5;
			int upem = face.UnitsPerEmForHinting;
			float scale = em / upem;
			int Hinted (int gid)
			{
				int a;
				if (hint == 5) Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.NaturalMetrics (face, gid, em, out a, out _, out _);
				else if (hint == 1 || hint == 3) Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.ClassicMetrics (face, gid, em, out a, out _, out _);
				else return (int) Math.Floor (face.DesignAdvance (gid) * scale + 0.5f);
				return (int) Math.Floor (a * scale + 0.5f);
			}
			float lsPx = (float) (m.LineSpacing * (double) em / m.Em);
			float margin = typographic ? 0f : (float) (GpTextLayout.IdealMargin * (double) em / GpTextLayout.Ideal);
			CharacterRange[] ranges = format.MeasurableCharacterRanges;
			var regions = new Region [count];
			for (int r = 0; r < count; r++) {
				CharacterRange cr = ranges != null && r < ranges.Length ? ranges [r] : new CharacterRange (0, 0);
				int first = cr.First, last = cr.First + cr.Length;
				var reg = new Region ();
				reg.MakeEmpty ();
				for (int li = 0; li < L.Lines.Count; li++) {
					GpTextLayout.Line line = L.Lines [li];
					// Hinted advances from the origin rounded to the pixel (the full imager's own
					// spreading of the hinted-vs-nominal difference, AdjustGlyphAdvances @1800f4e18,
					// is not modelled).
					var xs = new int [line.Glyphs.Count + 1];
					xs [0] = (int) Math.Floor (rect.X + margin + 0.5f);
					for (int g = 0; g < line.Glyphs.Count; g++) xs [g + 1] = xs [g] + Hinted (line.Glyphs [g]);
					int x0 = int.MinValue, x1 = int.MinValue;
					for (int g = 0; g < line.Glyphs.Count; g++) {
						int ch = line.Chars [g];
						if (ch >= first && ch < last) {
							if (x0 == int.MinValue) x0 = xs [g];
							x1 = xs [g + 1];
						}
					}
					if (x0 == int.MinValue) continue;
					float top = rect.Y + li * lsPx;
					float bottom = top + (float) ((m.Ascent + m.Descent) * (double) em / m.Em);
					int t = (int) Math.Ceiling (top), b = (int) Math.Ceiling (bottom);
					reg.Union (new Rectangle (x0, t, x1 - x0, b - t));
				}
				regions [r] = reg;
			}
			return regions;
		}

		// ---- state the engine keeps itself --------------------------------------------------------

		// A metafile's Graphics records each (GpGraphics::SetClip* -> MetafileRecorder::RecordSetClip*).

		void EngineClipRect (RectangleF r, CombineMode mode)
		{
			mf_rec?.SetClipRect (r, mode);
			EngineState ()?.CombineClip (r, mode);
		}

		void EngineClipPath (GraphicsPath path, CombineMode mode)
		{
			mf_rec?.SetClipPath (path, mode);
			EngineState ()?.CombineClip (path.gp.PointArray (), path.gp.TypeArray (), path.gp.FillMode, mode);
		}

		void EngineClipRegion (Region region, CombineMode mode)
		{
			mf_rec?.SetClipRegion (region, mode);
			EngineState ()?.CombineClip (region.gp, mode);
		}

		void EngineResetClip ()
		{
			mf_rec?.ResetClip ();
			gp?.ResetClip ();
		}

		void EngineOffsetClip (float dx, float dy)
		{
			mf_rec?.OffsetClip (dx, dy);
			EngineState ()?.OffsetClip (dx, dy);
		}
	}
}
