// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPen
    {
        /// <summary>GpPath::Widen @18008ab68 (GraphicsPath.Widen): the widened path in the space
        /// <paramref name="matrix"/> maps to, winding; null when GDI+ fails it. <paramref name="solid"/>
        /// drops the dashes first, as GpPath::IsOutlineVisible @180089ce0 does.</summary>
        public static partial GpPath Widen (GpPath path, Pen pen, GpMatrix matrix, float flatness, bool solid)
        {
            DpPen dp = DpPen.From (pen);
            if (solid) { dp.DashStyle = 0; dp.DashArray = null; }
            GpPath r = GetWidenedPath (path, dp, matrix, flatness, 96f);
            if (r != null) r.FillMode = FillMode.Winding;
            return r;
        }

        /// <summary>GdipGetPathWorldBounds with a pen: GpPath::GetBounds @18001c020, the control
        /// box grown by the pen's reach (not the widened outline).</summary>
        public static partial RectangleF WidenedBounds (GpPath path, Pen pen, GpMatrix? matrix)
            => GpStroke.Bounds (path, matrix, DpPen.From (pen), 96f);

        /// <summary>GpPath::GetWidenedPath @180089680: the outline of the stroke, in the space
        /// <paramref name="m"/> maps to (device space for drawing). An Inset pen widens each closed
        /// figure at twice the width and keeps what lies inside the path.</summary>
        public static GpPath GetWidenedPath (GpPath path, DpPen pen, GpMatrix? m, float flatness, float dpi)
        {
            GpMatrix mm = m ?? GpMatrix.CreateIdentity ();
            if (pen.Alignment != 1) return GetWidenedPathInternal (path, pen, mm, flatness, false);
            return InsetWidenedPath (path, pen, mm, flatness);
        }

        /// <summary>GetWidenedPath's Inset branch: each figure widened on its own with a Center pen
        /// (an open one at the pen's width; a closed one at twice it, compound bands halved and
        /// mirrored, the widener in its inset mode), a closed one then kept only where it lies inside
        /// the figure: both rasterized as regions in device space and intersected (DpRegion::And),
        /// the region's pixels returned as the outline (GpPath(DpRegion) + ConvertRegionOutputToWinding
        /// give the same pixel set).</summary>
        static GpPath InsetWidenedPath (GpPath path, DpPen pen, GpMatrix m, float flatness)
        {
            DpPen closedPen = pen.Clone (), openPen = pen.Clone ();
            closedPen.Alignment = 0; openPen.Alignment = 0;
            closedPen.Width = closedPen.Width * 2f;
            int cc = pen.CompoundCount;
            if (cc > 0) {
                var c = new float [cc * 2];
                for (int i = 0; i < cc; i++) {
                    c [i] = pen.Compound [i] * 0.5f;
                    c [cc * 2 - 1 - i] = 1f - pen.Compound [i] * 0.5f;
                }
                closedPen.Compound = c;
            }
            var result = new GpPath (FillMode.Winding);
            int n = path.Count, s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (path.Types [e + 1] & 7) != 0) e++;
                int cnt = e - s + 1;
                var sub = new GpPath (path.Points.GetRange (s, cnt).ToArray (), path.Types.GetRange (s, cnt).ToArray (), path.FillMode);
                bool closed = (path.Types [e] & 0x80) != 0;
                GpPath w = GetWidenedPathInternal (sub, closed ? closedPen : openPen, m, flatness, closed);
                if (w == null) return null;
                if (!closed) result.AddPath (w, false);
                else {
                    // Both paths are marked as curved (+0x18) for DpRegion::Set, so GetFlattenedPath
                    // re-enumerates them even when they are lines: every point lands on 1/256 pixel.
                    GpPath fw = w.Clone (); fw.HasBezier = true; fw.Flatten (null, 0.25f);
                    GpPath fs = sub.Clone (); fs.HasBezier = true; fs.Flatten (m, 0.25f);
                    DpRegion ra = GpRegionRaster.FromPath (fw.PointArray (), fw.TypeArray (), FillMode.Winding, GpMatrix.CreateIdentity ());
                    DpRegion rb = GpRegionRaster.FromPath (fs.PointArray (), fs.TypeArray (), sub.FillMode, GpMatrix.CreateIdentity ());
                    DpRegion r = DpRegion.Combine (ra, rb, DpRegion.Op.And);
                    GpPath rp = GpRegionToPath.Convert (r);
                    if (rp != null && rp.Count > 0) result.AddPath (GpRegionToPath.ToWinding (rp), false);
                }
                s = e + 1;
            }
            return result;
        }

        /// <summary>GpPath::GetWidenedPathInternal @18001c6e0.</summary>
        static GpPath GetWidenedPathInternal (GpPath path, DpPen pen, GpMatrix m, float flatness, bool inset)
        {
            GpMatrix inv = m;
            if (!inv.Invert ()) return null;
            GpPath p = path.Clone ();
            // Flattened (or transformed) in device space, then brought back.
            p.Flatten (m, flatness);
            if (p.Count > 0 && inv.Complexity != 0) {
                for (int i = 0; i < p.Count; i++) p.Points [i] = inv.Transform (p.Points [i]);
            }
            GpPath caps = null;
            if (pen.StartCap == 0xff || pen.EndCap == 0xff || (pen.StartCap & 0xf0) != 0 || (pen.EndCap & 0xf0) != 0) {
                var cc = new GpEndCapCreator (p, pen, m);
                caps = cc.CreateCapPath ();
                GpEndCapCreator.EraseMarkedSegments (p);
            }
            if (pen.DashStyle != 0 && p.Count > 0) {
                GpPath d = CreateDashedPath (p, pen, m, 0f, 0f, inset ? 0.5f : 1f, true);
                if (d == null) return null;
                p = d;
            }
            GpPath wide;
            if (p.Count < 1) wide = caps;
            else {
                var w = new GpPathWidener (p, pen, m, 0f, 0f, inset);
                wide = w.Widen ();
            }
            if (wide == null) return null;
            if (caps != null) wide.AddPath (caps, false);
            if (wide.Count > 0) wide.Transform (m);
            return wide;
        }

    }

    internal static partial class GpPathWarp
    {
        // GpPath::WarpAndFlattenSelf: the path flattened (through the matrix), then every point carried
        // from the source rectangle onto the destination quadrilateral -- upper-left, upper-right,
        // lower-left and lower-right; three points make it a parallelogram -- projectively
        // (Perspective) or bilinearly (Bilinear). A mode GDI+ does not know leaves the path empty.
        public static partial void Warp (GpPath path, GpMatrix? matrix, PointF[] dest, RectangleF src, WarpMode mode, float flatness)
        {
            if (path.Count == 0) return;
            if (mode != WarpMode.Perspective && mode != WarpMode.Bilinear) {
                path.Points.Clear ();
                path.Types.Clear ();
                path.Revalidate ();
                return;
            }
            path.Flatten (matrix, flatness);
            if (dest.Length == 0) return;
            PointF p0 = dest [0];
            PointF p1 = dest.Length > 1 ? dest [1] : new PointF (p0.X + src.Width, p0.Y);
            PointF p2 = dest.Length > 2 ? dest [2] : new PointF (p0.X, p0.Y + src.Height);
            PointF p3 = dest.Length > 3 ? dest [3] : new PointF (p1.X + p2.X - p0.X, p1.Y + p2.Y - p0.Y);
            bool perspective = mode == WarpMode.Perspective && dest.Length > 3;
            double a = 0, b = 0, c = 0, d = 0, e = 0, f = 0, g = 0, h = 0;
            if (perspective) {
                // The unit square onto the quadrilateral (Heckbert's square-to-quad mapping).
                double sx = p0.X - p1.X + p3.X - p2.X, sy = p0.Y - p1.Y + p3.Y - p2.Y;
                double dx1 = p1.X - p3.X, dx2 = p2.X - p3.X, dy1 = p1.Y - p3.Y, dy2 = p2.Y - p3.Y;
                double den = dx1 * dy2 - dx2 * dy1;
                g = den == 0 ? 0 : (sx * dy2 - dx2 * sy) / den;
                h = den == 0 ? 0 : (dx1 * sy - sx * dy1) / den;
                a = p1.X - p0.X + g * p1.X; b = p2.X - p0.X + h * p2.X; c = p0.X;
                d = p1.Y - p0.Y + g * p1.Y; e = p2.Y - p0.Y + h * p2.Y; f = p0.Y;
            }
            for (int i = 0; i < path.Points.Count; i++) {
                PointF p = path.Points [i];
                double u = (p.X - src.X) / src.Width, v = (p.Y - src.Y) / src.Height;
                double x, y;
                if (perspective) {
                    double w = g * u + h * v + 1;
                    x = (a * u + b * v + c) / w;
                    y = (d * u + e * v + f) / w;
                } else {
                    x = (1 - u) * (1 - v) * p0.X + u * (1 - v) * p1.X + (1 - u) * v * p2.X + u * v * p3.X;
                    y = (1 - u) * (1 - v) * p0.Y + u * (1 - v) * p1.Y + (1 - u) * v * p2.Y + u * v * p3.Y;
                }
                path.Points [i] = new PointF ((float) x, (float) y);
            }
            path.Revalidate ();
        }
    }
}
