// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s GpPathWidener, read out of gdiplus.dll (arm64, public PDB): a flattened path becomes the
// outline of its stroke. Every float expression keeps the binary's operand order.
//
//   Initialize @18001fb70        the widening space (world, the pen's own space when it has a
//                                transform, device space for a pen thinner than a device pixel),
//                                the pen's widths there, the subpaths copied without repeated points
//   Widen @180020b80             per subpath: caps (anchors and custom caps are drawn by the cap
//                                creator, here they become the dash cap), square caps as negative
//                                insets, gradients, then each compound band (Center: +-w/2, Inset:
//                                0..-w, mirrored w..0)
//   CalculateGradients @18009ef78 / CalculateGradientArray @180151680   unit segment directions,
//                                entry 0 the closing direction
//   CalculateNormals @18009eff8  (gy, -gx)
//   WidenSubpath @180021aa0 / WidenFirstPoint @180021140 / WidenLinePoints @180021598   the left and
//                                right offset outlines, a join at every vertex
//   getMiterBevelJoin @180022378, getRoundJoin @1800a0500 + getSmallRoundJoin @180022910,
//   getMiterExceeded @1800a04a0, getTurningDirection @180022d20
//   SetRoundCap @18009f660, SetDoubleRoundCap @18009f420, SetTriangleCap @18009f868, SetCaps
//                @18009f318, AddCompoundCaps @18009eed8, CombineClosedCaps @18009f1a0
//   CombineSubpathOutlines @18001f640 + modifyEdges @1800a07e0, combineTwoOpenSegments
//                @18009fef8, combineClosedSegments @1801e6118, CombinePaths @18001f118
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpPathWidener
    {
        const float Eps = 1.1920929e-07f;
        const float Tol = 0.00059604645f;

        readonly DpPen _pen;
        readonly bool _inset;          // +0x150
        bool _device;                  // +0x1d8: widened in device space
        GpMatrix _m;                   // +0x160
        GpMatrix _inv;                 // +0x190
        bool _penSpace;                // bVar6: widened in the pen transform's space
        float _unitScale;              // +0x1c0
        float _width;                  // +0x1c4
        float _width8;                 // +0x1c8
        float _minorWidth, _majorWidth;   // +0x1cc, +0x1d0
        public bool Valid;

        readonly List<PointF> _pts = new List<PointF> ();   // +0x70
        readonly List<byte> _types = new List<byte> ();     // +0x50

        float _startInset, _endInset;   // +0x268, +0x2b0
        PointF[] _grads = new PointF [0];   // +0x90
        int _gradCount;
        PointF[] _norms = new PointF [0];   // +0xb0

        readonly List<PointF> _lp = new List<PointF> (), _rp = new List<PointF> ();   // +0xf0, +0x130
        readonly List<byte> _lt = new List<byte> (), _rt = new List<byte> ();         // +0xd0, +0x110
        readonly List<PointF> _scp = new List<PointF> (), _ecp = new List<PointF> (); // +0x248, +0x290
        readonly List<byte> _sct = new List<byte> (), _ect = new List<byte> ();       // +0x228, +0x270

        public GpPathWidener (GpPath path, DpPen pen, GpMatrix? matrix, float dpiX, float dpiY, bool inset)
        {
            _pen = pen;
            _inset = inset;
            Initialize (path.PointArray (), path.TypeArray (), matrix, dpiX, dpiY);
        }

        // ---- Initialize -------------------------------------------------------------------------

        void Initialize (PointF[] src, byte[] types, GpMatrix? matrix, float dpiX, float dpiY)
        {
            int n = src.Length;
            if (n < 1) return;
            _device = _pen.Unit != 0;
            GpMatrix penXf = _pen.Xform;
            bool penSpace = false;
            if (_pen.Unit == 0 && (penXf.Complexity & ~1) != 0) {
                penSpace = true;
                penXf.Dx = 0; penXf.Dy = 0;
                penXf.Complexity &= ~1;
            }
            _m = matrix ?? GpMatrix.CreateIdentity ();
            if (penSpace) _m = GpMatrix.Multiply (penXf, _m);
            if (MathF.Abs (dpiX) < Eps || MathF.Abs (dpiY) < Eps) { dpiX = 96f; dpiY = 96f; }
            float w = _pen.Width;
            _width = w;
            float minorWidth, widthOut = w;
            if (!_device) {
                GpStroke.MajorMinor (_m, out float major, out float minor);
                _majorWidth = w * major;
                _unitScale = minor <= major ? minor : major;
                minorWidth = w * minor;
            } else {
                float s = DpPen.DeviceWidth (1f, _pen.Unit, dpiX);
                minorWidth = w * s;
                _unitScale = s;
                _width = minorWidth;
                widthOut = minorWidth;
            }
            _minorWidth = minorWidth;
            _width8 = widthOut;
            float thresh = 1.0000010f;
            if (_inset) thresh = _pen.DashStyle == 0 ? 2.000002f : 4.000004f;
            if (!(thresh <= minorWidth)) {
                // Thinner than a device pixel: a device-space pen of the threshold width.
                _width = thresh;
                _device = true;
                _majorWidth = thresh;
                _minorWidth = thresh;
                _m = matrix ?? GpMatrix.CreateIdentity ();
                penSpace = false;
            }
            _penSpace = penSpace;
            _inv = _m;
            if (!_inv.IsInvertible) return;
            _inv.Invert ();
            var pts = src;
            if ((penSpace && !_device) || (_device && _m.Complexity != 0)) {
                pts = (PointF[]) src.Clone ();
                if (penSpace && !_device) {
                    GpMatrix pi = penXf;
                    pi.Invert ();
                    pi.Transform (pts);
                } else _m.Transform (pts);
            }
            bool allSame = true;
            for (int i = 1; i < n; i++)
                if (pts [i - 1].X != pts [i].X || pts [i - 1].Y != pts [i].Y) { allSame = false; break; }
            if (allSame) return;

            // The subpaths, each point that does not repeat its predecessor (within epsilon).
            int s0 = 0;
            while (s0 < n) {
                int e = s0;
                while (e + 1 < n && (types [e + 1] & 7) != 0) e++;
                PointF last = pts [s0];
                _pts.Add (last);
                _types.Add ((byte) ((types [s0] & 0x10) != 0 ? 0x10 : 0));
                for (int i = s0 + 1; i <= e; i++) {
                    if ((types [i] & 7) != 1) continue;
                    PointF p = pts [i];
                    if (Eps < MathF.Abs (p.X - last.X) || Eps < MathF.Abs (p.Y - last.Y)) {
                        _pts.Add (p); _types.Add (1);
                        last = p;
                    }
                }
                int li = _types.Count - 1;
                bool closed = e - s0 + 1 >= 2 && (types [e] & 0x80) != 0;
                byte t = (byte) (closed ? _types [li] | 0x80 : _types [li] & 0x7f);
                t = (byte) ((types [e] & 0x10) != 0 ? t | 0x10 : t & 0xef);
                _types [li] = t;
                s0 = e + 1;
            }
            // Lone start points go.
            for (int i = 0; i < _types.Count; i++) {
                if ((_types [i] & 7) == 0 && (i == _types.Count - 1 || (_types [i + 1] & 7) == 0)) {
                    _pts.RemoveAt (i); _types.RemoveAt (i); i--;
                }
            }
            Valid = _pts.Count > 0;
        }

        // ---- Widen ------------------------------------------------------------------------------

        public GpPath Widen ()
        {
            if (!Valid) return null;
            var outP = new List<PointF> ();
            var outT = new List<byte> ();
            int cc = _pen.CompoundCount;
            float[] bands;
            int nb;
            if (cc < 1) { bands = new [] { 0f, _width }; nb = 2; }
            else if (_unitScale * _width8 < 0.5f) { bands = new [] { 0f, 0.5f }; nb = Math.Min (cc, 2); }
            else {
                bands = new float [cc];
                for (int i = 0; i < cc; i++) bands [i] = _width8 * _pen.Compound [i];
                nb = cc;
            }
            bool mirrored = false;
            if (_pen.Alignment == 1 && _m.IsInvertible)
                mirrored = _m.M22 * _m.M11 - _m.M21 * _m.M12 < 0f;
            float w = _width;
            bool center;
            float baseOff;
            if (_pen.Alignment == 1) { baseOff = mirrored ? w : 0f; center = false; }
            else { baseOff = w * 0.5f; center = true; }
            float rightOff = baseOff - w;

            int n = _pts.Count;
            int s = 0;
            while (s < n) {
                int e = s;
                while (e + 1 < n && (_types [e + 1] & 7) != 0) e++;
                bool closed = e - s + 1 >= 2 && (_types [e] & 0x80) != 0;
                int sc = _pen.StartCap, ec = _pen.EndCap;
                if ((_types [s] & 0x10) != 0 || (sc & 0xf0) != 0 || sc == 0xff) sc = _pen.DashCap;
                if ((_types [e] & 0x10) != 0 || (ec & 0xf0) != 0 || ec == 0xff) ec = _pen.DashCap;
                if (_inset) {
                    sc = (sc & ~2) == 0 ? sc : 0;
                    ec = (ec & ~2) == 0 ? ec : 0;
                }
                _startInset = sc == 1 ? _width * -0.5f : 0f;
                _endInset = ec == 1 ? _width * -0.5f : 0f;
                if (CalculateGradients (s, e)) {
                    int bsc = nb >= 3 ? 0 : sc, bec = nb >= 3 ? 0 : ec;
                    for (int i = 0; i + 1 < nb; i += 2) {
                        float r = baseOff - bands [i + 1];
                        float l = baseOff - bands [i];
                        if (Eps < MathF.Abs (l - r)) {
                            CalculateNormals ();
                            WidenSubpath (outP, outT, l, r, s, e, closed, bsc, bec, center && nb < 3);
                        }
                    }
                    if (nb > 2 && !closed) AddCompoundCaps (outP, outT, baseOff, rightOff, s, e, sc, ec);
                }
                s = e + 1;
            }
            var pts = outP.ToArray ();
            if (!_device) {
                if ((_pen.Xform.Complexity & ~1) != 0) {
                    GpMatrix t = _pen.Xform;
                    t.Dx = 0; t.Dy = 0; t.Complexity &= ~1;
                    t.Transform (pts);
                }
            } else if (_inv.Complexity != 0) _inv.Transform (pts);
            var types = outT.ToArray ();
            for (int i = 0; i < types.Length; i++) types [i] = (byte) (types [i] & 0xbf);
            return new GpPath (pts, types, FillMode.Winding);
        }

        bool CalculateGradients (int s, int e)
        {
            int n = e - s + 1;
            if (n < 1) return false;
            _grads = new PointF [n + 1];
            _gradCount = n + 1;
            return GradientArray (_grads, _pts, s, n);
        }

        /// <summary>CalculateGradientArray: entry i the unit direction from point i-1 to point i,
        /// entry 0 from the last point unlike the first, entry n back to the first.</summary>
        static bool GradientArray (PointF[] g, List<PointF> p, int s, int n)
        {
            if (n < 2) return false;
            PointF first = p [s];
            PointF prev = default;
            bool found = false;
            for (int k = n - 1; k >= 1; k--) {
                prev = p [s + k];
                if (prev.X != first.X || prev.Y != first.Y) { found = true; break; }
            }
            if (!found) return false;
            for (int i = 0; i <= n; i++) {
                PointF cur = i < n ? p [s + i] : first;
                float dx = cur.X - prev.X, dy = cur.Y - prev.Y;
                float len = dy * dy + dx * dx;
                if (0f < len) {
                    len = MathF.Sqrt (len);
                    dx = dx / len;
                    dy = dy / len;
                }
                g [i] = new PointF (dx, dy);
                prev = cur;
            }
            if (g [n].X == 0f && g [n].Y == 0f) {
                for (int i = 1; i < n; i++)
                    if (g [i].X != 0f || g [i].Y != 0f) { g [n] = g [i]; break; }
            }
            return true;
        }

        void CalculateNormals ()
        {
            _norms = new PointF [_gradCount];
            for (int i = 0; i < _gradCount; i++) _norms [i] = new PointF (_grads [i].Y, -_grads [i].X);
        }

        // ---- the outlines -----------------------------------------------------------------------

        void WidenSubpath (List<PointF> outP, List<byte> outT, float wl, float wr, int s, int e, bool closed, int sc, int ec, bool center)
        {
            PointF p0 = _pts [s], pe = _pts [e];
            bool coincide = p0.X == pe.X && p0.Y == pe.Y;
            float limit = _pen.MiterLimit;
            float limit2 = limit * limit;
            int join = _pen.Join;
            _lp.Clear (); _lt.Clear (); _rp.Clear (); _rt.Clear ();
            int flags = closed ? 3 : 2;
            if (coincide) flags |= 8;
            if (center) flags |= 0x20;
            float dist = MathF.Sqrt ((p0.X - pe.X) * (p0.X - pe.X) + (p0.Y - pe.Y) * (p0.Y - pe.Y));
            float sIns = _startInset;
            if (dist <= sIns) sIns = dist;
            float eIns = _endInset;
            if (dist - sIns <= eIns) eIns = dist - sIns;
            WidenFirstPoint (wl, wr, join, limit2, p0, sIns, flags, out PointF lastPoint);
            int lf = (closed ? 1 : 0) | 2 | 4 | (coincide ? 8 : 0) | (center ? 0x20 : 0);
            WidenLinePoints (wl, wr, join, limit2, s, e - s + 1, ref lastPoint, eIns, lf);

            _scp.Clear (); _sct.Clear (); _ecp.Clear (); _ect.Clear ();
            PointF gs = _grads [1], ge = _grads [e - s];
            if (sc == 2) { if (!_inset) SetRoundCap (p0, gs, true, wl, wr); else SetDoubleRoundCap (p0, gs, true, wl, wr); }
            else if (sc == 3) SetTriangleCap (p0, gs, true, wl, wr);
            if (ec == 2) { if (!_inset) SetRoundCap (pe, ge, false, wl, wr); else SetDoubleRoundCap (pe, ge, false, wl, wr); }
            else if (ec == 3) SetTriangleCap (pe, ge, false, wl, wr);
            CombineSubpathOutlines (outP, outT, closed);
        }

        void Join (int join, PointF p, PointF g1, PointF g2, PointF n1, PointF n2, float wl, float wr, float limit2, bool center,
                   PointF[] lb, out int lc, out bool lflag, PointF[] rb, out int rc, out bool rflag)
        {
            if (join == 2) {
                RoundJoin (p, g1, g2, n1, n2, wl, wr, out lc, lb, out lflag, out rc, rb, out rflag, center);
                return;
            }
            bool useMiter = join == 0 || (join == 3 && !MiterExceeded (g1, g2, limit2));
            MiterBevelJoin (p, g1, g2, n1, n2, wl, wr, out lc, lb, out lflag, out rc, rb, out rflag, false, limit2, useMiter, center);
        }

        void WidenFirstPoint (float wl, float wr, int join, float limit2, PointF p, float inset, int flags, out PointF lastPoint)
        {
            lastPoint = p;
            if ((flags & 2) == 0) return;
            bool closed = (flags & 1) != 0;
            if (!closed) join = 1;
            PointF g1 = _grads [0], g2 = _grads [1], n1 = _norms [0], n2 = _norms [1];
            var lb = new PointF [16];
            var rb = new PointF [16];
            Join (join, p, g1, g2, n1, n2, wl, wr, limit2, (flags & 0x20) != 0, lb, out int lc, out bool lflag, rb, out int rc, out bool rflag);
            if (!closed) {
                if (lc != 1) lb [0] = lb [1];
                if (rc != 1) rb [0] = rb [1];
                if (inset != 0f) {
                    lb [0] = new PointF (inset * g2.X + lb [0].X, inset * g2.Y + lb [0].Y);
                    rb [0] = new PointF (inset * g2.X + rb [0].X, inset * g2.Y + rb [0].Y);
                }
                _lp.Add (lb [0]); _lt.Add (0);
                _rp.Add (rb [0]); _rt.Add (0);
            } else {
                Emit (_lp, _lt, lb, lc, lflag, 0);
                Emit (_rp, _rt, rb, rc, rflag, 0);
            }
        }

        /// <summary>A join's points onto an outline: lines (count &gt; 0) or Bezier points (count
        /// &lt; 0), the first typed <paramref name="first"/> (with 0x40 inside a turn).</summary>
        static void Emit (List<PointF> pts, List<byte> types, PointF[] b, int c, bool flag, byte first)
        {
            byte t = 1;
            if (c < 0) { c = -c; t = 3; }
            if (c <= 0) return;
            if (flag) t |= 0x40;
            for (int i = 0; i < c; i++) { pts.Add (b [i]); types.Add (i == 0 ? (byte) (flag ? first | 0x40 : first) : t); }
        }

        void WidenLinePoints (float wl, float wr, int join, float limit2, int s, int count, ref PointF lastPoint, float inset, int flags)
        {
            bool isLast = (flags & 4) != 0;
            int last1 = count - 1;
            int stop = last1;
            if (isLast && (flags & 1) != 0) {
                if ((flags & 8) == 0) isLast = false;
                else stop = count - 2;
            }
            bool center = (flags & 0x20) != 0;
            bool endMode = false;
            PointF g1 = _grads [1], n1 = _norms [1];
            var lb = new PointF [16];
            var rb = new PointF [16];
            for (int i = 0; i < stop; i++) {
                PointF p = _pts [s + i + 1];
                if (isLast && i == last1 - 1) { endMode = true; join = 1; }
                if (lastPoint.X == p.X && lastPoint.Y == p.Y) continue;
                PointF g2 = _grads [i + 2], n2 = _norms [i + 2];
                Join (join, p, g1, g2, n1, n2, wl, wr, limit2, center, lb, out int lc, out bool lflag, rb, out int rc, out bool rflag);
                if (endMode) {
                    lc = 1; rc = 1; lflag = false; rflag = false;
                    if (inset != 0f) {
                        lb [0] = new PointF (lb [0].X - inset * g1.X, lb [0].Y - inset * g1.Y);
                        rb [0] = new PointF (rb [0].X - inset * g1.X, rb [0].Y - inset * g1.Y);
                    }
                }
                Emit (_lp, _lt, lb, lc, lflag, 1);
                Emit (_rp, _rt, rb, rc, rflag, 1);
                g1 = g2; n1 = n2;
                lastPoint = p;
            }
        }

        // ---- joins ------------------------------------------------------------------------------

        static bool MiterExceeded (PointF g1, PointF g2, float limit2)
        {
            float c = g1.X * g2.Y - g2.X * g1.Y;
            if (!(Eps < MathF.Abs (c))) return true;
            float dy = g1.Y - g2.Y, dx = g1.X - g2.X;
            return 0f < (dy * dy + dx * dx) - c * c * limit2;
        }

        static int TurningDirection (out float cross, PointF g1, PointF g2)
        {
            cross = 0f;
            if (Eps <= MathF.Abs (g1.X) || Eps <= MathF.Abs (g1.Y)) {
                if (Eps <= MathF.Abs (g2.X) || Eps <= MathF.Abs (g2.Y)) {
                    float dy = g1.Y - g2.Y;
                    if (9.9999997e-06f <= (g1.X - g2.X) * (g1.X - g2.X) + dy * dy) {
                        float c = g1.X * g2.Y - g2.X * g1.Y;
                        int d;
                        if (Eps < MathF.Abs (c)) d = c <= 0f ? 3 : 2;
                        else { d = 1; c = 0f; }
                        cross = c;
                        return d;
                    }
                }
            }
            return 0;
        }

        static int MiterBevelJoin (PointF p, PointF g1, PointF g2, PointF n1, PointF n2, float wl, float wr,
                                   out int lc, PointF[] lp, out bool lflag, out int rc, PointF[] rp, out bool rflag,
                                   bool thin, float limit2, bool useMiter, bool center)
        {
            lflag = false; rflag = false;
            bool miter = 1f < limit2 && useMiter;
            if ((Eps <= MathF.Abs (g1.X) || Eps <= MathF.Abs (g1.Y)) && (Eps <= MathF.Abs (g2.X) || Eps <= MathF.Abs (g2.Y))) {
                float d9 = g1.Y - g2.Y;
                if (9.9999997e-06f <= (g1.X - g2.X) * (g1.X - g2.X) + d9 * d9) {
                    float cross = g1.X * g2.Y - g2.X * g1.Y;
                    int dir;
                    if (Eps < MathF.Abs (cross)) dir = cross <= 0f ? 3 : 2;
                    else { dir = 1; cross = 0f; }
                    bool rf;
                    if (cross <= 0f) { lflag = 0f < wl; rf = 0f < wr; }
                    else { lflag = wl < 0f; rf = wr < 0f; }
                    rflag = rf;
                    bool bl = false, br = false;
                    float f15 = 0f, f19 = 0f, f12 = 0f, f9 = 0f;
                    float vx = 0f, vy = 0f;
                    if (miter && cross != 0f) {
                        vx = g1.X - g2.X; vy = g1.Y - g2.Y;
                        if ((vy * vy + vx * vx) - cross * cross * limit2 <= 0f) {
                            vx = vx / cross; vy = vy / cross;
                            if (cross <= 0f) {
                                br = true; rflag = false;
                                if (!center) { bl = true; lflag = false; } else bl = !lflag;
                            } else {
                                bl = true; lflag = false;
                                if (!center) { br = true; rflag = false; } else br = !rflag;
                            }
                        } else {
                            // Past the limit: the miter cut off at the limit.
                            float f10 = 1f, f11 = 1f, f22 = 0f, f20 = 0f;
                            float m = f10 <= f11 ? f11 : f10;
                            if (m < limit2) {
                                if (!lflag) {
                                    float sg = cross <= 0f ? -wl : wl;
                                    f15 = (MathF.Sqrt (f22 * f22 + (limit2 - f10)) - f22) * sg;
                                    f12 = (MathF.Sqrt (f20 * f20 + (limit2 - f11)) + f20) * sg;
                                }
                                if (!rf) {
                                    float sg = cross <= 0f ? -wr : wr;
                                    f19 = (MathF.Sqrt (f22 * f22 + (limit2 - f10)) - f22) * sg;
                                    f9 = (MathF.Sqrt (f20 * f20 + (limit2 - f11)) + f20) * sg;
                                }
                            }
                        }
                    }
                    if (bl) {
                        lp [0] = new PointF (vx * wl + p.X, vy * wl + p.Y);
                        lc = 1;
                    } else {
                        float ax = n1.X * wl + p.X + f15 * g1.X;
                        float ay = n1.Y * wl + p.Y + g1.Y * f15;
                        float bx = (n2.X * wl + p.X) - g2.X * f12;
                        float by = (n2.Y * wl + p.Y) - g2.Y * f12;
                        lp [0] = new PointF (ax, ay);
                        lp [1] = new PointF (bx, by);
                        if (MathF.Abs (bx - ax) + MathF.Abs (by - ay) <= Tol) { lflag = false; lc = 1; }
                        else lc = 2;
                    }
                    if (br) {
                        rp [0] = new PointF (vx * wr + p.X, vy * wr + p.Y);
                    } else {
                        float ax = n1.X * wr + p.X + f19 * g1.X;
                        float ay = wr * n1.Y + p.Y + f19 * g1.Y;
                        float bx = (n2.X * wr + p.X) - g2.X * f9;
                        float by = (n2.Y * wr + p.Y) - g2.Y * f9;
                        rp [0] = new PointF (ax, ay);
                        rp [1] = new PointF (bx, by);
                        if (Tol < MathF.Abs (bx - ax) + MathF.Abs (by - ay)) { rc = 2; return dir; }
                        rflag = false;
                    }
                    rc = 1;
                    return dir;
                }
            }
            PointF nn = n1.X == 0f && n1.Y == 0f ? n2 : n1;
            lp [0] = new PointF (nn.X * wl + p.X, nn.Y * wl + p.Y);
            lc = 1;
            rp [0] = new PointF (nn.X * wr + p.X, nn.Y * wr + p.Y);
            rc = 1;
            return 0;
        }

        static int RoundJoin (PointF p, PointF g1, PointF g2, PointF n1, PointF n2, float wl, float wr,
                              out int lc, PointF[] lp, out bool lflag, out int rc, PointF[] rp, out bool rflag, bool center)
        {
            float dot = g2.Y * g1.Y + g1.X * g2.X;
            lflag = false; rflag = false;
            int dir = TurningDirection (out float cross, g1, g2);
            if (-0.001f < dot) {
                SmallRoundJoin (p, g1, g2, n1, n2, wl, wr, out lc, lp, 0, out rc, rp, 0, dot, cross, 3, center);
                return dir;
            }
            SmallRoundJoin (p, g1, g2, n1, n2, wl, wr, out int tl, lp, 0, out int tr, rp, 0, dot, cross, 2, center);
            int l0 = 0 < tl ? tl : 0, r0 = 0 < tr ? tr : 0;
            lc = l0; rc = r0;
            float sx = n2.X + n1.X, sy = n2.Y + n1.Y;
            float mx, my;
            if (Eps <= MathF.Abs (sx) || Eps <= MathF.Abs (sy)) {
                float len = MathF.Sqrt (sy * sy + sx * sx);
                mx = sx / len; my = sy / len;
            } else { mx = -n1.Y; my = n1.X; }
            var mn = new PointF (mx, my);
            var mg = new PointF (-my, mx);
            float d2 = g1.Y * mx + g1.X * -my;
            float c2 = g1.X * mx - g1.Y * -my;
            SmallRoundJoin (p, g1, mg, n1, mn, wl, wr, out tl, lp, l0, out tr, rp, r0, d2, c2, 1, center);
            int r27 = tl < 0 ? -1 - tl : 0, r26 = tr < 0 ? -1 - tr : 0;
            lc += r27; rc += r26;
            float d3 = g2.Y * mx + g2.X * -my;
            float c3 = g2.Y * -my - g2.X * mx;
            SmallRoundJoin (p, mg, g2, mn, n2, wl, wr, out tl, lp, l0 + r27, out tr, rp, r0 + r26, d3, c3, 1, center);
            int r10 = tl < 0 ? r27 - tl : 0, r9 = tr < 0 ? r26 - tr : 0;
            if (0 < r10) lc = -r10;
            if (0 < r9) rc = -r9;
            return dir;
        }

        static void SmallRoundJoin (PointF p, PointF g1, PointF g2, PointF n1, PointF n2, float wl, float wr,
                                    out int lc, PointF[] lp, int lo, out int rc, PointF[] rp, int ro,
                                    float dot, float cross, int mode, bool center)
        {
            lc = 0; rc = 0;
            if ((mode & 3) == 0) return;
            float n1x = n1.X, n1y = n1.Y;
            if (0.99f <= dot) {
                if ((mode & 2) != 0) {
                    lp [lo] = new PointF (n1x * wl + p.X, n1y * wl + p.Y); lc = 1;
                    rp [ro] = new PointF (n1x * wr + p.X, n1y * wr + p.Y); rc = 1;
                }
                return;
            }
            float ac = cross < 0f ? -cross : cross;
            float t = 1f - dot;
            float k = ((MathF.Sqrt (t + t) - ac) * 4f) / (t * 3f);
            float n2x = n2.X, n2y = n2.Y;
            // a = the side written first (its count is lc unless swapped), b the other
            bool swap = cross < 0f;
            float wa = wl, wb = wr;
            if (swap) {
                wa = -wr; wb = -wl;
                n1x = -n1x; n1y = -n1y; n2x = -n2x; n2y = -n2y;
            }
            PointF[] pa = swap ? rp : lp, pb = swap ? lp : rp;
            int oa = swap ? ro : lo, ob = swap ? lo : ro;
            float dx = g1.X - g2.X, dy = g1.Y - g2.Y;
            bool mid;
            if (MathF.Abs (ac) < Eps || 0f < (dy * dy + dx * dx) - cross * cross * 9f) mid = false;
            else { dx = dx / ac; dy = dy / ac; mid = !center; }
            int ca = Side (p, g1, g2, n1x, n1y, n2x, n2y, wa, k, pa, oa, mode, mid, dx, dy);
            int cb = Side (p, g1, g2, n1x, n1y, n2x, n2y, wb, k, pb, ob, mode, mid, dx, dy);
            if (swap) { rc = ca; lc = cb; } else { lc = ca; rc = cb; }
        }

        static int Side (PointF p, PointF g1, PointF g2, float n1x, float n1y, float n2x, float n2y, float w, float k,
                         PointF[] o, int at, int mode, bool mid, float dx, float dy)
        {
            if (w <= 0f) {
                if ((mode & 2) == 0) return 0;
                if (w == 0f) { o [at] = p; return 1; }
                if (!mid) {
                    o [at] = new PointF (n1x * w + p.X, n1y * w + p.Y);
                    o [at + 1] = new PointF (n2x * w + p.X, n2y * w + p.Y);
                    return 2;
                }
                o [at] = new PointF (dx * w + p.X, dy * w + p.Y);
                return 1;
            }
            if ((mode & 1) == 0) return 0;
            float f = w * k;
            float ax = n1x * w + p.X, ay = n1y * w + p.Y;
            o [at] = new PointF (ax, ay);
            o [at + 1] = new PointF (f * g1.X + ax, g1.Y * f + ay);
            float bx = n2x * w + p.X, by = n2y * w + p.Y;
            o [at + 3] = new PointF (bx, by);
            o [at + 2] = new PointF (bx - g2.X * f, by - g2.Y * f);
            return -4;
        }

        // ---- caps -------------------------------------------------------------------------------

        static readonly float[] s_round = { 1, 0, 1, 0.5522848f, 0.5522848f, 1, 0, 1, -0.5522848f, 1, -1, 0.5522848f, -1, 0 };

        void SetRoundCap (PointF p, PointF g, bool start, float wl, float wr)
        {
            if (MathF.Abs (g.X) < Eps && MathF.Abs (g.Y) < Eps) return;
            List<PointF> cp = start ? _scp : _ecp;
            List<byte> ct = start ? _sct : _ect;
            cp.Clear (); ct.Clear ();
            float gx = start ? -g.X : g.X, gy = start ? -g.Y : g.Y;
            float h = (wl - wr) * 0.5f;
            float ins = start ? _startInset : _endInset;
            float a = h * gy, b = -h * gx;
            float cx = (g.Y * (wl + wr) * 0.5f + p.X) - ins * gx;
            float cy = (p.Y - g.X * (wl + wr) * 0.5f) - ins * gy;
            for (int i = 0; i < 7; i++) {
                float ux = s_round [i * 2], uy = s_round [i * 2 + 1];
                cp.Add (new PointF ((ux * a - uy * b) + cx, uy * a + ux * b + cy));
                ct.Add ((byte) (i == 0 ? 1 : 3));
            }
        }

        void SetDoubleRoundCap (PointF p, PointF g, bool start, float wl, float wr)
        {
            if (MathF.Abs (g.X) < Eps && MathF.Abs (g.Y) < Eps) return;
            List<PointF> cp = start ? _scp : _ecp;
            List<byte> ct = start ? _sct : _ect;
            cp.Clear (); ct.Clear ();
            float gx = start ? -g.X : g.X, gy = start ? -g.Y : g.Y;
            float h = (wl - wr) * 0.5f;
            float ins = start ? _startInset : _endInset;
            float a = h * gy, b = -h * gx;
            var u = new float [28];
            for (int i = 0; i < 7; i++) {
                float ux = s_round [i * 2], uy = s_round [i * 2 + 1];
                u [14 + i * 2] = ux * 0.5f - 0.5f; u [15 + i * 2] = uy * 0.5f;
                u [i * 2] = ux * 0.5f + 0.5f; u [i * 2 + 1] = uy * 0.5f;
            }
            float cx = ((wl + wr) * g.Y * 0.5f + p.X) - ins * gx;
            float cy = (p.Y - g.X * (wl + wr) * 0.5f) - ins * gy;
            for (int i = 0; i < 14; i++) {
                float ux = u [i * 2], uy = u [i * 2 + 1];
                cp.Add (new PointF ((ux * a - uy * b) + cx, uy * a + ux * b + cy));
                ct.Add ((byte) (i == 0 || i == 7 ? 1 : 3));
            }
        }

        void SetTriangleCap (PointF p, PointF g, bool start, float wl, float wr)
        {
            if (MathF.Abs (g.X) < Eps && MathF.Abs (g.Y) < Eps) return;
            List<PointF> cp = start ? _scp : _ecp;
            List<byte> ct = start ? _sct : _ect;
            cp.Clear (); ct.Clear ();
            float nx = g.Y, ny = -g.X;
            float dx = start ? ny : g.X, dy = start ? -nx : g.Y;
            float lx = nx * wl + p.X, ly = ny * wl + p.Y;
            float rx = nx * wr + p.X, ry = ny * wr + p.Y;
            float aw = MathF.Abs (wl - wr);
            var tip = new PointF ((rx + lx + aw * dx) * 0.5f, (ry + ly + aw * dy) * 0.5f);
            if (!start) { cp.Add (new PointF (lx, ly)); cp.Add (tip); cp.Add (new PointF (rx, ry)); }
            else { cp.Add (new PointF (rx, ry)); cp.Add (tip); cp.Add (new PointF (lx, ly)); }
            ct.Add (1); ct.Add (1); ct.Add (1);
        }

        void AddCompoundCaps (List<PointF> outP, List<byte> outT, float wl, float wr, int s, int e, int sc, int ec)
        {
            PointF p0 = _pts [s], pe = _pts [e];
            PointF gs = _grads [1], ge = _grads [e - s];
            _scp.Clear (); _sct.Clear (); _ecp.Clear (); _ect.Clear ();
            if (sc == 2) { if (!_inset) SetRoundCap (p0, gs, true, wl, wr); else SetDoubleRoundCap (p0, gs, true, wl, wr); }
            else if (sc == 3) SetTriangleCap (p0, gs, true, wl, wr);
            if (ec == 2) { if (!_inset) SetRoundCap (pe, ge, false, wl, wr); else SetDoubleRoundCap (pe, ge, false, wl, wr); }
            else if (ec == 3) SetTriangleCap (pe, ge, false, wl, wr);
            if (_scp.Count == 0 && _ecp.Count == 0) return;
            if (outT.Count > 0) outT [outT.Count - 1] |= 0x80;
            int start = outP.Count;
            if (_scp.Count > 0) { _sct [_sct.Count - 1] |= 0x80; AppendClosed (outP, outT, start, _scp, _sct); }
            if (_ecp.Count > 0) { _ect [_ect.Count - 1] |= 0x80; AppendClosed (outP, outT, start, _ecp, _ect); }
        }

        /// <summary>combineClosedSegments: a closed figure after what <paramref name="start"/>
        /// begins (the whole run so far starts a figure; its end is closed).</summary>
        static void AppendClosed (List<PointF> outP, List<byte> outT, int start, List<PointF> sp, List<byte> st)
        {
            int n = outP.Count - start;
            if (n == 0 && sp.Count == 0) return;
            if (n > 0) {
                outT [start] = 0;
                if ((outT [outT.Count - 1] & 0x80) == 0) outT [outT.Count - 1] |= 0x80;
            }
            if (sp.Count == 0) return;
            int at = outP.Count;
            outP.AddRange (sp); outT.AddRange (st);
            outT [at] = (byte) (outT [at] & 0xf8);
            outT [outT.Count - 1] |= 0x80;
        }

        // ---- CombineSubpathOutlines -------------------------------------------------------------

        void CombineSubpathOutlines (List<PointF> outP, List<byte> outT, bool closed)
        {
            int lc = _lp.Count, rc = _rp.Count;
            int start = outP.Count;
            if (!closed) {
                // modifyEdges: an end segment that runs backwards along the path is folded away.
                if (_gradCount > 2) {
                    PointF g1 = _grads [1], ge = _grads [_gradCount - 2];
                    Fold (_lp, g1, ge);
                    Fold (_rp, g1, ge);
                }
                bool sClosed = _sct.Count > 0 && (_sct [_sct.Count - 1] & 0x80) != 0;
                bool eClosed = _ect.Count > 0 && (_ect [_ect.Count - 1] & 0x80) != 0;
                if (_scp.Count != 0 || _ecp.Count != 0) {
                    outP.AddRange (_lp); outT.AddRange (_lt);
                    if (_ecp.Count > 0 && !eClosed) AppendOpen (outP, outT, start, _ecp, _ect, false);
                    if (rc > 0) AppendOpen (outP, outT, start, _rp, _rt, true);
                    if (_scp.Count > 0 && !sClosed) AppendOpen (outP, outT, start, _scp, _sct, false);
                    outT [start] = 0;
                } else CombinePaths (outP, outT, start, closed);
                if (outP.Count - start < 1) return;
                outT [outT.Count - 1] |= 0x80;
                if (_ecp.Count > 0 && eClosed) AppendClosed (outP, outT, start, _ecp, _ect);
                if (_scp.Count > 0 && sClosed) AppendClosed (outP, outT, start, _scp, _sct);
                return;
            }
            if (lc > 0) _lt [lc - 1] |= 0x80;
            if (rc > 0) _rt [rc - 1] |= 0x80;
            CombinePaths (outP, outT, start, closed);
        }

        static void Fold (List<PointF> p, PointF g1, PointF ge)
        {
            int n = p.Count;
            if (n <= 2) return;
            if ((p [1].Y - p [0].Y) * g1.Y + (p [1].X - p [0].X) * g1.X < 0f) p [0] = p [1];
            if ((p [n - 1].Y - p [n - 2].Y) * ge.Y + (p [n - 1].X - p [n - 2].X) * ge.X < 0f) p [n - 1] = p [n - 2];
        }

        /// <summary>combineTwoOpenSegments: a segment joined on to the open run, reversed first
        /// when asked; a first point repeating the run's last is dropped.</summary>
        static void AppendOpen (List<PointF> outP, List<byte> outT, int start, List<PointF> sp, List<byte> st, bool reverse)
        {
            PointF[] p = sp.ToArray ();
            byte[] t = st.ToArray ();
            if (reverse && p.Length > 0) ReversePath (p, t);
            int n = outP.Count - start, m = p.Length;
            int skip = 0;
            if (n > 0 && m > 0) {
                PointF l = outP [outP.Count - 1];
                skip = MathF.Abs (l.Y - p [0].Y) + MathF.Abs (l.X - p [0].X) < Tol ? 1 : 0;
            }
            int at = outP.Count;
            for (int i = skip; i < m; i++) { outP.Add (p [i]); outT.Add (t [i]); }
            if (n + m - skip > 0) outT [start] = (byte) ((outT [start] & 0xf8) | 1);
            if (n > 0 && (outT [start + n - 1] & 0x80) != 0) outT [start + n - 1] &= 0x7f;
            if (skip == 0 && m > 0) outT [at] = (byte) ((outT [at] & 0xf8) | 1);
            if (outP.Count - start > 0 && (outT [outT.Count - 1] & 0x80) != 0) outT [outT.Count - 1] &= 0x7f;
        }

        /// <summary>CombinePaths(left, right reversed, connect): the right outline reversed after
        /// the left, joined to it unless it is closed (then it is a figure of its own).</summary>
        void CombinePaths (List<PointF> outP, List<byte> outT, int start, bool closed)
        {
            outP.AddRange (_lp); outT.AddRange (_lt);
            int lc = _lp.Count;
            if (_rp.Count < 1) return;
            PointF[] p = _rp.ToArray ();
            byte[] t = _rt.ToArray ();
            ReversePath (p, t);
            int m = p.Length;
            // The reversed outline's first figure: closed?
            int fe = 0;
            while (fe + 1 < m && (t [fe + 1] & 7) != 0) fe++;
            bool firstClosed = fe + 1 >= 2 && (t [fe] & 0x80) != 0;
            int skip = 0;
            if (lc < 1 || firstClosed) t [0] = (byte) (t [0] & 0xf8);
            else {
                t [0] = (byte) ((t [0] & 0xf8) | 1);
                PointF l = outP [outP.Count - 1];
                if (MathF.Abs (l.Y - p [0].Y) + MathF.Abs (l.X - p [0].X) < Tol) {
                    // the first point goes; its type moves with the rest
                    for (int i = 0; i + 1 < m; i++) { p [i] = p [i + 1]; t [i] = t [i + 1]; }
                    m--;
                }
            }
            for (int i = 0; i < m; i++) { outP.Add (p [i]); outT.Add (t [i]); }
        }

        static void ReversePath (PointF[] p, byte[] t)
        {
            var tmp = new GpPath (FillMode.Winding);
            tmp.Points.AddRange (p);
            tmp.Types.AddRange (t);
            tmp.Reverse ();
            tmp.Points.CopyTo (p);
            tmp.Types.CopyTo (t);
        }
    }
}
