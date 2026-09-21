// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed, design-unit OpenType/TrueType font parser backing the off-Windows
// MS.Internal.Text.TextInterface surface (Font/FontFace/FontFamily/FontCollection).
//
// WPF's font/text stack works in *design units* (font-unit values relative to
// head.unitsPerEm), unlike the renderer's pixel-scaled TrueTypeFont in the WebGPU
// engine. This parser reads exactly the tables PresentationCore needs to build a
// GlyphTypeface and shape simple runs: head/maxp/hhea/OS2/post for metrics,
// hmtx for advances/side-bearings, cmap for character->glyph mapping, name for
// family/face names, plus raw table access for TryGetFontTable. TrueType Collections
// (.ttc/.otc) are supported via a per-face sfnt offset.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MS.Internal.Text.TextInterface.Managed
{
    internal sealed class OpenTypeFontData
    {
        private readonly byte[] _data;
        private readonly int _sfntBase;
        private readonly Dictionary<string, (int off, int len)> _tables = new(StringComparer.Ordinal);

        // head
        public ushort UnitsPerEm { get; private set; }
        public short IndexToLocFormat { get; private set; }
        public ushort MacStyle { get; private set; }        // bit0=bold, bit1=italic
        public short XMin { get; private set; }
        public short YMin { get; private set; }
        public short XMax { get; private set; }
        public short YMax { get; private set; }
        public double FontRevision { get; private set; }

        // maxp
        public ushort NumGlyphs { get; private set; }

        // hhea
        public short Ascender { get; private set; }
        public short Descender { get; private set; }
        public short LineGap { get; private set; }
        public ushort NumberOfHMetrics { get; private set; }

        // OS/2 (may be absent on old fonts)
        public bool HasOS2 { get; private set; }
        public ushort UsWeightClass { get; private set; } = 400;
        public ushort UsWidthClass { get; private set; } = 5;
        public ushort FsSelection { get; private set; }
        public short STypoAscender { get; private set; }
        public short STypoDescender { get; private set; }
        public short STypoLineGap { get; private set; }
        public ushort UsWinAscent { get; private set; }
        public ushort UsWinDescent { get; private set; }
        public short SxHeight { get; private set; }
        public short SCapHeight { get; private set; }
        public short YStrikeoutPosition { get; private set; }
        public short YStrikeoutSize { get; private set; }
        public ushort FsType { get; private set; }
        public byte PanoseFamilyKind { get; private set; }

        // post
        public short UnderlinePosition { get; private set; } = -100;
        public short UnderlineThickness { get; private set; } = 50;
        public bool IsFixedPitch { get; private set; }

        // Derived
        public bool IsCff { get; private set; }
        public bool IsSymbolFont { get; private set; }

        private CmapReader _cmap;
        private ushort[] _advanceWidths;
        private short[] _leftSideBearings;

        // name-table strings we care about (English preferred)
        public string FamilyName { get; private set; }
        public string SubfamilyName { get; private set; }
        public string FullName { get; private set; }
        public string TypographicFamilyName { get; private set; }
        public string TypographicSubfamilyName { get; private set; }
        public string PostScriptName { get; private set; }

        public OpenTypeFontData(byte[] data, int sfntOffset)
        {
            _data = data;
            _sfntBase = sfntOffset;
            ReadTableDirectory();
            ParseHead();
            ParseMaxp();
            ParseHhea();
            ParseOS2();
            ParsePost();
            ParseHmtx();
            ParseCmap();
            ParseName();
            ParseOutlineTables();
        }

        // ---- big-endian helpers (absolute file offsets) ----
        private ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(o));
        private short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(o));
        private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(o));

        private void ReadTableDirectory()
        {
            uint sfntVersion = U32(_sfntBase);
            IsCff = sfntVersion == 0x4F54544F; // 'OTTO'
            int numTables = U16(_sfntBase + 4);
            int dir = _sfntBase + 12;
            for (int i = 0; i < numTables; i++)
            {
                int rec = dir + i * 16;
                string tag = Encoding.ASCII.GetString(_data, rec, 4);
                int off = (int)U32(rec + 8);
                int len = (int)U32(rec + 12);
                _tables[tag] = (off, len);
            }
        }

        public bool TryGetTable(string tag, out int offset, out int length)
        {
            if (_tables.TryGetValue(tag, out var t)) { offset = t.off; length = t.len; return true; }
            offset = 0; length = 0; return false;
        }

        // ---- glyph outlines ----
        //
        // The tables that describe glyph SHAPES, as opposed to the metrics and mappings everything
        // above parses. Read lazily and kept as positions: 'glyf' of a CJK font is megabytes, and
        // most of a font's glyphs are never drawn.

        /// <summary>The whole font file. Outline readers index into it rather than copying tables out.</summary>
        public byte[] Raw => _data;

        /// <summary>Whether this font has quadratic outlines. False for CFF and for bitmap-only faces.</summary>
        public bool HasGlyfOutlines => _glyfOffset >= 0 && _locaOffset >= 0;

        private int _glyfOffset = -1;
        private int _locaOffset = -1;
        private int _locaLength;

        private CffOutlines.CffTable _cff;
        private bool _cffParsed;

        /// <summary>The parsed 'CFF ' table, or null when this font has none.</summary>
        public CffOutlines.CffTable Cff
        {
            get
            {
                if (_cffParsed) return _cff;

                _cffParsed = true;

                if (_tables.TryGetValue("CFF ", out var t)) _cff = CffOutlines.CffTable.Parse(_data, t.off, t.len);

                return _cff;
            }
        }

        /// <summary>
        /// Where a glyph's description sits in 'glyf'.
        ///
        /// 'loca' has one more entry than there are glyphs, so a glyph runs from its own entry to
        /// the next. Equal entries mean an empty glyph -- a space -- which is why the range comes
        /// back valid but zero-length rather than as a failure.
        /// </summary>
        public bool TryGetGlyfRange(int glyphIndex, out int start, out int end)
        {
            start = end = 0;

            if (!HasGlyfOutlines || glyphIndex < 0 || glyphIndex >= NumGlyphs) return false;

            bool longFormat = IndexToLocFormat != 0;
            int entry = longFormat ? 4 : 2;

            if ((glyphIndex + 2) * entry > _locaLength) return false;

            int at = _locaOffset + glyphIndex * entry;

            uint from = longFormat ? U32(at) : (uint)(U16(at) * 2);
            uint to = longFormat ? U32(at + 4) : (uint)(U16(at + 2) * 2);

            start = _glyfOffset + (int)from;
            end = _glyfOffset + (int)to;

            return start >= 0 && end >= start && end <= _data.Length;
        }

        private void ParseOutlineTables()
        {
            if (_tables.TryGetValue("glyf", out var glyf) && _tables.TryGetValue("loca", out var loca))
            {
                _glyfOffset = glyf.off;
                _locaOffset = loca.off;
                _locaLength = loca.len;
            }
        }

        public byte[] GetTableBytes(string tag)
        {
            if (!_tables.TryGetValue(tag, out var t)) return null;
            var bytes = new byte[t.len];
            Array.Copy(_data, t.off, bytes, 0, t.len);
            return bytes;
        }

        private void ParseHead()
        {
            if (!_tables.TryGetValue("head", out var h)) { UnitsPerEm = 1000; return; }
            int o = h.off;
            FontRevision = S16(o + 4) + U16(o + 6) / 65536.0;
            UnitsPerEm = U16(o + 18);
            if (UnitsPerEm == 0) UnitsPerEm = 1000;
            MacStyle = U16(o + 44);
            XMin = S16(o + 36); YMin = S16(o + 38); XMax = S16(o + 40); YMax = S16(o + 42);
            IndexToLocFormat = S16(o + 50);
        }

        private void ParseMaxp()
        {
            if (_tables.TryGetValue("maxp", out var m)) NumGlyphs = U16(m.off + 4);
        }

        private void ParseHhea()
        {
            if (!_tables.TryGetValue("hhea", out var h)) return;
            int o = h.off;
            Ascender = S16(o + 4);
            Descender = S16(o + 6);
            LineGap = S16(o + 8);
            NumberOfHMetrics = U16(o + 34);
        }

        private void ParseOS2()
        {
            if (!_tables.TryGetValue("OS/2", out var t)) return;
            HasOS2 = true;
            int o = t.off;
            int version = U16(o);
            UsWeightClass = U16(o + 4);
            UsWidthClass = U16(o + 6);
            FsType = U16(o + 8);
            YStrikeoutSize = S16(o + 26);
            YStrikeoutPosition = S16(o + 28);
            PanoseFamilyKind = _data[o + 32];
            FsSelection = U16(o + 62);
            STypoAscender = S16(o + 68);
            STypoDescender = S16(o + 70);
            STypoLineGap = S16(o + 72);
            UsWinAscent = U16(o + 74);
            UsWinDescent = U16(o + 76);
            if (version >= 2 && t.len >= 90)
            {
                SxHeight = S16(o + 86);
                SCapHeight = S16(o + 88);
            }
            // A symbol font maps into the private use / F000 range only (usFirstCharIndex).
            ushort usFirstChar = U16(o + 64);
            IsSymbolFont = usFirstChar >= 0xF000;
        }

        private void ParsePost()
        {
            if (!_tables.TryGetValue("post", out var t)) return;
            int o = t.off;
            UnderlinePosition = S16(o + 8);
            UnderlineThickness = S16(o + 10);
            IsFixedPitch = U32(o + 12) != 0;
        }

        private void ParseHmtx()
        {
            if (!_tables.TryGetValue("hmtx", out var t) || NumberOfHMetrics == 0)
            {
                _advanceWidths = Array.Empty<ushort>();
                _leftSideBearings = Array.Empty<short>();
                return;
            }
            int n = NumberOfHMetrics;
            _advanceWidths = new ushort[n];
            _leftSideBearings = new short[Math.Max(NumGlyphs, n)];
            int o = t.off;
            for (int i = 0; i < n; i++)
            {
                _advanceWidths[i] = U16(o + i * 4);
                _leftSideBearings[i] = S16(o + i * 4 + 2);
            }
            // Remaining glyphs share the last advance; their lsb is in the trailing array.
            int extra = NumGlyphs - n;
            int baseOff = o + n * 4;
            for (int i = 0; i < extra; i++)
                _leftSideBearings[n + i] = S16(baseOff + i * 2);
        }

        public ushort AdvanceWidth(int gid)
        {
            if (_advanceWidths.Length == 0) return UnitsPerEm;
            return gid < _advanceWidths.Length ? _advanceWidths[gid] : _advanceWidths[_advanceWidths.Length - 1];
        }

        public short LeftSideBearing(int gid)
        {
            if (_leftSideBearings == null || gid >= _leftSideBearings.Length) return 0;
            return _leftSideBearings[gid];
        }

        private void ParseCmap()
        {
            if (_tables.TryGetValue("cmap", out var t))
                _cmap = new CmapReader(_data, t.off);
        }

        public ushort GlyphIndex(uint codepoint)
        {
            if (_cmap == null) return 0;
            ushort g = (ushort)_cmap.Map(codepoint);
            if (g == 0 && TryRemapFluentIcon(codepoint, out uint alt))
                g = (ushort)_cmap.Map(alt);
            return g;
        }

        // "Segoe Fluent Icons" moved/renamed several glyphs relative to the older
        // "Segoe MDL2 Assets". Off-Windows we substitute an MDL2-based icon font
        // (WinSymbols3 / Symbols.ttf), which lacks the Fluent-only PUA codepoints,
        // so those glyphs would render as .notdef tofu. When a codepoint is absent,
        // fall back to its MDL2 equivalent so control glyphs still appear. This only
        // triggers for fonts that don't contain the native glyph, so a real Segoe
        // Fluent Icons install is unaffected.
        private static bool TryRemapFluentIcon(uint codepoint, out uint mdl2)
        {
            switch (codepoint)
            {
                case 0xE9AE: mdl2 = 0xE738; return true; // CheckBox indeterminate dash -> "Remove" (minus)
                // Fluent-only codepoints used by the WPF Gallery's navigation. Each target was
                // checked to exist in the substitute font before being chosen; a mapping to an
                // absent glyph would just move the tofu rather than remove it.
                case 0xEB3C: mdl2 = 0xE790; return true; // "Colors"         -> Color (palette)
                case 0xED58: mdl2 = 0xE76E; return true; // "Icons"          -> Emoji2
                case 0xEF58: mdl2 = 0xE9D9; return true; // "User Dashboard" -> Analytics
                case 0xF246: mdl2 = 0xF0E2; return true; // "Layout"         -> GridView
                default: mdl2 = 0; return false;
            }
        }

        public bool HasCharacter(uint codepoint) => GlyphIndex(codepoint) != 0;

        // ---- name table ----
        private void ParseName()
        {
            if (!_tables.TryGetValue("name", out var t)) return;
            int o = t.off;
            int count = U16(o + 2);
            int storage = o + U16(o + 4);
            // best score per nameID; prefer Windows/Unicode English (3,1,0x409) then Mac English.
            var best = new Dictionary<int, (int score, string val)>();
            for (int i = 0; i < count; i++)
            {
                int rec = o + 6 + i * 12;
                int platformId = U16(rec);
                int encodingId = U16(rec + 2);
                int languageId = U16(rec + 4);
                int nameId = U16(rec + 6);
                int len = U16(rec + 8);
                int strOff = storage + U16(rec + 10);
                if (strOff + len > _data.Length) continue;

                string val = DecodeNameString(platformId, encodingId, strOff, len);
                if (val == null) continue;

                int score = ScoreNameRecord(platformId, encodingId, languageId);
                if (!best.TryGetValue(nameId, out var cur) || score > cur.score)
                    best[nameId] = (score, val);
            }

            string Get(int id) => best.TryGetValue(id, out var v) ? v.val : null;
            FamilyName = Get(1);
            SubfamilyName = Get(2);
            FullName = Get(4);
            PostScriptName = Get(6);
            TypographicFamilyName = Get(16);
            TypographicSubfamilyName = Get(17);
        }

        private static int ScoreNameRecord(int platformId, int encodingId, int languageId)
        {
            // Windows Unicode BMP English wins, then Windows any-English, then Mac Roman English.
            if (platformId == 3 && encodingId == 1 && languageId == 0x409) return 100;
            if (platformId == 3 && encodingId == 1) return 80;
            if (platformId == 3 && encodingId == 10) return 70;
            if (platformId == 0) return 60;                       // Unicode
            if (platformId == 1 && languageId == 0) return 40;    // Mac English
            if (platformId == 1) return 20;
            return 10;
        }

        private string DecodeNameString(int platformId, int encodingId, int off, int len)
        {
            try
            {
                // Windows and Unicode platforms use UTF-16BE; Mac Roman is 8-bit ASCII-ish.
                if (platformId == 3 || platformId == 0)
                    return Encoding.BigEndianUnicode.GetString(_data, off, len);
                if (platformId == 1)
                    return Encoding.ASCII.GetString(_data, off, len);
            }
            catch { }
            return null;
        }
    }

    // Minimal cmap reader supporting the common Unicode subtable formats (4 and 12),
    // preferring a full-Unicode (format 12) or BMP (format 4) Windows/Unicode subtable.
    internal sealed class CmapReader
    {
        private readonly byte[] _d;
        private readonly int _subtable;    // absolute offset of chosen subtable
        private readonly int _format;

        public CmapReader(byte[] data, int cmapOffset)
        {
            _d = data;
            int numTables = U16(cmapOffset + 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = cmapOffset + 4 + i * 8;
                int platformId = U16(rec);
                int encodingId = U16(rec + 2);
                int off = cmapOffset + (int)U32(rec + 4);
                int score = ScoreSubtable(platformId, encodingId);
                if (score > bestScore) { bestScore = score; best = off; }
            }
            if (best >= 0)
            {
                _subtable = best;
                _format = U16(best);
            }
        }

        private static int ScoreSubtable(int platformId, int encodingId)
        {
            if (platformId == 3 && encodingId == 10) return 100; // Windows UCS-4
            if (platformId == 0 && encodingId >= 4) return 95;   // Unicode full
            if (platformId == 3 && encodingId == 1) return 90;   // Windows BMP
            if (platformId == 0) return 80;                      // Unicode BMP
            if (platformId == 3 && encodingId == 0) return 50;   // Symbol
            if (platformId == 1 && encodingId == 0) return 30;   // Mac Roman
            return 10;
        }

        private ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(o));
        private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(o));

        public int Map(uint c)
        {
            if (_subtable == 0) return 0;
            return _format switch
            {
                4 => MapFormat4(c),
                12 => MapFormat12(c),
                6 => MapFormat6(c),
                0 => MapFormat0(c),
                _ => 0,
            };
        }

        private int MapFormat0(uint c)
        {
            if (c > 255) return 0;
            return _d[_subtable + 6 + (int)c];
        }

        private int MapFormat6(uint c)
        {
            int first = U16(_subtable + 6);
            int count = U16(_subtable + 8);
            if (c < (uint)first || c >= (uint)(first + count)) return 0;
            return U16(_subtable + 10 + (int)(c - first) * 2);
        }

        private int MapFormat4(uint c)
        {
            if (c > 0xFFFF) return 0;
            int segX2 = U16(_subtable + 6);
            int segCount = segX2 / 2;
            int endCodes = _subtable + 14;
            int startCodes = endCodes + segX2 + 2;      // +2 reservedPad
            int idDeltas = startCodes + segX2;
            int idRangeOffsets = idDeltas + segX2;
            for (int i = 0; i < segCount; i++)
            {
                int end = U16(endCodes + i * 2);
                if (c <= (uint)end)
                {
                    int start = U16(startCodes + i * 2);
                    if (c < (uint)start) return 0;
                    short idDelta = (short)U16(idDeltas + i * 2);
                    int idRangeOffset = U16(idRangeOffsets + i * 2);
                    if (idRangeOffset == 0)
                        return (int)((c + (uint)idDelta) & 0xFFFF);
                    int glyphIndexAddr = idRangeOffsets + i * 2 + idRangeOffset + (int)(c - start) * 2;
                    if (glyphIndexAddr + 2 > _d.Length) return 0;
                    int g = U16(glyphIndexAddr);
                    if (g == 0) return 0;
                    return (int)((g + idDelta) & 0xFFFF);
                }
            }
            return 0;
        }

        private int MapFormat12(uint c)
        {
            int nGroups = (int)U32(_subtable + 12);
            int groups = _subtable + 16;
            // groups are sorted; binary search.
            int lo = 0, hi = nGroups - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int g = groups + mid * 12;
                uint startChar = U32(g);
                uint endChar = U32(g + 4);
                if (c < startChar) hi = mid - 1;
                else if (c > endChar) lo = mid + 1;
                else return (int)(U32(g + 8) + (c - startChar));
            }
            return 0;
        }
    }
}
