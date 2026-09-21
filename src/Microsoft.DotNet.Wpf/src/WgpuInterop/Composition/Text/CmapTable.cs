// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A character-to-glyph map (sfnt 'cmap'), shared by the TrueType (glyf) and CFF
// font readers. Supports the common subtable formats: 4 (segment-mapped BMP),
// 12 (segmented full Unicode), 6 (trimmed array) and 0 (byte table). The best
// subtable is chosen once; lookups are lazy and cached so format 12's full
// Unicode range needs no up-front prefill.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal sealed class CmapTable
    {
        private readonly byte[] _data;
        private readonly int _subtable;   // file offset of the chosen subtable (0 = none)
        private readonly int _format;     // 4 / 12 / 6 / 0, or -1 if none usable
        private int _f4SegCount, _f4EndCodes, _f4StartCodes, _f4IdDeltas, _f4IdRangeOffsets;
        private readonly Dictionary<int, int> _cache = new();   // codepoint -> glyph id

        public CmapTable(byte[] data, int cmapOffset)
        {
            _data = data;
            (_subtable, _format) = SelectSubtable(cmapOffset);
            if (_format == 4) PrepareFormat4(_subtable);
        }

        /// <summary>Maps a Unicode codepoint to a glyph id (0 = unmapped).</summary>
        public int Map(int codepoint)
        {
            if (_cache.TryGetValue(codepoint, out int gid)) return gid;
            gid = _subtable == 0 ? 0 : _format switch
            {
                4 => MapFormat4(codepoint),
                12 => MapFormat12(codepoint),
                6 => MapFormat6(codepoint),
                0 => MapFormat0(codepoint),
                _ => 0,
            };
            _cache[codepoint] = gid;
            return gid;
        }

        // Prefer a full-Unicode format 12, then a BMP format 4, then legacy 6/0;
        // prefer Windows/Unicode platforms over the (3,0) symbol encoding.
        private (int offset, int format) SelectSubtable(int cmap)
        {
            int numTables = U16(cmap + 2);
            int best = 0, bestFormat = -1, bestScore = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = cmap + 4 + i * 8;
                int platform = U16(rec);
                int encoding = U16(rec + 2);
                int sub = cmap + (int)U32(rec + 4);
                int format = U16(sub);
                if (format is not (4 or 12 or 6 or 0)) continue;

                int formatScore = format == 12 ? 200 : format == 4 ? 100 : format == 6 ? 50 : 10;
                int platformScore = (platform, encoding) switch
                {
                    (3, 10) => 9, (0, 4) => 9, (0, 5) => 9, (0, 6) => 9,  // full Unicode
                    (3, 1) => 8, (0, _) => 7,                            // BMP Unicode
                    (3, 0) => 1,                                          // symbol
                    _ => 0,
                };
                int score = formatScore + platformScore;
                if (score > bestScore) { bestScore = score; best = sub; bestFormat = format; }
            }
            return bestFormat < 0 ? (0, -1) : (best, bestFormat);
        }

        private void PrepareFormat4(int sub)
        {
            _f4SegCount = U16(sub + 6) / 2;
            _f4EndCodes = sub + 14;
            _f4StartCodes = _f4EndCodes + _f4SegCount * 2 + 2; // + reservedPad
            _f4IdDeltas = _f4StartCodes + _f4SegCount * 2;
            _f4IdRangeOffsets = _f4IdDeltas + _f4SegCount * 2;
        }

        private int MapFormat4(int c)
        {
            if (c > 0xFFFF) return 0; // format 4 is BMP-only
            for (int i = 0; i < _f4SegCount; i++)
            {
                if (c > U16(_f4EndCodes + i * 2)) continue;
                int start = U16(_f4StartCodes + i * 2);
                if (c < start) return 0;

                int idDelta = (short)U16(_f4IdDeltas + i * 2);
                int idRangeOffset = U16(_f4IdRangeOffsets + i * 2);
                if (idRangeOffset == 0)
                    return (c + idDelta) & 0xFFFF;

                // idRangeOffset is relative to its own slot.
                int glyphIndexAddr = _f4IdRangeOffsets + i * 2 + idRangeOffset + (c - start) * 2;
                int gid = U16(glyphIndexAddr);
                return gid == 0 ? 0 : (gid + idDelta) & 0xFFFF;
            }
            return 0;
        }

        // Format 12: segmented coverage, full Unicode; binary-searches the groups.
        private int MapFormat12(int c)
        {
            int nGroups = (int)U32(_subtable + 12);
            int groups = _subtable + 16;
            int lo = 0, hi = nGroups - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int g = groups + mid * 12;
                uint startChar = U32(g);
                uint endChar = U32(g + 4);
                if ((uint)c < startChar) hi = mid - 1;
                else if ((uint)c > endChar) lo = mid + 1;
                else return (int)(U32(g + 8) + ((uint)c - startChar));
            }
            return 0;
        }

        // Format 6: a single contiguous range of code points (BMP).
        private int MapFormat6(int c)
        {
            int first = U16(_subtable + 6);
            int count = U16(_subtable + 8);
            if (c < first || c >= first + count) return 0;
            return U16(_subtable + 10 + (c - first) * 2);
        }

        // Format 0: byte-encoding table (256-entry glyph id array).
        private int MapFormat0(int c)
            => (c is >= 0 and < 256) ? _data[_subtable + 6 + c] : 0;

        private int U16(int o) => (_data[o] << 8) | _data[o + 1];
        private uint U32(int o)
            => ((uint)_data[o] << 24) | ((uint)_data[o + 1] << 16) | ((uint)_data[o + 2] << 8) | _data[o + 3];
    }
}
