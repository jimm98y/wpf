// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>A font whose positional forms this shaper can reach.</summary>
    internal interface IOpenTypeShapingFont : IShapingFont
    {
        GsubTable? Gsub { get; }
    }

    /// <summary>Contextual shaping: the glyph a letter takes from the company it keeps.
    /// <para>The shapers above map one character to one glyph, which is right for Latin and wrong
    /// for a joining script. Arabic letters have four shapes -- isolated, initial, medial, final --
    /// and a face draws all four from one character by substituting the glyph, so mapping and
    /// stopping leaves a word as a row of disconnected letters. Measured against GDI at 16ppem, an
    /// Arabic word came out with 38% too much ink; joined text is narrower and lighter than the
    /// isolated forms it is made of.</para>
    /// <para><b>Which letters join is asked of the FACE, not of a table written here.</b> A letter
    /// the face gives an 'init' form to is one that joins to what follows; one it gives a 'fina'
    /// form to joins to what precedes. That is the same fact Unicode's joining classes record, but
    /// read from the font that has to draw it -- so a face with its own idea of a letter is right
    /// by construction, and there is no transcribed table here to fall out of date. Only the
    /// combining marks are decided here, from the Unicode category, because they are transparent:
    /// a mark between two letters must not break their join.</para></summary>
    internal sealed class OpenTypeTextShaper : ITextShaper
    {
        private const string Arabic = "arab";

        /// <summary>The script tag to shape this text with, when it is one that needs reordering
        /// and the face actually knows that script.</summary>
        private static string? IndicTag(GsubTable gsub, string text)
        {
            foreach (char c in text)
            {
                if (IndicScript(c) is not { } found) continue;
                // Whether the face KNOWS the script, not whether it has some particular
                // feature. Gating on 'half' or 'pres' meant only Devanagari was ever shaped:
                // Nirmala UI's Tamil and Kannada register neither, so those scripts fell straight
                // through to the pass-through path -- and their ink still looked right, because
                // ink cannot see the order a reordering script gets wrong.
                if (gsub.HasScript(found.Tag2)) return found.Tag2;
                if (gsub.HasScript(found.Tag1)) return found.Tag1;
            }
            return null;
        }

        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            var glyphs = new List<int>(text.Length);
            foreach (char c in text) glyphs.Add(font.GlyphIndex(c));

            if (font is IOpenTypeShapingFont indicFont && indicFont.Gsub is GsubTable indicGsub
                && IndicTag(indicGsub, text) is string indicScript)
            {
                ShapeIndic(indicGsub, font, text, indicScript, glyphs);
            }
            else if (font is IOpenTypeShapingFont ot && ot.Gsub is GsubTable gsub
                     && gsub.HasFeature(Arabic, "init"))
            {
                // The order Uniscribe and HarfBuzz use: compose first, then decide each
                // letter's form, then the rules that speak about sequences.
                gsub.ApplyFeature(Arabic, "ccmp", glyphs);
                Join(gsub, font, text, glyphs);
                gsub.ApplyFeature(Arabic, "rlig", glyphs);
                gsub.ApplyFeature(Arabic, "calt", glyphs);
            }

            foreach (int gid in glyphs) output.Add(new ShapedGlyph(gid, font.Advance(gid)));
            Kern(font, output);
        }

        /// <summary>Replaces each letter with its positional form.</summary>
        private static void Join(GsubTable gsub, IShapingFont font, string text, List<int> glyphs)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (Transparent(text[i])) continue;
                int previous = Neighbour(text, i, -1), next = Neighbour(text, i, +1);
                // Joining is mutual: this letter must reach towards its neighbour AND the
                // neighbour back towards it, which is why both sides are asked of the face.
                bool toPrevious = JoinsForward(gsub, font, text, previous) && Has(gsub, font, text, i, "fina");
                bool toNext = JoinsBack(gsub, font, text, next) && Has(gsub, font, text, i, "init");
                string form = toPrevious ? (toNext ? "medi" : "fina")
                                         : (toNext ? "init" : "isol");
                glyphs[i] = gsub.Substitute(Arabic, form, glyphs[i]);
            }
        }

        // ---- Indic ----

        /// <summary>The scripts whose clusters have to be REORDERED, and the tags they use.
        /// <para>Every one of these writes at least one vowel sign before the consonant it
        /// follows in the text -- Devanagari's i-matra is the familiar one. No amount of
        /// substitution can express that, which is why a GSUB reader alone leaves these scripts
        /// wrong: the glyphs are right and they are in the wrong order.</para>
        /// <para>The 'v2' tag is tried first and the old one second, exactly as a shaping engine
        /// does; a face registers one or the other.</para></summary>
        private static readonly (int Lo, int Hi, string Tag2, string Tag1)[] IndicScripts =
        {
            (0x0900, 0x097F, "dev2", "deva"),   // Devanagari
            (0x0980, 0x09FF, "bng2", "beng"),   // Bengali
            (0x0A00, 0x0A7F, "gur2", "guru"),   // Gurmukhi
            (0x0A80, 0x0AFF, "gjr2", "gujr"),   // Gujarati
            (0x0B00, 0x0B7F, "ory2", "orya"),   // Oriya
            (0x0B80, 0x0BFF, "tml2", "taml"),   // Tamil
            (0x0C00, 0x0C7F, "tel2", "telu"),   // Telugu
            (0x0C80, 0x0CFF, "knd2", "knda"),   // Kannada
            (0x0D00, 0x0D7F, "mlm2", "mlym"),   // Malayalam
        };

        /// <summary>The vowel signs written BEFORE the consonant they follow.</summary>
        private static bool PreBase(char c) => c is 'ि'
            or 'ি' or 'ে' or 'ৈ'
            or 'ਿ' or 'િ'
            or 'େ'
            or 'ெ' or 'ே' or 'ை'
            or 'െ' or 'േ' or 'ൈ';

        /// <summary>The virama sits at the same offset in every one of these blocks.</summary>
        private static bool Virama(char c) => (c & 0x7F) == 0x4D && IndicScript(c) is not null;

        private static (int Lo, int Hi, string Tag2, string Tag1)? IndicScript(char c)
        {
            foreach ((int lo, int hi, string t2, string t1) in IndicScripts)
                if (c >= lo && c <= hi) return (lo, hi, t2, t1);
            return null;
        }

        private static bool Mark(char c)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category == UnicodeCategory.NonSpacingMark
                   || category == UnicodeCategory.SpacingCombiningMark;
        }

        /// <summary>Shapes an Indic run: one cluster at a time, because that is the unit these
        /// features are written about.
        /// <para>The order is a shaping engine's: form the conjuncts and half forms FIRST, then
        /// reorder what is left, then apply the presentation features to the result. Reordering
        /// first would move a matra past a consonant that is about to be merged into a conjunct,
        /// and the matra would end up in front of the wrong thing.</para></summary>
        private static void ShapeIndic(GsubTable gsub, IShapingFont font, string text,
                                       string script, List<int> glyphs)
        {
            glyphs.Clear();
            int at = 0;
            while (at < text.Length)
            {
                int end = ClusterEnd(text, at);
                var cluster = new List<int>(end - at);
                var chars = new List<char>(end - at);
                for (int i = at; i < end; i++)
                {
                    cluster.Add(font.GlyphIndex(text[i]));
                    chars.Add(text[i]);
                }

                foreach (string feature in BasicForms)
                    gsub.ApplyFeature(script, feature, cluster);
                ReorderPreBase(chars, cluster);
                foreach (string feature in Presentation)
                    gsub.ApplyFeature(script, feature, cluster);

                glyphs.AddRange(cluster);
                at = end;
            }
        }

        // The standard order. 'pref' and 'pstf' were missing from a first pass and Kannada --
        // which spells its conjuncts as post-base forms -- was the one script that stayed wrong,
        // at 1.209 while every other Indic script had come within 2% of GDI.
        private static readonly string[] BasicForms =
            { "ccmp", "locl", "nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "half",
              "pstf", "vatu", "cjct" };

        private static readonly string[] Presentation =
            { "pres", "abvs", "blws", "psts", "haln", "calt", "clig" };

        /// <summary>Moves a pre-base vowel sign to the front of its cluster.
        /// <para>Only while the cluster still HAS that many glyphs: the basic-forms features may
        /// have merged the consonants into a conjunct, and the sign then belongs in front of the
        /// conjunct rather than in front of a glyph that no longer exists.</para></summary>
        private static void ReorderPreBase(List<char> chars, List<int> cluster)
        {
            for (int i = 1; i < chars.Count && i < cluster.Count; i++)
            {
                if (!PreBase(chars[i])) continue;
                int glyph = cluster[i];
                cluster.RemoveAt(i);
                cluster.Insert(0, glyph);
            }
        }

        /// <summary>One orthographic syllable: a base, the consonants a virama binds to it, and
        /// the signs that hang off the result.</summary>
        private static int ClusterEnd(string text, int at)
        {
            int i = at + 1;
            while (i < text.Length)
            {
                if (Mark(text[i])) { i++; continue; }
                // A virama binds the next consonant into this same cluster.
                if (Virama(text[i - 1])) { i++; continue; }
                break;
            }
            return i;
        }

        private static int Neighbour(string text, int from, int step)
        {
            for (int i = from + step; i >= 0 && i < text.Length; i += step)
                if (!Transparent(text[i])) return i;
            return -1;
        }

        /// <summary>Marks hang off the letter before them and take no part in joining.</summary>
        private static bool Transparent(char c)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category == UnicodeCategory.NonSpacingMark
                   || category == UnicodeCategory.EnclosingMark
                   || category == UnicodeCategory.Format;
        }

        // A letter reaches towards what FOLLOWS it if the face has an initial form for it -- and
        // a medial form counts too, for a letter that only ever appears joined on both sides.
        private static bool JoinsForward(GsubTable gsub, IShapingFont font, string text, int at)
            => at >= 0 && (Has(gsub, font, text, at, "init") || Has(gsub, font, text, at, "medi"));

        // ... and towards what PRECEDES it if it has a final form.
        private static bool JoinsBack(GsubTable gsub, IShapingFont font, string text, int at)
            => at >= 0 && (Has(gsub, font, text, at, "fina") || Has(gsub, font, text, at, "medi"));

        private static bool Has(GsubTable gsub, IShapingFont font, string text, int at, string form)
        {
            int gid = font.GlyphIndex(text[at]);
            return gid > 0 && gsub.Substitute(Arabic, form, gid) != gid;
        }

        /// <summary>The pair adjustments, exactly as the kerning shaper applies them.</summary>
        internal static void Kern(IShapingFont font, List<ShapedGlyph> output)
        {
            for (int i = 0; i < output.Count - 1; i++)
            {
                if (!font.TryGetKerning(output[i].GlyphId, output[i + 1].GlyphId, out float k)) continue;
                ShapedGlyph g = output[i];
                output[i] = new ShapedGlyph(g.GlyphId, g.Advance, g.XOffset, g.YOffset, k);
            }
        }
    }
}
