// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>The GPOS table's pair adjustments: how far apart a face wants two glyphs.
    /// <para>The legacy 'kern' table is what this stack read, and for Latin that is right -- GDI's
    /// simple text path applies those pairs and nothing else, which is why every Latin battery is
    /// exact without a line of GPOS. A COMPLEX script does not go down that path: ExtTextOutW hands
    /// it to the shaping engine, which applies the face's GPOS. Segoe UI's Arabic proves it -- its
    /// 'kern' feature holds PairPos entries for reh+zain and zain+seen of -171 units each, which is
    /// -1.34 pixels at 16ppem, and those are EXACTLY the two pairs of the Arabic alphabet whose ink
    /// matched GDI's while our run came out a pixel wider at each (thal+reh, its neighbour in the
    /// alphabet, is not covered and was exact). The rest of the alphabet needed nothing.</para>
    /// <para>Implemented: pair adjustment (type 2) in both formats -- the per-glyph list and the
    /// class matrix -- and extension positioning (type 9), because real faces wrap their lookups in
    /// it. Only the x advance is read. The other value fields, the cursive, mark and mark-to-mark
    /// lookups and the contextual types are not here: nothing measured has asked for them yet, and
    /// a face that wants them gets its unadjusted positions rather than a wrong guess.</para>
    /// <para>A pair records BOTH an x placement and an x advance, the same number twice
    /// (-171, -171 above). That is how a face writes a right-to-left kern so that an engine which
    /// lays the run out in either direction closes the same gap once: the placement is the
    /// compensation for the advance, not a second shift. Applying the advance alone is what matches
    /// GDI -- its ink sits where ours does, pair by pair, and only the gap differed.</para></summary>
    internal sealed class GposTable
    {
        private readonly byte[] _d;
        private readonly int _scriptList, _featureList, _lookupList;

        // (script, feature) -> lookup indices.
        private readonly Dictionary<(string, string), List<int>> _features = new();

        public GposTable(byte[] data, int offset)
        {
            _d = data;
            _scriptList = offset + U16(offset + 4);
            _featureList = offset + U16(offset + 6);
            _lookupList = offset + U16(offset + 8);
        }

        /// <summary>The adjustment this feature makes to the gap between two glyphs, in font design
        /// units, negative to tighten. False when no lookup of the feature covers the pair.</summary>
        public bool TryPairAdjustment(string script, string feature, int first, int second,
                                      out int xAdvanceUnits)
        {
            xAdvanceUnits = 0;
            foreach (int lookup in Lookups(script, feature))
                foreach ((int type, int sub) in SubTables(lookup))
                {
                    if (type != 2) continue;
                    if (TryPairSubtable(sub, first, second, out xAdvanceUnits)) return true;
                }
            return false;
        }

        /// <summary>Whether the table has any lookup for this script and feature at all, so a caller
        /// can skip the per-pair work for a face that says nothing about the script.</summary>
        public bool HasFeature(string script, string feature) => Lookups(script, feature).Count > 0;

        // ---- pair adjustment (type 2) ----

        private bool TryPairSubtable(int sub, int first, int second, out int xAdvanceUnits)
        {
            xAdvanceUnits = 0;
            int format = U16(sub);
            int index = CoverageIndex(sub + U16(sub + 2), first);
            if (index < 0) return false;
            int value1 = U16(sub + 4), value2 = U16(sub + 6);
            int size1 = ValueSize(value1), size2 = ValueSize(value2);

            if (format == 1)
            {
                int set = sub + U16(sub + 10 + index * 2);
                int count = U16(set), step = 2 + size1 + size2;
                for (int i = 0; i < count; i++)
                {
                    int rec = set + 2 + i * step;
                    if (U16(rec) != second) continue;
                    xAdvanceUnits = XAdvance(rec + 2, value1);
                    return true;
                }
                return false;
            }
            if (format == 2)
            {
                int class2Count = U16(sub + 14);
                int c1 = ClassOf(sub + U16(sub + 8), first);
                int c2 = ClassOf(sub + U16(sub + 10), second);
                if (c1 >= U16(sub + 12) || c2 >= class2Count) return false;
                int rec = sub + 16 + (c1 * class2Count + c2) * (size1 + size2);
                xAdvanceUnits = XAdvance(rec, value1);
                return xAdvanceUnits != 0;
            }
            return false;
        }

        /// <summary>The bytes a value record takes: two for every field its format names.</summary>
        private static int ValueSize(int format)
        {
            int n = 0;
            for (int bit = 1; bit <= 0x80; bit <<= 1)
                if ((format & bit) != 0) n += 2;
            return n;
        }

        /// <summary>The x advance out of a value record, which sits after the x and y placement when
        /// the format names those.</summary>
        private int XAdvance(int rec, int format)
        {
            if ((format & 0x0004) == 0) return 0;
            int at = rec;
            if ((format & 0x0001) != 0) at += 2;      // XPlacement
            if ((format & 0x0002) != 0) at += 2;      // YPlacement
            return (short) U16(at);
        }

        // ---- the same script, feature and coverage machinery GSUB has ----

        private List<int> Lookups(string script, string feature)
        {
            if (_features.TryGetValue((script, feature), out List<int>? cached)) return cached;
            var found = new List<int>();
            int langSys = LangSys(script);
            if (langSys > 0)
            {
                int count = U16(langSys + 4);
                for (int i = 0; i < count; i++)
                {
                    int index = U16(langSys + 6 + i * 2);
                    if (FeatureTag(index) != feature) continue;
                    int table = _featureList + U16(_featureList + 2 + index * 6 + 4);
                    int lookupCount = U16(table + 2);
                    for (int j = 0; j < lookupCount; j++) found.Add(U16(table + 4 + j * 2));
                }
            }
            return _features[(script, feature)] = found;
        }

        private int LangSys(string script)
        {
            int table = ScriptTable(script);
            if (table <= 0) return 0;
            int dflt = U16(table);
            return dflt == 0 ? 0 : table + dflt;
        }

        private int ScriptTable(string script)
        {
            int count = U16(_scriptList);
            for (int i = 0; i < count; i++)
            {
                int record = _scriptList + 2 + i * 6;
                if (Tag(record) == script) return _scriptList + U16(record + 4);
            }
            return 0;
        }

        private string FeatureTag(int index)
            => index >= U16(_featureList) ? string.Empty : Tag(_featureList + 2 + index * 6);

        /// <summary>Every subtable of a lookup, with extension positioning (type 9) unwrapped so the
        /// caller sees the type the subtable really is.</summary>
        private IEnumerable<(int Type, int Offset)> SubTables(int lookupIndex)
        {
            if (lookupIndex >= U16(_lookupList)) yield break;
            int lookup = _lookupList + U16(_lookupList + 2 + lookupIndex * 2);
            int type = U16(lookup), count = U16(lookup + 4);
            for (int i = 0; i < count; i++)
            {
                int sub = lookup + U16(lookup + 6 + i * 2);
                if (type == 9 && U16(sub) == 1)
                    yield return (U16(sub + 2), sub + (int) U32(sub + 4));
                else
                    yield return (type, sub);
            }
        }

        private int CoverageIndex(int coverage, int glyph)
        {
            int format = U16(coverage), count = U16(coverage + 2);
            if (format == 1)
            {
                for (int i = 0; i < count; i++)
                    if (U16(coverage + 4 + i * 2) == glyph) return i;
                return -1;
            }
            if (format == 2)
                for (int i = 0; i < count; i++)
                {
                    int record = coverage + 4 + i * 6;
                    int start = U16(record), end = U16(record + 2);
                    if (glyph >= start && glyph <= end) return U16(record + 4) + glyph - start;
                }
            return -1;
        }

        private int ClassOf(int classDef, int glyph)
        {
            int format = U16(classDef);
            if (format == 1)
            {
                int start = U16(classDef + 2), count = U16(classDef + 4);
                return glyph >= start && glyph < start + count ? U16(classDef + 6 + (glyph - start) * 2) : 0;
            }
            if (format == 2)
            {
                int count = U16(classDef + 2);
                for (int i = 0; i < count; i++)
                {
                    int record = classDef + 4 + i * 6;
                    if (glyph >= U16(record) && glyph <= U16(record + 2)) return U16(record + 4);
                }
            }
            return 0;
        }

        private int U16(int at) => at + 1 < _d.Length ? (_d[at] << 8) | _d[at + 1] : 0;

        private uint U32(int at) => at + 3 < _d.Length
            ? ((uint) _d[at] << 24) | ((uint) _d[at + 1] << 16) | ((uint) _d[at + 2] << 8) | _d[at + 3]
            : 0u;

        private string Tag(int at) => at + 3 < _d.Length
            ? string.Concat((char) _d[at], (char) _d[at + 1], (char) _d[at + 2], (char) _d[at + 3])
            : string.Empty;
    }
}
