// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>The GSUB table: the substitutions a face makes to its own glyphs.
    /// <para>Latin barely needs this and every measurement in this port was Latin, so it was never
    /// written -- the shaper mapped one character to one glyph and stopped. That is wrong for any
    /// script whose letters change shape with their neighbours: Arabic beh is a different glyph at
    /// the start, middle and end of a word, and a face draws all four forms from ONE character by
    /// substituting the glyph. Without this, Arabic comes out as a row of unjoined isolated
    /// letters -- legible to nobody, and 38% too much ink against GDI.</para>
    /// <para>Implemented: single substitution (type 1, both formats) which is what the four
    /// positional forms use, ligature substitution (type 4) which is what lam-alef needs and which
    /// is MANDATORY in Arabic rather than a typographic nicety, and extension (type 7) because real
    /// faces wrap their lookups in it. Coverage tables in both formats. Not implemented: the
    /// contextual types (5, 6, 8) -- unreached by the positional features, and a face that needs
    /// them for a script gets its ordinary forms rather than nothing.</para></summary>
    internal sealed class GsubTable
    {
        private readonly byte[] _d;
        private readonly int _scriptList, _featureList, _lookupList;

        // (script, feature) -> lookup indices, and lookup index -> parsed single substitutions.
        private readonly Dictionary<(string, string), List<int>> _features = new();
        private readonly Dictionary<int, Dictionary<int, int>> _singles = new();

        public GsubTable(byte[] data, int offset)
        {
            _d = data;
            _scriptList = offset + U16(offset + 4);
            _featureList = offset + U16(offset + 6);
            _lookupList = offset + U16(offset + 8);
        }

        /// <summary>The glyph this feature substitutes for <paramref name="glyph"/>, or the glyph
        /// itself when the feature does not touch it.</summary>
        public int Substitute(string script, string feature, int glyph)
        {
            foreach (int lookup in Lookups(script, feature))
                if (Singles(lookup).TryGetValue(glyph, out int to))
                    return to;
            return glyph;
        }

        /// <summary>The longest ligature this feature forms starting at <paramref name="at"/>.
        /// <para>Reports how many glyphs it consumed, because a ligature replaces several with
        /// one and the caller has to drop the rest.</para></summary>
        public bool TryLigature(string script, string feature, IReadOnlyList<int> glyphs, int at,
                                out int ligature, out int consumed)
        {
            ligature = 0; consumed = 0;
            foreach (int lookup in Lookups(script, feature))
                foreach ((int type, int sub) in SubTables(lookup))
                    if (type == 4 && TryLigatureSubtable(sub, glyphs, at, ref ligature, ref consumed))
                        return true;
            return consumed > 0;
        }

        /// <summary>The features a script registers, for asking a face what it can do rather
        /// than assuming -- 'rlig' turned out to be absent where it was expected.</summary>
        public IEnumerable<string> Features(string script)
        {
            int langSys = LangSys(script);
            if (langSys == 0) yield break;
            int count = U16(langSys + 4);
            for (int i = 0; i < count; i++) yield return FeatureTag(U16(langSys + 6 + i * 2));
        }

        /// <summary>The lookup types a feature is built from -- the question that explains a
        /// feature which is present and yet does nothing.</summary>
        public IEnumerable<int> LookupTypes(string script, string feature)
        {
            foreach (int lookup in Lookups(script, feature))
                foreach ((int type, int _) in SubTables(lookup)) yield return type;
        }

        /// <summary>Whether the face registers this script AT ALL.
        /// <para>Strictly: unlike the feature lookups, this must not fall back to the face's
        /// default script, or every tag ever asked about comes back present.</para></summary>
        public bool HasScript(string script) => ScriptTable(script) != 0;

        /// <summary>The scripts the face registers.</summary>
        public IEnumerable<string> Scripts()
        {
            int count = U16(_scriptList);
            for (int i = 0; i < count; i++) yield return Tag(_scriptList + 2 + i * 6);
        }

        /// <summary>Whether the face has this feature at all, so a shaper can tell "the face does
        /// not do this" from "the face does it and this glyph is unaffected".</summary>
        public bool HasFeature(string script, string feature) => Lookups(script, feature).Count > 0;

        // ---- the lists ----

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

        /// <summary>The default language system of a script, falling back to the face's default
        /// script -- a face may register its Arabic lookups under 'DFLT' alone.</summary>
        private int LangSys(string script)
        {
            int table = ScriptTable(script);
            if (table == 0) table = ScriptTable("DFLT");
            if (table == 0) return 0;
            int defaultLangSys = U16(table);
            return defaultLangSys == 0 ? 0 : table + defaultLangSys;
        }

        private int ScriptTable(string script)
        {
            int count = U16(_scriptList);
            for (int i = 0; i < count; i++)
            {
                int rec = _scriptList + 2 + i * 6;
                if (Tag(rec) == script) return _scriptList + U16(rec + 4);
            }
            return 0;
        }

        private string FeatureTag(int index)
        {
            if (index >= U16(_featureList)) return string.Empty;
            return Tag(_featureList + 2 + index * 6);
        }

        // ---- the lookups ----

        /// <summary>Every subtable of a lookup, with extension (type 7) unwrapped so callers see
        /// the type the subtable really is.</summary>
        private IEnumerable<(int Type, int Offset)> SubTables(int lookupIndex)
        {
            if (lookupIndex >= U16(_lookupList)) yield break;
            int lookup = _lookupList + U16(_lookupList + 2 + lookupIndex * 2);
            int type = U16(lookup), count = U16(lookup + 4);
            for (int i = 0; i < count; i++)
            {
                int sub = lookup + U16(lookup + 6 + i * 2);
                if (type == 7 && U16(sub) == 1)
                    yield return (U16(sub + 2), sub + (int) U32(sub + 4));
                else
                    yield return (type, sub);
            }
        }

        private Dictionary<int, int> Singles(int lookupIndex)
        {
            if (_singles.TryGetValue(lookupIndex, out Dictionary<int, int>? cached)) return cached;
            var map = new Dictionary<int, int>();
            foreach ((int type, int sub) in SubTables(lookupIndex))
            {
                if (type != 1) continue;
                int format = U16(sub);
                int coverage = sub + U16(sub + 2);
                if (format == 1)
                {
                    // Every covered glyph moves by the same delta, modulo the glyph count.
                    short delta = (short) U16(sub + 4);
                    foreach (int g in Covered(coverage)) map[g] = (g + delta) & 0xFFFF;
                }
                else if (format == 2)
                {
                    int glyphCount = U16(sub + 4);
                    int i = 0;
                    foreach (int g in Covered(coverage))
                    {
                        if (i >= glyphCount) break;
                        map[g] = U16(sub + 6 + i * 2);
                        i++;
                    }
                }
            }
            return _singles[lookupIndex] = map;
        }

        private bool TryLigatureSubtable(int sub, IReadOnlyList<int> glyphs, int at,
                                         ref int ligature, ref int consumed)
        {
            if (U16(sub) != 1) return false;
            int coverage = sub + U16(sub + 2);
            int index = CoverageIndex(coverage, glyphs[at]);
            if (index < 0 || index >= U16(sub + 4)) return false;

            int set = sub + U16(sub + 6 + index * 2);
            int ligCount = U16(set);
            for (int i = 0; i < ligCount; i++)
            {
                int lig = set + U16(set + 2 + i * 2);
                int components = U16(lig + 2);
                if (at + components > glyphs.Count) continue;
                bool match = true;
                for (int c = 1; c < components && match; c++)
                    match = glyphs[at + c] == U16(lig + 2 + c * 2);
                // Longest wins: a face may list both a two- and a three-glyph ligature here.
                if (match && components > consumed)
                {
                    ligature = U16(lig);
                    consumed = components;
                }
            }
            return consumed > 0;
        }

        // ---- applying a feature across a run ----

        /// <summary>Applies every lookup of a feature across a run of glyphs.
        /// <para>The positional features are asked about ONE glyph at a time, because which form a
        /// letter takes is the shaper's decision and not the font's. Everything else -- the
        /// mandatory ligatures, the contextual alternates -- is a rule about a SEQUENCE, and has to
        /// be walked across the run like this.</para></summary>
        public bool ApplyFeature(string script, string feature, List<int> glyphs)
        {
            bool changed = false;
            foreach (int lookup in Lookups(script, feature))
                for (int at = 0; at < glyphs.Count; at++)
                    if (ApplyLookup(lookup, glyphs, at, depth: 0))
                        changed = true;
            return changed;
        }

        private bool ApplyLookup(int lookupIndex, List<int> glyphs, int at, int depth)
        {
            if (depth > 4 || at >= glyphs.Count) return false;   // a font may not recurse forever
            foreach ((int type, int sub) in SubTables(lookupIndex))
            {
                switch (type)
                {
                    case 1:
                        if (Singles(lookupIndex).TryGetValue(glyphs[at], out int to) && to != glyphs[at])
                        {
                            glyphs[at] = to;
                            return true;
                        }
                        break;
                    case 4:
                    {
                        int lig = 0, consumed = 0;
                        if (TryLigatureSubtable(sub, glyphs, at, ref lig, ref consumed) && consumed >= 2)
                        {
                            glyphs[at] = lig;
                            glyphs.RemoveRange(at + 1, consumed - 1);
                            return true;
                        }
                        break;
                    }
                    case 6:
                        if (ApplyChained(sub, glyphs, at, depth)) return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>Chained contextual substitution (type 6): "substitute HERE, but only with
        /// these glyphs before and after".
        /// <para>This is how a real face spells its mandatory ligatures. Segoe UI files lam-alef --
        /// the one Arabic pair with no unjoined spelling -- under 'rlig' as type 6, so a reader
        /// that handles only the plain ligature type finds the feature present, applies nothing,
        /// and leaves the pair unjoined. Formats 1 and 3 are here; format 2 needs class
        /// definitions and is read the same way once ClassOf is answered.</para></summary>
        private bool ApplyChained(int sub, List<int> glyphs, int at, int depth)
        {
            int format = U16(sub);
            if (format == 3)
            {
                int p = sub + 2;
                int backtrack = U16(p); p += 2;
                int[] back = Offsets(sub, p, backtrack); p += backtrack * 2;
                int input = U16(p); p += 2;
                int[] mid = Offsets(sub, p, input); p += input * 2;
                int ahead = U16(p); p += 2;
                int[] look = Offsets(sub, p, ahead); p += ahead * 2;
                int records = U16(p); p += 2;

                if (!Matches(glyphs, at, back, mid, look)) return false;
                return ApplyRecords(glyphs, at, p, records, depth);
            }
            if (format == 1)
            {
                int coverage = sub + U16(sub + 2);
                int index = CoverageIndex(coverage, glyphs[at]);
                if (index < 0 || index >= U16(sub + 4)) return false;
                int set = sub + U16(sub + 6 + index * 2);
                int rules = U16(set);
                for (int r = 0; r < rules; r++)
                {
                    int rule = set + U16(set + 2 + r * 2);
                    int p = rule;
                    int backtrack = U16(p); p += 2;
                    bool ok = true;
                    for (int i = 0; i < backtrack && ok; i++, p += 2)
                        ok = at - 1 - i >= 0 && glyphs[at - 1 - i] == U16(p);
                    int input = U16(p); p += 2;
                    for (int i = 1; i < input && ok; i++, p += 2)
                        ok = at + i < glyphs.Count && glyphs[at + i] == U16(p);
                    if (!ok) { continue; }
                    int ahead = U16(p); p += 2;
                    for (int i = 0; i < ahead && ok; i++, p += 2)
                        ok = at + input + i < glyphs.Count && glyphs[at + input + i] == U16(p);
                    if (!ok) continue;
                    int records = U16(p); p += 2;
                    if (ApplyRecords(glyphs, at, p, records, depth)) return true;
                }
            }
            return false;
        }

        private bool Matches(List<int> glyphs, int at, int[] back, int[] mid, int[] look)
        {
            for (int i = 0; i < mid.Length; i++)
                if (at + i >= glyphs.Count || CoverageIndex(mid[i], glyphs[at + i]) < 0) return false;
            // Backtrack coverages are listed nearest-first, walking BACKWARDS from the input.
            for (int i = 0; i < back.Length; i++)
                if (at - 1 - i < 0 || CoverageIndex(back[i], glyphs[at - 1 - i]) < 0) return false;
            for (int i = 0; i < look.Length; i++)
            {
                int j = at + mid.Length + i;
                if (j >= glyphs.Count || CoverageIndex(look[i], glyphs[j]) < 0) return false;
            }
            return true;
        }

        /// <summary>Runs the nested lookups a matched context asks for, at the positions it names.
        /// </summary>
        private bool ApplyRecords(List<int> glyphs, int at, int p, int records, int depth)
        {
            bool applied = false;
            for (int i = 0; i < records; i++)
            {
                int sequenceIndex = U16(p + i * 4);
                int lookupIndex = U16(p + i * 4 + 2);
                if (ApplyLookup(lookupIndex, glyphs, at + sequenceIndex, depth + 1)) applied = true;
            }
            return applied;
        }

        private int[] Offsets(int sub, int p, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = sub + U16(p + i * 2);
            return result;
        }

        // ---- coverage ----

        private IEnumerable<int> Covered(int coverage)
        {
            int format = U16(coverage);
            if (format == 1)
            {
                int count = U16(coverage + 2);
                for (int i = 0; i < count; i++) yield return U16(coverage + 4 + i * 2);
            }
            else if (format == 2)
            {
                int ranges = U16(coverage + 2);
                for (int r = 0; r < ranges; r++)
                {
                    int rec = coverage + 4 + r * 6;
                    for (int g = U16(rec); g <= U16(rec + 2); g++) yield return g;
                }
            }
        }

        private int CoverageIndex(int coverage, int glyph)
        {
            int format = U16(coverage);
            if (format == 1)
            {
                int count = U16(coverage + 2);
                for (int i = 0; i < count; i++) if (U16(coverage + 4 + i * 2) == glyph) return i;
            }
            else if (format == 2)
            {
                int ranges = U16(coverage + 2);
                for (int r = 0; r < ranges; r++)
                {
                    int rec = coverage + 4 + r * 6;
                    if (glyph >= U16(rec) && glyph <= U16(rec + 2))
                        return U16(rec + 4) + glyph - U16(rec);
                }
            }
            return -1;
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
