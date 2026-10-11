// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A recorded page, walked for a device that draws VECTORS: the PDF writer (every head but Windows)
// and the printer DC (Windows). Both want the same things out of a scene -- its transforms and clips
// as a state stack, its shapes as paths of lines and Beziers, its text as runs of glyphs of a font --
// and differ only in the verbs they put down, so the walk is here and each device is an
// IPageSink.
//
// The scene is what Graphics recorded (SceneRecorder): containers carry an offset or transform, a
// rectangle or path clip, an opacity, or a snapshot (a picture of a source rectangle stretched into a
// destination); content draws before children. Text arrives three ways -- a printed DrawString's
// placed glyphs (WpfTextRunDraw), a screen GDI+ DrawString's fast-imager layout (GdiPlusTextDraw),
// and a plain string run (GlyphRunDraw) -- and each is turned into the one PageText a device needs.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using SceneBrush = Microsoft.Wpf.Interop.WebGpu.Composition.Brush;

namespace System.Drawing.WebGpuBackend
{
    /// <summary>A path as a device draws it: figures of lines and cubic Beziers.</summary>
    internal sealed class PagePath
    {
        internal enum Op : byte { Move, Line, Cubic, Close }

        internal readonly List<Op> Ops = new();
        internal readonly List<Vector2> Points = new();   // Move/Line: 1, Cubic: 3, Close: 0
        internal bool EvenOdd;
        /// <summary>A clip that is a printed page's clip region: its device rectangles (l, t, r, b).</summary>
        internal int[] DeviceRects;
        /// <summary>A clip that is a fill's own path: its device form as GDI is to be handed it.</summary>
        internal GdiShape GdiClip;

        internal bool IsEmpty => Ops.Count == 0;

        internal void MoveTo(Vector2 p) { Ops.Add(Op.Move); Points.Add(p); }
        internal void LineTo(Vector2 p) { Ops.Add(Op.Line); Points.Add(p); }
        internal void CubicTo(Vector2 a, Vector2 b, Vector2 c) { Ops.Add(Op.Cubic); Points.Add(a); Points.Add(b); Points.Add(c); }
        internal void Close() => Ops.Add(Op.Close);

