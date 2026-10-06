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
//   ExtractAll_MFT_LutsFromLut16 @1800110b0 / ExtractAll_MFT_LutsFromLut8 @180011218,
//        CalcNDim_Data8To8_Lut16 @18002be60 -> Calc324Dim_Data8To8_Lut16 @180030798   the output
//        profile's B2A0 lut16 or lut8 over every node (16- or 8-bit CLUT, any grid, a power of two
//        or not), with a linear ALUT: the profile's own ALUT becomes the combi's output ALUT
//   FillDescaledInputLut @18002f800 the input ELUT scaled onto the cube's grid
//   LHCalc3to4_Di8_Do8_Lut16_G32 @18001c250   per pixel: tetrahedral interpolation of the cube
//        (11-bit weights), then the output ALUT
//
// and the output profiles that differ from that one:
//
//   CMValidateProfile @180003a80 / CMValMftOutput @18003b320   every profile is validated first: an
//        output profile without any of desc, A2B0-2, B2A0-2, gamt, wtpt, cprt (no B2A0, say) or with
//        a version other than 2.x/4.x or a PCS other than XYZ/Lab is ERROR_INVALID_PROFILE -- there
//        is no fallback to another tag -- and so is a B2A0 of a type ExtractAll_MFT_Luts @180010f48
//        does not read (mft1, mft2, and only in a v4 profile mBA). GDI+ then fails
//        SetOutputChannelColorProfile with Win32Error and separates through rswop.icm.
//   an XYZ PCS: Create_LH_ProfileSet @18000a948 puts no conversion entry in, XYZ2Lab never runs,
//        and CreateCombi moves the source's TRC ELUT out of the cube into the combi's input ELUT; a
//        lut16/lut8's matrix (GetMatrixFromProfile @180013b88) is applied to the cube, unless it is
//        the identity
//   a v4 profile: ExtractAllLuts @180010a38 marks it version 2 for the perceptual intent, and the
//        v2 cube gets the v4 perceptual black point (in XYZ, via Lab2XYZ_forCube16 @18002f348)
//   lutBtoA (ExtractAll_MFT_LutsFromLutBToA @1800116c8): B curves, matrix + M curves, a CLUT of a
//        grid per dimension and A curves, through the general path of CalcNDim_Data8To8_Lut16 on a
//        cube of Lab in the v4 encoding; its A curves become the combi's output ALUT
//
// Checked against mscms over all 16.7M colours: RSWOP.icm and profiles made from it with grids of
// 9, 16, 17 and 32 points, lut8 and lut16, linear and curved tables; and v2/v4 lut16, lut8 and
// lutBtoA output profiles on Lab and XYZ PCSs, with and without matrices, curv and para curves,
// 8- and 16-bit CLUTs, uniform and per-dimension grids: no difference.
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

            /// <summary>IsColorProfileTagPresent: the tag is in the table.</summary>
            public bool Has (uint sig)
            {
                uint n = U32 (D, 128);
                for (uint i = 0; i < n && 144 + 12 * i <= D.Length; i++)
                    if (U32 (D, (int) (132 + 12 * i)) == sig) return true;
                return false;
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

        // ---- Lab2XYZ_forCube16 @18002f348 -------------------------------------------------------------

        /// <summary>@18006b840: the cube table Lab2XYZ_forCube16 interpolates (432 entries).</summary>
        static readonly ushort[] s_cube = {
            0, 7, 14, 21, 28, 35, 43, 50, 57, 64, 71, 78, 85, 92, 99, 106,
            113, 120, 128, 135, 142, 149, 156, 164, 171, 180, 188, 196, 205, 214, 224, 233,
            243, 253, 263, 274, 285, 296, 308, 320, 332, 344, 357, 370, 384, 397, 411, 426,
            440, 455, 471, 487, 503, 519, 536, 553, 570, 588, 606, 625, 644, 663, 683, 703,
            723, 744, 766, 787, 809, 832, 855, 878, 902, 926, 950, 975, 1001, 1027, 1053, 1080,
            1107, 1135, 1163, 1192, 1221, 1250, 1280, 1311, 1342, 1373, 1405, 1438, 1470, 1504, 1538, 1572,
            1607, 1643, 1679, 1715, 1752, 1790, 1828, 1866, 1906, 1945, 1986, 2026, 2068, 2110, 2152, 2195,
            2239, 2283, 2328, 2373, 2419, 2466, 2513, 2561, 2609, 2658, 2707, 2757, 2808, 2860, 2912, 2964,
            3018, 3071, 3126, 3181, 3237, 3293, 3351, 3408, 3467, 3526, 3586, 3646, 3707, 3769, 3831, 3895,
            3959, 4023, 4088, 4154, 4221, 4288, 4356, 4425, 4495, 4565, 4636, 4708, 4780, 4853, 4927, 5002,
            5077, 5153, 5230, 5308, 5386, 5466, 5546, 5626, 5708, 5790, 5874, 5957, 6042, 6128, 6214, 6301,
            6389, 6478, 6567, 6658, 6749, 6841, 6934, 7028, 7122, 7218, 7314, 7411, 7509, 7608, 7707, 7808,
            7909, 8012, 8115, 8219, 8324, 8430, 8536, 8644, 8753, 8862, 8972, 9084, 9196, 9309, 9423, 9538,
            9654, 9770, 9888, 10007, 10126, 10247, 10368, 10491, 10614, 10739, 10864, 10991, 11118, 11246, 11375, 11506,
            11637, 11769, 11902, 12037, 12172, 12308, 12446, 12584, 12723, 12864, 13005, 13147, 13291, 13435, 13581, 13727,
            13875, 14024, 14173, 14324, 14476, 14629, 14783, 14938, 15094, 15252, 15410, 15569, 15730, 15891, 16054, 16218,
            16383, 16549, 16716, 16885, 17054, 17225, 17396, 17569, 17743, 17918, 18094, 18272, 18450, 18630, 18811, 18993,
            19176, 19361, 19546, 19733, 19921, 20110, 20301, 20492, 20685, 20879, 21074, 21270, 21468, 21667, 21867, 22068,
            22270, 22474, 22679, 22885, 23093, 23301, 23511, 23723, 23935, 24149, 24364, 24580, 24798, 25016, 25237, 25458,
            25681, 25905, 26130, 26356, 26584, 26814, 27044, 27276, 27509, 27744, 27979, 28216, 28455, 28695, 28936, 29178,
            29422, 29668, 29914, 30162, 30411, 30662, 30914, 31167, 31422, 31678, 31936, 32195, 32455, 32717, 32980, 33245,
            33511, 33778, 34047, 34317, 34589, 34862, 35137, 35413, 35690, 35969, 36249, 36531, 36814, 37099, 37385, 37673,
            37962, 38252, 38544, 38838, 39133, 39429, 39727, 40027, 40328, 40630, 40934, 41240, 41547, 41855, 42165, 42477,
            42790, 43105, 43421, 43739, 44058, 44379, 44701, 45025, 45351, 45678, 46006, 46337, 46668, 47002, 47337, 47673,
            48011, 48351, 48692, 49035, 49380, 49726, 50074, 50423, 50774, 51127, 51481, 51837, 52194, 52554, 52914, 53277,
            53641, 54007, 54374, 54743, 55114, 55486, 55861, 56236, 56614, 56993, 57374, 57756, 58141, 58526, 58914, 59303,
            59694, 60087, 60482, 60878, 61276, 61675, 62077, 62480, 62885, 63292, 63700, 64110, 64522, 64935, 65351, 65535,
        };

        /// <summary>One interpolated entry of s_cube, in signed 32 bits as the code is (an index
        /// past 0x1ae is 0x1fffe, a negative argument 0).</summary>
        static int CubeOf (int u)
        {
            if (u < 0) return 0;
            int i = u >> 8;
            if (i >= 0x1af) return 0x1fffe;
            int t0 = s_cube [i];
            return t0 * 2 + ((s_cube [i + 1] - t0) * (u & 0xff) + 0x3f >> 7);
        }

        static void Lab2Xyz (ushort[] c, int nodes)
        {
            for (int i = 0, p = 0; i < nodes; i++, p += 3) {
                int l = c [p], a = c [p + 1], b = c [p + 2];
                int fx = CubeOf (l + ((a + a * 0x12 + 0xf) >> 5) - 0x4c00);
                int fz = CubeOf (l - ((b * 0x5f + 0x1f) >> 6) + 0xbe00);
                int t0 = s_cube [l >> 8];
                int y = t0 * 2 + ((s_cube [(l >> 8) + 1] - t0) * (l & 0xff) + 0x3f >> 7);
                // signed 32-bit products (mul + asr), which wrap for fx of 0x1fffe: kept as the code is
                int x = unchecked (fx * 0x7b6b + 0x4000) >> 15;
                int z = unchecked (fz * 0x34cb + 0x2000) >> 14;
                c [p] = (ushort) (x < 0x10000 ? x : 0xffff);
                c [p + 1] = (ushort) y;
                c [p + 2] = (ushort) (z < 0x10000 ? z : 0xffff);
            }
        }

        // ---- the version step: a v2 cube into a v4 profile (CreateCombi @180006af8, @18000930c) -------

        /// <summary>ADJUST_BLACK_POINT_TO_V4 (@180009364): the perceptual black point a v4 profile
        /// assumes, put on a cube that came through a v2 one -- each XYZ channel times a scale plus the
        /// PRM black (constants @18000a92c..@18000a940), in single precision with one rounding per
        /// operation (fmul, then fadd), truncated (fcvtzu) and stored as 16 bits.</summary>
        static void BlackPointToV4 (ushort[] c, int nodes)
        {
            float sx = BitConverter.Int32BitsToSingle (0x3f7f1ba0), ox = BitConverter.Int32BitsToSingle (0x42dc3372);
            float sy = BitConverter.Int32BitsToSingle (0x3f7f1c63), oy = BitConverter.Int32BitsToSingle (0x42e39cf3);
            float sz = BitConverter.Int32BitsToSingle (0x3f7f1bfd), oz = BitConverter.Int32BitsToSingle (0x42bc169c);
            for (int i = 0, p = 0; i < nodes; i++, p += 3) {
                float x = (float) c [p] * sx; x += ox;
                float y = (float) c [p + 1] * sy; y += oy;
                float z = (float) c [p + 2] * sz; z += oz;
                c [p] = (ushort) (uint) x;
                c [p + 1] = (ushort) (uint) y;
                c [p + 2] = (ushort) (uint) z;
            }
        }

        // ---- a lut16/lut8's matrix (GetMatrixFromProfile @180013b88, applied by CreateCombi @180009b00) -

        /// <summary>The tag's matrix as GetMatrixFromProfile reads it (s15Fixed16 times 1/65536,
        /// @180013ca8), or null when MatrixIsCloseToIdentity @180047608 finds every element within
        /// 1e-6f (@180013cb0) of the identity -- then nothing is applied.</summary>
        static double[] LutMatrix (byte[] d, int o)
        {
            var m = new double [9];
            for (int i = 0; i < 9; i++) m [i] = (double) S32 (d, o + 12 + 4 * i) * 1.52587890625E-5 * 1.0;
            double tol = (double) 1e-6f;
            for (int i = 0; i < 9; i++)
                if (tol <= Math.Abs (m [i] - (i % 4 == 0 ? 1.0 : 0.0))) return m;
            return null;
        }

        /// <summary>The matrix over every node: its elements rounded to 12 fraction bits (times
        /// 4096.0 @18000a920, half away from zero, truncated), each row summed in 32 bits, then
        /// 0x7ff away from zero, an arithmetic shift of 12 and a clamp to 16 bits.</summary>
        static void ApplyLutMatrix (ushort[] c, int nodes, double[] mtx)
        {
            var m = new int [9];
            for (int i = 0; i < 9; i++) {
                double v = mtx [i] * 4096.0;
                v = v <= 0.0 ? v - 0.5 : v + 0.5;
                m [i] = (int) v;
            }
            static ushort Row (int v)
            {
                v = (v < 1 ? v - 0x7ff : v + 0x7ff) >> 12;
                return v < 0 ? (ushort) 0 : v < 0x10000 ? (ushort) v : (ushort) 0xffff;
            }
            for (int i = 0, p = 0; i < nodes; i++, p += 3) {
                int x = c [p], y = c [p + 1], z = c [p + 2];
                c [p] = Row (x * m [0] + z * m [2] + y * m [1]);
                c [p + 1] = Row (z * m [5] + y * m [4] + x * m [3]);
                c [p + 2] = Row (z * m [8] + y * m [7] + x * m [6]);
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
            public int In, Out, Grid, XlutBits = 16;
            public ushort[] Elut, Xlut, Alut, DescaledElut;
            public double[] Matrix;          // a three-input lut on an XYZ PCS: its matrix, unless the identity
        }

        /// <summary>The output profile's lut16 or lut8 (ExtractAll_MFT_LutsFromLut16 @1800110b0,
        /// ExtractAll_MFT_LutsFromLut8 @180011218).</summary>
        static Lut16 ReadLut (Profile p, uint sig)
        {
            (int o, int n) = p.Tag (sig);
            Lut16 l = o >= 0 && n >= 48 && U32 (p.D, o) == SigMft1 ? ReadLut8 (p, o, n) : ReadLut16 (p, sig);
            // GetMatrixFromProfile @180013b88, asked for by both readers when the lut has three inputs
            // and the space it reads (an output profile's PCS) is XYZ
            if (l.In == 3 && p.Pcs == 0x58595a20) l.Matrix = LutMatrix (p.D, o);
            return l;
        }

        /// <summary>mscms' transform fails (ERROR_INVALID_PROFILE, 0x7db, from CMValidateProfile or an
        /// extractor): GDI+'s GpICMHolder::Init @1800fccf8 then returns E_FAIL, which
        /// SetOutputChannelProfile @18008f138 reports as Win32Error (7).</summary>
        internal sealed class Refused : Exception { }

        /// <summary>CMValidateProfile @180003a80 on an output profile: a major version of 2 or 4,
        /// and CMValOutput @18003b590 -> CMValMftOutput @18003b320: desc, A2B0, A2B1, A2B2, B2A0, B2A1,
        /// B2A2, gamt, wtpt and cprt all present (tags @18003b498), and a PCS of XYZ or Lab.</summary>
        static void ValidateOutput (Profile p)
        {
            uint major = p.Version & 0xff000000;
            if (((major < 0x2000000 || 0x3ffffff < major) && major != 0x4000000) || major == 0x3000000) throw new Refused ();
            foreach (uint t in s_outputTags)
                if (!p.Has (t)) throw new Refused ();
            if (p.Pcs != 0x58595a20 && p.Pcs != 0x4c616220) throw new Refused ();
        }

        static readonly uint[] s_outputTags = {
            0x64657363, 0x41324230, 0x41324231, 0x41324232, 0x42324130, 0x42324131, 0x42324132, 0x67616d74, 0x77747074, 0x63707274,
        };

        /// <summary>A 'mft1': ELUTs of 256 x 16 bits (Fill_ushort_ELUTs_from_lut8Tag @180047058), the
        /// 8-bit CLUT as it is (ExtractXlutFromLut8 @180012ab0), ALUTs of 1024 x 16 bits
        /// (Fill_ushort_ALUTs_from_lut8Tag @180061ec0).</summary>
        static Lut16 ReadLut8 (Profile p, int o, int n)
        {
            byte[] d = p.D;
            var l = new Lut16 { In = d [o + 8], Out = d [o + 9], Grid = d [o + 10], XlutBits = 8 };
            if (l.In == 0 || l.Out == 0 || l.In > 8 || l.Out > 8 || l.Grid < 2) throw new NotSupportedException ();
            long clut = l.Out;
            for (int i = 0; i < l.In; i++) clut *= l.Grid;
            int eo = o + 48, xo = eo + l.In * 256, ao = xo + (int) clut;
            if (ao + (long) l.Out * 256 > o + (long) n) throw new InvalidDataException ();
            l.Elut = new ushort [l.In * 256 + 1];
            int x = (0x1000 << 16) - 0x1000;
            x = (int) ((long) x * unchecked ((int) 0x80808081) >> 32) + x;
            int step = (x >> 7) - (x >> 31);
            for (int c = 0; c < l.In; c++)
                for (int i = 0; i < 256; i++) l.Elut [c * 256 + i] = (ushort) (d [eo + c * 256 + i] * step + 0x800 >> 12);
            l.Xlut = new ushort [clut];
            for (int i = 0; i < clut; i++) l.Xlut [i] = d [xo + i];
            l.Alut = new ushort [l.Out * 0x400 + 1];
            for (int c = 0; c < l.Out; c++) {
                int t0 = ao + c * 256;
                for (int i = 0; i < 0x3ff; i++) {
                    int idx = i * 0x3fd >> 12, f = i * 0x3fd & 0xfff;
                    int v0 = d [t0 + idx];
                    int v = v0 * 0x101;
                    if (f != 0) v += (short) ((d [t0 + idx + 1] * 0x101 - v0 * 0x101) * f + 0x800 >> 12);
                    l.Alut [c * 0x400 + i] = (ushort) v;
                }
                l.Alut [c * 0x400 + 0x3ff] = (ushort) (d [t0 + 0xff] * 0x101);
            }
            return l;
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

        /// <summary>Calc324Dim_Data8To8_Lut16 @180030798, 16-bit ALUT, 16-bit in and out: three
        /// 16-bit channels per node of <paramref name="src"/> into four of <paramref name="dst"/>,
        /// through a CLUT of 16 or 8 bits, on a grid that is a power of two (index and fraction cut
        /// out of the descaled value) or not (the value times the grid).</summary>
        static void Calc324 (ushort[] src, int nodes, ushort[] dst, Lut16 l, ushort[] delut, int elutBits)
        {
            int g = l.Grid, xlutBits = l.XlutBits;
            const int alutBits = 16;
            int gb = 0;
            while (gb + 1 < 0x20 && g >> (gb + 1) != 0) gb++;
            bool pow2 = 1 << gb == g;
            int fb = pow2 ? elutBits - gb : elutBits;
            if (fb < 0) throw new NotSupportedException ();
            int sum = xlutBits + fb - 10;
            int ab = sum < 0x11 ? sum : 0x10, post = sum > 0x10 ? sum - 0x10 : 0;
            uint fmask = (uint) (1 << fb) - 1;
            uint imask = (uint) (g - 1) << fb;       // pow2: the index bits; a value at or past them is the last node
            int one = 1 << ab;
            int ash = ab - (16 - alutBits);
            var stride = new int [3];
            for (int k = 2, s = 4; k >= 0; k--, s *= g) stride [k] = s;
            uint top = (uint) (g + 0xffff) & 0xffff;
            var fr = new int [3];
            var ix = new uint [3];
            var last = new bool [3];
            var acc = new uint [4];
            ushort[] x = l.Xlut, a = l.Alut;
            for (int nd = 0, p = 0, q = 0; nd < nodes; nd++, p += 3, q += 4) {
                for (int c = 0; c < 3; c++) {
                    uint v = (uint) src [p + c] - (uint) (src [p + c] >> 8);
                    uint i = v >> 8, f = v & 0xff;
                    uint e = (uint) delut [c * 256 + i] * (0x100 - f) + (uint) delut [c * 256 + i + 1] * f;
                    if (pow2) {
                        uint val = (ushort) (e >> 8);
                        fr [c] = (int) (fmask & (e >> 8));
                        ix [c] = (imask & (e >> 8)) >> fb;
                        last [c] = !(val < imask);
                    } else {
                        uint t = (e >> 8) * (uint) g;
                        fr [c] = (int) (fmask & t);
                        ix [c] = (ushort) (t >> fb);
                        last [c] = !(ix [c] < top);
                    }
                }
                int bse = stride [0] * (int) ix [0] + stride [1] * (int) ix [1] + stride [2] * (int) ix [2];
                int imax = fr [0] < fr [1] ? 1 : 0, imid = fr [1] <= fr [0] ? 1 : 0, imin = 2;
                if (fr [imid] < fr [2]) {
                    imin = imid; imid = 2;
                    if (fr [imax] < fr [2]) { imid = imax; imax = 2; }
                }
                uint w0 = (uint) ((1 << fb) - fr [imax]), w1 = (uint) (fr [imax] - fr [imid]), w2 = (uint) (fr [imid] - fr [imin]), w3 = (uint) fr [imin];
                int off = 0;
                if (!last [imax]) off = stride [imax];
                int n1 = bse + off;
                if (!last [imid]) off += stride [imid];
                int n2 = bse + off;
                if (!last [imin]) off += stride [imin];
                int n3 = bse + off;
                for (int k = 0; k < 4; k++)
                    acc [k] = x [bse + k] * w0 + x [n1 + k] * w1 + x [n2 + k] * w2 + x [n3 + k] * w3;
                for (int k = 0; k < 4; k++) {
                    uint u;
                    if (xlutBits < 16) {
                        u = (acc [k] >> ((xlutBits & 0xf) << 1)) + (acc [k] >> xlutBits) + acc [k];
                        u -= u >> 10;
                    } else {
                        u = (acc [k] >> xlutBits) + acc [k];
                        u = u - (u >> 10) >> post;
                    }
                    uint f = (uint) (one - 1) & u;
                    int i = (int) (u >> ab) + k * 0x400;
                    dst [q + k] = (ushort) ((uint) a [i] * (uint) (one - (int) f) + (uint) a [i + 1] * f >> ash);
                }
            }
        }

        // ---- curves as ELUTs (ExtractNestedCurves @1800123c0) ------------------------------------------

        /// <summary>Fill_ushort_ELUT_identical @180046e58: 2^inBits entries of outBits, a 14-bit
        /// fixed-point ramp.</summary>
        static void ElutIdentical (ushort[] dst, int o, int inBits, int outBits)
        {
            int n = 1 << inBits;
            int step = ((0x4000 << outBits) - 0x4000) / (n - 1);
            uint acc = 0x2000;
            for (int i = 0; i < n; i++) { dst [o + i] = (ushort) (acc >> 14); acc += (uint) step; }
        }

        /// <summary>The parametric curves gc_curveDefs (@18006b5a0) holds: type 0 is
        /// ParameterizedCurveGamma @1800476c0, 1..4 ParameterizedCurveType1 @1800478a0, Type2
        /// @180047900, Type3 @180047970, Type4 @1800374b0; each clamped to [0, 1].</summary>
        static double CurveFunc (int type, double[] p, double x)
        {
            double v;
            switch (type) {
            case 0:
                v = Math.Pow (x, p [0]);
                break;
            case 1:
                if (x < -p [2] / p [1]) return 0.0;
                v = Math.Pow (p [1] * x + p [2], p [0]);
                break;
            case 2:
                v = p [3];
                if (-p [2] / p [1] <= x) v = Math.Pow (p [1] * x + p [2], p [0]) + v;
                break;
            case 3:
                v = x < p [4] ? p [3] * x : Math.Pow (p [1] * x + p [2], p [0]);
                break;
            default:
                v = x < p [4] ? p [3] * x + p [6] : Math.Pow (p [1] * x + p [2], p [0]) + p [5];
                break;
            }
            if (!(0.0 <= v)) return 0.0;
            return v <= 1.0 ? v : 1.0;
        }

        /// <summary>Fill_ushort_ELUT_from_ParameterizedCurveFunc @18000b198: the function at every
        /// 2^(inBits-6)th entry (x = i * (1 / (n - 1)), times 2^outBits - 1, truncated) and at the last,
        /// the entries between filled linearly by unsigned 8-bit-fraction steps, and -- when the first
        /// parameter is below 1 -- the first 2^(inBits-6) entries evaluated exactly.</summary>
        static void ElutFromFunc (ushort[] dst, int o, int inBits, int outBits, int type, double[] p)
        {
            uint n = 1u << inBits;
            double inv = 1.0 / (double) (n - 1);
            double max = (double) ((1 << outBits) - 1);
            uint s = inBits > 6 ? 1u << (inBits - 6) : 1u;
            ushort Eval (uint i) => (ushort) (uint) (CurveFunc (type, p, (double) i * inv) * max);
            dst [o] = 0;
            for (uint i = 0; i < n - 1; i += s) dst [o + (int) i] = Eval (i);
            dst [o + (int) n - 1] = (ushort) (uint) (CurveFunc (type, p, 1.0) * max);
            for (uint i = 0; i < n - s; i += s) {
                uint v0 = dst [o + (int) i];
                uint d = (uint) dst [o + (int) (i + s)] - v0, acc = d;
                for (uint k = 1; k < s; k++) {
                    dst [o + (int) (i + k)] = (ushort) (v0 + ((acc << 8) / s + 0x80 >> 8));
                    acc += d;
                }
            }
            {
                uint i = n - s, v0 = dst [o + (int) i];
                uint d = (uint) dst [o + (int) n - 1] - v0, acc = d;
                for (uint k = 1; k < s - 1; k++) {
                    dst [o + (int) (i + k)] = (ushort) (v0 + ((acc << 8) / (s - 1) + 0x80 >> 8));
                    acc += d;
                }
            }
            if (inBits > 6 && p [0] < 1.0)
                for (uint j = 0; j < s; j++) dst [o + (int) j] = Eval (j);
        }

        /// <summary>The parameter counts gc_curveDefs gives types 0..4.</summary>
        static readonly int[] s_paraCount = { 1, 3, 4, 5, 7 };

        /// <summary>ExtractNestedCurves @1800123c0: <paramref name="count"/> 'curv' or 'para' tags
        /// from <paramref name="off"/> (each 4-byte aligned after the last), as ELUTs of 2^inBits entries
        /// of outBits (one zero after the last) -- Fill_ushort_ELUT_from_CurveTag @18000afe0 for a
        /// curv (0 entries: the identity, 1: a gamma of u8Fixed8 / 256, @18000b180; more: the table
        /// resampled), Fill_ushort_ELUT_from_ParametricCurveTag @180015970 for a para (its parameters
        /// s15Fixed16 / 65536, @180016160, SetUpAndValidateCurveParameters @180016098 refusing a type past
        /// 4, a zero gamma, or for types 1..4 a zero a). outBits 8 is computed at 16 and cut to the high
        /// byte.</summary>
        static ushort[] NestedCurves (byte[] d, int tag, int size, int count, int off, int inBits, int outBits)
        {
            int n = 1 << inBits;
            var e = new ushort [count * n + 1];
            int ob = outBits == 8 ? 16 : outBits;
            for (int c = 0; c < count; c++) {
                if (off >= size || size - off < 8) throw new Refused ();
                uint sig = U32 (d, tag + off);
                int len;
                if (sig == SigCurv) {
                    if (size - off < 12) throw new Refused ();
                    uint cnt = U32 (d, tag + off + 8);
                    if (cnt > 0x7ffffffe) throw new Refused ();
                    len = (int) ((cnt + 6) * 2);
                    if (off + (long) len > size) throw new Refused ();
                    if (cnt == 0) ElutIdentical (e, c * n, inBits, ob);
                    else if (cnt == 1) ElutFromFunc (e, c * n, inBits, ob, 0, new[] { (double) U16 (d, tag + off + 12) * 0.00390625 });
                    else {
                        if (inBits < 1 || inBits > 15 || cnt >= 0x2000) throw new Refused ();
                        ElutFromCurve (d, tag + off, e, c * n, inBits, ob);
                    }
                } else if (sig == SigPara) {
                    if (size - off < 10) throw new Refused ();
                    int type = U16 (d, tag + off + 8);
                    if (type >= 5) throw new Refused ();
                    len = (s_paraCount [type] + 3) * 4;
                    if (off + (long) len > size) throw new Refused ();
                    var p = new double [7];
                    for (int i = 0; i < s_paraCount [type]; i++) p [i] = (double) S32 (d, tag + off + 12 + 4 * i) * 1.52587890625E-5;
                    if (p [0] == 0.0 || (type > 0 && p [1] == 0.0)) throw new Refused ();
                    ElutFromFunc (e, c * n, inBits, ob, type, p);
                } else throw new Refused ();
                off = off + len + 3 & ~3;
            }
            if (outBits == 8)
                for (int i = 0; i < e.Length; i++) e [i] = (ushort) (e [i] >> 8);
            return e;
        }

        // ---- a v4 lutBtoA ('mBA ', ExtractAll_MFT_LutsFromLutBToA @1800116c8) ---------------------------

        sealed class BtoA
        {
            public int In, Out, ClutBits;
            public ushort[] Elut, MCurves, Clut, Alut;
            public double[] Matrix;          // 9 then 3 offsets (ExtractMatrixWithOffsets @180012288)
            public byte[] Grid;              // per input dimension
        }

        /// <summary>The tag as icm32 extracts it: B curves as ELUTs of 256 x 16 bits, the matrix with
        /// its offsets (s15Fixed16 / 65536, @1800123b8) and the M curves (256 x 16) only together and
        /// only for three inputs, the CLUT (ExtractAtoBStyleClut @180011dc8: a grid per dimension, 8 or
        /// 16 bits as stored), the A curves as ALUTs of 1024 x 16 bits or, without them, linear ones
        /// (ExtractACurvesFromLutBToA @1800108d8). Without a CLUT the inputs must equal the
        /// outputs.</summary>
        static BtoA ReadBtoA (byte[] d, int o, int size)
        {
            if (size < 32) throw new Refused ();
            var l = new BtoA { In = d [o + 8], Out = d [o + 9] };
            if (l.In == 0 || l.Out == 0 || l.In > 8 || l.Out > 8) throw new Refused ();
            int ob = (int) U32 (d, o + 12), om = (int) U32 (d, o + 16), omc = (int) U32 (d, o + 20), oc = (int) U32 (d, o + 24), oa = (int) U32 (d, o + 28);
            if (ob == 0) throw new Refused ();
            l.Elut = NestedCurves (d, o, size, l.In, ob, 8, 16);
            if (om != 0) {
                if (omc == 0 || l.In != 3) throw new Refused ();
                if (om >= size || size - om < 0x30) throw new Refused ();
                l.Matrix = new double [12];
                for (int i = 0; i < 12; i++) l.Matrix [i] = (double) S32 (d, o + om + 4 * i) * 1.52587890625E-5;
            }
            if (omc != 0) {
                if (om == 0) throw new Refused ();
                l.MCurves = NestedCurves (d, o, size, l.In, omc, 8, 16);
            }
            if (oc == 0) {
                if (l.In != l.Out) throw new Refused ();
                throw new NotSupportedException ();      // a CLUT-less lutBtoA cannot make CMYK of Lab
            }
            if (oc >= size || size - oc < 0x14) throw new Refused ();
            l.Grid = new byte [l.In];
            for (int i = 0; i < 16; i++) {
                byte g = d [o + oc + i];
                if (i < l.In) { if (g == 0) throw new Refused (); l.Grid [i] = g; }
                else if (g != 0) throw new Refused ();
            }
            uint total = 1;
            for (int i = 0; i < l.In; i++) {
                if (0xffffffffu / l.Grid [i] <= total) throw new Refused ();
                total *= l.Grid [i];
            }
            if (!(total < 0xffffffffu / (uint) l.Out)) throw new Refused ();
            int prec = d [o + oc + 16];
            if (prec != 1 && prec != 2) throw new Refused ();
            long bytes = (long) total * l.Out * prec;
            if (oc + 0x14 + bytes > size) throw new Refused ();
            l.ClutBits = prec * 8;
            l.Clut = new ushort [total * (uint) l.Out];
            int co = o + oc + 0x14;
            for (int i = 0; i < l.Clut.Length; i++) l.Clut [i] = prec == 2 ? U16 (d, co + 2 * i) : d [co + i];
            if (oa == 0) {
                l.Alut = new ushort [l.Out * 0x400 + 1];
                for (int c = 0; c < l.Out; c++) LinearAlut16 (l.Alut, c * 0x400);
            } else l.Alut = NestedCurves (d, o, size, l.Out, oa, 10, 16);
            return l;
        }

        /// <summary>ADJUST_LAB_TO_V4 (CreateCombi @18000a020): a cube of legacy Lab into the v4
        /// encoding a lutBtoA reads -- each channel clamped to 0xff00, plus its high byte.</summary>
        static void LabToV4 (ushort[] c, int count)
        {
            for (int i = 0; i < count; i++) {
                uint v = c [i] < 0xff01 ? c [i] : 0xff00u;
                c [i] = (ushort) (v + (v >> 8));
            }
        }

        /// <summary>CalcNDim_Data8To8_Lut16 @18002be60, the general path a lutBtoA takes (16-bit nodes
        /// in and out): per node the B-curve ELUT (8 bits of index, 8 of fraction), the matrix and M
        /// curves, then simplex interpolation of the CLUT (the fractions sorted largest first, each
        /// vertex stepped unless the index is the last), then <paramref name="alut"/>. A grid that is a
        /// power of two in every dimension indexes by bit fields (value * (g - 1) / g); any other by
        /// value * (g - 1) with 16 fraction bits.</summary>
        static void CalcNDim (ushort[] src, int nodes, ushort[] dst, BtoA l, ushort[] alut)
        {
            int nIn = l.In, nOut = l.Out;
            const int elutBits = 16, elutLog = 8, eShift = 16 - elutLog;    // param_2[1] = 0x100, [2] = 16
            const int one16 = 1 << eShift;
            const int iv13 = 1 << elutBits;
            const uint u14 = iv13 - 1;
            int u50 = elutBits;
            int mShift = 0, mMode = 0, mOne = 0;
            if (l.MCurves != null) {
                // M curves of 0x100 entries x 16 bits (DAT_180011a38)
                const int mLog = 8;
                u50 = 16;
                if (mLog < elutBits) { mShift = elutBits - mLog; mOne = 1 << mShift; mMode = 2; }
                else { mShift = mLog - elutBits; mMode = 1; }
            }
            int clutBits = l.ClutBits;
            int u40 = clutBits + u50;                          // the accumulator's bits
            const int aLog = 10, aBits = 16;                   // the linear ALUT: 0x400 x 16 bits
            int aMode, aShift, aPost = 0;
            if (aLog < u40) {
                aShift = u40 - aLog; aMode = 2;
                if (aShift > 16) { aPost = aShift - 16; aShift = 16; }
            } else { aMode = 1; aShift = aLog - u40; }
            int aOne = 1 << aShift;
            int outShift = 16 - aBits;
            int one = 1 << u50;
            var g = new int [nIn];
            var lg = new int [nIn];
            bool pow2 = true;
            for (int i = 0; i < nIn; i++) {
                g [i] = l.Grid [i];
                int b = 1;
                lg [i] = 0;
                for (; b < 0x20 && g [i] >> b != 0; b++) lg [i] = b;
                if (g [i] != 1 << lg [i]) pow2 = false;
            }
            var stride = new int [nIn];
            var fbits = new int [nIn];
            var fmask = new uint [nIn];
            var imask = new uint [nIn];
            var pos = new int [nIn];
            if (!pow2) {
                for (int k = 0, s = nOut; k < nIn; k++) { stride [nIn - 1 - k] = s; s *= g [nIn - 1 - k]; }
            } else {
                for (int i = 0; i < nIn; i++) {
                    if (u50 < lg [i]) throw new Refused ();
                    fbits [i] = u50 - lg [i];
                    fmask [i] = (1u << fbits [i]) - 1;
                    imask [i] = (uint) ((1 << lg [i]) - 1) << fbits [i];
                }
                for (int k = 0, acc = 0; k < nIn; k++) { pos [nIn - 1 - k] = acc; acc += lg [nIn - 1 - k]; }
            }
            var e = new uint [nIn];
            var v = new uint [nIn];
            var fr = new int [nIn];
            var ix = new uint [nIn];
            var order = new int [nIn];
            var acc4 = new uint [nOut];
            ushort[] x = l.Clut;
            for (int nd = 0, p = 0, q = 0; nd < nodes; nd++, p += nIn, q += nOut) {
                for (int c = 0; c < nIn; c++) {
                    uint u = (uint) src [p + c] - (uint) (src [p + c] >> elutLog);
                    uint f = (uint) (one16 - 1) & u;
                    int i = (int) (u >> eShift) + c * 0x100;
                    e [c] = (uint) l.Elut [i] * (uint) (one16 - (int) f) + (uint) l.Elut [i + 1] * f >> eShift;
                }
                if (l.MCurves != null) {
                    double[] m = l.Matrix;
                    var t = new uint [3];
                    for (int r = 0; r < 3; r++) {
                        double s = m [9 + r];
                        for (int c = 0; c < 3; c++) s = ((double) e [c] * m [r * 3 + c]) / (double) u14 + s;
                        s = (double) u14 * s;
                        uint w = 0;
                        if (0.0 < s) { w = (uint) (s + 0.5); if (w > u14) w = u14; }
                        t [r] = w;
                    }
                    for (int c = 0; c < 3; c++) {
                        if (mMode == 1) v [c] = l.MCurves [(int) ((t [c] << mShift) + (uint) (c * 0x100))];
                        else {
                            uint u = t [c] - (t [c] >> 8);
                            uint f = (uint) (mOne - 1) & u;
                            int i = (int) (u >> mShift) + c * 0x100;
                            v [c] = (uint) l.MCurves [i] * (uint) (mOne - (int) f) + (uint) l.MCurves [i + 1] * f >> mShift;
                        }
                    }
                } else for (int c = 0; c < nIn; c++) v [c] = e [c];
                int bse = 0;
                uint packed = 0;
                for (int c = 0; c < nIn; c++) {
                    uint t = unchecked ((uint) ((int) v [c] * iv13)) / u14;
                    if (!pow2) {
                        t *= (uint) (g [c] - 1);
                        fr [c] = (int) (t & (uint) (one - 1));
                        ix [c] = (ushort) (t >> u50);
                        bse += stride [c] * (int) (t >> u50);
                    } else {
                        uint w = t * (uint) (g [c] - 1) / (uint) g [c];
                        ix [c] = (ushort) w;
                        fr [c] = (int) ((fmask [c] & w) << (u50 - fbits [c]));
                        packed |= ((imask [c] & w) >> fbits [c]) << pos [c];
                    }
                    order [c] = c;
                }
                if (pow2) bse = nOut * (int) packed;
                // the comb sort the code runs (gap * 3 / 4), largest fraction first
                for (int gap = nIn; ;) {
                    gap = (int) ((uint) (gap * 3) >> 2 & 0xfffffff);
                    if (gap == 0) gap = 1;
                    bool swapped = false;
                    if (nIn != gap)
                        for (int k = 0; k < nIn - gap; k++)
                            if (fr [order [k]] < fr [order [k + gap]]) { (order [k], order [k + gap]) = (order [k + gap], order [k]); swapped = true; }
                    if (swapped) continue;
                    if (gap <= 1) break;
                }
                for (int k = 0; k < nOut; k++) acc4 [k] = 0;
                int prevW = one, step = 0, vtx = bse;
                uint bits = 0;
                for (int k = 0; k < nIn; k++) {
                    int c = order [k];
                    int f = fr [c];
                    if (!pow2) {
                        if (ix [c] < (uint) (g [c] + 0xffff & 0xffff)) step += stride [c];
                    } else if (ix [c] < imask [c]) bits |= 1u << pos [c];
                    for (int j = 0; j < nOut; j++) acc4 [j] += (uint) x [vtx + j] * (uint) (prevW - f);
                    vtx = pow2 ? bse + (int) bits * nOut : bse + step;
                    prevW = f;
                }
                for (int j = 0; j < nOut; j++) acc4 [j] += (uint) x [vtx + j] * (uint) prevW;
                for (int j = 0; j < nOut; j++) {
                    uint r, u = acc4 [j];
                    if (aMode == 1) r = alut [(int) ((u << aShift) + (uint) (j * 0x400))];
                    else {
                        u = (u >> clutBits) + u;
                        u = u - (u >> aLog) >> aPost;
                        uint f = (uint) (aOne - 1) & u;
                        int i = (int) (u >> aShift) + j * 0x400;
                        r = (uint) alut [i + 1] * f + (uint) alut [i] * (uint) (aOne - (int) f) >> aShift;
                    }
                    dst [q + j] = (ushort) (r << outShift);
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
            if (pd.Class != 0x70727472) throw new NotSupportedException ();
            ValidateOutput (pd);
            bool lab = pd.Pcs == 0x4c616220;
            // ExtractAllLuts @180010a38 takes an output profile's B2A0 for the perceptual intent, and
            // ExtractAll_MFT_Luts @180010f48 reads it by its type: mft1, mft2, or (a v4 profile only)
            // mBA; anything else is ERROR_INVALID_PROFILE
            (int bo, int bn) = pd.Tag (0x42324130);
            if (bo < 0 || bn < 8) throw new Refused ();
            uint type = U32 (pd.D, bo);
            bool v4 = 0x3ffffff < pd.Version;
            Lut16 l = null;
            BtoA m = null;
            if (type == SigMft1 || type == SigMft2) {
                l = ReadLut (pd, 0x42324130);
                if (l.In != 3) throw new Refused ();
                if (l.Out != 4) throw new NotSupportedException ();
            } else if (type == 0x6d424120 && v4) {
                m = ReadBtoA (pd.D, bo, bn);
                if (m.In != 3) throw new Refused ();
                if (m.Out != 4) throw new NotSupportedException ();
            } else throw new Refused ();
            ushort[] elut = TrcElut (ps);
            double[] mtx = TrcMatrix (ps);
            var lin = new ushort [4 * 0x400 + 1];
            for (int c = 0; c < 4; c++) LinearAlut16 (lin, c * 0x400);
            // Create_LH_ProfileSet @18000a948 puts a PCS-conversion entry (no profile) between the two
            // only when the output profile's PCS is Lab. Without one (an XYZ PCS) CreateCombi
            // (@1800085xx, local_400 bit 0 clear) hands the source's TRC ELUT to the combi as its input
            // ELUT and runs the cube through the matrix on a linear ELUT instead (the formula of
            // CreateLinearElut16 @1800372a0); with one, the TRC stays in the cube and the combi's input
            // ELUT is the linear one.
            var ein = new ushort [3 * 256];
            for (int c = 0; c < 3; c++) LinearElut16 (ein, c * 256, 256);
            ushort[] combiIn = ein;
            if (!lab) { combiIn = elut; elut = ein; }
            ushort[] cube = MakeCube3 (GridBits);
            DoMatrixForCube16 (cube, Nodes, elut, 256, true, 16, lin, 0x400, true, 16, mtx);
            // the conversion entry: XYZ2Lab_forCube16
            if (lab) Xyz2Lab (cube, Nodes);
            // ExtractAllLuts marks a v4 profile read for the perceptual (or saturation) intent as
            // version 2 (+0x20 = 2), the TRC source stays 1; a cube crossing from one to the other gets
            // the v4 perceptual black point (CreateCombi @18000930c), by way of XYZ when it is Lab
            if (v4) {
                if (lab) Lab2Xyz (cube, Nodes);
                BlackPointToV4 (cube, Nodes);
                if (lab) Xyz2Lab (cube, Nodes);
            }
            var grid = new ushort [Nodes * 4 + 0x1100];
            ushort[] output;
            if (l != null) {
                if (l.Matrix != null) ApplyLutMatrix (cube, Nodes, l.Matrix);
                var calc = new Lut16 { In = l.In, Out = l.Out, Grid = l.Grid, XlutBits = l.XlutBits, Xlut = l.Xlut, Alut = lin };
                Calc324 (cube, Nodes, grid, calc, Descale (l.Elut, 3 * 256, 16, l.Grid), 16);
                output = l.Alut;
            } else {
                // a lutBtoA reads Lab in the v4 encoding (local_404 = 2 against the cube's 1)
                if (lab) LabToV4 (cube, Nodes * 3);
                CalcNDim (cube, Nodes, grid, m, lin);
                output = m.Alut;
            }
            return new GpIcm (Descale (combiIn, 3 * 256, 16, Grid), grid, output, 4);
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
        static readonly System.Collections.Generic.Dictionary<string, (long, long, GpIcm, int)> s_cache = new System.Collections.Generic.Dictionary<string, (long, long, GpIcm, int)> ();

        /// <summary>The separation transform sRGB -> <paramref name="profile"/>, with GDI+'s status:
        /// 0, 2 (InvalidParameter: no ICM -- LoadICMDll's failure -- which is what a system without
        /// the standard sRGB profile is), or 3 (OutOfMemory: the profile does not open, or is not
        /// one), or 7 (Win32Error: mscms refuses the transform -- the profile fails validation or its
        /// B2A0 cannot be read -- GpICMHolder::Init's E_FAIL, @1800fcfb0, which
        /// SetOutputChannelProfile @18008f138 maps to 7; the 0x100 flag stays clear and the channel
        /// separates through rswop.icm). A profile of a kind this CMM does not build yet comes back as
        /// status 0 and no transform: the channel then separates by mapping.</summary>
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
                    if (s_cache.TryGetValue (path, out (long, long, GpIcm, int) hit) && hit.Item1 == s1 && hit.Item2 == s2) { status = hit.Item4; return hit.Item3; }
                }
                byte[] dst = File.ReadAllBytes (path);
                new Profile (dst);
                GpIcm t;
                int st = 0;
                try { t = Create (File.ReadAllBytes (srgb), dst); }
                catch (NotSupportedException) { t = null; }
                catch (Refused) { t = null; st = 7; }
                lock (s_lock) s_cache [path] = (s1, s2, t, st);
                status = st;
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
                Lut16 l = ReadLut (new Profile (dst), 0x42324130);
                r.SElut = l.Elut; r.SAlut = l.Alut; r.SXlut = l.Xlut;
                r.SDelut = Descale (l.Elut, 3 * 256, 16, l.Grid);
                r.Grid = new ushort [0x8000 * 4];
                Calc324 (cube, 0x8000, r.Grid, l, r.SDelut, 16);
                return r;
            }
        }
    }
}
