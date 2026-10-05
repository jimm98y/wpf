// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shaping GDI+'s full imager asks DirectWrite for, on the port's own font stack:
//
//   IDWriteTextAnalyzer::GetGlyphs (FullTextImager::CreateTextRuns @18003aaf8, GetGlyphsWithHotKeys)
//       the run's glyphs, the character-to-glyph cluster map, the per-glyph shaping properties
//       (bit 4 isClusterStart, bit 5 isDiacritic, bit 6 isZeroWidthSpace). GDI+ asks with no
//       features, which for Latin is the face's 'ccmp' and nothing typographic: no ligatures (the
//       battery: Calibri/Gabriola/Segoe UI "office fifty flight" are one glyph per character).
//   IDWriteTextAnalyzer::GetGlyphPlacements (GdipLscbkGetGlyphPositions @18003d790)
//       the advances at an em -- the design advance and the face's kerning ('kern' in GPOS, else
//       the legacy table) -- and the offsets.
//   IDWriteTextAnalyzer::GetGdiCompatibleGlyphPlacements (GlyphImager::Initialize @1800f5df0)
//       the same under the world-to-device transform with the realization's advance type: each
//       glyph's hinted device advance, back in DIPs.
//

using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using GdipText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpTextShaper
    {
        public const ushort PropClusterStart = 0x10, PropDiacritic = 0x20, PropZeroWidth = 0x40;

        /// <summary>The OpenType script a GDI+ ItemScript shapes with.</summary>
        public static string ScriptTag (int itemScript) => itemScript switch {
            3 => "grek", 4 => "cyrl", 5 => "armn", 6 => "hebr", 7 => "arab", 8 => "syrc", 9 => "thaa",
            0xa => "deva", 0xb => "beng", 0xc => "guru", 0xd => "gujr", 0xe => "orya", 0xf => "taml",
            0x10 => "telu", 0x11 => "knda", 0x12 => "mlym", 0x13 => "sinh", 0x14 => "thai", 0x15 => "lao ",
            0x16 => "tibt", 0x17 => "mymr", 0x18 => "geor", 0x19 => "ethi", 0x1a => "cher", 0x1b => "cans",
            0x1c => "ogam", 0x1d => "runr", 0x1e => "khmr", 0x1f => "mong", 0x21 => "bopo", 0x24 => "hang",
            0x26 => "kana", 0x27 => "kana", 0x28 => "hani", 0x29 => "yi  ",
            _ => "latn",
        };

        /// <summary>A shaped run: glyph i is Glyphs[i]; character k's first glyph is ClusterMap[k].</summary>
        public sealed class Shaped
        {
            public ushort[] Glyphs = Array.Empty<ushort> ();
            public ushort[] ClusterMap = Array.Empty<ushort> ();
            public ushort[] GlyphProps = Array.Empty<ushort> ();
            public ushort[] TextProps = Array.Empty<ushort> ();
        }

        /// <summary>FullTextImager::GetGlyphsWithHotKeys @1800f11a8: the hotkey markers (U+FFFF)
        /// are taken out, the rest shaped, and each marker joins the cluster of the character
        /// after it; a marker with none after it gets U+200B's glyph (the blank one where the face
        /// has none) and properties.</summary>
        public static Shaped GetGlyphsWithHotKeys (TrueTypeFont face, string text, int start, int len, int itemScript, bool rtl)
        {
            int marks = 0;
            for (int k = 0; k < len; k++) if (text [start + k] == '￿') marks++;
            if (marks == 0) return GetGlyphs (face, text, start, len, itemScript, rtl);
            var sb = new System.Text.StringBuilder (len - marks);
            for (int k = 0; k < len; k++) if (text [start + k] != '￿') sb.Append (text [start + k]);
            Shaped inner = marks == len ? new Shaped () : GetGlyphs (face, sb.ToString (), 0, sb.Length, itemScript, rtl);
            var glyphs = new List<ushort> (inner.Glyphs);
            var props = new List<ushort> (inner.GlyphProps);
            var map = new ushort [len];
            var tprops = new ushort [len];
            int zw = face.GlyphIndex ('​');
            if (zw == 0) zw = face.GlyphIndex (' ');
            int j = 0;      // index into the marker-free text
            for (int k = 0; k < len; k++) {
                if (text [start + k] != '￿') { map [k] = inner.ClusterMap [j]; tprops [k] = inner.TextProps [j]; j++; continue; }
                // The marker takes the cluster of the next character that is not one.
                int m = k + 1;
                while (m < len && text [start + m] == '￿') m++;
                if (m < len) {
                    int jj = j;
                    map [k] = inner.ClusterMap [jj];
                } else {
                    map [k] = (ushort) glyphs.Count;
                    glyphs.Add ((ushort) zw);
                    props.Add (PropClusterStart | PropZeroWidth);
                }
            }
            return new Shaped { Glyphs = glyphs.ToArray (), GlyphProps = props.ToArray (), ClusterMap = map, TextProps = tprops };
        }

        /// <summary>GetGlyphs over text[start, start + len).</summary>
        public static Shaped GetGlyphs (TrueTypeFont face, string text, int start, int len, int itemScript, bool rtl)
        {
            var glyphs = new List<int> (len);
            var clusters = new List<int> (len);
            var charToCluster = new int [len];
            for (int k = 0; k < len; k++) {
                int ch = text [start + k];
                // A surrogate pair is one character to the cmap and one cluster.
                if (char.IsHighSurrogate ((char) ch) && k + 1 < len && char.IsLowSurrogate (text [start + k + 1])) {
                    int cp = char.ConvertToUtf32 ((char) ch, text [start + k + 1]);
                    charToCluster [k] = charToCluster [k + 1] = glyphs.Count;
                    clusters.Add (k);
                    glyphs.Add (face.GlyphIndexOf (cp));
                    k++;
                    continue;
                }
                if (rtl) ch = Mirror (ch);
                charToCluster [k] = glyphs.Count;
                clusters.Add (k);
                glyphs.Add (face.GlyphIndexOf (ch));
            }
            GsubTable gsub = face.Gsub;
            if (gsub != null) {
                string tag = ScriptTag (itemScript);
                if (!gsub.HasScript (tag)) tag = gsub.HasScript ("DFLT") ? "DFLT" : tag;
                gsub.ApplyFeatures (tag, rtl ? DefaultFeaturesRtl : DefaultFeatures, glyphs, clusters);
            }
            var s = new Shaped {
                Glyphs = new ushort [glyphs.Count],
                ClusterMap = new ushort [len],
                GlyphProps = new ushort [glyphs.Count],
                TextProps = new ushort [len],
            };
            for (int i = 0; i < glyphs.Count; i++) {
                s.Glyphs [i] = (ushort) glyphs [i];
                s.GlyphProps [i] = PropClusterStart;
            }
            // Each character to the first glyph of the cluster that consumed it.
            int g = 0;
            for (int k = 0; k < len; k++) {
                while (g + 1 < clusters.Count && clusters [g + 1] <= k) g++;
                s.ClusterMap [k] = (ushort) g;
            }
            return s;
        }

        /// <summary>DirectWrite's default substitution features for a run GDI+ shapes with no
        /// features of its own: the mandatory ones and the standard ligatures and contextual
        /// alternates (Calibri's "fl" is one glyph in the full imager, two in the fast one, which
        /// maps characters through the cmap alone).</summary>
        static readonly string[] DefaultFeatures = { "ccmp", "locl", "rlig", "rclt", "calt", "liga", "clig" };
        static readonly string[] DefaultFeaturesRtl = { "ccmp", "locl", "rtla", "rtlm", "rlig", "rclt", "calt", "liga", "clig" };

        /// <summary>The bidi mirrored form of a character in a right-to-left run.</summary>
        static int Mirror (int ch) => ch switch {
            '(' => ')', ')' => '(', '<' => '>', '>' => '<', '[' => ']', ']' => '[', '{' => '}', '}' => '{',
            0xab => 0xbb, 0xbb => 0xab, 0x2039 => 0x203a, 0x203a => 0x2039, 0x2264 => 0x2265, 0x2265 => 0x2264,
            _ => ch,
        };

        static readonly bool s_legacyKern = Environment.GetEnvironmentVariable ("WF_FTI_LEGACYKERN") == "1";

        /// <summary>The face's kerning between two glyphs, in design units: GPOS 'kern' for the
        /// script (or DFLT), else the legacy 'kern' table.</summary>
        public static int Kern (TrueTypeFont face, int itemScript, int left, int right)
        {
            GposTable gpos = face.Gpos;
            if (gpos != null) {
                string tag = ScriptTag (itemScript);
                if (!gpos.HasFeature (tag, "kern")) tag = gpos.HasFeature ("latn", "kern") ? "latn" : gpos.HasFeature ("DFLT", "kern") ? "DFLT" : null;
                if (tag != null)
                    return gpos.TryPairAdjustment (tag, "kern", left, right, out int ku) ? ku : 0;
            }
            return s_legacyKern && face.TryGetKernUnits (left, right, out int u) ? u : 0;
        }

        /// <summary>The design advance DirectWrite gives GDI+ (IDWriteFontFace::GetDesignGlyphMetrics):
        /// a face with a bold simulation widens every glyph that has an outline by round(upem / 50)
        /// (blanks keep theirs; measured on 256-, 1000- and 2048-unit faces).</summary>
        public static int DesignAdvance (TrueTypeFont face, int gid)
        {
            int a = face.DesignAdvance (gid);
            if (face.SynthesizesBold && HasContours (face, gid))
                a += (int) MathF.Floor (face.UnitsPerEmForHinting / 50f + 0.5f);
            return a;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrueTypeFont, Dictionary<int, bool>> s_contours = new ();

        static bool HasContours (TrueTypeFont face, int gid)
        {
            Dictionary<int, bool> d = s_contours.GetOrCreateValue (face);
            lock (d) {
                if (!d.TryGetValue (gid, out bool has)) d [gid] = has = face.DesignContours (gid).Count > 0;
                return has;
            }
        }

        /// <summary>GetGlyphPlacements at an em of <paramref name="em"/> (design units scaled by em / upem).</summary>
        public static float[] GetGlyphAdvances (TrueTypeFont face, ushort[] glyphs, int start, int count, int itemScript, float em)
        {
            int upem = face.UnitsPerEmForHinting;
            float k = em / upem;
            var adv = new float [count];
            for (int i = 0; i < count; i++) {
                int a = DesignAdvance (face, glyphs [start + i]);
                if (i + 1 < count) a += Kern (face, itemScript, glyphs [start + i], glyphs [start + i + 1]);
                adv [i] = a * k;
            }
            return adv;
        }

        /// <summary>The device advance of one glyph in whole pixels for a realization: GDI natural
        /// (ClearType), GDI classic (the other grid-fitted hints), or design for the others.</summary>
        public static float DeviceAdvancePx (TrueTypeFont face, int gid, float em, float sx, float sy, int mode)
        {
            int upem = face.UnitsPerEmForHinting;
            if (mode == 5) {
                GdipText.NaturalMetrics (face, gid, em, sx, sy, out int adv, out _, out _);
                return MathF.Floor (adv * (em * sx / upem) + 0.5f) + SimBoldPx (face, gid);
            }
            if (mode == 1 || mode == 3) {
                GdipText.ClassicMetrics (face, gid, em * sx, out int adv, out _, out _);
                return MathF.Floor (adv * (em * sx / upem) + 0.5f) + SimBoldPx (face, gid);
            }
            return DesignAdvance (face, gid) * (em * sx / upem);
        }

        /// <summary>The realization's own advance in device pixels, not put on the pixel grid
        /// (GpFaceRealization::GetGlyphStringIdealAdvanceVector's source).</summary>
        public static float RealizationAdvancePx (TrueTypeFont face, int gid, float em, float sx, float sy, int mode)
        {
            int upem = face.UnitsPerEmForHinting;
            if (mode == 5) {
                GdipText.NaturalMetrics (face, gid, em, sx, sy, out int adv, out _, out _);
                return adv * (em * sx / upem) + SimBoldPx (face, gid);
            }
            if (mode == 1 || mode == 3) {
                GdipText.ClassicMetrics (face, gid, em * sx, out int adv, out _, out _);
                return adv * (em * sx / upem) + SimBoldPx (face, gid);
            }
            return DesignAdvance (face, gid) * (em * sx / upem);
        }

        /// <summary>DirectWrite's GDI-compatible metrics of a bold simulation: a glyph with an
        /// outline a device pixel wider.</summary>
        static int SimBoldPx (TrueTypeFont face, int gid) => face.SynthesizesBold && HasContours (face, gid) ? 1 : 0;
    }
}
