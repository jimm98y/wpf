// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s custom line cap (GpCustomLineCap / GpAdjustableArrowCap), read out of gdiplus.dll:
//
//   GpCustomLineCap ctor @18007b2e8   fill and stroke paths validated, base cap clamped to 0..3
//   SetFillPath @18007c2a8 / SetStrokePath @18007c350   a fill path needs 3 points, a stroke 2; the
//                                     cap's length is -ComputeCapLength, and a path that never
//                                     crosses the negative y axis is refused (NotImplemented)
//   GpAdjustableArrowCap::Update @18007c3f8 / GetPathData @1801d6320   the arrow: (w/2, -h), (0, 0),
//                                     (-w/2, -h) and, with a middle inset, (0, inset - h); filled it is
//                                     the fill path (closed), else the stroke path; base cap Triangle
//                                     and base inset h / w
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpCustomLineCap
    {
        public GpPath FillPath = new GpPath (FillMode.Winding);
        public GpPath StrokePath = new GpPath (FillMode.Winding);
        public LineCap BaseCap;
        public float BaseInset;
        public LineCap StrokeStartCap, StrokeEndCap;
        public LineJoin StrokeJoin;
        public float MiterLimit = 10f;
        public float WidthScale = 1f;
        public float FillLength, StrokeLength;   // +0x44, +0x48

        public bool IsArrow;
        public float ArrowWidth, ArrowHeight, ArrowMiddleInset;
        public bool ArrowFilled;

        public GpCustomLineCap Clone ()
        {
            var c = (GpCustomLineCap) MemberwiseClone ();
            c.FillPath = FillPath.Clone ();
            c.StrokePath = StrokePath.Clone ();
            return c;
        }

        public static int Create (GpPath fill, GpPath stroke, LineCap baseCap, float baseInset, out GpCustomLineCap cap)
        {
            cap = new GpCustomLineCap ();
            int st = 0;
            if (fill != null) st = cap.SetFillPath (fill);
            if (st == 0 && stroke != null) st = cap.SetStrokePath (stroke);
            if (st != 0) return st;
            cap.BaseCap = (uint) baseCap < 4 ? baseCap : LineCap.Flat;
            cap.BaseInset = baseInset;
            return 0;
        }

        public int SetFillPath (GpPath p)
        {
            if (p == null || p.Count == 0) { FillPath = new GpPath (FillMode.Winding); return 0; }
            if (p.Count < 3) return 2;
            FillPath = p.Clone ();
            FillLength = -ComputeCapLength (FillPath);
            return FillLength < 1.1920929e-07f ? 6 : 0;
        }

        public int SetStrokePath (GpPath p)
        {
            if (p == null || p.Count == 0) { StrokePath = new GpPath (FillMode.Winding); return 0; }
            if (p.Count < 2) return 2;
            StrokePath = p.Clone ();
            StrokeLength = -ComputeCapLength (StrokePath);
            return StrokeLength < 1.1920929e-07f ? 6 : 0;
        }

        /// <summary>ComputeCapLength @18007b5d8: the lowest y at which the path's segments cross the
        /// y axis (0 when none does below it).</summary>
        static float ComputeCapLength (GpPath p)
        {
            float best = 0f;
            int n = p.Count;
            if (n < 2) return 0f;
            for (int i = 0; i < n; i++) {
                PointF a = p.Points [i], b = p.Points [(i + 1) % n];
                if (IntersectYAxis (a, b, out float y) && y < best) best = y;
            }
            return best;
        }

        /// <summary>intersect_line_yaxis: where segment a-b crosses x = 0.</summary>
        static bool IntersectYAxis (PointF a, PointF b, out float y)
        {
            y = 0f;
            if ((a.X > 0f && b.X > 0f) || (a.X < 0f && b.X < 0f)) return false;
            float dx = b.X - a.X;
            if (dx == 0f) {
                if (a.X != 0f) return false;
                y = Math.Min (a.Y, b.Y);
                return true;
            }
            y = a.Y - a.X * (b.Y - a.Y) / dx;
            return true;
        }

        public static GpCustomLineCap CreateArrow (float width, float height, bool filled)
        {
            var c = new GpCustomLineCap { IsArrow = true, ArrowWidth = width, ArrowHeight = height, ArrowFilled = filled };
            c.UpdateArrow ();
            return c;
        }

        public void UpdateArrow ()
        {
            BaseCap = LineCap.Triangle;
            BaseInset = ArrowWidth == 0f ? 0f : ArrowHeight / ArrowWidth;
            float h = ArrowHeight, w = ArrowWidth, inset = ArrowMiddleInset;
            var pts = new [] { new PointF (w * 0.5f, -h), new PointF (0f, 0f), new PointF (-w * 0.5f, -h), new PointF (0f, inset - h) };
            var types = new byte [] { 0, 1, 1, 1 };
            int count = inset == 0f ? 3 : 4;
            if (ArrowFilled) types [count - 1] |= 0x80;
            var pp = new PointF [count];
            var tt = new byte [count];
            Array.Copy (pts, pp, count);
            Array.Copy (types, tt, count);
            var path = new GpPath (pp, tt, FillMode.Winding);
            if (!ArrowFilled) {
                StrokePath = path;
                StrokeLength = -ComputeCapLength (path);
                FillPath = new GpPath (FillMode.Winding);
            } else {
                FillPath = path;
                FillLength = -ComputeCapLength (path);
                StrokePath = new GpPath (FillMode.Winding);
            }
        }
    }
}
