// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed replacement for the native Unicode classification tables (PresentationNative
// MILGetClassificationTables). WPF's text code asks Classification for each character's class
// and that class's CharacterAttribute (Script / ItemClass / Flags / BreakType / BiDi / LineBreak).
//
// The data is the native table itself (ManagedClassificationData.cs, generated): the same 0x1D8
// classes with the same attributes, and the same code point -> class mapping. It used to be a
// coarse range classifier with fifteen made-up templates, and the difference was not cosmetic:
// those templates never set CharacterFastText, so Typeface.CheckFastPathNominalGlyphs could not
// pass a single run, and every face without required Latin typography (Tahoma, Consolas, ...)
// took the shaping path where stock WPF draws nominal glyphs; they filed ASCII punctuation under
// no script and Arabic letters under bidi R instead of AL; tabs and NBSP were not complex.
//

namespace MS.Internal
{
    internal static partial class ManagedClassification
    {
        private const int ClassCount = 0x1D8;   // UnicodeClass.Max

        private static readonly CharacterAttribute[] s_attributes = BuildAttributes();

        // Code point -> class for the BMP, expanded once from the ranges (the UTF-16 lookup is on
        // every character the text formatter scans).
        private static readonly ushort[] s_bmp = BuildBmp();

        private static CharacterAttribute[] BuildAttributes()
        {
            System.ReadOnlySpan<byte> raw = ClassAttributes;
            var attributes = new CharacterAttribute[ClassCount];
            for (int c = 0; c < ClassCount; c++)
            {
                int o = c * 8;
                attributes[c] = new CharacterAttribute
                {
                    Script = raw[o],
                    ItemClass = raw[o + 1],
                    Flags = (ushort)(raw[o + 2] | (raw[o + 3] << 8)),
                    BreakType = raw[o + 4],
                    BiDi = (DirectionClass)raw[o + 5],
                    LineBreak = (short)(raw[o + 6] | (raw[o + 7] << 8)),
                };
            }
            return attributes;
        }

        private static ushort[] BuildBmp()
        {
            System.ReadOnlySpan<int> starts = RangeStarts;
            System.ReadOnlySpan<ushort> classes = RangeClasses;
            var bmp = new ushort[0x10000];
            for (int r = 0; r < starts.Length && starts[r] < 0x10000; r++)
            {
                int end = r + 1 < starts.Length ? System.Math.Min(starts[r + 1], 0x10000) : 0x10000;
                System.MemoryExtensions.AsSpan(bmp, starts[r], end - starts[r]).Fill(classes[r]);
            }
            return bmp;
        }

        internal static CharacterAttribute Attr(int charClass)
        {
            Invariant.Assert(charClass >= 0 && charClass < ClassCount);
            return s_attributes[charClass];
        }

        internal static short GetClass(int cp)
        {
            if ((uint)cp < 0x10000)
            {
                return (short)s_bmp[cp];
            }

            System.ReadOnlySpan<int> starts = RangeStarts;
            int lo = 0, hi = starts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= cp) lo = mid; else hi = mid - 1;
            }
            return (short)RangeClasses[lo];
        }
    }
}
