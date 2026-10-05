// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The colour transform ImageAttributes' output channel separates through, as Windows makes it.
//
// GDI+ (GpRecolorObject::Flush @1800fcaf0) separates CMYK through ICM: with no profile of its own
// it asks for "rswop.icm" (SetupCmykSeparation @1800fd028), and GpICMHolder::Init @1800fccf8
// builds mscms' CreateMultiProfileTransform(sRGB, that profile, perceptual both, BEST_MODE |
// USE_RELATIVE_COLORIMETRIC) and TranslateBitmapBits(BM_xRGBQUADS -> BM_KYMCQUADS) runs it.
// mscms hands both to icm32.dll, the LinoColor CMM, and this is that CMM (icm32.dll arm64,
// 10.0.26100, public PDB) for the profiles that path takes: a matrix/TRC RGB source into an
// output profile's lut16:
//
//   CMCreateMultiProfileTransformInternal @18000d6f8   BEST_MODE (3) -> quality 2, the relative
//        colorimetric flag kept (0x100000), gamut checking off (0x80000)
//   PrepareCombiLUTs @180014238 / CreateCombi @180006af8   the profiles concatenated into one
//        "combi" LUT: a linear input ELUT, a cube of 2^n points per input channel (n = 5 for RGB at
//        this quality: 32^3), a linear output ALUT
//   MakeCube16 @18000bff8         the cube's nodes, i << (16 - n) with its bits replicated down
//   ExtractAll_TRC_Luts @180011a40 the source's TRC curves as 16-bit ELUTs (Extract_TRC_Elut
//        @180013528, Fill_ushort_ELUT_from_CurveTag @18000afe0), its colorants as a matrix halved
//        into the 16-bit XYZ encoding (Extract_TRC_Matrix @180013888), a linear ALUT
//        (CreateLinearAlut16 @180040d60)
//   DoMatrixForCube16 @18000c308  ELUT, matrix and ALUT over every node
//   XYZ2Lab_forCube16 @18002f550  into Lab, through a cube-root table (@18006bba0)
//   ExtractAll_MFT_LutsFromLut16 @1800110b0, CalcNDim_Data8To8_Lut16 @18002be60 ->
//        Calc324Dim_Data8To8_Lut16 @180030798   the output profile's B2A0 lut16 over every node
//   FillDescaledInputLut @18002f800 the input ELUT scaled onto the cube's grid
//   LHCalc3to4_Di8_Do8_Lut16_G32 @18001c250   per pixel: tetrahedral interpolation of the cube
//        (11-bit weights), then the output ALUT
//

