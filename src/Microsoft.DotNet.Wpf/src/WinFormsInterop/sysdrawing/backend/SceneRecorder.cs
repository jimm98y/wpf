// Implements the GPU-rasterization seam as PURE SCENE DATA: records System.Drawing.Graphics verbs
// (via IGpuSceneRecorder) into a WgpuInterop SceneVisual. NO GPU work happens here — no device, no
// readback. The driver keeps each window's recorded scene; the WebGPU present path composites all of
// them in ONE render pass on ONE device (no per-control readback / re-upload). Glyph rasterization
// happens at present time in the renderer that owns the font, so DrawText just records a GlyphRunDraw.

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class SceneRecorder : IGpuSceneRecorder
    {
        private readonly SceneVisual _root = new SceneVisual();
        // Clip stack: primitives go into the top container (nested so render order = insertion order).
        // SetClip pushes; a clip restore (ResetClip / assigning Graphics.Clip) pops one level.
        private readonly List<SceneVisual> _stack;

        internal SceneVisual Scene => _root;

        public SceneRecorder() { _stack = new List<SceneVisual> { _root }; }


        private SceneVisual Target => _stack[_stack.Count - 1];

        // Graphics.CompositingMode, applied to every primitive recorded from here on.
        private bool _sourceCopy;

        public void SetCompositingMode(bool sourceCopy) => _sourceCopy = sourceCopy;

        // Every primitive goes through here so the current compositing mode is stamped on it
        // exactly once, rather than being threaded through eleven separate append sites.
        private void Add(DrawingPrimitive p)
        {
            p.SourceCopy = _sourceCopy;
            Target.Content.Add(p);
        }

        public void SetClipRect(float x, float y, float w, float h, bool exclude)
        {
            var container = new SceneVisual();
            if (exclude)
            {
                // "everything except this rect": a huge outer rect + the excluded rect, even-odd ->
                // fills the ring. Used to gap the GroupBox border around its title.
                const float Big = 1 << 20;
                container.ClipGeometry = new PathGeometry(FillRule.EvenOdd, new List<PathFigure>
                {
                    RectFigure(-Big, -Big, Big * 2, Big * 2),
                    RectFigure(x, y, w, h),
                });
            }
            else
            {
                container.Clip = new Rect(x, y, w, h);   // intersect (fast scissor)
            }
            Target.Children.Add(container);
            _stack.Add(container);
        }

        public void ClearClip() { if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); }

        // Transform containers are counted separately from clips so ResetTransform unwinds exactly
        // the ones it pushed. WinForms draws composite controls by translating to a part's bounds,
        // drawing it at the origin and resetting -- ToolStrip does this per item -- so without it
        // every part landed on top of the first.
        private int _translateDepth;

        public void PushTranslate(float dx, float dy)
        {
            var container = new SceneVisual { Offset = new Vector2(dx, dy) };
            Target.Children.Add(container);
            _stack.Add(container);
            _translateDepth++;
        }

        public void ResetTransform()
        {
            while (_translateDepth > 0 && _stack.Count > 1)
            {
                _stack.RemoveAt(_stack.Count - 1);
                _translateDepth--;
            }
        }

        private static PathFigure RectFigure(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return f;
        }

        public void FillRect(float x, float y, float w, float h, int argb)
            => Add(new GeometryFill(new RectangleGeometry(new Rect(x, y, w, h)), Rgba(argb)));

        public void FillGradient(GradientShape shape, float x, float y, float w, float h, float[] polyXY, GradientDesc g)
        {
            Geometry geo = shape switch
            {
                GradientShape.Ellipse => new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f),
                GradientShape.Polygon => new PolygonGeometry(ToVecs(polyXY)),
                _ => new RectangleGeometry(new Rect(x, y, w, h)),
            };
            var stops = new GradientStop[g.Offsets.Length];
            for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(g.Offsets[i], Rgba(g.Argb[i]));
            Add(g.Radial
                ? new GeometryFill(geo, new RadialGradientBrush(new Vector2(g.Sx, g.Sy), g.Ex, g.Ey, stops))
                : new GeometryFill(geo, new LinearGradientBrush(new Vector2(g.Sx, g.Sy), new Vector2(g.Ex, g.Ey), stops)));
        }

        private static Vector2[] ToVecs(float[] xy)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return pts;
        }

        public void FillEllipse(float x, float y, float w, float h, int argb)
            => Add(new GeometryFill(
                new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f), Rgba(argb)));

        public void FillHatch(GradientShape shape, float x, float y, float w, float h, float[] polyXY,
                              byte[] tileRgba, int tileW, int tileH, float tileSize)
        {
            Geometry geo = shape switch
            {
                GradientShape.Ellipse => new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f),
                GradientShape.Polygon => new PolygonGeometry(ToVecs(polyXY)),
                _ => new RectangleGeometry(new Rect(x, y, w, h)),
            };
            // sRGB tile bytes -> uploaded as an sRGB texture (hardware decodes on sample), so no
            // sRGB->linear conversion here (unlike solid fills), matching the DrawImage path.
            Add(new GeometryFill(geo,
                new ImageBrush(tileRgba, tileW, tileH, TileMode.Tile, tileSize, tileSize)));
        }

        public void FillPolygon(float[] xy, int argb)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            Add(new GeometryFill(new PolygonGeometry(pts), Rgba(argb)));
        }

        public void DrawLine(float x1, float y1, float x2, float y2, int argb, float width = 1f)
        {
            RgbaColor c = Rgba(argb);

            // One pixel is the overwhelming case, and it is drawn as the exact integer rectangle the
            // line covers -- crisp, and identical to what GDI+ puts down. Anything else is stroked to
            // its real width: a pen thinner than a pixel comes out as partial coverage, which is how
            // a tick or a hairline is meant to read, and a wider one actually gets wider.
            bool hairline = width > 0.99f && width < 1.01f;
            float half = System.Math.Max(width, 0.01f) * 0.5f;

            if (y1 == y2)        // horizontal
            {
                float y = hairline ? y1 : y1 + 0.5f - half;
                Add(new GeometryFill(new RectangleGeometry(
                    new Rect(Min(x1, x2), y, Abs(x2 - x1) + 1, hairline ? 1 : width)), c));
            }
            else if (x1 == x2)   // vertical
            {
                float x = hairline ? x1 : x1 + 0.5f - half;
                Add(new GeometryFill(new RectangleGeometry(
                    new Rect(x, Min(y1, y2), hairline ? 1 : width, Abs(y2 - y1) + 1)), c));
            }
            else                 // diagonal: a quad along the segment
            {
                float dx = x2 - x1, dy = y2 - y1, len = (float)System.Math.Sqrt(dx * dx + dy * dy);
                float nx = -dy / len * half, ny = dx / len * half;
                Add(new GeometryFill(new PolygonGeometry(new[]
                {
                    new Vector2(x1 + nx, y1 + ny), new Vector2(x2 + nx, y2 + ny),
                    new Vector2(x2 - nx, y2 - ny), new Vector2(x1 - nx, y1 - ny),
                }), c));
            }
        }

        // A dashed line has to stay a STROKE: the scene's stroke style carries a dash array that the
        // renderer honours, whereas the solid path above collapses a line to a filled 1px rect and
        // would lose the pattern entirely. The forms designer draws its 8x8 dot grid as horizontal
        // lines dashed {1, 7}, so losing it turned the design surface into solid stripes.
        public void DrawDashedLine(float x1, float y1, float x2, float y2, int argb, float width, float[] dashPattern)
        {
            if (dashPattern == null || dashPattern.Length == 0)
            {
                DrawLine(x1, y1, x2, y2, argb);
                return;
            }

            float w = width <= 0 ? 1f : width;

            // A one-pixel stroke is centred on the line it is given, so an axis-aligned one at a
            // whole coordinate lands half in each of two pixels and fills neither -- a tree view's
            // connectors came out two faint columns wide where Windows draws one solid. Half a pixel
            // back puts it inside a single row or column. The solid path above does not need this:
            // it collapses to a filled rectangle rather than a stroke.
            if (w <= 1.5f)
            {
                if (x1 == x2) { x1 -= 0.5f; x2 -= 0.5f; }
                else if (y1 == y2) { y1 -= 0.5f; y2 -= 0.5f; }
            }

            var dashes = new double[dashPattern.Length];
            for (int i = 0; i < dashPattern.Length; i++)
                dashes[i] = dashPattern[i];      // stroke dashes are in multiples of thickness, as in GDI+

            // The pattern is anchored to the DEVICE GRID, not to where this particular line starts.
            // GDI's dotted pen is a brush pinned to the surface, so two dotted lines that meet end to
            // end carry on the same dots; ours restarted the pattern per call, and a tree view's
            // connector -- drawn as two segments, one either side of the node -- changed phase in the
            // middle of what reads as a single line.
            double period = 0.0;
            foreach (double d in dashes) period += d;
            double offset = 0.0;
            if (period > 0.0 && w > 0f && (x1 == x2 || y1 == y2))
            {
                offset = ((x1 == x2 ? y1 : x1) / w) % period;
                if (offset < 0.0) offset += period;
            }

            var fig = new PathFigure(new Vector2(x1, y1)) { Closed = false };
            fig.Segments.Add(new LineSegment(new Vector2(x2, y2)));
            var geo = new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
            Add(new GeometryStroke(geo, Rgba(argb),
                new StrokeStyle(w, LineCap.Butt, LineJoin.Miter, 10.0, dashes, offset)));
        }

        // Arc as a stroked path sampled along the ellipse (handles the full-circle radio/checkbox
        // ring at 0..359 as well as partial arcs).
        public void DrawArc(float x, float y, float w, float h, float startDeg, float sweepDeg, int argb, float thickness)
        {
            float cx = x + w / 2f, cy = y + h / 2f, rx = w / 2f, ry = h / 2f;
            int n = System.Math.Max(8, (int)(System.Math.Abs(sweepDeg) / 8f));
            var fig = new PathFigure(ArcPt(cx, cy, rx, ry, startDeg));
            for (int i = 1; i <= n; i++)
                fig.Segments.Add(new LineSegment(ArcPt(cx, cy, rx, ry, startDeg + sweepDeg * i / n)));
            fig.Closed = System.Math.Abs(sweepDeg) >= 359f;
            var geo = new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
            Add(new GeometryStroke(geo, Rgba(argb), new StrokeStyle(thickness < 1f ? 1f : thickness)));
        }

        private static Vector2 ArcPt(float cx, float cy, float rx, float ry, float deg)
        {
            double a = deg * System.Math.PI / 180.0;   // GDI+ degrees; y-down makes +angle clockwise (matches)
            return new Vector2(cx + rx * (float)System.Math.Cos(a), cy + ry * (float)System.Math.Sin(a));
        }

        public void DrawImage(byte[] rgba, int pw, int ph, float dx, float dy, float dw, float dh)
            // ImageBrush maps its pixel dimensions onto the geometry bounds (stretched to the dest rect).
            => Add(new GeometryFill(
                new RectangleGeometry(new Rect(dx, dy, dw, dh)), new ImageBrush(rgba, pw, ph)));

        public void DrawText(string text, float x, float y, float emPx, int argb, int simulations, string fontFamily)
            // GDI+ top-left origin -> GlyphRunDraw baseline (drop by ~ascent). Glyphs are rasterized
            // at present time by the renderer that owns the font.
            => Add(new GlyphRunDraw(text, new Vector2(x, y + emPx * 0.8f), emPx, Rgba(argb), simulations, fontFamily));

        // A colour as WinForms states it. WHICH SPACE depends on the one the compositor blends in,
        // and the two have to agree or every blend is wrong.
        //
        // This used to linearise unconditionally, pairing with the sRGB surface the host always asked
        // for -- a linear pipeline, hardware-encoded on write. It matched Windows on every opaque fill
        // and lost to it on every BLEND, because a linear blend is not what GDI does. GDI works on
        // encoded bytes, and so does this renderer in its default gamma mode (its gradient Lerp
        // already treats stops as sRGB-encoded, and RenderToRgba already forces a UNORM target).
        // WinForms was the odd one out, and text paid for it: a glyph edge at 0.72 coverage over white
        // came out at 145 where GDI puts it at 72, and our text carried about a quarter less ink than
        // Windows' across the whole window.
        //
        // The linear branch is not dead: the sink turns gamma mode OFF for backends that cannot
        // present a pre-encoded UNORM swapchain faithfully (ANGLE, notably), and there the surface
        // stays sRGB and the colours have to be linear to match it. WgpuPresenter picks the surface
        // format off the same switch.
        private static RgbaColor Rgba(int argb)
        {
            float r = ((argb >> 16) & 0xff) / 255f, g = ((argb >> 8) & 0xff) / 255f, b = (argb & 0xff) / 255f;
            float a = ((argb >> 24) & 0xff) / 255f;
            return WgpuSceneRenderer.s_gammaComposite
                 ? new RgbaColor(r, g, b, a)
                 : new RgbaColor(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), a);
        }

        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055) / 1.055, 2.4);

        private static float Min(float a, float b) => a < b ? a : b;
        private static float Abs(float v) => v < 0 ? -v : v;
    }
}
