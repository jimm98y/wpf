// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The path gradient brush's spans (gdiplus.dll 10.0.26100, arm64, public PDB), float op for float op:
//
//   GpPathGradient::CreateOutputSpan @18006d750   Clamp, or a draw rectangle whose world bounds lie
//        inside the brush rectangle, draws directly: DpOutputOneDPathGradientSpan when every surround
//        colour is the first (+0xa4), DpOutputPathGradientSpan otherwise. Any other wrap mode renders
//        the brush rectangle into a W x H ARGB GpBitmap (W, H = the rounded device bounds of the
//        transformed rectangle, at most 1000) through a GpGraphics FillRects with the brush made Clamp
//        and untransformed for the while, and fills with a GpTexture of it in the brush's wrap mode,
//        mapped back onto the transformed rectangle
//   DpOutputGradientSpan::DpOutputGradientSpan @180027bd0   brush transform x world-to-device
//   DpOutputOneDGradientSpan::AllocateOneDData @180028de0   a colour table of ceil(|d1| + |d2|) + 2
//        entries, d1 d2 the device diagonals of the brush rectangle
//   DpOutputOneDPathGradientSpan::SetupPathGradientOneDData @1800295e8   entry k at t = k / n: the
//        centre colour towards the first surround colour by slowAdjustValue(t) (or the preset blend),
//        clamped and rounded, through GammaUnlinearizePremultiplied when gamma corrected
//   DpOutputOneDPathGradientSpan::DpOutputOneDPathGradientSpan @180028790   GpPathGradient::Flatten,
//        the centre and the points rounded to 1/16 of a pixel, one GpBilinearTransform per edge (the
//        quad centre, p0, centre, p1 -- or the focus-scaled points -- over the unit square), and with
//        focus scales a second, constant-0 quad per edge inside the focus
//   DpOutputOneDPathGradientSpan::OutputSpan @180029290   the span cleared, then each quad's x spans
//        at the row, its source parameter u per pixel (integer coordinates: pixel centres) indexing
//        the table at floor(n * clamp(u) + 0.5); later quads overwrite earlier ones
//   GpBilinearTransform::SetBilinearTransform @1800264a0, GetXSpans @180026128 (GpQuadAnalyzer::
//        SetQuadAnalyzer @180026720), GetSourceParameter @180025e60 (the quadratic solved for v,
//        then u; a second root tried when the first falls outside [-0.02, 1.02])
//   DpOutputPathGradientSpan::DpOutputPathGradientSpan @1800bdf38   a DpTriangleData per edge: the
//        (unrounded) centre and the edge's two points, their colours linearised and premultiplied
//   DpTriangleData::SetTriangle @1800bf508   the triangle's edges set up by InitializeEdges
//        @18002e290 in 28.4 (RasterizerCeiling of x16) with its extra fields (x-major flag, start
//        and length in pixels, edge index); InitializeInactiveArray @18002e6b8, InsertNewEdges
//        @18015c600
//   DpTriangleData::SetXSpan @1800bf708   the aliased edge walk to the row; the two active edges
//        give the span and the barycentric weights at its ends from the distance along each edge
//   DpOutputPathGradientSpan::OutputSpan @1800beda0   every triangle stepped to the row, the scan
//        asked for the union of their spans only, each triangle's DpTriangleData::OutputSpan
//        @1800bef28 interpolating its weights across its span (blend factors through
//        slowAdjustValue, renormalised) and writing the premultiplied colour
//   GpTexture::CreateOutputSpan @18006dd30   always bilinear: DpOutputBilinearSpan_Identity for an
//        integer translation in Tile or Clamp, else DpOutputBilinearSpan (the MMX one is off). The
//        two texture spans below are this case's own minimal copy of GDI+'s bilinear sampling
//        (DpOutputBilinearSpan::OutputSpan @18002a0a0, DpOutputBilinearSpan_Identity::OutputSpan
//        @18002a650, ApplyWrapMode @180159608), to be unified with the image spans.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        /// <summary>GpPathGradient::CreateOutputSpan. <paramref name="draw"/> is the fill's device
        /// bounds (null when the verb has none).</summary>
        GpSpan CreatePathGradientSpan (PathGradientBrush pb, GpScan scan, Rectangle? draw)
        {
            bool direct = pb.WrapInternal == WrapMode.Clamp;
            if (!direct && draw is Rectangle r) {
                GpMatrix inv = WorldToDevice;
                if (inv.Invert ()) {
                    RectangleF tb = GpPathGradientSpans.TransformBounds (inv, r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                    direct = GpPathGradientSpans.Contains (pb.Rectangle, tb);
                }
            }
            if (direct) {
                GpSpan s = pb.OneSurround
                    ? new OneDPathGradientSpan (scan, pb, WorldToDevice, _ctx.Compositing)
                    : new PathGradientSpan (scan, pb, WorldToDevice, _ctx.Compositing);
                return s;
            }
            return CreatePathGradientTexture (pb, scan);
        }

        GpSpan CreatePathGradientTexture (PathGradientBrush pb, GpScan scan)
        {
            RectangleF rect = pb.Rectangle;
            float x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
            var p = new PointF [] { new PointF (x, y), new PointF (w + x, y), new PointF (x, h + y) };
            GpMatrix bx = pb.Xform;
            bx.Transform (p);
            var d = (PointF[]) p.Clone ();
            WorldToDevice.Transform (d);
            float minX = (d [1].X + d [2].X) - d [0].X, minY = (d [1].Y + d [2].Y) - d [0].Y;
            float maxX = minX, maxY = minY;
            for (int i = 0; i < 3; i++) {
                float px = d [i].X, py = d [i].Y;
                if (minX <= px && !(px <= maxX)) maxX = px;
                if (px < minX) minX = px;
                if (minY <= py && !(py <= maxY)) maxY = py;
                if (py < minY) minY = py;
            }
            int ix0 = (int) MathF.Floor (minX + 0.5f), iy0 = (int) MathF.Floor (minY + 0.5f);
            int ix1 = (int) MathF.Floor (maxX + 0.5f), iy1 = (int) MathF.Floor (maxY + 0.5f);
            int W = Math.Min (1000, ix1 - ix0), H = Math.Min (1000, iy1 - iy0);
            var bmpRect = new RectangleF (0, 0, (float) W, (float) H);
            GpMatrix toBrush = GpMatrix.CreateIdentity ();
            InferAffine (bmpRect, rect, out GpMatrix toBitmap);
            toBitmap.Complexity = toBitmap.ComputeComplexity ();
            GpPathGradientSpans.InferAffine (p, bmpRect, ref toBrush);
            if (W < 1 || H < 1) return null;

            var frame = new GdipFrame (W, H, PixelFormat.Format32bppArgb);
            var g = new GpGraphics (frame);
            g.SetWorld (GpMatrix.Multiply (toBitmap, g.World));
            WrapMode wrap = pb.WrapInternal;
            GpMatrix saved = pb.Xform;
            pb.WrapInternal = WrapMode.Clamp;
            pb.Xform = GpMatrix.CreateIdentity ();
            try {
                g.FillRects (pb, new [] { rect });
            } finally {
                pb.WrapInternal = wrap;
                pb.Xform = saved;
            }
            GdipFrame pargb = GdipPixels.Convert (frame, new Rectangle (0, 0, W, H), PixelFormat.Format32bppPArgb, false);
            var px32 = new uint [W * H];
            for (int row = 0; row < H; row++)
                Buffer.BlockCopy (pargb.Bits, row * pargb.Stride, px32, row * W * 4, W * 4);

            // GpTexture::CreateOutputSpan: the texture transform (toBrush, prepended to the identity)
            // times the world-to-device.
            GpMatrix tm = GpMatrix.Multiply (GpMatrix.Multiply (toBrush, GpMatrix.CreateIdentity ()), WorldToDevice);
            int wm = (int) wrap;
            if (GpPathGradientSpans.IsIntegerTranslate (tm) && (wm & ~4) == 0)
                return new PgTextureIdentitySpan (scan, px32, W, H, wm, tm);
            return new PgTextureBilinearSpan (scan, px32, W, H, wm, tm, _ctx.PixelOffset);
        }
    }

    internal static class GpPathGradientSpans
    {
        internal const float Eps = 1.1920928955078125e-07f;

        internal static int Round (float v) => (int) MathF.Floor (v + 0.5f);     // frintm, fcvtzs
        internal static int Ceil (float v) => (int) MathF.Ceiling (v);           // -floor(-v)
        internal static float Sixteenth (float v) => (float) (int) MathF.Floor (v * 16f + 0.5f) * 0.0625f;

        /// <summary>GpMatrix::IsIntegerTranslate.</summary>
        internal static bool IsIntegerTranslate (in GpMatrix m)
        {
            if ((m.Complexity & ~1) != 0) return false;
            return MathF.Abs ((float) Round (m.Dx) - m.Dx) <= 0.015625f && MathF.Abs ((float) Round (m.Dy) - m.Dy) <= 0.015625f;
        }

        /// <summary>GpMatrix::VectorTransform.</summary>
        internal static void VectorTransform (in GpMatrix m, ref float x, ref float y)
        {
            if (m.Complexity == 0) return;
            float px = x;
            x = y * m.M21 + m.M11 * px;
            y = m.M12 * px + m.M22 * y;
        }

        /// <summary>TransformBounds @1800353d8.</summary>
        internal static RectangleF TransformBounds (in GpMatrix m, float l, float t, float r, float b)
        {
            float x0 = l, y0 = t, x1 = r, y1 = b;
            if (m.Complexity != 0) {
                if ((m.Complexity & ~3) == 0) {
                    m.Transform (ref x0, ref y0);
                    m.Transform (ref x1, ref y1);
                    float a = x0, c = x1;
                    x0 = a < c ? a : c; x1 = a < c ? c : a;
                    a = y0; c = y1;
                    y0 = a < c ? a : c; y1 = a < c ? c : a;
                } else {
                    var p = new PointF [] { new PointF (l, t), new PointF (r, b), new PointF (l, b), new PointF (r, t) };
                    m.Transform (p);
                    x0 = x1 = p [0].X; y0 = y1 = p [0].Y;
                    for (int i = 1; i < 4; i++) {
                        x0 = Math.Min (x0, p [i].X); x1 = Math.Max (x1, p [i].X);
                        y0 = Math.Min (y0, p [i].Y); y1 = Math.Max (y1, p [i].Y);
                    }
                }
            }
            float w = x1 - x0, h = y1 - y0;
            if (w <= 0.0005960464477539062f) w = 0f;
            if (h <= 0.0005960464477539062f) h = 0f;
            return new RectangleF (x0, y0, w, h);
        }

        /// <summary>RectF::Contains(RectF).</summary>
        internal static bool Contains (RectangleF a, RectangleF b)
            => a.X <= b.X && b.Width + b.X <= a.Width + a.X && a.Y <= b.Y && b.Height + b.Y <= a.Height + a.Y;

        /// <summary>GpMatrix::InferAffineMatrix(PointF[3], RectF) @180034750: the rectangle onto the
        /// parallelogram p0 (top left), p1 (top right), p2 (bottom left).</summary>
        internal static bool InferAffine (PointF[] p, RectangleF r, ref GpMatrix m)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float nh = -h;
            float det = ((w + x) * (y + h) - y * x) + (x * nh - y * w);
            if (!(1.1920929e-07f <= MathF.Abs (det))) return false;
            float inv = 1f / det;
            m.M11 = (h * p [1].X + nh * p [0].X) * inv;
            m.M12 = (h * p [1].Y + nh * p [0].Y) * inv;
            m.M21 = (w * p [2].X + -w * p [0].X) * inv;
            m.M22 = (w * p [2].Y + -w * p [0].Y) * inv;
            float k = (x + w) * (y + h) - x * y;
            float a = -x * h, c = -y * w;
            m.Dx = (a * p [1].X + k * p [0].X + c * p [2].X) * inv;
            m.Dy = (a * p [1].Y + k * p [0].Y + c * p [2].Y) * inv;
            m.Complexity = m.ComputeComplexity ();
            return true;
        }

        /// <summary>The colour of a premultiplied float colour as DpTriangleData::OutputSpan and
        /// SetupPathGradientOneDData pack it: alpha clamped to [0, 255], each channel to [0, alpha],
        /// rounded (or through GammaUnlinearizePremultiplied128).</summary>
        internal static uint Pack (float A, float R, float G, float B, bool gamma)
        {
            float a = A < 0f ? 0f : A;
            if (a > 255f) a = 255f;
            float r = R < 0f ? 0f : R; if (r > a) r = a;
            float g = G < 0f ? 0f : G; if (g > a) g = a;
            float b = B < 0f ? 0f : B; if (b > a) b = a;
            if (gamma) return LineGradientSpan.Unlinearize (a, r, g, b);
            return (uint) (Round (a) & 0xff) << 24 | (uint) (Round (r) & 0xff) << 16 | (uint) (Round (g) & 0xff) << 8 | (uint) (Round (b) & 0xff);
        }
    }

    /// <summary>GpBilinearTransform: the unit square onto a quad, P(u, v) = p0 + u (p1 - p0) +
    /// v (p2 - p0) + u v a, inverted per pixel; a non-negative fixed value answers every pixel.</summary>
    internal sealed class GpBilinearTransform
    {
        float _bbX, _bbY, _bbW, _bbH;                    // +0x10
        float _ax, _ay, _bx, _by, _cx, _cy, _ox, _oy;    // +0x20 .. +0x3c
        float _a, _c;                                    // +0x40, +0x44
        float _qyMin, _qyMax;                            // the quad analyser (+0x48)
        readonly float[] _ys = new float [4], _ye = new float [4], _xs = new float [4], _dxdy = new float [4];
        readonly byte[] _flag = new byte [4];
        float _fixed = -1f;                              // +0x9c

        /// <summary>SetBilinearTransform(unit square, 4 points, fixed).</summary>
        public void Set (PointF p0, PointF p1, PointF p2, PointF p3, float fixedValue)
        {
            float maxX = p0.X, minX = p0.X, minY = p0.Y, maxY = p0.Y;
            PointF[] q = { p1, p2, p3 };
            foreach (PointF p in q) {
                float x = p.X;
                if (x >= minX || float.IsNaN (x) || float.IsNaN (minX)) maxX = x > maxX ? x : maxX;
                else minX = x;
                float y = p.Y;
                if (y < minY) minY = y;
                else maxY = y > maxY ? y : maxY;
            }
            _ax = ((p0.X - p1.X) - p2.X) + p3.X;
            _ay = ((p0.Y - p1.Y) - p2.Y) + p3.Y;
            _bx = p1.X - p0.X; _by = p1.Y - p0.Y;
            _cx = p2.X - p0.X; _cy = p2.Y - p0.Y;
            _ox = p0.X; _oy = p0.Y;
            _a = _ax == _cx && _ay == _cy ? 0f : _ax * _cy - _cx * _ay;
            _c = _bx * _cy - _by * _cx;
            _bbX = minX; _bbY = minY; _bbW = maxX - minX; _bbH = maxY - minY;
            SetQuad (p0, p1, p3, p2);
            _fixed = fixedValue;
        }

        // GpQuadAnalyzer::SetQuadAnalyzer: the quad's four edges, each with its y range, the x at its
        // top and dx/dy.
        void SetQuad (PointF q0, PointF q1, PointF q2, PointF q3)
        {
            PointF[] q = { q0, q1, q2, q3 };
            float xMin = q0.X, xMax = q0.X;
            _qyMin = q0.Y; _qyMax = q0.Y;
            for (int k = 0; k < 4; k++) {
                PointF a = q [k], b = q [(k + 1) & 3];
                if (a.Y < b.Y) {
                    _flag [k] = 1; _ys [k] = a.Y; _ye [k] = b.Y; _xs [k] = a.X;
                    _dxdy [k] = (b.X - a.X) / (b.Y - a.Y);
                } else if (b.Y < a.Y) {
                    _flag [k] = 2; _ys [k] = b.Y; _ye [k] = a.Y; _xs [k] = b.X;
                    _dxdy [k] = (b.X - a.X) / (b.Y - a.Y);
                } else {
                    _flag [k] = 0; _ys [k] = a.Y; _ye [k] = a.Y; _xs [k] = a.X; _dxdy [k] = 0f;
                }
                if (a.X < xMin) xMin = a.X;
                else if (xMax < a.X) xMax = a.X;
                if (_qyMin <= a.Y) { if (_qyMax < a.Y) _qyMax = a.Y; }
                else _qyMin = a.Y;
            }
        }

        readonly float[] _xc = new float [4];

        /// <summary>GetXSpans: the quad's x spans on row y within [xmin, xmax), ceiled; their count.</summary>
        public int GetXSpans (int[] o, int y, int xmin, int xmax)
        {
            float fy = y;
            if (fy < _bbY || fy >= _bbH + _bbY) return 0;
            float fr = xmax;
            if (fr < _bbX) return 0;
            float fl = xmin;
            if (fl >= _bbW + _bbX) return 0;
            if (fy < _qyMin || fy >= _qyMax) return 0;
            float[] xs = _xc;
            int n = 0;
            for (int k = 0; k < 4; k++) {
                if (_flag [k] == 0) continue;
                if (fy < _ys [k] || float.IsNaN (fy) || float.IsNaN (_ys [k])) continue;
                if (!(fy < _ye [k])) continue;
                xs [n++] = (fy - _ys [k]) * _dxdy [k] + _xs [k];
            }
            int cnt = n & ~1;
            if (cnt < 2) return 0;
            for (int i = 1; i < cnt; i++)
                for (int j = i; j < cnt; j++)
                    if (xs [j] < xs [i - 1]) { float t = xs [i - 1]; xs [i - 1] = xs [j]; xs [j] = t; }
            float s0 = xs [0], s1 = xs [1], s2 = xs [2], s3 = xs [3];
            int w9;
            if (!(s0 >= fr) && !(s1 <= fl)) {
                xs [0] = s0 > fl ? s0 : fl;
                xs [1] = s1 < fr ? s1 : fr;
                w9 = cnt;
            } else {
                int w8 = cnt;
                if (cnt > 2) {
                    xs [0] = s2; xs [1] = s3;
                    if (!(s2 >= fr) && !(s3 <= fl)) {
                        xs [0] = s2 > fl ? s2 : fl;
                        xs [1] = s3 < fr ? s3 : fr;
                    } else w8 -= 2;
                }
                w9 = w8 - 2;
            }
            if (w9 >= 4) {
                if (!(s2 >= fr) && !(s3 <= fl)) {
                    xs [2] = s2 > fl ? s2 : fl;
                    xs [3] = s3 < fr ? s3 : fr;
                } else w9 -= 2;
            }
            int outN = 0;
            if (w9 <= 0) return 0;
            int pairs = (int) ((uint) (w9 - 1) >> 1) + 1;
            for (int k = 0; k < pairs; k++) {
                int a = GpPathGradientSpans.Ceil (xs [2 * k]), b = GpPathGradientSpans.Ceil (xs [2 * k + 1]);
                o [outN] = a; o [outN + 1] = b;
                if (b > a) outN += 2;
            }
            return outN / 2;
        }

        /// <summary>GetSourceParameter: the (u, v) of device point (px, py); false when there is none
        /// (then <paramref name="u"/> and <paramref name="v"/> are left as they were).</summary>
        public bool GetSourceParameter (float px, float py, ref float u, ref float v)
        {
            if (_fixed >= 0f) { u = _fixed; v = _fixed; return true; }
            float s13 = _oy - py, s12 = _ox - px;
            float B = (_ax * s13 + _c) - _ay * s12;
            float C = _bx * s13 - _by * s12;
            float A = _a;
            int roots;
            float r0 = 0f, r1 = 0f;
            if (A == 0f) {
                if (B == 0f) return false;
                roots = 1;
                r0 = -C / B;
            } else {
                float disc = B * B - (A * 4f) * C;
                if (disc > 0f) {
                    roots = 2;
                    float sq = MathF.Sqrt (disc);
                    float c2 = C + C, a2 = A + A;
                    float q = B < 0f ? sq - B : -B - sq;
                    r0 = c2 / q;
                    r1 = q / a2;
                    if (r0 < 0f || r0 > 1f) {
                        if (!(r1 < 0f || float.IsNaN (r1)) && !(r1 > 1f)) { float t = r0; r0 = r1; r1 = t; }
                    }
                } else if (disc == 0f) {
                    roots = 1;
                    r0 = -B / (A + A);
                } else roots = 0;
                if (roots == 0) return false;
            }
            // u from v = r0
            bool ok0 = true, ok1 = false;
            float u0 = 0f, u1 = 0f, v0 = r0, v1 = 0f;
            float ex = _ax * r0 + _bx, ey = _ay * r0 + _by;
            if (MathF.Abs (ex) > MathF.Abs (ey)) u0 = -(_cx * r0 + s12) / ex;
            else if (MathF.Abs (ey) > 0f) u0 = -(_cy * r0 + s13) / ey;
            else ok0 = false;
            if (roots == 2) {
                const float Lo = -0.019999999552965164f, Hi = 1.0199999809265137f;
                if (!(u0 < Lo) && !(u0 > Hi) && !(r0 < Lo) && !(r0 > Hi) && ok0) {
                    u = u0; v = r0;
                    return true;
                }
                float fx = _ax * r1 + _bx, fy = _ay * r1 + _by;
                float uu;
                bool have = true;
                if (MathF.Abs (fx) > MathF.Abs (fy)) uu = -(_cx * r1 + s12) / fx;
                else if (MathF.Abs (fy) > 0f) uu = -(_cy * r1 + s13) / fy;
                else { uu = 0f; have = false; }
                if (have && !(uu < Lo || float.IsNaN (uu)) && !(uu > Hi || float.IsNaN (uu)) && !(r1 < Lo || float.IsNaN (r1)) && !(r1 > Hi || float.IsNaN (r1))) {
                    u = uu; v = r1;
                    return true;
                }
                ok1 = false;
                _ = v1; _ = u1;
            }
            if (!ok0 && !ok1) return false;
            u = u0; v = v0;
            return true;
        }
    }

    /// <summary>DpOutputOneDPathGradientSpan: one surround colour.</summary>
    internal sealed class OneDPathGradientSpan : GpSpan
    {
        readonly uint[] _table;
        readonly int _n;
        readonly GpBilinearTransform[] _bt;
        readonly bool _valid;
        readonly int[] _spans = new int [8];
        float[] _u = new float [64];

        public OneDPathGradientSpan (GpScan scan, PathGradientBrush pb, in GpMatrix worldToDevice, CompositingMode mode) : base (scan)
        {
            GpMatrix m = GpMatrix.Multiply (pb.Xform, worldToDevice);
            RectangleF rect = pb.Rectangle;
            // AllocateOneDData(1, 0)
            float w = rect.Width, h = rect.Height;
            float x0 = 0, y0 = 0, x1 = w, y1 = 0, x2 = w, y2 = h, x3 = 0, y3 = h;
            GpPathGradientSpans.VectorTransform (m, ref x0, ref y0);
            GpPathGradientSpans.VectorTransform (m, ref x1, ref y1);
            GpPathGradientSpans.VectorTransform (m, ref x2, ref y2);
            GpPathGradientSpans.VectorTransform (m, ref x3, ref y3);
            float ax = x0 - x2, ay = y0 - y2, bx = x1 - x3, by = y1 - y3;
            float lb = MathF.Sqrt (by * by + bx * bx);
            float la = MathF.Sqrt (ay * ay + ax * ax);
            int n = GpPathGradientSpans.Ceil (lb + la);
            if (n < 1) n = 1;
            _n = n;
            _table = new uint [n + 2];
            Setup (pb, mode == CompositingMode.SourceCopy);

            if (pb.GpPath != null) pb.Flatten (m);
            int count = pb.PointCount;
            PointF c = m.Transform (pb.CenterPoint);
            float cx = GpPathGradientSpans.Sixteenth (c.X), cy = GpPathGradientSpans.Sixteenth (c.Y);
            var cp = new PointF (cx, cy);
            PointF focus = pb.FocusScalesInternal;
            float fx = focus.X, fy = focus.Y;
            bool hasFocus = fx != 0f || fy != 0f;
            int total = hasFocus ? count * 2 : count;
            _bt = new GpBilinearTransform [total];
            for (int i = 0; i < total; i++) _bt [i] = new GpBilinearTransform ();
            bool flattened = pb.PointsFlattened;
            for (int i = 0; i < count; i++) {
                pb.GetPoint (i, out PointF p0);
                pb.GetPoint (i < count - 1 ? i + 1 : 0, out PointF p1);
                if (p0.X == p1.X && p0.Y == p1.Y) continue;
                if (!flattened) { p0 = m.Transform (p0); p1 = m.Transform (p1); }
                p0 = new PointF (GpPathGradientSpans.Sixteenth (p0.X), GpPathGradientSpans.Sixteenth (p0.Y));
                p1 = new PointF (GpPathGradientSpans.Sixteenth (p1.X), GpPathGradientSpans.Sixteenth (p1.Y));
                PointF f0 = cp, f1 = cp;
                if (hasFocus) {
                    f0 = new PointF ((p0.X - cx) * fx + cx, (p0.Y - cy) * fy + cy);
                    f1 = new PointF ((p1.X - cx) * fx + cx, (p1.Y - cy) * fy + cy);
                    _bt [i + count].Set (cp, f0, cp, f1, 0f);
                }
                _bt [i].Set (f0, p0, f1, p1, -1f);
            }
            _valid = true;
        }

        // SetupPathGradientOneDData.
        void Setup (PathGradientBrush pb, bool sourceCopy)
        {
            bool gamma = pb.GammaCorrection;
            int s0 = unchecked ((int) 0xff000000);
            pb.GetSurroundColor (0, ref s0);
            LineGradientSpan.Premul ((uint) pb.CenterArgb, gamma, out float ca, out float cr, out float cg, out float cb);
            LineGradientSpan.Premul ((uint) s0, gamma, out float sa, out float sr, out float sg, out float sb);
            float step = 1f / (float) _n;
            float t = 0f;
            int count = pb.BlendCount;
            float f0 = pb.BlendFactor0;
            bool preset = pb.PresetSet && pb.StoredPresetArgb != null && pb.StoredPositions != null && count > 1;
            for (int k = 0; k < _table.Length; k++) {
                float A, R, G, B;
                if (preset)
                    LineGradientSpan.InterpolatePreset (t, count, pb.StoredPresetArgb, pb.StoredPositions, gamma, out A, out R, out G, out B);
                else {
                    float s = t;
                    if (count != 1 || f0 != 1f) s = LineGradientSpan.SlowAdjust (t, count, f0, pb.StoredFactors, pb.StoredPositions);
                    A = (sa - ca) * s + ca;
                    R = (sr - cr) * s + cr;
                    G = (sg - cg) * s + cg;
                    B = (sb - cb) * s + cb;
                }
                if (MathF.Abs (A) >= GpPathGradientSpans.Eps || sourceCopy)
                    _table [k] = GpPathGradientSpans.Pack (A, R, G, B, gamma);
                else
                    _table [k] = 0;
                t = step + t;
            }
        }

        protected override void Fill (uint[] buf, int y, int xmin, int n)
        {
            Array.Clear (buf, 0, n);
            if (!_valid) return;
            if (_u.Length < n) _u = new float [Math.Max (n, _u.Length * 2)];
            int xmax = xmin + n;
            int[] sp = _spans;
            float fy = y;
            foreach (GpBilinearTransform bt in _bt) {
                int ns = bt.GetXSpans (sp, y, xmin, xmax);
                if (ns <= 0) continue;
                int sum = 0;
                for (int j = 0; j < ns; j++) sum = (sp [2 * j + 1] - sp [2 * j]) + sum;
                if (sum > n) continue;
                float u = 0f, v = 0f;
                int idx = 0;
                for (int j = 0; j < ns; j++) {
                    float fx = sp [2 * j];
                    for (int c = sp [2 * j + 1] - sp [2 * j]; c > 0; c--) {
                        bt.GetSourceParameter (fx, fy, ref u, ref v);
                        fx = fx + 1f;
                        _u [idx++] = u;
                    }
                }
                int ptr = 0;
                for (int j = 0; j < ns; j++) {
                    int x0 = sp [2 * j];
                    int room = (xmin - x0) + n;
                    if (room > n) continue;
                    int len = sp [2 * j + 1] - x0;
                    if (len >= 0) {
                        int cnt = (long) room > (long) len ? len : room;
                        int o = x0 - xmin;
                        for (int i = 0; i < cnt; i++) {
                            float t = _u [ptr + i];
                            float tc = t >= 0f || float.IsNaN (t) ? (t < 1f ? t : 1f) : 0f;
                            int k = GpPathGradientSpans.Round ((float) _n * tc);
                            buf [o + i] = _table [k];
                        }
                    }
                    ptr = len;
                }
            }
        }
    }

    /// <summary>DpOutputPathGradientSpan: a triangle per edge.</summary>
    internal sealed class PathGradientSpan : GpSpan
    {
        readonly TriangleData[] _tri;
        readonly bool _valid;
        readonly bool _sourceCopy;

        public PathGradientSpan (GpScan scan, PathGradientBrush pb, in GpMatrix worldToDevice, CompositingMode mode) : base (scan)
        {
            _sourceCopy = mode == CompositingMode.SourceCopy;
            GpMatrix m = GpMatrix.Multiply (pb.Xform, worldToDevice);
            if (pb.GpPath != null) pb.Flatten (m);
            int count = pb.PointCount;
            PointF center = m.Transform (pb.CenterPoint);
            int c0 = unchecked ((int) 0xff000000), c1 = unchecked ((int) 0xff000000);
            int cc = pb.CenterArgb;
            bool flattened = pb.PointsFlattened;
            _tri = new TriangleData [Math.Max (0, count)];
            for (int i = 0; i < count; i++) {
                int j = i < count - 1 ? i + 1 : 0;
                pb.GetPoint (i, out PointF p0);
                pb.GetPoint (j, out PointF p1);
                if (p0.X == p1.X && p0.Y == p1.Y) continue;
                pb.GetSurroundColor (i, ref c0);
                pb.GetSurroundColor (j, ref c1);
                if (!flattened) { p0 = m.Transform (p0); p1 = m.Transform (p1); }
                var t = new TriangleData ();
                t.SetTriangle (center, p0, p1, cc, c0, c1, pb.GammaCorrection);
                t.BlendCount = pb.BlendCount;
                t.Factor0 = pb.BlendFactor0;
                t.Factors = pb.StoredFactors;
                t.Positions = pb.StoredPositions;
                t.PresetArgb = pb.StoredPresetArgb;
                t.Preset = pb.PresetSet;
                _tri [i] = t;
            }
            _valid = true;
        }

        protected override void Fill (uint[] buf, int y, int x, int n) { }

        public override void OutputSpan (int y, int left, int right)
        {
            if (!_valid) return;
            int lo = right, hi = left;
            uint first = 0xffffffff, last = 0;
            for (int i = 0; i < _tri.Length; i++) {
                TriangleData t = _tri [i];
                if (t == null || !t.SetXSpan (y, out int sl, out int sr)) continue;
                if (lo >= sl) lo = sl;
                if (hi <= sr) hi = sr;
                if ((uint) i < first) first = (uint) i;
                last = (uint) i;
            }
            if (lo <= left) lo = left;
            if (hi >= right) hi = right;
            int n = hi - lo;
            if (n <= 0) { Reserve (right - left); return; }
            uint[] buf = NextBuffer (lo, y, n, Math.Max (n, right - left));
            Array.Clear (buf, 0, n);
            for (uint i = first; i <= last; i++) {
                TriangleData t = _tri [i];
                t?.OutputSpan (buf, n, _sourceCopy, y, lo, hi);
            }
        }
    }

    /// <summary>DpTriangleData: one triangle of a multi-colour path gradient (centre, p_i, p_i+1).</summary>
    internal sealed class TriangleData
    {
        sealed class TriEdge
        {
            public TriEdge Next;
            public int X, Dx, Error, ErrorUp, ErrorDown, StartY, EndY, Winding;
            public int XMajor, Start, Len, Index;
        }

        bool _valid, _gamma;
        // +0x780 / +0x790 / +0xa0: the centre's and the two points' colours, premultiplied (a, r, g, b)
        float _a0, _r0, _g0, _b0, _a1, _r1, _g1, _b1, _a2, _r2, _g2, _b2;
        int _xMin, _xMax, _yMin, _yMax;                    // +0x804 / +0x808 / +0x80c / +0x810
        readonly TriEdge _head = new TriEdge { X = int.MinValue };
        readonly TriEdge _tail = new TriEdge { X = int.MaxValue, StartY = int.MaxValue, EndY = int.MinValue };
        TriEdge[] _inactive;
        int _ip, _curY, _nextY;
        int _spanL, _spanR;                                // +0x824 / +0x828
        float _l0, _l1, _r0w, _r1w;                        // +0x814 .. +0x820

        public int BlendCount = 1;
        public float Factor0 = 1f;
        public float[] Factors, Positions;
        public int[] PresetArgb;
        public bool Preset;

        public void SetTriangle (PointF a, PointF b, PointF c, int ca, int cb, int cc, bool gamma)
        {
            _gamma = gamma;
            LineGradientSpan.Premul ((uint) ca, gamma, out _a0, out _r0, out _g0, out _b0);
            LineGradientSpan.Premul ((uint) cb, gamma, out _a1, out _r1, out _g1, out _b1);
            LineGradientSpan.Premul ((uint) cc, gamma, out _a2, out _r2, out _g2, out _b2);
            int[] p = {
                GpMatrix.RasterizerCeiling (a.X * 16f), GpMatrix.RasterizerCeiling (a.Y * 16f),
                GpMatrix.RasterizerCeiling (b.X * 16f), GpMatrix.RasterizerCeiling (b.Y * 16f),
                GpMatrix.RasterizerCeiling (c.X * 16f), GpMatrix.RasterizerCeiling (c.Y * 16f),
                GpMatrix.RasterizerCeiling (a.X * 16f), GpMatrix.RasterizerCeiling (a.Y * 16f),
            };
            int ax = p [0], bx = p [2], cx = p [4];
            int mn = ax < bx ? ax : bx, mx = ax > bx ? ax : bx;
            mn = mn >= cx ? cx : mn;
            mx = mx <= cx ? cx : mx;
            _xMin = mn >> 4; _xMax = mx >> 4;
            _head.Next = _tail;
            var edges = new System.Collections.Generic.List<TriEdge> ();
            int maxY = int.MinValue;
            InitializeEdges (p, 4, edges, ref maxY);
            _yMax = maxY;
            if (edges.Count == 0) return;
            // InitializeInactiveArray: an insertion sort (stable) on (StartY, X).
            var arr = edges.ToArray ();
            for (int i = 1; i < arr.Length; i++) {
                TriEdge e = arr [i];
                long key = Key (e);
                int j = i - 1;
                while (j >= 0 && key < Key (arr [j])) { arr [j + 1] = arr [j]; j--; }
                arr [j + 1] = e;
            }
            _inactive = new TriEdge [arr.Length + 1];
            Array.Copy (arr, _inactive, arr.Length);
            _inactive [arr.Length] = _tail;
            _yMin = _inactive [0].StartY;
            _curY = _nextY = _yMin;
            _ip = 0;
            _valid = true;
        }

        static long Key (TriEdge e) => ((long) e.StartY << 32) | (uint) (e.X + int.MaxValue);

        // InitializeEdges @18002e290, aliased and unclipped, with DpTriangleData's extra fields.
        static void InitializeEdges (int[] p, int vertexCount, System.Collections.Generic.List<TriEdge> edges, ref int maxY)
        {
            for (int e = 0; e < vertexCount - 1; e++) {
                int o = e * 2;
                int px = p [o], py = p [o + 1], qx = p [o + 2], qy = p [o + 3];
                int dN = qy - py, dM = qx - px;
                int ady = dN < 0 ? -dN : dN, adx = dM < 0 ? -dM : dM;
                int len = ady < adx ? adx : ady;
                int start = ady < adx ? px : py;
                int xStart, yStart, yEnd, winding;
                if (dN < 0) {
                    dN = -dN; dM = -dM; winding = -1;
                    xStart = qx; yStart = qy; yEnd = py;
                } else {
                    winding = 1;
                    xStart = px; yStart = py; yEnd = qy;
                }
                int yStartI = (yStart + 15) >> 4, yEndI = (yEnd + 15) >> 4;
                if (yStartI >= yEndI) continue;
                if (maxY < yEndI) maxY = yEndI;
                int dX, errorUp;
                if (dM < 0) {
                    int am = -dM;
                    if (am < dN) { dX = -1; errorUp = dN + dM; }
                    else {
                        int q = (int) ((uint) am / (uint) dN), r = am - q * dN;
                        dX = -q; errorUp = r;
                        if (r > 0) { dX = -q - 1; errorUp = dN - r; }
                    }
                } else if (dM < dN) { dX = 0; errorUp = dM; }
                else { dX = (int) ((uint) dM / (uint) dN); errorUp = dM - dX * dN; }
                int error = -1;
                if ((yStart & 15) != 0) {
                    for (int k = 16 - (yStart & 15); k != 0; k--) {
                        error += errorUp;
                        xStart += dX;
                        if (error >= 0) { error -= dN; xStart++; }
                    }
                }
                if ((xStart & 15) != 0) {
                    error -= (16 - (xStart & 15)) * dN;
                    xStart += 15;
                }
                edges.Add (new TriEdge {
                    X = xStart >> 4, Dx = dX, Error = error >> 4, ErrorUp = errorUp, ErrorDown = dN,
                    Winding = winding, StartY = yStartI, EndY = yEndI,
                    XMajor = ady < adx ? 1 : 0, Start = start >> 4, Len = len >> 4, Index = e,
                });
            }
        }

        /// <summary>SetXSpan: steps the edges to row <paramref name="y"/>; the span between the first
        /// two active edges and the weights at its ends.</summary>
        public bool SetXSpan (int y, out int l, out int r)
        {
            l = r = 0;
            if (!_valid || y < _yMin || _yMax <= y) return false;
            while (_curY <= y) {
                TriEdge prev = _head, e = _head.Next;
                while (true) {
                    while (y < e.EndY) {
                        e.X += e.Dx;
                        e.Error += e.ErrorUp;
                        if (e.Error >= 0) { e.Error -= e.ErrorDown; e.X++; }
                        prev = e;
                        e = e.Next;
                    }
                    if (e.EndY == int.MinValue) break;
                    e = e.Next;
                    prev.Next = e;
                }
                if (_curY == _nextY) Insert (_curY);
                _curY++;
            }
            TriEdge f = _head.Next, s = f.Next;
            int fx = f.X, sx = s.X;
            bool firstLeft = fx <= sx;
            if (firstLeft) { _spanL = fx; _spanR = sx; } else { _spanL = sx; _spanR = fx; }
            Weights (f, y, out float f0, out float f1);
            Weights (s, y, out float s0, out float s1);
            if (firstLeft) { _l0 = f0; _l1 = f1; _r0w = s0; _r1w = s1; }
            else { _r0w = f0; _r1w = f1; _l0 = s0; _l1 = s1; }
            l = _spanL; r = _spanR;
            return true;
        }

        static void Weights (TriEdge e, int y, out float w0, out float w1)
        {
            int c = e.XMajor == 0 ? y : e.X;
            int d = c - e.Start;
            if (d < 0) d = e.Start - c;
            float t = (float) d / (float) e.Len;
            switch (e.Index) {
            case 0: w0 = t; w1 = 0f; break;
            case 1: w1 = t; w0 = 1f - t; break;
            default: w0 = 0f; w1 = 1f - t; break;
            }
        }

        // InsertNewEdges @18015c600.
        void Insert (int y)
        {
            TriEdge prev = _head, a = _head.Next;
            TriEdge n = _inactive [_ip];
            do {
                int ax = a.X;
                while (ax < n.X) { ax = a.Next.X; prev = a; a = a.Next; }
                n.Next = a;
                prev.Next = n;
                a = n;
                _ip++;
                n = _inactive [_ip];
            } while (n.StartY == y);
            _nextY = n.StartY;
        }

        bool GetXSpan (int y, int xl, int xr, out int sl, out int sr)
        {
            sl = _spanL; sr = _spanR;
            return _valid && _yMin <= y && y < _yMax && xl <= _xMax && _xMin <= xr && _spanL != _spanR;
        }

        /// <summary>DpTriangleData::OutputSpan: this triangle's part of [xl, xr) on row y into the
        /// buffer that starts at xl.</summary>
        public void OutputSpan (uint[] buf, int count, bool sourceCopy, int y, int xl, int xr)
        {
            if (!GetXSpan (y, xl, xr, out int sl, out int sr)) return;
            int x0 = sl > xl ? sl : xl;
            int x1 = sr < xr ? sr : xr;
            if (x0 >= x1) return;
            float b0 = _l0, b1 = _l1, e0 = _r0w, e1 = _r1w;
            float l0 = _l0, l1 = _l1;
            float d = (float) x0 - (float) sl;
            if (MathF.Abs (d) > GpPathGradientSpans.Eps) {
                d = d / (float) (sr - sl);
                b0 = (e0 - l0) * d + l0;
                b1 = (e1 - l1) * d + l1;
            }
            d = (float) sr - (float) x1;
            if (MathF.Abs (d) > GpPathGradientSpans.Eps) {
                d = d / (float) (sr - sl);
                e0 = e0 - (e0 - l0) * d;
                e1 = e1 - (e1 - l1) * d;
            }
            int n = x1 - x0;
            float fn = n;
            float st0 = (e0 - b0) / fn, st1 = (e1 - b1) / fn;
            int o = x0 - xl;
            int room = (xl - x0) + count;
            if (room < 0) room = 0;
            bool trivial = BlendCount == 1 && Factor0 == 1f;
            bool preset = Preset && Positions != null && BlendCount > 1;
            for (int i = 0; i < n; i++, o++) {
                float A, R, G, B;
                if (preset)
                    LineGradientSpan.InterpolatePreset ((1f - b0) - b1, BlendCount, PresetArgb, Positions, _gamma, out A, out R, out G, out B);
                else {
                    float w0 = b0, w1 = b1;
                    if (!trivial) {
                        float wc = (1f - b0) - b1;
                        if (BlendCount != 1 || Factor0 != 1f) wc = LineGradientSpan.SlowAdjust (wc, BlendCount, Factor0, Factors, Positions);
                        if (w0 + w1 != 0f) {
                            float k = (1f - wc) / (w1 + w0);
                            w0 = w0 * k;
                            w1 = w1 * k;
                        }
                    }
                    A = ((_a1 - _a0) * w0 + _a0) + (_a2 - _a0) * w1;
                    R = ((_r1 - _r0) * w0 + _r0) + (_r2 - _r0) * w1;
                    G = ((_g1 - _g0) * w0 + _g0) + (_g2 - _g0) * w1;
                    B = ((_b1 - _b0) * w0 + _b0) + (_b2 - _b0) * w1;
                }
                b0 = st0 + b0;
                b1 = st1 + b1;
                uint px;
                if (MathF.Abs (A) < GpPathGradientSpans.Eps || float.IsNaN (A)) {
                    if (!sourceCopy) {
                        if (room != 0) { buf [o] = 0; room--; }
                        continue;
                    }
                }
                px = GpPathGradientSpans.Pack (A, R, G, B, _gamma);
                if (room != 0) { buf [o] = px; room--; }
            }
        }
    }

    // ---- the texture of a wrapped path gradient (GpTexture's bilinear spans, this case's copy) ------

    internal static class PgWrap
    {
        static int Mod (int v, int n) => v < 0 ? n - (~v - ~v / n * n) - 1 : v - v / n * n;

        static int ModFlip (int v, int n)
        {
            int m = Mod (v, n);
            return ((v - m) / n & 1) != 0 ? n - m - 1 : m;
        }

        /// <summary>ApplyWrapMode @180159608.</summary>
        public static void Apply (int mode, ref int x, ref int y, int w, int h)
        {
            switch (mode) {
            case 0: x = Mod (x, w); y = Mod (y, h); break;
            case 1: x = ModFlip (x, w); y = Mod (y, h); break;
            case 2: x = Mod (x, w); y = ModFlip (y, h); break;
            case 3: x = ModFlip (x, w); y = ModFlip (y, h); break;
            }
        }

        public static int ModPublic (int v, int n) => Mod (v, n);
    }

    /// <summary>DpOutputBilinearSpan for a texture (clamp colour 0).</summary>
    internal sealed class PgTextureBilinearSpan : GpSpan
    {
        readonly uint[] _px;
        readonly int _w, _h, _wrap;
        readonly GpMatrix _inv;

        public PgTextureBilinearSpan (GpScan scan, uint[] px, int w, int h, int wrap, GpMatrix srcToDevice, PixelOffsetMode pom) : base (scan)
        {
            _px = px; _w = w; _h = h; _wrap = wrap;
            if (pom == PixelOffsetMode.HighQuality || pom == PixelOffsetMode.Half) srcToDevice.Translate (0.5f, 0.5f, false);
            _inv = GpMatrix.CreateIdentity ();
            if (srcToDevice.IsInvertible) { _inv = srcToDevice; _inv.Invert (); }
        }

        protected override void Fill (uint[] buf, int y, int left, int n)
        {
            float x0 = left, y0 = y, x1 = left + n, y1 = y;
            _inv.Transform (ref x0, ref y0);
            _inv.Transform (ref x1, ref y1);
            float sx = (x1 - x0) / (float) n, sy = (y1 - y0) / (float) n;
            int w = _w, h = _h;
            float u = x0, v = y0;
            for (int i = 0; i < n; i++) {
                int ix = (int) MathF.Floor (u), iy = (int) MathF.Floor (v);
                int fx = (int) MathF.Floor ((u - (float) ix) * 2048f + 0.5f);
                int fyw = (int) MathF.Floor ((v - (float) iy) * 2048f + 0.5f);
                int ix1 = ix + 1, iy1 = iy + 1;
                if ((uint) (w - 1) <= (uint) ix || (uint) (h - 1) <= (uint) iy) {
                    PgWrap.Apply (_wrap, ref ix, ref iy, w, h);
                    PgWrap.Apply (_wrap, ref ix1, ref iy1, w, h);
                }
                int row0 = iy < 0 || iy >= h ? -1 : iy * w;
                int row1 = iy1 < 0 || iy1 >= h ? -1 : iy1 * w;
                uint p00, p01, p10, p11;
                if (ix < 0 || ix >= w) p00 = p01 = 0;
                else {
                    p00 = row0 < 0 ? 0 : _px [row0 + ix];
                    p01 = row1 < 0 ? 0 : _px [row1 + ix];
                }
                if (ix1 < 0 || ix1 >= w) p10 = p11 = 0;
                else {
                    p10 = row0 < 0 ? 0 : _px [row0 + ix1];
                    p11 = row1 < 0 ? 0 : _px [row1 + ix1];
                }
                if (ix1 < 0 || w <= ix || iy1 < 0 || h <= iy) buf [i] = 0;
                else {
                    int gy = 2048 - fyw;
                    uint r = 0;
                    for (int sh = 0; sh < 32; sh += 8) {
                        int a00 = (int) (p00 >> sh & 0xff), a10 = (int) (p10 >> sh & 0xff), a01 = (int) (p01 >> sh & 0xff), a11 = (int) (p11 >> sh & 0xff);
                        int t = ((a10 - a00) * fx + a00 * 0x800) * gy + ((a11 - a01) * fx + a01 * 0x800) * fyw + 0x200000;
                        r |= ((uint) t >> 22 & 0xff) << sh;
                    }
                    buf [i] = r;
                }
                u += sx; v += sy;
            }
        }
    }

    /// <summary>DpOutputBilinearSpan_Identity for a texture: an integer translation, copied.</summary>
    internal sealed class PgTextureIdentitySpan : GpSpan
    {
        readonly uint[] _px;
        readonly int _w, _h, _wrap, _offX, _offY;
        readonly bool _pow2;

        public PgTextureIdentitySpan (GpScan scan, uint[] px, int w, int h, int wrap, in GpMatrix srcToDevice) : base (scan)
        {
            _px = px; _w = w; _h = h; _wrap = wrap;
            _pow2 = ((w - 1) & w) == 0 && ((h - 1) & h) == 0;
            _offX = -GpPathGradientSpans.Round (srcToDevice.Dx);
            _offY = -GpPathGradientSpans.Round (srcToDevice.Dy);
        }

        protected override void Fill (uint[] buf, int y, int left, int n)
        {
            int sx = _offX + left, sy = _offY + y;
            int w = _w, h = _h;
            int o = 0;
            if (_wrap == 0) {
                if (_pow2) { sx &= w - 1; sy &= h - 1; }
                else {
                    if ((uint) w <= (uint) sx) sx = PgWrap.ModPublic (sx, w);
                    if ((uint) h <= (uint) sy) sy = PgWrap.ModPublic (sy, h);
                }
                int row = sy * w;
                int k = Math.Min (w - sx, n);
                int rest = n - k;
                for (int i = 0; i < k; i++) buf [o++] = _px [row + sx + i];
                while (rest > 0) {
                    int c = Math.Min (w, rest);
                    rest -= c;
                    for (int i = 0; i < c; i++) buf [o++] = _px [row + i];
                }
                return;
            }
            if ((uint) sy < (uint) h && sx < w && sx + n > 0) {
                int row = sy * w, from = sx, cnt = n;
                if (sx < 0) {
                    for (int i = 0; i < -sx; i++) buf [o++] = 0;
                    cnt += sx; from = 0;
                }
                int c = Math.Min (cnt, w - from);
                for (int i = 0; i < c; i++) buf [o++] = _px [row + from + i];
                for (int i = c; i < cnt; i++) buf [o++] = 0;
            } else {
                for (int i = 0; i < n; i++) buf [i] = 0;
            }
        }
    }
}
