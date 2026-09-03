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

        public void Shape(IShapingFont font, string text, List<ShapedGlyph> output)
        {
            output.Clear();
            var glyphs = new List<int>(text.Length);
            foreach (char c in text) glyphs.Add(font.GlyphIndex(c));

            if (font is IOpenTypeShapingFont ot && ot.Gsub is GsubTable gsub
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
