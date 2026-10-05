// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// GENERATED from gdiplus.dll 10.0.26100 (arm64) by scratchpad fti/py/gendir.py: the bidi class of
// each character class, the table UnicodeBidiAnalyze @1800f3060 reads after CharClassFromCh
// (@1802c2070, one int per class; DirectionClass's numbering: 0 L, 1 R, 2 AN, 3 EN, 4 AL, 5 ES,
// 6 CS, 7 ET, 8 NSM, 9 BN, 11 B, 12 LRE, 13 LRO, 14 RLE, 15 RLO, 16 PDF, 17 S, 18 WS, 19 ON).

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpTextTables
    {
        static readonly byte[] DirClassOfCharClass = {
            19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 6, 19, 19, 6, 19, 6, 0, 19, 0, 19, 19,
            0, 0, 0, 0, 8, 0, 19, 19, 19, 19, 19, 19, 6, 19, 19, 19, 6, 6, 19, 19, 6, 0, 19, 19, 6, 6, 19, 19, 18, 9, 8, 7,
            19, 7, 7, 7, 19, 7, 7, 7, 7, 19, 7, 19, 0, 7, 18, 18, 0, 0, 0, 19, 7, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            19, 19, 8, 0, 5, 7, 3, 7, 19, 0, 3, 3, 3, 0, 0, 18, 9, 0, 9, 1, 0, 19, 19, 0, 8, 19, 5, 19, 19, 19, 19, 6,
            17, 11, 18, 0, 0, 8, 19, 19, 19, 0, 19, 0, 19, 0, 0, 8, 0, 0, 19, 1, 8, 7, 1, 6, 4, 8, 2, 3, 19, 7, 4, 4,
            4, 0, 0, 8, 0, 0, 8, 0, 7, 0, 8, 0, 0, 8, 0, 0, 8, 0, 0, 8, 0, 0, 8, 0, 0, 8, 0, 0, 8, 0, 0, 8,
            0, 7, 0, 8, 0, 0, 8, 0, 19, 0, 0, 0, 6, 18, 17, 11, 7, 19, 19, 5, 19, 7, 0, 8, 3, 9, 9, 12, 14, 16, 13, 15,
            0, 0, 0, 0, 0, 0, 19, 19, 0, 0, 18, 19, 0, 8, 4, 8, 0, 8, 0, 7, 0, 8, 0, 4, 4, 8, 9, 0, 8, 0, 9, 19,
            0, 0, 0, 0, 4, 9,
        };

        /// <summary>The character's bidi class (UnicodeBidiAnalyze's table).</summary>
        public static int DirClass (int ch) => DirClassOfCharClass [CharClass (ch)];
    }
}
