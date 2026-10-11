// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ImageAttributes' colour adjustments as gdiplus.dll (arm64, 10.0.26100) keeps and applies them:
// GpRecolor holds a GpRecolorObject per ColorAdjustType (Default, Bitmap, Brush, Pen, Text).
//
//   GpRecolor::UseRecolorObject @1801aef70   Bitmap..Text use their own object, else Default's
//                                         unless SetToIdentity marked the type (then none)
//   GpRecolor::HasRecoloring @1801c9d28    the object in use has any flag set
//   GpRecolorObject flags: 1 NoOp, 2 matrix, 4 threshold, 8 gamma, 0x10 colour key, 0x20 remap
//        table, 0x40 output channel, 0x80 grey matrix (AltGrays)
//   GpRecolorObject::Flush @1800fcaf0      the matrix classified: 5 translate only, 3/4 diagonal
//        (alpha scale 1 or not), 1 alpha row/column untouched, 2 general; NoOp: none of it
//   GpRecolorObject::ComputeLuts @18003f048   diagonal LUTs floor(i * m + 0.5); the grey LUT
//        (grey matrix over (i, i, i, 255)); gamma/threshold LUT on R, G, B (pow in double)
//   GpRecolorObject::ColorAdjust @1800fc788   remap (exact ARGB), colour key (alpha to 0, RGB
//        kept), the matrix (TransformColor5x5 @1800fd188 and its AltGrays/Scale/Translate kin),
//        the gamma/threshold LUT, then the CMYK channel (DoCmykSeparationByMapping @18024da38)
//
// Pixels are straight (not premultiplied) ARGB.
//

