// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s Graphics.DrawString, as gdiplus.dll 10.0.26100.9444 draws it on its FAST path
// (FastTextImager). GDI+ text is neither GDI text nor DirectWrite's alpha texture: it lays the
// string out itself, asks DirectWrite for advances and for 1-bit 6x1 glyph bitmaps, and then runs
// GDI's ClearType back end, which is statically linked into gdiplus.dll, with its own gamma blend.
// Read out of the binaries (addresses are Ghidra VAs, image base 0x180000000):
//
//   FastTextImager::Initialize @1800393c0 / DrawString @180038298   which strings take this path
//       (see Layout's refusals) and the realization flags per TextRenderingHint.
//   FastDrawGlyphsGridFit @180038d58, FastAdjustGlyphPositionsProportional @180038850,
//   GetDeviceBaselineOrigin @1800391b0   the grid-fitted layout: nominal advances tracked by 1.03,
//       em/6 margins, hinted "GDI natural" advances, the proportional spread of the difference,
//       the origin x rounded and the baseline y = top + device ascent, NOT rounded.
//   FastDrawGlyphsNominal @1800ebaa0   fixed-pitch faces and the hints that do not fit.
//   bGetDEVICEMETRICS @1800a2c08, QuantizeTransform @1800a1f50, SearchVdmxTable @1801ed270   the
//       device ascent: usWinAscent scaled and rounded, widened to the VDMX entry.
//   GetGlyphPos @180023d18   x snapped to a sixth of a pixel; the glyph is DirectWrite's 6x1
//       bi-level bitmap of the fit made with scaler word 1 (ClearType, no compatible widths).
//   fsc_OverscaleToSubPixel @1800a33f0, ulClearTypeFilter @1800a3568   six samples to three
//       two-sample counts, then the 243-entry filter, right to left.
//   DpOutputClearTypeOptimizedSpan::RenderGlyph @1800257f0   overlapping glyphs: level sums.
//   ScanOperation::CTBlendSolid @1800c7f70   the blend, through the TextContrast tables.
//   AntiAlias / AntiAliasGridFit: DirectWrite's 4x4 bitmaps (raster type 4) of the fit with word
//       0x81 at a quarter-pixel phase, combined by max, through TextColorGammaTable and
//       Blend_sRGB_sRGB; nominal/design advances for AntiAlias, GDI-classic ones for the grid fit.
//
// The GDI-natural advances and side bearings, which the spec took from DirectWrite, are derived
// here from the port's scaler (NaturalMetrics) and were checked against DirectWrite's answers
// over eight faces, four styles and 6..24pt: 35,862 of 35,910 advances exact. Layouts were checked
// against the glyph ids and origins gdiplus.dll itself handed to DrawPlacedGlyphs (every string
// the fast imager took, exact), pixels against GDI+'s own Bitmap rendering.
//
// The tables are in GdiPlusText.Tables.cs. Everything else comes from the font file through this
// port's own scaler, so it is the same on every head.
//
// NOT MODELLED: FullTextImager (Line Services), which GDI+ falls back to for tabs, CR/LF and
// multi-line text, wrapping, right-to-left and vertical formats, tab stops, complex scripts,
// hot-key prefixes and italic overhang past the margins; and the bi-level realizations
// (SingleBitPerPixel[GridFit], AntiAliasGridFit at sizes the gasp does not grey, ClearType at sizes
// drawn from embedded bitmaps, Marlett), simulated bold, and a simulated italic under the
// antialiased hints. Layout returns null for all of those and the caller keeps drawing them as it
// did before (GDI's pipeline).
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static partial class GdiPlusText
    {
        /// <summary>System.Drawing.Text.TextRenderingHint's values.</summary>
        internal const int HintSystemDefault = 0, HintSingleBitPerPixelGridFit = 1, HintSingleBitPerPixel = 2,
                           HintAntiAliasGridFit = 3, HintAntiAlias = 4, HintClearTypeGridFit = 5;

        /// <summary>StringFormatFlags GDI+'s fast imager reads.</summary>
        internal const int FlagRightToLeft = 0x1, FlagVertical = 0x2, FlagNoFitBlackBox = 0x4,
                           FlagNoClip = 0x4000, FlagMeasureTrailingSpaces = 0x800;

        /// <summary>The scaler word DirectWrite hands its rasterizer for GDI+'s glyphs and for the
        /// "GDI natural" advances (flags 0x21 through TrueTypeRasterizer::NewTransform): ClearType,
        /// nothing else.</summary>
        internal const int NaturalScalerWord = 1;

        /// <summary>The same word for a glyph turned a quarter (m00 == 0): fs__NewTransformation
        /// @180070bc0 flips bit 2, so the scaler oversamples the glyph's y (the device x).</summary>
        internal const int SidewaysScalerWord = NaturalScalerWord ^ 4;

        /// <summary>The word for GDI+'s 4x4 antialiased glyph bitmaps: 0x81, ClearType with bit 7
        /// ("ClearType grey") -- 1,728 of 1,728 test glyphs exact against DirectWrite's own (with the
        /// face's dropout control), where the natural word 1 gets 96 of 168. (The bi-level raster
        /// type 0 is not modelled: no word tried reproduces DirectWrite's fits, e.g. Segoe UI 'b'
        /// and 'x' at 12ppem come out a pixel narrower.)</summary>
        internal const int GreyScalerWord = 0x81;

        /// <summary>GDI+'s default TextContrast (DpContext).</summary>
        internal const int DefaultContrast = 4;

        /// <summary>A string laid out the way FastTextImager lays it out, in the caller's space.</summary>
        internal sealed class Run
        {
            public ushort[] Glyphs = Array.Empty<ushort>();
            /// <summary>The first glyph's x before the device rounding (see <see cref="RoundOrigin"/>).</summary>
            public float OriginX;
            /// <summary>Grid-fitted layout: the origin is floor(x + 0.5) on the device, plus
            /// <see cref="LeadOffset"/>; the nominal layout leaves it where it falls.</summary>
            public bool RoundOrigin;
            public float LeadOffset;
            /// <summary>Glyph i + 1 sits Advances[i] right of glyph i (float sums, as GDI+ makes them).</summary>
            public float[] Advances = Array.Empty<float>();
            /// <summary>The baseline, not rounded.</summary>
            public float OriginY;
            /// <summary>The layout rectangle as a clip, when the text spills out of it.</summary>
            public bool HasClip;
            public float ClipX, ClipY, ClipW, ClipH;
            public float Em;
            /// <summary>Render mode (GpFaceRealization +0x1c): 5 ClearType (6x1 glyphs, filter,
            /// CTBlendSolid); 3 and 4 antialiased (4x4 glyphs, coverage by max, the gamma table);
            /// 1 bi-level grid-fitted, 2 bi-level unfitted (1x1 glyphs, the brush where they are set).</summary>
            public int Mode;
            /// <summary>The TextRenderingHint asked for (SystemDefault resolved).</summary>
            public int Hint;
            /// <summary>ulClearTypeFilter's other table (Courier New and its kin).</summary>
            public bool FixedFilter;
            /// <summary>Graphics.TextContrast, 0..12: which TextContrast tables the blend uses.</summary>
            public int Contrast = DefaultContrast;
            /// <summary>The world-to-device axis scale the run was laid out under (m11, m22): the
            /// origin is in world units, the advances in device pixels.</summary>
            public float Sx = 1f, Sy = 1f;
            /// <summary>The 6x1 glyph of the run's i'th glyph at the device size it is drawn at.</summary>
            public NaturalClearType.GlyphBits GlyphBits(TrueTypeFont font, int i)
                => Sx == 1f && Sy == 1f ? Glyph(font, Glyphs[i], Em)
                 : Sx == Sy ? Glyph(font, Glyphs[i], Em * Sx)
                 : Glyph(font, Glyphs[i], AxisPpem(Em * Sx), AxisPpem(Em * Sy));
        }

        /// <summary>The face a family and style (1 bold, 2 italic) resolves to, loaded exactly as the
        /// renderer loads a string run's (WgpuSceneRenderer.LoadFamily) so the layout and the drawing
        /// see the same glyphs. Null when it is not a TrueType face.</summary>
        internal static TrueTypeFont? Face(string family, int style)
        {
            if (string.IsNullOrEmpty(family)) return null;
            string key = family + "|" + (style & 3);
            lock (s_faces)
            {
                if (s_faces.TryGetValue(key, out TrueTypeFont? f)) return f;
                f = WgpuSceneRenderer.LoadFamily(family, style & 3) as TrueTypeFont;
                s_faces[key] = f;
                return f;
            }
        }

        private static readonly Dictionary<string, TrueTypeFont?> s_faces = new();

        private static int Floor(float v) => (int)MathF.Floor(v);
        private static long CDiv(long a, long b) => a / b;   // C division, toward zero

        /// <summary>After a <see cref="Layout"/> that returned null: whether that was GDI+'s own
        /// refusal (FastTextImager::Initialize / DrawString answering status 6, the string goes to
        /// FullTextImager) rather than a fast-imager string this port does not model.</summary>
        [ThreadStatic] internal static bool LastFull;

        /// <summary>Whether a simulated bold widens the DESIGN advances (a DirectWrite simulated
        /// face) or only the realization's (GDI+ emboldening an unsimulated one); the GDI+ port
        /// sets it from its model of DirectWrite's families. Unset, every simulation widens.</summary>
        internal static Func<TrueTypeFont, bool>? DesignBoldWidens;

        /// <summary>FastTextImager's decision and layout. Null where GDI+ would take its full imager
        /// (or where this port does not model the realization asked for): the caller then draws the
        /// string as it did before. An empty Run (no glyphs) is a string GDI+ draws nothing for.
        /// <paramref name="typographic"/> is StringFormat.GenericTypographic's tracking and margins
        /// (a draw with no StringFormat has the default ones). <paramref name="align"/> and
        /// <paramref name="lineAlign"/> are StringAlignment (0 near, 1 centre, 2 far);
        /// <paramref name="hint"/> is the TextRenderingHint.</summary>
        internal static Run? Layout(TrueTypeFont font, string family, float sizePt, string text,
                                    float x, float y, float rw, float rh,
                                    int formatFlags, bool typographic, int align, int lineAlign,
                                    bool hotkeyPrefix, int hint, float dpi = 96f, bool biLevel = false,
                                    float sx = 1f, float sy = 1f, float wrapWidth = float.NaN)
        {
            LastFull = false;
            if (string.IsNullOrEmpty(text) || font is null) return null;
            // FastTextImager::Initialize@1800393c0 takes a positive axis scale: m11 > 0, m22 != 0
            // (only m22 > 0 is modelled). The layout stays in world units (the rectangle, the em,
            // the margins, the line box) and every DEVICE quantity -- scale16, the hinted
            // advances, the margin room +0x90, the side bearings, the device ascent -- is the
            // world one through m11 or m22; the caller takes the world origin to the device.
            if (!(sx > 0f) || sy == 0f) { LastFull = true; return null; }
            if (!(sy > 0f)) return null;
            bool scaled = sx != 1f || sy != 1f;
            if (hint == HintSystemDefault) hint = HintClearTypeGridFit;   // a ClearType desktop
            // The realizations: ClearType (5), the 4x4 antialiased ones (3 grid-fitted, 4 not), and
            // -- for a caller that draws them (biLevel) -- the bi-level ones (1 grid-fitted, 2 not).
            if (hint < HintSingleBitPerPixelGridFit || hint > HintClearTypeGridFit) return null;
            if (!biLevel && (hint == HintSingleBitPerPixelGridFit || hint == HintSingleBitPerPixel)) return null;
            // Format flags & 0x40000003: right to left, vertical. MeasureTrailingSpaces changes the
            // trailing-space handling, which is not modelled.
            if ((formatFlags & (FlagRightToLeft | FlagVertical | 0x40000000)) != 0)
                { LastFull = (formatFlags & (FlagRightToLeft | FlagVertical | 0x40000000)) != 0; return null; }
            // A simulated bold is DirectWrite's simulated face, whose advances it widens itself:
            // the design advance of every outlined glyph by round(upem / 50), the GDI-compatible
            // natural ones by a device pixel (blanks included; the classic ones carry GDI's own).
            bool simBold = font.SynthesizesBold;
            bool designBold = simBold && (DesignBoldWidens?.Invoke(font) ?? true);
            // A simulated oblique is modelled for ClearType and bi-level only (below): DirectWrite's
            // 4x4 glyphs of a sheared face are not the upright fit sheared (Tahoma, Microsoft Sans
            // Serif italic).

            // The device's resolution: 96 for a window, the printer's for a printed page, where GDI+
            // runs the same imager at the device's em (and the caller's rectangle is in its pixels).
            float em = sizePt * (dpi / 72f);
            if (!(em > 0f) || em > 1000f) return null;
            int ppemRound = Floor(em + 0.5f);
            // Realize: under ClearType a face DirectWrite draws from its embedded bitmaps at this size,
            // and Marlett, are realized bi-level with GDI-classic widths; under AntiAliasGridFit a
            // size the 'gasp' does not grey is bi-level. Not modelled.
            // GpFaceRealization::Realize turns the hint's flag word into the render mode (+0x1c) and
            // the advance type (+0x20): 1 bi-level grid-fitted (GDI classic advances), 2 bi-level
            // not fitted (design), 3 and 4 antialiased (classic / design), 5 ClearType (GDI natural).
            int mode = hint;
            // Realize@1800a22a0 asks about embedded bitmaps only for a matrix with m11 == +-m22.
            bool squareXform = sx == sy;
            if (squareXform && scaled) ppemRound = Floor(em * sx + 0.5f);
            if (hint == HintClearTypeGridFit
                && ((squareXform && font.EmbeddedBitmapCount(ppemRound) > 100) || string.Equals(family, "Marlett", StringComparison.OrdinalIgnoreCase)))
                mode = 1;   // flags & ~0x410000 | 0x800000
            if (hint == HintAntiAliasGridFit && !font.GaspDoGray(ppemRound)) mode = 1;   // flags & ~0x10000
            if (mode <= 2 && !biLevel) return null;
            // Under a scale only the ClearType realization is modelled (the stretched 6x1 fit).
            if (scaled && mode != 5) return null;
            if (font.SynthesizesOblique && (mode == 3 || mode == 4)) return null;

            // CharacterAttributes bit 0x80 sends the string to the full imager: every control
            // character (tab, CR, LF), the complex scripts, and a hot-key prefix.
            foreach (char c in text)
            {
                if (c < 0x20 || (c >= 0x590 && c < 0x1E00) || char.IsSurrogate(c)) { LastFull = true; return null; }
                if (hotkeyPrefix && c == '&') return null;
            }

            int n = text.Length;
            var gids = new int[n];
            for (int i = 0; i < n; i++)
            {
                gids[i] = font.GlyphIndex(text[i]);
                // A character the face lacks would be linked to another face; not modelled.
                if (gids[i] <= 0) { LastFull = true; return null; }
            }

            int upem = font.UnitsPerEmForHinting;
            if (upem <= 0) return null;
            float lm, rm, tracking;
            bool noBlackBox = (formatFlags & FlagNoFitBlackBox) != 0;
            if (typographic) { lm = rm = 0f; tracking = 1f; }
            else { lm = rm = em * (1f / 6f); tracking = 1.03f; }

            var nom = new int[n];
            for (int i = 0; i < n; i++)
            {
                int a = font.DesignAdvance(gids[i]);
                if (designBold && font.DesignContours(gids[i]).Count > 0) a += Floor(upem / 50f + 0.5f);
                nom[i] = tracking != 1f ? Floor(a * tracking + 0.5f) : a;
            }
            int space = font.GlyphIndex(' ');
            float scale = em / upem * sx;       // +0xb0: (em / upem) * m11
            float scaleY = em / upem * sy;      // +0xbc
            int scale16 = Floor(scale * 65536f + 0.5f);

            // Initialize: the nominal width of ALL glyphs; the wrap test uses it before trailing
            // spaces go.
            long sumNom = 0;
            foreach (int a in nom) sumNom += a;
            float totalNom = (float)sumNom * em / upem;
            float ww = float.IsNaN(wrapWidth) ? rw : wrapWidth;   // +0xd0: 0 for NoWrap without trimming
            if (ww > 0f && ww < totalNom + lm + rm)
                { LastFull = true; return null; }   // it would wrap
            while ((formatFlags & FlagMeasureTrailingSpaces) == 0 && n > 0 && gids[n - 1] == space)
            {
                totalNom -= nom[n - 1] * em / upem;
                n--;
            }
            if (n == 0) return new Run { Em = em, Mode = mode, Hint = hint };

            float cellH = (float)(CellAscent(font) + CellDescent(font)) * em / upem;
            if (lm != 0f) cellH = em * 0.125f + cellH;
            // QuantizeTransform widens to VDMX only when the realization flags & 0x340000 are clear:
            // not for AntiAlias (0x118000) or SingleBitPerPixel (0x48000).
            DeviceAscentDescent(font, scaleY, out int ascDev, out int descDev,
                                vdmx: hint != HintAntiAlias && hint != HintSingleBitPerPixel);

            // FastDrawGlyphsNominal for a fixed-pitch face and for the hints that do not fit
            // (IsGridFittedTextRealizationMethod: 1, 3 and 5 fit).
            bool nominal = font.IsFixedPitch || hint == HintAntiAlias || hint == HintSingleBitPerPixel;
            var run = new Run { Em = em, Mode = mode, Hint = hint, FixedFilter = font.GdiContrastPalette, Sx = sx, Sy = sy };
            // The advance type: 2 GDI natural (ClearType), 1 GDI classic (the other grid-fitted
            // realizations, the bi-level one a ClearType face falls back to included), 0 design.
            int advType = mode == 5 ? 2 : hint == HintAntiAlias || hint == HintSingleBitPerPixel ? 0 : 1;

            // The advance type the realization asks for: GDI natural for ClearType, GDI classic for
            // AntiAliasGridFit, design for AntiAlias -- in design units.
            var adv = new int[n]; var lsb = new int[n]; var rsb = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (advType == 2 && scaled) NaturalMetrics(font, gids[i], em, sx, sy, out adv[i], out lsb[i], out rsb[i]);
                else if (advType == 2) NaturalMetrics(font, gids[i], em, out adv[i], out lsb[i], out rsb[i]);
                else if (advType == 1) ClassicMetrics(font, gids[i], em, out adv[i], out lsb[i], out rsb[i]);
                else DesignMetrics(font, gids[i], out adv[i], out lsb[i], out rsb[i]);
                if (designBold && advType == 0 && font.DesignContours(gids[i]).Count > 0) adv[i] += Floor(upem / 50f + 0.5f);
            }
            // The simulation's extra device pixel of a GDI natural advance, in 1/16 px.
            int bold16 = simBold && advType == 2 ? 16 : 0;

            // The black-box test (GetGlyphStringSidebearings): an overhang past a margin needs the
            // full imager.
            int lmin = 0, rmin = 0;
            if (!noBlackBox)
            {
                int lim = (ascDev + descDev) * 32;
                int cum = 0; lmin = lim;
                for (int i = 0; i < n; i++)
                {
                    if (cum >= lim) break;
                    lmin = Math.Min(lmin, cum + (int)(lsb[i] * scale * 16f));
                    cum += (int)(adv[i] * scale * 16f) + bold16;
                }
                cum = 0; rmin = lim;
                for (int i = n - 1; i >= 0; i--)
                {
                    if (cum >= lim) break;
                    rmin = Math.Min(rmin, cum + (int)(rsb[i] * scale * 16f));
                    cum += (int)(adv[i] * scale * 16f) + bold16;
                }
                if ((float)(-lmin) > lm * sx * 16f || (float)(-rmin) > rm * sx * 16f) { LastFull = true; return null; }
            }

            if (nominal)
            {
                // FastDrawGlyphsNominal: every glyph, leading spaces included, from an unrounded
                // origin, x += nom * scale16 / 65536.
                Origin(x, y, align, lineAlign, rw, rh, totalNom, lm, rm, cellH, 0f, ascDev, out float nx, out float ny, sy);
                run.Glyphs = new ushort[n];
                for (int i = 0; i < n; i++) run.Glyphs[i] = (ushort)gids[i];
                run.Advances = new float[Math.Max(0, n - 1)];
                for (int i = 0; i < n - 1; i++)
                    run.Advances[i] = (float)unchecked((int)((long)nom[i] * scale16)) * 1.52587890625e-05f;
                run.OriginX = nx; run.OriginY = ny; run.RoundOrigin = false;
                Clip(run, x, y, align, lineAlign, rw, rh, totalNom, lm, rm, cellH, (formatFlags & FlagNoClip) != 0);
                return run;
            }

            var hint16 = new int[n];
            for (int i = 0; i < n; i++) hint16[i] = Floor(adv[i] * scale + 0.5f) * 16 + bold16;

            // The trailing margin available to absorb hinting growth (+0x90).
            float lmx = lm * sx, rmx = rm * sx;    // the margins on the device (* +0xd4)
            int m90 = align == 0 ? Floor(rmx) : align == 1 ? Floor(Math.Min(lm, rm) * 2f * sx) : Floor(lmx);
            if (!noBlackBox)
            {
                if (align == 0) m90 = Floor(rmx) + (rmin >> 4);
                else if (align == 1) m90 = 2 * Math.Min(Floor(lmx) + (lmin >> 4), Floor(rmx) + (rmin >> 4));
                else m90 = Floor(lmx) + (lmin >> 4);
            }

            // ---- FastAdjustGlyphPositionsProportional ----
            int lead = 0;
            while (lead < n && gids[lead] == space) lead++;
            int tail = 0;
            if (lead < n)
                for (int k = n; k > lead && gids[k - 1] == space; k--) tail++;
            int end = n - tail;
            long spc = 0, spH = 0, spN = 0, nsN = 0, nsH = 0;
            for (int i = lead; i < end; i++)
            {
                if (gids[i] == space) { spc++; spH += hint16[i]; spN += nom[i]; }
                else { nsN += nom[i]; nsH += hint16[i]; }
            }
            int nmid = end - lead;
            long leadOff = lead > 0 ? ((long)nom[0] * scale16 * lead) >> 12 : 0;
            long D = CDiv((spN + nsN) * scale16, 4096) - (spH + nsH) + 8;
            long delta = CDiv(D, 16);
            long absorbed = 0;
            if (delta < 0) absorbed = Math.Max(delta, -m90);
            else if (delta > 0) absorbed = Math.Min(delta, ((long)upem * scale16) >> 16);
            long rem = delta - absorbed;
            float off94 = 0f;
            if (absorbed != 0 && align == 1) off94 = (float)absorbed / (sx + sx);   // FastAdjustGlyphPositionsProportional: device px back to world
            else if (absorbed != 0 && align == 2) off94 = (float)absorbed / sx;

            var outAdv = new List<long>(nmid);
            if (rem == 0 || nmid < 2)
            {
                long cum = 0, prev = 0;
                for (int i = lead; i < end; i++)
                {
                    cum += hint16[i]; long p = (cum + 8) >> 4; outAdv.Add(p - prev); prev = p;
                }
            }
            else
            {
                long spNpx = (spN * scale16 + 0x8000) >> 16;
                long minSp = ((((long)upem * scale16 + 0x50000) * 0x2aaaaaab) >> 32) >> 16;
                bool done = false;
                if (rem <= spNpx)
                {
                    long limit = Math.Max(CDiv(spNpx, 2), minSp * spc);
                    if (limit - spNpx <= rem && spc > 0)
                    {
                        long per = CDiv((spH + rem * 16) * 16, spc);
                        long cum = 0, prev = 0;
                        for (int i = lead; i < end; i++)
                        {
                            cum += gids[i] == space ? per : (long)hint16[i] * 16;
                            long p = (cum + 0x80) >> 8; outAdv.Add(p - prev); prev = p;
                        }
                        done = true;
                    }
                }
                if (!done)
                {
                    long spW;
                    if (spc == 0) spW = 0;
                    else
                    {
                        spW = rem >= 0 ? CDiv(spNpx * 2, spc) : minSp;
                        rem = CDiv(spH, 16) - spW * spc + rem;
                    }
                    int runs = 0;
                    for (int j = 0; j < nmid;)
                    {
                        if (gids[lead + j] == space)
                        {
                            while (j < nmid && gids[lead + j] == space) j++;
                            runs++;
                        }
                        else
                            while (j < nmid && gids[lead + j] != space) j++;
                    }
                    long gaps = nmid - runs - spc - 1;
                    long bas, thr, sgn;
                    if (gaps < 1)
                    {
                        if (spc > 0) spW += CDiv(rem, spc);
                        bas = 0; thr = 0; sgn = 0;
                    }
                    else
                    {
                        bas = CDiv(rem, gaps);
                        if (rem < 0) { sgn = -1; thr = (rem - bas * gaps) + gaps; }
                        else if (rem > 0) { sgn = 1; thr = gaps - (rem - bas * gaps); }
                        else { sgn = 0; thr = 0; }
                    }
                    long cnt = 0;
                    for (int j = 0; j < nmid; j++)
                    {
                        int i = lead + j;
                        if (gids[i] == space) outAdv.Add(spW);
                        else if (j + 1 >= nmid || gids[i + 1] == space) outAdv.Add(CDiv(hint16[i], 16));
                        else
                        {
                            long ex = cnt < thr ? 0 : sgn;
                            cnt++;
                            outAdv.Add(ex + CDiv(hint16[i], 16) + bas);
                        }
                    }
                }
            }

            // ---- GetDeviceBaselineOrigin + FastDrawGlyphsGridFit ----
            Origin(x, y, align, lineAlign, rw, rh, totalNom, lm, rm, cellH, off94, ascDev, out float ox, out float oy, sy);
            run.Glyphs = new ushort[nmid];
            for (int j = 0; j < nmid; j++) run.Glyphs[j] = (ushort)gids[lead + j];
            run.Advances = new float[Math.Max(0, nmid - 1)];
            for (int j = 0; j < nmid - 1; j++) run.Advances[j] = outAdv[j];
            run.OriginX = ox; run.OriginY = oy; run.RoundOrigin = true;
            run.LeadOffset = (float)(leadOff * 0.0625);
            Clip(run, x, y, align, lineAlign, rw, rh, totalNom, lm, rm, cellH, (formatFlags & FlagNoClip) != 0);
            return run;
        }

        /// <summary>Each glyph's device x, from the run's origin already moved to the device:
        /// float sums, exactly as GDI+ forms them.</summary>
        internal static float[] GlyphXs(Run run, float deviceOriginX)
        {
            int n = run.Glyphs.Length;
            var xs = new float[n];
            if (n == 0) return xs;
            float x0 = run.RoundOrigin ? (float)Floor(deviceOriginX + 0.5f) + run.LeadOffset : deviceOriginX;
            xs[0] = x0;
            for (int i = 1; i < n; i++) xs[i] = xs[i - 1] + run.Advances[i - 1];
            return xs;
        }

        /// <summary>IDWriteFontFace1::GetGdiCompatibleGlyphAdvances / -Metrics with useGdiNatural,
        /// in design units. DirectWrite measures GDI-compatibly at the WHOLE ppem: the advance is the
        /// scaler's phantom span under word 1 (fitted whatever 'gasp' says, as the bitmaps are; the
        /// scaled advance in 26.6 for a glyph with no program), rounded to whole pixels and turned back into design units at that
        /// ppem -- Arial's space at 20pt is 569 units, 7.41 pixels at the 26.67 em but 7.50 at 27,
        /// and DirectWrite answers 607, eight pixels. The side bearings are the 6x1 bitmap's: its ink
        /// in sixths of a pixel from the origin and from the advance.</summary>
        internal static void NaturalMetrics(TrueTypeFont font, int gid, float em, out int advDu, out int lsbDu, out int rsbDu)
        {
            int ppem = Floor(em + 0.5f);
            if (ppem < 1) ppem = 1;
            var key = (gid, ppem);
            var cache = s_metrics.GetValue(font, _ => new Dictionary<(int, int), (int, int, int)>());
            lock (cache)
                if (cache.TryGetValue(key, out var hit)) { (advDu, lsbDu, rsbDu) = hit; return; }

            int upem = font.UnitsPerEmForHinting;
            int design = font.DesignAdvance(gid);
            bool hasOutline = font.TryGetDesignXExtent(gid, out _, out _);
            if (!font.TryGetDWriteFittedSpan64(gid, ppem, NaturalScalerWord, out int span64))
                span64 = (int)MathF.Round(design * 64f * ppem / upem, MidpointRounding.AwayFromZero);
            // A glyph with no outline (the space) rounds up from 30/64, not 32/64: measured over
            // eight faces at 6..24pt, 29/64 stays down (Microsoft Sans Serif at 13ppem, 3.453 -> 3)
            // and 30/64 goes up (Segoe UI at 9ppem, 2.465 -> 3; Verdana Bold at 16, 5.469 -> 6).
            int px = (span64 + (hasOutline ? 32 : 34)) >> 6;
            advDu = (int)Math.Floor(px * (double)upem / ppem + 0.5);
            if (!OutlineXExtent(font, gid, ppem, out float x0, out float x1)) { lsbDu = 0; rsbDu = advDu; }
            else
            {
                // The box on the sample grid, each edge to the nearer sample boundary (a tie goes
                // inward on the left, outward on the right). Checked against DirectWrite's own
                // answers over eight faces, 6..24pt: within one design unit for all but ~2%.
                int left = (int)MathF.Ceiling(x0 * 6f - 0.5f), right = (int)MathF.Floor(x1 * 6f + 0.5f);
                lsbDu = (int)Math.Floor(left * (double)upem / (6.0 * ppem) + 0.5);
                rsbDu = (int)Math.Floor((6 * px - right) * (double)upem / (6.0 * ppem) + 0.5);
            }
            lock (cache)
            {
                if (cache.Count > 8192) cache.Clear();
                cache[key] = (advDu, lsbDu, rsbDu);
            }
        }

        /// <summary>NaturalMetrics under the world-to-device axis scale (sx, sy) GDI+ hands
        /// GetGdiCompatibleGlyphAdvances/-Metrics as the transform. A square one is the plain
        /// size em * sx (MakeRasterizerTransform rounds it to a whole ppem); a stretched one is
        /// fitted at each axis' whole ppem (<see cref="Glyph(TrueTypeFont, int, int, int)"/>) and
        /// its pixels go back to design units through the UNROUNDED device em: Arial Bold Italic
        /// 'H' at em 16 under (1.1014, 1.4904) is 13 px, 13 * 2048 / 17.622 = 1511 units, where
        /// the square 18 ppem gives 1479.</summary>
        internal static void NaturalMetrics(TrueTypeFont font, int gid, float em, float sx, float sy,
                                            out int advDu, out int lsbDu, out int rsbDu)
        {
            float ex = em * sx, ey = em * sy;
            int ppx = AxisPpem(ex), ppy = AxisPpem(ey);
            if (sx == sy || ppx == ppy) { NaturalMetrics(font, gid, ex, out advDu, out lsbDu, out rsbDu); return; }
            int upem = font.UnitsPerEmForHinting;
            int design = font.DesignAdvance(gid);
            bool hasOutline = font.TryGetDesignXExtent(gid, out _, out _);
            int sxs = TrueTypeInterpreter.StretchPpemX, sys = TrueTypeInterpreter.StretchPpemY;
            TrueTypeInterpreter.StretchPpemX = ppx; TrueTypeInterpreter.StretchPpemY = ppy;
            try
            {
                int ppem = Math.Max(ppx, ppy);
                if (!font.TryGetDWriteFittedSpan64(gid, ppem, NaturalScalerWord, out int span64))
                    span64 = (int)MathF.Round(design * 64f * ppx / upem, MidpointRounding.AwayFromZero);
                int px = (span64 + (hasOutline ? 32 : 34)) >> 6;
                advDu = (int)Math.Floor(px * (double)upem / ex + 0.5);
                if (!OutlineXExtent(font, gid, ppem, out float x0, out float x1)) { lsbDu = 0; rsbDu = advDu; }
                else
                {
                    int left = (int)MathF.Ceiling(x0 * 6f - 0.5f), right = (int)MathF.Floor(x1 * 6f + 0.5f);
                    lsbDu = (int)Math.Floor(left * (double)upem / (6.0 * ex) + 0.5);
                    rsbDu = (int)Math.Floor((6 * px - right) * (double)upem / (6.0 * ex) + 0.5);
                }
            }
            finally { TrueTypeInterpreter.StretchPpemX = sxs; TrueTypeInterpreter.StretchPpemY = sys; }
        }

        /// <summary>IDWriteFontFace::GetDesignGlyphMetrics: hmtx and the glyf box.</summary>
        internal static void DesignMetrics(TrueTypeFont font, int gid, out int advDu, out int lsbDu, out int rsbDu)
        {
            advDu = font.DesignAdvance(gid);
            if (font.TryGetDesignXExtent(gid, out int xMin, out int xMax)) { lsbDu = xMin; rsbDu = advDu - xMax; }
            else { lsbDu = 0; rsbDu = advDu; }
        }

        /// <summary>The same with useGdiNatural off (GDI_CLASSIC), for the grid-fitted hints other than
        /// ClearType: GDI's own compatible advance at the whole ppem (hdmx, or the bi-level program),
        /// back in design units; the side bearings from the bi-level fit's box in whole pixels.</summary>
        internal static void ClassicMetrics(TrueTypeFont font, int gid, float em, out int advDu, out int lsbDu, out int rsbDu)
        {
            int ppem = Floor(em + 0.5f);
            if (ppem < 1) ppem = 1;
            int upem = font.UnitsPerEmForHinting;
            int px = (int)MathF.Round(font.DeviceAdvance(gid, ppem));
            advDu = (int)Math.Floor(px * (double)upem / ppem + 0.5);
            if (!NaturalClearType.TryGetGdiClassicOutline(font, gid, ppem, out List<PathFigure> figures, out _)
                || !XExtent(figures, out float x0, out float x1)) { lsbDu = 0; rsbDu = advDu; return; }
            int left = (int)MathF.Ceiling(x0 * 6f - 0.5f), right = (int)MathF.Floor(x1 * 6f + 0.5f);
            lsbDu = (int)Math.Floor(left * (double)upem / (6.0 * ppem) + 0.5);
            rsbDu = (int)Math.Floor((6 * px - right) * (double)upem / (6.0 * ppem) + 0.5);
        }

        /// <summary>The x extent of the outline the 6x1 bitmap is scanned from, in pixels.</summary>
        private static bool OutlineXExtent(TrueTypeFont font, int gid, float ppem, out float x0, out float x1,
                                           int word = NaturalScalerWord)
        {
            x0 = x1 = 0f;
            if (!font.TryGetDWriteFittedOutline(gid, ppem, word, out List<PathFigure> figures, out _))
                if (!font.TryGetScaledOutline(gid, ppem, out figures)) return false;
            return XExtent(figures, out x0, out x1);
        }

        private static bool XExtent(List<PathFigure> figures, out float x0, out float x1)
        {
            x0 = float.MaxValue; x1 = float.MinValue;
            foreach (PathFigure f in figures)
            {
                Take(f.Start.X, ref x0, ref x1);
                foreach (PathSegment sg in f.Segments)
                    switch (sg)
                    {
                        case LineSegment l: Take(l.Point.X, ref x0, ref x1); break;
                        case QuadraticBezierSegment q: Take(q.Control.X, ref x0, ref x1); Take(q.Point.X, ref x0, ref x1); break;
                        case CubicBezierSegment c: Take(c.Control1.X, ref x0, ref x1); Take(c.Control2.X, ref x0, ref x1); Take(c.Point.X, ref x0, ref x1); break;
                    }
            }
            return x0 <= x1;
            static void Take(float v, ref float lo, ref float hi) { if (v < lo) lo = v; if (v > hi) hi = v; }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrueTypeFont, Dictionary<(int, int), (int, int, int)>>
            s_metrics = new();

        /// <summary>bGetDEVICEMETRICS + QuantizeTransform: the win ascent and descent scaled
        /// (FixMul, half away from zero), widened to the VDMX entry for this ppem.</summary>
        /// <summary>The cell ascent and descent GDI+ reads through DirectWrite (DWRITE_FONT_METRICS):
        /// usWinAscent / usWinDescent, or the typographic ones for a face with USE_TYPO_METRICS.</summary>
        internal static int CellAscent(TrueTypeFont font) => font.UseTypoMetrics ? font.TypoAscender : font.WinAscent;
        internal static int CellDescent(TrueTypeFont font) => font.UseTypoMetrics ? -font.TypoDescender : font.WinDescent;

        internal static void DeviceAscentDescent(TrueTypeFont font, float scale, out int asc, out int desc, bool vdmx = true)
        {
            long s16 = (long)Math.Round(scale * 65536.0);
            static int FixMul(long a, long b)
            {
                long p = a * b;
                long r = (Math.Abs(p) + 0x8000) >> 16;
                return (int)(p >= 0 ? r : -r);
            }
            asc = -FixMul(s16, -CellAscent(font));
            desc = FixMul(s16, CellDescent(font));
            int ppem = FixMul(s16, font.UnitsPerEmForHinting);
            if (vdmx && font.TryGetGdiPlusVdmx(ppem, out int yMax, out int yMin))
            {
                asc = Math.Max(asc, yMax);
                desc = Math.Max(desc, -yMin);
            }
        }

        private static void Origin(float x, float y, int align, int lineAlign, float rw, float rh, float totalNom,
                                   float lm, float rm, float cellH, float off94, int ascDev, out float ox, out float oy,
                                   float sy = 1f)
        {
            ox = x; oy = y;
            float tot = totalNom + lm + rm;
            if (align == 1) ox = (rw - tot) * 0.5f + ox;
            else if (align == 2) ox = (rw - tot) + ox;
            if (lineAlign == 1) oy = (rh - cellH) * 0.5f + oy;
            else if (lineAlign == 2) oy = (rh - cellH) + oy;
            ox = off94 + lm + ox;
            // GetDeviceBaselineOrigin@1800391b0: y - (float)(+0xac) / m22, +0xac the negated ascent.
            oy = sy == 1f ? oy - (float)(-ascDev) : oy - (float)(-ascDev) / sy;
        }

        /// <summary>FastTextImager::DrawString: the layout rectangle becomes a clip when the text box
        /// spills out of it; an axis with no extent clips to the text box.</summary>
        private static void Clip(Run run, float x, float y, int align, int lineAlign, float rw, float rh,
                                 float totalNom, float lm, float rm, float cellH, bool noClip)
        {
            if (noClip || !(rw > 0f || rh > 0f)) return;
            float tot = totalNom + lm + rm;
            float wx = x, wy = y;
            if (align == 1) wx = (rw - tot) * 0.5f + wx;
            else if (align == 2) wx = (rw - tot) + wx;
            if (lineAlign == 1) wy = (rh - cellH) * 0.5f + wy;
            else if (lineAlign == 2) wy = (rh - cellH) + wy;
            float cx, cw, cy, ch;
            if (rw > 0f) { cx = x; cw = rw; } else { cx = wx; cw = tot; }
            if (rh > 0f) { cy = y; ch = rh; } else { cy = wy; ch = cellH; }
            if (wx < cx || cx + cw < wx + tot || wy < cy || cy + ch < cellH + wy)
            {
                run.HasClip = true;
                run.ClipX = cx; run.ClipY = cy; run.ClipW = cw; run.ClipH = ch;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Pixels
        // ---------------------------------------------------------------------------------------

        /// <summary>A run's ClearType levels on the device grid: per pixel an index into gaOutTable
        /// (0 = nothing, 0x72 = full), the glyphs composed by level sums.</summary>
        internal sealed class Levels
        {
            /// <summary>An antialiased run: Index is the coverage 0..15, not a gaOutTable index.</summary>
            public bool Grey;
            public int Left, Top, Width, Height;
            public byte[] Index = Array.Empty<byte>();
        }

        /// <summary>The 6x1 bi-level glyph GDI+ draws (DirectWrite's, fitted with word 1).
        /// <para>GDI+ asks with DWRITE_GRID_FIT_MODE_ENABLED, so the face's 'gasp' is not consulted:
        /// Consolas at 9ppem, which its gasp leaves unfitted, is fitted here (and measured fitted,
        /// see NaturalMetrics) -- 735 of 735 test strings exact with that, 703 without.</para></summary>
        internal static NaturalClearType.GlyphBits Glyph(TrueTypeFont font, int gid, float em)
            => NaturalClearType.Rasterize(font, gid, em, 1, gridFit: true, scalerFlags: NaturalScalerWord, forceGridFit: true);

        /// <summary>The same glyph under a device transform that scales x and y apart (GDI+ text
        /// played into a stretched device): DrawPlacedGlyphs hands CreateGlyphBitmapArray the world
        /// em and the world-to-device matrix, MakeRasterizerTransform@18008fe48 (dwrite) does not
        /// round an anisotropic matrix to a size, and the scaler hints the glyph at each axis' own
        /// whole ppem (<see cref="TrueTypeInterpreter.StretchPpemX"/>). Equal ppems are the
        /// ordinary glyph.</summary>
        internal static NaturalClearType.GlyphBits Glyph(TrueTypeFont font, int gid, int ppemX, int ppemY)
        {
            if (ppemX == ppemY) return Glyph(font, gid, ppemX);
            int sx = TrueTypeInterpreter.StretchPpemX, sy = TrueTypeInterpreter.StretchPpemY;
            TrueTypeInterpreter.StretchPpemX = ppemX;
            TrueTypeInterpreter.StretchPpemY = ppemY;
            try
            {
                return NaturalClearType.Rasterize(font, gid, Math.Max(ppemX, ppemY), 1, gridFit: true,
                                                  scalerFlags: NaturalScalerWord, forceGridFit: true);
            }
            finally
            {
                TrueTypeInterpreter.StretchPpemX = sx;
                TrueTypeInterpreter.StretchPpemY = sy;
            }
        }

        /// <summary>A glyph laid sideways in vertical text (FullTextImager::GetFontTransform's quarter
        /// turn): the realization matrix maps the glyph's x onto device y and its y onto device -x,
        /// so the scaler (scl_InitializeScaling) sizes the glyph's x by the device y scale and its y
        /// by the device x scale, fits it there, and turns the fit afterwards (scl_PostTransformGlyph,
        /// the matrix over its own stretch).</summary>
        internal static NaturalClearType.GlyphBits GlyphSideways(TrueTypeFont font, int gid, int ppemAlong, int ppemAcross)
        {
            int sx = TrueTypeInterpreter.StretchPpemX, sy = TrueTypeInterpreter.StretchPpemY;
            TrueTypeInterpreter.StretchPpemX = ppemAlong == ppemAcross ? 0 : ppemAlong;
            TrueTypeInterpreter.StretchPpemY = ppemAlong == ppemAcross ? 0 : ppemAcross;
            try
            {
                // fs__NewTransformation toggles word bit 2 for m00 == 0: ClearType on the glyph's y.
                return NaturalClearType.RasterizeQuarterTurn(font, gid, Math.Max(ppemAlong, ppemAcross), SidewaysScalerWord);
            }
            finally
            {
                TrueTypeInterpreter.StretchPpemX = sx;
                TrueTypeInterpreter.StretchPpemY = sy;
            }
        }

        /// <summary>GetGdiCompatibleGlyphMetrics with isSideways (what GDI+'s
        /// GetGlyphStringVerticalOriginOffsets asks the realization for): the glyph measured as if
        /// laid sideways under the world-to-device axis scale (m11, m22) -- its advance is the
        /// natural fit's span with the glyph's x sized by the device y scale, back in design units
        /// through em * m22, and its vertical origin (sTypoAscender, for a face with no vertical
        /// metrics) rounded to the pixels of the device x scale and back through em * m11.
        /// Times New Roman's .notdef at em 20 under (1.1014, 1.4904): 1580 and 1395 (design 1593
        /// and 1420); under (1.4, 1.4904) 1580 and 1390.</summary>
        internal static void SidewaysMetrics(TrueTypeFont font, int gid, float em, float m11, float m22,
                                             out int advDu, out int voyDu)
        {
            int upem = font.UnitsPerEmForHinting;
            int along = AxisPpem(em * m22), across = AxisPpem(em * m11);
            int sxs = TrueTypeInterpreter.StretchPpemX, sys = TrueTypeInterpreter.StretchPpemY;
            TrueTypeInterpreter.StretchPpemX = along == across ? 0 : along;
            TrueTypeInterpreter.StretchPpemY = along == across ? 0 : across;
            try
            {
                bool hasOutline = font.TryGetDesignXExtent(gid, out _, out _);
                if (!font.TryGetDWriteFittedSpan64(gid, Math.Max(along, across), SidewaysScalerWord, out int span64))
                    span64 = (int)MathF.Round(font.DesignAdvance(gid) * 64f * along / upem, MidpointRounding.AwayFromZero);
                int px = (span64 + (hasOutline ? 32 : 34)) >> 6;
                advDu = (int)Math.Floor(px * (double)upem / (em * m22) + 0.5);
            }
            finally { TrueTypeInterpreter.StretchPpemX = sxs; TrueTypeInterpreter.StretchPpemY = sys; }
            int voyPx = (int)Math.Floor(font.TypoAscender * (double)across / upem + 0.5);
            voyDu = (int)Math.Floor(voyPx * (double)upem / (em * m11) + 0.5);
        }

        /// <summary>A device em (the world em through one axis of the world-to-device matrix) as
        /// the scaler sizes it: MakeRasterizerTransform's 16.16, rounded to a whole pixel (head
        /// flags bit 3, set on every face this is used with).</summary>
        internal static int AxisPpem(float deviceEm)
        {
            long f = (long)Math.Floor(Math.Abs(deviceEm) * 65536.0 + 0.5);
            return (int)((f + 0x8000) >> 16);
        }

        /// <summary>GetGlyphPos + the glyph bitmap's placement: the sample column glyph bit column 0
        /// lands on, and the row of bit row 0.</summary>
        internal static (int Sample, int Row) Place(NaturalClearType.GlyphBits g, float x, float y)
        {
            int x6 = Floor(x * 6f + 0.5f);
            float fx = (float)(x6 / 6.0);
            return (NaturalClearType.RoundHalfAway(6f * fx) + g.Left, NaturalClearType.RoundHalfAway(y) + g.Top);
        }

        /// <summary>fsc_OverscaleToSubPixel, ulClearTypeFilter and RenderGlyph for a run whose glyph
        /// i is <paramref name="glyphs"/>[i] placed at device (<paramref name="xs"/>[i], <paramref name="y"/>).</summary>
        internal static Levels Compose(IReadOnlyList<NaturalClearType.GlyphBits> glyphs, float[] xs, float y, bool fixedFilter)
            => Compose(glyphs, xs, null, y, fixedFilter);

        /// <summary>The same with each glyph on its own baseline (DrawDriverString's origins).</summary>
        internal static Levels Compose(IReadOnlyList<NaturalClearType.GlyphBits> glyphs, float[] xs, float[]? ys, float y, bool fixedFilter)
        {
            int n = glyphs.Count;
            var place = new (int S, int R)[n];
            int p0 = int.MaxValue, p1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                var g = glyphs[i];
                if (g.IsEmpty) continue;
                place[i] = Place(g, xs[i], ys is null ? y : ys[i]);
                p0 = Math.Min(p0, FloorDiv(place[i].S, 6) - 1);
                p1 = Math.Max(p1, FloorDiv(place[i].S + g.Width - 1, 6) + 1);
                r0 = Math.Min(r0, place[i].R);
                r1 = Math.Max(r1, place[i].R + g.Height - 1);
            }
            var lv = new Levels();
            if (p0 > p1) return lv;
            lv.Left = p0; lv.Top = r0; lv.Width = p1 - p0 + 1; lv.Height = r1 - r0 + 1;
            lv.Index = new byte[lv.Width * lv.Height];
            ReadOnlySpan<byte> filter = fixedFilter ? FilterFixed : Filter;
            ReadOnlySpan<byte> ov6 = Overscale6, levels = OutLevels, lut = ComposeLut;
            int[] ov = Array.Empty<int>();
            for (int i = 0; i < n; i++)
            {
                var g = glyphs[i];
                if (g.IsEmpty) continue;
                (int s, int r) = place[i];
                // The pixels this glyph's filter can reach: one either side of its ink.
                int gp0 = FloorDiv(s, 6) - 1, gp1 = FloorDiv(s + g.Width - 1, 6) + 1;
                int gw = gp1 - gp0 + 1;
                if (ov.Length < gw + 2) ov = new int[gw + 2];
                for (int row = 0; row < g.Height; row++)
                {
                    // Three two-sample counts per pixel; ov[k + 1] is pixel gp0 + k.
                    Array.Clear(ov, 0, gw + 2);
                    for (int k = 0; k < gw; k++)
                    {
                        int v = 0, first = 6 * (gp0 + k) - s;
                        for (int b = 0; b < 6; b++)
                        {
                            int c = first + b;
                            v <<= 1;
                            if (c >= 0 && c < g.Width && g.Bits[row * g.Width + c]) v |= 1;
                        }
                        ov[k + 1] = ov6[v];
                    }
                    int outRow = (r + row - lv.Top) * lv.Width;
                    // ulClearTypeFilter: right to left; the right neighbour is the unfiltered one.
                    for (int k = gw - 1; k >= 0; k--)
                    {
                        int cur = ov[k + 1], left = ov[k], right = ov[k + 2];
                        int rt = (right >> 4) & 3;
                        if ((left & 3) == 0 && cur == 0 && rt == 0) continue;
                        int idx = ((((cur >> 4 & 3) + (left & 3) * 3) * 3 + (cur >> 2 & 3)) * 3 + (cur & 3)) * 3 + rt;
                        int v = idx > 0xf2 ? 0 : filter[idx];
                        if (v == 0) continue;
                        int at = outRow + gp0 + k - lv.Left;
                        int d = lv.Index[at];
                        if (d == 0) { lv.Index[at] = (byte)v; continue; }
                        int lr = Math.Min(6, levels[v * 3] + levels[d * 3]);
                        int lg = Math.Min(6, levels[v * 3 + 1] + levels[d * 3 + 1]);
                        int lb = Math.Min(6, levels[v * 3 + 2] + levels[d * 3 + 2]);
                        lv.Index[at] = lut[(lr * 7 + lg) * 7 + lb];
                    }
                }
            }
            return lv;
        }

        /// <summary>A 4x4 antialiased glyph (raster type 4) at one quarter-pixel phase: coverage
        /// 0..15 per pixel, Left/Top relative to the whole pixel the glyph's origin falls in.</summary>
        internal sealed class GreyGlyph
        {
            public int Left, Top, Width, Height;
            public byte[] Coverage = Array.Empty<byte>();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrueTypeFont, Dictionary<(int, int, int), GreyGlyph>>
            s_grey = new();

        /// <summary>DirectWrite's raster type 4 for GDI+'s antialiased hints: the fit with
        /// <see cref="GreyScalerWord"/>, placed at a quarter pixel in x and in y, scanned four
        /// samples by four rows a pixel; a pixel's coverage is the count of its sixteen, at most 15.</summary>
        internal static GreyGlyph Grey(TrueTypeFont font, int gid, float em, int phaseX, int phaseY)
        {
            var cache = s_grey.GetValue(font, _ => new Dictionary<(int, int, int), GreyGlyph>());
            var key = (gid, BitConverter.SingleToInt32Bits(em), phaseX * 4 + phaseY);
            lock (cache)
                if (cache.TryGetValue(key, out GreyGlyph? hit)) return hit;
            var g = new GreyGlyph();
            int dropout = 0;
            if (font.TryGetDWriteFittedOutline(gid, em, GreyScalerWord, out List<PathFigure> figures, out dropout)
                || font.TryGetScaledOutline(gid, em, out figures))
            {
                float dx = phaseX / 4f, dy = phaseY / 4f;
                var moved = new List<PathFigure>(figures.Count);
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                System.Numerics.Vector2 M(System.Numerics.Vector2 p)
                {
                    var q = new System.Numerics.Vector2(p.X + dx, p.Y + dy);
                    if (q.X < x0) x0 = q.X;
                    if (q.X > x1) x1 = q.X;
                    if (q.Y < y0) y0 = q.Y;
                    if (q.Y > y1) y1 = q.Y;
                    return q;
                }
                foreach (PathFigure f in figures)
                {
                    var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                    foreach (PathSegment sg in f.Segments)
                        nf.Segments.Add(sg switch
                        {
                            LineSegment l => new LineSegment(M(l.Point)),
                            QuadraticBezierSegment q => new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                            CubicBezierSegment c => new CubicBezierSegment(M(c.Control1), M(c.Control2), M(c.Point)),
                            _ => sg,
                        });
                    moved.Add(nf);
                }
                if (x0 <= x1)
                {
                    int ox = (int)MathF.Floor(x0) - 1, oy = (int)MathF.Floor(y0) - 1;
                    int w = (int)MathF.Ceiling(x1) + 1 - ox, h = (int)MathF.Ceiling(y1) + 1 - oy;
                    bool[]? bits = PathRasterizer.ScanGlyphBits(new PathGeometry(FillRule.NonZero, moved), ox, oy, w, h, 4,
                                                                dropout, 4);
                    if (bits is not null)
                    {
                        var cov = new int[w * h];
                        int cols = w * 4;
                        for (int r = 0; r < h * 4; r++)
                            for (int c = 0; c < cols; c++)
                                if (bits[r * cols + c]) cov[(r >> 2) * w + (c >> 2)]++;
                        int c0 = w, c1 = -1, r0 = h, r1 = -1;
                        for (int r = 0; r < h; r++)
                            for (int c = 0; c < w; c++)
                                if (cov[r * w + c] > 0)
                                {
                                    c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
                                    r0 = Math.Min(r0, r); r1 = Math.Max(r1, r);
                                }
                        if (c1 >= 0)
                        {
                            g.Left = ox + c0; g.Top = oy + r0; g.Width = c1 - c0 + 1; g.Height = r1 - r0 + 1;
                            g.Coverage = new byte[g.Width * g.Height];
                            for (int r = 0; r < g.Height; r++)
                                for (int c = 0; c < g.Width; c++)
                                    g.Coverage[r * g.Width + c] = (byte)Math.Min(15, cov[(r0 + r) * w + c0 + c]);
                        }
                    }
                }
            }
            lock (cache)
            {
                if (cache.Count > 8192) cache.Clear();
                cache[key] = g;
            }
            return g;
        }

        /// <summary>The antialiased run (OutputTextOptimized of DpOutputAntiAliasSolid8BPPOptimizedSpan):
        /// each glyph at its quarter-pixel phase, overlapping glyphs combined by MAX. Levels.Index
        /// holds the coverage 0..15.</summary>
        internal static Levels ComposeGrey(TrueTypeFont font, IReadOnlyList<ushort> gids, float em, float[] xs, float y)
        {
            int n = gids.Count;
            var place = new (GreyGlyph G, int X, int Y)[n];
            int p0 = int.MaxValue, p1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;
            int qy = Floor(y * 4f + 0.5f), iy = FloorDiv(qy, 4);
            for (int i = 0; i < n; i++)
            {
                int qx = Floor(xs[i] * 4f + 0.5f), ix = FloorDiv(qx, 4);
                GreyGlyph g = Grey(font, gids[i], em, qx - 4 * ix, qy - 4 * iy);
                place[i] = (g, ix + g.Left, iy + g.Top);
                if (g.Width == 0) continue;
                p0 = Math.Min(p0, place[i].X); p1 = Math.Max(p1, place[i].X + g.Width - 1);
                r0 = Math.Min(r0, place[i].Y); r1 = Math.Max(r1, place[i].Y + g.Height - 1);
            }
            var lv = new Levels { Grey = true };
            if (p0 > p1) return lv;
            lv.Left = p0; lv.Top = r0; lv.Width = p1 - p0 + 1; lv.Height = r1 - r0 + 1;
            lv.Index = new byte[lv.Width * lv.Height];
            foreach ((GreyGlyph g, int gx, int gy) in place)
                for (int r = 0; r < g.Height; r++)
                    for (int c = 0; c < g.Width; c++)
                    {
                        byte v = g.Coverage[r * g.Width + c];
                        int at = (gy + r - lv.Top) * lv.Width + gx + c - lv.Left;
                        if (v > lv.Index[at]) lv.Index[at] = v;
                    }
            return lv;
        }

        /// <summary>A pixel of an antialiased run: TextColorGammaTable's colour for the coverage
        /// (cov = 255 - Inv[contrast][255 - 17k], 17k at contrast 0), PremultiplyWithCoverage, then
        /// Blend_sRGB_sRGB over the paper.</summary>
        internal static (byte R, byte G, byte B) BlendGreyPixel(int coverage, byte br, byte bg, byte bb, int alpha,
                                                                byte dr, byte dg, byte db, int contrast = DefaultContrast)
        {
            if (coverage == 0) return (dr, dg, db);
            if (contrast > 12 || contrast < 0) contrast = DefaultContrast;
            ReadOnlySpan<byte> t = ContrastTables;
            int k17 = coverage * 255 / 15;
            int cov = contrast == 0 ? k17 : 255 - t[contrast * 512 + 256 + 255 - k17];
            int u = alpha * cov + 0x80;
            int A = (u + (u >> 8)) >> 8;
            if (A == 0) return (dr, dg, db);
            int sr = Mul(br, A), sg = Mul(bg, A), sb = Mul(bb, A);
            if (A == 255) return ((byte)sr, (byte)sg, (byte)sb);
            return (Over(sr, dr, A), Over(sg, dg, A), Over(sb, db, A));

            static int Mul(int v, int a) { int w = v * a + 0x80; return ((w + (w >> 8)) >> 8) & 0xff; }
            static byte Over(int src, int d, int a) { int w = d * (255 - a) + 0x80; return (byte)((src + ((w + (w >> 8)) >> 8)) & 0xff); }
        }

        /// <summary>The TextContrast inverse table: Inv[contrast][v].</summary>
        internal static int ContrastInverse(int contrast, int v) => ContrastTables[contrast * 512 + 256 + v];

        /// <summary>The three channel levels (0..6) of a gaOutTable index.</summary>
        internal static (int R, int G, int B) LevelsOf(int index)
        {
            ReadOnlySpan<byte> t = OutLevels;
            return (t[index * 3], t[index * 3 + 1], t[index * 3 + 2]);
        }

        /// <summary>ScanOperation::CTBlendSolid for one channel: brush channel <paramref name="brush"/>
        /// at alpha <paramref name="alpha"/> (0..255, not premultiplied) over <paramref name="dst"/>,
        /// with level <paramref name="level"/> (0..6).</summary>
        internal static byte BlendChannel(int brush, int alpha, int dst, int level, int contrast = DefaultContrast)
        {
            if (level == 0) return (byte)dst;
            if (contrast > 12 || contrast < 0) contrast = DefaultContrast;
            ReadOnlySpan<byte> t = ContrastTables;
            int dir = contrast * 512, inv = dir + 256;
            double v = (t[dir + brush] - t[dir + dst]) * (double)alpha * level / 1530.0 + t[dir + dst] + 0.5;
            return t[inv + ((int)v & 0xff)];
        }

        /// <summary>A pixel of the run: the brush where the coverage is full and the brush opaque,
        /// else CTBlendSolid per channel.</summary>
        internal static (byte R, byte G, byte B) BlendPixel(int index, byte br, byte bg, byte bb, int alpha,
                                                            byte dr, byte dg, byte db, int contrast = DefaultContrast)
        {
            if (index == 0x72 && alpha == 255) return (br, bg, bb);
            (int l0, int l1, int l2) = LevelsOf(index);
            return (BlendChannel(br, alpha, dr, l0, contrast), BlendChannel(bg, alpha, dg, l1, contrast),
                    BlendChannel(bb, alpha, db, l2, contrast));
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
