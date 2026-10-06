// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drawing INTO a managed Bitmap, on the CPU: a scene that Graphics.FromImage recorded, rendered onto
// the bitmap's pixels. It is one more device for the page walker that already serves the printer DC
// and the PDF writer (ScenePageWalker / IPageSink), so it sees what they see -- paths in their
// transform, clips, opacity, brushes, text runs -- and puts down pixels.
//
// What is exact, and what is not (the next phase replaces the approximate parts with GDI+'s own
// rasterizer):
//
//   EXACT   the blend: GDI+ draws onto a bitmap through premultiplied ARGB, rounding the
//           premultiply and truncating through its reciprocal table on the way back (GdipPixels),
//           and only the pixels a primitive touches take that round trip -- so Bitmap(Image), a
//           copied icon, an image drawn 1:1 come out byte for byte;
//           whole-pixel rectangles and aliased fills (the recorder already turns GDI+'s aliased
//           fills into pixel runs), an image drawn 1:1 at a whole-pixel offset, GDI+'s antialiased
//           Bezier fills (recorded by GdipAntialias as coverage images), the banded linear gradient
//           (sixteen bands, sampled at pixel corners);
//           a GDI+ DrawString on opaque pixels: its own levels (GdiPlusText) through its own
//           ClearType / grey blend against the pixel under it, as gdiplus.dll writes them.
//   APPROX  antialiased edges of other shapes (exact-area horizontal, 4x vertical coverage, not
//           GDI+'s 8x8 supersampling); strokes (PathStroker's outline, not GDI+'s widener); scaled
//           or rotated images (bilinear at pixel centres, not GDI+'s prefiltered kernel); radial
//           and path gradients (a smooth ramp); text on a transparent or translucent pixel, and any
//           text drawn as glyph outlines (grey coverage).
//