using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpRecolorObject
    {
        public uint Flags;
        public int MatrixFlags;                      // ColorMatrixFlag
        public float[] M = new float [25];
        public float[] Gray = new float [25];
        public uint KeyLow = 0xff000000, KeyHigh = 0xff000000;
        public uint[] Remap;                         // old, new pairs
        public float Threshold, Gamma;
        public int Channel;
        /// <summary>The GpICMHolder (+0x608): the separation transform, shared by clones.</summary>
        public GpIcm Icm;
        int _type;
        bool _lutOn;
        byte[] _lut = new byte [0x600];
        uint[] _grayLut;

        public GpRecolorObject Clone ()
        {
            var c = (GpRecolorObject) MemberwiseClone ();
            c.M = (float[]) M.Clone (); c.Gray = (float[]) Gray.Clone ();
            c._lut = (byte[]) _lut.Clone ();
            c.Remap = (uint[]) Remap?.Clone ();
            return c;
        }

        static float Mx (float[] m, int r, int c) => m [r * 5 + c];

        public void Flush ()
        {
            _type = 0; _lutOn = false;
            if ((Flags & 1) != 0) return;
            if ((Flags & 2) != 0) {
                bool diag = true, translate = true;
                for (int r = 0; r < 5 && diag; r++)
                    for (int c = 0; c < 5 && diag; c++)
                        if (r != c && M [r * 5 + c] != 0f) diag = false;
                for (int r = 0; r < 4 && translate; r++)
                    for (int c = 0; c < 5 && translate; c++) {
                        float v = M [r * 5 + c];
                        if (r == c) v -= 1f;
                        if (1.1920929e-07f <= MathF.Abs (v)) translate = false;
                    }
                if (translate) _type = 5;
                else if (diag) _type = (1.1920929e-07f <= MathF.Abs (Mx (M, 3, 3) - 1f)) ? 4 : 3;
                else {
                    _type = 1;
                    for (int u = 0; u < 5; u++) {
                        float v;
                        if (u != 3) {
                            if (!(MathF.Abs (Mx (M, u, 3)) < 1.1920929e-07f)) { _type = 2; break; }
                            v = Mx (M, 3, u);
                        } else v = Mx (M, 3, 3) - 1f;
                        if (1.1920929e-07f <= MathF.Abs (v)) { _type = 2; break; }
                    }
                }
            }
            ComputeLuts ();
            // An output channel with no profile of its own separates through rswop.icm.
            if ((Flags & 0x40) != 0 && (Flags & 0x100) == 0) SetupCmykSeparation ("rswop.icm");
        }

        /// <summary>SetupCmykSeparation @1800fd028: the holder freed, then made for sRGB ->
        /// <paramref name="profile"/>; on success the 0x100 flag. GDI+'s status.</summary>
        public int SetupCmykSeparation (string profile)
        {
            Icm = null;
            GpIcm t = GpIcm.Setup (profile, out int status);
            if (status == 0) { Icm = t; Flags |= 0x100; }
            return status;
        }

        static byte Clamp255 (int v) => (byte) (v < 0x100 ? (v < 0 ? 0 : v) : 0xff);
        static int Fl (float f) => (int) MathF.Floor (f);

        void ComputeLuts ()
        {
            if (_type == 3 || _type == 4) {
                float r = M [0], g = M [6], b = M [12], a = _type == 4 ? M [18] : 1f;
                for (int i = 0; i < 256; i++) {
                    float fi = i;
                    _lut [0x100 + i] = Clamp255 (Fl (fi * r + 0.5f));
                    _lut [0x200 + i] = Clamp255 (Fl (fi * g + 0.5f));
                    _lut [0x300 + i] = Clamp255 (Fl (fi * b + 0.5f));
                    _lut [0x400 + i] = Clamp255 (Fl (fi * a + 0.5f));
                }
            }
            if ((Flags & 0x80) != 0) {
                _grayLut = new uint [256];
                for (uint i = 0; i < 256; i++) _grayLut [i] = 0xff000000u | i << 16 | i << 8 | i;
                Transform5x5 (_grayLut, 0, 256, Gray);
            }
            uint gt = Flags & 0xc;
            _lutOn = gt != 0;
            if (gt == 8) {
                _lut [0x5ff] = 0xff; _lut [0x500] = 0;
                for (int i = 1; i < 0xff; i++) {
                    double p = Math.Pow ((double) i / 255.0, (double) Gamma);
                    _lut [0x500 + i] = (byte) Fl ((float) (p * 255.0) + 0.5f);
                }
            } else if (gt == 4) {
                uint t = (uint) (int) MathF.Ceiling (Threshold * 255f);
                for (int i = 0; i < 256; i++) _lut [0x500 + i] = (byte) ((uint) i < (t & 0xff) ? 0 : 0xff);
            } else if (gt == 0xc) {
                for (int i = 0; i < 256; i++) {
                    double p = Math.Pow ((double) i / 255.0, (double) Gamma);
                    _lut [0x500 + i] = (byte) (p < (double) Threshold ? 0 : 0xff);
                }
            }
        }

        /// <summary>TransformColor5x5 @1800fd188 (B saturated by ByteSaturate, the rest floor(v + 0.5)).</summary>
        static void Transform5x5 (uint[] px, int o, int n, float[] m)
        {
            float t0 = m [20] * 255f, t1 = m [21] * 255f, t2 = m [22] * 255f, t3 = m [23] * 255f;
            for (int i = o; i < o + n; i++) {
                uint p = px [i];
                float b = p & 0xff, g = p >> 8 & 0xff, r = p >> 16 & 0xff, a = p >> 24;
                float vb = m [7] * g + m [2] * r + m [12] * b + m [17] * a + t2;
                int ob = vb < 255f ? (0f < vb ? Fl (vb + 0.5f) : 0) : 255;
                int og = Clamp255 (Fl (m [6] * g + m [1] * r + m [11] * b + a * m [16] + t1 + 0.5f));
                int or = Clamp255 (Fl (m [5] * g + m [0] * r + m [10] * b + m [15] * a + t0 + 0.5f));
                int oa = Clamp255 (Fl (m [8] * g + m [3] * r + m [13] * b + a * m [18] + t3 + 0.5f));
                px [i] = (uint) (oa << 24 | or << 16 | og << 8 | (byte) ob);
            }
        }

        static bool IsGray (uint p) => (byte) p == (byte) (p >> 8) && (byte) p == (byte) (p >> 16);

        void Transform5x5AltGrays (uint[] px, int o, int n, bool skip)
        {
            float[] m = M;
            float t0 = m [20] * 255f, t1 = m [21] * 255f, t2 = m [22] * 255f, t3 = m [23] * 255f;
            for (int i = o; i < o + n; i++) {
                uint p = px [i];
                if (IsGray (p)) {
                    if (!skip) px [i] = _grayLut [p & 0xff];
                    continue;
                }
                float b = p & 0xff, g = p >> 8 & 0xff, r = p >> 16 & 0xff, a = p >> 24;
                int ob = Clamp255 (Fl (m [7] * g + m [2] * r + m [12] * b + m [17] * a + t2 + 0.5f));
                int og = Clamp255 (Fl (m [6] * g + m [1] * r + m [11] * b + m [16] * a + t1 + 0.5f));
                int or = Clamp255 (Fl (m [5] * g + m [0] * r + m [10] * b + m [15] * a + t0 + 0.5f));
                int oa = Clamp255 (Fl (m [8] * g + m [3] * r + m [13] * b + m [18] * a + t3 + 0.5f));
                px [i] = (uint) (oa << 24 | or << 16 | og << 8 | ob);
            }
        }

        uint Scale (uint p) => (uint) (_lut [0x300 + (p & 0xff)] | _lut [0x200 + (p >> 8 & 0xff)] << 8 | _lut [0x100 + (p >> 16 & 0xff)] << 16 | _lut [0x400 + (p >> 24)] << 24);

        void Translate (uint[] px, int o, int n, bool alt, bool skip)
        {
            int ob = Fl (M [22] * 255f + 0.5f), og = Fl (M [21] * 255f + 0.5f), or = Fl (M [20] * 255f + 0.5f), oa = Fl (M [23] * 255f + 0.5f);
            for (int i = o; i < o + n; i++) {
                uint p = px [i];
                if (alt && IsGray (p)) {
                    if (!skip) px [i] = _grayLut [p & 0xff];
                    continue;
                }
                px [i] = (uint) (Clamp255 ((int) (p & 0xff) + ob) | Clamp255 ((int) (p >> 8 & 0xff) + og) << 8
                    | Clamp255 ((int) (p >> 16 & 0xff) + or) << 16 | Clamp255 ((int) (p >> 24) + oa) << 24);
            }
        }

        void ColorTwist (uint[] px, int o, int n)
        {
            switch (_type) {
            case 1: case 2:
                if (MatrixFlags == 0) Transform5x5 (px, o, n, M);
                else Transform5x5AltGrays (px, o, n, MatrixFlags == 1);
                break;
            case 3: case 4:
                for (int i = o; i < o + n; i++) {
                    uint p = px [i];
                    if (MatrixFlags != 0 && IsGray (p)) {
                        if (MatrixFlags != 1) px [i] = _grayLut [p & 0xff];
                        continue;
                    }
                    px [i] = Scale (p);
                }
                break;
            case 5:
                Translate (px, o, n, MatrixFlags != 0, MatrixFlags == 1);
                break;
            }
        }

        /// <summary>GpRecolorObject::ColorAdjust: straight ARGB pixels in place.</summary>
        public void ColorAdjust (uint[] px, int o, int n)
        {
            if ((Flags & 0x20) != 0 && Remap != null)
                for (int i = o; i < o + n; i++)
                    for (int k = 0; k + 1 < Remap.Length; k += 2)
                        if (px [i] == Remap [k]) { px [i] = Remap [k + 1]; break; }
            if ((Flags & 0x10) != 0)
                for (int i = o; i < o + n; i++) {
                    uint p = px [i];
                    if ((KeyLow & 0xff) <= (p & 0xff) && (p & 0xff) <= (KeyHigh & 0xff)
                        && (KeyLow >> 8 & 0xff) <= (p >> 8 & 0xff) && (p >> 8 & 0xff) <= (KeyHigh >> 8 & 0xff)
                        && (KeyLow >> 16 & 0xff) <= (p >> 16 & 0xff) && (p >> 16 & 0xff) <= (KeyHigh >> 16 & 0xff))
                        px [i] = p & 0xffffff;
                }
            ColorTwist (px, o, n);
            if (_lutOn)
                for (int i = o; i < o + n; i++) {
                    uint p = px [i];
                    px [i] = (p & 0xff000000u) | (uint) _lut [0x500 + (p >> 16 & 0xff)] << 16 | (uint) _lut [0x500 + (p >> 8 & 0xff)] << 8 | _lut [0x500 + (p & 0xff)];
                }
            if ((Flags & 0x40) != 0) {
                if (Icm == null) CmykByMapping (px, o, n);
                else CmykByIcm (px, o, n);
            }
        }

        /// <summary>DoCmykSeparationByICM @1800fc9f0: each pixel through the transform (xRGB in,
        /// KYMC out), the channel's byte inverted into R, G and B, the alpha kept.</summary>
        void CmykByIcm (uint[] px, int o, int n)
        {
            int sh = 8 * Channel;
            for (int i = o; i < o + n; i++) {
                uint p = px [i];
                uint v = ~(Icm.Translate (p) >> sh) & 0xff;
                px [i] = (p & 0xff000000u) | v << 16 | v << 8 | v;
            }
        }

        void CmykByMapping (uint[] px, int o, int n)
        {
            int ch = Channel;
            if ((uint) ch > 3) return;
            for (int i = o; i < o + n; i++) {
                uint p = px [i];
                int c = (int) (0xff - (p >> 16)) & 0xff, mm = (int) (0xff - (p >> 8)) & 0xff, y = (int) (0xff - p) & 0xff;
                int k = Math.Min (Math.Min (c, mm), y);
                int v = ch == 0 ? c : ch == 1 ? mm : ch == 2 ? y : k;
                if (ch < 3) v = (v - k) & 0xff;
                uint g = (uint) (~v & 0xff);
                px [i] = (p & 0xff000000u) | g << 16 | g << 8 | g;
            }
        }
    }

    /// <summary>GpRecolor: one GpRecolorObject per ColorAdjustType.</summary>
    internal sealed class GpRecolor
    {
        readonly GpRecolorObject[] _obj = new GpRecolorObject [5];
        readonly bool[] _identity = new bool [5];

        public GpRecolor Clone ()
        {
            var c = new GpRecolor ();
            for (int i = 0; i < 5; i++) { c._obj [i] = _obj [i]?.Clone (); c._identity [i] = _identity [i]; }
            return c;
        }

        static void CheckType (ColorAdjustType t)
        {
            if ((uint) t > 4) throw new ArgumentException ("Parameter is not valid.");
        }

        /// <summary>SetRecolorObject @180084f00: the type's object, created on first use.</summary>
        public GpRecolorObject Set (ColorAdjustType t)
        {
            CheckType (t);
            int i = (int) t;
            if (_obj [i] == null) { _obj [i] = new GpRecolorObject (); _identity [i] = false; }
            return _obj [i];
        }

        /// <summary>ClearRecolorObject @1801c9910: the type's own object or null.</summary>
        public GpRecolorObject Existing (ColorAdjustType t) { CheckType (t); return _obj [(int) t]; }

        public void SetToIdentity (ColorAdjustType t) { CheckType (t); _obj [(int) t] = null; _identity [(int) t] = true; }

        public void Reset (ColorAdjustType t) { CheckType (t); _obj [(int) t] = null; _identity [(int) t] = false; }

        /// <summary>UseRecolorObject @1801aef70.</summary>
        public GpRecolorObject Use (ColorAdjustType t)
        {
            int i = (int) t;
            if ((uint) (i - 1) >= 4) return null;
            return _obj [i] ?? (_identity [i] ? null : _obj [0]);
        }

        public bool HasRecoloring (ColorAdjustType t)
        {
            if ((int) t == 6) {
                for (int i = 0; i < 5; i++) if (_obj [i] != null && _obj [i].Flags != 0) return true;
                return false;
            }
            GpRecolorObject o = Use (t);
            return o != null && o.Flags != 0;
        }

        public void Flush ()
        {
            for (int i = 0; i < 5; i++)
                if (_obj [i] != null && (i == 0 || _obj [i] != _obj [0])) _obj [i].Flush ();
        }

        public void ColorAdjust (uint[] argb, int o, int n, ColorAdjustType t) => Use (t)?.ColorAdjust (argb, o, n);

        /// <summary>The bitmap as recoloured (PipeLockBitsFromMemory's InitImageBitmap with the
        /// recolor operation, GpRecolorOp::Run @180100f00 = ColorAdjust on straight ARGB).</summary>
        public GdipFrame Apply (GdipFrame f, ColorAdjustType type)
        {
            GpRecolorObject o = Use (type);
            if (o == null) return f;
            var dst = new GdipFrame (f.Width, f.Height, PixelFormat.Format32bppArgb) { DpiX = f.DpiX, DpiY = f.DpiY };
            var row = new uint [f.Width];
            for (int y = 0; y < f.Height; y++) {
                GdipPixels.ReadArgb (f, 0, y, f.Width, row, 0);
                o.ColorAdjust (row, 0, f.Width);
                GdipPixels.WriteArgb (dst, 0, y, f.Width, row, 0);
            }
            return dst;
        }
    }
}
