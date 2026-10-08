// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DirectWrite composes a base letter and the combining diacritical marks after it into the
// precomposed character -- "a" U+0301 draws as the font's "á" -- when the font has one. It is not a
// font feature (Arial's ccmp has no such ligatures); TextShaping's generic engine does it while it
// maps characters to glyphs (GenericEngineGetGlyphs, 0x18000d610), from its own CDM tables
// (CdmCompositionData.cs) and a four-state machine (0x180126850):
//
//   state 0  a base character with a row               -> state 1, else no composition
//   state 1  a CDM mark that composes with the base    -> state 2, else no composition
//   state 2  a CDM mark that composes with the result  -> state 3; any other character: compose
//            the two; a CDM mark that does NOT compose: no composition at all
//   state 3  as state 2, composing three
//   after a third mark that composes: compose all four
//
// So it is all or nothing: "a" U+0301 U+0302 has no precomposed form and stays three glyphs, not
// "á" and a circumflex. The composed character is used only when the font maps it to a glyph
// (GetDefaultGlyph); otherwise the characters keep their own glyphs.
//

namespace MS.Internal.Text.TextInterface
{
    internal static partial class CdmComposition
    {
        private const int Columns = 82;

        /// <summary>The CDM row of a base character, or 0 when it has none.</summary>
        private static int Row(char c)
        {
            int i = System.MemoryExtensions.BinarySearch(BaseChars, c);
            return i < 0 ? 0 : BaseRows[i];
        }

        /// <summary>The CDM column of a combining mark, or -1 when it is not one.</summary>
        private static int Column(char c)
        {
            if (c >= 0x0300 && c <= 0x034E) return c - 0x0300;
            if (c >= 0x0360 && c <= 0x0362) return 79 + (c - 0x0360);
            return -1;
        }

        private static char Compose(int row, int column)
        {
            if (row == 0) return '\0';
            int i = System.MemoryExtensions.BinarySearch(EntryKeys, (ushort)(row * Columns + column));
            return i < 0 ? '\0' : EntryValues[i];
        }

        /// <summary>
        ///  Whether the characters at <paramref name="start"/> compose, and if so into what and how
        ///  many of them it takes (2 to 4).
        /// </summary>
        internal static unsafe bool TryCompose(char* text, int start, int length, out char composed, out int consumed)
        {
            composed = '\0';
            consumed = 0;

            int row = Row(text[start]);
            if (row == 0) return false;

            char current = '\0';
            int marks = 0;
            for (int j = start + 1; ; j++)
            {
                int column = j < length ? Column(text[j]) : -1;
                if (column < 0)
                {
                    // The end of the text or a character that is not a CDM mark: what has
                    // composed so far stands (states 2 and 3), or nothing did (state 1).
                    break;
                }

                char next = Compose(marks == 0 ? row : Row(current), column);
                if (next == '\0')
                {
                    // A CDM mark that does not compose: the first one simply ends it; a later one
                    // undoes the composition altogether.
                    if (marks > 0) marks = 0;
                    break;
                }

                current = next;
                if (++marks == 3) break;
            }

            if (marks == 0) return false;
            composed = current;
            consumed = marks + 1;
            return true;
        }
    }
}