using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using SceneBrush = Microsoft.Wpf.Interop.WebGpu.Composition.Brush;
using SceneLinear = Microsoft.Wpf.Interop.WebGpu.Composition.LinearGradientBrush;
using SceneRadial = Microsoft.Wpf.Interop.WebGpu.Composition.RadialGradientBrush;
using SceneImage = Microsoft.Wpf.Interop.WebGpu.Composition.ImageBrush;
using SceneSolid = Microsoft.Wpf.Interop.WebGpu.Composition.SolidColorBrush;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class SceneRaster : IPageSink
    {
        /// <summary>Renders <paramref name="scenes"/> (recorded SceneVisuals, oldest first) onto
        /// <paramref name="frame"/>'s pixels.</summary>
        internal static void Render (GdipFrame frame, object[] scenes)
        {
            if (!GdipPixels.Convertible (frame.Format) || (frame.Format & PixelFormat.Indexed) != 0) return;
            var r = new SceneRaster (frame);
            foreach (object s in scenes)
                if (s is SceneVisual v) ScenePageWalker.Walk (v, r);
            r.WriteBack ();
        }

        /// <summary>Whether a recorded scene needs the pixels under it to be right -- it replaces
        /// rather than blends somewhere (SourceCopy) -- and so cannot be drawn as a nested scene.</summary>
        internal static bool NeedsDestination (object scene) => scene is SceneVisual v && HasSourceCopy (v);

        /// <summary>An image's pixels as they were drawn, for the ones Graphics recorded already
        /// carried through a bitmap's premultiplied round trip (for the GPU, which blends straight):
        /// this rasterizer does that round trip itself.</summary>
        internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], byte[]> Original = new ();

        /// <summary>Whether a recorded scene draws text. Text is what this rasterizer draws only
        /// approximately (the GDI ClearType pipeline lives in the GPU renderer), so a bitmap holding
        /// text is better drawn as its nested scene; everything else it draws as GDI+ does, blends
        /// included, so a bitmap without text is rendered here and drawn as pixels.</summary>
        internal static bool HasText (object scene) => scene is SceneVisual v && HasTextIn (v);

        static bool HasTextIn (SceneVisual v)
        {
            foreach (DrawingPrimitive p in v.Content) {
                if (p is GlyphRunDraw || p is GdiPlusTextDraw || p is WpfTextRunDraw) return true;
                if (p is NestedVisualDraw n && HasTextIn (n.Visual)) return true;
            }
            foreach (SceneVisual c in v.Children)
                if (HasTextIn (c)) return true;
            return false;
        }

        static bool HasSourceCopy (SceneVisual v)
        {
            foreach (DrawingPrimitive p in v.Content) {
                if (p.SourceCopy) return true;
                if (p is NestedVisualDraw n && HasSourceCopy (n.Visual)) return true;
            }
            foreach (SceneVisual c in v.Children)
                if (HasSourceCopy (c)) return true;
            return false;
        }

        // ---- the surface ------------------------------------------------------------------------

        readonly GdipFrame _frame;
        readonly int _w, _h;
        readonly uint[] _px;        // premultiplied ARGB, as GDI+ blends
        readonly bool[] _touched;   // only touched pixels take the round trip back

        SceneRaster (GdipFrame frame)
        {
            _frame = frame;
            _w = frame.Width; _h = frame.Height;
            _px = GdipPixels.ToArgb (frame, new Rectangle (0, 0, _w, _h));
            for (int i = 0; i < _px.Length; i++) _px [i] = GdipPixels.PremultiplyArgb (_px [i]);
            _touched = new bool [_px.Length];
        }

        void WriteBack ()
        {
            var row = new uint [_w];
            var orig = new uint [_w];
            for (int y = 0; y < _h; y++) {
                bool any = false;
                for (int x = 0; x < _w; x++) if (_touched [y * _w + x]) { any = true; break; }
                if (!any) continue;
                GdipPixels.ReadArgb (_frame, 0, y, _w, orig, 0);
                for (int x = 0; x < _w; x++) {
                    int i = y * _w + x;
                    row [x] = _touched [i] ? GdipPixels.UnpremultiplyArgb (_px [i]) : orig [x];
                }
                GdipPixels.WriteArgb (_frame, 0, y, _w, row, 0);
            }
        }

        // ---- state ------------------------------------------------------------------------------

        struct State
        {
            public Matrix3x2 M;
            public byte[] Clip;     // per-pixel coverage, or null for none
            public float Opacity;
        }

        State _s = new State { M = Matrix3x2.Identity, Opacity = 1f };
        readonly Stack<State> _saved = new Stack<State> ();
        bool _sourceCopy;

        public void Save () => _saved.Push (_s);

        public void Restore () { if (_saved.Count > 0) _s = _saved.Pop (); }

        public void Concat (Matrix3x2 m) => _s.M = m * _s.M;

        public void Opacity (float alpha) => _s.Opacity *= Math.Clamp (alpha, 0f, 1f);

        public void SourceCopy (bool on) => _sourceCopy = on;

        public void ClipRect (float x, float y, float w, float h)
        {
            var path = new PagePath ();
            path.Rect (x, y, w, h);
            if (_s.M.M12 == 0 && _s.M.M21 == 0) {
                // Upright: a pixel is in when its centre is.
                Vector2 a = Vector2.Transform (new Vector2 (x, y), _s.M), b = Vector2.Transform (new Vector2 (x + w, y + h), _s.M);
                int x0 = (int) MathF.Ceiling (MathF.Min (a.X, b.X) - 0.5f), x1 = (int) MathF.Ceiling (MathF.Max (a.X, b.X) - 0.5f);
                int y0 = (int) MathF.Ceiling (MathF.Min (a.Y, b.Y) - 0.5f), y1 = (int) MathF.Ceiling (MathF.Max (a.Y, b.Y) - 0.5f);
                var mask = new byte [_w * _h];
                for (int yy = Math.Max (0, y0); yy < Math.Min (_h, y1); yy++)
                    for (int xx = Math.Max (0, x0); xx < Math.Min (_w, x1); xx++) mask [yy * _w + xx] = 255;
                Intersect (mask);
                return;
            }
            ClipPath (path);
        }

        public void ClipPath (PagePath path)
        {
            var mask = new byte [_w * _h];
            CoverageMask cm = PathRasterizer.Rasterize (Device (path));
            if (!cm.IsEmpty) {
                int ox = (int) cm.OriginX, oy = (int) cm.OriginY;
                for (int y = 0; y < cm.Height; y++) {
                    int py = oy + y;
                    if (py < 0 || py >= _h) continue;
                    for (int x = 0; x < cm.Width; x++) {
                        int px = ox + x;
                        if (px < 0 || px >= _w) continue;
                        mask [py * _w + px] = cm.Coverage [y * cm.Width + x];
                    }
                }
            }
            Intersect (mask);
        }

        void Intersect (byte[] mask)
        {
            if (_s.Clip != null)
                for (int i = 0; i < mask.Length; i++) mask [i] = (byte) ((mask [i] * _s.Clip [i] + 127) / 255);
            _s.Clip = mask;
        }

        // A page path in device space.
        PathGeometry Device (PagePath path)
        {
            var figures = new List<PathFigure> ();
            PathFigure fig = null;
            int k = 0;
            foreach (PagePath.Op op in path.Ops) {
                switch (op) {
                case PagePath.Op.Move:
                    fig = new PathFigure (Vector2.Transform (path.Points [k++], _s.M)) { Closed = false };
                    figures.Add (fig);
                    break;
                case PagePath.Op.Line:
                    fig?.Segments.Add (new LineSegment (Vector2.Transform (path.Points [k++], _s.M)));
                    break;
                case PagePath.Op.Cubic:
                    var c1 = Vector2.Transform (path.Points [k++], _s.M);
                    var c2 = Vector2.Transform (path.Points [k++], _s.M);
                    var e = Vector2.Transform (path.Points [k++], _s.M);
                    fig?.Segments.Add (new CubicBezierSegment (c1, c2, e));
                    break;
                case PagePath.Op.Close:
                    if (fig != null) fig.Closed = true;
                    break;
                }
            }
            return new PathGeometry (path.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero, figures);
        }

        // ---- fills ------------------------------------------------------------------------------

        public void Fill (PagePath path, SceneBrush brush)
        {
            if (path.IsEmpty || brush == null) return;
            CoverageMask cm = PathRasterizer.Rasterize (Device (path));
            if (cm.IsEmpty) return;
            Paint (cm, brush, path.Bounds ());
        }

        public void Stroke (PagePath path, SceneBrush brush, StrokeStyle style)
        {
            if (path.IsEmpty || brush == null) return;
            // Outlined in the path's own space (the pen is in its units), then filled.
            var local = new PagePath ();
            PathGeometry outline = PathStroker.Stroke (LocalGeometry (path), style);
            foreach (PathFigure f in outline.Figures) {
                local.MoveTo (f.Start);
                Vector2 cur = f.Start;
                foreach (PathSegment s in f.Segments) {
                    switch (s) {
                    case LineSegment l: local.LineTo (l.Point); cur = l.Point; break;
                    case CubicBezierSegment c: local.CubicTo (c.Control1, c.Control2, c.Point); cur = c.Point; break;
                    case QuadraticBezierSegment q:
                        local.CubicTo (cur + (q.Control - cur) * (2f / 3f), q.Point + (q.Control - q.Point) * (2f / 3f), q.Point);
                        cur = q.Point;
                        break;
                    }
                }
                local.Close ();
            }
            local.EvenOdd = false;
            if (local.IsEmpty) return;
            CoverageMask cm = PathRasterizer.Rasterize (Device (local));
            if (!cm.IsEmpty) Paint (cm, brush, path.Bounds ());
        }

        static PathGeometry LocalGeometry (PagePath path)
        {
            var figures = new List<PathFigure> ();
            PathFigure fig = null;
            int k = 0;
            foreach (PagePath.Op op in path.Ops) {
                switch (op) {
                case PagePath.Op.Move: fig = new PathFigure (path.Points [k++]) { Closed = false }; figures.Add (fig); break;
                case PagePath.Op.Line: fig?.Segments.Add (new LineSegment (path.Points [k++])); break;
                case PagePath.Op.Cubic: fig?.Segments.Add (new CubicBezierSegment (path.Points [k], path.Points [k + 1], path.Points [k + 2])); k += 3; break;
                case PagePath.Op.Close: if (fig != null) fig.Closed = true; break;
                }
            }
            return new PathGeometry (FillRule.NonZero, figures);
        }

        public bool Text (PageText run) => false;   // drawn as its outlines

        // Coverage times the brush, blended in.
        void Paint (CoverageMask cm, SceneBrush brush, (float X0, float Y0, float X1, float Y1) localBounds)
        {
            int ox = (int) cm.OriginX, oy = (int) cm.OriginY;
            int x0 = Math.Max (0, ox), y0 = Math.Max (0, oy);
            int x1 = Math.Min (_w, ox + cm.Width), y1 = Math.Min (_h, oy + cm.Height);
            if (x0 >= x1 || y0 >= y1) return;
            Sampler sample = SamplerFor (brush, localBounds);
            if (sample == null) return;
            int opacity = (int) MathF.Round (_s.Opacity * 255f);
            if (opacity <= 0) return;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) {
                    int c = cm.Coverage [(y - oy) * cm.Width + (x - ox)];
                    if (c == 0) continue;
                    int i = y * _w + x;
                    if (_s.Clip != null) { c = (c * _s.Clip [i] + 127) / 255; if (c == 0) continue; }
                    if (opacity < 255) { c = (c * opacity + 127) / 255; if (c == 0) continue; }
                    Blend (i, sample (x, y), c);
                }
        }

        void Blend (int i, uint src, int coverage)
        {
            _touched [i] = true;
            int sa = (int) (src >> 24), sr = (int) (src >> 16) & 0xff, sg = (int) (src >> 8) & 0xff, sb = (int) src & 0xff;
            if (coverage < 255) {
                sa = (sa * coverage + 127) / 255; sr = (sr * coverage + 127) / 255; sg = (sg * coverage + 127) / 255; sb = (sb * coverage + 127) / 255;
            }
            uint d = _px [i];
            int da = (int) (d >> 24), dr = (int) (d >> 16) & 0xff, dg = (int) (d >> 8) & 0xff, db = (int) d & 0xff;
            int keep = _sourceCopy ? 255 - coverage : 255 - sa;
            da = sa + (da * keep + 127) / 255;
            dr = sr + (dr * keep + 127) / 255;
            dg = sg + (dg * keep + 127) / 255;
            db = sb + (db * keep + 127) / 255;
            _px [i] = (uint) (Math.Min (255, da) << 24 | Math.Min (255, dr) << 16 | Math.Min (255, dg) << 8 | Math.Min (255, db));
        }

        // ---- brushes ----------------------------------------------------------------------------

        /// <summary>A brush's premultiplied colour at device pixel (x, y).</summary>
        delegate uint Sampler (int x, int y);

        static byte Byte (double v) => (byte) Math.Clamp ((int) Math.Round (v * 255.0), 0, 255);

        static uint Srgb (RgbaColor c)
        {
            (double r, double g, double b, double a) = ScenePageWalker.Srgb (c);
            return (uint) Byte (a) << 24 | (uint) Byte (r) << 16 | (uint) Byte (g) << 8 | Byte (b);
        }

        Sampler SamplerFor (SceneBrush brush, (float X0, float Y0, float X1, float Y1) bounds)
        {
            Matrix3x2.Invert (_s.M, out Matrix3x2 inv);
            switch (brush) {
            case SceneSolid solid: {
                uint p = GdipPixels.PremultiplyArgb (Srgb (solid.Color));
                return (x, y) => p;
            }
            case SceneLinear lin when lin.Stops != null && lin.Stops.Length > 0: {
                uint[] ramp = Ramp (lin.Stops, lin.Bands > 0 ? lin.Bands : 255);
                Vector2 s0 = lin.Start, axis = lin.End - lin.Start;
                float len2 = axis.LengthSquared ();
                int bands = lin.Bands;
                bool corner = bands > 0;   // GDI+ samples its gradients at the pixel's integer corner
                return (x, y) => {
                    Vector2 p = Vector2.Transform (corner ? new Vector2 (x, y) : new Vector2 (x + 0.5f, y + 0.5f), inv);
                    float t = len2 > 0 ? Vector2.Dot (p - s0, axis) / len2 : 0f;
                    t = Spread (t, lin.SpreadMethod);
                    int n = ramp.Length - 1;
                    int k = bands > 0 ? Math.Clamp ((int) MathF.Floor (t * bands + 0.5f), 0, n) : Math.Clamp ((int) MathF.Round (t * n), 0, n);
                    return ramp [k];
                };
            }
            case SceneRadial rad when rad.Stops != null && rad.Stops.Length > 0: {
                uint[] ramp = Ramp (rad.Stops, 255);
                return (x, y) => {
                    Vector2 p = Vector2.Transform (new Vector2 (x + 0.5f, y + 0.5f), inv) - rad.Center;
                    float dx = rad.RadiusX > 0 ? p.X / rad.RadiusX : 0f, dy = rad.RadiusY > 0 ? p.Y / rad.RadiusY : 0f;
                    float t = Spread (MathF.Sqrt (dx * dx + dy * dy), rad.SpreadMethod);
                    return ramp [Math.Clamp ((int) MathF.Round (t * 255f), 0, 255)];
                };
            }
            case SceneImage img when img.PixelWidth > 0 && img.PixelHeight > 0 && img.PixelsRgba.Length >= img.PixelWidth * img.PixelHeight * 4:
                return ImageSampler (img, bounds, inv);
            default:
                return null;
            }
        }

        static float Spread (float t, GradientSpreadMethod m)
        {
            switch (m) {
            case GradientSpreadMethod.Repeat: return t - MathF.Floor (t);
            case GradientSpreadMethod.Reflect: { float f = t - 2f * MathF.Floor (t / 2f); return f > 1f ? 2f - f : f; }
            default: return Math.Clamp (t, 0f, 1f);
            }
        }

        // The stops' colour at k / n for k = 0..n, premultiplied, interpolated in sRGB.
        static uint[] Ramp (GradientStop[] stops, int n)
        {
            var ramp = new uint [n + 1];
            for (int k = 0; k <= n; k++) {
                float t = k / (float) n;
                uint c;
                if (t <= stops [0].Offset) c = Srgb (stops [0].Color);
                else if (t >= stops [stops.Length - 1].Offset) c = Srgb (stops [stops.Length - 1].Color);
                else {
                    c = Srgb (stops [stops.Length - 1].Color);
                    for (int i = 1; i < stops.Length; i++)
                        if (t <= stops [i].Offset) {
                            uint a = Srgb (stops [i - 1].Color), b = Srgb (stops [i].Color);
                            float span = stops [i].Offset - stops [i - 1].Offset;
                            float f = span > 0 ? (t - stops [i - 1].Offset) / span : 0f;
                            c = Lerp (a, b, f);
                            break;
                        }
                }
                ramp [k] = GdipPixels.PremultiplyArgb (c);
            }
            return ramp;
        }

        static uint Lerp (uint a, uint b, float f)
        {
            uint r = 0;
            for (int s = 0; s < 32; s += 8) {
                float va = (a >> s) & 0xff, vb = (b >> s) & 0xff;
                r |= (uint) Math.Clamp ((int) MathF.Round (va + (vb - va) * f), 0, 255) << s;
            }
            return r;
        }

        Sampler ImageSampler (SceneImage img, (float X0, float Y0, float X1, float Y1) b, Matrix3x2 inv)
        {
            int pw = img.PixelWidth, ph = img.PixelHeight;
            byte[] rgba = Original.TryGetValue (img.PixelsRgba, out byte[] drawn) ? drawn : img.PixelsRgba;
            int opacity = (int) MathF.Round (Math.Clamp (img.Opacity, 0f, 1f) * 255f);
            // Local units per image pixel, and where pixel (0,0) starts.
            float ox, oy, sx, sy;
            bool tile = img.TileMode != TileMode.None;
            if (tile) {
                ox = 0; oy = 0;
                sx = img.TileWidth > 0 ? img.TileWidth / pw : 1f;
                sy = img.TileHeight > 0 ? img.TileHeight / ph : 1f;
            } else {
                ox = b.X0; oy = b.Y0;
                sx = (b.X1 - b.X0) / pw; sy = (b.Y1 - b.Y0) / ph;
            }
            if (sx == 0 || sy == 0) return null;
            uint Texel (int u, int v)
            {
                if (tile) {
                    u %= pw; if (u < 0) u += pw;
                    v %= ph; if (v < 0) v += ph;
                } else {
                    u = Math.Clamp (u, 0, pw - 1); v = Math.Clamp (v, 0, ph - 1);
                }
                int o = (v * pw + u) * 4;
                uint c = (uint) rgba [o + 3] << 24 | (uint) rgba [o] << 16 | (uint) rgba [o + 1] << 8 | rgba [o + 2];
                if (opacity < 255) c = (c & 0xffffff) | (uint) ((rgba [o + 3] * opacity + 127) / 255) << 24;
                return GdipPixels.PremultiplyArgb (c);
            }
            // Device pixels per image pixel: one, on whole-pixel boundaries, is a copy.
            Matrix3x2 m = _s.M;
            float dsx = m.M11 * sx, dsy = m.M22 * sy;
            float dox = m.M11 * ox + m.M31, doy = m.M22 * oy + m.M32;
            bool copy = m.M12 == 0 && m.M21 == 0 && MathF.Abs (dsx - 1f) < 1e-4f && MathF.Abs (dsy - 1f) < 1e-4f
                        && MathF.Abs (dox - MathF.Round (dox)) < 1e-3f && MathF.Abs (doy - MathF.Round (doy)) < 1e-3f;
            if (copy) {
                int ix = (int) MathF.Round (dox), iy = (int) MathF.Round (doy);
                return (x, y) => Texel (x - ix, y - iy);
            }
            return (x, y) => {
                Vector2 p = Vector2.Transform (new Vector2 (x + 0.5f, y + 0.5f), inv);
                float u = (p.X - ox) / sx - 0.5f, v = (p.Y - oy) / sy - 0.5f;
                int u0 = (int) MathF.Floor (u), v0 = (int) MathF.Floor (v);
                float fu = u - u0, fv = v - v0;
                uint c00 = Texel (u0, v0), c10 = Texel (u0 + 1, v0), c01 = Texel (u0, v0 + 1), c11 = Texel (u0 + 1, v0 + 1);
                return Lerp (Lerp (c00, c10, fu), Lerp (c01, c11, fu), fv);
            };
        }

        // ---- GDI+'s own DrawString ------------------------------------------------------------

        public bool GdiPlusText (GdiPlusTextDraw draw)
        {
            Matrix3x2 m = _s.M;
            if (m.M11 != 1f || m.M22 != 1f || m.M12 != 0f || m.M21 != 0f) return false;
            TrueTypeFont font = GdipText.Face (draw.FontFamily, draw.Style);
            GdipText.Run run = draw.Run;
            if (font == null || run.Glyphs.Length == 0) return font != null;
            float[] xs = GdipText.GlyphXs (run, run.OriginX + m.M31);
            float y = run.OriginY + m.M32;
            GdipText.Levels lv;
            if (run.Mode == GdipText.HintAntiAlias || run.Mode == GdipText.HintAntiAliasGridFit)
                lv = GdipText.ComposeGrey (font, run.Glyphs, run.Em, xs, y);
            else {
                var bits = new NaturalClearType.GlyphBits [run.Glyphs.Length];
                for (int i = 0; i < bits.Length; i++) bits [i] = GdipText.Glyph (font, run.Glyphs [i], run.Em);
                lv = GdipText.Compose (bits, xs, y, run.FixedFilter);
            }
            if (lv.Width == 0 || lv.Height == 0) return true;
            int cx0 = 0, cy0 = 0, cx1 = _w, cy1 = _h;
            if (run.HasClip) {
                cx0 = (int) MathF.Ceiling (run.ClipX + m.M31); cy0 = (int) MathF.Ceiling (run.ClipY + m.M32);
                cx1 = (int) MathF.Ceiling (run.ClipX + run.ClipW + m.M31); cy1 = (int) MathF.Ceiling (run.ClipY + run.ClipH + m.M32);
            }
            int argb = draw.Argb;
            byte br = (byte) (argb >> 16), bg = (byte) (argb >> 8), bb = (byte) argb;
            int alpha = (int) MathF.Round (((argb >> 24) & 0xff) * Math.Clamp (_s.Opacity, 0f, 1f));
            if (alpha == 0) return true;
            uint ink = (uint) br << 16 | (uint) bg << 8 | bb;
            for (int row = 0; row < lv.Height; row++)
                for (int col = 0; col < lv.Width; col++) {
                    int px = lv.Left + col, py = lv.Top + row;
                    if (px < Math.Max (0, cx0) || py < Math.Max (0, cy0) || px >= Math.Min (_w, cx1) || py >= Math.Min (_h, cy1)) continue;
                    int idx = lv.Index [row * lv.Width + col];
                    if (idx == 0) continue;
                    int i = py * _w + px;
                    int clip = _s.Clip == null ? 255 : _s.Clip [i];
                    if (clip == 0) continue;
                    uint d = _px [i];
                    if ((d >> 24) == 255 && clip == 255) {
                        // Opaque paper: GDI+'s blend, exactly.
                        (byte r, byte g, byte b) = lv.Grey
                            ? GdipText.BlendGreyPixel (idx, br, bg, bb, alpha, (byte) (d >> 16), (byte) (d >> 8), (byte) d, run.Contrast)
                            : GdipText.BlendPixel (idx, br, bg, bb, alpha, (byte) (d >> 16), (byte) (d >> 8), (byte) d, run.Contrast);
                        _px [i] = 0xff000000u | (uint) r << 16 | (uint) g << 8 | b;
                        _touched [i] = true;
                        continue;
                    }
                    // Transparent or clipped paper: the levels as plain coverage.
                    int cov;
                    if (lv.Grey) cov = idx * 255 / 15;
                    else { (int l0, int l1, int l2) = GdipText.LevelsOf (idx); cov = (l0 + l1 + l2) * 255 / 18; }
                    cov = (cov * clip + 127) / 255;
                    if (cov > 0) Blend (i, GdipPixels.PremultiplyArgb ((uint) alpha << 24 | ink), cov);
                }
            return true;
        }
    }
}
