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
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.FillRects (brush, rects);
		}

		bool EngineFillPolygon (Brush brush, PointF [] pts, FillMode mode)
		{
			if (gp == null) return false;
			var p = new GpPath (mode);
			p.AddPolygon (pts, pts.Length);
			return EngineFillPath (brush, p);
		}

		bool EngineFillEllipse (Brush brush, float x, float y, float w, float h)
		{
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddEllipse (x, y, w, h);
			return EngineFillPath (brush, p);
		}

		bool EngineFillPie (Brush brush, float x, float y, float w, float h, float start, float sweep)
		{
			if (gp == null) return false;
			var p = new GpPath ();
			p.AddPie (x, y, w, h, start, sweep);
			return EngineFillPath (brush, p);
		}

		bool EngineFillClosedCurve (Brush brush, PointF [] pts, float tension, FillMode mode)
		{
			if (gp == null) return false;
			var p = new GpPath (mode);
			p.AddClosedCurve (pts, pts.Length, tension);
			return EngineFillPath (brush, p);
		}

		bool EngineFillRegion (Brush brush, Region region)
		{
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.FillRegion (brush, region.gp);
		}

		bool EngineClear (Color color)
		{
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
			if (pen == null || pts == null || pts.Length < 2 || gp == null) return false;
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
			if (pen == null || rects == null || gp == null) return false;
			GpGraphics e = Engine ();
			if (e == null) return false;
			return e.DrawRects (pen, rects);
		}

		bool EngineDrawArc (Pen pen, float x, float y, float w, float h, float start, float sweep)
		{
			if (pen == null || gp == null) return false;
			var p = new GpPath ();
			if (!p.AddArc (x, y, w, h, start, sweep)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawEllipse (Pen pen, float x, float y, float w, float h)
		{
			if (pen == null || gp == null) return false;
			var p = new GpPath ();
			p.AddEllipse (x, y, w, h);
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawPie (Pen pen, float x, float y, float w, float h, float start, float sweep)
		{
			if (pen == null || gp == null) return false;
			var p = new GpPath ();
			p.AddPie (x, y, w, h, start, sweep);
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawBeziers (Pen pen, PointF [] pts)
		{
			if (pen == null || pts == null || gp == null) return false;
			if (pts.Length < 4) return gp != null;
			var p = new GpPath ();
			if (!p.AddBeziers (pts, pts.Length)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawCurve (Pen pen, PointF [] pts, float tension, int offset, int segments)
		{
			if (pen == null || pts == null || pts.Length < 2 || gp == null) return false;
			var p = new GpPath ();
			if (!p.AddCurve (pts, pts.Length, tension, offset, segments)) return false;
			return EngineDrawPath (pen, p);
		}

		bool EngineDrawClosedCurve (Pen pen, PointF [] pts, float tension)
		{
			if (pen == null || pts == null || pts.Length < 3 || gp == null) return false;
			var p = new GpPath ();
			if (!p.AddClosedCurve (pts, pts.Length, tension)) return false;
			return EngineDrawPath (pen, p);
		}


		// ---- state the engine keeps itself --------------------------------------------------------

		void EngineClipRect (RectangleF r, CombineMode mode) => EngineState ()?.CombineClip (r, mode);

		void EngineClipPath (GraphicsPath path, CombineMode mode)
			=> EngineState ()?.CombineClip (path.gp.PointArray (), path.gp.TypeArray (), path.gp.FillMode, mode);

		void EngineClipRegion (Region region, CombineMode mode) => EngineState ()?.CombineClip (region.gp, mode);

		void EngineResetClip () => gp?.ResetClip ();

		void EngineOffsetClip (float dx, float dy) => EngineState ()?.OffsetClip (dx, dy);
	}
}
