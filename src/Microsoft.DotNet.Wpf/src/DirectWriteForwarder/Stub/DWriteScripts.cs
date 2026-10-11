// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The script DirectWrite's AnalyzeScript gives a code point -- what stock WPF's TextItemizer cuts its
// items at. It is the Unicode Script property, not WPF's classification: Hiragana and Katakana are two
// scripts ("nihongo no tekisuto" is three items, Han, Hiragana, Katakana, and stock draws three glyph
// runs), fullwidth Latin is Latin, and the format and bidi controls have no visual of their own.
//

namespace MS.Internal.Text.TextInterface
{
    internal static partial class DWriteScripts
    {
        private const ushort NoVisualFlag = 0x8000;

        /// <summary>DirectWrite's script id for a code point (0: Common/Inherited), and whether it
        /// has no visual.</summary>
        internal static int Of(int scalar, out bool noVisual)
        {
            System.ReadOnlySpan<int> starts = RangeStarts;
            int lo = 0, hi = starts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= scalar) lo = mid; else hi = mid - 1;
            }
            ushort value = RangeScripts[lo];
            noVisual = (value & NoVisualFlag) != 0;
            return value & ~NoVisualFlag;
        }
    }
}
