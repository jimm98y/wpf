// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DpOutputSpanStretch<HighQualityBilinear = 0 | HighQualityBicubic = 1> (gdiplus.dll arm64 10.0.26100):
// the separable filter GDI+ draws axis-aligned HighQuality images with. Every source row the kernel
// reaches is filtered horizontally into a circular buffer of lines, and each device row is the
// vertical filter over those lines. Weights are differences of a cumulative kernel table, so a source
// pixel weighs the kernel's area between its neighbours' sample positions.
//
//   InitializeClass @1800c2f50 / @180030cf0   scales and kernel widths in 16.16 (the kernel at least
//        one source pixel each side, two for bicubic, wider when shrinking), the device extent in
//        28.4; the x origin is srcX + (dstX - floor(left)) * scale (y: (floor(top) - dstY) * scale)
//   OutputSpan_Original @1800c39b0 / @1800c35d0   the vertical pass and the line cache (0xa4 the
//        last source row filtered, 0xb8 the circular index)
//   StretchScanline @1800c3d80 / @1800313b0   the horizontal pass; outside the source the wrap mode,
//        or for Clamp the clamp colour
//   tables: tent @1802b06f0 (513 entries, index t >> 9 about 256), cubic @1802af6e0 (1025, t >> 8 about 512)
//
// The feature flag that selects the optimised OutputSpan is off: OutputSpan is OutputSpan_Original.
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpStretchSpan : GpSpan
    {
        readonly DpBitmapSrc _src;
        readonly int _wrap;
        readonly uint _clamp;
        readonly bool _transparentClamp;
        readonly int[] _table;
        readonly int _shift, _center;
        int _x0, _xs, _xk, _xki, _col0, _ncol;
        int _y0, _ys, _yk, _yki, _row0, _nlines;
        uint[] _lines;
        int[] _ycoef;
        int _last = int.MaxValue, _idx;
        readonly bool _valid;

        GpStretchSpan (bool bicubic, GpScan scan, DpBitmapSrc src, DpImageAttr ia, RectangleF srcRect, RectangleF dstRect) : base (scan)
        {
            _src = src; _wrap = ia.Wrap; _clamp = ia.Clamp;
            _table = bicubic ? Cubic : Tent;
            _shift = bicubic ? 8 : 9; _center = bicubic ? 512 : 256;
            if ((uint) _wrap >= 5) return;
            _transparentClamp = _wrap == 4 && _clamp == 0;
            _valid = Init (bicubic, srcRect, dstRect);
        }

        public static GpSpan Create (bool bicubic, GpScan scan, DpBitmapSrc src, DpImageAttr ia, RectangleF srcRect, RectangleF dstRect)
        {
            var s = new GpStretchSpan (bicubic, scan, src, ia, srcRect, dstRect);
            return s._valid ? s : null;
        }

        static bool ValidFixed16 (float f) => -32768f <= f && f <= 32767f;
        static int Round (float f) => (int) MathF.Floor (f);

        bool Init (bool bicubic, RectangleF s, RectangleF d)
        {
            float sx = s.X, sy = s.Y, sw = s.Width, sh = s.Height, dx = d.X, dy = d.Y, dw = d.Width, dh = d.Height;
            if (!(ValidFixed16 (sx) && ValidFixed16 (sy) && ValidFixed16 (sw) && ValidFixed16 (sh) && ValidFixed16 (dx) && ValidFixed16 (dy) && ValidFixed16 (dw) && ValidFixed16 (dh)))
                return false;
            const float F = 65536f, InvF = 1.52587890625e-05f;
            _xs = Round ((sw / dw) * F + 0.5f);
            float dws = dw / sw;
            _ys = Round ((sh / dh) * F + 0.5f);
            float dhs = dh / sh;
            if (_xs == 0 || _ys == 0) return false;
            int ax = Round (dx * 16f + 0.5f), bx = Round ((sw * dws + dx) * 16f + 0.5f);
            int ay = Round (dy * 16f + 0.5f), by = Round ((sh * dhs + dy) * 16f + 0.5f);
            int minX, maxX, minY, maxY;
            if (ax > bx) { minX = bx; maxX = ax; } else { minX = ax; maxX = bx; }
            if (ay > by) { minY = by; maxY = ay; } else { minY = ay; maxY = by; }
            _col0 = (minX + 15) >> 4;
            int f70 = maxX << 12, f68 = minX << 12, f6c = minY << 12, f74 = maxY << 12;
            float ex = dx;
            int px = f68 >> 16;
            if (_xs < 0) { px = (f70 + 0xffff) >> 16; ex = dw + dx; }
            _x0 = Round ((((ex - (float) px) * (float) _xs) * InvF + sx) * F + 0.5f);
            float kmul = bicubic ? 2f : 1f;
            int kmin = bicubic ? 0x20000 : 0x10000;
            _xk = Round (((kmul * sw) / dw) * F + 0.5f);
            if (_xk < 0) _xk = -_xk;
            if (_xk < kmin) _xk = kmin;
            _xki = Round ((F / (float) _xk) * F + 0.5f);
            float fy = _ys;
            float oy;
            short top = (short) (f6c >> 16);
            if (_ys < 0) oy = sy - (((float) ((f74 + 0xffff) >> 16) - (dh + dy)) * fy) * InvF;
            else oy = (((float) top - dy) * fy) * InvF + sy;
            _y0 = Round (oy * F + 0.5f);
            _yk = Round (((kmul * sh) / dh) * F + 0.5f);
            if (_yk < 0) _yk = -_yk;
            if (_yk < kmin) _yk = kmin;
            _yki = Round ((F / (float) _yk) * F + 0.5f);
            _row0 = top;
            _ncol = ((f70 + 0xffff) >> 16) - (short) (f68 >> 16) + 1;
            _nlines = ((_yk + 0xffff) >> 16) * 2 + 1;
            if (_ncol <= 0 || _nlines <= 0 || (long) _ncol * _nlines > 1 << 26) return false;
            _lines = new uint [_nlines * _ncol];
            _ycoef = new int [_nlines + 2];
            return true;
        }

        uint SrcPixel (int row, int p)
        {
            int w = _src.Width;
            if ((uint) p < (uint) w) return _src.Px [row + p];
            if (_wrap == 4) return _clamp;
            GpWrap.Apply1D (_wrap, ref p, w, false);
            return _src.Px [row + p];
        }

        /// <summary>StretchScanline: the line's horizontally filtered pixels of one source row.</summary>
        void StretchScanline (int lineOff, int srcRow)
        {
            int c = _x0 - 0x8000;
            for (int i = 0; i < _ncol; i++) {
                int first = (c - _xk + 0xffff) >> 16, last = (c + _xk + 0xffff) >> 16;
                int prev = 0, a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                for (int p = first; p <= last; p++) {
                    int t = (int) ((long) (p * 0x10000 - c) * _xki >> 16);
                    int T = _table [_center + (t >> _shift)];
                    int wgt = T - prev; prev = T;
                    uint v = SrcPixel (srcRow, p);
                    a0 += (int) (v & 0xff) * wgt; a1 += (int) (v >> 8 & 0xff) * wgt;
                    a2 += (int) (v >> 16 & 0xff) * wgt; a3 += (int) (v >> 24) * wgt;
                }
                _lines [lineOff + i] = Pack (a0, a1, a2, a3);
                c += _xs;
            }
        }

        uint Pack (int a0, int a1, int a2, int a3)
        {
            if (_shift == 8) {
                // <1>: alpha to 255, each colour to alpha, then everything to 0.
                int ab = (a0 + 0x8000) >> 16, ag = (a1 + 0x8000) >> 16, ar = (a2 + 0x8000) >> 16, aa = (a3 + 0x8000) >> 16;
                int A = aa < 0x100 ? aa : 0xff;
                int R = ar <= A ? ar : A, G = ag <= A ? ag : A, B = ab <= A ? ab : A;
                B &= ~(B >> 31); G &= ~(G >> 31); R &= ~(R >> 31); A &= ~(A >> 31);
                return (uint) B | (uint) G << 8 | (uint) R << 16 | (uint) A << 24;
            }
            int b = (a0 + 0x8000) >> 16, g = (a1 + 0x8000) >> 16, r = (a2 + 0x8000) >> 16, a = (a3 + 0x8000) >> 16;
            if (!(b < 0x100)) b = 0xff;
            if (!(g < 0x100)) g = 0xff;
            if (!(r < 0x100)) r = 0xff;
            if (!(a < 0x100)) a = 0xff;
            return (uint) b | (uint) g << 8 | (uint) r << 16 | (uint) a << 24;
        }

        protected override void Fill (uint[] buf, int y, int x, int n) { }

        /// <summary>OutputSpan_Original.</summary>
        public override void OutputSpan (int y, int left, int right)
        {
            int start = Math.Max (_col0, left);
            int end = Math.Min (_ncol + _col0, right);
            if (end < start) return;
            int count = end - start;
            if (_ncol < count) return;
            int row = Math.Max (_row0, y);
            int cy = _y0 + (row - _row0) * _ys - 0x8000;
            int first = ((cy - _yk) + 0xffff) >> 16;
            int lastRow = (_yk + cy + 0xffff) >> 16;
            if (lastRow < first) return;
            if (_last != int.MaxValue) {
                bool reuse;
                int ni;
                if (_ys < 0) { reuse = lastRow - _last >= 0; ni = (_idx - _last) + first; }
                else { reuse = _last - first >= 0; ni = (_idx - _last) + first - 1; }
                if (reuse) {
                    _idx = ni;
                    if (_idx < 0) { _idx = _nlines + ni; if (_idx < 0) _idx = 0; }
                } else _idx = 0;
            }
            int H = _src.Height, W = _src.Width;
            if (_idx >= 0 && _idx < _nlines) {
                uint t = (uint) (int) ((long) (first * 0x10000 - cy) * _yki >> 16);
                int prev = 0;
                for (int k = 0; k < _nlines; k++) {
                    int li = _idx + k;
                    if (_nlines <= li) li -= li / _nlines * _nlines;
                    int srow = k + first;
                    if (lastRow < srow) _ycoef [li] = 0;
                    else {
                        int T = _table [_center + ((int) t >> _shift)];
                        _ycoef [li] = T - prev;
                        t = (uint) ((int) t + _yki);
                        prev = T;
                    }
                    int lineOff = _ncol * li;
                    int sr = srow;
                    if ((uint) H <= (uint) sr) {
                        if (_transparentClamp) { _ycoef [li] = 0; continue; }
                        if (_wrap == 4) {
                            for (int i = 0; i < count; i++) _lines [lineOff + i] = _clamp;
                            continue;
                        }
                        GpWrap.Apply1D (_wrap, ref sr, H, true);
                    }
                    bool doIt = _last == int.MaxValue;
                    if (!doIt) {
                        int dd = (_last - k) - first;
                        doIt = _ys < 0 ? 0 < dd : dd < 0;
                    }
                    if (doIt) StretchScanline (lineOff, sr * W);
                }
                _last = _ys >= 0 ? _nlines + first - 1 : first;
            }
            uint[] buf = Scan.Next (start, row, count);
            SetBuffer (buf);
            int col = start - _col0;
            for (int j = 0; j < count; j++) {
                int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                for (int k = 0; k < _nlines; k++) {
                    int wgt = _ycoef [k];
                    uint v = _lines [_ncol * k + col + j];
                    a0 += (int) (v & 0xff) * wgt; a1 += (int) (v >> 8 & 0xff) * wgt;
                    a2 += (int) (v >> 16 & 0xff) * wgt; a3 += (int) (v >> 24) * wgt;
                }
                buf [j] = Pack (a0, a1, a2, a3);
            }
        }
    }
}
