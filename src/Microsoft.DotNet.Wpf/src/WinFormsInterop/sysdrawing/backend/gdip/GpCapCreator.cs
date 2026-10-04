// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s anchor and custom line caps (GpEndCapCreator), read out of gdiplus.dll (arm64, public PDB):
//
//   GpEndCapCreator ctor @1800a6fd0   the caps: Square/Round/Diamond/ArrowAnchor are reference custom
//                                caps (ReferenceSquareAnchor @1800a7c38: a square of half-diagonal 1,
//                                ReferenceRoundAnchor @1800a7b98: the unit circle,
//                                ReferenceDiamondAnchor @1800a7ae0, ReferenceArrowAnchor @1800a7a20:
//                                (0,0) (-1,-sqrt3) (1,-sqrt3) with base inset 1); NoAnchor is none;
//                                a mirroring pen transform reverses the cap paths
//   CreateCapPath @1800a75f0 / GetCapsForSubpath @1800a7770   each open figure's two caps
//   SetCustomFillCaps @1800a7cf8   a fill cap scaled by the pen width (at least two device pixels),
//                                turned along the direction to where the line leaves a circle of the
//                                cap's length (ComputeCapGradient @1800a73d0, intersect_circle_line
//                                @1800a5a00); the points inside it are marked 0x40 for
//                                GpPath::EraseMarkedSegments @1801b3588 and the last one moved to the
//                                cap's base inset
//   SetCustomStrokeCaps @1800a8028   a stroke cap the same way, flattened and widened with the cap's
//                                stroke caps and join (PrepareDpPenForCustomCap @1801e7ce0)
//   getTransformedPoints @18007c4f0
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpEndCapCreator
    {
        const float Eps = 1.1920929e-07f;

        readonly GpPath _path;
        readonly DpPen _pen;
        readonly GpMatrix _m;
        readonly GpCustomLineCap _start, _end;

        public GpEndCapCreator (GpPath path, DpPen pen, GpMatrix? matrix)
        {
            _path = path;
            _pen = pen;
            _m = GpMatrix.Multiply (pen.Xform, matrix ?? GpMatrix.CreateIdentity ());
            _start = CapFor (pen.StartCap, pen.CustomStart);
            _end = CapFor (pen.EndCap, pen.CustomEnd);
            GpMatrix x = pen.Xform;
            if (x.M22 * x.M11 - x.M21 * x.M12 < 0f) {
                _start?.FillPath.Reverse (); _start?.StrokePath.Reverse ();
                _end?.FillPath.Reverse (); _end?.StrokePath.Reverse ();
            }
        }

        static GpCustomLineCap CapFor (int cap, GpCustomLineCap custom)
        {
            switch (cap) {
            case 0x11: return Reference (new [] { new PointF (-0.70710683f, -0.70710683f), new PointF (0.70710683f, -0.70710683f), new PointF (0.70710683f, 0.70710683f), new PointF (-0.70710683f, 0.70710683f) }, 0f);
            case 0x12: {
                var p = new GpPath (FillMode.Winding);
                p.AddEllipse (-1f, -1f, 2f, 2f);
                return Reference (p, 0f);
            }
            case 0x13: return Reference (new [] { new PointF (0f, 1f), new PointF (-1f, 0f), new PointF (0f, -1f), new PointF (1f, 0f) }, 0f);
            case 0x14: return Reference (new [] { new PointF (0f, 0f), new PointF (-1f, -1.7320508f), new PointF (1f, -1.7320508f) }, 1f);
            case 0xff: return custom?.Clone ();
            default: return null;
            }
        }

        static GpCustomLineCap Reference (PointF[] poly, float inset)
        {
            var p = new GpPath (FillMode.Winding);
            p.AddPolygon (poly, poly.Length);
            return Reference (p, inset);
        }

        static GpCustomLineCap Reference (GpPath fill, float inset)
        {
            GpCustomLineCap.Create (fill, null, LineCap.Flat, 0f, out GpCustomLineCap c);
            c.BaseInset = inset;
            return c;
        }

        public GpPath CreateCapPath ()
        {
            var result = new GpPath (FillMode.Winding);
            int n = _path.Count;
            int s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (_path.Types [e + 1] & 7) != 0) e++;
                if ((_path.Types [e] & 0x80) == 0) {
                    GetCapsForSubpath (s, e, out GpPath sp, out GpPath ep);
                    if (sp != null) result.AddPath (sp, false);
                    if (ep != null) result.AddPath (ep, false);
                }
                s = e + 1;
            }
            return result;
        }

        void GetCapsForSubpath (int s, int e, out GpPath startPath, out GpPath endPath)
        {
            startPath = endPath = null;
            if (_start == null && _end == null) return;
            PointF p0 = _path.Points [s], pe = _path.Points [e];
            var sp = new List<PointF> (); var st = new List<byte> ();
            var ep = new List<PointF> (); var et = new List<byte> ();
            SetCustomFillCaps (p0, pe, s, e, sp, st, ep, et);
            SetCustomStrokeCaps (p0, pe, s, e, sp, st, ep, et);
            if (sp.Count > 0) startPath = new GpPath (sp.ToArray (), st.ToArray (), FillMode.Alternate);
            if (ep.Count > 0) endPath = new GpPath (ep.ToArray (), et.ToArray (), FillMode.Alternate);
        }

        void SetCustomFillCaps (PointF p0, PointF pe, int s, int e, List<PointF> sp, List<byte> st, List<PointF> ep, List<byte> et)
        {
            GpStroke.MajorMinor (_m, out float a, out float b);
            float minAx = b <= a ? b : a;
            float k = 2f;   // the path's +0x100 flag is set
            for (int side = 0; side < 2; side++) {
                GpCustomLineCap cap = side == 0 ? _start : _end;
                if (cap == null || cap.FillPath.Count <= 0) continue;
                float fl = cap.FillLength;
                float sc = _pen.Width * cap.WidthScale;
                float ins = Eps <= MathF.Abs (fl) ? cap.BaseInset / fl : 0f;
                float r = sc <= 1f / minAx ? 1f / minAx : sc;
                PointF g = side == 0 ? CapGradient (s, e, +1, r * fl * r * fl, ins) : CapGradient (e, s, -1, r * fl * r * fl, ins);
                var dir = new PointF (-g.X, -g.Y);
                Transformed (cap.FillPath, side == 0 ? p0 : pe, dir, sc, k / minAx, side == 0 ? sp : ep, side == 0 ? st : et);
            }
        }

        void SetCustomStrokeCaps (PointF p0, PointF pe, int s, int e, List<PointF> sp, List<byte> st, List<PointF> ep, List<byte> et)
        {
            for (int side = 0; side < 2; side++) {
                GpCustomLineCap cap = side == 0 ? _start : _end;
                if (cap == null || cap.StrokePath.Count <= 0) continue;
                List<PointF> op = side == 0 ? sp : ep;
                List<byte> ot = side == 0 ? st : et;
                op.Clear (); ot.Clear ();
                float sc = _pen.Width * cap.WidthScale;
                float sl = 1.1920929e-07f <= MathF.Abs (cap.StrokeLength) ? cap.StrokeLength : 1f;
                PointF g = side == 0 ? CapGradient (s, e, +1, sl * sc * sl * sc, cap.BaseInset / sl)
                                     : CapGradient (e, s, -1, sl * sc * sl * sc, cap.BaseInset / sl);
                var dir = new PointF (-g.X, -g.Y);
                var tp = new List<PointF> (); var tt = new List<byte> ();
                Transformed (cap.StrokePath, side == 0 ? p0 : pe, dir, sc, sc, tp, tt);
                var path = new GpPath (tp.ToArray (), tt.ToArray (), FillMode.Winding);
                path.Flatten (null, 0.25f);
                DpPen cp = _pen.Clone ();
                cp.StartCap = (int) cap.StrokeStartCap; cp.EndCap = (int) cap.StrokeEndCap; cp.Join = (int) cap.StrokeJoin;
                cp.Alignment = 0; cp.MiterLimit = 10f;
                cp.DashStyle = 0; cp.DashCap = 0; cp.DashArray = null; cp.DashOffset = 0f; cp.Compound = null;
                cp.CustomStart = cp.CustomEnd = null;
                cp.Width = cp.Width * cap.WidthScale;
                var w = new GpPathWidener (path, cp, _m, 0f, 0f, false);
                GpPath wide = w.Widen (keepFlags: true);
                if (wide != null) { op.AddRange (wide.Points); ot.AddRange (wide.Types); }
            }
        }

        /// <summary>getTransformedPoints: the cap's points turned onto <paramref name="dir"/>, scaled,
        /// at <paramref name="at"/> (the cap's +0x20/+0x28 offsets are zero).</summary>
        static void Transformed (GpPath cap, PointF at, PointF dir, float s9, float s10, List<PointF> op, List<byte> ot)
        {
            float scale = s10 <= s9 ? s9 : s10;
            float dx = dir.X, dy = dir.Y;
            float f4 = (1f - scale) * 0f, f6 = 0f * (1f - scale);
            for (int i = 0; i < cap.Count; i++) {
                float x = cap.Points [i].X, y = cap.Points [i].Y;
                float nx = x * dy * scale + dx * scale * y + dx * f6 + dy * f4 + at.X;
                float ny = x * -scale * dx + dy * scale * y + (dy * f6 - dx * f4) + at.Y;
                op.Add (new PointF (nx, ny));
                ot.Add (cap.Types [i]);
            }
        }

        /// <summary>ComputeCapGradient: from the end at <paramref name="from"/> inwards, the points
        /// inside the circle of radius sqrt(<paramref name="r2"/>) are marked for erasing; the last one
        /// moves to the base inset; the result is the unit direction to where the line leaves.</summary>
        PointF CapGradient (int from, int to, int step, float r2, float ins)
        {
            List<PointF> P = _path.Points;
            List<byte> T = _path.Types;
            PointF p0 = P [from], last = p0;
            bool found = false, wasMarked = false;
            int cur = from;
            while (step > 0 ? cur <= to : cur >= to) {
                last = P [cur];
                if (r2 < (last.X - p0.X) * (last.X - p0.X) + (last.Y - p0.Y) * (last.Y - p0.Y)) { found = true; break; }
                wasMarked = (T [cur] & 0x40) != 0;
                T [cur] = (byte) (T [cur] | 0x40);
                cur += step;
            }
            cur -= step;
            if (found && !wasMarked) T [cur] = (byte) (T [cur] & 0xbf);
            PointF q6 = P [cur];
            PointF q = IntersectCircleLine (p0, r2, last, q6, out PointF o) ? o : q6;
            PointF g = Normalize (new PointF (q.X - p0.X, q.Y - p0.Y));
            P [cur] = new PointF ((p0.X - q.X) * (1f - ins) + q.X, (p0.Y - q.Y) * (1f - ins) + q.Y);
            return g;
        }

        /// <summary>GpVector2D::Normalize @1801e7c48.</summary>
        static PointF Normalize (PointF v)
        {
            double x = v.X, y = v.Y;
            double d = x * x + y * y;
            if (d < 0) return PointF.Empty;
            d = Math.Sqrt (d);
            if (Math.Abs (d) < 1.1920928955078125e-07) return PointF.Empty;
            float f = (float) d;
            return new PointF (v.X / f, v.Y / f);
        }

        static bool IntersectCircleLine (PointF c, float r2, PointF a, PointF b, out PointF o)
        {
            o = PointF.Empty;
            float dy = b.Y - a.Y, dx = b.X - a.X;
            double len = Math.Sqrt ((double) (dy * dy + dx * dx));
            const double E = 1.1920928955078125e-07;
            if (!(E <= len)) return false;
            float inv = (float) (1.0 / len);
            float ux = dx * inv, uy = dy * inv;
            float cy = c.Y - a.Y, cx = c.X - a.X;
            double d2 = (double) (cy * cy + cx * cx);
            double proj = (double) (cy * uy + cx * ux);
            double rr = r2;
            if (!(E <= proj || d2 < rr)) return false;
            double disc = (rr - d2) + proj * proj;
            if (!(E <= disc)) return false;
            disc = Math.Sqrt (disc);
            double t;
            if (rr <= d2 && (t = proj - disc) > E && 0.0 <= t) { }
            else if ((t = disc + proj) > E && 0.0 <= t) { }
            else return false;
            float tx = (float) t * ux, ty = (float) t * uy;
            o = new PointF (tx + a.X, ty + a.Y);
            return true;
        }

        /// <summary>GpPath::EraseMarkedSegments: the points marked 0x40 go; a point after an erased
        /// figure start starts the figure.</summary>
        public static void EraseMarkedSegments (GpPath p)
        {
            int w = 0;
            bool startGone = false;
            for (int i = 0; i < p.Count; i++) {
                byte t = p.Types [i];
                if ((t & 0x40) == 0) {
                    if (i != w) {
                        p.Points [w] = p.Points [i];
                        p.Types [w] = startGone ? (byte) (t & 0xf8) : t;
                    }
                    startGone = false;
                    w++;
                } else startGone = (t & 7) == 0 || startGone;
            }
            int gone = p.Count - w;
            if (gone > 0) {
                p.Points.RemoveRange (w, gone);
                p.Types.RemoveRange (w, gone);
            }
            p.Revalidate ();
        }
    }
}
