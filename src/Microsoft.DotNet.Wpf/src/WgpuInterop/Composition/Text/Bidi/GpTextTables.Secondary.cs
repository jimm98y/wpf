// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// GENERATED from gdiplus.dll 10.0.26100 (arm64) by scratchpad a2/g10/gen.py -- do not edit.
//   SecondaryClassificationLookup @1802bb820, ScBaseToScFlags @1802bc020, ScFlagsToScFE @1802b9e20,
//   SecondaryItemization's action / next-state tables @1802b3430 / @1802b3500, the primary language's
//   digit script @1802b3a30 and each script's digits @1802b3c20 (GetDigitSubstitutionsScript @1800eec58).

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpTextTables
    {
        /// <summary>SecondaryClassificationLookup: the BMP ranges whose secondary class is not 9 (start, end, class).</summary>
        static readonly int[] SecondaryRanges = {
            0x0023, 0x0025, 5, 0x0028, 0x0029, 10, 0x002b, 0x002b, 5, 0x002c, 0x002c, 1, 0x002d, 0x002d, 5,
            0x002e, 0x002e, 1, 0x0030, 0x0039, 3, 0x003a, 0x003a, 1, 0x003c, 0x003c, 10, 0x003e, 0x003e, 10,
            0x005b, 0x005b, 10, 0x005d, 0x005d, 10, 0x007b, 0x007b, 10, 0x007d, 0x007d, 10, 0x00a0, 0x00a0, 1,
            0x00a2, 0x00a5, 5, 0x00ab, 0x00ab, 10, 0x00b0, 0x00b1, 5, 0x00b2, 0x00b3, 3, 0x00b9, 0x00b9, 3,
            0x00bb, 0x00bb, 10, 0x060c, 0x060c, 1, 0x066a, 0x066a, 5, 0x06f0, 0x06f9, 3, 0x09f2, 0x09f3, 5,
            0x0e3f, 0x0e3f, 5, 0x1100, 0x11ff, 6, 0x17db, 0x17db, 5, 0x2030, 0x2034, 5, 0x2039, 0x203a, 10,
            0x2045, 0x2046, 10, 0x2070, 0x2070, 3, 0x2074, 0x2079, 3, 0x207a, 0x207b, 5, 0x207d, 0x207e, 10,
            0x2080, 0x2089, 3, 0x208a, 0x208b, 5, 0x208d, 0x208e, 10, 0x20a0, 0x20b1, 5, 0x212e, 0x212e, 5,
            0x2201, 0x2204, 10, 0x2208, 0x220d, 10, 0x2211, 0x2211, 10, 0x2212, 0x2213, 5, 0x2215, 0x2216, 10,
            0x221a, 0x221d, 10, 0x221f, 0x2222, 10, 0x2224, 0x2224, 10, 0x2226, 0x2226, 10, 0x222b, 0x2233, 10,
            0x2239, 0x2239, 10, 0x223b, 0x224c, 10, 0x2252, 0x2255, 10, 0x225f, 0x2260, 10, 0x2262, 0x2262, 10,
            0x2264, 0x226b, 10, 0x226e, 0x228c, 10, 0x228f, 0x2292, 10, 0x2298, 0x2298, 10, 0x22a2, 0x22a3, 10,
            0x22a6, 0x22b8, 10, 0x22be, 0x22bf, 10, 0x22c9, 0x22cd, 10, 0x22d0, 0x22d1, 10, 0x22d6, 0x22ed, 10,
            0x22f0, 0x22f1, 10, 0x2308, 0x230b, 10, 0x2320, 0x2321, 10, 0x2329, 0x232a, 10, 0x2460, 0x249b, 2,
            0x249c, 0x24e9, 6, 0x24ea, 0x24ea, 2, 0x24eb, 0x24ff, 6, 0x25a0, 0x27ff, 6, 0x2e80, 0x2fdf, 6,
            0x3000, 0x3007, 6, 0x3008, 0x3011, 7, 0x3012, 0x3013, 6, 0x3014, 0x301b, 7, 0x301c, 0x31bf, 6,
            0x3200, 0xa4cf, 6, 0xac00, 0xd7af, 6, 0xd840, 0xd8bf, 6, 0xdb80, 0xdbff, 8, 0xe000, 0xf8ff, 8,
            0xf900, 0xfaff, 6, 0xfb29, 0xfb29, 5, 0xfe30, 0xfe4f, 6, 0xfe50, 0xfe50, 1, 0xfe52, 0xfe52, 1,
            0xfe55, 0xfe55, 1, 0xfe5f, 0xfe5f, 5, 0xfe62, 0xfe63, 5, 0xfe69, 0xfe6a, 5, 0xff00, 0xff02, 6,
            0xff03, 0xff05, 4, 0xff06, 0xff0a, 6, 0xff0b, 0xff0b, 4, 0xff0c, 0xff0c, 0, 0xff0d, 0xff0d, 4,
            0xff0e, 0xff0e, 0, 0xff0f, 0xff0f, 6, 0xff10, 0xff19, 2, 0xff1a, 0xff1a, 0, 0xff1b, 0xff5f, 6,
            0xffe0, 0xffe1, 4, 0xffe2, 0xffe4, 6, 0xffe5, 0xffe6, 4, 0xffe7, 0xffef, 6,
        };
        /// <summary>ScBaseToScFlags, per secondary class.</summary>
        static readonly byte[] ScBaseToScFlags = { 18, 2, 17, 1, 19, 3, 16, 20, 32, 0, 4 };
        /// <summary>ScFlagsToScFE: the state machine's column of a masked class flag.</summary>
        static readonly byte[] ScFlagsToScFE = { 0, 4, 5, 6, 2, 2, 2, 2, 3, 3, 3, 3, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        /// <summary>The secondary itemizer's action and next state, [column * 7 + state].</summary>
        static readonly byte[] SecondaryAction = { 0, 0, 4, 5, 6, 7, 8, 2, 2, 4, 5, 6, 7, 0, 2, 2, 4, 5, 0, 7, 8, 2, 2, 4, 5, 6, 0, 8, 2, 3, 0, 0, 6, 7, 8, 0, 0, 1, 5, 6, 7, 8, 1, 0, 0, 5, 6, 7, 8 };
        static readonly byte[] SecondaryNext = { 0, 0, 0, 0, 0, 0, 0, 6, 6, 6, 6, 6, 6, 6, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 2, 2, 2, 2, 2, 2, 2, 0, 0, 3, 0, 0, 0, 0, 1, 1, 2, 0, 0, 0, 0 };
        /// <summary>The ItemScript of a primary language's digits.</summary>
        static readonly byte[] LanguageDigitScript = { 2, 45, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 46, 2, 61, 2, 2, 2, 2, 2, 2, 2, 2, 62, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 63, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 49, 50, 51, 52, 48, 53, 54, 55, 49, 63, 63, 60, 56, 2, 58, 57, 2, 2, 63, 49, 50, 2, 2, 2, 2, 2, 45, 61, 63, 2, 61, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };
        /// <summary>The scripts with digits of their own (the per-script table's first digit).</summary>
        static readonly ushort[] ScriptZeroDigit = { 0, 0, 48, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1632, 3664, 2406, 48, 2534, 2662, 2790, 2918, 3174, 3302, 3430, 3872, 3792, 0, 0, 0, 1776, 1776, 2406 };
    }
}