        internal (float X0, float Y0, float X1, float Y1) Bounds()
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector2 p in Points)
            {
                x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y);
                x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y);
            }
            return (x0, y0, x1, y1);
        }

        private const float Kappa = 0.5522847498f;

        internal static PagePath Of(Geometry g)
        {
            var p = new PagePath();
            p.Append(g);
            return p;
        }

        internal void Append(Geometry g)
        {
            switch (g)
            {
                case RectangleGeometry r:
                    Rect(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height);
                    break;
                case PolygonGeometry poly when poly.Points.Length > 0:
                    MoveTo(poly.Points[0]);
                    for (int i = 1; i < poly.Points.Length; i++) LineTo(poly.Points[i]);
                    Close();
                    break;
                case EllipseGeometry e:
                    Ellipse(e.Center.X, e.Center.Y, e.RadiusX, e.RadiusY);
                    break;
                case RoundedRectangleGeometry rr:
                    Rounded(rr.Rect, rr.RadiusX, rr.RadiusY);
                    break;
                case PathGeometry path:
                    EvenOdd = path.FillRule == FillRule.EvenOdd;
                    foreach (PathFigure f in path.Figures)
                    {
                        MoveTo(f.Start);
                        Vector2 cur = f.Start;
                        foreach (PathSegment s in f.Segments)
                        {
                            switch (s)
                            {
                                case LineSegment l: LineTo(l.Point); cur = l.Point; break;
                                case CubicBezierSegment c: CubicTo(c.Control1, c.Control2, c.Point); cur = c.Point; break;
                                case QuadraticBezierSegment q:
                                    CubicTo(cur + (q.Control - cur) * (2f / 3f), q.Point + (q.Control - q.Point) * (2f / 3f), q.Point);
                                    cur = q.Point;
                                    break;
                            }
                        }
                        if (f.Closed) Close();
                    }
                    break;
                case GeometryGroup group:
                    EvenOdd = group.FillRule == FillRule.EvenOdd;
                    foreach (Geometry child in group.Children) Append(child);
                    break;
                case CombinedGeometry combined:
                    // Union and Xor read correctly as the two outlines under even-odd for the shapes
                    // System.Drawing records (disjoint or nested); Intersect is handled by the walker
                    // as a clip. Exclude keeps the first outline only.
                    Append(combined.Geometry1);
                    if (combined.Mode != GeometryCombineMode.Exclude && combined.Mode != GeometryCombineMode.Intersect)
                    {
                        Append(combined.Geometry2);
                        if (combined.Mode == GeometryCombineMode.Xor) EvenOdd = true;
                    }
                    break;
            }
        }

        internal void Rect(float x, float y, float w, float h)
        {
            MoveTo(new Vector2(x, y)); LineTo(new Vector2(x + w, y)); LineTo(new Vector2(x + w, y + h)); LineTo(new Vector2(x, y + h)); Close();
        }

        internal void Ellipse(float cx, float cy, float rx, float ry)
        {
            float kx = rx * Kappa, ky = ry * Kappa;
            MoveTo(new Vector2(cx + rx, cy));
            CubicTo(new Vector2(cx + rx, cy + ky), new Vector2(cx + kx, cy + ry), new Vector2(cx, cy + ry));
            CubicTo(new Vector2(cx - kx, cy + ry), new Vector2(cx - rx, cy + ky), new Vector2(cx - rx, cy));
            CubicTo(new Vector2(cx - rx, cy - ky), new Vector2(cx - kx, cy - ry), new Vector2(cx, cy - ry));
            CubicTo(new Vector2(cx + kx, cy - ry), new Vector2(cx + rx, cy - ky), new Vector2(cx + rx, cy));
            Close();
        }

        private void Rounded(Microsoft.Wpf.Interop.WebGpu.Composition.Rect r, float rx, float ry)
        {
            rx = MathF.Min(rx, r.Width / 2f); ry = MathF.Min(ry, r.Height / 2f);
            if (rx <= 0 || ry <= 0) { Rect(r.X, r.Y, r.Width, r.Height); return; }
            float x0 = r.X, y0 = r.Y, x1 = r.X + r.Width, y1 = r.Y + r.Height, kx = rx * Kappa, ky = ry * Kappa;
            MoveTo(new Vector2(x0 + rx, y0));
            LineTo(new Vector2(x1 - rx, y0));
            CubicTo(new Vector2(x1 - rx + kx, y0), new Vector2(x1, y0 + ry - ky), new Vector2(x1, y0 + ry));
            LineTo(new Vector2(x1, y1 - ry));
            CubicTo(new Vector2(x1, y1 - ry + ky), new Vector2(x1 - rx + kx, y1), new Vector2(x1 - rx, y1));
            LineTo(new Vector2(x0 + rx, y1));
            CubicTo(new Vector2(x0 + rx - kx, y1), new Vector2(x0, y1 - ry + ky), new Vector2(x0, y1 - ry));
            LineTo(new Vector2(x0, y0 + ry));
            CubicTo(new Vector2(x0, y0 + ry - ky), new Vector2(x0 + rx - kx, y0), new Vector2(x0 + rx, y0));
            Close();
        }
    }

    /// <summary>A run of text as a device needs it: glyphs of one face, placed, in local units.</summary>
    internal sealed class PageText
    {
        public TrueTypeFont Face;
        public string Family;          // the family the run was asked for, for a device that names fonts
        public int Style;              // 1 bold, 2 italic, as asked
        public float Em;
        public ushort[] Glyphs;
        public float[] X, Y;           // each glyph's origin, absolute in local units (y down, on the baseline)
        public string[] Text;          // per glyph, the characters it stands for (or null)
        public RgbaColor Color;
        public List<DrawingPrimitive> Outlines;   // the same run as filled shapes, for a device that cannot do text
    }

    /// <summary>The verbs a vector device puts a page down with. Coordinates are local to the
    /// current transform, which <see cref="Concat"/> builds up inside Save/Restore pairs.</summary>
    internal interface IPageSink
    {
        void Save();
        void Restore();
        void Concat(Matrix3x2 m);
        void ClipRect(float x, float y, float w, float h);
        void ClipPath(PagePath path);
        void Opacity(float alpha);
        void Fill(PagePath path, SceneBrush brush);
        void Stroke(PagePath path, SceneBrush brush, StrokeStyle style);
        /// <summary>False when the run cannot be drawn as text; its outlines are then filled.</summary>
        bool Text(PageText run);

        /// <summary>The compositing mode of the primitive about to be emitted
        /// (CompositingMode.SourceCopy replaces the destination). Only a raster device cares.</summary>
        void SourceCopy(bool on) { }

        /// <summary>A screen DrawString laid out by GDI+'s fast imager, offered whole before it is
        /// turned into glyphs: a raster device can draw it as GDI+ does. False to take the glyphs.</summary>
        bool GdiPlusText(GdiPlusTextDraw draw) => false;
    }

    internal static class ScenePageWalker
    {
        internal static void Walk(SceneVisual root, IPageSink sink) => Visit(root, sink);

        // Clips a picture of the page needs and the page devices do not (SetPreviewClipPath).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PathGeometry, object> s_previewOnly = new();

        internal static void MarkPreviewOnly(PathGeometry g) => s_previewOnly.AddOrUpdate(g, s_previewOnly);

        // A page's clip region in device rectangles (SetRegionClip), for a device that clips as GDI does.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PathGeometry, int[]> s_regionClips = new();

        internal static void MarkRegionClip(PathGeometry g, int[] deviceRects) => s_regionClips.AddOrUpdate(g, deviceRects);

        // A fill's path clip as ConvertPathToGdi gives it to GDI (SetGdiPathClip).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PathGeometry, GdiShape> s_gdiPathClips = new();

        internal static void MarkGdiPathClip(PathGeometry g, GdiShape s) => s_gdiPathClips.AddOrUpdate(g, s);

        private static void Visit(SceneVisual v, IPageSink sink)
        {
            sink.Save();
            Matrix3x2 local = v.LocalToParent;
            if (!local.IsIdentity) sink.Concat(local);
            if (v.Snapshot is SceneSnapshot snap)
            {
                // A picture of Source drawn 1:1, stretched into Dest: as vectors, the transform
                // that maps one onto the other and a clip to what the picture covered.
                Microsoft.Wpf.Interop.WebGpu.Composition.Rect s = snap.Source, d = snap.Dest;
                if (s.Width > 0 && s.Height > 0)
                {
                    sink.ClipRect(d.X, d.Y, d.Width, d.Height);
                    sink.Concat(Matrix3x2.CreateTranslation(-s.X, -s.Y)
                              * Matrix3x2.CreateScale(d.Width / s.Width, d.Height / s.Height)
                              * Matrix3x2.CreateTranslation(d.X, d.Y));
                    sink.ClipRect(s.X, s.Y, s.Width, s.Height);
                }
            }
            if (v.Clip is Microsoft.Wpf.Interop.WebGpu.Composition.Rect clip) sink.ClipRect(clip.X, clip.Y, clip.Width, clip.Height);
            if (v.ClipGeometry is PathGeometry cg && !s_previewOnly.TryGetValue(cg, out _))
            {
                PagePath clipPath = PagePath.Of(cg);
                if (s_regionClips.TryGetValue(cg, out int[] rects)) clipPath.DeviceRects = rects;
                if (s_gdiPathClips.TryGetValue(cg, out GdiShape shape)) clipPath.GdiClip = shape;
                sink.ClipPath(clipPath);
            }
            if (v.Opacity < 1.0) sink.Opacity((float)v.Opacity);
            foreach (DrawingPrimitive p in v.Content) Emit(p, sink);
            foreach (SceneVisual child in v.Children) Visit(child, sink);
            sink.Restore();
        }

        private static void Emit(DrawingPrimitive p, IPageSink sink)
        {
            sink.SourceCopy(p.SourceCopy);
            switch (p)
            {
                case GeometryFill fill:
                    if (fill.Geometry is CombinedGeometry { Mode: GeometryCombineMode.Intersect } both)
                    {
                        sink.Save();
                        sink.ClipPath(PagePath.Of(both.Geometry2));
                        sink.Fill(PagePath.Of(both.Geometry1), fill.Brush);
                        sink.Restore();
                    }
                    else sink.Fill(PagePath.Of(fill.Geometry), fill.Brush);
                    break;
                case GeometryStroke stroke:
                    sink.Stroke(PagePath.Of(stroke.Geometry), stroke.Brush, stroke.Style);
                    break;
                case GeometryDrawing drawing:
                    if (drawing.Fill != null) sink.Fill(PagePath.Of(drawing.Geometry), drawing.Fill);
                    if (drawing.Stroke != null) sink.Stroke(PagePath.Of(drawing.Geometry), drawing.Stroke, drawing.StrokeStyle);
                    break;
                case NestedVisualDraw nested:
                    Visit(nested.Visual, sink);
                    break;
                case WpfTextRunDraw run:
                    if (!sink.Text(FromPlaced(run)))
                        foreach (DrawingPrimitive f in run.Fallback) Emit(f, sink);
                    break;
                case GdiPlusTextDraw gdiPlus when sink.GdiPlusText(gdiPlus):
                    break;
                case GdiPlusTextDraw gdiPlus:
                    if (FromGdiPlus(gdiPlus) is not PageText g || !sink.Text(g))
                        Emit(gdiPlus.Fallback, sink);
                    break;
                case GlyphRunDraw glyphs:
                    if (FromString(glyphs) is PageText t && !sink.Text(t))
                        foreach (DrawingPrimitive f in t.Outlines) Emit(f, sink);
                    break;
            }
        }

        private static PageText FromPlaced(WpfTextRunDraw run)
        {
            int n = run.Glyphs.Length;
            var t = new PageText
            {
                Face = run.Font, Family = run.SourceFamily, Style = run.SourceStyle, Em = run.EmSize,
                Glyphs = run.Glyphs, X = new float[n], Y = new float[n], Color = run.Color, Outlines = run.Fallback,
            };
            for (int i = 0; i < n; i++) { t.X[i] = run.Origin.X + run.X[i]; t.Y[i] = run.Origin.Y + run.Y[i]; }
            t.Text = TextOf(run.Characters, run.GlyphClusters, n);
            return t;
        }

        // Per glyph, the characters of the cluster it begins.
        internal static string[] TextOf(string chars, int[] clusters, int glyphs)
        {
            if (string.IsNullOrEmpty(chars)) return null;
            var text = new string[glyphs];
            if (clusters == null || clusters.Length != glyphs)
            {
                if (chars.Length != glyphs) return null;
                for (int i = 0; i < glyphs; i++) text[i] = chars[i].ToString();
                return text;
            }
            for (int i = 0; i < glyphs; i++)
            {
                int start = clusters[i];
                if (i > 0 && clusters[i - 1] == start) continue;
                int end = chars.Length;
                for (int k = i + 1; k < glyphs; k++) if (clusters[k] != start) { end = clusters[k]; break; }
                if (start >= 0 && start < chars.Length && end > start) text[i] = chars.Substring(start, end - start);
            }
            return text;
        }

        private static PageText FromGdiPlus(GdiPlusTextDraw draw)
        {
            GdiPlusText.Run run = draw.Run;
            TrueTypeFont face = GdiPlusText.Face(draw.FontFamily, draw.Style);
            if (face == null || run.Glyphs.Length == 0) return null;
            float[] xs = GdiPlusText.GlyphXs(run, run.OriginX);
            int n = run.Glyphs.Length;
            var ys = new float[n];
            for (int i = 0; i < n; i++) ys[i] = run.OriginY;
            string text = draw.Fallback.Text;
            return new PageText
            {
                Face = face, Family = draw.FontFamily, Style = draw.Style, Em = run.Em, Glyphs = run.Glyphs,
                X = xs, Y = ys, Color = draw.Fallback.Color,
                Text = text != null && text.Length == n ? TextOf(text, null, n) : null,
                Outlines = new List<DrawingPrimitive> { draw.Fallback },
            };
        }

        // A plain string run: laid out from the face's design advances, as PrintText lays out a page.
        private static PageText FromString(GlyphRunDraw run)
        {
            int style = run.Simulations & 3;
            TrueTypeFont face = PrintText.Face(run.FontFamily ?? "Segoe UI", style);
            if (face == null || string.IsNullOrEmpty(run.Text)) return null;
            float unit = run.EmSize / face.UnitsPerEmForHinting;
            var glyphs = new List<ushort>(); var xs = new List<float>(); var text = new List<string>();
            float pen = run.Origin.X;
            foreach (char c in run.Text)
            {
                int g = char.IsSurrogate(c) ? 0 : Math.Max(0, face.GlyphIndex(c));
                if (c != ' ' && c != '\t') { glyphs.Add((ushort)g); xs.Add(pen); text.Add(c.ToString()); }
                pen += face.DesignAdvance(g) * unit;
            }
            int n = glyphs.Count;
            var ys = new float[n];
            for (int i = 0; i < n; i++) ys[i] = run.Origin.Y;
            var outlines = new List<DrawingPrimitive>();
            var fills = new List<GlyphFill>();
            float scale = run.EmSize / face.PixelsPerEm;
            for (int i = 0; i < n; i++)
            {
                fills.Clear();
                GlyphRunPainter.Paint(face, face, glyphs[i], scale, xs[i], ys[i], fills);
                foreach (GlyphFill gf in fills)
                    outlines.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, gf.Figures), new SolidColorBrush(run.Color)));
            }
            return new PageText
            {
                Face = face, Family = run.FontFamily, Style = style, Em = run.EmSize, Glyphs = glyphs.ToArray(),
                X = xs.ToArray(), Y = ys, Color = run.Color, Text = text.ToArray(), Outlines = outlines,
            };
        }

        /// <summary>A scene colour as the sRGB bytes it was recorded from (the recorder linearises
        /// colours when the compositor blends in linear light).</summary>
        internal static (double R, double G, double B, double A) Srgb(RgbaColor c)
        {
            if (WgpuSceneRenderer.s_gammaComposite) return (c.R, c.G, c.B, c.A);
            return (Encode(c.R), Encode(c.G), Encode(c.B), c.A);
        }

        private static double Encode(float l)
            => l <= 0.0031308f ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
    }
}
