// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s character tables (GpTextTables.Data.cs, generated from gdiplus.dll) and the lookups it
// makes through them:
//
//   CharClassFromCh @18004a038   a BMP character's class through pccUnicodeClass's 256 pages; above
//       the BMP 0x101 for 0x20000..0x3FFFF (the CJK extensions), 0x100 otherwise
//   CharacterAttributes          per class: the ItemScript (byte 0), the itemizer's state class
//       (byte 1), and the flags (bits 16..): 0x80 the string needs the full imager, 0x100 a digit
//   BreakClassFromCharClass{Narrow,Wide}   the Line Services breaking class (FullTextImager's
//       +0x280; Wide when the face says IDWriteGdiPlusFontFace::IsWide)
//   LineBreakBehavior / the break conditions   whether Line Services may break between two classes
//       directly, and whether across spaces (TryPrevBreakRegular @18011d338)
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpTextTables
    {
        public const int ScriptLatin = 1, ScriptControl = 0x2c;

        /// <summary>CharClassFromCh.</summary>
        public static int CharClass (int ch)
        {
            if (ch < 0x10000) {
                int p = ClassIndex [ch >> 8];
                return p < 0 ? -1 - p : ClassPages [p * 256 + (ch & 0xff)];
            }
            if (ch >= 0x20000 && ch < 0x40000) return 0x101;
            return 0x100;
        }

        public static uint Attributes (int ch) => CharacterAttributes [CharClass (ch)];

        /// <summary>The ItemScript of a character's class.</summary>
        public static int Script (int ch) => (int) (Attributes (ch) & 0xff);

        /// <summary>The itemizer's state class of a character (ItemizationFiniteStateMachine).</summary>
        public static int StateClass (int ch) => (int) ((Attributes (ch) >> 8) & 0xff);

        /// <summary>The flags (CharacterAttributes bits 16..): 0x80 full imager, 0x100 digit.</summary>
        public static int Flags (int ch) => (int) (Attributes (ch) >> 16);

        public static int BreakClass (int ch, bool wide) => (wide ? BreakClassWide : BreakClassNarrow) [CharClass (ch)];

        /// <summary>LineBreakBehavior's condition between a character of class <paramref name="before"/>
        /// and one of class <paramref name="after"/>: whether a break may come between them.</summary>
        public static bool CanBreakDirect (int before, int after) => BreakConditions [LineBreakBehavior [before * 23 + after] * 2] != 0;

        /// <summary>The same condition: whether a break may come across spaces between them.</summary>
        public static bool CanBreakAcrossSpaces (int before, int after) => BreakConditions [LineBreakBehavior [before * 23 + after] * 2 + 1] != 0;
    }
}
