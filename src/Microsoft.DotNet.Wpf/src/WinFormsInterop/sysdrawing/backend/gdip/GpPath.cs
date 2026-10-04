// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s path (GpPath / DpPath), operation for operation, read out of gdiplus.dll:
//
//   AddPointHelper @18001af10   a figure continues when one is open: its first point is dropped when
//                               within 1.1920929e-07 of the last, otherwise joined by a line; a closed
//                               shape starts a figure of its own
//   AddBeziers @18001aa00, AddPolygon @18001b2a8 (a repeated last point dropped), AddRects @18001b368
//   GetArcPoints @180089018 + NormalizeArcAngles/NormalizeAngle   arcs in quarter turns, each a unit
//                               arc rotated into place through GpMatrix (CRT sinf/cosf), ray angles
//                               mapped to the ellipse's parameter with a double atan2
//   AddEllipse @180087c60       13 points with kappa 0.5522847
//   ConvertSplineToBezierPoints @180088aa8   cardinal splines, tension / 3
//   Flatten @180088e60          FixedPointPathEnumerate at 16 / (4 * flatness), then scaled back
//   ReversePath @180020700      each figure's types shifted and its flags carried, then the arrays
//                               reversed whole
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpPath
    {
        public const byte Start = 0, Line = 1, Bezier = 3, TypeMask = 7, DashMode = 0x10, Marker = 0x20, Close = 0x80;
        const float Eps = 1.1920929e-07f;

        public readonly List<PointF> Points = new List<PointF> ();
        public readonly List<byte> Types = new List<byte> ();
        public FillMode FillMode;
        public bool SubpathActive;    // +0xf8
        public int SubpathCount;      // +0xfc
        public bool HasBezier;        // +0x18

        public GpPath (FillMode mode = FillMode.Alternate) { FillMode = mode; }

        public GpPath (PointF[] pts, byte[] types, FillMode mode)
        {
            FillMode = mode;
            Points.AddRange (pts);
            Types.AddRange (types);
            Revalidate ();
        }

        public GpPath Clone ()
        {
            var c = new GpPath (FillMode) { SubpathActive = SubpathActive, SubpathCount = SubpathCount, HasBezier = HasBezier };
            c.Points.AddRange (Points);
            c.Types.AddRange (Types);
            return c;
        }

        public int Count => Points.Count;

        /// <summary>DpPath::ValidatePathTypes's bookkeeping, after the arrays were set wholesale.</summary>
        public void Revalidate ()
        {
            SubpathCount = 0;
            HasBezier = false;
            for (int i = 0; i < Types.Count; i++) {
                if ((Types [i] & TypeMask) == Start) SubpathCount++;
                if ((Types [i] & TypeMask) == Bezier) HasBezier = true;
            }
            SubpathActive = Types.Count > 0 && (Types [Types.Count - 1] & Close) == 0;
        }

        public void Reset ()
        {
            Points.Clear (); Types.Clear ();
            SubpathActive = false; SubpathCount = 0; HasBezier = false;
            FillMode = FillMode.Alternate;
        }

        // ---- figures --------------------------------------------------------------------------------

        public void StartFigure () => SubpathActive = false;

        public void CloseFigure ()
        {
            if (!SubpathActive) return;
            if (Types.Count > 0) Types [Types.Count - 1] |= Close;
            StartFigure ();
        }

        public void CloseFigures ()
        {
            int n = Points.Count;
            if (n > 1) {
                for (int i = 0; i < n - 1; i++)
                    if (Types [i + 1] == 0) Types [i] |= Close;
                Types [n - 1] |= Close;
            }
            StartFigure ();
        }

        public void SetMarker ()
        {
            if (Types.Count > 0) Types [Types.Count - 1] |= Marker;
        }

        public void ClearMarkers ()
        {
            for (int i = 0; i < Types.Count; i++) Types [i] = (byte) (Types [i] & ~Marker);
        }

        // ---- adding ---------------------------------------------------------------------------------

        /// <summary>AddPointHelper: appends points, typing the first; returns the index of the first
        /// point whose type the caller still has to set (or -1 when nothing was added).</summary>
        int AddPoints (IList<PointF> pts, int offset, int count, bool newClosedFigure)
        {
            if (newClosedFigure) StartFigure ();
            bool typeFirst = true;
            int n = Points.Count;
            if (SubpathActive && n > 0) {
                PointF last = Points [n - 1];
                if (MathF.Abs (pts [offset].X - last.X) < Eps && MathF.Abs (pts [offset].Y - last.Y) < Eps) {
                    if (count == 1) return -1;
                    offset++; count--;
                    typeFirst = false;
                }
            }
            if (count == 0) return -1;
            for (int i = 0; i < count; i++) { Points.Add (pts [offset + i]); Types.Add (Line); }
            int next = n;
            if (!SubpathActive) {
                Types [n] = Start;
                SubpathCount++;
                next = n + 1;
            } else if (typeFirst) {
                Types [n] = Line;
                next = n + 1;
            }
            if (!newClosedFigure) SubpathActive = true;
            return next;
        }

        public bool AddLines (IList<PointF> pts, int count)
        {
            if (pts == null || count < 1) return false;
            int at = AddPoints (pts, 0, count, false);
            if (at < 0) return true;
            for (int i = at; i < Points.Count; i++) Types [i] = Line;
            return true;
        }

        public void AddLine (float x1, float y1, float x2, float y2) => AddLines (new[] { new PointF (x1, y1), new PointF (x2, y2) }, 2);

        public bool AddBeziers (IList<PointF> pts, int count)
        {
            if (pts == null || count < 4 || count % 3 != 1) return false;
            int first;   // 0 start, 1 line, -1 dropped
            int offset = 0;
            int n = Points.Count;
            if (!SubpathActive || n < 1) {
                first = 0;
                SubpathCount++;
            } else {
                PointF last = Points [n - 1];
                if (Eps <= MathF.Abs (pts [0].X - last.X) || Eps <= MathF.Abs (pts [0].Y - last.Y)) first = 1;
                else { first = -1; offset = 1; count--; }
            }
            for (int i = 0; i < count; i++) { Points.Add (pts [offset + i]); Types.Add (Bezier); }
            if (first == 0) Types [n] = Start;
            else if (first == 1) Types [n] = Line;
            SubpathActive = true;
            HasBezier = true;
            return true;
        }

        public void AddBezier (float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
            => AddBeziers (new[] { new PointF (x1, y1), new PointF (x2, y2), new PointF (x3, y3), new PointF (x4, y4) }, 4);

        public bool AddPolygon (IList<PointF> pts, int count)
        {
            if (count < 3 || pts == null) return false;
            int n = count;
            if (count > 3 && pts [0].X == pts [count - 1].X) {
                n = count - 1;
                if (pts [0].Y != pts [count - 1].Y) n = count;
            }
            int at = AddPoints (pts, 0, n, true);
            if (at < 0) return true;
            for (int i = at; i < Points.Count; i++) Types [i] = Line;
            Types [Points.Count - 1] = Line | Close;
            return true;
        }

        public void AddRects (IList<RectangleF> rects)
        {
            foreach (RectangleF r in rects) {
                if (!(Eps < r.Width && Eps < r.Height)) continue;
                float x1 = r.Width + r.X, y1 = r.Height + r.Y;
                AddPolygon (new[] { new PointF (r.X, r.Y), new PointF (x1, r.Y), new PointF (x1, y1), new PointF (r.X, y1) }, 4);
            }
        }

        public void AddEllipse (float x, float y, float w, float h)
        {
            var p = new PointF [13];
            // As compiled (@180087c60): x * (w / 2) + (left + w / 2).
            float hw = w * 0.5f, hh = h * 0.5f, cx = x + hw, cy = y + hh;
            for (int i = 0; i < 13; i++)
                p [i] = new PointF (s_ellipse [2 * i] * hw + cx, s_ellipse [2 * i + 1] * hh + cy);
            StartFigure ();
            AddBeziers (p, 13);
            CloseFigure ();
        }

        const float K = 0.5522847f;
        static readonly float[] s_ellipse = {
            1, 0, 1, K, K, 1, 0, 1, -K, 1, -1, K, -1, 0, -1, -K, -K, -1, 0, -1, K, -1, 1, -K, 1, 0,
        };

        public bool AddArc (float x, float y, float w, float h, float start, float sweep)
        {
            bool close = false;
            if (360f <= sweep) { close = true; sweep = 360f; }
            else if (sweep <= -360f) { close = true; sweep = -360f; }
            var pts = new PointF [13];
            int n = GetArcPoints (pts, x, y, w, h, start, sweep);
            if (n < 1) return n >= 0;
            AddBeziers (pts, n);
            if (close) CloseFigure ();
            return true;
        }

        public bool AddPie (float x, float y, float w, float h, float start, float sweep)
        {
            StartFigure ();
            AddLines (new[] { new PointF (w * 0.5f + x, h * 0.5f + y) }, 1);
            AddArc (x, y, w, h, start, sweep);
            CloseFigure ();
            return true;
        }

        /// <summary>GpPath::GetArcPoints: the arc's Bezier points, or -1 for an empty rectangle.</summary>
        public static int GetArcPoints (PointF[] o, float x, float y, float w, float h, float start, float sweep)
        {
            if (w <= Eps || h <= Eps) return -1;
            if (sweep == 0f) return 0;
            int dir = NormalizeArcAngles (ref start, ref sweep, w, h);
            float rx = w * 0.5f, ry = h * 0.5f;
            float cx = x + rx, cy = y + ry;
            int segs = (int) (sweep / 1.5707963267948966);
            if (segs * 1.5707963267948966 < sweep) segs++;
            int count;
            if (segs == 0) { segs = 1; count = 4; }
            else if (segs < 5) {
                if (segs < 0) return 0;
                count = segs * 3 + 1;
            } else { segs = 4; count = 13; }
            float dirF = dir;
            int p = 0;
            float rem = sweep, cur = start;
            while (segs != 0) {
                float half = rem <= 1.5707963267948966 ? rem * 0.5f : 0.7853982f;
                float c = Crt.Cosf (half), s = Crt.Sinf (half);
                float k = (4f - c) / 3f;
                float m = ((3f - c) * s) / (c * 3f + 3f);
                if (dir < 1) {
                    o [p] = new PointF (c, s);
                    o [p + 1] = new PointF (k, m);
                    o [p + 2] = new PointF (k, -m);
                    o [p + 3] = new PointF (c, -s);
                } else {
                    o [p] = new PointF (c, -s);
                    o [p + 1] = new PointF (k, -m);
                    o [p + 2] = new PointF (k, m);
                    o [p + 3] = new PointF (c, s);
                }
                var mat = GpMatrix.CreateIdentity ();
                mat.Translate (cx, cy, false);
                mat.Scale (rx, ry, false);
                mat.Rotate ((float) ((double) ((dirF * half + cur) * 180f) / Math.PI), false);
                segs--;
                int nt = segs < 1 ? 4 : 3;
                for (int i = 0; i < nt; i++) o [p + i] = mat.Transform (o [p + i]);
                p += 3;
                rem -= 1.5707964f;
                cur = dir < 1 ? cur - 1.5707964f : cur + 1.5707964f;
            }
            return count;
        }

        static int NormalizeArcAngles (ref float start, ref float sweep, float w, float h)
        {
            float s = start, e = start + sweep;
            float abs = sweep <= 0f ? -sweep : sweep;
            int dir = 0f < sweep ? 1 : -1;
            NormalizeAngle (ref s, w, h);
            NormalizeAngle (ref e, w, h);
            float r = 6.2831855f;
            if (abs < 360f) {
                r = dir < 1 ? s - e : e - s;
                if (r < 0f) r += 6.2831855f;
            }
            start = s;
            sweep = r;
            return dir;
        }

        static void NormalizeAngle (ref float a, float w, float h)
        {
            float v = ModF (a, 360f);
            if (v < 0f || 360f < v) v = 0f;
            if (w == h) { a = (float) ((double) v * Math.PI / 180.0); return; }
            int q;
            if (90f < v) {
                if (180f < v) {
                    if (270f < v) { q = 4; v = 360f - v; }
                    else { q = 3; v = v - 180f; }
                } else { q = 2; v = 180f - v; }
            } else q = 1;
            double d = (float) ((double) v * Math.PI / 180.0);
            double c = Math.Cos (d), s = Math.Sin (d);
            float r = (float) Math.Atan2 (s * w, c * h);
            if (q == 2) r = 3.1415927f - r;
            else if (q == 3) r = r + 3.1415927f;
            else if (q == 4) r = 6.2831855f - r;
            a = r;
        }

        /// <summary>GpModF @1801ace10.</summary>
        static float ModF (float x, float m)
        {
            if (x <= 0f) {
                if (0f <= x) return 0f;
                float r = -x - (float) (int) (-x / m) * m;
                if (0f < r) r = m - r;
                return r;
            }
            return x - (float) (int) (x / m) * m;
        }

        public bool AddCurve (IList<PointF> pts, int count, float tension, int offset, int segments)
        {
            if (pts == null || count < 2 || offset < 0 || count <= offset || segments < 1 || count - offset <= segments) return false;
            PointF[] b = SplineToBeziers (pts, count, offset, segments, tension);
            return AddBeziers (b, b.Length);
        }

        public bool AddClosedCurve (IList<PointF> pts, int count, float tension)
        {
            if (pts == null || count < 3) return false;
            PointF[] b = SplineToBeziers (pts, count, 0, count, tension);
            StartFigure ();
            AddBeziers (b, b.Length);
            CloseFigure ();
            return true;
        }

        /// <summary>GpPath::ConvertSplineToBezierPoints.</summary>
        public static PointF[] SplineToBeziers (IList<PointF> p, int count, int offset, int segments, float tension)
        {
            var o = new PointF [segments * 3 + 1];
            float t = tension / 3f;
            o [0] = p [offset];
            int w = 1;
            for (int i = offset + 1; i <= offset + segments; i++) {
                int prev = i - 1;
                PointF q0, q1, q2, q3;
                if (prev < 2 || count - 2 <= prev) {
                    if (segments == count) {
                        q0 = p [(i + count - 2) % count];
                        q1 = p [i - 1];
                        q2 = p [i % count];
                        q3 = p [(i + 1) % count];
                    } else {
                        q0 = p [prev < 1 ? 0 : i - 2];
                        q1 = p [i - 1];
                        q2 = p [count <= i ? count - 1 : i];
                        q3 = p [i + 1 < count ? i + 1 : count - 1];
                    }
                } else {
                    q0 = p [i - 2]; q1 = p [i - 1]; q2 = p [i]; q3 = p [i + 1];
                }
                o [w] = new PointF ((q1.X - q0.X * t) + q2.X * t, (q1.Y - q0.Y * t) + q2.Y * t);
                o [w + 1] = new PointF ((t * q1.X + q2.X) - q3.X * t, (q1.Y * t + q2.Y) - q3.Y * t);
                o [w + 2] = q2;
                w += 3;
            }
            return o;
        }

        /// <summary>GpPath::AddPath: the other path's figures appended, the first joined to the
        /// open figure when <paramref name="connect"/> (CombinePaths).</summary>
        public void AddPath (GpPath other, bool connect)
        {
            if (other == null || other.Count == 0) return;
            int n = Points.Count;
            int start = 0;
            if (connect && SubpathActive && n > 0) {
                // The first figure continues this one: its start point becomes a line (dropped when it
                // repeats the last point).
                PointF last = Points [n - 1];
                PointF first = other.Points [0];
                if (first == last) start = 1;
                else {
                    Points.Add (first);
                    Types.Add ((byte) ((other.Types [0] & ~TypeMask) | Line));
                    start = 1;
                }
            }
            for (int i = start; i < other.Count; i++) { Points.Add (other.Points [i]); Types.Add (other.Types [i]); }
            Revalidate ();
        }

        // ---- transforms -----------------------------------------------------------------------------

        public void Transform (in GpMatrix m)
        {
            if (m.Complexity == 0) return;
            for (int i = 0; i < Points.Count; i++) Points [i] = m.Transform (Points [i]);
        }

        public void Offset (float dx, float dy)
        {
            for (int i = 0; i < Points.Count; i++) Points [i] = new PointF (Points [i].X + dx, Points [i].Y + dy);
        }

        /// <summary>GpPath::Flatten(matrix, flatness).</summary>
        public void Flatten (GpMatrix? matrix, float flatness)
        {
            if (!HasBezier) {
                if (matrix is GpMatrix t) Transform (t);
                return;
            }
            float f4 = flatness * 4f;
            float k = 16f / f4;
            GpMatrix m = matrix ?? GpMatrix.CreateIdentity ();
            m.M11 *= k; m.M12 *= k; m.M21 *= k; m.M22 *= k; m.Dx *= k; m.Dy *= k;
            m.Complexity = m.ComputeComplexity ();
            var flat = new GpPath (FillMode);
            GpRaster.EnumerateForFlatten (this, m, (buf, n, term) => {
                var pts = new PointF [n];
                for (int i = 0; i < n; i++) pts [i] = new PointF (buf [i * 2] * 0.0625f, buf [i * 2 + 1] * 0.0625f);
                int use = term == 2 ? n - 1 : n;
                flat.AddLines (pts, use);
                if (term == 2) flat.CloseFigure ();
                else if (term == 1) flat.StartFigure ();
            });
            var s = GpMatrix.CreateIdentity ();
            s.Scale (f4, f4, false);
            flat.Transform (s);
            Points.Clear (); Points.AddRange (flat.Points);
            Types.Clear (); Types.AddRange (flat.Types);
            HasBezier = false;
            SubpathActive = flat.SubpathActive;
        }

        /// <summary>ReversePath @180020700.</summary>
        public void Reverse ()
        {
            int n = Points.Count;
            if (n < 2) return;
            byte[] t = Types.ToArray ();
            byte prevEndMarker = 0;
            int s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (t [e + 1] & TypeMask) != Start) e++;
                byte first = t [s], last = t [e];
                byte closed = (byte) (e - s + 1 >= 2 ? t [e] >> 7 : 0);
                for (int i = s + 1; i <= e; i++) t [i - 1] = t [i];
                if (e > 0) t [e - 1] &= 0x7f;
                t [e] = 0;
                t [s] = (byte) ((first & DashMode) != 0 ? t [s] | DashMode : t [s] & ~DashMode);
                t [e] = (byte) ((last & DashMode) != 0 ? t [e] | DashMode : t [e] & ~DashMode);
                t [s] = (byte) (closed != 0 ? t [s] | Close : t [s] & 0x7f);
                for (int k = e; k >= s + 2; k--)
                    t [k] = (byte) ((t [k - 2] & Marker) != 0 ? t [k] | Marker : t [k] & ~Marker);
                if (s + 1 <= e)
                    t [s + 1] = (byte) ((first & Marker) != 0 ? t [s + 1] | Marker : t [s + 1] & ~Marker);
                t [s] = (byte) (prevEndMarker != 0 ? t [s] | Marker : t [s] & ~Marker);
                for (int k = e; k >= s + 1; k--)
                    t [k] = (byte) ((t [k - 1] & 0x40) != 0 ? t [k] | 0x40 : t [k] & ~0x40);
                t [s] = (byte) ((first & 0x40) != 0 ? t [s] | 0x40 : t [s] & ~0x40);
                prevEndMarker = (byte) ((last >> 5) & 1);
                s = e + 1;
            }
            Array.Reverse (t);
            Points.Reverse ();
            Types.Clear ();
            Types.AddRange (t);
        }

        /// <summary>The control-point bounds (GpPath::CalcCacheBounds).</summary>
        public RectangleF ControlBounds ()
        {
            if (Points.Count == 0) return RectangleF.Empty;
            float l = Points [0].X, tp = Points [0].Y, r = l, b = tp;
            foreach (PointF p in Points) { l = Math.Min (l, p.X); tp = Math.Min (tp, p.Y); r = Math.Max (r, p.X); b = Math.Max (b, p.Y); }
            return new RectangleF (l, tp, r - l, b - tp);
        }

        public PointF[] PointArray () => Points.ToArray ();
        public byte[] TypeArray () => Types.ToArray ();
    }
}
