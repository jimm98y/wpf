// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s dashed paths, read out of gdiplus.dll (arm64, public PDB):
//
//   GpPath::CreateDashedPath @18001b778   the path flattened in device space at 0.25 and brought
//                                back; the dash array (padded with a zero gap when odd) in units of
//                                the pen width, at least one device pixel (four for an inset pen's
//                                half-width pass); AdjustDashArrayForCaps @1801df310 shortens each
//                                dash and lengthens each gap by the dash cap's two insets
//   getDashData @18001d1f8 + EmitLineSegment @18001be90   the offset taken modulo the pattern,
//                                each dash an open figure whose points carry the dash-mode flag
//                                0x10; a dash that reaches a path end within half a pen width
//                                gives that end back to the line cap
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpPen
    {
        /// <summary>GpPen::GetDashCapInsetLength @1801df4f0.</summary>
        static float DashCapInset (DpPen pen, float unit) => pen.DashCap == 2 || pen.DashCap == 3 ? unit * 0.5f : 0f;

        public static GpPath CreateDashedPath (GpPath path, DpPen pen, GpMatrix? m, float dpiX, float dpiY, float scale, bool forCaps)
        {
            if (pen.DashStyle == 0 || pen.DashCount + 1 < 2 || pen.DashArray == null) return null;
            float w = pen.Width;
            bool world = true;
            float minW = 1.1920929e-07f <= MathF.Abs (scale - 0.5f) ? 1f : 4f;
            float unit;
            if (pen.Unit == 0) {
                float major = 1f;
                if (m is GpMatrix mm) GpStroke.MajorMinor (mm, out major, out _);
                unit = major * w < minW ? minW / major : w;
            } else {
                world = false;
                w = DpPen.DeviceWidth (w, pen.Unit, dpiX);
                unit = w <= minW ? minW : w;
            }
            unit = unit * scale;
            GpMatrix fwd = m ?? GpMatrix.CreateIdentity ();
            GpMatrix inv = fwd;
            if (!inv.IsInvertible) return null;
            inv.Invert ();
            int count = pen.DashCount;
            if ((count & 1) != 0) count++;
            var dash = new float [count];
            Array.Copy (pen.DashArray, dash, pen.DashCount);
            for (int i = 0; i < count; i++) dash [i] = dash [i] * unit;
            GpPath p = path.Clone ();
            if (p.Count == 0) return null;
            p.Flatten (fwd, 0.25f);
            if (world && inv.Complexity != 0)
                for (int i = 0; i < p.Count; i++) p.Points [i] = inv.Transform (p.Points [i]);
            if (forCaps) {
                float ins = DashCapInset (pen, unit);
                ins = ins + ins;
                if (0f < ins) {
                    float min = unit * 0.001f;
                    for (int i = 0; i < count; i++) {
                        if ((i & 1) == 0) { dash [i] = dash [i] - ins; if (dash [i] < min) dash [i] = min; }
                        else dash [i] = dash [i] + ins;
                    }
                }
            }
            var outP = new List<PointF> ();
            var outT = new List<byte> ();
            int n = p.Count;
            int s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (p.Types [e + 1] & 7) != 0) e++;
                bool closed = e - s + 1 >= 2 && (p.Types [e] & 0x80) != 0;
                int np = e - s + 1;
                var pts = new PointF [np];
                for (int i = 0; i < np; i++) pts [i] = p.Points [s + i];
                var grads = new PointF [np + 1];
                var lens = new float [np + 1];
                if (GradientArray (grads, lens, pts, np)) {
                    float ins = 0f;
                    if (forCaps && closed) { ins = DashCapInset (pen, unit); ins = ins + ins; }
                    DashData (outP, outT, w, pen.DashOffset * w - ins, dash, count, pts, np, closed, lens);
                }
                s = e + 1;
            }
            if (outP.Count < 1) return null;
            var r = new GpPath (outP.ToArray (), outT.ToArray (), FillMode.Alternate);
            if (!world && inv.Complexity != 0) r.Transform (inv);
            return r;
        }

        /// <summary>CalculateGradientArray @180151680 with the segment lengths.</summary>
        static bool GradientArray (PointF[] g, float[] len, PointF[] p, int n)
        {
            if (n < 2) return false;
            PointF first = p [0], prev = default;
            bool found = false;
            for (int k = n - 1; k >= 1; k--) {
                prev = p [k];
                if (prev.X != first.X || prev.Y != first.Y) { found = true; break; }
            }
            if (!found) return false;
            for (int i = 0; i <= n; i++) {
                PointF cur = i < n ? p [i] : first;
                float dx = cur.X - prev.X, dy = cur.Y - prev.Y;
                float l = dy * dy + dx * dx;
                if (0f < l) {
                    l = MathF.Sqrt (l);
                    dx = dx / l;
                    dy = dy / l;
                }
                g [i] = new PointF (dx, dy);
                len [i] = l;
                prev = cur;
            }
            if (g [n].X == 0f && g [n].Y == 0f) {
                for (int i = 1; i < n; i++)
                    if (g [i].X != 0f || g [i].Y != 0f) { g [n] = g [i]; len [n] = len [i]; break; }
            }
            return true;
        }

        static void DashData (List<PointF> outP, List<byte> outT, float penW, float offset, float[] dash, int count,
                              PointF[] P, int n, bool closed, float[] lens)
        {
            int n9 = n;
            if (closed && n != 0 && P [0].X == P [n - 1].X) {
                n9 = n - 1;
                if (P [0].Y != P [n - 1].Y) n9 = n;
            }
            int start = outP.Count;
            float total = 0f;
            for (int i = 0; i < count; i++) total = dash [i] + total;
            float rem;
            if (offset <= 0f) {
                if (0f <= offset) rem = 0f;
                else {
                    rem = -offset - (float) (int) (-offset / total) * total;
                    if (0f < rem) rem = total - rem;
                }
            } else rem = offset - (float) (int) (offset / total) * total;
            int di = 0;
            while (true) {
                if (di < 0 || count <= di) break;
                float d = dash [di];
                if (rem < d) { rem = d - rem; break; }
                di++;
                rem = rem - d;
            }
            if (di >= count) di = 0;
            bool on = (di & 1) == 0;
            di = (di + 1) % count;
            float next = dash [di];
            if (next == 0f && on && di == count - 1) {
                di = (di + 1) % count; rem = rem + dash [di];
                di = (di + 1) % count; next = dash [di];
            }
            bool cont = false;
            int i9, li;
            PointF cur;
            float segRem;
            if (!closed) {
                i9 = 1; li = 1;
                cur = P [0];
                segRem = lens [1];
            } else {
                i9 = 0; li = 0;
                cur = P [n9 - 1];
                double dx = (double) (P [0].X - cur.X), dy = (double) (P [0].Y - cur.Y);
                double d = Math.Sqrt (dy * dy + dx * dx);
                segRem = 1.1920928955078125e-07 <= Math.Abs (d) ? (float) d : 0f;
            }
            while (0 <= i9 && i9 < n9) {
                PointF T = P [i9];
                if (rem <= segRem) {
                    float t = rem / segRem;
                    segRem = segRem - rem;
                    var np = new PointF ((T.X - cur.X) * t + cur.X, (T.Y - cur.Y) * t + cur.Y);
                    if (on) Emit (outP, outT, cur, np, !cont, ref cont, false);
                    cur = np;
                    on = (di & 1) == 0;
                    int d2 = (di + 1) % count;
                    float nn = dash [d2];
                    float r2 = next;
                    di = d2;
                    if (nn == 0f && on && di == count - 1) {
                        di = (di + 1) % count; r2 = next + dash [di];
                        di = (di + 1) % count; nn = dash [di];
                    }
                    rem = r2;
                    next = nn;
                    cont = false;
                } else {
                    rem = rem - segRem;
                    if (on) Emit (outP, outT, cur, T, !cont, ref cont, true);
                    else cont = false;
                    li = (li + 1) % n9;
                    i9++;
                    segRem = lens [li];
                    cur = T;
                }
            }
            int oc = outP.Count - start;
            if (!closed && oc != 0 && n9 != 0) {
                float tol = penW * penW * 0.25f;
                PointF a = P [n9 - 1], b = outP [outP.Count - 1];
                if ((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) < tol) outT [outT.Count - 1] &= 0xef;
                a = P [0]; b = outP [start];
                if ((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) < tol) outT [start] &= 0xef;
            }
        }

        static void Emit (List<PointF> outP, List<byte> outT, PointF a, PointF b, bool startNew, ref bool cont, bool track)
        {
            if (1.1920929e-07f <= MathF.Abs (a.X - b.X) || 1.1920929e-07f <= MathF.Abs (a.Y - b.Y)) {
                if (startNew) { outP.Add (a); outT.Add (0x10); }
                outP.Add (b); outT.Add (0x11);
                if (track) cont = true;
            } else if (track) cont = false;
        }
    }
}
