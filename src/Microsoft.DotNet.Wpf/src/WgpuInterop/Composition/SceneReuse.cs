// A WinForms window that paints again records a new scene, primitive for primitive, even when it
// draws exactly what it drew before. The damage tracker compares content by reference (that is what
// keeps it cheap), so a repaint with the same pixels was damage all the same: every WM_MOVE of
// app's main window -- DotNetBar invalidates the whole ribbon form on a move -- re-rendered the
// form's caption and borders, 117,760 px, at 10-19 ms a step, for a window whose content had not
// changed at all.
//
// So a fresh recording is matched against the window's previous scene and every part that is equal
// by VALUE is replaced by the previous object: the tracker then sees the same references and damages
// only what really changed, the renderer keeps its per-primitive caches, and a repaint that changed
// nothing returns the previous scene itself.

using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class SceneReuse
    {
        /// <summary>The scene to keep for a window that recorded <paramref name="fresh"/> after
        /// <paramref name="previous"/>: the previous scene when the two are equal, otherwise the fresh
        /// one with every equal descendant and primitive swapped for its previous counterpart.</summary>
        internal static SceneVisual Adopt(SceneVisual? previous, SceneVisual fresh)
        {
            if (previous == null || ReferenceEquals(previous, fresh)) return fresh;
            return AdoptVisual(previous, fresh);
        }

        private static SceneVisual AdoptVisual(SceneVisual o, SceneVisual n)
        {
            bool same = SameState(o, n);

            // Content: aligned on the common prefix and suffix, like the tracker's own patching.
            List<DrawingPrimitive> oc = o.Content, nc = n.Content;
            int pre = 0, max = System.Math.Min(oc.Count, nc.Count);
            while (pre < max && TakeIfSame(oc, nc, pre, pre)) pre++;
            int suf = 0;
            while (suf < max - pre && TakeIfSame(oc, nc, oc.Count - 1 - suf, nc.Count - 1 - suf)) suf++;
            if (oc.Count != nc.Count || pre + suf < nc.Count) same = false;

            // Children: by position, likewise from both ends.
            List<SceneVisual> ok = o.Children, nk = n.Children;
            int kmax = System.Math.Min(ok.Count, nk.Count);
            int kp = 0;
            for (; kp < kmax; kp++)
            {
                SceneVisual a = AdoptVisual(ok[kp], nk[kp]);
                nk[kp] = a;
                if (!ReferenceEquals(a, ok[kp])) break;
            }
            int ks = 0;
            for (; ks < kmax - kp - 1; ks++)
            {
                int oi = ok.Count - 1 - ks, ni = nk.Count - 1 - ks;
                SceneVisual a = AdoptVisual(ok[oi], nk[ni]);
                nk[ni] = a;
                if (!ReferenceEquals(a, ok[oi])) break;
            }
            if (ok.Count != nk.Count) same = false;
            else
                for (int i = 0; i < nk.Count && same; i++)
                    same = ReferenceEquals(ok[i], nk[i]);

            return same ? o : n;
        }

        private static bool TakeIfSame(List<DrawingPrimitive> oc, List<DrawingPrimitive> nc, int oi, int ni)
        {
            DrawingPrimitive a = oc[oi], b = nc[ni];
            if (ReferenceEquals(a, b)) return true;
            if (!SamePrimitive(a, b)) return false;
            nc[ni] = a;
            return true;
        }

        private static bool SameState(SceneVisual a, SceneVisual b)
            => a.Id == b.Id && a.Offset == b.Offset && a.Transform == b.Transform && a.Opacity == b.Opacity
            && SameRect(a.Clip, b.Clip) && SameGeometry(a.ClipGeometry, b.ClipGeometry)
            && ReferenceEquals(a.Effect, b.Effect) && SameBrush(a.OpacityMask, b.OpacityMask)
            && a.BitmapCached == b.BitmapCached && SameSnapshot(a.Snapshot, b.Snapshot)
            && a.AliasedEdges == b.AliasedEdges && a.NearestBitmapScaling == b.NearestBitmapScaling
            && SameFloats(a.GuidelinesX, b.GuidelinesX) && SameFloats(a.GuidelinesY, b.GuidelinesY);

        private static bool SameSnapshot(SceneSnapshot? a, SceneSnapshot? b)
            => ReferenceEquals(a, b) || (a != null && b != null && SameRect(a.Source, b.Source) && SameRect(a.Dest, b.Dest)
               && a.GdiStretch == b.GdiStretch && a.WindowBlend == b.WindowBlend);

        internal static bool SamePrimitive(DrawingPrimitive? a, DrawingPrimitive? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType() || a.SourceCopy != b.SourceCopy) return false;
            switch (a)
            {
                case GeometryFill f:
                {
                    var g = (GeometryFill)b;
                    return f.IsGlyph == g.IsGlyph && f.PixelAligned == g.PixelAligned && f.BaselineAnchor == g.BaselineAnchor
                        && SameBrush(f.Brush, g.Brush) && SameGeometry(f.Geometry, g.Geometry);
                }
                case GeometryStroke s:
                {
                    var t = (GeometryStroke)b;
                    return SameStyle(s.Style, t.Style) && SameBrush(s.Brush, t.Brush) && SameGeometry(s.Geometry, t.Geometry);
                }
                case GeometryDrawing d:
                {
                    var e = (GeometryDrawing)b;
                    return SameStyle(d.StrokeStyle, e.StrokeStyle) && SameBrush(d.Fill, e.Fill) && SameBrush(d.Stroke, e.Stroke)
                        && SameGeometry(d.Geometry, e.Geometry);
                }
                case GdiPlusTextDraw p:
                {
                    var q = (GdiPlusTextDraw)b;
                    return p.FontFamily == q.FontFamily && p.Style == q.Style && p.Argb == q.Argb
                        && SameRun(p.Run, q.Run) && SamePrimitive(p.Fallback, q.Fallback);
                }
                case GlyphRunDraw r:
                {
                    var s2 = (GlyphRunDraw)b;
                    return r.Text == s2.Text && r.Origin == s2.Origin && r.EmSize == s2.EmSize && SameColor(r.Color, s2.Color)
                        && r.Simulations == s2.Simulations && r.FontFamily == s2.FontFamily;
                }
                case WpfTextRunDraw w:
                {
                    var x = (WpfTextRunDraw)b;
                    if (!ReferenceEquals(w.Font, x.Font) || w.EmSize != x.EmSize || w.Origin != x.Origin || !SameColor(w.Color, x.Color)
                        || w.Display != x.Display || !SameRect(w.ClipRect, x.ClipRect) || w.Mirror != x.Mirror
                        || w.SourceFamily != x.SourceFamily || w.SourceStyle != x.SourceStyle || w.Characters != x.Characters
                        || !SameUShorts(w.Glyphs, x.Glyphs) || !SameFloats(w.X, x.X) || !SameFloats(w.Y, x.Y)
                        || !SameFloats(w.RtlNominal, x.RtlNominal) || !SameInts(w.GlyphClusters, x.GlyphClusters)
                        || w.Fallback.Count != x.Fallback.Count) return false;
                    for (int i = 0; i < w.Fallback.Count; i++)
                        if (!SamePrimitive(w.Fallback[i], x.Fallback[i])) return false;
                    return true;
                }
                default:
                    // Nested visuals and 3-D content are compared by reference only.
                    return false;
            }
        }

        private static bool SameRun(Text.GdiPlusText.Run a, Text.GdiPlusText.Run b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.OriginX != b.OriginX || a.OriginY != b.OriginY || a.RoundOrigin != b.RoundOrigin || a.LeadOffset != b.LeadOffset
                || a.Lead != b.Lead || a.LastAdvance != b.LastAdvance || a.HasClip != b.HasClip
                || a.ClipX != b.ClipX || a.ClipY != b.ClipY || a.ClipW != b.ClipW || a.ClipH != b.ClipH
                || a.Em != b.Em || a.Mode != b.Mode || a.Hint != b.Hint || a.FixedFilter != b.FixedFilter
                || a.Contrast != b.Contrast || a.Sx != b.Sx || a.Sy != b.Sy) return false;
            // Precomputed levels are compared by their key: the same key is the same coverage.
            if ((a.Pre == null) != (b.Pre == null)
                || (a.Pre != null && (a.PreKey != b.PreKey || (a.PreKey == 0 && !ReferenceEquals(a.Pre, b.Pre))))) return false;
            return SameUShorts(a.Glyphs, b.Glyphs) && SameFloats(a.Advances, b.Advances);
        }

        private static bool SameStyle(StrokeStyle a, StrokeStyle b)
            => a.Thickness == b.Thickness && a.Cap == b.Cap && a.Join == b.Join && a.MiterLimit == b.MiterLimit
            && a.DashOffset == b.DashOffset && SameDoubles(a.DashArray, b.DashArray);

        private static bool SameColor(RgbaColor a, RgbaColor b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

        internal static bool SameBrush(Brush? a, Brush? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            switch (a)
            {
                case SolidColorBrush s: return SameColor(s.Color, ((SolidColorBrush)b).Color);
                case LinearGradientBrush l:
                {
                    var m = (LinearGradientBrush)b;
                    return l.Start == m.Start && l.End == m.End && l.SpreadMethod == m.SpreadMethod && l.Bands == m.Bands && SameStops(l.Stops, m.Stops);
                }
                case RadialGradientBrush r:
                {
                    var s2 = (RadialGradientBrush)b;
                    return r.Center == s2.Center && r.RadiusX == s2.RadiusX && r.RadiusY == s2.RadiusY && r.SpreadMethod == s2.SpreadMethod
                        && SameStops(r.Stops, s2.Stops);
                }
                case ImageBrush i:
                {
                    var j = (ImageBrush)b;
                    // The pixels by reference: an unchanged bitmap hands the recorder the same array.
                    return i.SamePixels(j) && i.PixelWidth == j.PixelWidth && i.PixelHeight == j.PixelHeight
                        && i.TileMode == j.TileMode && i.TileWidth == j.TileWidth && i.TileHeight == j.TileHeight && i.Opacity == j.Opacity
                        && ReferenceEquals(i.SourceVisual, j.SourceVisual) && i.SourceId == j.SourceId
                        && i.SourceTexW == j.SourceTexW && i.SourceTexH == j.SourceTexH
                        && i.U0 == j.U0 && i.V0 == j.V0 && i.U1 == j.U1 && i.V1 == j.V1;
                }
                default: return false;
            }
        }

        private static bool SameStops(GradientStop[] a, GradientStop[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Offset != b[i].Offset || !SameColor(a[i].Color, b[i].Color)) return false;
            return true;
        }

        internal static bool SameGeometry(Geometry? a, Geometry? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            switch (a)
            {
                case RectangleGeometry r: return SameRect(r.Rect, ((RectangleGeometry)b).Rect);
                case RoundedRectangleGeometry q:
                {
                    var s = (RoundedRectangleGeometry)b;
                    return SameRect(q.Rect, s.Rect) && q.RadiusX == s.RadiusX && q.RadiusY == s.RadiusY;
                }
                case EllipseGeometry e:
                {
                    var f = (EllipseGeometry)b;
                    return e.Center == f.Center && e.RadiusX == f.RadiusX && e.RadiusY == f.RadiusY;
                }
                case PolygonGeometry p: return SamePoints(p.Points, ((PolygonGeometry)b).Points);
                case CombinedGeometry c:
                {
                    var d = (CombinedGeometry)b;
                    return c.Mode == d.Mode && SameGeometry(c.Geometry1, d.Geometry1) && SameGeometry(c.Geometry2, d.Geometry2);
                }
                case GeometryGroup g:
                {
                    var h = (GeometryGroup)b;
                    if (g.FillRule != h.FillRule || g.Children.Count != h.Children.Count) return false;
                    for (int i = 0; i < g.Children.Count; i++)
                        if (!SameGeometry(g.Children[i], h.Children[i])) return false;
                    return true;
                }
                case PathGeometry pg:
                {
                    var ph = (PathGeometry)b;
                    if (pg.FillRule != ph.FillRule || pg.Figures.Count != ph.Figures.Count) return false;
                    for (int i = 0; i < pg.Figures.Count; i++)
                        if (!SameFigure(pg.Figures[i], ph.Figures[i])) return false;
                    return true;
                }
                default: return false;
            }
        }

        private static bool SameFigure(PathFigure a, PathFigure b)
        {
            if (a.Start != b.Start || a.Closed != b.Closed || a.Filled != b.Filled || a.ImpliedStart != b.ImpliedStart || a.Segments.Count != b.Segments.Count) return false;
            for (int i = 0; i < a.Segments.Count; i++)
            {
                PathSegment s = a.Segments[i], t = b.Segments[i];
                if (s.GetType() != t.GetType()) return false;
                switch (s)
                {
                    case LineSegment l when l.Point == ((LineSegment)t).Point: break;
                    case QuadraticBezierSegment q when q.Control == ((QuadraticBezierSegment)t).Control
                        && q.Point == ((QuadraticBezierSegment)t).Point && q.ImpliedEnd == ((QuadraticBezierSegment)t).ImpliedEnd: break;
                    case CubicBezierSegment c when c.Control1 == ((CubicBezierSegment)t).Control1
                        && c.Control2 == ((CubicBezierSegment)t).Control2 && c.Point == ((CubicBezierSegment)t).Point: break;
                    default: return false;
                }
            }
            return true;
        }

        private static bool SameRect(Rect a, Rect b) => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
        private static bool SameRect(Rect? a, Rect? b) => a.HasValue == b.HasValue && (!a.HasValue || SameRect(a.Value, b!.Value));

        private static bool SamePoints(Vector2[] a, Vector2[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameFloats(float[]? a, float[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return System.MemoryExtensions.SequenceEqual<float>(a, b);
        }

        private static bool SameDoubles(double[]? a, double[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return System.MemoryExtensions.SequenceEqual<double>(a, b);
        }

        private static bool SameUShorts(ushort[]? a, ushort[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return System.MemoryExtensions.SequenceEqual<ushort>(a, b);
        }

        private static bool SameInts(int[]? a, int[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return System.MemoryExtensions.SequenceEqual<int>(a, b);
        }
    }
}
