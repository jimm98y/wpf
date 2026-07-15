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

        private static PathFigure RectFigure(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return f;
        }

        public void FillRect(float x, float y, float w, float h, int argb)
            => Target.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(x, y, w, h)), Rgba(argb)));

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
            Target.Content.Add(g.Radial
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
            => Target.Content.Add(new GeometryFill(
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
            Target.Content.Add(new GeometryFill(geo,
                new ImageBrush(tileRgba, tileW, tileH, TileMode.Tile, tileSize, tileSize)));
        }

        public void FillPolygon(float[] xy, int argb)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            Target.Content.Add(new GeometryFill(new PolygonGeometry(pts), Rgba(argb)));
        }

        public void DrawLine(float x1, float y1, float x2, float y2, int argb)
        {
            RgbaColor c = Rgba(argb);
            if (y1 == y2)        // horizontal 1px
                Target.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(Min(x1, x2), y1, Abs(x2 - x1) + 1, 1)), c));
            else if (x1 == x2)   // vertical 1px
                Target.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(x1, Min(y1, y2), 1, Abs(y2 - y1) + 1)), c));
            else                 // diagonal: a thin quad along the segment
            {
                float dx = x2 - x1, dy = y2 - y1, len = (float)System.Math.Sqrt(dx * dx + dy * dy);
                float nx = -dy / len * 0.5f, ny = dx / len * 0.5f;
                Target.Content.Add(new GeometryFill(new PolygonGeometry(new[]
                {
                    new Vector2(x1 + nx, y1 + ny), new Vector2(x2 + nx, y2 + ny),
                    new Vector2(x2 - nx, y2 - ny), new Vector2(x1 - nx, y1 - ny),
                }), c));
            }
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
            Target.Content.Add(new GeometryStroke(geo, Rgba(argb), new StrokeStyle(thickness < 1f ? 1f : thickness)));
        }

        private static Vector2 ArcPt(float cx, float cy, float rx, float ry, float deg)
        {
            double a = deg * System.Math.PI / 180.0;   // GDI+ degrees; y-down makes +angle clockwise (matches)
            return new Vector2(cx + rx * (float)System.Math.Cos(a), cy + ry * (float)System.Math.Sin(a));
        }

        public void DrawImage(byte[] rgba, int pw, int ph, float dx, float dy, float dw, float dh)
            // ImageBrush maps its pixel dimensions onto the geometry bounds (stretched to the dest rect).
            => Target.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(dx, dy, dw, dh)), new ImageBrush(rgba, pw, ph)));

        public void DrawText(string text, float x, float y, float emPx, int argb)
            // GDI+ top-left origin -> GlyphRunDraw baseline (drop by ~ascent). Glyphs are rasterized
            // at present time by the renderer that owns the font.
            => Target.Content.Add(new GlyphRunDraw(text, new Vector2(x, y + emPx * 0.8f), emPx, Rgba(argb)));

        // ARGB int -> RgbaColor, converting colour channels sRGB->LINEAR: the scene renders to an sRGB
        // surface and the renderer treats RgbaColor as linear (the hardware re-encodes to sRGB on
        // write), so without this the fills come out washed-out. Alpha stays linear.
        private static RgbaColor Rgba(int argb)
            => new RgbaColor(SrgbToLinear(((argb >> 16) & 0xff) / 255f),
                             SrgbToLinear(((argb >> 8) & 0xff) / 255f),
                             SrgbToLinear((argb & 0xff) / 255f),
                             ((argb >> 24) & 0xff) / 255f);

        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055) / 1.055, 2.4);

        private static float Min(float a, float b) => a < b ? a : b;
        private static float Abs(float v) => v < 0 ? -v : v;
    }
}