using System.IO;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpIcm
    {
        // ---- the profile ---------------------------------------------------------------------------

        sealed class Profile
        {
            public byte[] D;
            public uint Class, ColorSpace, Pcs, Version;

            public Profile (byte[] d)
            {
                if (d == null || d.Length < 132 || U32 (d, 36) != 0x61637370) throw new InvalidDataException ();
                D = d;
                Version = U32 (d, 8); Class = U32 (d, 12); ColorSpace = U32 (d, 16); Pcs = U32 (d, 20);
            }

            /// <summary>The tag's data (offset, size), or (-1, 0).</summary>
            public (int, int) Tag (uint sig)
            {
                uint n = U32 (D, 128);
                for (uint i = 0; i < n && 144 + 12 * i <= D.Length; i++) {
                    int o = (int) (132 + 12 * i);
                    if (U32 (D, o) == sig) {
                        uint off = U32 (D, o + 4), size = U32 (D, o + 8);
                        if (off + (ulong) size > (ulong) D.Length) return (-1, 0);
                        return ((int) off, (int) size);
                    }
                }
                return (-1, 0);
            }
        }

        static uint U32 (byte[] d, int o) => (uint) (d [o] << 24 | d [o + 1] << 16 | d [o + 2] << 8 | d [o + 3]);
        static ushort U16 (byte[] d, int o) => (ushort) (d [o] << 8 | d [o + 1]);
        static int S32 (byte[] d, int o) => (int) U32 (d, o);

        const uint SigCurv = 0x63757276, SigPara = 0x70617261, SigMft2 = 0x6d667432, SigMft1 = 0x6d667431;

        // ---- the source's TRC ELUTs (Fill_ushort_ELUT_from_CurveTag @18000afe0) --------------------------

        /// <summary>A 'curv' of n > 1 entries as an ELUT of 2^inBits entries of outBits.</summary>
        static void ElutFromCurve (byte[] d, int o, ushort[] dst, int dstOff, int inBits, int outBits)
        {
            uint count = U32 (d, o + 8);
            if (count < 2 || count >= 0x2000 || inBits < 1 || inBits > 15 || outBits <= 8) throw new NotSupportedException ();
            int n = 1 << inBits;
            uint last = (uint) (n - 1);
            uint step = ((count - 1) * 0x40000u) / last;
            int shift = 0x16, round = 0x1fffff;
            uint scale = (uint) (((double) ((1 << outBits) - 1) / 65535.0) * 4194304.0);
            for (; (scale & 0xfff8000) != 0; scale = (uint) ((int) scale >> 1)) { shift--; round >>= 1; }
            for (int i = 0; i < n; i++) {
                uint p = step * (uint) i + 4;
                uint idx = (uint) ((int) p >> 3) >> 15;
                uint frac = p >> 3 & 0x7fff;
                uint v0 = U16 (d, o + 12 + (int) idx * 2);
                int t = (int) (scale * v0);
                ushort r;
                if (frac == 0) r = (ushort) (t + round >> shift);
                else {
                    uint v1 = U16 (d, o + 12 + (int) idx * 2 + 2);
                    r = (ushort) ((t >> 1) + ((int) ((uint) ((int) ((v1 - v0) * scale) >> 15) * frac) >> 1) + (round >> 1) >> (shift - 1));
                }
                dst [dstOff + i] = r;
            }
        }

        /// <summary>CreateLinearAlut16 @180040d60: 1024 entries.</summary>
        static void LinearAlut16 (ushort[] dst, int o)
        {
            for (int i = 0; i < 0x400; i++) dst [o + i] = (ushort) Math.Min ((i * 0x400fc + 0x7ff) >> 12, 0xffff);
        }

        /// <summary>CreateLinearElut16 @1800372a0.</summary>
        static void LinearElut16 (ushort[] dst, int o, int n)
        {
            uint m = (uint) (n - 1);
            for (int i = 0; i < n; i++) {
                uint v = ((uint) i * 0xffff + (m >> 1) - 1) / m;
                dst [o + i] = (ushort) (v < 0x10000 ? v : 0xffff);
            }
        }

        // ---- MakeCube16 @18000bff8 (three dimensions) ------------------------------------------------

        static ushort[] MakeCube3 (int bits)
        {
            int n = 1 << bits;
            var c = new ushort [n * n * n * 3];
            int s = 16 - bits;
            ushort Node (int i)
            {
                uint v = (uint) (i << s) & 0xffff;
                v = v >> bits | v;
                v = v >> (2 * bits) | v;
                return (ushort) (v >> (4 * bits) | v);
            }
            int k = 0;
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                    for (int e = 0; e < n; e++) { c [k++] = Node (a); c [k++] = Node (b); c [k++] = Node (e); }
            return c;
        }

        // ---- DoMatrixForCube16 @18000c308 (16-bit ELUT, 16-bit ALUT, 16-bit cube) ----------------------

        static void DoMatrixForCube16 (ushort[] cube, int nodes, ushort[] elut, int elutSize, bool elutSeparate, int elutBits,
            ushort[] alut, int alutSize, bool alutSeparate, int alutBits, double[] mtx)
        {
            int eb = 0;
            while (eb < 0x21 && 1 << eb != elutSize) eb++;
            int s = 16 - eb, ef = elutBits - s;
            if (ef < 0) throw new NotSupportedException ();
            int one = 1 << s;
            var m = new int [9];
            double sc = (double) (alutSize - 1) * 16.0;
            for (int i = 0; i < 9; i++) {
                double v = mtx [i] * sc;
                v = v <= 0.0 ? v - 0.5 : v + 0.5;
                m [i] = (int) v;
            }
            int af = 16 - alutBits, ash = 8 - af, eup = s - ef;
            uint elast = (uint) (elutSize - 1), alast = (uint) (alutSize - 1);
            int a1 = alutSeparate ? alutSize : 0;
            int Elut (int c, int off)
            {
                uint v = (uint) c - (uint) (c >> eb);
                uint idx = v >> s, f = (uint) (one - 1) & v;
                uint e = elut [off + idx];
                if (idx < elast) return (int) (e * (uint) (one - f) + elut [off + idx + 1] * f >> ef);
                return (int) (e << eup);
            }
            ushort Alut (int x, int off)
            {
                if (x < 0) x = 0;
                uint idx = (uint) (x >> 8);
                if (idx < alast) return (ushort) ((uint) alut [off + idx] * (uint) (0x100 - (x & 0xff)) + (uint) alut [off + idx + 1] * (uint) (x & 0xff) >> ash);
                return (ushort) (alut [off + alutSize - 1] << af);
            }
            for (int i = 0, p = 0; i < nodes; i++, p += 3) {
                int e0 = Elut (cube [p], 0);
                int e1 = Elut (cube [p + 1], elutSeparate ? elutSize : 0);
                int e2 = Elut (cube [p + 2], elutSeparate ? 2 * elutSize : 0);
                int i2 = e0 + 2 >> 2, i4 = e1 + 2 >> 2, i1 = e2 + 2 >> 2;
                cube [p] = Alut (m [2] * i1 + m [1] * i4 + m [0] * i2 + 0x1ff >> 10, 0);
                cube [p + 1] = Alut (m [5] * i1 + m [4] * i4 + m [3] * i2 + 0x1ff >> 10, a1);
                cube [p + 2] = Alut (m [8] * i1 + m [7] * i4 + m [6] * i2 + 0x1ff >> 10, alutSeparate ? 2 * alutSize : 0);
            }
        }

        // ---- XYZ2Lab_forCube16 @18002f550 -------------------------------------------------------------

        /// <summary>@18006bba0: the cube-root table XYZ2Lab_forCube16 interpolates.</summary>
        static readonly ushort[] s_cubeRoot = {
            0, 1156, 2312, 3391, 4260, 4993, 5635, 6208, 6730, 7209, 7654, 8070, 8462, 8833, 9185, 9520,
            9841, 10149, 10445, 10731, 11006, 11273, 11531, 11781, 12024, 12261, 12491, 12716, 12935, 13149, 13358, 13562,
            13762, 13958, 14150, 14338, 14523, 14704, 14883, 15058, 15230, 15399, 15565, 15729, 15890, 16049, 16206, 16360,
            16512, 16662, 16810, 16957, 17101, 17243, 17384, 17522, 17659, 17795, 17929, 18061, 18192, 18322, 18450, 18577,
            18702, 18826, 18949, 19070, 19191, 19310, 19428, 19545, 19661, 19775, 19889, 20002, 20114, 20224, 20334, 20443,
            20551, 20658, 20764, 20869, 20974, 21077, 21180, 21282, 21383, 21484, 21584, 21683, 21781, 21878, 21975, 22072,
            22167, 22262, 22356, 22450, 22543, 22635, 22727, 22818, 22908, 22998, 23087, 23176, 23265, 23352, 23439, 23526,
            23612, 23698, 23783, 23868, 23952, 24035, 24119, 24201, 24284, 24365, 24447, 24528, 24608, 24688, 24768, 24847,
            24926, 25004, 25082, 25159, 25237, 25313, 25390, 25466, 25541, 25617, 25692, 25766, 25840, 25914, 25988, 26061,
            26134, 26206, 26278, 26350, 26421, 26493, 26563, 26634, 26704, 26774, 26844, 26913, 26982, 27051, 27119, 27187,
            27255, 27323, 27390, 27457, 27524, 27590, 27656, 27722, 27788, 27853, 27919, 27983, 28048, 28112, 28177, 28241,
            28304, 28368, 28431, 28494, 28556, 28619, 28681, 28743, 28805, 28867, 28928, 28989, 29050, 29111, 29171, 29231,
            29291, 29351, 29411, 29470, 29530, 29589, 29647, 29706, 29765, 29823, 29881, 29939, 29996, 30054, 30111, 30168,
            30225, 30282, 30339, 30395, 30451, 30507, 30563, 30619, 30674, 30730, 30785, 30840, 30895, 30949, 31004, 31058,
            31112, 31166, 31220, 31274, 31327, 31381, 31434, 31487, 31540, 31593, 31645, 31698, 31750, 31802, 31854, 31906,
            31958, 32010, 32061, 32112, 32164, 32215, 32265, 32316, 32367, 32417, 32468, 32518, 32568, 32618, 32668, 32717,
            32767, 32816, 32866, 32915, 32964, 33013, 33062, 33110, 33159, 33207, 33256, 33304, 33352, 33400, 33448, 33495,
            33543, 33590, 33638, 33685, 33732, 33779, 33826, 33873, 33919, 33966, 34013, 34059, 34105, 34151, 34197, 34243,
            34289, 34335, 34380, 34426, 34471, 34516, 34562, 34607, 34652, 34697, 34741, 34786, 34831, 34875, 34919, 34964,
            35008, 35052, 35096, 35140, 35184, 35227, 35271, 35314, 35358, 35401, 35444, 35488, 35531, 35574, 35617, 35659,
            35702, 35745, 35787, 35830, 35872, 35914, 35956, 35998, 36040, 36082, 36124, 36166, 36208, 36249, 36291, 36332,
            36373, 36415, 36456, 36497, 36538, 36579, 36620, 36660, 36701, 36742, 36782, 36823, 36863, 36903, 36943, 36984,
            37024, 37064, 37104, 37143, 37183, 37223, 37262, 37302, 37342, 37381, 37420, 37459, 37499, 37538, 37577, 37616,
            37655, 37693, 37732, 37771, 37810, 37848, 37887, 37925, 37963, 38002, 38040, 38078, 38116, 38154, 38192, 38230,
            38268, 38305, 38343, 38381, 38418, 38456, 38493, 38530, 38568, 38605, 38642, 38679, 38716, 38753, 38790, 38827,
            38864, 38900, 38937, 38974, 39010, 39047, 39083, 39119, 39156, 39192, 39228, 39264, 39300, 39336, 39372, 39409,
            39444, 39480, 39516, 39551, 39588, 39622, 39658, 39693, 39729, 39764, 39799, 39835, 39870, 39905, 39940, 39975,
        };

        static int CubeRoot (uint idx, uint frac, int fracBits, int round)
        {
            if (idx >= 0x1af) return 0x9c27;
            uint t0 = s_cubeRoot [idx], t1 = s_cubeRoot [idx + 1];
            return (int) t0 + ((int) ((t1 - t0) * frac + (uint) round) >> fracBits);
        }

        static ushort Clamp16 (int v) => (v & 0xffff0000) == 0 ? (ushort) v : v < 0 ? (ushort) 0 : (ushort) 0xffff;

        static void Xyz2Lab (ushort[] c, int nodes)
        {
            for (int i = 0, p = 0; i < nodes; i++, p += 3) {
                uint y = c [p + 1];
                int fy = CubeRoot (y >> 7, y & 0x7f, 7, 0x3f);
                uint x = (uint) c [p] * 0x213;
                int fx = CubeRoot ((uint) ((int) x >> 4) >> 12, x >> 4 & 0xfff, 12, 0x7ff);
                uint z = (uint) c [p + 2] * 0x26cb;
                int fz = CubeRoot ((uint) ((int) z >> 8) >> 12, z >> 8 & 0xfff, 12, 0x7ff);
                ushort a = Clamp16 ((fx - fy) * 0x1af + 0x400040 >> 7);
                ushort b = Clamp16 ((fy - fz) * 0x2b1b + 0x10001000 >> 13);
                c [p] = fy * 2 < 0x10000 ? (ushort) (fy * 2) : (ushort) 0xffff;
                c [p + 1] = a;
                c [p + 2] = b;
            }
        }

        // ---- the source: a matrix/TRC RGB profile (ExtractAll_TRC_Luts @180011a40) -------------------

        static readonly uint[] s_trc = { 0x72545243, 0x67545243, 0x62545243 };    // rTRC gTRC bTRC
        static readonly uint[] s_xyz = { 0x7258595a, 0x6758595a, 0x6258595a };    // rXYZ gXYZ bXYZ

        static ushort[] TrcElut (Profile p)
        {
            var e = new ushort [3 * 256];
            for (int c = 0; c < 3; c++) {
                (int o, int n) = p.Tag (s_trc [c]);
                if (o < 0 || n < 12 || U32 (p.D, o) != SigCurv || (ulong) n < ((ulong) U32 (p.D, o + 8) + 6) * 2) throw new NotSupportedException ();
                ElutFromCurve (p.D, o, e, c * 256, 8, 16);
            }
            return e;
        }

        /// <summary>Extract_TRC_Matrix @180013888: the colorants (s15Fixed16) halved, row r = X/Y/Z.</summary>
        static double[] TrcMatrix (Profile p)
        {
            var m = new double [9];
            for (int c = 0; c < 3; c++) {
                (int o, int n) = p.Tag (s_xyz [c]);
                if (o < 0 || n < 20) throw new NotSupportedException ();
                for (int r = 0; r < 3; r++) m [r * 3 + c] = (double) S32 (p.D, o + 8 + 4 * r) * 1.52587890625E-5 * 0.5;
            }
            return m;
        }

        // ---- the output profile's lut16 (ExtractAll_MFT_LutsFromLut16 @1800110b0) -------------------------

        /// <summary>An 'mft2' as icm32 resamples it: ELUTs of 256 x 16 bits per input, the CLUT as
        /// it is (16 bits), ALUTs of 1024 x 16 bits per output.</summary>
        sealed class Lut16
        {
            public int In, Out, Grid;
            public ushort[] Elut, Xlut, Alut, DescaledElut;
        }

        static Lut16 ReadLut16 (Profile p, uint sig)
        {
            (int o, int n) = p.Tag (sig);
            if (o < 0 || n < 52 || U32 (p.D, o) != SigMft2) throw new NotSupportedException ();
            byte[] d = p.D;
            var l = new Lut16 { In = d [o + 8], Out = d [o + 9], Grid = d [o + 10] };
            int nIn = U16 (d, o + 48), nOut = U16 (d, o + 50);
            if (l.In == 0 || l.Out == 0 || l.In > 8 || l.Out > 8 || l.Grid < 2 || nIn < 2 || nOut < 2) throw new NotSupportedException ();
            long clut = l.Out;
            for (int i = 0; i < l.In; i++) clut *= l.Grid;
            int eo = o + 52, xo = eo + l.In * nIn * 2, ao = xo + (int) clut * 2;
            if (ao + (long) l.Out * nOut * 2 > o + (long) n) throw new InvalidDataException ();
            l.Elut = ElutsFromLut16 (d, eo, l.In, nIn, 16);
            l.Xlut = new ushort [clut];
            for (int i = 0; i < clut; i++) l.Xlut [i] = U16 (d, xo + i * 2);
            l.Alut = AlutsFromLut16 (d, ao, l.Out, nOut);
            return l;
        }

        /// <summary>Fill_ushort_ELUTs_from_lut16Tag @180015a48: each input table resampled to 256
        /// entries (one zero after the last table).</summary>
        static ushort[] ElutsFromLut16 (byte[] d, int o, int channels, int n, int bits)
        {
            if (n > 0x2000) throw new InvalidDataException ();
            var e = new ushort [channels * 256 + 1];
            int x = n * 0x40000 - 0x40000;
            x = (int) ((long) x * unchecked ((int) 0x80808081) >> 32) + x;
            int step = (x >> 7) - (x >> 31);
            int shift = 0x16, round = 0x1fffff;
            uint scale = (uint) (((double) ((1 << bits) - 1) / 65535.0) * 4194304.0);
            for (; (scale & 0xfff8000) != 0; scale = (uint) ((int) scale >> 1)) { shift--; round >>= 1; }
            for (int c = 0; c < channels; c++) {
                int t0 = o + c * n * 2;
                for (int i = 0; i < 256; i++) {
                    uint p = (uint) (step * i + 4);
                    uint idx = (uint) ((int) p >> 3) >> 15;
                    uint frac = p >> 3 & 0x7fff;
                    uint v0 = U16 (d, t0 + (int) idx * 2);
                    int t = (int) (scale * v0);
                    ushort r;
                    if (frac == 0) r = (ushort) (t + round >> shift);
                    else {
                        uint v1 = U16 (d, t0 + (int) idx * 2 + 2);
                        r = (ushort) ((t >> 1) + ((int) ((uint) ((int) ((v1 - v0) * scale) >> 15) * frac) >> 1) + (round >> 1) >> (shift - 1));
                    }
                    e [c * 256 + i] = r;
                }
            }
            return e;
        }

        /// <summary>Fill_ushort_ALUTs_from_lut16Tag @18002f230: each output table resampled to 1024
        /// entries (one zero after the last).</summary>
        static ushort[] AlutsFromLut16 (byte[] d, int o, int channels, int n)
        {
            if (n > 0x1000) throw new InvalidDataException ();
            var a = new ushort [channels * 0x400 + 1];
            uint x = (uint) (n - 1) * 0x100000;
            uint hi = (uint) ((ulong) x * 0x401005 >> 32);
            uint step = hi + (x - hi >> 1) >> 9;
            for (int c = 0; c < channels; c++) {
                int t0 = o + c * n * 2;
                for (uint i = 0; i < 0x400; i++) {
                    uint p = i * step + 0x10;
                    int idx = (int) (p >> 0x14);
                    uint f = p >> 5 & 0x7fff;
                    ushort v = U16 (d, t0 + idx * 2);
                    if (f != 0) v = (ushort) (v + (short) ((int) (((uint) U16 (d, t0 + idx * 2 + 2) - v) * f + 0x3fff) >> 0xf));
                    a [c * 0x400 + i] = v;
                }
            }
            return a;
        }

        /// <summary>FillDescaledInputLut @18002f800: an ELUT of 2^bits entries scaled onto a grid of
        /// <paramref name="grid"/> points (one zero after).</summary>
        static ushort[] Descale (ushort[] elut, int count, int bits, int grid)
        {
            var r = new ushort [count + 1];
            int full = 1 << bits;
            uint last = (uint) (full - 1);
            for (int i = 0; i < count; i++) {
                uint v = (uint) elut [i] * (uint) full / last;
                r [i] = (ushort) (((uint) (grid - 1) * v) / (uint) grid);
            }
            return r;
        }

        /// <summary>Calc324Dim_Data8To8_Lut16 @180030798, a grid of points that is not a power of
        /// two, 16-bit CLUT, 16-bit ALUT, 16-bit in and out: three 16-bit channels per node of
        /// <paramref name="src"/> into four of <paramref name="dst"/>.</summary>
        static void Calc324 (ushort[] src, int nodes, ushort[] dst, Lut16 l, ushort[] delut, int elutBits)
        {
            int g = l.Grid;
            const int xlutBits = 16, alutBits = 16;
            int sum = xlutBits + elutBits - 10;
            int ab = sum < 0x11 ? sum : 0x10, post = sum > 0x10 ? sum - 0x10 : 0;
            uint fmask = (uint) (1 << elutBits) - 1;
            int one = 1 << ab;
            int ash = ab - (16 - alutBits);
            var stride = new int [3];
            for (int k = 2, s = 4; k >= 0; k--, s *= g) stride [k] = s;
            uint top = (uint) (g + 0xffff) & 0xffff;
            var fr = new int [3];
            var ix = new uint [3];
            var acc = new uint [4];
            ushort[] x = l.Xlut, a = l.Alut;
            for (int nd = 0, p = 0, q = 0; nd < nodes; nd++, p += 3, q += 4) {
                for (int c = 0; c < 3; c++) {
                    uint v = (uint) src [p + c] - (uint) (src [p + c] >> 8);
                    uint i = v >> 8, f = v & 0xff;
                    uint t = ((uint) delut [c * 256 + i] * (0x100 - f) + (uint) delut [c * 256 + i + 1] * f >> 8) * (uint) g;
                    fr [c] = (int) (fmask & t);
                    ix [c] = (ushort) (t >> elutBits);
                }
                int bse = stride [0] * (int) ix [0] + stride [1] * (int) ix [1] + stride [2] * (int) ix [2];
                int imax = fr [0] < fr [1] ? 1 : 0, imid = fr [1] <= fr [0] ? 1 : 0, imin = 2;
                if (fr [imid] < fr [2]) {
                    imin = imid; imid = 2;
                    if (fr [imax] < fr [2]) { imid = imax; imax = 2; }
                }
                uint w0 = (uint) ((1 << elutBits) - fr [imax]), w1 = (uint) (fr [imax] - fr [imid]), w2 = (uint) (fr [imid] - fr [imin]), w3 = (uint) fr [imin];
                int off = 0;
                if (ix [imax] < top) off = stride [imax];
                int n1 = bse + off;
                if (ix [imid] < top) off += stride [imid];
                int n2 = bse + off;
                if (ix [imin] < top) off += stride [imin];
                int n3 = bse + off;
                for (int k = 0; k < 4; k++)
                    acc [k] = x [bse + k] * w0 + x [n1 + k] * w1 + x [n2 + k] * w2 + x [n3 + k] * w3;
                for (int k = 0; k < 4; k++) {
                    uint u = (acc [k] >> xlutBits) + acc [k];
                    u = u - (u >> 10) >> post;
                    uint f = (uint) (one - 1) & u;
                    int i = (int) (u >> ab) + k * 0x400;
                    dst [q + k] = (ushort) ((uint) a [i] * (uint) (one - (int) f) + (uint) a [i + 1] * f >> ash);
                }
            }
        }

        // ---- the transform ---------------------------------------------------------------------------

        const int GridBits = 5, Grid = 1 << GridBits, Nodes = Grid * Grid * Grid;

        ushort[] _in;      // 3 x 256: the descaled linear input ELUT (5 bits of node, 11 of fraction)
        ushort[] _grid;    // Nodes x 4, padded for the far neighbours of the last node
        ushort[] _out;     // 4 x 1024: the output profile's own ALUTs
        public readonly int Outputs;

        GpIcm (ushort[] input, ushort[] grid, ushort[] output, int outputs) { _in = input; _grid = grid; _out = output; Outputs = outputs; }

        /// <summary>The combi CreateCombi builds for an RGB matrix/TRC source into an output profile's
        /// perceptual lut16 (B2A0) of four channels; anything else is not ported.</summary>
        public static GpIcm Create (byte[] source, byte[] destination)
        {
            var ps = new Profile (source);
            var pd = new Profile (destination);
            // A destination that is not CMYK makes a transform mscms then refuses to translate into
            // BM_KYMCQUADS: TranslateBitmapBits fails and GDI+ (which ignores that) separates the
            // pixels as they were -- the quad's bytes are B, G, R and alpha.
            if (pd.ColorSpace != 0x434d594b) return new GpIcm (null, null, null, 0);
            if (ps.ColorSpace != 0x52474220 || ps.Pcs != 0x58595a20) throw new NotSupportedException ();
            if (pd.Pcs != 0x4c616220) throw new NotSupportedException ();
            ushort[] elut = TrcElut (ps);
            double[] mtx = TrcMatrix (ps);
            var lin = new ushort [4 * 0x400 + 1];
            for (int c = 0; c < 4; c++) LinearAlut16 (lin, c * 0x400);
            Lut16 l = ReadLut16 (pd, 0x42324130);
            if (l.In != 3 || l.Out != 4) throw new NotSupportedException ();
            ushort[] cube = MakeCube3 (GridBits);
            DoMatrixForCube16 (cube, Nodes, elut, 256, true, 16, lin, 0x400, true, 16, mtx);
            Xyz2Lab (cube, Nodes);
            var grid = new ushort [Nodes * 4 + 0x1100];
            var calc = new Lut16 { In = l.In, Out = l.Out, Grid = l.Grid, Xlut = l.Xlut, Alut = lin };
            Calc324 (cube, Nodes, grid, calc, Descale (l.Elut, 3 * 256, 16, l.Grid), 16);
            var ein = new ushort [3 * 256];
            for (int c = 0; c < 3; c++) LinearElut16 (ein, c * 256, 256);
            return new GpIcm (Descale (ein, 3 * 256, 16, Grid), grid, l.Alut, 4);
        }

        /// <summary>LHCalc3to4_Di8_Do8_Lut16_G32 @18001c250 for one pixel: the xRGB quad's R, G, B
        /// in, the KYMC quad out (byte 0 = C, 1 = M, 2 = Y, 3 = K); for a destination that is not
        /// CMYK the pixel as it was (the translation fails).</summary>
        public uint Translate (uint xrgb)
        {
            if (_in == null) return xrgb;
            uint e0 = _in [xrgb >> 16 & 0xff], e1 = _in [0x100 + (xrgb >> 8 & 0xff)], e2 = _in [0x200 + (xrgb & 0xff)];
            uint f0 = e0 & 0x7ff, f1 = e1 & 0x7ff, f2 = e2 & 0x7ff;
            int node = (int) ((e0 >> 11 << 10) + (e1 >> 11 << 5) + (e2 >> 11)) * 4;
            const int D0 = 0x1000, D1 = 0x80, D2 = 4;
            ushort[] g = _grid;
            uint r = 0;
            for (int c = 0; c < 4; c++) {
                int p = node + c;
                uint s;
                if (f2 < f0) {
                    if (f1 < f0) {
                        if (f2 < f1) s = g [p] * (0x800 - f0) + g [p + D0] * (f0 - f1) + g [p + D0 + D1] * (f1 - f2) + g [p + D0 + D1 + D2] * f2;
                        else s = g [p] * (0x800 - f0) + g [p + D0] * (f0 - f2) + g [p + D0 + D2] * (f2 - f1) + g [p + D0 + D1 + D2] * f1;
                    } else s = g [p] * (0x800 - f1) + g [p + D1] * (f1 - f0) + g [p + D0 + D1] * (f0 - f2) + g [p + D0 + D1 + D2] * f2;
                } else {
                    if (f1 < f0) s = g [p] * (0x800 - f2) + g [p + D2] * (f2 - f0) + g [p + D0 + D2] * (f0 - f1) + g [p + D0 + D1 + D2] * f1;
                    else if (f2 < f1) s = g [p] * (0x800 - f1) + g [p + D1] * (f1 - f2) + g [p + D1 + D2] * (f2 - f0) + g [p + D0 + D1 + D2] * f0;
                    else s = g [p] * (0x800 - f2) + g [p + D2] * (f2 - f1) + g [p + D1 + D2] * (f1 - f0) + g [p + D0 + D1 + D2] * f0;
                }
                r |= (uint) (_out [(s >> 17) + 0x400 * c] >> 8) << (8 * c);
            }
            return r;
        }

        // ---- GpICMHolder::Init @1800fccf8 as SetupCmykSeparation @1800fd028 asks for it ---------------

        /// <summary>Where mscms resolves a bare profile name: the colour directory.</summary>
        static string ColorDirectory =>
            OperatingSystem.IsWindows () ? Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.System), "spool", "drivers", "color") : null;

        static readonly object s_lock = new object ();
        static readonly System.Collections.Generic.Dictionary<string, (long, long, GpIcm)> s_cache = new System.Collections.Generic.Dictionary<string, (long, long, GpIcm)> ();

        /// <summary>The separation transform sRGB -> <paramref name="profile"/>, with GDI+'s status:
        /// 0, 2 (InvalidParameter: no ICM -- LoadICMDll's failure -- which is what a system without
        /// the standard sRGB profile is), or 3 (OutOfMemory: the profile does not open, or is not
        /// one). A profile of a kind this CMM does not build yet comes back as status 0 and no
        /// transform: the channel then separates by mapping.</summary>
        public static GpIcm Setup (string profile, out int status)
        {
            string dir = ColorDirectory;
            string srgb = dir == null ? null : Path.Combine (dir, "sRGB Color Space Profile.icm");
            if (srgb == null || !File.Exists (srgb)) { status = 2; return null; }
            string path = profile;
            try {
                if (dir != null && !Path.IsPathRooted (profile) && Path.GetFileName (profile) == profile) path = Path.Combine (dir, profile);
                path = Path.GetFullPath (path);
                if (!File.Exists (path)) { status = 3; return null; }
                long s1 = File.GetLastWriteTimeUtc (path).Ticks, s2 = File.GetLastWriteTimeUtc (srgb).Ticks;
                lock (s_lock) {
                    if (s_cache.TryGetValue (path, out (long, long, GpIcm) hit) && hit.Item1 == s1 && hit.Item2 == s2) { status = 0; return hit.Item3; }
                }
                byte[] dst = File.ReadAllBytes (path);
                new Profile (dst);
                GpIcm t;
                try { t = Create (File.ReadAllBytes (srgb), dst); }
                catch (NotSupportedException) { t = null; }
                lock (s_lock) s_cache [path] = (s1, s2, t);
                status = 0;
                return t;
            } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is ArgumentException || e is NotSupportedException || e is IndexOutOfRangeException) {
                status = 3;
                return null;
            }
        }

        // ---- debugging ---------------------------------------------------------------------------

        internal sealed class StageData
        {
            public ushort[] Elut, Alut, Cube0, Cube1, Cube2, Grid, SElut, SDelut, SAlut, SXlut;
            public double[] Matrix;
        }

        internal static class Debug
        {
            public static StageData Stages (byte[] src, byte[] dst)
            {
                var r = new StageData ();
                var ps = new Profile (src);
                r.Elut = TrcElut (ps);
                r.Matrix = TrcMatrix (ps);
                r.Alut = new ushort [3 * 0x400];
                for (int c = 0; c < 3; c++) LinearAlut16 (r.Alut, c * 0x400);
                ushort[] cube = MakeCube3 (5);
                r.Cube0 = (ushort[]) cube.Clone ();
                DoMatrixForCube16 (cube, 0x8000, r.Elut, 256, true, 16, r.Alut, 0x400, true, 16, r.Matrix);
                r.Cube1 = (ushort[]) cube.Clone ();
                Xyz2Lab (cube, 0x8000);
                r.Cube2 = (ushort[]) cube.Clone ();
                Lut16 l = ReadLut16 (new Profile (dst), 0x42324130);
                r.SElut = l.Elut; r.SAlut = l.Alut; r.SXlut = l.Xlut;
                r.SDelut = Descale (l.Elut, 3 * 256, 16, l.Grid);
                r.Grid = new ushort [0x8000 * 4];
                Calc324 (cube, 0x8000, r.Grid, l, r.SDelut, 16);
                return r;
            }
        }
    }
}
