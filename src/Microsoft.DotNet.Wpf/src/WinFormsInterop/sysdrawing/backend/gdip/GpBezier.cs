// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s Bezier flattener (gdiplus.dll Bezier32::bInit @18002fcc0, cFlatten @18015d6c0,
// Bezier64::vInit @180209ce8, cFlatten @1800c27f0): hybrid forward differencing in 28.4, the code
// wpfgfx's bezier.cpp carries. Bezier32 works while the control hull, 16 out on every side, fits in
// 14 bits; past that Bezier64 takes over with GDI's 2/3-pixel low error (4 << 32).
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class BezierFlattener
    {
        readonly bool _b32;
        // Bezier32
        int _steps32;
        Hfd32 _x, _y;
        int _left, _top;
        // Bezier64
        Hfd64 _xh, _yh, _xl, _yl;
        int _stepsHigh, _stepsLow;
        int[] _clip64;

        const long ErrorHigh = (long) (6 * (1 << 15) >> (32 - 28)) << 32;
        const long ErrorLow = 4L << 32;

        /// <summary><paramref name="p"/>: four 28.4 control points as x0,y0..x3,y3; <paramref name="clip"/>
        /// left, top, right, bottom in 28.4 (or null).</summary>
        public BezierFlattener (int[] p, int[] clip)
        {
            int minX = p [0], minY = p [1], maxX = p [0], maxY = p [1];
            for (int i = 1; i < 4; i++) {
                minX = Math.Min (minX, p [i * 2]); minY = Math.Min (minY, p [i * 2 + 1]);
                maxX = Math.Max (maxX, p [i * 2]); maxY = Math.Max (maxY, p [i * 2 + 1]);
            }
            _left = minX - 16; _top = minY - 16;
            int right = maxX + 16, bottom = maxY + 16;
            uint or = 0;
            for (int i = 0; i < 4; i++) or |= (uint) (p [i * 2] - _left) | (uint) (p [i * 2 + 1] - _top);
            if ((or & 0xffffc000u) == 0) {
                _b32 = true;
                _steps32 = 1;
                _x.Init (p [0] - _left, p [2] - _left, p [4] - _left, p [6] - _left);
                _y.Init (p [1] - _top, p [3] - _top, p [5] - _top, p [7] - _top);
                int shift = 0;
                if (clip == null || (_left < clip [2] && _top < clip [3] && clip [0] < right && clip [1] < bottom)) {
                    while (true) {
                        int t = 0x6000 << shift;
                        if (_x.Error <= t && _y.Error <= t) break;
                        shift += 2;
                        _x.LazyHalve (shift); _y.LazyHalve (shift);
                        _steps32 <<= 1;
                    }
                }
                _x.Steady (shift); _y.Steady (shift);
                _x.Step (); _y.Step ();
                _steps32--;
                return;
            }
            _stepsHigh = 1; _stepsLow = 0;
            _xh.Init (p [0], p [2], p [4], p [6]);
            _yh.Init (p [1], p [3], p [5], p [7]);
            _clip64 = clip;
            while (_xh.Error > ErrorHigh || _yh.Error > ErrorHigh) {
                _stepsHigh <<= 1;
                _xh.Halve (); _yh.Halve ();
            }
        }

        /// <summary>Writes up to <paramref name="room"/> points (x,y pairs) at point index
        /// <paramref name="at"/>; returns how many.</summary>
        public int Flatten (int[] buf, int at, int room, out bool more)
        {
            return _b32 ? Flatten32 (buf, at, room, out more) : Flatten64 (buf, at, room, out more);
        }

        int Flatten32 (int[] buf, int at, int room, out bool more)
        {
            int original = room;
            do {
                buf [at * 2] = _x.Value + _left;
                buf [at * 2 + 1] = _y.Value + _top;
                at++;
                if (_steps32 == 0) { more = false; return original - room + 1; }
                if (Math.Max (_x.Error, _y.Error) > 0x30000) {
                    _x.Halve (); _y.Halve ();
                    _steps32 <<= 1;
                }
                while ((_steps32 & 1) == 0 && _x.ParentErrorBy4 <= 0xc000 && _y.ParentErrorBy4 <= 0xc000) {
                    _x.Double (); _y.Double ();
                    _steps32 >>= 1;
                }
                _steps32--;
                _x.Step (); _y.Step ();
            } while (--room != 0);
            more = true;
            return original;
        }

        int Flatten64 (int[] buf, int at, int room, out bool more)
        {
            int original = room;
            var a = new int [8];
            do {
                if (_stepsLow == 0) {
                    _xh.Untransform (a, 0);
                    _yh.Untransform (a, 1);
                    _xl.Init (a [0], a [2], a [4], a [6]);
                    _yl.Init (a [1], a [3], a [5], a [7]);
                    _stepsLow = 1;
                    bool visible = true;
                    if (_clip64 != null) {
                        int minX = a [0], minY = a [1], maxX = a [0], maxY = a [1];
                        for (int i = 1; i < 4; i++) {
                            minX = Math.Min (minX, a [i * 2]); minY = Math.Min (minY, a [i * 2 + 1]);
                            maxX = Math.Max (maxX, a [i * 2]); maxY = Math.Max (maxY, a [i * 2 + 1]);
                        }
                        visible = minX - 16 < _clip64 [2] && minY - 16 < _clip64 [3] && _clip64 [0] < maxX + 16 && _clip64 [1] < maxY + 16;
                    }
                    if (visible)
                        while (_xl.Error > ErrorLow || _yl.Error > ErrorLow) {
                            _stepsLow <<= 1;
                            _xl.Halve (); _yl.Halve ();
                        }
                    if (--_stepsHigh != 0) {
                        _xh.Step (); _yh.Step ();
                        if (_xh.Error > ErrorHigh || _yh.Error > ErrorHigh) {
                            _stepsHigh <<= 1;
                            _xh.Halve (); _yh.Halve ();
                        }
                        while ((_stepsHigh & 1) == 0 && _xh.ParentError <= ErrorHigh && _yh.ParentError <= ErrorHigh) {
                            _xh.Double (); _yh.Double ();
                            _stepsHigh >>= 1;
                        }
                    }
                }
                _xl.Step (); _yl.Step ();
                buf [at * 2] = _xl.Value;
                buf [at * 2 + 1] = _yl.Value;
                at++;
                _stepsLow--;
                if (_stepsLow == 0 && _stepsHigh == 0) { more = false; return original - room + 1; }
                if (_xl.Error > ErrorLow || _yl.Error > ErrorLow) {
                    _stepsLow <<= 1;
                    _xl.Halve (); _yl.Halve ();
                }
                while ((_stepsLow & 1) == 0 && _xl.ParentError <= ErrorLow && _yl.ParentError <= ErrorLow) {
                    _xl.Double (); _yl.Double ();
                    _stepsLow >>= 1;
                }
            } while (--room != 0);
            more = true;
            return original;
        }

        struct Hfd32
        {
            public int E0, E1, E2, E3;
            public void Init (int p1, int p2, int p3, int p4)
            {
                E0 = p1 << 10; E1 = (p4 - p1) << 10;
                E2 = (p2 - p3 - p3 + p4) * 0x1800; E3 = (p1 - p2 - p2 + p3) * 0x1800;
            }
            public int Error => Math.Max (Math.Abs (E2), Math.Abs (E3));
            public int ParentErrorBy4 => Math.Max (Math.Abs (E3), Math.Abs (E2 + E2 - E3));
            public void LazyHalve (int shift) { E2 = (E2 + E3) >> 1; E1 = (E1 - (E2 >> shift)) >> 1; }
            public void Steady (int shift)
            {
                E0 <<= 3; E1 <<= 3;
                int l = shift - 3;
                if (l < 0) { E2 <<= -l; E3 <<= -l; } else { E2 >>= l; E3 >>= l; }
            }
            public void Halve () { E2 = (E2 + E3) >> 3; E1 = (E1 - E2) >> 1; E3 >>= 2; }
            public void Double () { E1 += E1 + E2; E3 <<= 2; E2 = (E2 << 3) - E3; }
            public void Step () { E0 += E1; int t = E2; E1 += t; E2 += t - E3; E3 = t; }
            public int Value => (E0 + 0x1000) >> 13;
        }

        struct Hfd64
        {
            public long E0, E1, E2, E3;
            public void Init (int p1, int p2, int p3, int p4)
            {
                E0 = (long) p1 << 28;
                E1 = ((long) p4 - p1) * 0x10000000L;
                E2 = ((long) p4 + (long) p3 * -2 + p2) * 0x60000000L;
                E3 = ((long) p1 + (long) p2 * -2 + p3) * 0x60000000L;
            }
            public long Error => Math.Max (Math.Abs (E2), Math.Abs (E3));
            public long ParentError => Math.Max (Math.Abs (E3 << 2), Math.Abs ((E2 << 3) - (E3 << 2)));
            public void Halve () { E2 += E3; E2 >>= 3; E1 -= E2; E1 >>= 1; E3 >>= 2; }
            public void Double () { E1 <<= 1; E1 += E2; E3 <<= 2; E2 <<= 3; E2 -= E3; }
            public void Step () { E0 += E1; long t = E2; E1 += E2; E2 += t; E2 -= E3; E3 = t; }
            public int Value => (int) ((E0 + (1L << 27)) >> 28);
            public void Untransform (int[] a, int o)
            {
                long p0 = E0, p2 = E1 + E1 + E1;
                long p1 = p2 + p2 - E2;
                p2 = p1 + p1 - E3;
                p1 -= E3; p1 -= E3;
                p1 /= 18; p2 /= 18;
                p1 += E0; p2 += E0;
                long p3 = E0 + E1;
                a [o] = (int) ((p0 + (1L << 27)) >> 28);
                a [o + 2] = (int) ((p1 + (1L << 27)) >> 28);
                a [o + 4] = (int) ((p2 + (1L << 27)) >> 28);
                a [o + 6] = (int) ((p3 + (1L << 27)) >> 28);
            }
        }
    }
}
