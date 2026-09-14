// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Our text against Windows' text, pixel for pixel, with nothing in between.
//
// This asks GDI for the same string at the same size with CLEARTYPE_QUALITY -- the font's own hints,
// run by Windows' own rasterizer, resolved onto the three lamps of each pixel -- then draws the same
// string through our renderer at the same origin and subtracts the two images. What comes back is
// not an opinion about whether the text looks right; it is the number of pixels where we disagree
// with Windows.
//
// IT ASKS FOR CLEARTYPE BECAUSE CLEARTYPE IS WHAT WE DRAW. It used to ask for ANTIALIASED_QUALITY,
// and that was right while our text was grey. Comparing subpixel output against a grey reference
// measures the difference between two rendering modes, which is not a fault anybody can fix.
//
// ON THE GREEN LAMP. Both sides are reduced to one number per pixel, the green channel, and green
// because it is the middle lamp and the one the eye weighs most heavily -- a glyph that is right in
// green is right. Comparing all three would fail on the fringe colours, which are a filter's opinion
// rather than a shape.
//
// WHY IT ASSERTS ON STRUCTURE AND NOT ON SHADE. Windows puts its coverage through a contrast curve
// that is a tunable of the machine, not a constant anything can read off the font. Ours goes through
// one measured against it (see SubpixelGamma), which lands close and cannot land exactly. From
// IDENTICAL outlines the bytes would still differ.
//
// So this asserts on STRUCTURE: which pixels the ink covers, not what shade it is.
//
// WHAT IS LEFT, now that the text is subpixel: the numbers in Allowed, which are larger than the
// twenty-two pixels the grey comparison was down to. ClearType is a far harder target -- three
// times the horizontal resolution means a third of a pixel of error is a whole lamp, and errors the
// grey comparison could not see are now visible. Measured against GDI ClearType over four runs at
// six sizes, the disagreement came down from 2386 pixels to 1043 and the mean channel error from
// 70.8 to 44.1 as the two halves of subpixel rendering went in: vertical-only hinting, then the
// contrast curve.
//
// It is worth saying that this file once explained at length why a residue of about three hundred
// pixels could not be removed, and named the '&', the '@' and the digits as cases where "the
// designer made a judgement call" that no interpreter could recover. That was wrong. Every one of
// them was arithmetic: rounding a negative number one step too far (see the note on the fixed-point
// helpers in TrueTypeInterpreter), a scale used for two jobs at once, and an advance taken from the
// design metrics at the sizes the face's own 'hdmx' happens to skip. The lesson is the obvious one
// and it is easy to forget: an explanation of why a difference is irreducible is a hypothesis, and a
// comfortable one, and it stops the search.
//
// WHAT IS COVERED, and it is worth saying because each of these was once assumed and was once
// wrong: every printable character of the UI font one at a time; the accented letters, which are
// built out of components and hinted by a different route; runs of them, which is the ONLY way the
// advances between glyphs are checked, and which is how a drift of a fraction of a pixel per letter
// was found; and the whole repertoire in all four faces of the family at every size from ten pixels
// an em to twenty, because a hinting program is rewritten by the face at each size and the faults
// found here were arithmetic -- wrong at one size and right at its neighbours.
//
// WHY ONLY SEGOE UI AND NOT ARIAL, TAHOMA, VERDANA, TIMES. Those were measured too, and they
// disagree with us by a hundred pixels a run. It is not our hinting: their 'gasp' table asks for
// GRIDFIT WITHOUT DOGRAY between nine and seventeen pixels an em, so GDI draws them BILEVEL at
// every size a UI uses -- no grey at all -- and there is nothing to compare an antialiased
// rasterizer against. (Segoe UI, Consolas and Calibri are ClearType-era faces whose 'gasp' asks for
// grey, which is what makes them comparable.) Courier New and Calibri differ for a second reason:
// they ship EMBEDDED BITMAPS in 'EBDT'/'EBLC', and at the sizes those cover, GDI draws the
// designer's hand-made bitmap instead of any outline. Neither is a defect on our side, and adding
// those faces here would only assert that we are not GDI.
//
// Windows only, obviously; skipped everywhere else.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class WindowsGlyphParityTests : RendererTestBase
    {
        public WindowsGlyphParityTests(GpuFixture gpu) : base(gpu) { }

        // Nine point on a 96-DPI screen: the size the shell draws its own text at, and the size
        // every measurement against Windows in this area has been taken at.
        private const int Ppem = 12;

        /// <summary>The sizes to check runs at. Nine point is the one that matters most, but a
        /// hinting program is written per size -- the face's 'prep' runs again at each -- so a
        /// change that is right at twelve pixels an em can be wrong at eleven or sixteen.</summary>
        private static readonly int[] Sizes = { 11, 12, 13, 16, 19 };
        private const int Baseline = 18;
        private const int PenX = 6;
        private const int Width = 460;
        // Tall enough for the largest size checked to sit inside it. It was 28, which is fine at
        // nine point and puts the baseline BELOW the bitmap at nineteen pixels an em -- both images
        // then showed the tops of the glyphs and the comparison was of the wrong thing.
        private const int Height = 56;

        /// <summary>What still differs from Windows, and by how many pixels. Everything not named
        /// here covers exactly the pixels Windows covers.
        /// <para>These are SUBPIXEL numbers and they are bigger than the grey ones they replaced,
        /// because ClearType is a harder target: a third of a pixel of error is a whole lamp, so
        /// disagreements the grey comparison could not see are visible here. The ones that remain
        /// cluster where a stroke lands between two lamps -- the capitals at nineteen pixels an em
        /// are the worst of them.</para>
        /// <para>Every number here was earned down from a bigger one. Lower one when a change earns
        /// it, and never raise one: a number going up means something that used to match Windows has
        /// stopped.</para>
        /// <para>RAISED ONCE, DELIBERATELY, AND THIS IS THE RECORD OF IT. XHintMode went from 5 to
        /// 6 because the live control window prefers it by 159,129 pixels. 206 of these numbers went
        /// up and 117 went down. That is not the rule above being waived quietly -- it is the rule
        /// doing its job and pointing at something real.</para>
        /// <para>This suite's oracle is ExtTextOutW at CLEARTYPE_QUALITY: one glyph, straight
        /// through GDI. The window's oracle is stock WinForms drawing itself. Mode 6 moves us AWAY
        /// from the first and TOWARDS the second, on the same glyphs, and both cannot be Windows.
        /// The window is what was asked for and what the ink corroborates -- its regions sit on
        /// 1.000 under mode 6 against 0.99 under mode 5, and the pure Label regions, which are text
        /// on a plain background and nothing else, improve by 43%.</para>
        /// <para>ANSWERED, and this suite's oracle is exonerated. TextRenderer and ExtTextOutW at
        /// CLEARTYPE_QUALITY are byte-identical -- SUM|d| 0, drawn into the same GDI DIB -- so what
        /// is asked for here is exactly what WinForms draws. Both harnesses also render our side
        /// through the same RenderToRgba under identical flags, and the paper makes no difference.
        /// The two disagreed because they measure different POPULATIONS: mode 6 wins at regular@12
        /// and nowhere else, the live window is Segoe UI 9pt at scale 1 which is exactly ppem 12,
        /// and these 442 cases average four styles across eleven sizes.</para>
        /// <para>So the raise is not a proxy disagreeing with the target. It is this suite
        /// correctly reporting that the shipped mode is worse at every size but the one the window
        /// happens to use. These numbers are the honest record of that trade, and the 206 that went
        /// up are the sizes we got worse at.</para></summary>
        private static readonly Dictionary<string, int> Allowed = new()
        {
            ["%"] = 0,
            ["&"] = 0,
            ["("] = 0,
            [")"] = 0,
            ["*"] = 0,
            ["/"] = 1,
            ["0123456789@11"] = 1,
            ["0123456789@12"] = 3,
            ["0123456789@12bi"] = 4,
            ["0123456789@12i"] = 1,
            ["0123456789@13"] = 5,
            ["0123456789@16"] = 2,
            ["0123456789@16b"] = 2,
            ["0123456789@16bi"] = 2,
            ["0123456789@16i"] = 4,
            ["0123456789@19"] = 3,
            ["1"] = 0,
            ["3"] = 0,
            ["5"] = 1,
            ["6"] = 0,
            ["7"] = 1,
            // ONE pixel, 42/255, and it went UP when the glyph flattening got finer.
            // It is not a regression: at the old 0.004px tolerance the chord sag happened
            // to cancel a real residual here, and with the sag gone the residual shows.
            // Stable at every tolerance from 0.0002 down to 0.00001, so it is not the
            // flattening -- something else in this glyph is a fraction of a lamp out, and
            // the old zero was luck. Every other entry in this table TIGHTENED.
            ["8"] = 1,
            ["<"] = 0,
            [">"] = 0,
            ["@"] = 0,
            ["ABCDEFGHIJKLM@11"] = 1,
            ["ABCDEFGHIJKLM@12"] = 1,
            ["ABCDEFGHIJKLM@13"] = 0,
            ["ABCDEFGHIJKLM@16"] = 0,
            ["ABCDEFGHIJKLM@19"] = 1,
            ["B"] = 1,
            ["C"] = 0,
            ["Cancel Apply@12b"] = 2,
            ["Cancel Apply@12bi"] = 0,
            ["Cancel Apply@12i"] = 2,
            ["Cancel Apply@16b"] = 1,
            ["Cancel Apply@16bi"] = 0,
            ["Cancel Apply@16i"] = 1,
            ["G"] = 0,
            ["Handgloves@12b"] = 2,
            ["Handgloves@12bi"] = 0,
            ["Handgloves@12i"] = 2,
            ["Handgloves@16b"] = 1,
            ["Handgloves@16bi"] = 0,
            ["Handgloves@16i"] = 3,
            ["Illinois still@11"] = 0,
            ["Illinois still@12"] = 0,
            ["Illinois still@13"] = 0,
            ["Illinois still@16"] = 0,
            ["Illinois still@19"] = 4,
            ["K"] = 0,
            ["NOPQRSTUVWXYZ@11"] = 0,
            ["NOPQRSTUVWXYZ@12"] = 0,
            ["NOPQRSTUVWXYZ@13"] = 1,
            ["NOPQRSTUVWXYZ@16"] = 1,
            ["NOPQRSTUVWXYZ@19"] = 1,
            ["Příliš@12b"] = 9,
            ["Příliš@12bi"] = 0,
            ["Příliš@12i"] = 0,
            ["Příliš@16b"] = 8,
            ["Příliš@16bi"] = 1,
            ["Příliš@16i"] = 1,
            ["Q"] = 0,
            ["R"] = 0,
            ["S"] = 1,
            ["Shapes 2026@11"] = 0,
            ["Shapes 2026@12"] = 1,
            ["Shapes 2026@13"] = 2,
            ["Shapes 2026@16"] = 4,
            ["Shapes 2026@19"] = 3,
            ["X"] = 0,
            ["^"] = 0,
            ["`"] = 0,
            ["a"] = 0,
            ["abcdefghijklm@11"] = 1,
            ["abcdefghijklm@12"] = 1,
            ["abcdefghijklm@13"] = 0,
            ["abcdefghijklm@16"] = 2,
            ["abcdefghijklm@19"] = 1,
            ["b"] = 0,
            ["d"] = 0,
            ["e"] = 0,
            ["g"] = 0,
            ["h"] = 0,
            ["k"] = 1,
            ["m"] = 0,
            ["n"] = 0,
            ["nopqrstuvwxyz@11"] = 0,
            ["nopqrstuvwxyz@12"] = 2,
            ["nopqrstuvwxyz@13"] = 0,
            ["nopqrstuvwxyz@16"] = 1,
            ["nopqrstuvwxyz@19"] = 3,
            ["p"] = 0,
            ["q"] = 2,
            ["repertoire@10"] = 78,
            ["repertoire@10b"] = 77,
            ["repertoire@10bi"] = 19,
            ["repertoire@10i"] = 20,
            ["repertoire@11"] = 70,
            ["repertoire@11b"] = 119,
            ["repertoire@11bi"] = 24,
            ["repertoire@11i"] = 11,
            ["repertoire@12"] = 72,
            ["repertoire@12b"] = 165,
            ["repertoire@12bi"] = 9,
            ["repertoire@12i"] = 23,
            ["repertoire@13"] = 78,
            ["repertoire@13b"] = 86,
            ["repertoire@13bi"] = 13,
            ["repertoire@13i"] = 22,
            ["repertoire@14"] = 75,
            ["repertoire@14b"] = 173,
            ["repertoire@14bi"] = 24,
            ["repertoire@14i"] = 16,
            ["repertoire@15"] = 79,
            ["repertoire@15b"] = 147,
            ["repertoire@15bi"] = 34,
            ["repertoire@15i"] = 25,
            ["repertoire@16"] = 122,
            ["repertoire@16b"] = 165,
            ["repertoire@16bi"] = 19,
            ["repertoire@16i"] = 33,
            ["repertoire@17"] = 101,
            ["repertoire@17b"] = 191,
            ["repertoire@17bi"] = 19,
            ["repertoire@17i"] = 23,
            ["repertoire@18"] = 77,
            ["repertoire@18b"] = 158,
            ["repertoire@18bi"] = 10,
            ["repertoire@18i"] = 17,
            ["repertoire@19"] = 126,
            ["repertoire@19b"] = 211,
            ["repertoire@19bi"] = 23,
            ["repertoire@19i"] = 31,
            ["repertoire@20"] = 113,
            ["repertoire@20b"] = 195,
            ["repertoire@20bi"] = 4,
            ["repertoire@20i"] = 4,
            ["s"] = 0,
            ["u"] = 0,
            ["v"] = 0,
            ["x"] = 0,
            ["y"] = 0,
            ["z"] = 0,
            ["Á@11"] = 0,
            ["Á@12"] = 0,
            ["Á@13"] = 0,
            ["Á@16"] = 0,
            ["Á@19"] = 0,
            ["Ä@11"] = 4,
            ["Ä@13"] = 1,
            ["Ä@16"] = 5,
            ["Ä@19"] = 1,
            ["É@11"] = 0,
            ["É@12"] = 0,
            ["É@13"] = 1,
            ["É@16"] = 1,
            ["É@19"] = 0,
            ["Ñ@11"] = 6,
            ["Ñ@12"] = 1,
            ["Ñ@13"] = 6,
            ["Ñ@16"] = 3,
            ["Ñ@19"] = 5,
            ["Ö@11"] = 0,
            ["Ö@13"] = 2,
            ["Ö@16"] = 7,
            ["Ö@19"] = 1,
            ["Ü@12"] = 1,
            ["Ü@13"] = 1,
            ["Ü@16"] = 4,
            ["Ü@19"] = 2,
            ["à@11"] = 0,
            ["à@12"] = 0,
            ["à@13"] = 1,
            ["à@16"] = 1,
            ["à@19"] = 0,
            ["á@11"] = 0,
            ["á@12"] = 1,
            ["á@13"] = 0,
            ["á@16"] = 1,
            ["á@19"] = 0,
            ["â@11"] = 6,
            ["â@12"] = 5,
            ["â@13"] = 2,
            ["â@16"] = 3,
            ["â@19"] = 5,
            ["ã@11"] = 1,
            ["ã@12"] = 2,
            ["ã@13"] = 5,
            ["ã@16"] = 5,
            ["ã@19"] = 6,
            ["ä@11"] = 0,
            ["ä@12"] = 0,
            ["ä@13"] = 1,
            ["ä@16"] = 4,
            ["å@11"] = 3,
            ["å@12"] = 3,
            ["å@13"] = 2,
            ["å@16"] = 6,
            ["å@19"] = 9,
            ["ç@11"] = 4,
            ["ç@12"] = 5,
            ["ç@13"] = 4,
            ["ç@16"] = 2,
            ["è@11"] = 0,
            ["è@12"] = 0,
            ["è@13"] = 0,
            ["è@16"] = 1,
            ["è@19"] = 1,
            ["é@11"] = 0,
            ["é@12"] = 0,
            ["é@13"] = 0,
            ["é@16"] = 0,
            ["é@19"] = 1,
            ["ê@11"] = 3,
            ["ê@12"] = 3,
            ["ê@13"] = 1,
            ["ê@16"] = 4,
            ["ê@19"] = 3,
            ["ë@11"] = 2,
            ["ë@12"] = 0,
            ["ë@13"] = 2,
            ["ë@16"] = 6,
            ["ë@19"] = 1,
            ["ì@11"] = 0,
            ["ì@12"] = 0,
            ["ì@13"] = 0,
            ["ì@16"] = 0,
            ["ì@19"] = 1,
            ["í@11"] = 0,
            ["í@12"] = 1,
            ["í@13"] = 0,
            ["í@16"] = 0,
            ["í@19"] = 1,
            ["î@11"] = 5,
            ["î@12"] = 7,
            ["î@13"] = 4,
            ["î@16"] = 5,
            ["î@19"] = 13,
            ["ï@19"] = 4,
            ["ñ@11"] = 2,
            ["ñ@12"] = 2,
            ["ñ@13"] = 0,
            ["ñ@16"] = 6,
            ["ñ@19"] = 6,
            ["ò@11"] = 0,
            ["ò@12"] = 0,
            ["ò@13"] = 1,
            ["ò@16"] = 0,
            ["ò@19"] = 2,
            ["ó@11"] = 0,
            ["ó@12"] = 0,
            ["ó@13"] = 0,
            ["ó@16"] = 0,
            ["ó@19"] = 3,
            ["ô@11"] = 4,
            ["ô@12"] = 4,
            ["ô@13"] = 2,
            ["ô@16"] = 6,
            ["ô@19"] = 3,
            ["õ@11"] = 1,
            ["õ@12"] = 1,
            ["õ@13"] = 7,
            ["õ@16"] = 1,
            ["õ@19"] = 6,
            ["ö@11"] = 0,
            ["ö@13"] = 2,
            ["ö@16"] = 8,
            ["ö@19"] = 5,
            ["ù@11"] = 0,
            ["ù@12"] = 0,
            ["ù@13"] = 0,
            ["ù@16"] = 2,
            ["ù@19"] = 0,
            ["ú@11"] = 0,
            ["ú@12"] = 0,
            ["ú@13"] = 0,
            ["ú@16"] = 1,
            ["ú@19"] = 0,
            ["û@11"] = 4,
            ["û@12"] = 5,
            ["û@13"] = 4,
            ["û@16"] = 2,
            ["û@19"] = 4,
            ["ü@11"] = 0,
            ["ü@12"] = 0,
            ["ü@13"] = 0,
            ["ü@16"] = 6,
            ["ü@19"] = 4,
            ["ý@11"] = 0,
            ["ý@12"] = 0,
            ["ý@13"] = 0,
            ["ý@16"] = 0,
            ["ý@19"] = 1,
            ["ÿ@11"] = 3,
            ["ÿ@12"] = 1,
            ["ÿ@13"] = 1,
            ["ÿ@16"] = 4,
            ["Č@11"] = 2,
            ["Č@12"] = 5,
            ["Č@13"] = 3,
            ["Č@16"] = 0,
            ["Č@19"] = 2,
            ["Ř@11"] = 5,
            ["Ř@12"] = 4,
            ["Ř@13"] = 2,
            ["Ř@16"] = 6,
            ["Ř@19"] = 5,
            ["Š@11"] = 3,
            ["Š@12"] = 2,
            ["Š@13"] = 2,
            ["Š@16"] = 3,
            ["Š@19"] = 1,
            ["š@11"] = 2,
            ["š@12"] = 4,
            ["š@13"] = 2,
            ["š@16"] = 1,
            ["š@19"] = 2,
            ["ů@11"] = 3,
            ["ů@12"] = 5,
            ["ů@13"] = 7,
            ["ů@16"] = 4,
            ["ů@19"] = 6,
            ["Ž@11"] = 2,
            ["Ž@12"] = 4,
            ["Ž@13"] = 4,
            ["Ž@16"] = 2,
            ["Ž@19"] = 4,
            ["ž@11"] = 1,
            ["ž@12"] = 0,
            ["ž@13"] = 0,
            ["ž@16"] = 3,
            ["ž@19"] = 6,
            ["F"] = 0,
            ["W"] = 0,
            ["c"] = 0,
            ["Ö@12"] = 0,
        };

        private static int AllowanceFor(string key)
            => Allowed.TryGetValue(key, out int n) ? n : 0;

        /// <summary>With WPF_ALLOW_REPORT set, write what each case ACTUALLY measures instead of
        /// arguing with the table. A change to the rasterizer moves every one of these -- the ratchet
        /// is exact in both directions by design -- so the table is REGENERATED from a run rather
        /// than edited by hand.</summary>
        private static void RecordActual(string key, int actual)
        {
            string? path = Environment.GetEnvironmentVariable("WPF_ALLOW_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            lock (Repertoire) File.AppendAllText(path!, key + "\t" + actual + Environment.NewLine);
        }

        /// <summary>Every printable character the UI font is asked for, ONE AT A TIME, so a failure
        /// names the character rather than the line it was in.</summary>
        public static TheoryData<char> EveryCharacter()
        {
            var data = new TheoryData<char>();
            for (char c = ' '; c <= '~'; c++) data.Add(c);
            return data;
        }

        [Theory]
        [MemberData(nameof(EveryCharacter))]
        public void EveryGlyph_CoversTheSamePixelsAsWindows(char character)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            string text = character.ToString();
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            byte[] windows = Gdi.Draw(text, ProbeFamily(), Ppem, PenX, Baseline, Width, Height);
            byte[] ours = Ours(font, text);
            Dump($"c{(int)character}", windows, ours);

            Difference diff = Difference.Between(windows, ours, Width, Height);
            // Say WHICH machine drew ours. A glyph that quietly fell back to the analysis differs
            // from Windows everywhere at once, and reads as a hinting bug when it is a loading one.
            bool ranFaceHints = font.FaceHintsGlyph(font.GlyphIndex(character), Ppem);
            RecordActual(text, diff.Pixels);
            int allowed = AllowanceFor(text);
            Assert.True(diff.Pixels <= allowed,
                (ranFaceHints ? "" : "(the face's own hints did NOT run for this glyph) ")
                + diff.Describe(text, windows, ours, Width, Height));
            Assert.True(diff.Pixels >= allowed,
                $"'{character}' now matches Windows in {allowed - diff.Pixels} more pixels than "
                + "Allowed says it does -- lower its entry, or drop it.");
        }

        /// <summary>Runs, at several sizes. A run catches what a single glyph cannot: the advances
        /// between them, and the face's program being re-run at each size.</summary>
        public static TheoryData<string, int> RunsAndSizes()
        {
            var data = new TheoryData<string, int>();
            foreach (int ppem in Sizes)
                foreach (string text in new[]
                {
                    "abcdefghijklm", "nopqrstuvwxyz", "ABCDEFGHIJKLM", "NOPQRSTUVWXYZ",
                    "0123456789", "Shapes 2026", "Illinois still",
                })
                    data.Add(text, ppem);
            return data;
        }

        [Theory]
        [MemberData(nameof(RunsAndSizes))]
        public void RunsOfText_CoverTheSamePixelsAsWindows(string text, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            int baseline = ppem + 12;            // room for the ascenders at this size
            byte[] windows = Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height);
            byte[] ours = Ours(new TrueTypeFont(File.ReadAllBytes(file!)), text, ppem, baseline);
            Dump($"{ppem}-{text}", windows, ours);

            Difference diff = Difference.Between(windows, ours, Width, Height);
            RecordActual($"{text}@{ppem}", diff.Pixels);
            int allowed = AllowanceFor($"{text}@{ppem}");
            Assert.True(diff.Pixels <= allowed,
                $"at {ppem} pixels an em: " + diff.Describe(text, windows, ours, Width, Height));
            Assert.True(diff.Pixels >= allowed,
                $"'{text}' at {ppem} now matches Windows in {allowed - diff.Pixels} more pixels "
                + "than Allowed says it does -- lower its entry, or drop it.");
        }

        // ---- accented letters --------------------------------------------------------------------
        //
        // An 'e' with an acute over it is not a glyph in the file: it is the 'e' and the acute, each
        // a glyph of its own, with an offset saying where the second goes. That matters here because
        // the face's hinting program for such a letter is written against components that have
        // ALREADY been fitted -- it moves an accent clear of a stem that is on the grid, and it can
        // only be run after the parts have been.
        //
        // These were the letters that proved it. Before composites were fitted this way they were
        // the only characters in the font that fell back to the analysis, so every accented word in
        // French, Spanish, Czech or Turkish was drawn by a different rule from the word beside it --
        // and 'e' under an accent came out a different weight from the same 'e' on its own.

        /// <summary>The accented letters of the Latin alphabets, one at a time. All composites in
        /// this face; a character that is not is skipped rather than quietly passing.</summary>
        public static TheoryData<char, int> AccentedCharacters()
        {
            var data = new TheoryData<char, int>();
            foreach (int ppem in Sizes)
                foreach (char c in "áàâäãåéèêëíìîïñóòôöõúùûüýÿçšžťůđÁÄÅÉÑÖÜČŘŠŽ")
                    data.Add(c, ppem);
            return data;
        }

        [Theory]
        [MemberData(nameof(AccentedCharacters))]
        public void EveryAccentedGlyph_CoversTheSamePixelsAsWindows(char character, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            Assert.SkipUnless(font.IsCompositeGlyph(character),
                              $"'{character}' is not built out of components in this face");

            // The point of the exercise: the face's OWN program has to run for a composite. Without
            // this the images can still agree by luck at one size, and the test would be measuring
            // the analysis fitter rather than the interpreter.
            Assert.True(font.FaceHintsGlyph(font.GlyphIndex(character), ppem),
                        $"'{character}' did not run the face's own hinting program");

            string text = character.ToString();
            int baseline = ppem + 12;
            byte[] windows = Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height);
            byte[] ours = Ours(font, text, ppem, baseline);
            Dump($"a{(int)character}-{ppem}", windows, ours);

            Difference diff = Difference.Between(windows, ours, Width, Height);
            RecordActual($"{text}@{ppem}", diff.Pixels);
            int allowed = AllowanceFor($"{text}@{ppem}");
            Assert.True(diff.Pixels <= allowed,
                        $"at {ppem} pixels an em: " + diff.Describe(text, windows, ours, Width, Height));
            Assert.True(diff.Pixels >= allowed,
                        $"'{character}' at {ppem} now matches Windows in {allowed - diff.Pixels} "
                        + "more pixels than Allowed says it does -- lower its entry, or drop it.");
        }

        // ---- the other faces of the family ---------------------------------------------------------
        //
        // The bold and the italic are separate FILES with hinting programs of their own, written for
        // stems of a different weight; a change that is right for the regular can be wrong for them.
        // Both are asked for the way an application asks -- a weight and a slant, not a face name --
        // so this also covers GDI and us picking the same file out of the family.

        public static TheoryData<string, bool, bool, int> StyledRuns()
        {
            var data = new TheoryData<string, bool, bool, int>();
            foreach (int ppem in new[] { 12, 16 })
                foreach ((bool bold, bool italic) in new[] { (true, false), (false, true), (true, true) })
                    foreach (string text in new[] { "Handgloves", "0123456789", "Cancel Apply", "Příliš" })
                        data.Add(text, bold, italic, ppem);
            return data;
        }

        [Theory]
        [MemberData(nameof(StyledRuns))]
        public void TheBoldAndTheItalic_CoverTheSamePixelsAsWindows(string text, bool bold, bool italic,
                                                                    int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no Segoe UI bold={bold} italic={italic}");

            int baseline = ppem + 12;
            byte[] windows = Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, bold, italic);
            byte[] ours = Ours(new TrueTypeFont(File.ReadAllBytes(file!)), text, ppem, baseline);
            string style = (bold ? "b" : "") + (italic ? "i" : "");
            Dump($"{style}-{ppem}-{text}", windows, ours);

            Difference diff = Difference.Between(windows, ours, Width, Height);
            RecordActual($"{text}@{ppem}{style}", diff.Pixels);
            int allowed = AllowanceFor($"{text}@{ppem}{style}");
            Assert.True(diff.Pixels <= allowed,
                        $"Segoe UI {style} at {ppem} pixels an em: "
                        + diff.Describe(text, windows, ours, Width, Height));
            Assert.True(diff.Pixels >= allowed,
                        $"'{text}' {style} at {ppem} now matches Windows in {allowed - diff.Pixels} "
                        + "more pixels than Allowed says it does -- lower its entry, or drop it.");
        }

        // ---- the whole family, at every size a screen asks for -------------------------------------
        //
        // The theories above check a lot at a few sizes. This checks EVERYTHING at every size from
        // ten pixels an em to twenty, in all four faces of the family -- roughly six thousand
        // character-size-face combinations -- because the bugs that were found here were arithmetic,
        // and arithmetic goes wrong at one size and not its neighbours. A rounding fault that moved
        // the underscore one row down lived only at seventeen pixels an em, which no test looked at.
        //
        // In RUNS rather than one character at a time, and that is a deliberate trade: a run costs
        // one render instead of fifty, which is what makes this affordable to keep, and it checks the
        // advances between the glyphs into the bargain. The cost of it is that a failure names a run
        // of a dozen characters rather than one -- so the message prints the pictures, and the
        // per-character theories above are still there to narrow it down.

        /// <summary>Every printable character the UI font is asked for, plus the accented letters of
        /// the Latin alphabets, in runs short enough to fit the bitmap at the largest size.</summary>
        private static readonly string[] Repertoire =
        {
            "ABCDEFGHIJKLM", "NOPQRSTUVWXYZ", "abcdefghijklm", "nopqrstuvwxyz",
            "0123456789", "!\"#$%&'()*+,-./", ":;<=>?@[]^_`", "{|}~",
            "áàâäãåéèêë", "íìîïñóòôöõ", "úùûüýÿçšžť", "ůđÁÄÅÉÑÖÜČŘŠŽ",
        };

        public static TheoryData<bool, bool, int> FacesAndSizes()
        {
            var data = new TheoryData<bool, bool, int>();
            foreach ((bool bold, bool italic) in new[] { (false, false), (true, false),
                                                         (false, true), (true, true) })
                for (int ppem = 10; ppem <= 20; ppem++)
                    data.Add(bold, italic, ppem);
            return data;
        }

        [Theory]
        [MemberData(nameof(FacesAndSizes))]
        public void TheWholeRepertoire_CoversTheSamePixelsAsWindows(bool bold, bool italic, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no Segoe UI bold={bold} italic={italic}");

            // SYNTHESISE THE STYLE THE FILE DOES NOT DECLARE, exactly as the renderer does.
            // Without this the suite is measuring nothing for any face that ships no
            // italic: FontFiles.Find falls back to the upright file, we draw it upright,
            // and GDI shears its own -- Tahoma's italic rows came out six to twenty times
            // worse than its roman for that reason alone, which reads exactly like a
            // hinting catastrophe and is a bug in the test.
            byte[] bytes = File.ReadAllBytes(file!);
            FontFiles.DeclaredStyle(bytes, 0, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic);
            string style = (bold ? "b" : "") + (italic ? "i" : "");
            int baseline = ppem + 12;

            int total = 0;
            // A DENOMINATOR for the structural count, because on its own it flatters us. Structural
            // counts only pixels one side inks solidly (>=128) and the other leaves all but bare
            // (<32); the whole 32..127 band is deliberately ignored. So it is a count of pixels in
            // the WRONG PLACE, not a measure of agreement, and it has to be read against how many
            // pixels carry ink at all and how many disagree about shade.
            int shade = 0, inked = 0; long sum = 0, signed = 0; var hist = new int[6];
            long digits = 0, letters = 0, punct = 0;
            var byLevel = new long[3]; var atLevel = new int[3];
            var byEdge = new long[3]; var atEdge = new int[3];
            var report = new System.Text.StringBuilder();
            foreach (string text in Repertoire)
            {
                byte[] windows = Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height,
                                          bold, italic);
                byte[] ours = Ours(font, text, ppem, baseline);
                Difference diff = Difference.Between(windows, ours, Width, Height);
                shade += diff.Pixels;
                total += diff.Pixels;
                sum += diff.Total;
                // Split by what the string IS. Keeping the x fitting improves the window's
                // MonthCalendar (all digits) and worsens its TreeView and DataGridView (words), so
                // the question is whether the fitting suits some glyph classes and not others --
                // which an aggregate over the whole repertoire cannot answer.
                if (char.IsDigit(text[0])) digits += diff.Total;
                else if (char.IsLetter(text[0])) letters += diff.Total;
                else punct += diff.Total;
                for (int i = 0; i < windows.Length; i++)
                {
                    int d = Math.Abs(windows[i] - ours[i]);
                    if (d == 0) continue;
                    // SIGNED as well as absolute: a large mean |d| with a signed mean near zero is
                    // ink in the wrong PLACE, and one with a signed mean to match is ink of the
                    // wrong WEIGHT. The two want completely different work.
                    signed += ours[i] - windows[i];
                    // WHERE the disagreement sits. A solid pixel differing is a blend or curve
                    // fault; an edge pixel differing is the rasterizer splitting coverage
                    // differently. They are separate problems and the totals hide which one it is.
                    int band = windows[i] < 32 ? 0 : windows[i] < 224 ? 1 : 2;
                    byLevel[band] += d; atLevel[band]++;

                    // WHICH KIND OF EDGE it sits on, read off GDI's own image. A vertical edge (ink
                    // changing left-to-right) is split between LAMPS; a horizontal one (changing
                    // top-to-bottom) is decided by the vertical sampling. They are different code
                    // and the totals cannot tell them apart.
                    int x = i % Width, y = i / Width;
                    if (x > 0 && x < Width - 1 && y > 0 && y < Height - 1)
                    {
                        int gx = Math.Abs(windows[i + 1] - windows[i - 1]);
                        int gy = Math.Abs(windows[i + Width] - windows[i - Width]);
                        int kind = gx > gy * 2 ? 0 : gy > gx * 2 ? 1 : 2;
                        byEdge[kind] += d; atEdge[kind]++;
                    }
                    hist[d < 8 ? 0 : d < 16 ? 1 : d < 32 ? 2 : d < 64 ? 3 : d < 128 ? 4 : 5]++;
                }
                foreach (byte v in windows) if (v >= 32) inked++;
                if (diff.Pixels == 0) continue;

                report.AppendLine(diff.Describe(text, windows, ours, Width, Height));
                // WPF_ALLOW_REPORT_GROUPS=1: one line per repertoire GROUP, not just the total.
                // The totals answer "did this get worse" and nothing else; when a change moves a
                // ratchet by ONE pixel the only useful next question is which group, and that was
                // unanswerable because the per-group report is printed only on failure -- so the
                // run you want to compare against is exactly the one that does not print it.
                if (Environment.GetEnvironmentVariable("WPF_ALLOW_REPORT_GROUPS") == "1")
                    RecordActual($"grp@{ppem}{style}[{Repertoire.AsSpan().IndexOf(text)}]",
                                 diff.Pixels);
            }

            string? rep = Environment.GetEnvironmentVariable("WPF_STRUCT_REPORT");
            if (!string.IsNullOrEmpty(rep))
                lock (Repertoire)
                    File.AppendAllText(rep!, $"{(style.Length == 0 ? "regular" : style)}@{ppem} {total}"
                                            + $" shade={shade} gdiInked={inked} sum={sum}"
                                            + $" signed={signed} digits={digits} letters={letters}"
                                            + $" punct={punct} hist={string.Join("/", hist)}"
                                            + $" band={string.Join("/", byLevel)}"
                                            + $" bandN={string.Join("/", atLevel)}"
                                            + $" edge={string.Join("/", byEdge)}"
                                            + $" edgeN={string.Join("/", atEdge)}"
                                            + Environment.NewLine);
            RecordActual($"repertoire@{ppem}{style}", total);
            int allowed = AllowanceFor($"repertoire@{ppem}{style}");
            Assert.True(total <= allowed,
                        $"Segoe UI {(style.Length == 0 ? "regular" : style)} at {ppem} pixels an em: "
                        + $"{total} pixels covered differently.\n" + report);
            Assert.True(total >= allowed,
                        $"the whole repertoire {style} at {ppem} now matches Windows in "
                        + $"{allowed - total} more pixels than Allowed says it does -- lower its "
                        + "entry, or drop it.");
        }

        /// <summary>The same string through our renderer: greyscale coverage of black on white, at
        /// the same origin GDI was given.</summary>
        /// <summary>The same run as Ours(), but the whole RGBA buffer rather than one lamp.</summary>
        /// <summary>STAGE CR: our ClearType pipeline fed GDI'S OWN fitted outline, against GDI's
        /// ClearType pixels. Reported only -- set WPF_STAGECR_REPORT.
        /// <para>Every split before this one was taken at the GREYSCALE stage, which is not what we
        /// ship. Stage C compares our whole pipeline against GDI's whole pipeline and cannot say
        /// which half disagrees. This gives both sides the SAME geometry -- GDI's -- so what is left
        /// is our lamp sampling, our filter and our contrast curve, and nothing else. The gap
        /// between C and CR is the geometry's contribution.</para>
        /// <para>It matters because the outline, the rasterizer, the lamp phase and the total ink
        /// are all already measured correct, and GDI is known to RENDER a shape wider than the one
        /// it hands out -- so "geometry" and "filter" are the only two candidates left and nobody
        /// has priced them separately.</para>
        /// <para>THE ANSWER, and it is a negative one:</para>
        /// <code>  face      ppem    C (ours)   CR (GDI's geometry)   change
        ///         Segoe UI    11     870,909        817,154          -6.2%
        ///         Segoe UI    12     955,027        925,906          -3.0%
        ///         Segoe UI    13   1,042,012        970,736          -6.8%
        ///         Segoe UI    16   1,405,608      1,531,357          +8.9%
        ///         Arial       11     979,609      1,181,375         +20.6%
        ///         Arial       12   1,035,507      1,003,587          -3.1%
        ///         Arial       13   1,167,355      1,156,474          -0.9%
        ///         Arial       16   1,647,462      1,687,475          +2.4%</code>
        /// <para>Handing our ClearType pipeline a DIFFERENT glyph geometry moves the disagreement by
        /// a few percent and as often up as down. Roughly a million of it survives either way, so
        /// the mass is in the lamps, the filter and the contrast curve -- not in which of the two
        /// outlines they are fed.</para>
        /// <para>ONE CAVEAT, and it is not small: GGO_NATIVE hands out an outline GDI does not
        /// draw -- its rendered stems are wider (see GdiStageTests' column profiles). So CR does not
        /// equalise the geometry, it swaps ours for a THIRD one. What it establishes is weaker than
        /// intended but still useful: the ClearType output is insensitive to geometry differences of
        /// this size, which caps what any amount of stem-fitting work can win.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        public void StageCR_ClearTypeAgainstGdisOwnOutline(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STAGECR_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STAGECR_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}  (stage CR: same geometry, our ClearType against GDI's)");
            report.AppendLine("ppem   stage                        differing      sum|d|   mean|d|");

            foreach (int ppem in new[] { 11, 12, 13, 16 })
            {
                int baseline = ppem + 12;
                long cPix = 0, cSum = 0, rPix = 0, rSum = 0;
                var raw = new byte[Width * Height * 4];
                foreach (string group in Repertoire)
                    foreach (char ch in group)
                    {
                        // Gdi.Draw's RETURN value is not the pixels -- the raw RGB comes back through
                        // s_rawRgb, which is how StageC does it. Comparing the return value gave a
                        // mean |d| of 764.7 out of 765: every pixel maximally wrong, which is the
                        // signature of comparing against the wrong buffer rather than of a bug.
                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(ch.ToString(), family, ppem, PenX, baseline, Width, Height, false, false);
                        Gdi.s_rawRgb = null;
                        byte[] ours = OursRgba(font, ch.ToString(), ppem, baseline, correction: true);
                        byte[] fromGdi = OursRgbaFromFigures(
                            GdiStageTests.GdiOutlineAt(ch, family, ppem, PenX, baseline), font);
                        Tally(ours, raw, ref cPix, ref cSum);
                        Tally(fromGdi, raw, ref rPix, ref rSum);
                    }
                report.AppendLine($"{ppem,4}   C  ours end to end        {cPix,9}  {cSum,10}  "
                                  + $"{(cPix == 0 ? 0 : (double)cSum / cPix):0.00}");
                report.AppendLine($"{ppem,4}   CR ours on GDI's outline  {rPix,9}  {rSum,10}  "
                                  + $"{(rPix == 0 ? 0 : (double)rSum / rPix):0.00}");
            }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>SOLVE FOR GDI'S FILTER, from its own pixels, with no font fitting in the way.
        /// Reported only -- set WPF_FILTER_REPORT.
        /// <para>Stage CR showed the disagreement barely moves when the geometry is swapped, so the
        /// mass is in the lamps, the filter and the contrast curve. Those have only ever been tuned
        /// against finished pixels. This measures the filter itself.</para>
        /// <para>The trick is to work at 7 and 8 ppem, BELOW Segoe UI's gasp gridfit threshold.
        /// There GDI does not fit, so the shape it renders IS the outline we can compute exactly --
        /// stage R measures 0.994 and 0.984 agreement at those sizes and nowhere else. Scaling that
        /// outline three times in x and rasterizing gives one column per lamp, which is that lamp's
        /// exact coverage; GDI's own lamp value is the answer it produced from the same input. Least
        /// squares over five taps then READS GDI'S FILTER OFF rather than guessing it.</para>
        /// <para>The alignment is SWEPT, not assumed. Solving at offset 0 alone returned taps peaked
        /// at both ends -- 0.286, 0.029, 0.181, 0.057, 0.315 -- with 70% of the variance unexplained,
        /// which is what a misaligned window looks like and not what a filter looks like. If one
        /// offset fits far better than its neighbours, that is where GDI's lamps sit against ours;
        /// if none does, the model is wrong rather than the alignment.</para>
        /// <para>THE ANSWER, once GDI's buffer is read as the BGRA it is:</para>
        /// <code>  lampOff  samples      t-2      t-1       t0      t+1      t+2      sum   unexpl
        ///        -1   16,933  -0.0033  -0.0106   0.2926   0.3235   0.2986   0.9008    7.91%
        ///         0   16,922  -0.0150   0.2983   0.3167   0.3084  -0.0091   0.8992    7.91%
        ///         1   16,924   0.2821   0.3286   0.2981  -0.0003  -0.0071   0.9014    7.93%</code>
        /// <para>Three things, none of them measured before. GDI's filter IS a three-tap box -- the
        /// outer taps come back -0.015 and -0.009, zero to the noise, so it is not the classic
        /// five-tap [1,2,3,2,1]/9 and that argument can stop. Its gain is 0.899, not 1.0: GDI's
        /// lamps are a tenth lighter than a normalised box would make them, close to the 0.88
        /// darkening the comment above PathRasterizer.SubpixelFilter already describes. And 7.91%
        /// of the variance is NOT linear -- that residue is the contrast curve, now a bounded
        /// isolated quantity instead of something tangled up with geometry and lamps.</para>
        /// <para>The offsets either side of 0 fit symmetrically worse, which is what proves the lamp
        /// mapping is right rather than merely assumed.</para>
        /// <para>AND THE CURVE, which is what that 7.91% is. With the filter known to be a box, the
        /// linear prediction for a lamp is the mean of it and its two neighbours, so binning GDI's
        /// actual lamp against that plots the transfer directly:</para>
        /// <code>   in    out   implied gamma        in    out   implied gamma
        ///        0.05  0.015      1.402          0.40  0.340      1.177
        ///        0.10  0.066      1.180          0.50  0.448      1.158
        ///        0.15  0.118      1.126          0.60  0.564      1.121
        ///        0.20  0.158      1.146          0.70  0.644      1.234
        ///        0.25  0.228      1.066          0.80  0.754      1.265</code>
        /// <para>A gamma of about 1.15 across the middle -- and we already use 1.20. So the shape of
        /// our filter, its gain and its curve are all close to what GDI measurably does, which is
        /// the same story the corrected stage C tells.</para>
        /// <para>The 1.40 in the lowest bin is NOT evidence of a toe. GDI quantises its lamps, so at
        /// coverage this faint many samples land on zero and drag the mean down; the bin is
        /// measuring the quantiser, not the curve. Do not fit a curve to that end of it.</para>
        /// </summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        public void FilterTaps_SolvedFromGdisOwnPixels(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_FILTER_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_FILTER_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no " + family);

            // Our own font too: the level histogram below needs OUR pixels as well as GDI's.
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            const int Taps = 5, Half = 2, Off = 3;
            var ata = new double[2 * Off + 1, Taps, Taps];
            var atb = new double[2 * Off + 1, Taps];
            var samplesAt = new long[2 * Off + 1];
            var sumBAt = new double[2 * Off + 1];
            var sumBSqAt = new double[2 * Off + 1];
            // THE CURVE ITSELF. With the filter known to be a three-tap box, the linear
            // prediction for a lamp is just the mean of it and its two neighbours. Binning
            // GDI's actual lamp against that prediction plots the transfer curve directly --
            // which is what the 7.91% the linear fit cannot explain actually IS.
            const int Bins = 20;
            // WHICH VALUES each side is capable of emitting. This file states in two places
            // that GDI's ClearType output holds exactly seven levels, k/6 -- and one row of
            // one glyph came back 255, 182, 144, 102, 58, which is not that set. A histogram
            // settles it, and it matters: if the two quantisers differ, every lamp that lands
            // between their levels is a disagreement no filter or curve can remove.
            var gdiLevels = new long[256];
            var ourLevels = new long[256];
            // AND HOW FAR APART, in LEVELS rather than in bytes. The claim this is testing is
            // that the residual is borderline rounding -- lamps landing either side of a
            // threshold. If that is true almost every disagreement is exactly ONE level. If
            // many are two or three, something upstream is still wrong and the claim is not.
            var ladder = new byte[] { 0, 58, 102, 144, 182, 219, 255 };
            var levelGap = new long[8];
            long lampsCompared = 0;
            // WHERE those two-or-more-level lamps are, and which WAY they go. A twelfth of
            // inked lamps being badly out is only actionable if they are somewhere: on curved
            // glyphs, or on straight ones, or spread evenly. And if they are systematically
            // ours-darker or ours-lighter that is a bias, which is a different fix from noise.
            var badByChar = new System.Collections.Generic.Dictionary<char, int>();
            long badDarker = 0, badLighter = 0;
            var curveSum = new double[Bins + 1];
            var curveN = new long[Bins + 1];
            var raw = new byte[Width * Height * 4];

            // Is GDI even drawing SUBPIXEL at these sizes? If gasp puts 7 and 8 in greyscale then
            // every lamp of a pixel carries the same value and fitting three of them to three
            // different coverages is fitting noise. (It does not: 247 coloured against 10 neutral.)
            int neutral = 0, coloured = 0;
            {
                var probe = new byte[Width * Height * 4];
                Gdi.s_rawRgb = probe;
                Gdi.Draw("Hamburgefonstiv", family, 8, PenX, 20, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                for (int i3 = 0; i3 + 3 < probe.Length; i3 += 4)
                {
                    int r = probe[i3], g = probe[i3 + 1], b2 = probe[i3 + 2];
                    if (r == 255 && g == 255 && b2 == 255) continue;
                    if (Math.Max(r, Math.Max(g, b2)) - Math.Min(r, Math.Min(g, b2)) > 8) coloured++;
                    else neutral++;
                }
            }

            foreach (int ppem in new[] { 7, 8 })
            {
                int baseline = ppem + 12;
                foreach (string group in Repertoire)
                    foreach (char ch in group)
                    {
                        List<PathFigure> figures =
                            GdiStageTests.GdiOutlineAt(ch, family, ppem, PenX, baseline);
                        if (figures.Count == 0) continue;

                        // Three times as wide: one column per lamp, each the exact area the outline
                        // covers of that lamp. NOT RasterizeSubpixel -- that has already applied OUR
                        // filter, which is the thing being measured.
                        var wide = new List<PathFigure>(figures.Count);
                        foreach (PathFigure f in figures)
                        {
                            var g = new PathFigure(new Vector2(f.Start.X * 3f, f.Start.Y)) { Closed = f.Closed };
                            foreach (PathSegment seg in f.Segments) g.Segments.Add(WidenX(seg));
                            wide.Add(g);
                        }
                        CoverageMask m = PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero, wide));
                        if (m.Coverage == null) continue;

                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(ch.ToString(), family, ppem, PenX, baseline, Width, Height, false, false);
                        Gdi.s_rawRgb = null;

                        byte[] oursPix = OursRgba(font, ch.ToString(), ppem, baseline, correction: true);
                        for (int q = 0; q + 3 < oursPix.Length; q += 4)
                            for (int qc = 0; qc < 3; qc++) ourLevels[oursPix[q + qc]]++;

                        var v = new double[Taps];
                        for (int y = 0; y < Height; y++)
                        {
                            int row = y - (int) m.OriginY;
                            if ((uint) row >= (uint) m.Height) continue;
                            for (int x = 0; x < Width; x++)
                                for (int c = 0; c < 3; c++)
                                {
                                    int lamp = 3 * x + c - (int) m.OriginX;
                                    double b = 1.0 - raw[(y * Width + x) * 4 + (2 - c)] / 255.0;
                                    for (int o = -Off; o <= Off; o++)
                                    {
                                        bool any = b > 0.004;
                                        for (int k = -Half; k <= Half; k++)
                                        {
                                            int li = lamp + o + k;
                                            double cov = (uint) li < (uint) m.Width
                                                ? m.Coverage[row * m.Width + li] / 255.0 : 0.0;
                                            v[k + Half] = cov;
                                            if (cov > 0.004) any = true;
                                        }
                                        if (!any) continue;
                                        int oi = o + Off;
                                        for (int i2 = 0; i2 < Taps; i2++)
                                        {
                                            atb[oi, i2] += v[i2] * b;
                                            for (int j = 0; j < Taps; j++) ata[oi, i2, j] += v[i2] * v[j];
                                        }
                                        samplesAt[oi]++; sumBAt[oi] += b; sumBSqAt[oi] += b * b;
                                        if (o == 0) gdiLevels[raw[(y * Width + x) * 4 + (2 - c)]]++;
                                        if (o == 0)
                                        {
                                            double pred = (v[Half - 1] + v[Half] + v[Half + 1]) / 3.0;
                                            int bin = (int) Math.Round(pred * Bins);
                                            if ((uint) bin <= Bins) { curveSum[bin] += b; curveN[bin]++; }
                                        }
                                    }
                                }
                        }
                    }
            }

            // THE LEVEL GAP IS MEASURED AT 11 TO 13, not at the 7 and 8 the filter solve uses.
            // At 7 and 8 stage C reads our ink at 1.36 and 1.33 of GDI's -- those sizes are
            // below the gridfit threshold and our text there is far heavier than Windows',
            // so a level histogram taken there measures that, not the sizes anyone reads.
            foreach (int ppem in new[] { 11, 12, 13 })
            {
                int baseline = ppem + 12;
                foreach (string group in Repertoire)
                    foreach (char ch in group)
                    {
                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(ch.ToString(), family, ppem, PenX, baseline, Width, Height, false, false);
                        Gdi.s_rawRgb = null;
                        byte[] oursPix = OursRgba(font, ch.ToString(), ppem, baseline, correction: true);
                        for (int q = 0; q + 3 < oursPix.Length; q += 4)
                            for (int qc = 0; qc < 3; qc++)
                            {
                                byte ov = oursPix[q + qc], gv = raw[q + (2 - qc)];
                                if (ov == 255 && gv == 255) continue;      // paper both sides
                                int lo = Nearest(ladder, ov), lg = Nearest(ladder, gv);
                                levelGap[Math.Min(7, Math.Abs(lo - lg))]++;
                                lampsCompared++;
                                if (Math.Abs(lo - lg) >= 2)
                                {
                                    badByChar.TryGetValue(ch, out int bc);
                                    badByChar[ch] = bc + 1;
                                    // lower level index = closer to 0 = MORE ink
                                    if (lo < lg) badDarker++; else badLighter++;
                                }
                            }
                    }
            }

            // ONE ROW OF ONE GLYPH, printed. A 70% residual with the alignment already swept is
            // the point to stop fitting and look at the numbers themselves.
            var report = new System.Text.StringBuilder();
            {
                var figs = GdiStageTests.GdiOutlineAt('n', family, 8, PenX, 20);
                var wide2 = new List<PathFigure>(figs.Count);
                foreach (PathFigure f in figs)
                {
                    var g = new PathFigure(new Vector2(f.Start.X * 3f, f.Start.Y)) { Closed = f.Closed };
                    foreach (PathSegment seg in f.Segments) g.Segments.Add(WidenX(seg));
                    wide2.Add(g);
                }
                CoverageMask mm = PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero, wide2));
                var probe2 = new byte[Width * Height * 4];
                Gdi.s_rawRgb = probe2;
                Gdi.Draw("n", family, 8, PenX, 20, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                int yy = 16;
                int rr = yy - (int) mm.OriginY;
                report.AppendLine($"   'n'@8 row {yy}   lamp: ours(exact coverage) | GDI(1 - v/255)");
                if ((uint) rr < (uint) mm.Height)
                    for (int xx = PenX - 1; xx < PenX + 8; xx++)
                        for (int cc = 0; cc < 3; cc++)
                        {
                            int li = 3 * xx + cc - (int) mm.OriginX;
                            double cov = (uint) li < (uint) mm.Width
                                ? mm.Coverage[rr * mm.Width + li] / 255.0 : 0.0;
                            double gv = 1.0 - probe2[(yy * Width + xx) * 4 + (2 - cc)] / 255.0;
                            report.AppendLine($"      x={xx,3} {"RGB"[cc]}   ours {cov,6:0.000}   GDI {gv,6:0.000}");
                        }
            }
            report.AppendLine("== " + family + "  GDI's filter, solved from its own lamps at 7 and 8 ppem");
            report.AppendLine($"   GDI at 8ppem: {coloured} coloured px, {neutral} neutral px");
            report.AppendLine("   lampOff  samples       t-2       t-1        t0       t+1       t+2       sum   unexplained");
            for (int o = -Off; o <= Off; o++)
            {
                int oi = o + Off;
                var a2 = new double[Taps, Taps];
                var b2 = new double[Taps];
                for (int i2 = 0; i2 < Taps; i2++)
                {
                    b2[i2] = atb[oi, i2];
                    for (int j = 0; j < Taps; j++) a2[i2, j] = ata[oi, i2, j];
                }
                double[] w = Solve(a2, b2, Taps);
                double explained = 0, sum = 0;
                for (int i2 = 0; i2 < Taps; i2++) { explained += w[i2] * b2[i2]; sum += w[i2]; }
                double ssTot = sumBSqAt[oi] - sumBAt[oi] * sumBAt[oi] / Math.Max(1, samplesAt[oi]);
                double ssRes = sumBSqAt[oi] - explained;
                report.Append($"   {o,7}  {samplesAt[oi],7:N0}");
                for (int i2 = 0; i2 < Taps; i2++) report.Append($"{w[i2],10:0.0000}");
                report.AppendLine($"{sum,10:0.0000}   {(ssTot <= 0 ? 0 : ssRes / ssTot):P2}");
            }
            report.AppendLine();
            // THE SAME STEM IN THE MODE WE ACTUALLY SHIP AGAINST. The claim that GDI renders a
            // shape wider than GGO_NATIVE reports was measured off GGO_GRAY8 -- GREYSCALE -- and
            // greyscale hints differently from ClearType. Summing the lamp ink of one clean stem
            // three ways settles whether that finding survives in the mode that matters.
            // THE WHOLE REPERTOIRE, not seven letters. The open question is which stems GDI
            // rounds and which it leaves alone, and that is readable off its own output: a glyph
            // we render light is one whose stem we snapped and GDI did not.
            foreach (char probe in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
            foreach (int ppem in new[] { 12 })
            {
                int baseline = ppem + 12;
                Gdi.s_rawRgb = raw;
                Gdi.Draw(probe.ToString(), family, ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                double ctInk = 0;
                for (int q = 0; q + 3 < raw.Length; q += 4)
                    for (int qc = 0; qc < 3; qc++) ctInk += (255 - raw[q + qc]) / 255.0;
                double outlineInk = 0;
                CoverageMask om = PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero,
                    GdiStageTests.GdiOutlineAt(probe, family, ppem, PenX, baseline)));
                if (om.Coverage != null) foreach (byte bb in om.Coverage) outlineInk += bb / 255.0;
                byte[] op = OursRgba(font, probe.ToString(), ppem, baseline, correction: true);
                double ourInk2 = 0;
                for (int q = 0; q + 3 < op.Length; q += 4)
                    for (int qc = 0; qc < 3; qc++) ourInk2 += (255 - op[q + qc]) / 255.0;
                report.AppendLine($"   '{probe}'@{ppem}  GDI ClearType {ctInk / 3:0.000}   "
                                  + $"GGO_NATIVE outline {outlineInk:0.000}   ours {ourInk2 / 3:0.000}");
                if (probe == 'H')
                {
                    // WHERE the missing ink is. Column lamp-ink, ours against GDI's, for the one
                    // glyph whose total is badly out while a single stem matches exactly.
                    report.AppendLine("      x     ours      GDI     diff");
                    for (int x = PenX - 1; x < PenX + 10; x++)
                    {
                        double oc = 0, gc = 0;
                        for (int y = 0; y < Height; y++)
                            for (int qc = 0; qc < 3; qc++)
                            {
                                oc += (255 - op[(y * Width + x) * 4 + qc]) / 255.0;
                                gc += (255 - raw[(y * Width + x) * 4 + qc]) / 255.0;
                            }
                        if (oc > 0.001 || gc > 0.001)
                            report.AppendLine($"   {x,4}  {oc / 3,7:0.000}  {gc / 3,7:0.000}  {(oc - gc) / 3,7:0.000}");
                    }
                }
            }
            report.AppendLine();
            report.AppendLine("   how far apart, in LEVELS of the seven-step ladder");
            report.AppendLine($"      of the two-or-more: {badDarker:N0} ours DARKER, {badLighter:N0} ours LIGHTER");
            {
                var worst = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<char, int>>(badByChar);
                worst.Sort((x, y) => y.Value.CompareTo(x.Value));
                report.Append("      worst glyphs:");
                for (int k = 0; k < Math.Min(14, worst.Count); k++)
                    report.Append($" '{worst[k].Key}'={worst[k].Value}");
                report.AppendLine();
            }
            report.AppendLine($"      lamps compared {lampsCompared:N0}");
            for (int k = 0; k < 8; k++)
                if (levelGap[k] > 0)
                    report.AppendLine($"      {k} level{(k == 1 ? " " : "s")} apart  {levelGap[k],9:N0}   {(double)levelGap[k] / Math.Max(1, lampsCompared):P2}");
            report.AppendLine();
            report.AppendLine("   distinct lamp values emitted (excluding paper and full ink)");
            int gdiDistinct = 0, ourDistinct = 0;
            for (int k = 1; k < 255; k++) { if (gdiLevels[k] > 50) gdiDistinct++; if (ourLevels[k] > 50) ourDistinct++; }
            report.AppendLine($"      GDI {gdiDistinct}   ours {ourDistinct}");
            report.Append("      GDI: ");
            for (int k = 1; k < 255; k++) if (gdiLevels[k] > 50) report.Append($"{k} ");
            report.AppendLine();
            report.Append("      ours:");
            for (int k = 1; k < 255; k++) if (ourLevels[k] > 50) report.Append($" {k}");
            report.AppendLine();
            report.AppendLine();
            report.AppendLine("   GDI's transfer curve: box-average coverage in, lamp coverage out");
            report.AppendLine("      in     out    out/in   samples");
            for (int k = 0; k <= Bins; k++)
            {
                if (curveN[k] < 20) continue;
                double inv = (double) k / Bins, outv = curveSum[k] / curveN[k];
                report.AppendLine($"   {inv,6:0.000}  {outv,6:0.000}  {(inv <= 0 ? 0 : outv / inv),8:0.000}   {curveN[k],7:N0}");
            }
            File.AppendAllText(path!, report.ToString());

            PathSegment WidenX(PathSegment seg) => seg switch
            {
                LineSegment l => new LineSegment(new Vector2(l.Point.X * 3f, l.Point.Y)),
                QuadraticBezierSegment q => new QuadraticBezierSegment(
                    new Vector2(q.Control.X * 3f, q.Control.Y), new Vector2(q.Point.X * 3f, q.Point.Y)),
                CubicBezierSegment cu => new CubicBezierSegment(
                    new Vector2(cu.Control1.X * 3f, cu.Control1.Y),
                    new Vector2(cu.Control2.X * 3f, cu.Control2.Y),
                    new Vector2(cu.Point.X * 3f, cu.Point.Y)),
                _ => seg,
            };
        }

        /// <summary>Which rung of GDI's seven-step ladder a lamp value sits on.</summary>
        private static int Nearest(byte[] ladder, byte v)
        {
            int best = 0, bd = 999;
            for (int i = 0; i < ladder.Length; i++)
            {
                int d = Math.Abs(ladder[i] - v);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        private static double[] Solve(double[,] a, double[] b, int n)
        {
            var m = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) m[i, j] = a[i, j];
                m[i, n] = b[i];
            }
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-12) continue;
                if (piv != col)
                    for (int j = 0; j <= n; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col] / m[col, col];
                    for (int j = col; j <= n; j++) m[r, j] -= f * m[col, j];
                }
            }
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0 : m[i, n] / m[i, i];
            return x;
        }

        /// <summary>Ours (RGBA) against GDI's raw buffer, which is B G R A -- s_rawRgb is a straight
        /// Marshal.Copy out of a Windows DIB and its name is a lie. Indexing both sides the same way
        /// compares our RED lamp against GDI's BLUE one, which for subpixel text is comparing
        /// opposite edges of the same stem.</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr AddFontMemResourceEx(byte[] font, int cb, IntPtr pdv, ref int numFonts);

        [DllImport("gdi32.dll")]
        private static extern bool RemoveFontMemResourceEx(IntPtr h);

        /// <summary>ASK GDI DIRECTLY what its interpreter does with one MIRP.
        /// Reported only -- set WPF_ORACLE_REPORT.
        /// <para>Everything else here reads a shipping face and infers the rules from glyphs someone
        /// else hinted. That has a floor: when GDI and we disagree about a stem, the font's program,
        /// its pre-program, its control values and two interpreters are all in the way at once, and
        /// no measurement over real text separates them.</para>
        /// <para>So build a font with nothing in it. One contour, four points, and six instructions:
        /// SVTCA[x], MDAP[R] on the left edge, one MIRP moving the right edge to a control value,
        /// IUP[x]. Sweep the control value across the sub-pixel range, one glyph per value, and
        /// render it through GDI. The width that comes back IS the interpreter's answer, with no
        /// font left to argue about -- and the same font through ours answers the same question.
        /// The two columns are the rounding rule, read off both implementations.</para>
        /// <para>WHAT THIS SHOWS, STATED CAREFULLY. With the round bit off GDI returns the exact
        /// fractional width and we return a whole pixel (48 of 49 swept values); with it on we
        /// agree except that our threshold sits 15/64 of a pixel early. And on a program that
        /// contains no y instruction at all, GDI leaves y at 8.2031 pixels where we return 8.
        /// So we are imposing a whole-pixel grid on both axes that the program did not ask for.
        /// <para>Whether each half of that is a BUG is a separate question, and the y half is
        /// not settled here. ClearType legitimately grid-fits y -- that is what this port's
        /// vertical-only hinting is -- so a whole-pixel y may be right and GGO_NATIVE, which
        /// this session established is NOT GDI's ClearType geometry, may simply be answering a
        /// different question. The x half does not have that defence: ClearType is sub-pixel in
        /// x by definition, so a whole-pixel x on a MIRP that asked for no rounding is a real
        /// disagreement with the mode we ship in.</para>
        /// <para>Ruled out as the source, each measured against this oracle rather than reasoned
        /// about: RoundDistance (the round bit is off), SnapX and plainX (not reached at
        /// XHintMode 5), CompatibleWidthMode, LsbSnapMode, SmallGlyphPixels, XPixelWidths and
        /// XOutlineWidths (modes 8/13/14/16, not 5), the 26.6 scaling into the zone (MulFix, no
        /// rounding), StoreGlyph on the way out (a plain copy), and FitIsPlausible (2px slack,
        /// this bar passes). The snap is somewhere else.</para>
        /// <para>THE SHARPEST TEST THIS ALLOWS. With s_stemFat off the interpreter's output is
        /// IDENTICAL to GDI's on all 49 unrounded values, so any difference in the rendered
        /// pixels is downstream of the interpreter by construction. It is large:</para>
        /// <code>  cvt     GDIink  ourInk        cvt     GDIink  ourInk
        ///        1.0000   0.924   0.924        1.4688   1.058   1.412
        ///        1.1563   0.969   1.078        1.6250   1.058   1.591
        ///        1.3125   1.058   1.258        1.7031   1.058   1.591
        ///        49 rows, mean +0.287px, worst +0.533</code>
        /// <para>GDI's ink STOPS GROWING at 1.058 pixels: a stem of geometric width 1.7 renders
        /// with the same ink as one of 1.3. Ours grows continuously, because keeping x
        /// sub-pixel is this port's deliberate design. 1.058 is about what a ONE-pixel stem
        /// renders as, and one pixel is what GGO_NATIVE reports for every one of these glyphs,
        /// so the reading is that GDI rounds the stem in ClearType and not only in bi-level.
        /// None of the four CompatibleWidthMode settings reproduces it (0 and 1 give +0.287,
        /// 3 gives +0.254, 2 is worse at +0.528).</para>
        /// <para>HELD OPEN, because it contradicts a measurement already taken: if GDI rounded
        /// stems in ClearType then Segoe UI 'l' at 12ppem would not measure 9.706 on both sides,
        /// and it does, exactly, at four sizes. Either this synthetic font provokes a path real
        /// faces do not take, or 'l' agrees by landing on a whole pixel anyway. Settling that
        /// either confirms this port's central x-sub-pixel decision or overturns it, so it is
        /// worth doing properly rather than acting on one probe.</para></summary>
        [Fact]
        public void MirpOracle_WhatGdiDoesWithOneControlValue()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? path = Environment.GetEnvironmentVariable("WPF_ORACLE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_ORACLE_REPORT to collect this");

            const int Ppem = 12;
            const string Family = "WpfOracleProbe";
            double unitsPerPixel = SyntheticFont.UnitsPerEm / (double)Ppem;   // 170.667 at 12ppem

            // Sweep 0.55px to 1.95px in sixteenths of a pixel, rounded and unrounded.
            var bars = new List<SyntheticFont.Bar>();
            var wanted = new List<double>();
            // Sixty-fourths across the boundary, because at sixteenths the two interpreters
            // already disagree between 1.25 and 1.5 and the question is where exactly each
            // one flips.
            foreach (bool round in new[] { true, false })
                for (int sixtyfourth = 64; sixtyfourth <= 112; sixtyfourth++)
                {
                    double px = sixtyfourth / 64.0;
                    int units = (int)Math.Round(px * unitsPerPixel);
                    bars.Add(new SyntheticFont.Bar(units, 400, 400 + units, round, minDistance: false));
                    wanted.Add(px);
                }

            // THE CONTROL: the same widths with NO PROGRAM. If GDI renders these at their
            // fractional widths then it does not force-round a stem, and the saturation in the
            // rows above belongs to the MIRP path. If it rounds these too, it rounds
            // everything, and this port's x-sub-pixel design is what is wrong.
            for (int sixtyfourth = 64; sixtyfourth <= 112; sixtyfourth++)
            {
                double px = sixtyfourth / 64.0;
                int units = (int)Math.Round(px * unitsPerPixel);
                bars.Add(new SyntheticFont.Bar(units, 400, 400 + units, false, false, noProgram: true));
                wanted.Add(px);
            }

            byte[] fontBytes = SyntheticFont.Build(Family, bars);
            File.WriteAllBytes(path + ".ttf", fontBytes);   // for inspection when GDI refuses
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            int lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Assert.True(handle != IntPtr.Zero && count > 0,
                        "GDI refused the synthetic font: handle=" + handle + " fonts=" + count
                        + " err=" + lastErr + " bytes=" + fontBytes.Length);

            var report = new System.Text.StringBuilder();
            try
            {
                var font = new TrueTypeFont(fontBytes);
                report.AppendLine($"== one MIRP, {Ppem}ppem, ClearType -- what each interpreter makes of it");
                // Ink columns are quantised by the rasterizer to about a sixth of a pixel.
                // The OUTLINE columns are not quantised at all: GGO_NATIVE hands back GDI's
                // fitted points, and TryGetFittedOutline hands back ours, so those two are
                // the interpreters answering the same question exactly.
                report.AppendLine("   round   cvt(px)  GDIink  ourInk  |  GDIfit  ourFit   fit diff");
                var raw = new byte[Width * Height * 4];
                for (int i = 0; i < bars.Count; i++)
                {
                    string ch = ((char)(0x41 + i)).ToString();
                    int baseline = Ppem + 12;
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Family, Ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    double gdi = BarWidth(raw, bgra: true);
                    double ours = BarWidth(OursRgba(font, ch, Ppem, baseline, correction: true), bgra: false);
                    double gdiFit = XExtent(GdiStageTests.GdiOutlineAt(ch[0], Family, Ppem, 0, 0));
                    // TryGetHintedOutline, NOT TryGetFittedOutline. The first runs the face's own
                    // program through the interpreter and only falls back to GlyphHinter -- the
                    // autofitter, whose Round is Floor(x + 0.5) and therefore snaps to whole
                    // pixels by design -- when the face's hints fail or come out implausible.
                    // Calling the inner one measured the autofitter and called it our interpreter.
                    double ourFit = font.TryGetHintedOutline(font.GlyphIndex(ch[0]), Ppem, out List<PathFigure> f)
                        ? XExtent(f) : 0;
                    if (gdi <= 0 && ours <= 0) continue;
                    if (bars[i].NoProgram && (i == 98 || i == 110 || i == 122 || i == 134))
                    {
                        // The lamps themselves, for a bar whose geometry both sides agree on
                        // EXACTLY. If GDI samples each lamp in-or-out at six positions its
                        // ANSWERED, and the answer is that they are IDENTICAL -- every lamp,
                        // every width, byte for byte. Our ClearType coverage chain is exact
                        // against GDI's on geometry the two agree about, which retires the
                        // filter, the gain, the curve and the quantiser all at once.
                        //
                        // The 0.136px that BarWidth reported was never horizontal. BarWidth
                        // divides ink by the number of INKED ROWS, and the row profiles show
                        // GDI with a ninth faint row on top -- 0.094 against eight identical
                        // full rows -- where we have eight and nothing above them. The glyph
                        // spans -8.2031 to 0 on BOTH sides, so that row is real geometry: we
                        // sample a lamp once, at the scanline centre, and the top row's
                        // centre at -8.5 misses it. That is SubpixelRows, and it is a
                        // measured decision (see PathRasterizer) that one row beats four on
                        // real text, precisely because y hinting leaves no partial rows.
                        // This font has no program, so it manufactures the one case where
                        // the decision costs -- the probe built its own residual.
                        // pre-filter coverage can only be a sixth at a time; if it integrates
                        // area, it is continuous. The filter is a box and preserves the total,
                        // so the difference survives to here and is visible in the profile.
                        report.AppendLine($"      [w={wanted[i]:0.0000}] GDI lamps: {LampProfile(raw, true)}");
                        report.AppendLine($"      [w={wanted[i]:0.0000}] our lamps: {LampProfile(OursRgba(font, ch, Ppem, baseline, correction: true), false)}");
                        report.AppendLine($"      [w={wanted[i]:0.0000}] GDI rows : {RowProfile(raw, true)}");
                        report.AppendLine($"      [w={wanted[i]:0.0000}] our rows : {RowProfile(OursRgba(font, ch, Ppem, baseline, correction: true), false)}");
                    }
                    if (i == 4 || i == 8 || i == 110 || i == 122)
                    {
                        // The coordinates themselves, for the two glyphs either side of where
                        // the two interpreters part company. If ours are whole in Y as well as
                        // X then something rounds the entire outline; if only X, it is the
                        // horizontal path.
                        report.AppendLine($"      [{i}] GDI xs: {Coords(GdiStageTests.GdiOutlineAt(ch[0], Family, Ppem, 0, 0), true)}");
                        report.AppendLine($"      [{i}] GDI ys: {Coords(GdiStageTests.GdiOutlineAt(ch[0], Family, Ppem, 0, 0), false)}");
                        report.AppendLine($"      [{i}] our xs: {Coords(f, true)}");
                        report.AppendLine($"      [{i}] our ys: {Coords(f, false)}");
                    }
                    report.AppendLine($"   {(bars[i].NoProgram ? "non" : bars[i].Round ? "yes" : "no ")}   {wanted[i],7:0.0000}"
                                      + $"  {gdi,6:0.000}  {ours,6:0.000}  |"
                                      + $"  {gdiFit,6:0.000}  {ourFit,6:0.000}  {ourFit - gdiFit,9:0.000}");
                }
            }
            finally { RemoveFontMemResourceEx(handle); }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>IS THE COVERAGE CHAIN EXACT EVERYWHERE, or only where it was checked?
        /// Reported only -- set WPF_FLOOR_REPORT.
        /// <para>The lamp comparison that retired the filter, the gain, the curve and the quantiser
        /// was one size, one shape, one background. That is enough to say those knobs are not
        /// merely at a LOCAL optimum -- an exact match cannot be improved on, and moving any of
        /// them away breaks it -- but only over the ground it covered. A knob sweep that looks flat
        /// at 12ppem can still be wrong at 9 or 18, and then the right answer is a joint move that
        /// no single sweep from the current setting would ever find.</para>
        /// <para>So walk the floor: every size the control window actually uses, bars at a spread
        /// of sub-pixel widths, and demand our lamps equal GDI's EXACTLY. Anything less than every
        /// row exact says the floor is not level and there is a global optimum still to find.</para>
        /// </summary>
        [Fact]
        public void CoverageFloor_ExactAtEverySizeTheWindowUses()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? path = Environment.GetEnvironmentVariable("WPF_FLOOR_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_FLOOR_REPORT to collect this");

            const string Family = "WpfFloorProbe";
            // One font, many sizes. Widths in FONT UNITS, so each renders at a different fraction
            // of a pixel at each ppem -- which is the point: the sub-pixel phase has to vary.
            var bars = new List<SyntheticFont.Bar>();
            for (int units = 96; units <= 288; units += 12)
                bars.Add(new SyntheticFont.Bar(units, 400, 400 + units, false, false, noProgram: true));

            byte[] fontBytes = SyntheticFont.Build(Family, bars);
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI would not accept the synthetic font");

            var report = new System.Text.StringBuilder();
            try
            {
                var font = new TrueTypeFont(fontBytes);
                report.AppendLine("== is our lamp coverage GDI's, at every size the window uses?");
                report.AppendLine("   ppem   lamps cmp  differing   solo   shift   offset in LAMPS (ours - GDI)");
                var raw = new byte[Width * Height * 4];
                for (int ppem = 8; ppem <= 20; ppem++)
                {
                    // TWO axes, kept apart. A row BOTH sides ink is the horizontal coverage chain
                    // answering the same question -- filter, gain, curve, quantiser. A row only one
                    // side inks is the vertical one, which is SubpixelRows and already understood.
                    // Added together they say the floor is not level and hide which half is not.
                    long compared = 0, differing = 0, worst = 0, soloRows = 0, shifted = 0;
                    var offsets = new long[17];
                    for (int i = 0; i < bars.Count; i++)
                    {
                        string ch = ((char)(0x41 + i)).ToString();
                        int baseline = ppem + 12;
                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(ch, Family, ppem, PenX, baseline, Width, Height, false, false);
                        Gdi.s_rawRgb = null;
                        byte[] ours = OursRgba(font, ch, ppem, baseline, correction: true);
                        for (int y = 0; y < Height; y++)
                        {
                            bool gRow = false, oRow = false;
                            for (int x = 0; x < Width && !(gRow && oRow); x++)
                                for (int c = 0; c < 3; c++)
                                {
                                    if (raw[(y * Width + x) * 4 + (2 - c)] != 255) gRow = true;
                                    if (ours[(y * Width + x) * 4 + c] != 255) oRow = true;
                                }
                            if (gRow != oRow) { soloRows++; continue; }
                            if (!gRow) continue;
                            // A THIRD axis hides in here. A bar that lands on a different lamp
                            // COLUMN disagrees by 255 at both ends while its coverage is the same
                            // shape -- that is placement, not the filter or the curve. Compare the
                            // inked run against the inked run, and count the offset separately.
                            int[] gLamp = Lamps(raw, y, true), oLamp = Lamps(ours, y, false);
                            int gs = First(gLamp), os = First(oLamp);
                            if (gs < 0 || os < 0) continue;
                            if (gs != os) { shifted++; offsets[Math.Clamp(os - gs + 8, 0, 16)]++; continue; }
                            int len = Math.Max(gLamp.Length - gs, oLamp.Length - os);
                            for (int k = 0; k < len; k++)
                            {
                                int g = gs + k < gLamp.Length ? gLamp[gs + k] : 255;
                                int o = os + k < oLamp.Length ? oLamp[os + k] : 255;
                                compared++;
                                if (g == o) continue;
                                differing++;
                                worst = Math.Max(worst, Math.Abs(g - o));
                            }
                        }
                    }
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int k = 0; k < offsets.Length; k++)
                            if (offsets[k] > 0) sb.Append($" {k - 8:+0;-0;0}:{offsets[k]}");
                        report.AppendLine($"   {ppem,4}   {compared,10:N0}   {differing,8:N0}   {soloRows,6}   {shifted,6}   lamps{sb}");
                    }
                }
            }
            finally { RemoveFontMemResourceEx(handle); }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>ARE OUR GLYPHS THE RIGHT HEIGHT? Reported only -- set WPF_HEIGHT_REPORT.
        /// <para>Error per inked pixel runs 11.5 at 10ppem, 27.9 / 26.3 / 27.5 at 11, 12 and 13,
        /// then 11.1 / 10.4 / 9.8 at 14, 15, 16. Three sizes are three times worse than their
        /// neighbours, and the excess is disproportionately on HORIZONTAL edges -- 0.33 to 0.42 of
        /// the vertical-edge error against 0.15 to 0.17 at 16 and 17. A horizontal edge is decided
        /// by where the outline's top and bottom land, so the first thing to ask is whether our
        /// glyphs are simply the wrong HEIGHT at those sizes.</para>
        /// <para>hdmx, gasp, all three delta modes and four vertical samples have each been tried
        /// and none of them is it.</para></summary>
        [Fact]
        public void HeightProfile_WhereTheBadBandIs()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_HEIGHT_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_HEIGHT_REPORT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine("== ink rows, GDI against ours (top..bottom), Segoe UI regular");
            report.AppendLine("   ppem  glyph   GDI rows    our rows    dTop dBot");
            // 18-20 added when ppem 20 became the one size the face's symmetric branch turns on
            // at, and the one size we render too heavy. A report that stops short of the defect
            // cannot be pointed at it.
            foreach (int ppem in new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 })
            {
                int baseline = ppem + 12;
                foreach (char ch in "HIlnmuETioBDKN")
                {
                    string t = ch.ToString();
                    byte[] w = Gdi.Draw(t, ProbeFamily(), ppem, PenX, baseline, Width, Height);
                    byte[] o = Ours(font, t, ppem, baseline);
                    (int wt, int wb) = InkRows(w);
                    (int ot, int ob) = InkRows(o);
                    if (ppem == 12 || ((ppem == 16 || ppem == 20) && ch == 'H'))
                    {
                        // THE LAMPS THEMSELVES, so the stem edges can be read in thirds of a
                        // pixel instead of inferred from a row total. Ink per row says GDI's
                        // stem is wider than ours at 12ppem and identical at 16; only the lamp
                        // pattern says WHERE the two edges are, and a stem that starts on a lamp
                        // boundary looks nothing like one that straddles it.
                        var raw = new byte[Width * Height * 4];
                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(t, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                        Gdi.s_rawRgb = null;
                        int mid = (wt + wb) / 2 == wt + 4 ? wt + 3 : wt + 1;   // a plain stem row
                        report.AppendLine($"      [H@{ppem}] GDI lamps: {LampRow(raw, mid, true)}"
                            + $"   (two stems total {LampTotal(raw, mid, true) / 3.0:0.000}px)");
                        report.AppendLine($"      [H@{ppem}] our lamps: "
                            + LampRow(OursRgba(font, t, ppem, baseline, correction: true), mid, false)
                            + $"   (two stems total {LampTotal(OursRgba(font, t, ppem, baseline, correction: true), mid, false) / 3.0:0.000}px)");
                    }
                    if ((ppem == 12 || ppem == 16) && (ch == 'o' || ch == 'H'))
                    {
                        // The extents match at every size, so whatever is wrong with the horizontal
                        // edges is INSIDE the glyph. Ink per row says where.
                        report.AppendLine($"      [{ch}@{ppem}] GDI rows: {RowInk(w, wt, wb)}");
                        report.AppendLine($"      [{ch}@{ppem}] our rows: {RowInk(o, wt, wb)}");
                    }
                    report.AppendLine($"   {ppem,4}  '{ch}'    {wt,3}..{wb,-3}     {ot,3}..{ob,-3}"
                                      + $"    {ot - wt,+3} {ob - wb,+3}"
                                      + (ot != wt || ob != wb ? "   <-- differs" : ""));
                }
            }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>All the ink in one row, in lamps.</summary>
        private static double LampTotal(byte[] rgba, int y, bool bgra)
        {
            double t = 0;
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                    t += (255 - rgba[(y * Width + x) * 4 + (bgra ? 2 - c : c)]) / 255.0;
            return t;
        }

        /// <summary>One row's lamps as coverage out of 255, from the first inked lamp.</summary>
        private static string LampRow(byte[] rgba, int y, bool bgra)
        {
            var sb = new System.Text.StringBuilder();
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int v = 255 - rgba[(y * Width + x) * 4 + (bgra ? 2 - c : c)];
                    if (v > 0 || sb.Length > 0) sb.Append(v.ToString()).Append(' ');
                }
            string t = sb.ToString().TrimEnd();
            return t.Length > 150 ? t.Substring(0, 150) : t;
        }

        /// <summary>Ink per row, as a fraction of a fully covered pixel.</summary>
        private static string RowInk(byte[] mask, int top, int bottom)
        {
            var sb = new System.Text.StringBuilder();
            for (int y = top; y <= bottom; y++)
            {
                double ink = 0;
                for (int x = 0; x < Width; x++) ink += mask[y * Width + x] / 255.0;
                sb.Append(ink.ToString("0.00")).Append("  ");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>First and last row carrying any ink.</summary>
        private static (int, int) InkRows(byte[] mask)
        {
            int top = -1, bottom = -1;
            for (int y = 0; y < Height; y++)
            {
                bool inked = false;
                for (int x = 0; x < Width && !inked; x++)
                    if (mask[y * Width + x] > 8) inked = true;
                if (!inked) continue;
                if (top < 0) top = y;
                bottom = y;
            }
            return (top, bottom);
        }

        /// <summary>One row of lamps, left to right, as the value each carries.</summary>
        private static int[] Lamps(byte[] rgba, int y, bool bgra)
        {
            var v = new int[Width * 3];
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                    v[x * 3 + c] = rgba[(y * Width + x) * 4 + (bgra ? 2 - c : c)];
            return v;
        }

        /// <summary>The first lamp carrying any ink, or -1.</summary>
        private static int First(int[] lamps)
        {
            for (int i = 0; i < lamps.Length; i++) if (lamps[i] != 255) return i;
            return -1;
        }

        /// <summary>What advance do we give a digit, and where does it come from?
        /// <para>Drawn through this port, a run of digits is a pixel per glyph wider than GDI's:
        /// '9999' spans 28 pixels of ink against GDI's 24, so our advance is 7 where GDI's is 6.
        /// Segoe UI's hmtx has 1104 units for '0', which at 12ppem is 6.469 and rounds to 6, and
        /// its hdmx row for 12 says 6 as well -- so both sources agree with GDI and the question
        /// is which one we are actually reading. Reported only: WPF_DIGITADV.</para></summary>
        [Fact]
        public void DigitAdvance_WhereItComesFrom()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_DIGITADV");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_DIGITADV to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine("== digit advances");
            report.AppendLine("   ppem  ch  gid   device?  device   linear   rounded");
            foreach (int ppem in new[] { 11, 12, 13 })
                foreach (char ch in "019")
                {
                    int gid = font.GlyphIndex(ch);
                    bool ok = font.TryGetDeviceAdvance(gid, ppem, out float dev);
                    float lin = font.LinearAdvanceForTest(gid, ppem);
                    report.AppendLine($"   {ppem,4}  '{ch}' {gid,4}   {ok,-6}  {dev,6:0.000}"
                                      + $"  {lin,7:0.000}  {MathF.Round(lin),7:0}");
                }
            File.AppendAllText(path!, report.ToString());
        }
        /// <summary>The inked lamps of the busiest row, left to right, as coverage out of 255.
        /// One number per LAMP, not per pixel, so the three of a pixel are consecutive.</summary>
        private static string LampProfile(byte[] rgba, bool bgra)
        {
            int best = -1; double bestInk = 0;
            for (int y = 0; y < Height; y++)
            {
                double ink = 0;
                for (int x = 0; x < Width; x++)
                    for (int c = 0; c < 3; c++)
                        ink += 255 - rgba[(y * Width + x) * 4 + (bgra ? 2 - c : c)];
                if (ink > bestInk) { bestInk = ink; best = y; }
            }
            if (best < 0) return "(none)";
            var sb = new System.Text.StringBuilder();
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int v = 255 - rgba[(best * Width + x) * 4 + (bgra ? 2 - c : c)];
                    if (v > 0 || sb.Length > 0) sb.Append(v.ToString()).Append(' ');
                }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Total ink per ROW, top to bottom, in pixels. BarWidth divides by the number
        /// of inked rows, so a bar that is a different HEIGHT reads as a different WIDTH.</summary>
        private static string RowProfile(byte[] rgba, bool bgra)
        {
            var sb = new System.Text.StringBuilder();
            for (int y = 0; y < Height; y++)
            {
                double ink = 0;
                for (int x = 0; x < Width; x++)
                    for (int c = 0; c < 3; c++)
                        ink += (255 - rgba[(y * Width + x) * 4 + (bgra ? 2 - c : c)]) / 255.0;
                if (ink > 0.001 || sb.Length > 0) sb.Append((ink / 3.0).ToString("0.000")).Append(' ');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Every distinct coordinate in a figure, as text.</summary>
        private static string Coords(List<PathFigure> figures, bool x)
        {
            var seen = new SortedSet<double>();
            foreach (PathFigure fg in figures)
            {
                seen.Add(Math.Round(x ? fg.Start.X : fg.Start.Y, 4));
                foreach (PathSegment seg in fg.Segments)
                    if (seg is LineSegment l) seen.Add(Math.Round(x ? l.Point.X : l.Point.Y, 4));
            }
            return string.Join(" ", seen);
        }

        /// <summary>How far a figure spans in x. For the synthetic bar that is the stem width
        /// the interpreter decided on, with no rasterizer in between.</summary>
        private static double XExtent(List<PathFigure> figures)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (PathFigure f in figures)
            {
                void See(Vector2 v) { if (v.X < lo) lo = v.X; if (v.X > hi) hi = v.X; }
                See(f.Start);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: See(l.Point); break;
                        case QuadraticBezierSegment q: See(q.Control); See(q.Point); break;
                        case CubicBezierSegment c: See(c.Control1); See(c.Control2); See(c.Point); break;
                    }
            }
            return hi < lo ? 0 : hi - lo;
        }

        /// <summary>The rendered width of a vertical bar, in pixels: total lamp coverage divided by
        /// the number of rows carrying any, which needs no assumption about where the bar is.</summary>
        private static double BarWidth(byte[] rgba, bool bgra)
        {
            double ink = 0;
            int rows = 0;
            for (int y = 0; y < Height; y++)
            {
                double row = 0;
                for (int x = 0; x < Width; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int idx = (y * Width + x) * 4 + (bgra ? 2 - c : c);
                        row += (255 - rgba[idx]) / 255.0;
                    }
                if (row > 0.05) { rows++; ink += row; }
            }
            return rows == 0 ? 0 : ink / 3.0 / rows;
        }

        private static void Tally(byte[] ours, byte[] gdiBgra, ref long pixels, ref long sum)
        {
            for (int i = 0; i + 3 < ours.Length && i + 3 < gdiBgra.Length; i += 4)
            {
                int d = Math.Abs(ours[i] - gdiBgra[i + 2])
                      + Math.Abs(ours[i + 1] - gdiBgra[i + 1])
                      + Math.Abs(ours[i + 2] - gdiBgra[i]);
                if (d == 0) continue;
                pixels++; sum += d;
            }
        }

        /// <summary>Our ClearType pixels for a geometry we were HANDED, rather than one we
        /// fitted -- flagged the way the string-run path flags a glyph batch (isGlyph plus
        /// PixelAligned) so it takes the same lamps, the same filter and the same contrast
        /// curve. Anything that differs is those three and nothing else.</summary>
        private byte[] OursRgbaFromFigures(List<PathFigure> placed, TrueTypeFont font)
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                                              new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),
                                              isGlyph: true) { PixelAligned = true });
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            return renderer.RenderToRgba(root, Width, Height, RgbaColor.FromBytes(255, 255, 255, 255));
        }

        /// <summary>KERN THE WAY THE ORACLE KERNS. ExtTextOutW does not apply pair adjustments and
        /// DrawTextW does -- measured, Arial 16ppem "AVAVAVAVAVAVAVAVAVAV": ExtTextOut 218 pixels of
        /// ink, DrawText 199, one per pair; "Z A" 23 against 22 -- so a run drawn here has to be
        /// kerned or not according to which of the two is on the other side. It was always kerned,
        /// and the weight specimen carries the pair (space, A) in Arial and Times: every capital and
        /// everything after them sat a pixel left of ExtTextOut's from 10ppem up, which is why those
        /// two faces scored five times Segoe UI's and why "A K N R W X Y Z" spaced out scored more
        /// than twice the same letters run together. Not a rendering difference at all -- the pen
        /// was asked one question and the oracle another.</summary>
        private static int OracleSimulations => Gdi.s_useDrawText ? 0 : GlyphRunDraw.NoKerningSimulation;

        private byte[] OursRgba(TrueTypeFont font, string text, int ppem, int baseline, bool correction,
                                float dx = 0f, int widthOverride = 0)
        {
            // Paper and Ink, not hardcoded white and black. Gdi.Draw already fills its DIB with
            // Paper, so with these fixed here WPF_PARITY_BG changed ONE side of the comparison and
            // the report came back with 923,621 differing pixels and an ink ratio of 0.10 -- the
            // whole background disagreeing, not the text. The knob's own comment says it exists to
            // find out whether the live window's #F0F0F0 is why the two harnesses disagree, and it
            // could not answer that while only GDI was listening to it.
            var root = new SceneVisual();
            // ShiftX/ShiftY as well, for the same reason as Paper: the knobs exist to ask questions
            // of the whole comparison and reached only one of the two renderers. The window lays
            // glyphs out at real layout positions while this probe uses an integer pen, so how
            // sharply the disagreement depends on sub-pixel position is exactly what separates
            // "our filter is wrong" from "the window puts glyphs somewhere the probe never does".
            root.Content.Add(new GlyphRunDraw(text, new Vector2(PenX + dx + ShiftX, baseline + ShiftY),
                ppem, RgbaColor.FromBytes((byte)((Ink >> 16) & 0xFF), (byte)((Ink >> 8) & 0xFF),
                                          (byte)(Ink & 0xFF), 255), OracleSimulations));
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = correction;
            return renderer.RenderToRgba(root, widthOverride > 0 ? widthOverride : Width, Height,
                RgbaColor.FromBytes((byte)((Paper >> 16) & 0xFF), (byte)((Paper >> 8) & 0xFF),
                                    (byte)(Paper & 0xFF), 255));
        }

        /// <summary>What we draw, as the green lamp, for comparison against what Windows draws.
        /// <para>THE CORRECTION IS ON. It used to be off, deliberately: while these tests asserted on
        /// a >=128-against-<32 band the curve was invisible to them, and leaving it out kept the
        /// comparison about WHERE the ink is. Now that they assert plain inequality, rendering our
        /// raw coverage against GDI's finished, contrast-corrected pixels would book our own missing
        /// gamma as a difference in every antialiased pixel -- measuring a step we chose to skip
        /// rather than anything about the port. Compare like with like.</para></summary>
        /// <summary>The paper both sides draw on, as RRGGBB. White unless WPF_PARITY_BG says else.
        /// <para>The suite has always drawn black on white; the live window draws control text on
        /// #F0F0F0, selections on blue, disabled text in grey. A subpixel fringe is a BLEND, so the
        /// paper is part of what is being compared -- and the correction curve was tuned entirely on
        /// white. This is how to find out whether that is why the two harnesses disagree.</para>
        /// </summary>
        private static readonly uint Paper =
            uint.TryParse(Environment.GetEnvironmentVariable("WPF_PARITY_BG"),
                          System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint bg)
                ? bg : 0xFFFFFFu;

        /// <summary>The ink colour, as RRGGBB. Black unless WPF_PARITY_FG says else -- so that
        /// INVERTED polarity (light text on dark) can be measured, which every control with a
        /// selected row draws and which the correction curve was never derived on.</summary>
        private static readonly uint Ink =
            uint.TryParse(Environment.GetEnvironmentVariable("WPF_PARITY_FG"),
                          System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint fg)
                ? fg : 0x000000u;

        private static readonly bool RawCoverage =
            Environment.GetEnvironmentVariable("WPF_PARITY_RAW") == "1";

        /// <summary>A sub-pixel shift applied to where we draw the run, for sweeping POSITION.
        /// <para>A sweep of this was done before and judged by INK, which is exactly the measure a
        /// translation cannot move -- every linear step conserves ink, so the answer was 'no effect'
        /// by construction. Mean |difference| does see it.</para></summary>
        private static readonly float ShiftX =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_PARITY_DX"),
                           System.Globalization.CultureInfo.InvariantCulture, out float sx) ? sx : 0f;

        private static readonly float ShiftY =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_PARITY_DY"),
                           System.Globalization.CultureInfo.InvariantCulture, out float sy) ? sy : 0f;

        private byte[] Ours(TrueTypeFont font, string text, int ppem = Ppem, int baseline = Baseline,
                            bool? correction = null)
        {
            var root = new SceneVisual();
            root.Content.Add(new GlyphRunDraw(text, new Vector2(PenX + ShiftX, baseline + ShiftY),
                ppem, RgbaColor.FromBytes((byte)((Ink >> 16) & 0xFF), (byte)((Ink >> 8) & 0xFF),
                                          (byte)(Ink & 0xFF), 255), OracleSimulations));
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = correction ?? !RawCoverage;
            byte[] rgba = renderer.RenderToRgba(root, Width, Height,
                RgbaColor.FromBytes((byte)((Paper >> 16) & 0xFF), (byte)((Paper >> 8) & 0xFF),
                                    (byte)(Paper & 0xFF), 255));
            var grey = new byte[Width * Height];
            for (int i = 0; i < grey.Length; i++)
                grey[i] = (byte)(255 - rgba[i * 4 + 1]);   // the green lamp, as on the Windows side
            return grey;
        }

        /// <summary>With WPF_GLYPH_DUMP set, write both images and their difference out as PNGs, so
        /// the same tools that measure a screen capture can be turned on them. The character maps in
        /// the failure message are for reading; these are for measuring.</summary>
        private static void Dump(string text, byte[] windows, byte[] ours)
        {
            string? dir = Environment.GetEnvironmentVariable("WPF_GLYPH_DUMP");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir!);

            // One file per string, named after it -- a theory runs every case, and a fixed name
            // would leave only whichever happened to run last.
            var name = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsUpper(c)) name.Append('u');      // 'abc' and 'ABC' are the same filename
                name.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            }

            Write(Path.Combine(dir!, $"gdi-{name}.png"), windows);
            Write(Path.Combine(dir!, $"our-{name}.png"), ours);
            var marks = new byte[windows.Length];
            for (int i = 0; i < marks.Length; i++) marks[i] = (byte)Math.Abs(windows[i] - ours[i]);
            Write(Path.Combine(dir!, $"dif-{name}.png"), marks);
        }

        /// <summary>One glyph's LAMPS, column by column, ours beside GDI's, at several sizes.
        /// <para>Everything else here reports a number for a whole repertoire. When the disagreement
        /// is a uniform few percent spread over every glyph and every row, an aggregate cannot say
        /// what is actually different about one stem -- whether it is narrower, or in a different
        /// place, or spread over more lamps at the same total. This prints the thing itself.</para>
        /// <para>Reported only: set WPF_STEM_REPORT.</para></summary>
        [Theory]
        [InlineData("l")]
        [InlineData("I")]
        [InlineData("n")]
        [InlineData("o")]
        [InlineData("e")]
        [InlineData("c")]
        [InlineData("T")]
        [InlineData("Today")]
        public void StemLamps_ReadAgainstGdis(string text)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STEM_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STEM_REPORT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();

            for (int ppem = 10; ppem <= 14; ppem++)
            {
                int baseline = ppem + 12;
                Gdi.s_rawRgb = raw;
                Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);

                // One row through the middle of the stem, where there is nothing but the stem.
                int row = baseline - ppem / 3;
                report.AppendLine($"'{text}' @{ppem} row {row}   GDI (r,g,b)        ours (r,g,b)");
                for (int x = PenX - 2; x < PenX + 8; x++)
                {
                    int i = (row * Width + x) * 4;
                    // GDI draws into a Windows DIB, which is BGRA; we read back RGBA8Unorm. Both
                    // were indexed [i+2],[i+1],[i] and both labelled "(r,g,b)", which is right for
                    // GDI and BACKWARDS for us -- so our lamps printed in the opposite order and
                    // every stem of ours looked mirrored against GDI's. For black text on white
                    // that swap is invisible everywhere except the subpixel fringes, which is
                    // exactly what this report exists to show.
                    report.AppendLine($"  col {x,3}   "
                        + $"{255 - raw[i + 2],3},{255 - raw[i + 1],3},{255 - raw[i],3}"
                        + $"        {255 - mine[i],3},{255 - mine[i + 1],3},{255 - mine[i + 2],3}");
                }
            }

            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>The shading transfer, measured WHERE THE GEOMETRY IS PROVABLY IDENTICAL.
        /// <para>This was measured before and the answer was "no pointwise curve can work": GDI's
        /// mean rose smoothly with ours but with a standard deviation of 47-79, so our value did not
        /// predict GDI's. That was measured at sizes where the two sides draw DIFFERENT OUTLINES --
        /// ours y-only fitted, GDI's whatever ClearType fits -- so the scatter was mostly geometry
        /// and the conclusion did not follow.</para>
        /// <para>At 7 and 8 pixels an em Segoe UI's 'gasp' asks for no grid-fitting, so both sides
        /// rasterize the SAME unhinted outline -- stage A measures that agreement at 1.0001. Here
        /// the only thing left between our lamps and GDI's is the shading, and if the transfer is
        /// tight here it can be fitted exactly rather than approximated by a gamma.</para>
        /// <para>Reported only: set WPF_TRANSFER to a file.</para></summary>
        [Fact]
        public void ShadingTransfer_WhereGeometryIsIdentical()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_TRANSFER");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_TRANSFER to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();

            foreach (int ppem in new[] { 7, 8, 12 })          // 12 for contrast: gridfit is ON there
            {
                int baseline = ppem + 12;
                var sum = new long[17]; var sq = new double[17]; var n = new int[17];
                foreach (string text in Repertoire)
                {
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    // OURS BEFORE THE CURVE: the transfer is what the curve should BE, so measuring
                    // it through the curve would only report the curve back.
                    byte[] mine = OursRgba(font, text, ppem, baseline, correction: false);

                    for (int i = 0; i < Width * Height * 4; i += 4)
                    {
                        int[] theirs = { 255 - raw[i + 2], 255 - raw[i + 1], 255 - raw[i] };
                        int[] ours = { 255 - mine[i], 255 - mine[i + 1], 255 - mine[i + 2] };
                        for (int L = 0; L < 3; L++)
                        {
                            if (ours[L] == 0 && theirs[L] == 0) continue;
                            int b = ours[L] * 16 / 256;
                            sum[b] += theirs[L]; sq[b] += (double)theirs[L] * theirs[L]; n[b]++;
                        }
                    }
                }
                report.AppendLine($"=== {ppem}ppem "
                    + (ppem <= 8 ? "(gasp: NO gridfit -- same outline both sides)" : "(gridfit ON)"));
                report.AppendLine("ours(bin)   n        GDI mean    sd");
                for (int b = 0; b < 17; b++)
                {
                    if (n[b] < 50) continue;
                    double mean = (double)sum[b] / n[b];
                    double sd = Math.Sqrt(Math.Max(0, sq[b] / n[b] - mean * mean));
                    report.AppendLine($"{b * 16,4}-{b * 16 + 15,-6} {n[b],-8} {mean,8:0.0} {sd,7:0.0}");
                }
                lock (Repertoire) File.AppendAllText(path!, report.ToString());
                report.Clear();
            }
        }

        /// <summary>Per glyph, the sub-pixel x offset that best reproduces what GDI DREW.
        /// <para>A global offset sweep says zero, and a global sweep is the average of every glyph.
        /// If glyphs individually want different offsets the average can be zero while every glyph is
        /// misplaced, and nothing measured so far could tell those apart. This slides OUR fitted
        /// outline against GDI's own rendering, one glyph at a time, and reports the distribution --
        /// a tight peak at zero means our placement is right and the residual is shape; a spread
        /// means placement, and names the glyphs to look at.</para>
        /// <para>Reported only: set WPF_GLYPHOFFSET.</para></summary>
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(16)]
        public void PerGlyphOffset_AgainstGdis(int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_GLYPHOFFSET");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_GLYPHOFFSET to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            var tally = new int[17];
            var worst = new List<(float Off, char Ch)>();
            var residual = new List<(double At0, double AtBest, char Ch)>();
            var shifts = new System.Text.StringBuilder();
            var predicted = new List<(char Ch, int Need, int BiLevel)>();

            int baseline = ppem + 12;
            foreach (char c in Letters)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;

                // Rasterized DIRECTLY, not through the renderer: it snaps a run's origin to a whole
                // device pixel (as GDI does), so every offset inside half a pixel renders identically,
                // they all tie, and a strict `<` hands the answer to whichever was tried first. The
                // first version of this probe reported "every glyph wants exactly -0.500" at both
                // sizes -- the search boundary -- which is what that artefact looks like.
                if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                                                                   out List<PathFigure> figs))
                    continue;

                double best = double.MaxValue; float bestOff = 0;
                for (int k = -8; k <= 8; k++)
                {
                    float off = k / 16f;
                    double e = GlyphLampError(figs, PenX + off, baseline, raw);
                    if (e < best) { best = e; bestOff = off; }
                }
                tally[(int) MathF.Round(bestOff * 16) + 8]++;
                if (MathF.Abs(bestOff) >= 0.25f) worst.Add((bestOff, c));
                // What the glyph costs WHERE IT ACTUALLY SITS, and what sliding it could recover.
                // A glyph that is expensive at 0 and stays expensive at its best offset is not
                // misplaced -- it is the wrong SHAPE, and no rounding rule will reach it.
                residual.Add((GlyphLampError(figs, PenX, baseline, raw), best, c));
                // The table an upper-bound run feeds back in: what is the MOST that placing every
                // glyph perfectly could be worth? A rule is only worth looking for if the ceiling
                // is worth reaching.
                shifts.AppendLine($"SHIFT {ppem} {font.GlyphIndex(c)} {(int) MathF.Round(bestOff * 16)}");
                // What the BI-LEVEL fit would move this glyph by, beside what the pixels ask for.
                // A fresh font each time: hinted outlines are cached by (glyph, size), so reusing
                // the instance returns the ClearType answer again and the pass looks like a no-op.
                var biFont = new TrueTypeFont(File.ReadAllBytes(file!));
                TrueTypeInterpreter.BiLevelPass = true;
                bool gotBi;
                List<PathFigure> biFigs;
                try { gotBi = ((IHintedGlyphFont) biFont).TryGetHintedOutline(biFont.GlyphIndex(c), ppem, out biFigs); }
                finally { TrueTypeInterpreter.BiLevelPass = false; }
                if (gotBi)
                {
                    float ctL = float.MaxValue, biL = float.MaxValue;
                    foreach (PathFigure f in figs) ctL = MathF.Min(ctL, FigureLeft(f));
                    foreach (PathFigure f in biFigs) biL = MathF.Min(biL, FigureLeft(f));
                    predicted.Add((c, (int) MathF.Round(bestOff * 16), (int) MathF.Round((biL - ctL) * 16)));
                }
            }

            report.AppendLine($"== {ppem}ppem   best per-glyph x offset, in sixteenths of a pixel");
            for (int k = 0; k < 17; k++)
                if (tally[k] > 0)
                    report.AppendLine($"   {k - 8,+3}/16 ({(k - 8) / 16f,6:+0.000;-0.000; 0.000})  {new string('#', tally[k])} {tally[k]}");
            if (worst.Count > 0)
            {
                var names = new List<string>();
                foreach ((float off, char ch) in worst) names.Add($"'{ch}'{off:+0.00;-0.00}");
                report.AppendLine("   a quarter pixel or more: " + string.Join(" ", names));
            }
            if (predicted.Count > 0)
            {
                int agree = 0, close = 0;
                var line = new System.Text.StringBuilder();
                foreach ((char ch, int need, int bi) in predicted)
                {
                    if (need == bi) agree++;
                    if (Math.Abs(need - bi) <= 2) close++;
                    line.Append($"{ch}:{need:+0;-0;0}/{bi:+0;-0;0} ");
                }
                report.AppendLine($"   bi-level PREDICTS the needed shift exactly for {agree} of"
                                  + $" {predicted.Count}, within 2/16 for {close}");
                report.AppendLine("   need/bi-level, sixteenths: " + line);
            }
            residual.Sort((x, y) => y.At0.CompareTo(x.At0));
            double total = 0, recoverable = 0;
            foreach ((double at0, double atBest, char _) in residual) { total += at0; recoverable += at0 - atBest; }
            report.AppendLine($"   cost where they sit = {total:0}, of which sliding could recover {recoverable:0}"
                              + $" ({(total > 0 ? recoverable * 100 / total : 0):0.0}%)");
            report.Append("   dearest: ");
            for (int i = 0; i < 12 && i < residual.Count; i++)
                report.Append($"'{residual[i].Ch}'{residual[i].At0:0} ");
            report.AppendLine();
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
            if (Environment.GetEnvironmentVariable("WPF_SHIFT_TABLE") is string t && t.Length > 0)
                lock (Repertoire) File.AppendAllText(t, shifts.ToString());
        }

        /// <summary>SOLVE for the outline GDI's ClearType actually draws.
        /// <para>GetGlyphOutline does not report it -- proved: our fitted outline matches what it
        /// DOES report on 61 of 62 glyphs and rendering that is 82% worse than discarding x. But the
        /// drawn lamps are evidence, and our rasterizer and curve are independently validated (at
        /// 7-8ppem, where the gasp turns fitting off and both sides draw the same outline, our ink
        /// lands within 0.15% of GDI's). So put a plain vertical bar of every plausible edge and
        /// width through OUR pipeline and keep the one that reproduces GDI's lamps: that recovers
        /// the geometry ClearType is drawing, in the only place it is visible.</para>
        /// <para>Reported only: set WPF_SOLVE.</para></summary>
        [Fact]
        public void SolveForClearTypeGeometry()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_SOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_SOLVE to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine("'l'  ppem   GDI's stem (left..right, px)   ours natural   ours x-fitted");

            for (int ppem = 11; ppem <= 19; ppem++)
            {
                int baseline = ppem + 12;
                Gdi.s_rawRgb = raw;
                Gdi.Draw("l", "Segoe UI", ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;

                int row = baseline - ppem / 3;
                // GDI's lamps across the stem, as ink.
                var want = new int[36];
                for (int k = 0; k < 12; k++)
                {
                    int i = (row * Width + PenX - 2 + k) * 4;
                    want[k * 3 + 0] = 255 - raw[i + 2];
                    want[k * 3 + 1] = 255 - raw[i + 1];
                    want[k * 3 + 2] = 255 - raw[i];
                }

                double best = double.MaxValue, bestErr = 0; float bl = 0, br = 0;
                for (int li = 0; li <= 256; li++)
                {
                    float left = PenX - 1 + li / 64f;
                    for (int wi = 21; wi <= 149; wi++)
                    {
                        float w = wi / 64f;
                        int[] got = BarLamps(left, left + w, row, ppem);
                        double e = 0;
                        for (int k = 0; k < 36; k++) { double d = got[k] - want[k]; e += d * d; }
                        if (e < best) { best = e; bl = left - PenX; br = left + w - PenX; bestErr = e; }
                    }
                }

                float natL = 0, natR = 0, fitL = 0, fitR = 0, biL = 0, biR = 0;
                Edges(font, ppem, subpixel: true, out natL, out natR);
                Edges(font, ppem, subpixel: false, out fitL, out fitR);
                // And the BI-LEVEL fit, on a fresh font so the (glyph, size) outline cache cannot
                // hand back the ClearType answer. GDI's real stem can then be placed against BOTH
                // of ours: between them, or outside them both.
                var biFont = new TrueTypeFont(File.ReadAllBytes(file!));
                TrueTypeInterpreter.BiLevelPass = true;
                try { Edges(biFont, ppem, subpixel: true, out biL, out biR); }
                finally { TrueTypeInterpreter.BiLevelPass = false; }
                report.AppendLine($"     {ppem,4}   gdi {bl,6:0.000}..{br,-6:0.000} (w {br - bl,5:0.000})"
                                  + $" rms={MathF.Sqrt((float)bestErr / 36),4:0.0}"
                                  + $"   ct {natL,5:0.00}..{natR,-5:0.00} (w {natR - natL,4:0.00})"
                                  + $"   bi {biL,5:0.00}..{biR,-5:0.00} (w {biR - biL,4:0.00})");
            }
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>Squared lamp difference between our rasterization of a glyph's figures, placed at
        /// (x, baseline), and GDI's own drawing of it.</summary>
        private static double GlyphLampError(List<PathFigure> figures, float x, float baseline,
                                             byte[] raw, int fromX = 0, int toX = int.MaxValue)
        {
            var moved = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var g = new PathFigure(new Vector2(f.Start.X + x, f.Start.Y + baseline)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l:
                            g.Segments.Add(new LineSegment(new Vector2(l.Point.X + x, l.Point.Y + baseline))); break;
                        case QuadraticBezierSegment q:
                            g.Segments.Add(new QuadraticBezierSegment(
                                new Vector2(q.Control.X + x, q.Control.Y + baseline),
                                new Vector2(q.Point.X + x, q.Point.Y + baseline))); break;
                        case CubicBezierSegment cu:
                            g.Segments.Add(new CubicBezierSegment(
                                new Vector2(cu.Control1.X + x, cu.Control1.Y + baseline),
                                new Vector2(cu.Control2.X + x, cu.Control2.Y + baseline),
                                new Vector2(cu.Point.X + x, cu.Point.Y + baseline))); break;
                    }
                moved.Add(g);
            }

            PathRasterizer.SubpixelMask m = PathRasterizer.RasterizeSubpixel(
                new PathGeometry(FillRule.NonZero, moved));
            if (m.IsEmpty) return double.MaxValue;

            double e = 0;
            // A COLUMN WINDOW, so one stem can be scored on its own pixels and the answer
            // owes nothing to coordinates outside it.
            int lo = Math.Max(0, fromX), hi = Math.Min(Width - 1, toX);
            for (int y = 0; y < Height; y++)
                for (int px = lo; px <= hi; px++)
                {
                    int i = (y * Width + px) * 4;
                    int gy = y - (int) m.OriginY, gx = px - (int) m.OriginX;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int ours = 0;
                        if (gy >= 0 && gy < m.Height && gx >= 0 && gx < m.Width)
                            ours = InkThroughTheCurve(m.Rgba[(gy * m.Width + gx) * 4 + ch]);
                        int theirs = 255 - raw[i + (2 - ch)];
                        double d = ours - theirs;
                        e += d * d;
                    }
                }
            return e;
        }

        /// <summary>Where each glyph's INK actually begins and ends, ours against GDI's.
        /// <para>The per-glyph offset probe finds one number per glyph, and one number cannot tell a
        /// glyph that is in the wrong PLACE from one that is the wrong WIDTH -- it splits the
        /// difference and reports something that is neither. 'm' wanting -7/16 while 'n', very nearly
        /// the same shape, wants +6/16 is that artefact showing. Both edges, separately, do tell
        /// them apart: equal shifts on both edges is placement, unequal is width.</para>
        /// <para>Reported only: set WPF_EXTENTS.</para></summary>
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(16)]
        public void InkExtents_AgainstGdis(int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_EXTENTS");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_EXTENTS to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {ppem}ppem   ink edges in LAMPS (ours - GDI); +left means ours starts right of theirs");
            int baseline = ppem + 12;
            double sumL = 0, sumR = 0, sumW = 0; int n = 0;
            var rows = new System.Text.StringBuilder();

            foreach (char c in Letters)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                                                                   out List<PathFigure> figs))
                    continue;
                if (!GlyphLampExtents(figs, PenX, baseline, raw, out int gl, out int gr,
                                      out int ol, out int orr)) continue;
                double dl = (ol - gl) / 3.0, dr = (orr - gr) / 3.0;
                sumL += dl; sumR += dr; sumW += dr - dl; n++;
                rows.Append($"'{c}' L{dl,+5:+0.00;-0.00} R{dr,+5:+0.00;-0.00}  ");
                if (n % 6 == 0) rows.AppendLine();
            }
            report.AppendLine($"   mean left {sumL / Math.Max(n, 1):+0.000;-0.000} px,"
                              + $" mean right {sumR / Math.Max(n, 1):+0.000;-0.000} px,"
                              + $" mean WIDTH {sumW / Math.Max(n, 1):+0.000;-0.000} px  (n={n})");
            report.Append(rows).AppendLine();
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>Which FIT each glyph's pixels prefer: the ClearType rounding or the bi-level one.
        /// <para>Every rule tried for choosing between them was a guess at a discriminator -- height,
        /// then ascenders, then curvature -- checked against a whole band of the specimen at a time.
        /// This asks the question one glyph at a time and in the only court that matters, GDI's own
        /// lamps: fit the glyph both ways, rasterize both, and report which is closer.</para>
        /// <para>Reported only: set WPF_FITCHOICE.</para></summary>
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(16)]
        public void WhichFitDoThePixelsPrefer(int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_FITCHOICE");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_FITCHOICE to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            byte[] bytes = File.ReadAllBytes(file!);
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            int baseline = ppem + 12;
            var wantsBi = new System.Text.StringBuilder();
            var wantsCt = new System.Text.StringBuilder();
            double gain = 0;

            foreach (char c in Letters)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;

                // A FRESH font for each fit: hinted outlines are cached by (glyph, size), so the
                // second ask would otherwise return the first fit's answer.
                var ctFont = new TrueTypeFont(bytes);
                if (!((IHintedGlyphFont) ctFont).TryGetHintedOutline(ctFont.GlyphIndex(c), ppem,
                                                                     out List<PathFigure> ct)) continue;
                var biFont = new TrueTypeFont(bytes);
                List<PathFigure> bi;
                TrueTypeInterpreter.BiLevelPass = true;
                try
                {
                    if (!((IHintedGlyphFont) biFont).TryGetHintedOutline(biFont.GlyphIndex(c), ppem, out bi))
                        continue;
                }
                finally { TrueTypeInterpreter.BiLevelPass = false; }

                double ctErr = GlyphLampError(ct, PenX, baseline, raw);
                double biErr = GlyphLampError(bi, PenX, baseline, raw);
                if (biErr < ctErr) { wantsBi.Append(c); gain += ctErr - biErr; }
                else wantsCt.Append(c);
            }
            report.AppendLine($"== {ppem}ppem");
            report.AppendLine($"   prefer BI-LEVEL ({wantsBi.Length}): {wantsBi}");
            report.AppendLine($"   prefer CLEARTYPE ({wantsCt.Length}): {wantsCt}");
            report.AppendLine($"   choosing per glyph would be worth {gain:0}");
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>How much of each glyph's residual a per-glyph SCALE and OFFSET can explain.
        /// <para>The bar solver now reproduces GDI's lamps with rms 0 -- our rasterizer, filter and
        /// blend are an exact model of GDI's rendering given the right outline -- so what it recovers
        /// IS GDI's geometry, and the same is true of anything else fitted this way. Sliding a glyph
        /// was worth 32% of the error; this asks what a stretch buys on top, which is the difference
        /// between "GDI places glyphs differently" and "GDI FITS them differently".</para>
        /// <para>Reported only: set WPF_GLYPHFIT.</para></summary>
        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(16)]
        public void PerGlyphScaleAndOffset_AgainstGdis(int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_GLYPHFIT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_GLYPHFIT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            int baseline = ppem + 12;
            double at0 = 0, bestSlide = 0, bestBoth = 0;
            var scales = new Dictionary<int, int>();

            foreach (char c in Letters)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                                                                   out List<PathFigure> figs))
                    continue;

                at0 += GlyphLampError(figs, PenX, baseline, raw);
                double slide = double.MaxValue, both = double.MaxValue; int bestS = 0;
                for (int k = -8; k <= 8; k++)
                {
                    double e = GlyphLampError(figs, PenX + k / 16f, baseline, raw);
                    if (e < slide) slide = e;
                    // Scale about the glyph's own origin, in half percents, then slide.
                    for (int sp = -8; sp <= 8; sp++)
                    {
                        List<PathFigure> scaled = ScaleX(figs, 1f + sp / 200f);
                        double e2 = GlyphLampError(scaled, PenX + k / 16f, baseline, raw);
                        if (e2 < both) { both = e2; bestS = sp; }
                    }
                }
                bestSlide += slide; bestBoth += both;
                scales[bestS] = scales.GetValueOrDefault(bestS) + 1;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {ppem}ppem   cost {at0:0}");
            report.AppendLine($"   sliding alone leaves      {bestSlide:0}  ({(at0 - bestSlide) * 100 / at0:0.0}% recovered)");
            report.AppendLine($"   sliding AND stretching    {bestBoth:0}  ({(at0 - bestBoth) * 100 / at0:0.0}% recovered)");
            report.Append("   best stretch, in half percents: ");
            var keys = new List<int>(scales.Keys); keys.Sort();
            foreach (int k in keys) report.Append($"{k:+0;-0;0}:{scales[k]} ");
            report.AppendLine();
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>SOLVE for the x coordinates GDI's ClearType actually fitted a glyph to.
        /// <para>This is the instrument the whole investigation wanted and could not have until now.
        /// GetGlyphOutline reports the BI-LEVEL outline, not ClearType's, and fitting geometry
        /// through our pipeline used to charge every pipeline error to the geometry. Neither
        /// objection stands any more: the bar solver reproduces GDI's lamps at rms 0, so our
        /// rasterizer, filter and blend ARE an exact model of GDI's rendering given the right
        /// outline. What comes back from a fit is therefore GDI's geometry.</para>
        /// <para>Coordinate descent over the glyph's DISTINCT x values -- a stem glyph has two, an
        /// 'H' four -- each swept in 64ths of a pixel and kept where the lamps agree best. Comparing
        /// the answer against our own fitted x says which instruction outcome we get wrong, one
        /// coordinate at a time, which is what deriving the rule requires.</para>
        /// <para>WPF_SOLVEGLYPH=char@ppem, e.g. "H@12".</para>
        /// <para>WHAT THIS INSTRUMENT CAN AND CANNOT TELL YOU -- read before using it. Lamps
        /// carry seven levels, so a RANGE of positions renders identically, and the descent only
        /// takes a strict improvement: whichever end of the range it sweeps from is the end it
        /// keeps. It swept up from -24, so it reported the leftmost member of every tie, and
        /// every difference it ever printed came out NEGATIVE.</para>
        /// <para>Measured with WPF_SOLVE_REVERSE=1, which sweeps the other way: 'n' point 13
        /// comes back 1.734 forwards and 2.078 reversed; 'B' at 3.250 gives 2.875 and 3.125; '0'
        /// at 1.000 gives 0.625 and 0.750. Those coordinates are not determined by GDI's pixels
        /// at all.</para>
        /// <para>So the run reports the INTERVAL now. Across n, P, '0' and H not one coordinate
        /// is pinned to a 64th, the median range is about twenty 64ths -- a third of a pixel --
        /// and most ranges already CONTAIN our own value: 10 of 12 for 'n', 10 of 14 for 'P', 15
        /// of 25 for '0', 2 of 4 for 'H'. Where the range contains ours there was never anything
        /// to explain.</para>
        /// <para>RETRACTED, therefore: the census of 281 coordinates that made -0.375 an
        /// eighteen-strong error class; the conclusion that GDI TOUCHES a point we interpolate;
        /// the triples of a touched point with its followers; and the arriving/ours/GDI sample
        /// that concluded GDI's answers lie on no grid we round to. Each was reading the sweep
        /// direction as if it were GDI. The leftward bias WAS the finding.</para>
        /// <para>What survives is the binary question -- is our coordinate inside the range GDI
        /// allows -- which does not depend on the tie-break. On that metric at 12ppem: letters
        /// 52% under mode 5 and 66% under mode 6; digits 88% under mode 5 and 79% under mode 6.
        /// Same split and same size as the biased count gave (57/70 and 87/78), so the
        /// letters-versus-digits result stands on its own.</para>
        /// <para>AND WITH THE INTERVAL, THE FIRST DETERMINED STATEMENT ABOUT A GLYPH. 'H' at
        /// 12ppem has to satisfy [0.922,1.078] [2.094,2.235] [6.921,7.078] [8.094,8.250]. Both
        /// modes fail the last two:</para>
        /// <para>mode 6  1.000  2.094  6.656  7.750   stem to stem 5.656<br/>
        /// mode 5  1.125  2.219  6.812  7.906   stem to stem 5.687</para>
        /// <para>The ranges force the second stem's left edge to 7.0 and the first's to 1.0, so
        /// GDI's stem-to-stem is SIX PIXELS EXACTLY and ours is a third of a pixel short. Our
        /// stem WIDTH of 1.094 is inside the allowed [1.016, 1.312] -- the width is fine and the
        /// whole second stem is displaced. That is mode-independent, so it is not the rounding
        /// grid, and it is the first thing here that is true rather than merely fitted.</para>
        /// <para>Where it comes from: point 1 reaches MDAP[r] at 6.75 and mode 6 rounds it to the
        /// nearest LAMP, 6.66, where the whole pixel would be 7.0 and in range. The obvious fix
        /// is positions on the physical grid with distances left alone, and it is measured and
        /// wrong: WPF_CT_POSGRID=physical costs 5,834,105 against 2,180,771 under mode 5, and
        /// does nothing at all under mode 6, where the lamp grid takes precedence.</para>
        /// <para>AND THE WHOLE ROUNDING-RULE FAMILY IS NOW CLOSED, fitted against ten glyphs'
        /// intervals at once instead of read off one. For every coordinate, take the value
        /// ARRIVING at the last instruction that moved it and ask which candidate rounding lands
        /// inside the interval GDI allows. Over 182 coordinates:</para>
        /// <para>leave it alone 83, whole pixel 76, half 90, lamp 83, quarter 98, eighth 83,
        /// sixteenth 85 -- against 115 for what our pipeline actually produces. Restricted to the
        /// 36 coordinates whose last mover is MDAP, MIRP or MIAP, where rounding is the whole of
        /// the job: 9, 13, 13, 13, 14, 8, 9 against 17 for ours.</para>
        /// <para>Every simple rounding of the arriving value is WORSE than what we already do, on
        /// both populations. So GDI's positions are not our arriving value put on any grid, and
        /// the family of fixes that has occupied this investigation -- lamp instead of pixel,
        /// sixteenth instead of lamp, physical for positions -- is exhausted rather than
        /// untried.</para>
        /// <para>The room is real: we are inside the allowed interval for 115 of 182 coordinates,
        /// and only 17 of 36 where a fitting instruction decides it. Whatever closes that gap
        /// depends on more than the value arriving at the instruction.</para>
        /// <para>'l' at 12ppem has NO coordinate outside its range under mode 6 -- we draw it
        /// correctly -- so whatever this is, it is not general.</para></summary>
        private static readonly bool s_solveReverse =
            Environment.GetEnvironmentVariable("WPF_SOLVE_REVERSE") == "1";

        /// <summary>ASKS GDI WHAT IT ANSWERS GETINFO, instead of assuming it.
        /// <para>This matters more than its size suggests. Segoe UI does not hint one way; it
        /// carries several hinting programs and picks between them at run time. Its fpgm holds a
        /// family of dispatchers shaped
        /// <c>FDEF PUSHB[2] RS EQ IF &lt;call the real work&gt; ELSE POP POP POP EIF ENDF</c> --
        /// every instruction in a glyph is tagged with a mode number and runs only if that number
        /// equals storage[2]. And storage[2] is computed, in fpgm at 2270, from GETINFO alone:</para>
        /// <code>
        /// storage[2] = 1
        /// if 35 &lt;= version &lt;= 64:
        ///     storage[2] = 0                                  // bi-level
        ///     if GETINFO(32) == 4096: storage[2] += 1         // greyscale
        ///     if version >= 36:
        ///         if GETINFO(64) == 8192: storage[2] += 2     // ClearType
        ///         if version == 36:       storage[2] += 32
        /// </code>
        /// <para>So the rasterizer version is not a detail, it is a GATE: below 36 the face never
        /// even asks whether ClearType is on, storage[2] can only be 0 or 1, and every ClearType
        /// instruction in every glyph is skipped. We answered 35, so we have been running Segoe
        /// UI's BI-LEVEL hinting and then applying our own invented x rules on top of it. That is
        /// why no rounding rule ever fitted and why XHintMode helps some glyphs and hurts others:
        /// it is a hand-made substitute for a branch the font wanted to take itself.</para>
        /// <para>GETINFO's answer never reaches an API, so the only way to read it is to make the
        /// answer visible: the probe font's glyph shifts ITSELF right by what it is told, and the
        /// ink says what GDI said. Set WPF_GETINFO_ORACLE to a path.</para>
        /// <para>Read BOTH columns. GetGlyphOutline renders greyscale and answers as a greyscale
        /// rasterizer, so the GGO column cannot see the ClearType branch at all -- which is how an
        /// earlier attempt concluded the face had none. The drawn column is the one that counts.</para>
        /// </summary>
        [Fact]
        public void WhatGdiAnswersGetInfo()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_GETINFO_ORACLE");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_GETINFO_ORACLE to collect this");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the oracle");

            const string Fam = "WpfGetInfoOracle";
            int ProbePpem = int.TryParse(Environment.GetEnvironmentVariable("WPF_GETINFO_PPEM"), out int pp) ? pp : 16;
            // WPF_GETINFO_GASP=times ships Times New Roman roman's gasp (v1: <=8 0xA, <=17 0x5,
            // else 0xF), so the symmetric-rendering answer is read for a face that DECLARES
            // its wishes; the original probe had no gasp and answered about itself.
            string? giGasp = Environment.GetEnvironmentVariable("WPF_GETINFO_GASP");
            // WPF_GETINFO_GASP=v0 ships Times New Roman ITALIC's own table instead: version 0,
            // which has no symmetric bits to read at all. Five of the specimen faces ship one
            // (Times italic/bold/bold-italic, Arial italic/bold) and we answer their symmetric
            // query YES on the strength of oracle points alone -- this asks GDI itself.
            SyntheticFont.GaspVersion = giGasp == "v0" ? 0 : 1;
            SyntheticFont.GaspRanges = giGasp switch
            {
                "times" => new (int, int)[] { (8, 0xA), (17, 0x5), (0xFFFF, 0xF) },
                "v0" => new (int, int)[] { (8, 0x2), (20, 0x1), (0xFFFF, 0x3) },
                _ => null,
            };
            // 0 is the baseline: the same bar with no program at all, so the shift is measured
            // against the position the outline alone puts it in.
            // Negative entries ask the EXACT question the face asks (== the bit), not merely
            // "non-zero" -- which is the difference between a branch being taken and not.
            // 2 and 4 are here because nothing had ever asked them, and they are not idle
            // questions: GDI's ClearType rasterizer works in a space stretched three times
            // in x, so 'stretched' is exactly the kind of thing it might answer yes to --
            // and a face that branches on it would hint differently for reasons no amount of
            // looking at the ClearType bit could explain.
            // 4096 is 'ClearType greyscale', and it is here because THREE OF SIX faces in the
            // specimen ask it -- Verdana, Tahoma and Arial all do, Segoe UI does not -- and
            // nothing had ever answered it. Every rule in this file was tuned on the one face
            // that never asks.
            int[] selectors = { 0, 1, 2, 4, 32, 64, 128, 256, 512, 1024, 2048, 4096,
                                -2, -4, -32, -64, -128, -256, -512, -1024, -2048, -4096 };
            string[] names =
            {
                "(no program, baseline)", "rasterizer version", "rotated", "stretched",
                "greyscale", "ClearType enabled",
                "compatible widths", "horizontal LCD stripes", "BGR order",
                "sub-pixel positioned", "symmetric rendering",
                "greyscale ClearType",
                "rotated == bit", "stretched == bit",
                "greyscale == bit", "ClearType == bit", "compatible widths == bit",
                "stripes == bit", "BGR == bit", "sub-pixel pos == bit", "symmetric == bit",
                "greyscale ClearType == bit",
            };

            var bars = new List<SyntheticFont.Bar>();
            foreach (int sel in selectors)
                bars.Add(new SyntheticFont.Bar(0, 400, 700, false, false,
                                               noProgram: sel == 0, probe: sel));
            byte[] fontBytes;
            try { fontBytes = SyntheticFont.Build(Fam, bars); }
            finally { SyntheticFont.GaspRanges = null; SyntheticFont.GaspVersion = 1; }
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI refused the probe font");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== what GDI answers GETINFO, read off the ink at {ProbePpem}ppem");
            report.AppendLine("   selector  meaning                    drawn(ClearType)      GGO(greyscale)     GGO stretched 3x");
            try
            {
                var raw = new byte[Width * Height * 4];
                int baseDrawn = -1, baseGgo = -1, baseWide = -1;
                for (int i = 0; i < selectors.Length; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Fam, ProbePpem, PenX, ProbePpem + 12, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    int drawn = InkLeftColumn(raw);
                    var ggoFig = GdiStageTests.GdiOutlineAt(ch[0], Fam, ProbePpem, 0, 0);
                    int ggo = ggoFig.Count == 0 ? -1 : (int) MathF.Round(XLeft(ggoFig));
                    // AND THE SAME QUESTION THROUGH A STRETCHED MAT2, because stage D's
                    // whole premise is that asking GGO for the glyph tripled in x asks for
                    // it in the space ClearType rasterizes in. If GDI still answers "not
                    // ClearType" there, the stretch buys a finer grid and NOT the face's
                    // ClearType branch, and stage D is reading the bi-level program.
                    var wideFig = GdiStageTests.GdiOutline(ch[0], Fam, ProbePpem,
                                                          unhinted: false, xScale: 3);
                    int wide = wideFig.Count == 0 ? -1 : (int) MathF.Round(XLeft(wideFig) * 3);
                    if (i == 0) { baseDrawn = drawn; baseGgo = ggo; baseWide = wide; }
                    string d = drawn < 0 ? "no ink" : (drawn - baseDrawn).ToString();
                    string g = ggo < 0 ? "no outline" : (ggo - baseGgo).ToString();
                    string wd = wide < 0 ? "no outline" : (wide - baseWide).ToString();
                    report.AppendLine($"   {selectors[i],8}  {names[i],-25}  {d,14}  {g,18}  {wd,18}");
                }
                report.AppendLine("   (selector 1 shifts by VERSION MINUS 32 pixels; every other row"
                                  + " shifts ten pixels when the bit is set and none when it is not)");
            }
            finally { RemoveFontMemResourceEx(handle); }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>THE SAME QUESTIONS, ASKED FROM THE PRE-PROGRAM. Set WPF_GETINFO_PREP.
        /// <para>This is not a duplicate of WhatGdiAnswersGetInfo and the difference is the whole
        /// point. A face computes its rendering-mode variable in 'prep' -- Segoe UI's fpgm at 2270
        /// builds storage[2] out of GETINFO answers there -- and that variable then decides which
        /// of its several hinting programs every glyph runs. So what GDI answers during PREP is
        /// what actually matters, and what it answers inside a glyph need not be the same.</para>
        /// <para>Asked because the two disagree with the pixels. GDI reports symmetric rendering
        /// from a glyph program, which would put Segoe UI in mode 134 and skip the pass that
        /// rounds 51 stem control values to whole pixels -- and GDI's own H at 12ppem is the
        /// WHOLE-PIXEL stem (lamps 73 153 255 197 111 36, 2.157px, which our symmetric-OFF output
        /// matches exactly and our symmetric-ON output does not).</para></summary>
        [Fact]
        public void WhatGdiAnswersGetInfoInThePreProgram()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_GETINFO_PREP");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_GETINFO_PREP to collect this");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the oracle");

            const string Fam = "WpfPrepInfoOracle";
            int ProbePpem = int.TryParse(Environment.GetEnvironmentVariable("WPF_GETINFO_PREP_PPEM"),
                out int pp) ? pp : 16;
            // The same two real tables the glyph-side oracle ships, because the symmetric answer is
            // a question about the FACE: "times" is Times New Roman roman's v1 gasp, "v0" is Times
            // Italic's own version-0 table, which has no symmetric bits in it at all.
            string? giGasp = Environment.GetEnvironmentVariable("WPF_GETINFO_PREP_GASP");
            SyntheticFont.GaspVersion = giGasp == "v0" ? 0 : 1;
            SyntheticFont.GaspRanges = giGasp switch
            {
                "times" => new (int, int)[] { (8, 0xA), (17, 0x5), (0xFFFF, 0xF) },
                "v0" => new (int, int)[] { (8, 0x2), (20, 0x1), (0xFFFF, 0x3) },
                _ => null,
            };
            int[] sel = { 32, 64, 128, 256, 512, 1024, 2048, 4096 };
            string[] names = { "greyscale", "ClearType enabled", "compatible widths",
                               "horizontal LCD stripes", "BGR order", "sub-pixel positioned",
                               "symmetric rendering", "greyscale ClearType" };
            // What each answer is WORTH in the rendering-mode bitmask the MS core faces accumulate
            // in storage[2], so the probe can report the mode itself and not just its bits.
            int[] weight = { 1, 2, 4, 8, 16, 64, 128, 0 };

            // One baseline bar with no program, then one per selector reading cvt[100+i].
            var bars = new List<SyntheticFont.Bar> { new(0, 400, 700, false, false, noProgram: true) };
            for (int i = 0; i < sel.Length; i++)
                bars.Add(new SyntheticFont.Bar(0, 400, 700, false, false, probe: -10000 - i));
            byte[] fontBytes;
            try { fontBytes = SyntheticFont.Build(Fam, bars, sel); }
            finally { SyntheticFont.GaspRanges = null; SyntheticFont.GaspVersion = 1; }
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI refused the prep-probe font");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== what GDI answers GETINFO IN PREP, at {ProbePpem}ppem"
                + (giGasp is null ? ", no gasp" : $", gasp={giGasp}"));
            report.AppendLine("   selector  meaning                      GDI      ours");
            int gdiMode = 0, ourMode = 0;
            try
            {
                var font = new TrueTypeFont(fontBytes);
                var raw = new byte[Width * Height * 4];
                int base0 = -1, ourBase0 = -1;
                for (int i = 0; i <= sel.Length; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Fam, ProbePpem, PenX, ProbePpem + 12, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    int drawn = InkLeftColumn(raw);
                    int ourDrawn = InkLeftColumn(OursRgba(font, ch, ProbePpem, ProbePpem + 12,
                        correction: true));
                    if (i == 0) { base0 = drawn; ourBase0 = ourDrawn; continue; }
                    bool gdiSet = drawn >= 0 && drawn - base0 >= 5;
                    bool ourSet = ourDrawn >= 0 && ourDrawn - ourBase0 >= 5;
                    if (gdiSet) gdiMode |= weight[i - 1];
                    if (ourSet) ourMode |= weight[i - 1];
                    string g = drawn < 0 ? "no ink" : (drawn - base0).ToString();
                    string o = ourDrawn < 0 ? "no ink" : (ourDrawn - ourBase0).ToString();
                    report.AppendLine($"   {sel[i - 1],8}  {names[i - 1],-25} {g,6} {o,9}"
                        + (gdiSet != ourSet ? "   <<< DIFFERS" : ""));
                }
                report.AppendLine("   (ten pixels means the bit came back EXACTLY set, as the face tests it)");
                report.AppendLine($"   => storage[2] would be  GDI {gdiMode}   ours {ourMode}"
                    + (gdiMode != ourMode ? "   <<< DIFFERENT PROGRAM" : "   (same program)"));
            }
            finally { RemoveFontMemResourceEx(handle); }
            Console.Error.Write(report.ToString());
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>HOW GDI ROUNDS A CONTROL VALUE IN CLEARTYPE, read off its own pixels.
        /// `WPF_CVTROUND=<ppem>`.
        /// <para>The Times pools both come down to this. Those faces' ClearType branches read
        /// control values back with RCVT and compute coordinates from them, so if GDI rounds a
        /// control value on a different grid than we do, every coordinate downstream differs --
        /// and no comparison of finished glyphs can tell the two apart. So the probe asks
        /// directly: one bar per sixteenth of a pixel, each shifting itself right by
        /// ROUND(RCVT(k)), and the ink CENTROID reports where it landed to a fraction of a pixel.
        /// A staircase that is flat until the half pixel and then jumps a whole one means GDI
        /// rounds control values to WHOLE PIXELS; a straight ramp of a sixteenth per step means it
        /// rounds them on the sixteenth grid. Ours is printed beside it.</para></summary>
        [Fact]
        public void HowGdiRoundsAControlValue()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the oracle");
            string? spec = Environment.GetEnvironmentVariable("WPF_CVTROUND");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_CVTROUND=<ppem>");
            int ppem = int.Parse(spec!);
            const string Fam = "WpfCvtRoundProbe";
            double unitsPerPixel = SyntheticFont.UnitsPerEm / (double) ppem;

            // Bar 0 is the baseline: the same rectangle with no program at all.
            var bars = new List<SyntheticFont.Bar> { new(0, 400, 700, false, false, noProgram: true) };
            var wanted = new List<double>();
            int den = int.Parse(Environment.GetEnvironmentVariable("WPF_CVTROUND_DEN") ?? "16");
            int steps = int.Parse(Environment.GetEnvironmentVariable("WPF_CVTROUND_STEPS") ?? "24");
            // WPF_CVTROUND_PREP=1 moves the rounding into the PRE-PROGRAM: prep rounds control
            // value i and stores it at 100+i, and the glyph shifts itself by that stored value
            // without rounding anything itself. Whatever grid prep used is what gets drawn.
            bool inPrep = Environment.GetEnvironmentVariable("WPF_CVTROUND_PREP") == "1";
            var prepRound = new List<int>();
            for (int k = 1; k <= steps; k++)
            {
                int units = (int) Math.Round(k / (double) den * unitsPerPixel);
                int slot = bars.Count;
                bars.Add(new SyntheticFont.Bar(units, 400, 700, false, false,
                    probe: inPrep ? -30000 - (100 + slot) : -20000 - slot));
                if (inPrep) prepRound.Add(slot);
                wanted.Add(k / (double) den);
            }
            if (inPrep) Assert.True(100 + steps < 128, "cvt slots 100.. must fit the padded table");
            // Times' own gasp, so the probe sits in the same symmetric-smoothing regime as the
            // faces the question is about.
            SyntheticFont.GaspRanges = new (int, int)[] { (8, 0xA), (17, 0x5), (0xFFFF, 0xF) };
            SyntheticFont.PrepRoundCvts = inPrep ? prepRound : null;
            byte[] fontBytes;
            try { fontBytes = SyntheticFont.Build(Fam, bars); }
            finally { SyntheticFont.GaspRanges = null; SyntheticFont.PrepRoundCvts = null; }
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI refused the control-value probe");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== where a bar lands after shifting itself by ROUND(cvt) in "
                + (inPrep ? "PREP" : "the GLYPH") + $", {Fam} at {ppem}ppem");
            report.AppendLine("   cvt(px)   GDI shift   our shift");
            try
            {
                var font = new TrueTypeFont(fontBytes);
                var raw = new byte[Width * Height * 4];
                double gdiBase = 0, ourBase = 0;
                for (int i = 0; i < bars.Count; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Fam, ppem, PenX, ppem + 12, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    double g = InkCentroidX(raw, bgra: true);
                    double o = InkCentroidX(OursRgba(font, ch, ppem, ppem + 12, correction: true), bgra: false);
                    if (i == 0) { gdiBase = g; ourBase = o; continue; }
                    report.AppendLine($"   {wanted[i - 1],7:0.0000} {g - gdiBase,11:0.000} {o - ourBase,11:0.000}"
                        + ((Math.Abs((g - gdiBase) - (o - ourBase)) > 0.02) ? "   <<<" : ""));
                }
            }
            finally { RemoveFontMemResourceEx(handle); }
            Console.Error.Write(report.ToString());
        }

        /// <summary>The ink-weighted mean COLUMN, which moves with a fraction of a pixel where a
        /// first-inked-column reading cannot.</summary>
        private static double InkCentroidX(byte[] rgba, bool bgra)
        {
            double sum = 0, wsum = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    int ink = (255 - rgba[i]) + (255 - rgba[i + 1]) + (255 - rgba[i + 2]);
                    sum += ink; wsum += ink * (double) x;
                }
            return sum == 0 ? 0 : wsum / sum;
        }

        /// <summary>The first column carrying any ink, or -1. The probe reads a POSITION, so this
        /// deliberately does not care how much ink there is or what shape it makes.</summary>
        private static int InkLeftColumn(byte[] bgra)
        {
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {
                    int i = (y * Width + x) * 4;
                    if (bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200) return x;
                }
            return -1;
        }

        private static float XLeft(List<PathFigure> figures)
        {
            var xs = new SortedSet<float>();
            foreach (PathFigure f in figures) CollectXs(f, xs);
            float min = float.MaxValue;
            foreach (float x in xs) if (x < min) min = x;
            return min == float.MaxValue ? 0f : min;
        }

        [Fact]
        public void SolveTheXCoordinatesGdiFitted()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_SOLVEGLYPH");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_SOLVEGLYPH=char@ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            string[] parts = spec!.Split('@');
            char c = parts[0][0];
            int ppem = int.Parse(parts[1]), baseline = ppem + 12;
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
            Gdi.s_rawRgb = null;
            Assert.True(((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                                                                     out List<PathFigure> ours));

            // The distinct x values, and a map from each to itself that the descent will move.
            var xs = new SortedSet<float>();
            foreach (PathFigure f in ours) CollectXs(f, xs);
            var order = new List<float>(xs);
            var move = new Dictionary<float, float>();
            foreach (float x in order) move[x] = x;

            double Err() => GlyphLampError(Remap(ours, move), PenX, baseline, raw);
            double best = Err();
            Console.Error.WriteLine($"=== '{c}' @{ppem}: solving {order.Count} distinct x values, start {best:0}");
            for (int pass = 0; pass < 4; pass++)
            {
                bool moved = false;
                foreach (float x in order)
                {
                    float keep = move[x], bestAt = keep;
                    // TIE-BREAKING IS NOT NEUTRAL, and it decides what this instrument reports.
                    // Lamps carry seven levels, so a whole RANGE of coordinates can render
                    // identically; the sweep only takes a strict improvement, so whichever end it
                    // starts from is the end it keeps. Sweeping up from -24 reports the leftmost
                    // member of every tie -- and every difference this test has ever reported was
                    // negative. WPF_SOLVE_REVERSE=1 sweeps the other way; where the two disagree,
                    // the coordinate is not determined by GDI's pixels at all and neither answer
                    // means anything.
                    for (int j = -24; j <= 24; j++)
                    {
                        int k = s_solveReverse ? -j : j;
                        move[x] = keep + k / 64f;
                        double e = Err();
                        if (e < best - 1e-9) { best = e; bestAt = move[x]; moved = true; }
                    }
                    move[x] = bestAt;
                }
                if (!moved) break;
            }
            Console.Error.WriteLine($"    solved  {best:0}");

            // AND HOW MUCH OF EACH COORDINATE THE PIXELS ACTUALLY DETERMINE. Lamps carry seven
            // levels, so a range of positions can render identically, and a descent that only
            // takes strict improvements keeps whichever end it started from -- which made every
            // difference this test reported come out NEGATIVE, and made a rule out of the sweep
            // direction. Report the interval instead: it is the honest answer, and where it
            // contains our own value there is nothing to explain.
            var lo = new Dictionary<float, float>();
            var hi = new Dictionary<float, float>();
            foreach (float x in order)
            {
                float keep = move[x];
                float a = keep, b = keep;
                bool any = false;
                for (int k = -24; k <= 24; k++)
                {
                    move[x] = keep + k / 64f;
                    if (Err() > best + 1e-9) continue;
                    if (!any) { a = move[x]; any = true; }
                    b = move[x];
                }
                move[x] = keep;
                lo[x] = a; hi[x] = b;
            }

            var widths = new List<float>();
            int determined = 0, contains = 0;
            foreach (float x in order)
            {
                widths.Add(hi[x] - lo[x]);
                if (hi[x] - lo[x] < 1f / 64f + 1e-6f) determined++;
                if (x >= lo[x] - 1e-6f && x <= hi[x] + 1e-6f) contains++;
            }
            widths.Sort();
            Console.Error.WriteLine($"    of {order.Count} coordinates: {determined} pinned to a 64th,"
                + $" {contains} whose range already contains ours; median range"
                + $" {widths[widths.Count / 2] * 64:0.0}/64");
            {
                var sb2 = new System.Text.StringBuilder("    ours -> [lo, hi] : ");
                foreach (float x in order)
                    sb2.Append($"{x:0.000}->[{lo[x]:0.000},{hi[x]:0.000}] ");
                Console.Error.WriteLine(sb2.ToString());
            }
            var sb = new System.Text.StringBuilder("    ours -> gdi : ");
            foreach (float x in order) sb.Append($"{x:0.000}->{move[x]:0.000}  ");
            Console.Error.WriteLine(sb.ToString());
        }

        /// <summary>WHICH POINTS GDI TOUCHED IN X, derived rather than guessed.
        /// <para>Every rule tried so far has been a rule about VALUES -- which grid a coordinate
        /// rounds on, how wide a stem comes out. All of them failed, and the per-glyph table under
        /// XWholePixelGrid says why they were bound to: whole-pixel MDAP makes the simple glyphs
        /// nearly exact and the complex ones much worse, which is not what a wrong rounding looks
        /// like. It is what a wrong TOUCH SET looks like -- move an anchor and IUP drags every
        /// point behind it, so the damage grows with the number of points rather than with the
        /// size of the error.</para>
        /// <para>The touch set is not observable, but it is derivable. IUP can only put an
        /// untouched point where its two nearest touched neighbours put it: proportionally if the
        /// point started between them, shifted by the nearer one's delta if it started outside. So
        /// take OUR touch set, compute what IUP would have to produce from GDI's OWN anchor
        /// positions, and ask whether GDI's coordinate for that point is a value that prediction
        /// allows. Where it is not, GDI touched a point we interpolate -- and that is a fact about
        /// GDI, not a preference between two fits.</para>
        /// <para>Ask it against the INTERVAL, never against the single solved value: the solver's
        /// answer is its own tie-break wherever the pixels leave a coordinate free, and reading
        /// that as GDI's position is what produced this investigation's retracted conclusions.</para>
        /// <para>WPF_TOUCHSETS=chars@ppem, e.g. "HNM8s0@12".</para></summary>
        [Fact]
        public void WhichPointsGdiTouchedInX()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_TOUCHSETS");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_TOUCHSETS=chars@ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            string[] parts = spec!.Split('@');
            int ppem = int.Parse(parts[1]), baseline = ppem + 12;
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            int totalPoints = 0, totalUntouched = 0, totalImpossible = 0;
            int inRange = 0, inRangeTotal = 0;
            double moved = 0;
            int touchedOk = 0, touchedTotal = 0, interpOk = 0, interpTotal = 0;
            int outlineOnly = 0, cvOnly = 0, bothWork = 0, neitherWorks = 0;
            int rigidBefore = 0, rigidAfter = 0, rigidGlyphs = 0;
            int bilevelOk = 0, unhintedOk = 0, oracleTotal = 0;
            int oursOnOracle = 0;
            double movedBilevel = 0, movedOurs = 0;

            TrueTypeInterpreter.s_capturePoints = true;
            try
            {
                foreach (char c in parts[0])
                {
                    var raw = new byte[Width * Height * 4];
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height,
                             false, false);
                    Gdi.s_rawRgb = null;
                    if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                                                                      out List<PathFigure> ours))
                        continue;
                    TrueTypeInterpreter.GlyphPoints? pts = font.LastHintedPoints;
                    if (pts is null || pts.PointCount == 0) continue;

                    SolveGdiX(ours, baseline, raw, out Dictionary<float, float> move,
                              out Dictionary<float, float> lo, out Dictionary<float, float> hi);

                    // GDI's value for a point is the solved value of the coordinate our fit put it
                    // at. Points that share a coordinate share an answer -- the pixels cannot tell
                    // them apart either, so no information is lost that the oracle ever had.
                    float GdiAt(int i)
                    {
                        float k = MathF.Round(pts.FitX[i], 3);
                        return move.TryGetValue(k, out float v) ? v : pts.FitX[i];
                    }
                    (float, float) Range(int i)
                    {
                        float k = MathF.Round(pts.FitX[i], 3);
                        return lo.TryGetValue(k, out float a2) ? (a2, hi[k]) : (GdiAt(i), GdiAt(i));
                    }

                    var impossible = new List<int>();
                    var detail = new List<string>();
                    // THE SOLVER ANSWERS PER COORDINATE, NOT PER POINT. Points our fit put
                    // on the same x move together in the descent and come back with one
                    // answer between them, so a point sharing its x with a TOUCHED point is
                    // reported wherever the touched one wanted to be, and the difference is
                    // the instrument, not GDI. Count the sharers and say so.
                    var sharers = new Dictionary<float, (int all, int touched)>();
                    for (int i = 0; i < pts.PointCount; i++)
                    {
                        float k = MathF.Round(pts.FitX[i], 3);
                        sharers.TryGetValue(k, out (int all, int touched) v);
                        sharers[k] = (v.all + 1, v.touched + (pts.TouchedX[i] ? 1 : 0));
                    }
                    int untouched = 0, first = 0;
                    foreach (int end in pts.EndPoints)
                    {
                        int n = end - first + 1;
                        if (n <= 0) { first = end + 1; continue; }
                        var touched = new List<int>();
                        for (int i = first; i <= end; i++) if (pts.TouchedX[i]) touched.Add(i);
                        if (touched.Count == 0) { first = end + 1; continue; }

                        for (int t = 0; t < touched.Count; t++)
                        {
                            int a = touched[t], b = touched[(t + 1) % touched.Count];
                            // Walk the points strictly between the two anchors, wrapping round the
                            // contour the way IUP does.
                            for (int step = 1; ; step++)
                            {
                                int i = first + ((a - first + step) % n);
                                if (i == b) break;
                                untouched++;
                                // IUP, exactly as the interpreter runs it: the inside/outside
                                // test on the SCALED start, the proportion in FONT UNITS. The two
                                // disagree by up to a 64th, which is the size of several of the
                                // differences being judged here, so approximating one with the
                                // other decides the answer.
                                int r1 = a, r2 = b;
                                if (pts.OrusX[r1] > pts.OrusX[r2]) (r1, r2) = (r2, r1);
                                float o1 = pts.StartX[r1], o2 = pts.StartX[r2];
                                float d1 = GdiAt(r1) - o1, d2 = GdiAt(r2) - o2;
                                float op = pts.StartX[i], predicted;
                                if (op <= o1) predicted = op + d1;
                                else if (op >= o2) predicted = op + d2;
                                else if (pts.OrusX[r1] == pts.OrusX[r2]) predicted = op + d2;
                                else
                                    predicted = (o1 + d1)
                                        + (pts.OrusX[i] - pts.OrusX[r1])
                                          * ((o2 + d2 - (o1 + d1))
                                             / (pts.OrusX[r2] - pts.OrusX[r1]));

                                (float rl, float rh) = Range(i);
                                float gap = predicted < rl ? rl - predicted
                                          : predicted > rh ? predicted - rh : 0f;
                                // A 64th of tolerance is not slack, it is the quantum the whole
                                // pipeline works in: our anchors and GDI's differ by rounding
                                // alone, and IUP carries that difference into every point behind
                                // them. Only a gap LARGER than the quantum says anything.
                                if (gap > 1f / 64f)
                                {
                                    impossible.Add(i);
                                    detail.Add($"      pt{i,-3} {(pts.OnCurve[i] ? "on " : "off")}"
                                        + $" start({op:0.00},{pts.StartY[i]:0.00}) ours {pts.FitX[i]:0.000}"
                                        + $" iup-from-gdi {predicted:0.000} but gdi allows"
                                        + $" [{rl:0.000},{rh:0.000}]  gap {gap * 64:0.0}/64"
                                        + $" [shared by {sharers[MathF.Round(pts.FitX[i], 3)].all},"
                                          + $" {sharers[MathF.Round(pts.FitX[i], 3)].touched} touched]"
                                        + $"  anchors {a}(start {pts.StartX[a]:0.00} gdi {GdiAt(a):0.000})"
                                        + $",{b}(start {pts.StartX[b]:0.00} gdi {GdiAt(b):0.000})");
                                }
                            }
                        }
                        first = end + 1;
                    }

                    // THE SAME GLYPH FITTED THE OTHER WAY. Our model of GDI's ClearType x takes
                    // the OUTLINE distance almost always, because the control-value cut-in is
                    // divided by sixteen. Fitting it again with the cut-in whole takes the CONTROL
                    // VALUE almost always. Every coordinate then has two candidate positions, and
                    // GDI's own pixels can be asked which of them it used -- which is a far better
                    // question than "is ours allowed", because it names the alternative.
                    int savedDiv = TrueTypeInterpreter.s_cutInDivisor;
                    float[] cvFit;
                    try
                    {
                        TrueTypeInterpreter.s_cutInDivisor = 1;
                        // A FRESH face: the hinted outline is cached per (glyph, size), so asking
                        // the same instance twice returns the first answer and the second
                        // configuration silently measures as a no-op.
                        var other = new TrueTypeFont(File.ReadAllBytes(file!));
                        ((IHintedGlyphFont) other).TryGetHintedOutline(other.GlyphIndex(c), ppem, out _);
                        cvFit = other.LastHintedPoints?.FitX ?? Array.Empty<float>();
                    }
                    finally { TrueTypeInterpreter.s_cutInDivisor = savedDiv; }

                    // WHAT IS GDI'S CLEARTYPE X, ASKED WITHOUT A MODEL AT ALL.
                    //
                    // There are two oracles and they have never been put side by side.
                    // GetGlyphOutline gives GDI's own BI-LEVEL fitted x exactly, and the lamp
                    // solver gives the interval its CLEARTYPE pixels allow. So ask, of GDI's
                    // bi-level answer and of the plain scaled outline, which one lands inside
                    // GDI's own ClearType interval. Neither is a guess about what GDI does: both
                    // are things GDI itself produced.
                    List<Vector2> ggoPlain = GdiStageTests.Flatten(
                        GdiStageTests.GdiOutline(c, ProbeFamily(), ppem, unhinted: true));
                    List<Vector2> ggoFit = GdiStageTests.Flatten(
                        GdiStageTests.GdiOutline(c, ProbeFamily(), ppem, unhinted: false));
                    bool ggoUsable = ggoPlain.Count == ggoFit.Count && ggoPlain.Count > 0;

                    // AND HOW OFTEN OUR OWN COORDINATE IS ONE GDI ALLOWS. The impossible count
                    // above judges the touch set; this judges the geometry we actually ship,
                    // and it is the only measure of our CLEARTYPE fitting there is -- GGO
                    // renders greyscale and cannot see the ClearType branch at all.
                    for (int i = 0; i < pts.PointCount; i++)
                    {
                        (float ql, float qh) = Range(i);
                        inRangeTotal++;
                        // How far the x fitting moved this point at all. If the machinery
                        // were as inert as each knob measures, this would be near zero.
                        moved += Math.Abs(pts.FitX[i] - pts.StartX[i]);
                        // AND WHICH KIND OF COORDINATE IS WRONG. A point the program PLACED
                        // being out of range indicts a fitting instruction; an INTERPOLATED
                        // one indicts the anchors it hangs between. The two want completely
                        // different fixes, and nothing has ever separated them.
                        bool ok = pts.FitX[i] >= ql - 1f / 64f && pts.FitX[i] <= qh + 1f / 64f;
                        if (pts.TouchedX[i]) { touchedTotal++; if (ok) touchedOk++; }
                        else { interpTotal++; if (ok) interpOk++; }
                        if (ok) inRange++;

                        if (ggoUsable)
                        {
                            int j = GdiStageTests.Nearest(ggoPlain, pts.StartX[i], -pts.StartY[i]);
                            if (j >= 0)
                            {
                                oracleTotal++;
                                float bilevel = ggoFit[j].X, plainX = ggoPlain[j].X;
                                if (bilevel >= ql - 1f / 64f && bilevel <= qh + 1f / 64f) bilevelOk++;
                                if (plainX >= ql - 1f / 64f && plainX <= qh + 1f / 64f) unhintedOk++;
                                // Ours over the SAME subset, or the three numbers are not
                                // comparable: the two oracles can only be read on glyphs
                                // GGO reports consistently, and ours is defined everywhere.
                                if (ok) oursOnOracle++;
                                // HOW FAR EACH MODE MOVES X AT ALL. GDI's bi-level fit
                                // against the outline it started from, and ours against the
                                // same, so the two can be compared as displacements rather
                                // than as scores.
                                movedBilevel += Math.Abs(bilevel - plainX);
                                movedOurs += Math.Abs(pts.FitX[i] - plainX);
                            }
                        }

                        // Which distance GDI used, where the two disagree enough to tell.
                        if (pts.TouchedX[i] && i < cvFit.Length)
                        {
                            bool cvOk = cvFit[i] >= ql - 1f / 64f && cvFit[i] <= qh + 1f / 64f;
                            if (Math.Abs(cvFit[i] - pts.FitX[i]) > 1f / 64f)
                            {
                                if (ok && !cvOk) outlineOnly++;
                                else if (cvOk && !ok) cvOnly++;
                                else if (ok) bothWork++;
                                else neitherWorks++;
                            }
                        }
                    }
                    // AND WHETHER A RIGID SHIFT WOULD FIX THE GLYPH. Asking for the best whole-
                    // glyph displacement before theorising about shape is the lesson this
                    // investigation has had to learn twice. If a glyph's coordinates come good
                    // together under one shift, the fault is where the glyph was PUT -- its origin,
                    // or the first point anchored to a phantom -- and not how it was shaped.
                    int atZero = 0, atBest = 0, bestShift = 0;
                    // WIDE ENOUGH NOT TO CLIP. At plus or minus eight several glyphs reported
                    // a best shift of exactly the limit, which means the search, not the
                    // glyph, was choosing the answer.
                    for (int k = -32; k <= 32; k++)
                    {
                        int fit = 0;
                        for (int i = 0; i < pts.PointCount; i++)
                        {
                            (float ql, float qh) = Range(i);
                            float v = pts.FitX[i] + k / 64f;
                            if (v >= ql - 1f / 64f && v <= qh + 1f / 64f) fit++;
                        }
                        if (k == 0) atZero = fit;
                        if (fit > atBest) { atBest = fit; bestShift = k; }
                    }
                    rigidBefore += atZero;
                    rigidAfter += atBest;
                    if (bestShift != 0 && atBest > atZero)
                    {
                        rigidGlyphs++;
                        // AND WHAT WOULD PREDICT IT. If GDI puts a glyph's LEFT EDGE on a
                        // whole pixel and we leave it where the outline falls, the shift is
                        // exactly what rounding that edge would cost -- a few sixty-fourths,
                        // differing per glyph in size and sign, which is the shape of what
                        // the measurement shows.
                        float xMin = float.MaxValue;
                        for (int i = 0; i < pts.PointCount; i++)
                            if (pts.StartX[i] < xMin) xMin = pts.StartX[i];
                        float lsbRound = MathF.Round(xMin) - xMin;
                        Console.Error.WriteLine($"      '{c}' a rigid {bestShift:+0;-0}/64 would take"
                            + $" {atZero} of {pts.PointCount} coordinates to {atBest}"
                            + $"   (left edge {xMin:0.000}, rounding it would move"
                            + $" {lsbRound * 64:+0.0;-0.0}/64)");
                    }

                    totalPoints += pts.PointCount;
                    totalUntouched += untouched;
                    totalImpossible += impossible.Count;
                    int ourTouched = 0;
                    for (int i = 0; i < pts.PointCount; i++) if (pts.TouchedX[i]) ourTouched++;
                    Console.Error.WriteLine($"'{c}' @{ppem}: {pts.PointCount} points, we touch"
                        + $" {ourTouched} in x; of {untouched} we interpolate, {impossible.Count}"
                        + " cannot be what GDI has"
                        + (impossible.Count == 0 ? "" : " -- points " + string.Join(",", impossible)));
                    foreach (string d in detail) Console.Error.WriteLine(d);
                }
            }
            finally { TrueTypeInterpreter.s_capturePoints = false; }

            Console.Error.WriteLine($"TOTAL {totalPoints} points, {totalUntouched} interpolated,"
                + $" {totalImpossible} impossible under our touch set;"
                + $" {inRange} of {inRangeTotal} of our own coordinates"
                + $" ({100.0 * inRange / Math.Max(1, inRangeTotal):0.0}%) are ones GDI allows;"
                + $" our x fitting moves a point {moved / Math.Max(1, inRangeTotal):0.000}px on average");
            Console.Error.WriteLine($"      of the coordinates the program PLACED,"
                + $" {touchedOk} of {touchedTotal}"
                + $" ({100.0 * touchedOk / Math.Max(1, touchedTotal):0.0}%) are allowed;"
                + $" of the ones IUP interpolated, {interpOk} of {interpTotal}"
                + $" ({100.0 * interpOk / Math.Max(1, interpTotal):0.0}%)");
            Console.Error.WriteLine($"      of {oracleTotal} coordinates, GDI's own BI-LEVEL answer"
                + $" is inside GDI's ClearType interval {bilevelOk} times"
                + $" ({100.0 * bilevelOk / Math.Max(1, oracleTotal):0.0}%), the plain scaled outline"
                + $" {unhintedOk} times ({100.0 * unhintedOk / Math.Max(1, oracleTotal):0.0}%),"
                + $" and ours {oursOnOracle}"
                + $" ({100.0 * oursOnOracle / Math.Max(1, oracleTotal):0.0}%)");
            Console.Error.WriteLine($"      displacement from the unhinted outline:"
                + $" GDI bi-level {movedBilevel / Math.Max(1, oracleTotal):0.000}px per point,"
                + $" ours {movedOurs / Math.Max(1, oracleTotal):0.000}px");
            Console.Error.WriteLine($"      a per-glyph rigid shift would take {rigidBefore}"
                + $" coordinates to {rigidAfter}, helping {rigidGlyphs} glyphs");
            Console.Error.WriteLine($"      where the two distances disagree: outline is the one"
                + $" GDI allows {outlineOnly}x, the control value {cvOnly}x,"
                + $" either would do {bothWork}x, neither {neitherWorks}x");
        }

        /// <summary>GDI's own fitted x coordinates, and the interval of each that the pixels allow.
        /// <para>Coordinate descent over the distinct x values of our fit, scoring against GDI's
        /// lamps -- sound because the rasterizer reproduces GDI's lamps exactly given the right
        /// outline, so a fit returns GDI's geometry rather than a resemblance of it.</para></summary>
        private static void SolveGdiX(List<PathFigure> ours, int baseline, byte[] raw,
                                      out Dictionary<float, float> move,
                                      out Dictionary<float, float> lo,
                                      out Dictionary<float, float> hi)
        {
            var xs = new SortedSet<float>();
            foreach (PathFigure f in ours) CollectXs(f, xs);
            var order = new List<float>(xs);
            move = new Dictionary<float, float>();
            foreach (float x in order) move[x] = x;
            Dictionary<float, float> m = move;
            double Err() => GlyphLampError(Remap(ours, m), PenX, baseline, raw);
            double best = Err();
            for (int pass = 0; pass < 4; pass++)
            {
                bool moved = false;
                foreach (float x in order)
                {
                    float keep = m[x], bestAt = keep;
                    for (int j = -24; j <= 24; j++)
                    {
                        int k = s_solveReverse ? -j : j;
                        m[x] = keep + k / 64f;
                        double err = Err();
                        if (err < best - 1e-9) { best = err; bestAt = m[x]; moved = true; }
                    }
                    m[x] = bestAt;
                }
                if (!moved) break;
            }
            lo = new Dictionary<float, float>();
            hi = new Dictionary<float, float>();
            foreach (float x in order)
            {
                float keep = m[x];
                float a = keep, b = keep;
                bool any = false;
                for (int k = -24; k <= 24; k++)
                {
                    m[x] = keep + k / 64f;
                    if (Err() > best + 1e-9) continue;
                    if (!any) { a = m[x]; any = true; }
                    b = m[x];
                }
                m[x] = keep;
                lo[x] = a; hi[x] = b;
            }
        }

        /// <summary>GDI'S STEM POSITION AND WIDTH, SOLVED JOINTLY -- the constraint the
        /// per-coordinate intervals cannot give.
        /// <para>Every solve so far has moved ONE coordinate and held the rest still, so its
        /// interval is conditional on values that are themselves probably wrong, and a pair of such
        /// intervals says much less than it appears to. A stem is two coordinates that only mean
        /// anything together: what its pixels determine is the PAIR.</para>
        /// <para>So vary both edges over a neighbourhood, score only the COLUMNS THE STEM OCCUPIES
        /// -- which makes the answer independent of every coordinate outside it, the thing the
        /// single-coordinate solve could never claim -- and report every pair that reaches the
        /// minimum, as (left, width), because that is the form a rule would be written in.</para>
        /// <para>WPF_SOLVESTEM=char@ppem, with WPF_FACE to choose the face.</para>
        /// <para>Each stem also prints one machine-readable line -- face, char, ppem, our left and
        /// width, the UNFITTED left and width, GDI's allowed left and width, the residual, and
        /// whether ours is among the winners -- because a rule for stem placement has to be a rule
        /// about something, and the unfitted position is the only candidate that does not
        /// presuppose the answer.</para>
        /// <para>WHAT IT SAYS, over 131 stems from Tahoma and Arial at 12 and 16ppem. The solve is
        /// EXACT on 27 of them; on the rest no pair in the search box reproduces GDI's lamps,
        /// because neighbouring curves contribute to those columns too, so read the rule fit on the
        /// 27 and not on the whole:</para>
        /// <code>
        ///   GDI's stem LEFT EDGE                 GDI's stem WIDTH
        ///     ours as shipped        52%           ours as shipped        89%
        ///     the unfitted value     33%           the unfitted value     78%
        ///     unfitted round 1/3     33%           unfitted round 1/3     22%
        ///     unfitted round 1/16    30%           unfitted round 1px      7%
        ///     unfitted round 1px     26%
        ///     unfitted floor 1/3     26%
        ///     unfitted floor 1px     22%
        ///     unfitted floor 1/2     22%
        /// </code>
        /// <para>Our widths are right 89 per cent of the time and our left edges 52, which is the
        /// same split the gradient census and the per-coordinate solve both reported. And OURS IS
        /// THE BEST OF EVERY CANDIDATE: no rounding of the unfitted left edge onto a whole pixel, a
        /// half, a third or a sixteenth, in either direction, comes within twenty points of it. So
        /// GDI's stem placement is not a grid applied to the outline, and the family of rules of
        /// that shape is now closed against an oracle that is unconditional per stem rather than
        /// per coordinate.</para>
        /// <para>THE ANCHOR HYPOTHESIS IS DEAD, AND IT DIED OF MY OWN BIAS. It looked as though
        /// we and GDI hold different edges: Tahoma's 'H' at 16ppem has GDI moving the left stem's
        /// outer edge and leaving the right stem's, and us doing the opposite. Counted over the
        /// exactly solved stems that came out 9 to 1 in favour of GDI holding the RIGHT edge --
        /// and the count was worthless, because I derived the right edge's allowed range as
        /// lLo+wLo..lHi+wHi. That combines two extremes no single winner reaches, so it is far too
        /// wide, and it is exactly the range the test compares against. With the right edge's own
        /// marginal emitted from the winners the asymmetry vanishes: GDI holds the left edge twice,
        /// the right three times, either seven, and NEITHER fifteen, with median displacements of
        /// 8.5/64 on the left and 9.5/64 on the right. Symmetric. GDI has no preferred edge; it
        /// TRANSLATES the stem, keeping the width, which is what the gradient census said from the
        /// pixels.</para>
        /// <para>AND NO GRID DESCRIBES THAT TRANSLATION. Over 42 exactly solved stems from Tahoma,
        /// Arial and Verdana, with every marginal -- left, right, width and centre -- emitted from
        /// the winners rather than inferred:</para>
        /// <code>
        ///   centre on a whole pixel    5%      left on a whole pixel   19%
        ///   centre on a half          12%      left on a third         45%
        ///   centre on a third         21%      right on a whole pixel  26%
        ///   centre on a sixth         36%      right on a third        43%
        ///   centre = unfitted centre  45%
        ///   centre = OUR centre       67%
        /// </code>
        /// <para>Ours is the best predictor of every feature of GDI's stem -- 52 per cent of left
        /// edges, 89 of widths, 67 of centres -- and nothing built out of a grid comes near it, on
        /// any of the three features, against whole pixels, halves, thirds or sixths.</para>
        /// <para>So the search for "GDI's stem rule" is finished, not stalled: there is no rule of
        /// that shape to find. GDI's ClearType stem placement is the outcome of the face's own
        /// program under conditions we already reproduce better than any alternative anyone has
        /// proposed, and the residue is per-stem detail below a sixteenth of a pixel. Anything
        /// further needs a different kind of evidence, not another candidate rule.</para></summary>
        [Fact]
        public void SolveOneStemJointly()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_SOLVESTEM");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_SOLVESTEM=char@ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {ProbeFamily()}");

            string[] parts = spec!.Split('@');
            char c = parts[0][0];
            int ppem = int.Parse(parts[1]), baseline = ppem + 12;
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(c.ToString(), ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
            Gdi.s_rawRgb = null;
            // WHERE THE OUTLINE PUT EACH EDGE BEFORE FITTING. A rule for GDI's stem placement
            // has to be a rule about something, and the unfitted position is the only
            // candidate that does not presuppose the answer.
            TrueTypeInterpreter.s_capturePoints = true;
            List<PathFigure> ours;
            TrueTypeInterpreter.GlyphPoints? pts;
            try
            {
                Assert.True(((IHintedGlyphFont) font).TryGetHintedOutline(
                                 font.GlyphIndex(c), ppem, out ours));
                pts = font.LastHintedPoints;
            }
            finally { TrueTypeInterpreter.s_capturePoints = false; }

            var plainOf = new Dictionary<float, float>();
            if (pts is not null)
                for (int i = 0; i < pts.PointCount; i++)
                {
                    float key = MathF.Round(pts.FitX[i], 3);
                    if (!plainOf.ContainsKey(key)) plainOf[key] = pts.StartX[i];
                }
            float Plain(float v) => plainOf.TryGetValue(MathF.Round(v, 3), out float u) ? u : v;

            var xs = new SortedSet<float>();
            foreach (PathFigure f in ours) CollectXs(f, xs);
            var order = new List<float>(xs);
            Console.Error.WriteLine($"=== {ProbeFamily()} '{c}' @{ppem}: {order.Count} distinct x"
                                    + $" -- {string.Join(" ", order)}");

            for (int k = 0; k + 1 < order.Count; k++)
            {
                float a0 = order[k], b0 = order[k + 1];
                if (b0 - a0 < 0.4f || b0 - a0 > 4f) continue;    // a hairline or a counter, not a stem
                int colLo = (int) MathF.Floor(PenX + a0) - 2;
                int colHi = (int) MathF.Ceiling(PenX + b0) + 2;

                var move = new Dictionary<float, float>();
                foreach (float x in order) move[x] = x;
                double best = double.MaxValue;
                var winners = new List<(int da, int db)>();
                for (int da = -14; da <= 14; da++)
                    for (int db = -14; db <= 14; db++)
                    {
                        move[a0] = a0 + da / 64f;
                        move[b0] = b0 + db / 64f;
                        double err = GlyphLampError(Remap(ours, move), PenX, baseline, raw,
                                                    colLo, colHi);
                        if (err < best - 1e-9) { best = err; winners.Clear(); }
                        if (err < best + 1e-9) winners.Add((da, db));
                    }

                float wLo = float.MaxValue, wHi = float.MinValue, lLo = float.MaxValue, lHi = float.MinValue;
                // The RIGHT edge's own marginal, not the sum of the other two. Deriving it as
                // lLo+wLo..lHi+wHi combines two extremes that no single winner reaches, so it
                // is far too wide -- and it is exactly the range a test of "which edge did GDI
                // hold" compares against, so the slack biases that test toward the right edge.
                float rLo = float.MaxValue, rHi = float.MinValue;
                float cLo = float.MaxValue, cHi = float.MinValue;
                foreach ((int da, int db) w in winners)
                {
                    float left = a0 + w.da / 64f, right = b0 + w.db / 64f;
                    float width = right - left;
                    if (left < lLo) lLo = left;
                    if (left > lHi) lHi = left;
                    if (right < rLo) rLo = right;
                    if (right > rHi) rHi = right;
                    if (width < wLo) wLo = width;
                    if (width > wHi) wHi = width;
                    float centre = (left + right) / 2;
                    if (centre < cLo) cLo = centre;
                    if (centre > cHi) cHi = centre;
                }
                bool oursWins = winners.Contains((0, 0));
                Console.Error.WriteLine($"    stem {a0:0.000}..{b0:0.000} (ours w={b0 - a0:0.000})"
                    + $" columns {colLo}..{colHi}: residual {best:0}, {winners.Count} pairs reach it,"
                    + $" ours {(oursWins ? "IS" : "is NOT")} among them");
                Console.Error.WriteLine($"      GDI allows left {lLo:0.000}..{lHi:0.000},"
                    + $" width {wLo:0.000}..{wHi:0.000}");
                // And the same thing in a form a script can fit a rule to.
                Console.Error.WriteLine($"STEM {ProbeFamily()},{c},{ppem},"
                    + $"{a0:0.0000},{b0 - a0:0.0000},"
                    + $"{Plain(a0):0.0000},{Plain(b0) - Plain(a0):0.0000},"
                    + $"{lLo:0.0000},{lHi:0.0000},{wLo:0.0000},{wHi:0.0000},"
                    + $"{rLo:0.0000},{rHi:0.0000},"
                    + $"{cLo:0.0000},{cHi:0.0000},"
                    + $"{best:0},{(oursWins ? 1 : 0)}");
            }
        }

        /// <summary>WHERE GDI PUTS A STEM, read off a font that contains nothing else.
        /// <para>Every attempt to find GDI's stem rule so far has inferred it from real faces,
        /// where dozens of instructions interact and any candidate can be defended or attacked by
        /// choosing a different glyph. The rule families are now closed that way and none of them
        /// fitted -- so the thing to change is not the candidate but the KIND of evidence.</para>
        /// <para>The synthetic bar's program is already the minimal case, and nothing else:</para>
        /// <code>
        ///   SVTCA[x]
        ///   PUSHB 0     MDAP[1]        round the LEFT edge to the grid
        ///   PUSHB 1,cvt MIRP           place the RIGHT edge at the control value
        ///   IUP[x]
        /// </code>
        /// <para>So sweeping the bar's unfitted left edge in fine steps and reading back where GDI
        /// put the two edges measures the two functions directly: what MDAP does to a POSITION and
        /// what MIRP does with a WIDTH. No other instruction can be blamed, and the input is known
        /// exactly rather than solved for.</para>
        /// <para>Reading back is the same inversion the coverage work relies on: our rasterizer
        /// reproduces GDI's lamps exactly given the right rectangle, so the rectangle that
        /// reproduces GDI's row IS what GDI drew. Searched on the 64th grid over both edges.</para>
        /// <para>WPF_STEMPROBE=&lt;path&gt; to collect it.</para>
        /// <para>WHAT IT MEASURED, AND WHAT IT DID NOT. At 16ppem with a 1.25px outline width,
        /// GDI places the bar consistently LEFT of where its outline is, by 4 to 16 sixty-fourths,
        /// mean about -10/64. We place it exactly at the outline, because our MDAP rounds on the
        /// sixteenth grid and the input is already there. That is the same direction as every
        /// finding on real faces -- our stems sit right of GDI's -- now shown on a font that
        /// contains one bar, one MDAP and one MIRP and nothing else, with the input known rather
        /// than solved for.</para>
        /// <para>But it is NOT a rule, and the reason is the instrument. A single isolated bar
        /// saturates its lamps, so a whole neighbourhood of geometries draws it identically: every
        /// row here has 100 to 121 rectangles reaching residual ZERO, and the allowed left edge is
        /// a range about 9/64 wide. Read as ranges rather than as the sweep's first winner -- which
        /// is the trap this file has recorded three times -- no quantiser survives:</para>
        /// <code>
        ///   round to a whole pixel   input 3.250 wants >= 3.094, whole gives 3.000
        ///   round to a third         input 3.188 allows <= 3.078, a third gives 3.333
        ///   floor to a third         input 3.250 wants >= 3.094, floor gives 3.000
        ///   round to a sixth         input 3.188 allows <= 3.078, a sixth gives 3.167
        /// </code>
        /// <para>The mean shift is close to a sixth of a pixel, which is half a lamp, and a
        /// half-lamp difference in where the lamp grid is assumed to start would produce exactly
        /// this. Tested: WPF_SUBPIXEL_SAMPLE=centre moves the specimen by 529 out of 2,343,248, so
        /// the sampling phase is not it either.</para>
        /// <para>TWO THINGS THAT DO NOT EXPLAIN THE TIES, so nobody retries them. Narrowing the
        /// bar does not: at 48, 80, 160 and 256 font units the mean tie count is 107, 107,
        /// 108, 108 -- so it is not lamp saturation. And our lamp QUANTISER does not, which is
        /// the more surprising one: SubpixelLevels is 3, each lamp taking one of {0, half, 1},
        /// which sets a floor of a third of a lamp -- a ninth of a pixel, near enough the 9/64
        /// the ranges actually measure. It looked like the whole answer. Setting
        /// WPF_SUBPIXEL_QUANT=0, which keeps the exact area and removes that floor entirely,
        /// leaves the mean tie count at 108. Unchanged. So something else makes a hundred
        /// distinct rectangles render bit-identically here and it is not yet known what.</para>
        /// <para>TO MAKE THIS DECISIVE the probe glyph has to constrain harder than one bar can.
        /// Two or three bars at known separations in ONE glyph would do it -- the lamp pattern
        /// stops saturating and the ties collapse -- and that means teaching SyntheticFont to write
        /// a multi-bar glyph, which it cannot do today.</para></summary>
        [Fact]
        public void WhereGdiPutsAStem()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? path = Environment.GetEnvironmentVariable("WPF_STEMPROBE");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STEMPROBE to collect this");

            const string Family = "WpfStemPlace";
            const int Ppem = 16;
            // 2048 units per em at 16ppem is 128 units to the pixel, so 8 units is a sixteenth of
            // one. Sweep a whole pixel of PHASE at a fixed width, which is the variable the rule
            // has to be a function of.
            // WPF_STEMPROBE_W in font units. A WIDE bar saturates its lamps and a whole
            // neighbourhood of geometries draws it identically; a narrow one has a sharp
            // profile that moves distinctly, so the ties should collapse and the readout
            // tighten. 160 units is 1.25px at 16ppem.
            int Width0 = int.TryParse(Environment.GetEnvironmentVariable("WPF_STEMPROBE_W"),
                                      out int w0) && w0 > 0 ? w0 : 160;
            // A NO-PROGRAM CONTROL FIRST. Where the model thinks a bar's left edge is and where
            // GDI actually draws it differ by a constant -- the side bearing the synthetic font
            // happens to declare -- and without measuring that constant every reading below is
            // offset by it. An unhinted bar must come back exactly where its outline puts it, so
            // whatever it comes back short by IS the constant.
            var bars = new List<SyntheticFont.Bar>();
            // THE DECLARED BEARING MOVES WITH THE OUTLINE, which is hmtx's default here and is
            // what makes this a sweep at all. Holding it still was tried and is the opposite
            // mistake: a glyph is drawn at its origin, xMin minus the bearing, so pinning the
            // bearing while moving xMin moves the origin with it and every bar lands in the SAME
            // place. Measured that way GDI's left edge stays near 2.9 while the model's input
            // climbs to 3.94, which reads as an MDAP moving a point by up to 40/64 and is entirely
            // the instrument.
            bars.Add(new SyntheticFont.Bar(Width0, 384, 384 + Width0, round: false,
                                           minDistance: false, noProgram: true));
            for (int step = 0; step < 16; step++)
            {
                int left = 384 + step * 8;
                bars.Add(new SyntheticFont.Bar(Width0, left, left + Width0, round: true,
                                               minDistance: false));
            }
            byte[] fontBytes = SyntheticFont.Build(Family, bars);
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI would not accept the probe font");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== where GDI puts a stem: {Family} at {Ppem}ppem,"
                              + $" outline width {Width0 / 128.0:0.0000}px, MIRP round=true");
            report.AppendLine("   unfitted left -> GDI left, GDI width      (all in pixels)");
            try
            {
                var raw = new byte[Width * Height * 4];
                var font = new TrueTypeFont(fontBytes);
                float offset = 0;
                for (int i = 0; i < bars.Count; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    int baseline = Ppem + 12;
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Family, Ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;

                    float plainLeft = bars[i].Left * Ppem / (float) SyntheticFont.UnitsPerEm;
                    float plainWidth = Width0 * Ppem / (float) SyntheticFont.UnitsPerEm;
                    float lLo = float.MaxValue, lHi = float.MinValue;
                    float wLo = float.MaxValue, wHi = float.MinValue;
                    int hits = 0;

                    // Invert: which rectangles reproduce GDI's lamps. There are MANY -- a bar's
                    // row saturates, so a whole neighbourhood of geometries draws it identically --
                    // so report the RANGE and never the sweep's first winner, which is the trap
                    // that has caught this investigation three times.
                    double best = double.MaxValue;
                    for (int pass = 0; pass < 2; pass++)
                        for (int l = -40; l <= 40; l++)
                            for (int w = -24; w <= 24; w++)
                            {
                                float left = plainLeft + l / 64f, wid = plainWidth + w / 64f;
                                if (wid <= 0.05f) continue;
                                double err = GlyphLampError(Rectangle(left, wid), PenX, baseline, raw);
                                if (pass == 0) { if (err < best - 1e-9) best = err; continue; }
                                if (err > best + 1e-9) continue;
                                if (left < lLo) lLo = left;
                                if (left > lHi) lHi = left;
                                if (wid < wLo) wLo = wid;
                                if (wid > wHi) wHi = wid;
                                hits++;
                            }

                    float ourLeft = float.NaN, ourWidth = float.NaN;
                    if (((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(ch[0]), Ppem,
                                                                      out List<PathFigure> mine))
                    {
                        var xs = new SortedSet<float>();
                        foreach (PathFigure f in mine) CollectXs(f, xs);
                        if (xs.Count >= 2) { ourLeft = xs.Min; ourWidth = xs.Max - xs.Min; }
                    }

                    if (i == 0)
                    {
                        // The control. Its own allowed range straddles where its outline is; the
                        // centre of that range against the outline is the model's offset.
                        offset = (lLo + lHi) / 2 - plainLeft;
                        report.AppendLine($"   control (no program): outline {plainLeft:0.0000},"
                            + $" GDI allows {lLo:0.0000}..{lHi:0.0000} w {wLo:0.0000}..{wHi:0.0000}"
                            + $"  => model offset {offset * 64:+0.0;-0.0}/64");
                        continue;
                    }
                    report.AppendLine($"   {plainLeft,7:0.0000} -> left"
                        + $" {lLo - offset,7:0.0000}..{lHi - offset,7:0.0000}"
                        + $" w {wLo,6:0.0000}..{wHi,6:0.0000}"
                        + $"   ours {ourLeft,7:0.0000} w {ourWidth,6:0.0000}"
                        + $"   ties {hits,4}"
                        + $"   MDAP {((lLo + lHi) / 2 - offset - plainLeft) * 64,6:+0.0;-0.0}/64");
                }
            }
            finally { RemoveFontMemResourceEx(handle); }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>A bar as a path, for inverting GDI's lamps back into a rectangle.</summary>
        private static List<PathFigure> Rectangle(float left, float width)
        {
            var f = new PathFigure(new Vector2(left, 0f)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(left + width, 0f)));
            f.Segments.Add(new LineSegment(new Vector2(left + width, -10.9375f)));
            f.Segments.Add(new LineSegment(new Vector2(left, -10.9375f)));
            return new List<PathFigure> { f };
        }

        /// <summary>HOW FINELY OUR OWN RASTERIZER CAN TELL TWO GEOMETRIES APART.
        /// <para>The stem probe finds about a hundred rectangles rendering bit-identically, and
        /// neither narrowing the bar nor removing the lamp quantiser changes that. If the reason is
        /// that our rasterizer cannot resolve a sixty-fourth of a pixel, that is not a probe
        /// artefact at all -- it would mean the whole sixteenth-of-a-pixel x grid the hinting works
        /// on is thrown away downstream, and every measurement taken through this rasterizer has a
        /// floor nobody has stated.</para>
        /// <para>So ask it directly: slide one rectangle right in 64ths and find the first offset
        /// whose rendering differs from the original at all.</para>
        /// <para>WPF_RESOLUTION=&lt;path&gt; to collect it.</para>
        /// <para>ANSWER, and it closes the probe's open question. Sliding a rectangle right
        /// from x=3.0 in 64ths, the first offset whose rendering differs at all:</para>
        /// <code>
        ///   width 0.375  3/64      width 1.250  6/64      width 2.000  1/64
        ///   width 0.750  6/64      width 1.500  6/64
        /// </code>
        /// <para>Six sixty-fourths for most widths, which is the tie width the stem probe
        /// measures, so the probe's hundred identical rectangles are this and nothing more
        /// mysterious. It is NOT the lamp quantiser -- WPF_SUBPIXEL_QUANT=0 keeps the exact
        /// area and gives the same 3/6/6/6/1 -- and it is not a snap, because it depends on
        /// the width. It is PHASE. A lamp is a third of a pixel, so an edge sitting mid-lamp
        /// moves 1/64 and changes that lamp's coverage by about 4.7 per cent, which the
        /// contrast curve does not carry as far as one output byte. Width 2.0 is the tell: at
        /// x=3.0 both its edges land on lamp boundaries -- 3.0 and 5.0 are both multiples of a
        /// third -- so a 64th immediately opens a new lamp and shows.</para>
        /// <para>The useful consequence is a TOLERANCE. A geometry error under about six
        /// sixty-fourths, a tenth of a pixel, cannot appear in the output at all; our stem
        /// placement errors run 0.1 to 0.2px, which is just above it. So the target is not
        /// GDI's exact coordinate, which no oracle can supply, but being within 6/64 of it --
        /// and on left edges we already are, 52 per cent of the time.</para></summary>
        [Fact]
        public void HowFinelyOurRasterizerResolves()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_RESOLUTION");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_RESOLUTION to collect this");

            var report = new System.Text.StringBuilder();
            report.AppendLine("== the smallest shift our rasterizer renders differently");
            report.AppendLine("   width   first offset that changes ANY byte");
            foreach (float width in new[] { 0.375f, 0.75f, 1.25f, 1.5f, 2.0f })
            {
                byte[] baseline = RenderRect(3.0f, width);
                int first = 0;
                for (int k = 1; k <= 64; k++)
                {
                    byte[] moved = RenderRect(3.0f + k / 64f, width);
                    bool same = baseline.Length == moved.Length;
                    for (int i = 0; same && i < baseline.Length; i++)
                        if (baseline[i] != moved[i]) same = false;
                    if (!same) { first = k; break; }
                }
                report.AppendLine($"   {width,5:0.000}   {(first == 0 ? "none within a pixel" : first + "/64")}");
            }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>One rectangle, rasterized the way the probe rasterizes it.</summary>
        private static byte[] RenderRect(float left, float width)
        {
            PathRasterizer.SubpixelMask m = PathRasterizer.RasterizeSubpixel(
                new PathGeometry(FillRule.NonZero, Rectangle(left + PenX, width)));
            if (m.IsEmpty) return Array.Empty<byte>();
            var outp = new byte[m.Rgba.Length + 2];
            Array.Copy(m.Rgba, outp, m.Rgba.Length);
            outp[^2] = (byte) m.OriginX;
            outp[^1] = (byte) m.Width;
            return outp;
        }

        /// <summary>IS OUR COVERAGE GDI'S ON A DIAGONAL EDGE? Nothing has ever asked.
        /// <para>Every coverage check in this suite uses upright bars, so all of them test vertical
        /// edges and none tests a slanted one. That matters because a third of what is left
        /// disagrees on diagonals -- classified by GDI's own gradient, Tahoma's roman at 16ppem
        /// splits 117,990 vertical, 12,382 horizontal, 67,880 diagonal -- and because a diagonal is
        /// the one place the decision to take a SINGLE vertical sample per row has never been
        /// checked. One sample is provably right for a horizontal edge, since GDI has no vertical
        /// antialiasing; on a slanted edge it is an assumption.</para>
        /// <para>So: the same no-program bars, sheared, rendered by both, lamps compared.</para>
        /// <para>WPF_DIAG_REPORT=&lt;path&gt; to collect it.</para>
        /// <para>ANSWER: our diagonals are FINE, and the hypothesis is dead. Differing lamps,
        /// slant against ppem, with slant 0 as the control:</para>
        /// <code>
        ///   slant     @12   @16   @20
        ///       0      24    33   103
        ///     120       0     0    68
        ///     300       4     8    62
        ///     600      14    18    76
        /// </code>
        /// <para>A slanted edge agrees with GDI as closely as an upright one and at 12 and
        /// 16ppem rather more closely. So one vertical sample per row is right for a diagonal
        /// too, and the diagonal third of the remaining error is not rasterization: those
        /// points are placed by IUP between the stem anchors, so it is inherited from stem
        /// placement like everything else. 20ppem is worse at every slant including none,
        /// which is the symmetric-smoothing front and not this.</para>
        /// <para>The control earned its place. Written with GDI's buffer and ours indexed the
        /// same way -- ours is RGBA and GDI's is BGRA -- it compared GDI's red against our
        /// blue and reported the UPRIGHT bars differing in 336 of 456 lamps at a mean of 124.
        /// A slant-only test would have called that a diagonal problem.</para></summary>
        /// <summary>Is our lamp coverage GDI's where two strokes CROSS?
        /// <para>CoverageOnADiagonal_AgainstGdis settled the lone slanted edge: ours agrees with
        /// GDI's, so one vertical sample per row is right and diagonals per se are not the problem.
        /// It never drew two edges close together, and that is where the residual actually lives.
        /// Verdana 'v' -- two diagonals meeting at a vertex -- is pixel-exact at 16ppem with a blank
        /// difference map; 'x' and 'X' are not, and their row profile says the centres agree to a
        /// tenth of a lamp while every row is LIGHT. So nothing is misplaced and something is
        /// missing, and it is missing next to the crossing.</para>
        /// <para>These bars carry NO glyph program, so both renderers rasterize the same outline
        /// and the interpreter is out of the question. The uncrossed pair is the control: the same
        /// two strokes, same widths, same slants, drawn apart.</para>
        /// <para>WPF_CROSS_REPORT=&lt;path&gt; to collect it.</para></summary>
         /// <summary>GDI's raster beside ours as character maps, for a synthetic probe. Both are
        /// cropped to the union of their ink and printed a green channel at a time, which is what
        /// the lamp tables in this file are read in.</summary>
        private static void DumpTwo(string title, byte[] gdiBgra, byte[] oursRgba)
        {
            int top = int.MaxValue, bot = -1, left = int.MaxValue, right = -1;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int k = (y * Width + x) * 4;
                    if (gdiBgra[k + 1] >= 250 && oursRgba[k + 1] >= 250) continue;
                    if (y < top) top = y;
                    if (y > bot) bot = y;
                    if (x < left) left = x;
                    if (x > right) right = x;
                }
            if (bot < 0) { Console.Error.WriteLine($"   {title}: nothing drawn"); return; }
            Console.Error.WriteLine($"   {title}   rows {top}..{bot} cols {left}..{right}"
                                    + "   GDI | ours");
            for (int y = top; y <= bot; y++)
            {
                var line = new System.Text.StringBuilder("   ");
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        int v = 255 - (pass == 0 ? gdiBgra : oursRgba)[(y * Width + x) * 4 + 1];
                        line.Append(v <= 0 ? '.' : (char) ('0' + Math.Min(9, (v * 9 + 127) / 255)));
                    }
                    line.Append("  |  ");
                }
                Console.Error.WriteLine(line.ToString());
            }
        }

       [Fact]
        public void CoverageAtACrossing_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? path = Environment.GetEnvironmentVariable("WPF_CROSS_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_CROSS_REPORT to collect this");

            const string Family = "WpfCrossProbe";
            var report = new System.Text.StringBuilder();
            report.AppendLine("== is our lamp coverage GDI's where two strokes CROSS?");
            report.AppendLine("   taper  slant  ppem     lamps  differing   worst  mean|d|"
                              + "    GDI ink   our ink   ours/GDI");

            bool cross = false;
            foreach (int taper in new[] { 0, 96, 192, 280 })
            foreach (int slant in new[] { 0, 300, 900 })
            {
                var bars = new List<SyntheticFont.Bar>();
                for (int units = 96; units <= 288; units += 48)
                    bars.Add(new SyntheticFont.Bar(units, 400, 400 + units, false, false,
                                                   noProgram: true, slant: slant, cross: cross,
                                                   taper: Math.Min(taper, units - 16)));
                // WPF_CROSS_GASP=nosym: ship a 'gasp' that asks for gridfit and grey but NOT
                // symmetric smoothing, so GDI renders these bars the way it renders a real face
                // that declines it. Without a table GDI's fallback turns smoothing ON (see the
                // note in SyntheticFont.GaspRanges), and the tie question this probe is being
                // asked -- whether a lamp sample exactly on a span's right edge is inside it --
                // gets a different answer on a real glyph than on these bars. The only thing that
                // differs between the two cases is the smoothing mode, so the probe has to be able
                // to turn it off before the difference can be blamed on anything else.
                SyntheticFont.GaspRanges =
                    Environment.GetEnvironmentVariable("WPF_CROSS_GASP") == "nosym"
                        ? new[] { (0xFFFF, 0x0003) } : null;
                byte[] fontBytes = SyntheticFont.Build(Family + "T" + taper + "S" + slant
                                                       + (SyntheticFont.GaspRanges is null ? "" : "G"),
                                                       bars);
                int count = 0;
                IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
                if (handle == IntPtr.Zero || count == 0)
                {
                    report.AppendLine($"   {taper,5}  {slant,5}   GDI refused the font");
                    continue;
                }
                try
                {
                    var font = new TrueTypeFont(fontBytes);
                    var raw = new byte[Width * Height * 4];
                    foreach (int ppem in new[] { 12, 16, 20 })
                    {
                        long compared = 0, differing = 0, worst = 0, sum = 0, gInk = 0, oInk = 0;
                        for (int i = 0; i < bars.Count; i++)
                        {
                            string ch = ((char) (0x41 + i)).ToString();
                            int baseline = ppem + 12;
                            Gdi.s_rawRgb = raw;
                            Gdi.Draw(ch, Family + "T" + taper + "S" + slant
                                         + (SyntheticFont.GaspRanges is null ? "" : "G"),
                                     ppem, PenX, baseline,
                                     Width, Height, false, false);
                            Gdi.s_rawRgb = null;
                            byte[] ours = OursRgba(font, ch, ppem, baseline, correction: true);
                            // WPF_CROSS_DUMP=taper,slant,ppem: the two rasters side by side for one
                            // case. The table says the crossing disagrees and by how much; it
                            // cannot say WHERE, and where is the whole question -- ink spread over
                            // the wedge between two converging strokes is a different fault from
                            // ink at their outer edges.
                            if (Environment.GetEnvironmentVariable("WPF_CROSS_DUMP")
                                    is { Length: > 0 } cdump
                                && (cdump == $"{taper},{slant},{ppem}"
                                    || cdump == $"{taper},{slant},{ppem},{i}"))
                                DumpTwo($"taper={taper} slant={slant} @{ppem} bar {i}"
                                        + $" (width {96 + 48 * i}u)", raw, ours);
                            // Rows only one side inks are the vertical-extent question, not this
                            // one, and they carry a full-ink difference each -- see the note on
                            // CoverageOnADiagonal_AgainstGdis. Ours is RGBA, GDI's is BGRA.
                            for (int y = 0; y < Height; y++)
                            {
                                bool gRow = false, oRow = false;
                                for (int x = 0; x < Width && !(gRow && oRow); x++)
                                    for (int c = 0; c < 3; c++)
                                    {
                                        if (raw[(y * Width + x) * 4 + (2 - c)] != 255) gRow = true;
                                        if (ours[(y * Width + x) * 4 + c] != 255) oRow = true;
                                    }
                                if (!gRow || !oRow) continue;
                                for (int x = 0; x < Width; x++)
                                for (int c = 0; c < 3; c++)
                                {
                                    int k = y * Width + x;
                                    int g = 255 - raw[k * 4 + (2 - c)];
                                    int o = 255 - ours[k * 4 + c];
                                    if (g == 0 && o == 0) continue;
                                    compared++; gInk += g; oInk += o;
                                    int d = Math.Abs(g - o);
                                    if (d == 0) continue;
                                    differing++; sum += d;
                                    if (d > worst) worst = d;
                                }
                            }
                        }
                        report.AppendLine($"   {taper,5}  {slant,5}  {ppem,4}  {compared,8}"
                            + $"  {differing,9}  {worst,6}"
                            + $"  {(differing == 0 ? 0 : sum / (double) differing),7:0.0}"
                            + $"  {gInk,9}  {oInk,9}"
                            + $"     {(gInk == 0 ? 0 : oInk / (double) gInk),6:0.0000}");
                    }
                }
                finally { RemoveFontMemResourceEx(handle); }
            }
            File.AppendAllText(path!, report.ToString());
            Console.Error.Write(report.ToString());
        }

        [Fact]
        public void CoverageOnADiagonal_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? path = Environment.GetEnvironmentVariable("WPF_DIAG_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_DIAG_REPORT to collect this");

            const string Family = "WpfDiagProbe";
            var report = new System.Text.StringBuilder();
            report.AppendLine("== is our lamp coverage GDI's on a SLANTED edge?");
            report.AppendLine("   slant   ppem   lamps      differing    worst   mean|d| on those");

            // 0 is the control and must reproduce the upright result: near enough zero.
            foreach (int slant in new[] { 0, 120, 300, 600 })
            {
                var bars = new List<SyntheticFont.Bar>();
                for (int units = 96; units <= 288; units += 24)
                    bars.Add(new SyntheticFont.Bar(units, 400, 400 + units, false, false,
                                                   noProgram: true, slant: slant));
                byte[] fontBytes = SyntheticFont.Build(Family + slant, bars);
                int count = 0;
                IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
                if (handle == IntPtr.Zero || count == 0)
                {
                    report.AppendLine($"   {slant,5}   GDI refused the font");
                    continue;
                }
                try
                {
                    var font = new TrueTypeFont(fontBytes);
                    var raw = new byte[Width * Height * 4];
                    foreach (int ppem in new[] { 12, 16, 20 })
                    {
                        long compared = 0, differing = 0, worst = 0, sum = 0;
                        for (int i = 0; i < bars.Count; i++)
                        {
                            string ch = ((char) (0x41 + i)).ToString();
                            int baseline = ppem + 12;
                            Gdi.s_rawRgb = raw;
                            Gdi.Draw(ch, Family + slant, ppem, PenX, baseline, Width, Height,
                                     false, false);
                            Gdi.s_rawRgb = null;
                            byte[] ours = OursRgba(font, ch, ppem, baseline, correction: true);
                            // ROWS ONLY ONE SIDE INKS ARE A DIFFERENT QUESTION -- the vertical
                            // extent, which is SubpixelRows and already understood -- and they
                            // carry a full-ink difference each, so including them swamps the
                            // horizontal coverage this is asking about. The control proves it:
                            // with them in, an UPRIGHT bar reported 350 of 470 lamps differing
                            // at a mean of 121, against the near-exact answer the same bars give
                            // in CoverageFloor.
                            for (int y = 0; y < Height; y++)
                            {
                                bool gRow = false, oRow = false;
                                for (int x = 0; x < Width && !(gRow && oRow); x++)
                                    for (int c = 0; c < 3; c++)
                                    {
                                        if (raw[(y * Width + x) * 4 + (2 - c)] != 255) gRow = true;
                                        if (ours[(y * Width + x) * 4 + c] != 255) oRow = true;
                                    }
                                if (!gRow || !oRow) continue;
                                for (int x = 0; x < Width; x++)
                                for (int c = 0; c < 3; c++)
                                {
                                    int k = y * Width + x;
                                    int g = 255 - raw[k * 4 + (2 - c)];
                                    // OURS IS RGBA AND GDI'S BUFFER IS BGRA. Reading both with
                                    // the same index compares GDI's red against our blue, which
                                    // is what made the upright control report 336 of 456 lamps
                                    // differing at a mean of 124 when the same bars are near
                                    // exact. The control is there to catch exactly this.
                                    int o = 255 - ours[k * 4 + c];
                                    if (g == 0 && o == 0) continue;
                                    compared++;
                                    int d = Math.Abs(g - o);
                                    if (d == 0) continue;
                                    differing++; sum += d;
                                    if (d > worst) worst = d;
                                }
                            }
                        }
                        report.AppendLine($"   {slant,5}   {ppem,4}   {compared,8}   {differing,8}"
                            + $"   {worst,6}   {(differing == 0 ? 0 : sum / (double) differing),8:0.0}");
                    }
                }
                finally { RemoveFontMemResourceEx(handle); }
            }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>WHAT THE PRODUCT'S OWN PARSER MAKES OF EACH FACE'S 'gasp'.
        /// <para>Asked because a scratchpad probe reported four common faces as having no gasp at
        /// all when they demonstrably do, and the difference matters: a face we think has no gasp
        /// gets no grid fitting and no symmetric smoothing from us at any size. The question is not
        /// what a script thinks, it is what THIS parser does, so ask it directly.</para>
        /// <para>WPF_GASP_REPORT=&lt;path&gt; to collect it.</para></summary>
        [Fact]
        public void EveryFacesGasp_AsWeReadIt()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the installed faces");
            string? path = Environment.GetEnvironmentVariable("WPF_GASP_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_GASP_REPORT to collect this");

            // EVERY family the machine has, when WPF_GASP_ALL is set. The named list is the
            // regression view; the full sweep is the hunt, because the renderer answers a
            // family it cannot open by drawing in the FALLBACK face and saying nothing, so
            // the only way to find those is to ask about all of them.
            string[] families = Environment.GetEnvironmentVariable("WPF_GASP_ALL") == "1"
                ? System.Linq.Enumerable.ToArray(FontFiles.ScannedFamilyNames())
                : new[]
            {
                "Segoe UI", "Arial", "Times New Roman", "Verdana", "Tahoma", "Consolas",
                "Calibri", "Georgia", "Courier New", "Trebuchet MS", "Segoe UI Semibold",
                "Microsoft Sans Serif", "Cambria", "Candara", "Corbel", "Constantia",
            };
            var report = new System.Text.StringBuilder();
            report.AppendLine("== gasp as the product reads it: gridfit / symmetric, by size");
            report.AppendLine("   family                    style   8   12   16   18   20   24");
            var scanned = FontFiles.ScannedFamilyNames();
            report.AppendLine($"   (the name-table scan found {scanned.Count} families"
                + $"; Trebuchet present: {System.Linq.Enumerable.Contains(scanned, "Trebuchet MS")})");
            foreach (string fam in families)
                foreach ((string label, bool bold, bool italic) in
                         new[] { ("R", false, false), ("B", true, false), ("I", false, true) })
                {
                    string? file = FontFiles.Find(fam, bold, italic);
                    if (file is null)
                    {
                        // Say so. A family that does not resolve renders in the FALLBACK face,
                        // which is the quietest way for text to be wrong, so it must not be a
                        // silent "continue" here of all places.
                        report.AppendLine($"   {fam,-24} {label,-5}  NOT FOUND");
                        continue;
                    }
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file); } catch (IOException) { continue; }
                    // A .ttc holds several faces and needs an offset; opening it at zero
                    // throws for a missing 'head'. Report which files that is rather than
                    // failing the run, because 'we cannot read this face at all' is itself
                    // the answer to the question being asked.
                    TrueTypeFont font;
                    try { font = new TrueTypeFont(bytes, false, false, FontFiles.SfntOffset(bytes)); }
                    catch (Exception ex)
                    {
                        report.AppendLine($"   {fam,-24} {label,-5}  UNREADABLE: "
                            + $"{System.IO.Path.GetFileName(file)} -- {ex.Message}");
                        continue;
                    }
                    var row = new System.Text.StringBuilder();
                    foreach (int ppem in new[] { 8, 12, 16, 18, 20, 24 })
                    {
                        bool grid = ((IHintedGlyphFont) font).WantsGridFit(ppem);
                        bool sym = ((IHintedGlyphFont) font).WantsSymmetricSmoothing(ppem);
                        row.Append($"  {(grid ? "G" : "-")}{(sym ? "S" : "-")} ");
                    }
                    report.AppendLine($"   {fam,-24} {label,-5}{row}");
                }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>EVERY INSTALLED FAMILY OPENS. A guard, not a report.
        /// <para>The renderer answers a family it cannot open by drawing in the FALLBACK typeface
        /// and saying nothing -- one <c>catch (Exception) { return null; }</c> in LoadFamily hides
        /// every cause -- so a font-resolution bug is invisible until someone looks at a page and
        /// wonders why it is in the wrong face. Four such causes were found in one session:</para>
        /// <code>
        ///   a collection has no sfnt at offset 0        Cambria
        ///   the filename guess misses                   Trebuchet MS
        ///   PostScript outlines need the other reader   any OpenType/CFF family
        ///   the family has no regular face              Brush Script MT and six others
        /// </code>
        /// <para>Each was silent, and each would have been loud here. So this asserts rather than
        /// reports: every family the name scan finds must resolve to a file and that file must
        /// open, in every style.</para>
        /// <para>It is machine-dependent by nature -- it asks about the fonts that are installed --
        /// so it names what failed rather than just counting, and a face that GDI itself would
        /// refuse is a legitimate reason to add an exclusion here with the reason written down.
        /// </para></summary>
        [Fact]
        public void EveryInstalledFamily_ResolvesAndOpens()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads the installed faces");

            var failures = new List<string>();
            int checkedFaces = 0;
            foreach (string fam in FontFiles.ScannedFamilyNames())
                foreach ((string label, bool bold, bool italic) in
                         new[] { ("regular", false, false), ("bold", true, false), ("italic", false, true) })
                {
                    string? file = FontFiles.Find(fam, bold, italic);
                    if (file is null) { failures.Add($"{fam} {label}: no file"); continue; }
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file); }
                    catch (IOException e) { failures.Add($"{fam} {label}: unreadable -- {e.Message}"); continue; }
                    catch (UnauthorizedAccessException) { continue; }   // a font we are not allowed to read
                    int sfnt = FontFiles.SfntOffset(bytes);
                    try
                    {
                        // Exactly what the renderer does, so that passing here means it works there.
                        if (CffFont.IsCff(bytes, sfnt)) _ = new CffFont(bytes, false, false, sfnt);
                        else _ = new TrueTypeFont(bytes, false, false, sfnt);
                        checkedFaces++;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{fam} {label}: {System.IO.Path.GetFileName(file)}"
                                     + $" would fall back -- {e.Message}");
                    }
                }

            Assert.True(checkedFaces > 0, "the name scan found no families at all");
            Assert.True(failures.Count == 0,
                        $"{failures.Count} of {checkedFaces + failures.Count} faces would silently draw"
                        + " in the fallback typeface:" + Environment.NewLine
                        + string.Join(Environment.NewLine, failures));
        }

        /// <summary>SOLVE FOR GDI'S STEM: the left edge and width that reproduce its pixels.
        /// <para>There has never been an oracle for GDI's ClearType-mode outline. GetGlyphOutline
        /// answers for its own mode and the two disagree, so every rule for x fitting has had to be
        /// guessed at and scored on aggregates. But our rasterizer reproduces GDI's lamps EXACTLY
        /// for a given outline, which makes the outline recoverable: for a glyph that is a single
        /// rectangle, render every plausible (left, width) and see which one GDI drew.</para>
        /// <para>'I' in most faces is exactly that rectangle, so it is where this can be done
        /// without assuming anything. The y extents are taken from our own fitted outline: y is not
        /// in question -- the bi-level comparison pairs every point in both axes -- and holding
        /// them fixed keeps the search two-dimensional.</para>
        /// <para>WPF_STEMSOLVE=family/char/ppem[/B|I|BI].</para></summary>
        [Fact]
        public void SolveGdisStemGeometry()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_STEMSOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_STEMSOLVE=family/char/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            char c = parts[1][0];
            int gid = font.GlyphIndex(c);
            Assert.True(gid > 0, $"{parts[0]} has no '{c}'");
            Assert.True(((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out List<PathFigure> fitted)
                        && fitted.Count > 0, "we do not grid-fit it at this size");

            float left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
            foreach (PathFigure f in fitted)
            {
                void See(Vector2 p)
                {
                    if (p.X < left) left = p.X;
                    if (p.X > right) right = p.X;
                    if (p.Y < bottom) bottom = p.Y;
                    if (p.Y > top) top = p.Y;
                }
                See(f.Start);
                foreach (PathSegment sg in f.Segments)
                    if (sg is LineSegment ls) See(ls.Point);
                    else if (sg is QuadraticBezierSegment qs) { See(qs.Control); See(qs.Point); }
            }

            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;

            long Score(float l, float w)
            {
                // The fitted outline is ALREADY in the renderer's space -- ScaleFigures only
                // translates it -- so the rectangle is placed the same way, not flipped. Flipping
                // it put the glyph above the baseline and scored 22,860 where our own outline
                // scores 3,060, which is how the mistake announced itself.
                var rect = new List<PathFigure>(1) { RectFigure(PenX + l, 28f + bottom, w, top - bottom) };
                byte[] ours = OursRgbaFromFigures(rect, font);
                long sum = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch = 0; ch < 3; ch++)
                        sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                return sum;
            }

            // Coarse then fine, because a full sixty-fourth sweep of both axes is 16,000 renders.
            float bestL = left, bestW = right - left;
            long best = long.MaxValue;
            for (int li = -16; li <= 16; li++)
                for (int wi = -16; wi <= 24; wi++)
                {
                    float l = left + li / 16f, w = (right - left) + wi / 16f;
                    if (w <= 0.1f) continue;
                    long sc = Score(l, w);
                    if (sc < best) { best = sc; bestL = l; bestW = w; }
                }
            for (int li = -8; li <= 8; li++)
                for (int wi = -8; wi <= 8; wi++)
                {
                    float l = bestL + li / 64f, w = bestW + wi / 64f;
                    if (w <= 0.1f) continue;
                    long sc = Score(l, w);
                    if (sc < best) { best = sc; bestL = l; bestW = w; }
                }

            // THE SOLUTION IS AN INTERVAL, not a point. Several (left, width) pairs can rasterize
            // to the same lamps, so reading a rule off whichever one the search happened to reach
            // would be reading noise. Scan the neighbourhood for every pair that also scores zero
            // and report the extremes; a rule has to fit inside these, and a narrow interval is
            // itself the evidence that the answer is well determined.
            float loL = bestL, hiL = bestL, loW = bestW, hiW = bestW;
            float loR = bestL + bestW, hiR = bestL + bestW;
            if (best == 0)
                for (int li = -24; li <= 24; li++)
                    for (int wi = -24; wi <= 24; wi++)
                    {
                        float l = bestL + li / 64f, w = bestW + wi / 64f;
                        if (w <= 0.1f || Score(l, w) != 0) continue;
                        if (l < loL) loL = l; if (l > hiL) hiL = l;
                        if (w < loW) loW = w; if (w > hiW) hiW = w;
                        // AND THE RIGHT EDGE. For a stem about a pixel wide, left and width trade
                        // off against each other -- move the left edge right and widen to match and
                        // the lamps can come out the same. If that is what is happening the
                        // solution set is a diagonal, the individual numbers mean little, and only
                        // the combination is determined. This says which.
                        float r = l + w;
                        if (r < loR) loR = r; if (r > hiR) hiR = r;
                    }

            // The natural width too: the question a rule has to answer is what GDI does to it.
            float natL = float.MaxValue, natR = float.MinValue;
            if (font.TryGetGlyphOutline(gid, out List<PathFigure> plain))
                foreach (PathFigure f in plain)
                {
                    void SeeX(Vector2 p) { if (p.X < natL) natL = p.X; if (p.X > natR) natR = p.X; }
                    SeeX(f.Start);
                    foreach (PathSegment sg in f.Segments)
                        if (sg is LineSegment l2) SeeX(l2.Point);
                        else if (sg is QuadraticBezierSegment q2) { SeeX(q2.Control); SeeX(q2.Point); }
                }
            float natScale = ppem / (float) font.PixelsPerEm;
            // And the BI-LEVEL fit and GDI's own advance, because the ClearType outline turned out
            // to be placed relative to those, not to the natural outline or to our ClearType fit.
            // BiLevelPass must be set BEFORE the face is constructed: the face reads it once.
            float biL = float.NaN, biW = float.NaN;
            bool savedBi = TrueTypeInterpreter.BiLevelPass, savedSub = TrueTypeFont.SubpixelFitting;
            TrueTypeInterpreter.BiLevelPass = true;
            TrueTypeFont.SubpixelFitting = false;
            try
            {
                var biFont = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
                if (((IHintedGlyphFont) biFont).TryGetHintedOutline(gid, ppem, out List<PathFigure> bi) && bi.Count > 0)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (PathFigure f in bi)
                    {
                        void SeeX(Vector2 p) { if (p.X < lo) lo = p.X; if (p.X > hi) hi = p.X; }
                        SeeX(f.Start);
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment l3) SeeX(l3.Point);
                            else if (sg is QuadraticBezierSegment q3) { SeeX(q3.Control); SeeX(q3.Point); }
                    }
                    biL = lo; biW = hi - lo;
                }
            }
            finally { TrueTypeInterpreter.BiLevelPass = savedBi; TrueTypeFont.SubpixelFitting = savedSub; }
            int gdiAdvance = Gdi.TextWidth(c.ToString(), parts[0], ppem, bold, italic);
            Console.Error.WriteLine($"{ppem,4}  bilevel {biL,7:0.000} {biW,7:0.000}  GDI advance {gdiAdvance}"
                                    + $"  linear {font.Advance(gid) * natScale:0.000}");
            Console.Error.WriteLine($"{ppem,4}  natural {natL * natScale,7:0.000} {(natR - natL) * natScale,7:0.000}"
                                    + $"  ours {left,7:0.000} {right - left,7:0.000}"
                                    + $"  GDI {bestL,7:0.000} {bestW,7:0.000}"
                                    + $"  resid {best,7}{(best == 0 ? " SOLVED" : "")}"
                                    + $"   L in [{loL,6:0.000},{hiL,6:0.000}]"
                                    + $" W in [{loW,6:0.000},{hiW,6:0.000}]"
                                    + $" R in [{loR,6:0.000},{hiR,6:0.000}]");
            // THE CONVENTION CHECK. Everything here rests on placing an outline the way the real
            // renderer places a glyph; if it does not, every "GDI left" is off by a constant and
            // the absolute numbers are fiction. Render the same glyph through the ordinary path and
            // through the figure path and compare the two scores: they must agree.
            byte[] endToEnd = OursRgba(font, c.ToString(), ppem, 28, correction: true);
            long e2e = 0;
            for (int i = 0; i < Width * Height; i++)
                for (int ch = 0; ch < 3; ch++)
                    e2e += Math.Abs(raw[i * 4 + (2 - ch)] - endToEnd[i * 4 + ch]);
            long viaFigures = Score(left, right - left);
            Console.Error.WriteLine($"   convention  end-to-end {e2e}  via figures {viaFigures}"
                                    + (e2e == viaFigures ? "  AGREE" : "  DISAGREE -- absolute values are unsafe"));
            Console.Error.WriteLine($"== {parts[0]} '{c}' @{ppem}{(style == "" ? "" : "/" + style)}");
            Console.Error.WriteLine($"   ours      left {left,7:0.000}  width {right - left,7:0.000}"
                                    + $"   sum|d| {Score(left, right - left),8}");
            Console.Error.WriteLine($"   GDI's     left {bestL,7:0.000}  width {bestW,7:0.000}"
                                    + $"   sum|d| {best,8}"
                                    + (best == 0 ? "   SOLVED EXACTLY" : ""));
            Console.Error.WriteLine($"   in 64ths  left {bestL * 64,7:0}      width {bestW * 64,7:0}");
        }

        /// <summary>THE LAMPS OF A RUN, one row, straight from GDI: which subpixels each stem of
        /// "IIII" occupies, so the advance GDI actually LAYS OUT with can be read off the ink
        /// instead of trusted from GetTextExtentPoint32. WPF_RUNPROBE=family/text/ppem[/style].</summary>
        [Fact]
        public void LampsOfAGdiRun()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_RUNPROBE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_RUNPROBE=family/text/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(parts[1], parts[0], ppem, PenX, 28, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;
            string? file = FontFiles.Find(parts[0], bold, italic);
            if (file is not null)
            {
                byte[] bytes = File.ReadAllBytes(file);
                int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
                FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
                var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
                float natScale = ppem / (float) font.PixelsPerEm;
                foreach (char c in System.Linq.Enumerable.Distinct(parts[1]))
                {
                    int gid = font.GlyphIndex(c);
                    Console.Error.WriteLine($"adv {ppem} '{c}' linear {font.Advance(gid) * natScale:0.000}"
                                            + $" GDI {Gdi.TextWidth(c.ToString(), parts[0], ppem, bold, italic)}"
                                            // The three advances GDI can be asked for: the string
                                            // extent, the layout width, and the bi-level cell.
                                            + $" charW {Gdi.LayoutAdvance(c, parts[0], ppem, bold, italic)}"
                                            + $" cellInc {Gdi.HintedMetrics(c, parts[0], ppem, bold, italic).CellIncX}");
                }
            }
            int y = 28 - ppem / 3;
            var sb = new System.Text.StringBuilder();
            sb.Append($"row {y}, pen {PenX}: ");
            for (int x = 0; x < Math.Min(Width, PenX + ppem * parts[1].Length + 4); x++)
            {
                int i = (y * Width + x) * 4;
                sb.Append($"{x}:{255 - raw[i + 2]:000},{255 - raw[i + 1]:000},{255 - raw[i]:000} ");
            }
            Console.Error.WriteLine(sb.ToString());
        }

        /// <summary>A closed rectangle as a path figure, y up from the baseline.</summary>
        private static PathFigure RectFigure(float x, float yTop, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, yTop)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, yTop)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, yTop + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, yTop + h)));
            return f;
        }

        /// <summary>A STEM ONE WHOLE PIXEL WIDER MUST NOT BE HANDED THE NARROW STEM'S MASK.
        /// <para>The mask cache hashed a geometry as h * 31 + (xBits &lt;&lt; 32 ^ yBits) per point.
        /// With x in the top half of the word, two consecutive points whose x moved by a float-bit
        /// difference of 2^22 -- one pixel for x in [2,4), two in [4,8) -- multiplied out to a
        /// multiple of 2^64 once the flag multipliers were applied, and the wider shape got the
        /// narrower shape's cached texture. Found by the edge solver: the right edge of an 'I'
        /// scored identically at +0 and +64 sixty-fourths. One renderer, two rectangles, and the
        /// second must not come back as the first.</para></summary>
        [Fact]
        public void AWiderRectangleIsNotTheCachedNarrowOne()
        {
            string? file = FontFiles.Find("Arial", false, false);
            Assert.SkipWhen(file is null, "this machine has no Arial");
            byte[] bytes = File.ReadAllBytes(file!);
            var font = new TrueTypeFont(bytes, false, false, FontFiles.SfntOffset(bytes, "Arial", false, false));
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            byte[] One(int left64, int right64)
            {
                var root = new SceneVisual();
                var rect = new List<PathFigure>(1) { RectFigure(PenX + left64 / 64f, 28f - 11f, (right64 - left64) / 64f, 11f) };
                root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, rect),
                                                  new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),
                                                  isGlyph: true) { PixelAligned = true });
                return renderer.RenderToRgba(root, Width, Height, RgbaColor.FromBytes(255, 255, 255, 255));
            }
            byte[] narrow = One(43, 138), wide = One(43, 202), narrowAgain = One(43, 138);
            long widerDiff = 0, sameDiff = 0;
            for (int i = 0; i < narrow.Length; i++)
            {
                widerDiff += Math.Abs(narrow[i] - wide[i]);
                sameDiff += Math.Abs(narrow[i] - narrowAgain[i]);
            }
            Assert.Equal(0, sameDiff);
            Assert.True(widerDiff > 1000, $"a rectangle one pixel wider drew the same pixels (|d| = {widerDiff})");
        }

        /// <summary>SOLVE FOR GDI'S EDGES: the x of every vertical edge that reproduces its pixels.
        /// <para>The stem solver recovers GDI's outline for a glyph that is one rectangle, and the
        /// shift solver asks whether a single translation of ours explains the rest. Between them
        /// lies every straight-sided glyph -- H, N, E, T, L, F have their x in a handful of
        /// vertical edges -- and each edge can be solved for on its own. Our fitted points are
        /// grouped by their x (to a sixty-fourth), every group is an edge, and each is slid in
        /// sixty-fourths while the others hold still, keeping the position that leaves GDI's
        /// pixels least disagreed with; repeated until nothing moves. The rasterizer being exact,
        /// a glyph GDI fitted with the same topology reaches zero, and the per-edge answer -- our
        /// x, GDI's x, the difference -- is what every x rule so far has had to be guessed at
        /// without.</para>
        /// <para>An exact match is a run of positions about a sixth of a pixel wide, so the run is
        /// printed and its middle is the reported answer, not whichever end the sweep met first.
        /// The bi-level fitting's edges are printed beside, since that outline keeps being the
        /// suspect.</para>
        /// <summary>GDI'S CLEARTYPE OUTLINE IN BOTH AXES, recovered by inverting our own
        /// rasterizer. WPF_XYSOLVE=family/chars/ppem[/B|I].
        /// <para>DO NOT USE IT AT 8PPEM. This solver renders our glyph as a plain GeometryFill
        /// built from TryGetHintedOutline; the weight report -- which IS the holdout, and therefore
        /// the goal -- renders it through GlyphRunDraw, the shipped glyph-run path. At 10, 12 and
        /// 16ppem the two agree exactly, glyph for glyph. At 8 they do not, and in BOTH directions:
        /// Times 'p' scores 477 through the run path and 0 here, 'b' 228 against 0, while Verdana
        /// 'o' scores 0 through the run path and 36 here and Arial 'o' 0 against 86. Both harnesses
        /// ask GDI for the same bitmap at the same pen, so it is our two paths that differ, not the
        /// reference.</para>
        /// <para>8ppem is also exactly where these faces' gasp declines grid-fitting (all six clear
        /// both bits at &lt;= 8), which is the one size at which TryGetHintedOutline returns the
        /// plain outline scaled rather than a fitted one. Whatever the difference is, it lives
        /// downstream of the outline and only in that regime -- so a conclusion drawn here about
        /// 8ppem is a conclusion about a path the holdout does not measure.</para>
        /// <para>HOW FAR TO TRUST A SINGLE COORDINATE. Residual zero means this outline reproduces
        /// GDI's pixels exactly; it does NOT mean it is the only outline that does, and the two
        /// have been confused here before. Forty-odd coordinates against a ten-pixel-square image
        /// is an underdetermined system, and the search is coordinate descent, so what comes back
        /// is one point of a solution SET reached from our fit. Started from a genuinely different
        /// outline -- Times '9'@14 with WPF_CT_SCFS_X=0, which moves its bowl -- the same glyph
        /// solves to P8 149 where the default start says 131, and P13 133 where it says 150. That
        /// run stops at residual 118 rather than 0, so it is not a rival solution and does not
        /// refute the converged one; but it does show the descent is start-dependent, and no
        /// second start has yet been found that also reaches zero.</para>
        /// <para>So: the SHAPE of the answer -- which points must move, in which direction, by
        /// roughly how much -- is evidence. An individual coordinate is a hypothesis. The slack
        /// column (WPF_XYSOLVE_INTERVAL=1) is a one-dimensional slice through the solution at the
        /// point the descent landed on, not a joint region, so a point sitting outside its
        /// neighbour's interval is not proof of anything on its own.</para>
        /// <para>This is the instrument the Y side has never had. SolveGdisEdges moves each EDGE
        /// in x only, so it cannot see a point GDI placed at a different HEIGHT -- and on a curve
        /// it slides x to fake the ink a wrong y produced, which is how it reported a bowl's
        /// control point 39/64 to the right when the real difference was the shape. The shear
        /// solver has two unknowns and cannot bend anything. This one walks every emitted point
        /// and tries it at a range of offsets in x and then in y, keeping whatever lowers GDI's
        /// own score, for a few passes.</para>
        /// <para>Read the RESIDUAL first. Zero means the returned coordinates ARE GDI's, to the
        /// resolution the rasterizer can distinguish, and the per-point table is then a direct
        /// measurement to hint against. A residual that stalls well above zero means our
        /// rasterizer cannot produce GDI's pixels from ANY outline, which moves the problem out
        /// of the interpreter entirely -- so either answer is worth having.</para>
        /// <para>IT CONVERGES, AND THE EARLIER VERDICT THAT IT DOES NOT WAS A SPAN ARTEFACT. This
        /// was written off as "963 from 8,396, saturating bounds" with the span at 24, which is
        /// simply too narrow: the points that carry a diagonal glyph's error want half to
        /// three-quarters of a pixel, so they pinned themselves to the boundary and the descent
        /// stopped. At 36 the same glyphs solve essentially exactly -- Arial 'K'@20 3,397 -> 154,
        /// Times 'A'@24 1,978 -> 100, Times 'W'@24 2,504 -> 118, Arial 'z'@20 3,353 -> 373 -- so
        /// the default is 36 now. A saturated delta (one equal to the span) always means widen it
        /// and run again; never read one as an answer.</para>
        /// <para>WHICH MAKES THIS THE ORACLE FOR GDI'S CLEARTYPE **Y**, the instrument whose
        /// absence has been the named blocker for the serif and diagonal rows. SolveGdisEdges
        /// moves x only and therefore lies in a knowable way: on Arial 'K'@20 it stalls at 1,546
        /// and reports GDI pulling apart four points our fit leaves collinear on the stem's right
        /// edge, which is nonsense -- with both axes the same glyph solves to 154 and the answer is
        /// that two points are wrong in Y by 30/64 and 20/64 and x is right to within 8/64. Prefer
        /// this whenever an x-only solve stalls.</para>
        /// <para>WPF_XYSOLVE_PASSES (default 4), WPF_XYSOLVE_SPAN (default 36, the half-width of
        /// each point's search in 64ths). Cost is roughly points x 2 x (2*span/step) x passes
        /// renders -- a 64-point 'W' at span 36 is 16,129 of them, about a minute.</para></summary>
        [Fact]
        public void SolveGdisOutlineXy()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_XYSOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_XYSOLVE=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');
            int passes = int.TryParse(Environment.GetEnvironmentVariable("WPF_XYSOLVE_PASSES"), out int pz) ? pz : 4;
            int span = int.TryParse(Environment.GetEnvironmentVariable("WPF_XYSOLVE_SPAN"), out int sp) ? sp : 36;

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            renderer.SubpixelRowsOverride = font.WantsSymmetricSmoothing(ppem) ? 5 : 0;
            renderer.DropoutOverride = font.WantsDropoutControl(ppem, out int scanType) ? scanType + 1 : 0;
            renderer.PpemOverride = ppem;
            var raw = new byte[Width * Height * 4];

            bool savedSubpix = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = true;
            try
            {
                TrueTypeInterpreter.s_capturePoints = true;
                foreach (char c in parts[1])
                {
                    int gid = font.GlyphIndex(c);
                    if (gid <= 0 || !((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem,
                            out List<PathFigure> fitted) || fitted.Count == 0)
                    { Console.Error.WriteLine("== not fitted"); continue; }
                    // THE INTERPRETER'S OWN INDEX FOR EACH EMITTED POINT, because a path index is
                    // not a point index and matching them by coordinate is ambiguous exactly where
                    // it matters. Times New Roman '9' at 14ppem has THREE interpreter points at
                    // cur x = 408 -- P17, P18 and P19, one of them a touched anchor -- so "the
                    // solver moved pt19" can mean the anchor agrees with GDI or that it does not,
                    // and nothing in the output says which. On Arial it never bit: 'X' has
                    // thirteen distinct points and its fitted path is 1:1 with them.
                    // <para>The map is the same reconstruction GdiStageTests uses for GGO: walk
                    // each contour, emit the point, and emit a placeholder wherever two
                    // consecutive points are both off-curve, because the outline builder
                    // materialises the implied on-curve midpoint there. A closing duplicate of the
                    // first point ends each figure. Where the counts do not line up the column is
                    // printed as -1 rather than guessed at.</para>
                    var ipts = font.LastHintedPoints;
                    var pathToPoint = new List<int>();
                    if (ipts is not null && ipts.EndPoints.Length > 0)
                    {
                        int firstPt = 0;
                        foreach (int end in ipts.EndPoints)
                        {
                            if (end < firstPt || end >= ipts.PointCount) { pathToPoint.Clear(); break; }
                            int startOfFigure = pathToPoint.Count;
                            for (int k = firstPt; k <= end; k++)
                            {
                                pathToPoint.Add(k);
                                int nxt = k == end ? firstPt : k + 1;
                                if (!ipts.OnCurve[k] && !ipts.OnCurve[nxt]) pathToPoint.Add(-1);
                            }
                            pathToPoint.Add(firstPt);          // the closing duplicate
                            firstPt = end + 1;
                            if (startOfFigure > pathToPoint.Count) break;
                        }
                    }

                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
                    Gdi.s_rawRgb = null;

                    // The fit, flattened to one array per axis in EMISSION order, which is the
                    // order M() below walks -- so a point's identity is its index here.
                    var pxl = new List<int>();
                    var pyl = new List<int>();
                    void See(Vector2 p)
                    { pxl.Add((int) MathF.Round(p.X * 64f)); pyl.Add((int) MathF.Round(p.Y * 64f)); }
                    foreach (PathFigure f in fitted)
                    {
                        See(f.Start);
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment l) See(l.Point);
                            else if (sg is QuadraticBezierSegment q) { See(q.Control); See(q.Point); }
                            else if (sg is CubicBezierSegment c3)
                            { See(c3.Control1); See(c3.Control2); See(c3.Point); }
                    }
                    int[] sx = pxl.ToArray(), sy = pyl.ToArray();
                    int[] ox = (int[]) sx.Clone(), oy = (int[]) sy.Clone();

                    int idx = 0;
                    long Score()
                    {
                        idx = 0;
                        Vector2 M(Vector2 ignored)
                        { int i = idx++; return new(PenX + sx[i] / 64f, 28f + sy[i] / 64f); }
                        var placed = new List<PathFigure>(fitted.Count);
                        foreach (PathFigure f in fitted)
                        {
                            var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                            foreach (PathSegment sg in f.Segments)
                                nf.Segments.Add(sg switch
                                {
                                    LineSegment l => new LineSegment(M(l.Point)),
                                    QuadraticBezierSegment q =>
                                        new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                                    CubicBezierSegment c3 =>
                                        new CubicBezierSegment(M(c3.Control1), M(c3.Control2), M(c3.Point)),
                                    _ => sg,
                                });
                            placed.Add(nf);
                        }
                        var root = new SceneVisual();
                        root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                            new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)), isGlyph: true)
                            { PixelAligned = true });
                        byte[] ours = renderer.RenderToRgba(root, Width, Height,
                            RgbaColor.FromBytes(255, 255, 255, 255));
                        long sum = 0;
                        for (int i = 0; i < Width * Height; i++)
                            for (int ch = 0; ch < 3; ch++)
                                sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                        return sum;
                    }

                    // WPF_XYSOLVE_ANCHORS=1: SEARCH THE OUTLINES OUR INTERPRETER CAN ACTUALLY
                    // PRODUCE, rather than every arrangement of independent coordinates.
                    // <para>The descent below moves one coordinate at a time and stops at residual
                    // zero, so what it answers is "is there SOME outline that renders GDI's pixels"
                    // -- and the answer is nearly always yes, because forty-odd free coordinates
                    // against a ten-pixel square is wildly underdetermined. The question that
                    // matters is narrower. Our glyph programs place a handful of ANCHORS and IUP
                    // derives every other point from them, so the outlines we can reach form a
                    // manifold of a few dimensions: Times Bold '0' at 16ppem has FOUR x-touched
                    // points and forty-two derived ones. Asking whether any anchor assignment
                    // reproduces GDI splits the remaining question cleanly in two. If one does,
                    // our ClearType placement of the touched points is wrong and the search hands
                    // over GDI's values. If none does, GDI's outline is off our manifold
                    // altogether, and it must be touching points our program never touches.</para>
                    // <para>The coordinate-wise walk-back cannot answer this, and that is the
                    // point: moving an anchor alone drags nothing with it and breaks the render,
                    // so an anchor that ought to move together with its dependents is reported as
                    // a pinned anchor plus a dozen mysteriously displaced points -- which is
                    // exactly the shape the Times bowls have been showing all along.</para>
                    // <para>IUP[x] is reimplemented here over the captured arrays rather than
                    // re-run through the interpreter, and it is CHECKED: with the anchors at our
                    // own values it must reproduce our own fitted x exactly, or the glyph is
                    // skipped and says so. That check is what makes a negative result mean
                    // anything.</para>
                    bool anchorMode = Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS") == "1"
                                      && ipts is not null && pathToPoint.Count == sx.Length;
                    if (anchorMode)
                    {
                        int n = ipts!.PointCount;
                        var org = new int[n];
                        var orus = new int[n];
                        var fit = new int[n];
                        for (int i = 0; i < n; i++)
                        {
                            org[i] = (int) MathF.Round(ipts.StartX[i] * 64f);
                            orus[i] = ipts.OrusX[i];
                            fit[i] = (int) MathF.Round(ipts.FitX[i] * 64f);
                        }
                        // WPF_XYSOLVE_ANCHORS_DROP=p,q,...: pretend the program did NOT touch these
                        // points, so IUP places them instead. The search had only ever been able to
                        // ADD anchors, which assumes GDI's touch set is ours plus extras -- and
                        // "GDI touches a DIFFERENT set" is just as good an explanation of an
                        // unreachable outline, and not expressible without this.
                        var dropped = new HashSet<int>();
                        if (Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS_DROP")
                            is { Length: > 0 } dropSpec)
                            foreach (string tok in dropSpec.Split(','))
                                if (int.TryParse(tok, out int dp)) dropped.Add(dp);
                        var touchedList = new List<int>();
                        for (int i = 0; i < n; i++)
                            if (ipts.TouchedX[i] && !dropped.Contains(i)) touchedList.Add(i);
                        int[] anchorOf = touchedList.ToArray();
                        var extraOf = new List<int>();
                        int wantExtra = int.TryParse(
                            Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS_EXTRA"),
                            out int we) ? we : 0;

                        void CarryRun(int[] c, int from, int to, int r1, int r2)
                        {
                            if (from > to) return;
                            int o1 = orus[r1], o2 = orus[r2];
                            if (o1 >= o2) { (o1, o2) = (o2, o1); (r1, r2) = (r2, r1); }
                            int g1 = org[r1], g2 = org[r2];
                            int d1 = c[r1] - g1, d2 = c[r2] - g2;
                            if (o1 == o2)
                            { for (int i = from; i <= to; i++) c[i] = org[i] + d1; return; }
                            for (int i = from; i <= to; i++)
                            {
                                int x = org[i];
                                if (x >= g2) { c[i] = x + d2; continue; }
                                if (x <= g1) { c[i] = x + d1; continue; }
                                long den = o2 - o1, num = orus[i] - o1;
                                long sp = (long) (g2 + d2) - (g1 + d1);
                                c[i] = (int) ((num * sp + (den >> 1)) / den) + g1 + d1;
                            }
                        }

                        int[] RunIup(int[] anchorVals)
                        {
                            var c = new int[n];
                            for (int i = 0; i < n; i++) c[i] = org[i];
                            for (int k = 0; k < anchorOf.Length; k++) c[anchorOf[k]] = anchorVals[k];
                            for (int k = 0; k < extraOf.Count; k++)
                                c[extraOf[k]] = anchorVals[anchorOf.Length + k];
                            int first = 0;
                            foreach (int end in ipts.EndPoints)
                            {
                                if (end < first || end >= n) break;
                                var t = new List<int>();
                                for (int i = first; i <= end; i++)
                                    if ((ipts.TouchedX[i] && !dropped.Contains(i))
                                        || extraOf.Contains(i)) t.Add(i);
                                if (t.Count == 1)
                                {
                                    int d = c[t[0]] - org[t[0]];
                                    for (int i = first; i <= end; i++)
                                        if (!(ipts.TouchedX[i] && !dropped.Contains(i))
                                            && !extraOf.Contains(i)) c[i] = org[i] + d;
                                }
                                else if (t.Count > 1)
                                {
                                    for (int k = 0; k + 1 < t.Count; k++)
                                        CarryRun(c, t[k] + 1, t[k + 1] - 1, t[k], t[k + 1]);
                                    CarryRun(c, t[t.Count - 1] + 1, end, t[t.Count - 1], t[0]);
                                    CarryRun(c, first, t[0] - 1, t[t.Count - 1], t[0]);
                                }
                                first = end + 1;
                            }
                            return c;
                        }

                        // WPF_XYSOLVE_ANCHORS_EXTRA=n: let the search ADD anchors, chosen from the
                        // points the program left to IUP.
                        // <para>The plain search says Times Bold's bowls are off the manifold our
                        // interpreter can reach -- no placement of the program's own anchors
                        // reproduces GDI, over ten random restarts -- which means GDI is touching
                        // points our program never touches. That is a conclusion with a hole in it:
                        // it names no point. Adding candidate anchors closes the hole. Try each
                        // untouched point in turn as an extra anchor, run the descent in one more
                        // dimension, and keep the best; if a glyph that could not reach zero reaches
                        // it with ONE extra anchor, the search has just named the point GDI touches
                        // and the value it puts there, which is a target rather than a deduction.
                        // </para>
                        // <para>WHAT IT SAYS ON TIMES BOLD '0'@16, AND HOW FAR TO TRUST IT. Four
                        // anchors reach 860; the greedy addition finds P2 and P31 and stalls at
                        // 714; forcing P17 and P19 -- the top of the bowl, where the best reachable
                        // outline misses by 8 while everything else is within 4 -- gives 758, four
                        // forced 656, eight forced 436. A pair matters where a single point does
                        // not (P17 alone is 895, WORSE than not adding it), which is why the greedy
                        // form alone could not have found it.</para>
                        // <para>BUT THE HIGH-DIMENSIONAL NUMBERS ARE NOT BOUNDS, and there is a
                        // clean proof of it rather than a suspicion. An eighteen-anchor manifold
                        // strictly CONTAINS a twelve-anchor one -- set the six extra anchors to
                        // whatever the smaller configuration produced -- so its minimum cannot be
                        // higher. Forcing all fourteen points the free solver moves gives 510
                        // against the eight-point set's 436. That is impossible for a true minimum,
                        // so the descent is failing as the dimension grows, and 436 is a property
                        // of the search. Only the low-dimensional results are evidence: the
                        // program's own four anchors reach 860 under ten random restarts and at
                        // spans of 36, 72 and 128, and one or two added anchors reach 714.</para>
                        // <para>The reference array is not it either: WPF_CT_IUP_REF=scaled, the
                        // other half of itrp_IUP's gs[0x196] branch, is exactly neutral on four of
                        // five of these glyphs and worth 2,034 -> 1,953 on the fifth.</para>
                        var anchors = new int[anchorOf.Length];
                        for (int k = 0; k < anchorOf.Length; k++) anchors[k] = fit[anchorOf[k]];
                        int[] check = RunIup(anchors);
                        int bad = -1;
                        if (dropped.Count == 0)
                            for (int i = 0; i < n && bad < 0; i++) if (check[i] != fit[i]) bad = i;
                        if (bad >= 0)
                        {
                            Console.Error.WriteLine("   ANCHOR MODE OFF: the reimplemented IUP[x]"
                                + $" disagrees with the interpreter at P{bad} ({check[bad]} against"
                                + $" {fit[bad]}), so something other than interpolation places"
                                + " points here and an anchor search would search the wrong"
                                + " manifold. The usual cause is an instruction AFTER IUP -- Times'"
                                + " 'c' at 16ppem SHPIXes pt17 by half a pixel once IUP has run --"
                                + " which leaves the captured touch set different from the one IUP"
                                + " actually saw. Two of fifteen Times glyphs per size are refused"
                                + " this way; the other thirteen are the result.");
                        }
                        else
                        {
                            // WPF_XYSOLVE_ANCHORS_ADD=p,q,...: force specific points into the anchor
                            // set, AFTER the self-check -- adding an anchor changes the
                            // interpolation of its neighbours, so a check run with the extras in
                            // would (correctly) refuse every glyph. The greedy addition below takes
                            // one point at a time, so a PAIR that only works together is invisible
                            // to it, and a pair is exactly what a bowl needs: narrowing its top
                            // without narrowing its sides takes two anchors, one each side.
                            if (Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS_ADD")
                                is { Length: > 0 } forceAnchorSpec)
                            {
                                foreach (string tok in forceAnchorSpec.Split(','))
                                    if (int.TryParse(tok, out int ap) && ap >= 0 && ap < n
                                        && !(ipts.TouchedX[ap] && !dropped.Contains(ap))
                                        && !extraOf.Contains(ap))
                                        extraOf.Add(ap);
                                var grown0 = new int[anchorOf.Length + extraOf.Count];
                                Array.Copy(anchors, grown0, anchorOf.Length);
                                for (int k = 0; k < extraOf.Count; k++)
                                    grown0[anchorOf.Length + k] = fit[extraOf[k]];
                                anchors = grown0;
                            }
                            // WRITE THE DIFFERENCE, NOT THE VALUE, so that the anchors at our own
                            // positions reproduce our own outline EXACTLY. Writing the recomputed
                            // coordinate straight into the path is not the same thing: the implied
                            // midpoints are averages, and recomputing them here with integer
                            // division lands a sixty-fourth away from what the outline builder
                            // produced in floating point. That is enough to move a lamp -- Segoe
                            // UI's 'o' at 12ppem scored 137 as fitted and ZERO once the midpoints
                            // had been through this function, which made our own outline look like
                            // GDI's and would have reported every anchor as already correct.
                            // Applying deltas leaves the baseline untouched by construction.
                            void Write(int[] c)
                            {
                                for (int i = 0; i < sx.Length; i++)
                                {
                                    int p = pathToPoint[i];
                                    if (p >= 0) sx[i] = ox[i] + (c[p] - fit[p]);
                                }
                                for (int i = 0; i < sx.Length; i++)
                                    if (pathToPoint[i] < 0 && i > 0 && i + 1 < sx.Length)
                                        sx[i] = ox[i] + ((sx[i - 1] - ox[i - 1])
                                                         + (sx[i + 1] - ox[i + 1])) / 2;
                            }
                            long best = Score();
                            int arenders = 1;
                            // RESTARTS, because "no anchor placement reaches GDI" is a claim about
                            // a SEARCH, and this one is coordinate descent in four dimensions. A
                            // local minimum would read as a proof that GDI is off our manifold --
                            // and a search reporting its own limits as a property of the world is
                            // the failure mode this file has already hit twice. WPF_XYSOLVE_
                            // ANCHORS_RESTARTS=n re-runs the descent from n random starts and keeps
                            // the best; only if none of them reaches zero is the negative worth
                            // anything.
                            int restarts = int.TryParse(
                                Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS_RESTARTS"),
                                out int rs) ? rs : 0;
                            var rng = new Random(12345);
                            Console.Error.WriteLine($"== {c} {parts[0]}@{ppem}{style}"
                                + $"  anchor mode: {anchorOf.Length} x-touched"
                                + $" points place {n - anchorOf.Length} others; start {best}");
                            // EVERY STEP IS ONE SIXTY-FOURTH, and the search does not stop at the
                            // first value that reaches zero. A coarse first pass would make the
                            // reported deltas multiples of its own step -- the coordinate solver
                            // did exactly that and produced a "law" that was nothing but its step
                            // size -- and a descent that halts on the first exact hit reports the
                            // first value on its path rather than the smallest move. So: unit
                            // steps, and once the residual is zero, walk every anchor back toward
                            // our own value for as long as it stays zero. What comes out is the
                            // SMALLEST anchor movement that reproduces GDI.
                            for (int pass = 0; pass < 3; pass++)
                                for (int k = 0; k < anchors.Length; k++)
                                {
                                    int keep = anchors[k], bestV = keep;
                                    for (int d = -span; d <= span; d++)
                                    {
                                        if (d == 0) continue;
                                        anchors[k] = keep + d;
                                        Write(RunIup(anchors));
                                        arenders++;
                                        long v = Score();
                                        int home0 = k < anchorOf.Length ? fit[anchorOf[k]]
                                                                        : fit[extraOf[k - anchorOf.Length]];
                                        if (v < best || (v == best && Math.Abs(keep + d - home0)
                                                                      < Math.Abs(bestV - home0)))
                                        { best = v; bestV = anchors[k]; }
                                    }
                                    anchors[k] = bestV;
                                    Write(RunIup(anchors));
                                }
                            // ADD ANCHORS, greedily, one at a time.
                            for (int e = 0; e < wantExtra && best > 0; e++)
                            {
                                int bestPt = -1, bestVal = 0;
                                long bestHere = best;
                                var work = new int[anchors.Length + 1];
                                Array.Copy(anchors, work, anchors.Length);
                                for (int cand = 0; cand < n; cand++)
                                {
                                    if ((ipts.TouchedX[cand] && !dropped.Contains(cand))
                                        || extraOf.Contains(cand)) continue;
                                    extraOf.Add(cand);
                                    work[anchors.Length] = fit[cand];
                                    long local = long.MaxValue; int localV = fit[cand];
                                    for (int d = -span; d <= span; d++)
                                    {
                                        work[anchors.Length] = fit[cand] + d;
                                        Write(RunIup(work));
                                        arenders++;
                                        long v = Score();
                                        if (v < local) { local = v; localV = work[anchors.Length]; }
                                    }
                                    extraOf.RemoveAt(extraOf.Count - 1);
                                    if (local < bestHere) { bestHere = local; bestPt = cand; bestVal = localV; }
                                }
                                if (bestPt < 0) break;
                                extraOf.Add(bestPt);
                                var grown = new int[anchors.Length + 1];
                                Array.Copy(anchors, grown, anchors.Length);
                                grown[anchors.Length] = bestVal;
                                anchors = grown;
                                best = bestHere;
                                Console.Error.WriteLine($"   + extra anchor P{bestPt} at {bestVal}"
                                    + $" (ours {fit[bestPt]}, d {bestVal - fit[bestPt]}) -> {best}");
                                // re-descend on everything now that the set has grown
                                for (int pass = 0; pass < 2; pass++)
                                    for (int k = 0; k < anchors.Length; k++)
                                    {
                                        int home = k < anchorOf.Length ? fit[anchorOf[k]]
                                                                       : fit[extraOf[k - anchorOf.Length]];
                                        int keep = anchors[k], bestV = keep;
                                        for (int d = -span; d <= span; d++)
                                        {
                                            anchors[k] = home + d;
                                            Write(RunIup(anchors));
                                            arenders++;
                                            long v = Score();
                                            if (v < best) { best = v; bestV = anchors[k]; }
                                        }
                                        anchors[k] = bestV;
                                    }
                                Write(RunIup(anchors));
                            }
                            for (int r = 0; r < restarts && best > 0; r++)
                            {
                                var trial = new int[anchors.Length];
                                int Home(int k) => k < anchorOf.Length ? fit[anchorOf[k]]
                                                                      : fit[extraOf[k - anchorOf.Length]];
                                for (int k = 0; k < trial.Length; k++)
                                    trial[k] = Home(k) + rng.Next(-span, span + 1);
                                long cur2 = long.MaxValue;
                                for (int pass = 0; pass < 3; pass++)
                                    for (int k = 0; k < trial.Length; k++)
                                    {
                                        int keep = trial[k], bestV = keep;
                                        for (int d = -span; d <= span; d++)
                                        {
                                            trial[k] = Home(k) + d;
                                            Write(RunIup(trial));
                                            arenders++;
                                            long v = Score();
                                            if (v < cur2) { cur2 = v; bestV = trial[k]; }
                                        }
                                        trial[k] = bestV;
                                    }
                                if (cur2 < best)
                                { best = cur2; Array.Copy(trial, anchors, trial.Length); }
                            }
                            Write(RunIup(anchors));
                            if (best == 0)
                                for (int k = 0; k < anchors.Length; k++)
                                {
                                    int home = k < anchorOf.Length ? fit[anchorOf[k]]
                                                                   : fit[extraOf[k - anchorOf.Length]];
                                    while (anchors[k] != home)
                                    {
                                        int keep = anchors[k];
                                        anchors[k] += anchors[k] < home ? 1 : -1;
                                        Write(RunIup(anchors));
                                        arenders++;
                                        if (Score() != 0) { anchors[k] = keep; break; }
                                    }
                                }
                            Write(RunIup(anchors));
                            Write(RunIup(anchors));
                            Console.Error.WriteLine($"   ANCHOR SEARCH: best {best} in {arenders}"
                                + " renders" + (best == 0
                                    ? "   REACHED GDI -- our anchors are wrong and these are GDI's"
                                    : "   CANNOT reach GDI from ANY anchor placement"));
                            // The resulting outline, so a residual the anchors cannot reach can
                            // be compared point for point against what the free solver says GDI
                            // wants -- which says WHERE the unreachable part of the glyph is.
                            if (best > 0 && Environment.GetEnvironmentVariable("WPF_XYSOLVE_ANCHORS_DUMP") == "1")
                            {
                                int[] fin = RunIup(anchors);
                                for (int i = 0; i < n; i++)
                                    if (fin[i] != fit[i])
                                        Console.Error.WriteLine($"     best P{i,-3} ours {fit[i],5}"
                                            + $" -> {fin[i],5}  d {fin[i] - fit[i],4}");
                            }
                            for (int k = 0; k < anchors.Length; k++)
                            {
                                bool ext = k >= anchorOf.Length;
                                int pt = ext ? extraOf[k - anchorOf.Length] : anchorOf[k];
                                if (anchors[k] != fit[pt] || best == 0)
                                    Console.Error.WriteLine($"     {c}@{ppem}{style} anchor"
                                        + $" P{pt,-3}{(ext ? "+" : " ")} ours {fit[pt],5}"
                                        + $"  best {anchors[k],5}  d {anchors[k] - fit[pt],4}");
                            }
                            continue;
                        }
                    }

                    // WPF_XYSOLVE_MIDS=tied: an implied on-curve midpoint is NOT a free
                    // coordinate. TrueType lets two off-curve control points sit next to each other
                    // and leaves the on-curve point between them implied at their midpoint; the
                    // outline builder materialises it, and this solver has been perturbing it as
                    // though it were a point of the glyph. No interpreter can move it on its own --
                    // it is the average of its neighbours by construction -- so a residual reached
                    // by moving one is a residual reached by an outline that cannot exist.
                    // <para>It is not hypothetical. Of twenty-seven imperfect glyphs that solve to
                    // zero, NINE do it without moving a single real point: the free search found its
                    // answer entirely in the midpoints. Those nine were then reported as "the touch
                    // set is right and only the values are wrong, and no value differs", which is
                    // nonsense on its face and is what exposed this.</para>
                    // <para>Tied, a midpoint follows its neighbours and is never perturbed.</para>
                    bool tieMids = Environment.GetEnvironmentVariable("WPF_XYSOLVE_MIDS") == "tied"
                                   && pathToPoint.Count == sx.Length;
                    void TieMids()
                    {
                        if (!tieMids) return;
                        for (int i = 0; i < sx.Length; i++)
                            if (pathToPoint[i] < 0 && i > 0 && i + 1 < sx.Length)
                            {
                                sx[i] = (sx[i - 1] + sx[i + 1]) / 2;
                                sy[i] = (sy[i - 1] + sy[i + 1]) / 2;
                            }
                    }
                    long start = Score(), cur = start;
                    int renders = 1;
                    for (int pass = 0; pass < passes && cur > 0; pass++)
                    {
                        int step = pass == 0 ? 4 : pass == 1 ? 2 : 1;
                        for (int i = 0; i < sx.Length && cur > 0; i++)
                            for (int axis = 0; axis < 2; axis++)
                            {
                                if (tieMids && pathToPoint[i] < 0) continue;
                                int[] arr = axis == 0 ? sx : sy;
                                int keep = arr[i], bestV = keep;
                                for (int d = -span; d <= span; d += step)
                                {
                                    if (d == 0) continue;
                                    arr[i] = keep + d;
                                    TieMids();
                                    long v = Score();
                                    renders++;
                                    if (v < cur) { cur = v; bestV = arr[i]; }
                                }
                                arr[i] = bestV;
                                TieMids();
                            }
                        Console.Error.WriteLine("   pass " + pass + " (step " + step + "): " + cur);
                    }

                    // NOW WALK IT BACK. The descent above stops the moment the residual reaches
                    // zero, and pass 0 moves in steps of FOUR sixty-fourths, so on a glyph that
                    // solves in the first pass every coordinate it reports is necessarily ours
                    // plus a multiple of 4/64. That is an artefact of the step, and a convincing
                    // one: swept over Times at four sizes it made 147 of 148 differences exact
                    // multiples of a SIXTEENTH OF A PIXEL, which reads as a law about GDI and is
                    // nothing of the kind. Any conclusion drawn from the arithmetic of these
                    // numbers -- a quantised correction, a delta-shaped shift -- was drawing on
                    // the search grid.
                    // <para>So once the residual is zero, minimise the DISPLACEMENT instead: move
                    // every point back toward our own fit one sixty-fourth at a time for as long
                    // as the residual stays zero. What comes out is the outline CLOSEST TO OURS
                    // that still reproduces GDI's pixels exactly, which is the honest form of the
                    // question -- "how far must we move, at least?" -- and it is free of the
                    // grid. Points the image does not constrain collapse onto our own values and
                    // stop being reported as differences at all.</para>
                    if (cur == 0)
                    {
                        bool moved = true;
                        int backRenders = 0;
                        for (int sweep = 0; sweep < 8 && moved; sweep++)
                        {
                            moved = false;
                            for (int i = 0; i < sx.Length; i++)
                                for (int axis = 0; axis < 2; axis++)
                                {
                                    int[] arr = axis == 0 ? sx : sy, ours = axis == 0 ? ox : oy;
                                    while (arr[i] != ours[i])
                                    {
                                        int keep = arr[i];
                                        arr[i] += arr[i] < ours[i] ? 1 : -1;
                                        backRenders++;
                                        if (Score() != 0) { arr[i] = keep; break; }
                                        moved = true;
                                    }
                                }
                        }
                        // AND WALK THE FLAT RUNS BACK TOGETHER. The loop above moves ONE
                        // coordinate at a time, which cannot undo a move that only works jointly --
                        // and that is exactly the case this instrument keeps being pointed at.
                        // Times' bowls reach their extremes through three points at one design x,
                        // IUP can only stack them, and our '9'@14 has P9 = P10 = P11 = 36 dead
                        // straight. If GDI's edge is straight too but sits somewhere else, every
                        // single-point step off that edge breaks the render and the walk-back stops
                        // with all three reported as forced -- an edge that "must bow" when what it
                        // must do is MOVE. So: group the points our own fit puts at the same x, and
                        // walk each group back as one.
                        int groupRenders = 0;
                        var byX = new Dictionary<int, List<int>>();
                        for (int i = 0; i < ox.Length; i++)
                        {
                            if (!byX.TryGetValue(ox[i], out List<int>? g)) byX[ox[i]] = g = new();
                            g.Add(i);
                        }
                        foreach (KeyValuePair<int, List<int>> kv in byX)
                        {
                            List<int> g = kv.Value;
                            if (g.Count < 2) continue;
                            bool bent = false;
                            foreach (int i in g) if (sx[i] != kv.Key) bent = true;
                            if (!bent) continue;
                            // CAN THE EDGE BE STRAIGHT SOMEWHERE ELSE? Try putting the whole group
                            // back onto ONE x -- ours first, then outward -- and keep the nearest
                            // value that still renders GDI exactly. A bow that survives this is a
                            // bow the pixels actually require; one that does not was the descent
                            // bending an edge it could have translated.
                            var keep = new int[g.Count];
                            int was = 0, wasN = 0;
                            for (int k = 0; k < g.Count; k++)
                            {
                                keep[k] = sx[g[k]];
                                was += Math.Abs(keep[k] - ox[g[k]]);
                                if (keep[k] != ox[g[k]]) wasN++;
                            }
                            bool fixedIt = false;
                            for (int d = 0; d <= span && !fixedIt; d++)
                                for (int sgn = 0; sgn < 2 && !fixedIt; sgn++)
                                {
                                    int v = kv.Key + (sgn == 0 ? d : -d);
                                    // ONLY IF IT COSTS LESS. Straightening onto ANY value that
                                    // renders exactly is not an improvement -- it can put a point
                                    // that already agreed with us somewhere it does not, and the
                                    // first version of this pass turned 136 reported differences
                                    // into 166 by doing exactly that. The pass exists to find a
                                    // SMALLER explanation, so it must reduce the displacement.
                                    // ... and must not spread the difference over MORE points
                                    // than it removes it from. Trading "two points out by 3 and 2"
                                    // for "three points out by 1" is a smaller total move and a
                                    // worse description of what happened.
                                    int now = 0, nowN = 0;
                                    foreach (int i in g)
                                    { now += Math.Abs(v - ox[i]); if (v != ox[i]) nowN++; }
                                    if (now >= was || nowN > wasN) { if (d == 0) break; continue; }
                                    foreach (int i in g) sx[i] = v;
                                    groupRenders++;
                                    if (Score() == 0) fixedIt = true;
                                    if (d == 0) break;
                                }
                            if (!fixedIt)
                                for (int k = 0; k < g.Count; k++) sx[g[k]] = keep[k];
                        }
                        renders += backRenders + groupRenders;
                        Console.Error.WriteLine($"   walked back in {backRenders} renders"
                            + $" (+{groupRenders} straightening flat runs);"
                            + " these are the SMALLEST moves that still render GDI exactly");
                    }
                    Console.Error.WriteLine($"== {c} {parts[0]}@{ppem}{style}  {sx.Length} points,"
                        + $" {renders} renders;  as fitted {start}  ->  residual {cur}"
                        + (cur == 0 ? "   EXACT -- these ARE GDI own coordinates" : ""));
                    // AND WHETHER THE GLYPH'S OWN PROGRAM PUT IT THERE. A point the program
                    // touched in x carries a `*`; an untouched one was placed by IUP from its
                    // neighbours. The distinction decides which half of the pipeline a difference
                    // belongs to, and it cannot be had from the coordinates: an interpolated point
                    // can sit anywhere its anchors put it, including exactly on a grid line.
                    string Pt(int i)
                    {
                        if (i >= pathToPoint.Count || pathToPoint[i] < 0)
                            return pathToPoint.Count == sx.Length ? "mid   " : "?     ";
                        int p = pathToPoint[i];
                        bool t = ipts is not null && p < ipts.TouchedX.Length && ipts.TouchedX[p];
                        // AND WHETHER IT IS ON THE CURVE. An off-curve control point is not a place
                        // the outline goes through, so a difference there is a difference in the
                        // SHAPE of a curve rather than in where a point was put -- and the two have
                        // different causes and different fixes. `^` marks off-curve.
                        bool off = ipts is not null && p < ipts.OnCurve.Length && !ipts.OnCurve[p];
                        return $"P{p}{(t ? "*" : "")}{(off ? "^" : "")}".PadRight(6);
                    }
                    // AND WHETHER THE POINT SITS IN A STRAIGHT VERTICAL RUN OF OUR OWN FIT.
                    // Times' bowls reach their extremes through THREE points at one design x -- a
                    // flat vertical edge -- and IUP cannot do anything but stack them, because a
                    // point sharing its reference coordinate with the run's anchor interpolates to
                    // num == 0 and lands exactly on it. So our '9'@14 has P9 = P10 = P11 = 36 and
                    // P17 = P18 = P19 = 408, dead straight, while GDI's pixels force P9 to 39, P11
                    // to 38 and P17 to 398. If that is systematic -- if what must move is
                    // overwhelmingly the flanks of our straight runs -- then GDI is bowing an edge
                    // that our interpolation cannot bow, and the question is what does it.
                    bool Flat(int i)
                    {
                        bool same = false;
                        if (i > 0 && ox[i - 1] == ox[i]) same = true;
                        if (i + 1 < ox.Length && ox[i + 1] == ox[i]) same = true;
                        return same;
                    }
                    int dFlat = 0, nFlat = 0;
                    for (int i = 0; i < sx.Length; i++)
                    {
                        if (!Flat(i)) continue;
                        nFlat++;
                        if (sx[i] != ox[i] || sy[i] != oy[i]) dFlat++;
                    }
                    // WPF_XYSOLVE_TARGETFIT=1: HOW MANY POINTS MUST GDI HAVE TOUCHED? Answered by
                    // arithmetic rather than by a search.
                    // <para>The anchor-space search asks whether some anchor placement renders GDI's
                    // pixels, and it answers by coordinate descent -- which works in four dimensions
                    // and demonstrably fails above about eight, since an eighteen-anchor manifold
                    // contains a twelve-anchor one and yet scored WORSE. No amount of restarting
                    // fixes a descent in that many dimensions.</para>
                    // <para>It does not need a search. IUP is DETERMINED by its anchors: given an
                    // outline, set the anchors to that outline's own values, run the interpolation,
                    // and every other point lands where it must. So take the outline the free solver
                    // just found -- which reproduces GDI's pixels exactly -- as the target, seed the
                    // anchor set with the points the program actually touches, and read off the
                    // point that lands furthest from the target. Make THAT an anchor and repeat.
                    // Each step is one interpolation, the deviation falls monotonically, and the
                    // sequence ends when the target is exactly reproduced. The number of anchors it
                    // took is the number of points GDI must have touched to draw that outline, and
                    // the order names them.</para>
                    // <para>The target is one of several outlines that render the same pixels, so
                    // the COUNT is an upper bound on what GDI needs rather than GDI's own touch set.
                    // An upper bound is still worth having: "four anchors and IUP cannot do it" plus
                    // "six can" is a different problem from "six cannot, nor twenty".</para>
                    // <para>THIS IS AN X-ONLY ANALYSIS, which matters when reading it. The solver
                    // moves both axes; the target here is built from sx alone, so a glyph whose
                    // residual is in Y comes out "IUP-consistent with no anchor moved" -- true, and
                    // not the whole story. Over thirty-one imperfect glyphs the split is 109 points
                    // differing in x against 28 in y (7 in both), seventeen glyphs fixed by x alone
                    // against three by y alone and eleven mixed, and |dy| is a single sixty-fourth
                    // in twenty of the twenty-eight. So x is the bulk and y is not nothing.</para>
                    // <para>Counts on those thirty-one, midpoints tied: nine need no extra touched
                    // point (the y-driven ones), and the rest need 2, 3, 4, 5, 8, 11, 12, 13, 15 --
                    // and one needs 29, which is Times Bold '0'@16. So the outlier that has taken
                    // three rounds IS an outlier: most glyphs are a handful of touched points away,
                    // not a rewrite.</para>
                    if (Environment.GetEnvironmentVariable("WPF_XYSOLVE_TARGETFIT") == "1"
                        && cur == 0 && ipts is not null && pathToPoint.Count == sx.Length)
                    {
                        int np = ipts.PointCount;
                        var org2 = new int[np];
                        var orus2 = new int[np];
                        var fit2 = new int[np];
                        var target = new int[np];
                        for (int i = 0; i < np; i++)
                        {
                            org2[i] = (int) MathF.Round(ipts.StartX[i] * 64f);
                            orus2[i] = ipts.OrusX[i];
                            fit2[i] = (int) MathF.Round(ipts.FitX[i] * 64f);
                            target[i] = fit2[i];
                        }
                        for (int i = 0; i < sx.Length; i++)
                            if (pathToPoint[i] >= 0) target[pathToPoint[i]] = sx[i];

                        var anchorSet = new HashSet<int>();
                        for (int i = 0; i < np; i++) if (ipts.TouchedX[i]) anchorSet.Add(i);
                        int seeded = anchorSet.Count;

                        void Carry2(int[] cc, int from, int to, int r1, int r2)
                        {
                            if (from > to) return;
                            int o1 = orus2[r1], o2 = orus2[r2];
                            if (o1 >= o2) { (o1, o2) = (o2, o1); (r1, r2) = (r2, r1); }
                            int g1 = org2[r1], g2 = org2[r2];
                            int d1 = cc[r1] - g1, d2 = cc[r2] - g2;
                            if (o1 == o2)
                            { for (int i = from; i <= to; i++) cc[i] = org2[i] + d1; return; }
                            for (int i = from; i <= to; i++)
                            {
                                int x = org2[i];
                                if (x >= g2) { cc[i] = x + d2; continue; }
                                if (x <= g1) { cc[i] = x + d1; continue; }
                                long den = o2 - o1, num = orus2[i] - o1;
                                long sp = (long) (g2 + d2) - (g1 + d1);
                                cc[i] = (int) ((num * sp + (den >> 1)) / den) + g1 + d1;
                            }
                        }

                        int[] Iup2()
                        {
                            var cc = new int[np];
                            for (int i = 0; i < np; i++)
                                cc[i] = anchorSet.Contains(i) ? target[i] : org2[i];
                            int first = 0;
                            foreach (int end in ipts.EndPoints)
                            {
                                if (end < first || end >= np) break;
                                var t = new List<int>();
                                for (int i = first; i <= end; i++) if (anchorSet.Contains(i)) t.Add(i);
                                if (t.Count == 1)
                                {
                                    int d = cc[t[0]] - org2[t[0]];
                                    for (int i = first; i <= end; i++)
                                        if (!anchorSet.Contains(i)) cc[i] = org2[i] + d;
                                }
                                else if (t.Count > 1)
                                {
                                    for (int k = 0; k + 1 < t.Count; k++)
                                        Carry2(cc, t[k] + 1, t[k + 1] - 1, t[k], t[k + 1]);
                                    Carry2(cc, t[t.Count - 1] + 1, end, t[t.Count - 1], t[0]);
                                    Carry2(cc, first, t[0] - 1, t[t.Count - 1], t[0]);
                                }
                                first = end + 1;
                            }
                            return cc;
                        }

                        var added = new List<int>();
                        int targetTol = int.TryParse(
                            Environment.GetEnvironmentVariable("WPF_XYSOLVE_TARGETFIT_TOL"),
                            out int ttol) ? ttol : 0;
                        int worst = 0, worstPt = -1;
                        for (int step = 0; step <= np; step++)
                        {
                            int[] cc = Iup2();
                            worst = 0; worstPt = -1;
                            for (int i = 0; i < np; i++)
                            {
                                if (anchorSet.Contains(i)) continue;
                                int e = Math.Abs(cc[i] - target[i]);
                                if (e > worst) { worst = e; worstPt = i; }
                            }
                            // A TOLERANCE, because the count is otherwise inflated by noise. The
                            // walk stops when the worst-placed point is within it, so the answer
                            // becomes "how many points must GDI have touched to get the outline
                            // within n sixty-fourths" rather than "to reproduce one particular
                            // solved outline exactly". Thirty-five of the hundred-odd points this
                            // adds at tolerance zero are off by a SINGLE sixty-fourth, and a point
                            // a 64th out is not evidence that an instruction placed it.
                            if (worst <= targetTol || worstPt < 0) break;
                            // WHAT KIND OF POINT IS IT? Three attributes, because a rule for "which
                            // points GDI touches that our program does not" has to be stated in
                            // terms the interpreter can see. `pinned` is the sharp one: a point
                            // whose ORUS equals that of a run endpoint can never be moved off that
                            // endpoint's value by interpolation -- num comes out zero -- so if GDI
                            // has it somewhere else, GDI touched it, and that is a deduction rather
                            // than a fit.
                            bool onCurve = ipts.OnCurve[worstPt];
                            bool pinned = false;
                            {
                                int f2 = 0;
                                foreach (int e2 in ipts.EndPoints)
                                {
                                    if (worstPt >= f2 && worstPt <= e2)
                                    {
                                        foreach (int a2 in anchorSet)
                                            if (a2 >= f2 && a2 <= e2 && orus2[a2] == orus2[worstPt])
                                                pinned = true;
                                        break;
                                    }
                                    f2 = e2 + 1;
                                }
                            }
                            if (Environment.GetEnvironmentVariable("WPF_XYSOLVE_TARGETFIT_DUMP") == "1")
                                Console.Error.WriteLine($"     +anchor {c}@{ppem}{style} P{worstPt,-3}"
                                    + $" off {worst,4}  {(onCurve ? "on " : "off")}"
                                    + $" {(pinned ? "PINNED" : "free  ")}"
                                    + $" ours {fit2[worstPt],5} gdi {target[worstPt],5}");
                            anchorSet.Add(worstPt);
                            added.Add(worstPt);
                        }
                        Console.Error.WriteLine($"   TARGETFIT {c}@{ppem}{style}: the solved outline"
                            + $" needs {anchorSet.Count} touched points ({seeded} from the program"
                            + $" + {added.Count} more) -- "
                            + (added.Count == 0 ? "IT IS ALREADY IUP-CONSISTENT"
                               : "added " + string.Join(",", added))
                            + (targetTol > 0 ? $"   [tolerance {targetTol}/64]" : ""));
                        // AND WHEN IT IS CONSISTENT, GDI'S OWN ANCHOR VALUES FALL OUT. No search:
                        // if the target is reproduced by interpolating from the program's own
                        // touched points, then the target's value AT each of those points is the
                        // value GDI put there. That turns "three quarters of the residual is anchor
                        // placement" from a characterisation into a table of targets.
                        if (added.Count == 0)
                            for (int i = 0; i < np; i++)
                                if (ipts.TouchedX[i] && target[i] != fit2[i])
                                    Console.Error.WriteLine($"     ANCHOR {c}@{ppem}{style} P{i,-3}"
                                        + $" ours {fit2[i],5}  gdi {target[i],5}"
                                        + $"  d {target[i] - fit2[i],4}"
                                        + $"  org {org2[i],5}  orus {orus2[i],6}");
                    }
                    int dOff = 0, dOn = 0, dTouch = 0, nOff = 0, nOn = 0;
                    for (int i = 0; i < sx.Length; i++)
                    {
                        if (i >= pathToPoint.Count || pathToPoint[i] < 0 || ipts is null) continue;
                        int p = pathToPoint[i];
                        if (p >= ipts.OnCurve.Length) continue;
                        bool diff = sx[i] != ox[i] || sy[i] != oy[i];
                        if (ipts.OnCurve[p]) { nOn++; if (diff) dOn++; }
                        else { nOff++; if (diff) dOff++; }
                        if (diff && p < ipts.TouchedX.Length && ipts.TouchedX[p]) dTouch++;
                    }
                    if (ipts is not null && pathToPoint.Count == sx.Length)
                        Console.Error.WriteLine($"   SPLIT {c}@{ppem}: differing  off-curve"
                            + $" {dOff}/{nOff}  on-curve {dOn}/{nOn}  x-touched {dTouch}"
                            + $"  in-flat-run {dFlat}/{nFlat} of {sx.Length}");
                    if (pathToPoint.Count != sx.Length)
                        Console.Error.WriteLine($"   (no interpreter index: the path emits"
                            + $" {sx.Length} points and the reconstruction makes"
                            + $" {pathToPoint.Count})");
                    for (int i = 0; i < sx.Length; i++)
                        if (sx[i] != ox[i] || sy[i] != oy[i])
                            Console.Error.WriteLine($"   pt {i,3} {Pt(i)} ours ({ox[i],5},{oy[i],5})"
                                + $"  gdi ({sx[i],5},{sy[i],5})  d ({sx[i] - ox[i],4},{sy[i] - oy[i],4})");
                    // WPF_XYSOLVE_INTERVAL=1: how much SLACK each x has, once the residual is zero.
                    // <para>The solve is a coordinate descent that moves a point only when the move
                    // reduces the residual and stops at zero, so what it reports is the FIRST
                    // configuration on its path that renders GDI's pixels exactly -- not the only
                    // one. A point the pixels do not pin down lands anywhere in its slack, and the
                    // per-point column then says "this value works", not "this is GDI's". Reading
                    // it as the latter is how one ends up trying to reproduce a coordinate that was
                    // never determined.</para>
                    // <para>This walks each x out in both directions from the solved value and
                    // reports the widest run that keeps the residual at zero. A point printed
                    // `[0,0]` is PINNED and its value is GDI's; a wide interval is a point the
                    // pixels do not constrain, and any rule that reproduces something inside it is
                    // as good as any other. Every point is listed, not just the moved ones,
                    // because an unmoved point with a wide interval is equally uninformative.</para>
                    if (cur == 0 && Environment.GetEnvironmentVariable("WPF_XYSOLVE_INTERVAL") == "1")
                    {
                        Console.Error.WriteLine("   slack in x that still renders GDI exactly:");
                        for (int i = 0; i < sx.Length; i++)
                        {
                            int keep = sx[i], lo = 0, hi = 0;
                            while (lo > -span)
                            { sx[i] = keep + lo - 1; if (Score() != 0) break; lo--; }
                            while (hi < span)
                            { sx[i] = keep + hi + 1; if (Score() != 0) break; hi++; }
                            sx[i] = keep;
                            Console.Error.WriteLine($"   pt {i,3} {Pt(i)} ours {ox[i],5}  gdi {keep,5}"
                                + $"  slack [{lo,3},{hi,3}]{(lo == 0 && hi == 0 ? "  PINNED" : "")}");
                        }
                    }
                }
            }
            finally
            {
                TrueTypeFont.SubpixelFitting = savedSubpix;
                TrueTypeInterpreter.s_capturePoints = false;
            }
        }

        /// <summary>WHERE DOES GDI PUT ONE POINT? `WPF_KNOTSOLVE=family/chars/ppem[/B|I]` with
        /// `WPF_KNOTSOLVE_AT=<x>,<y>` in 64ths naming a point of OUR fitted outline by its
        /// coordinates.
        /// <para>The per-edge solver has as many unknowns as the glyph has edges and stops
        /// converging on a curve; the shear solver has two but cannot bend anything. This has ONE:
        /// slide every emitted point that sits at (x, y) along x, and report GDI's own score at
        /// each position. It exists because reasoning had narrowed Times' bowls to a single
        /// coordinate -- the knot an ALIGNRP loop collapses three points onto -- and the only thing
        /// left to do with a claim that specific is to put the point there and look.</para>
        /// <para>A zero says the claim is exactly right. A minimum that is NOT near zero says the
        /// point is not the whole difference, which is worth just as much: it is the difference
        /// between one wrong coordinate and a wrong SHAPE.</para></summary>
        [Fact]
        public void SolveGdisKnot()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_KNOTSOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_KNOTSOLVE=family/chars/ppem[/style]");
            string? at = Environment.GetEnvironmentVariable("WPF_KNOTSOLVE_AT");
            Assert.SkipWhen(string.IsNullOrEmpty(at), "set WPF_KNOTSOLVE_AT=<x64>,<y64>");
            string[] atp = at!.Split(',');
            int atX = int.Parse(atp[0]), atY = int.Parse(atp[1]);

            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');
            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            renderer.SubpixelRowsOverride = font.WantsSymmetricSmoothing(ppem) ? 5 : 0;
            renderer.DropoutOverride = font.WantsDropoutControl(ppem, out int scanType) ? scanType + 1 : 0;
            renderer.PpemOverride = ppem;
            var raw = new byte[Width * Height * 4];

            bool savedSubpix = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = true;
            try
            {
                foreach (char c in parts[1])
                {
                    int gid = font.GlyphIndex(c);
                    if (gid <= 0 || !((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem,
                            out List<PathFigure> fitted) || fitted.Count == 0)
                    { Console.Error.WriteLine($"== '{c}': not fitted"); continue; }

                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
                    Gdi.s_rawRgb = null;

                    // WPF_KNOTSOLVE_MODE=y sweeps the point's Y instead, and then matches on Y
                    // ALONE -- so both ends of a symmetric feature move together, which is what a
                    // bowl's shoulder is. The straight side of a Times bowl runs between two such
                    // rows, and its LENGTH is what decides how many rows the glyph is full width
                    // for.
                    bool yMode = Environment.GetEnvironmentVariable("WPF_KNOTSOLVE_MODE") == "y";
                    int moved = 0;
                    long Score(int newV)
                    {
                        moved = 0;
                        Vector2 M(Vector2 p)
                        {
                            float x = p.X, y = p.Y;
                            if (yMode)
                            {
                                if ((int) MathF.Round(p.Y * 64f) == atY) { y = newV / 64f; moved++; }
                            }
                            else if ((int) MathF.Round(p.X * 64f) == atX
                                     && (int) MathF.Round(p.Y * 64f) == atY)
                            { x = newV / 64f; moved++; }
                            return new(PenX + x, 28f + y);
                        }
                        var placed = new List<PathFigure>(fitted.Count);
                        foreach (PathFigure f in fitted)
                        {
                            var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                            foreach (PathSegment sg in f.Segments)
                                nf.Segments.Add(sg switch
                                {
                                    LineSegment l => new LineSegment(M(l.Point)),
                                    QuadraticBezierSegment q =>
                                        new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                                    CubicBezierSegment c3 =>
                                        new CubicBezierSegment(M(c3.Control1), M(c3.Control2), M(c3.Point)),
                                    _ => sg,
                                });
                            placed.Add(nf);
                        }
                        var root = new SceneVisual();
                        root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                            new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)), isGlyph: true)
                            { PixelAligned = true });
                        byte[] ours = renderer.RenderToRgba(root, Width, Height,
                            RgbaColor.FromBytes(255, 255, 255, 255));
                        long sum = 0;
                        for (int i = 0; i < Width * Height; i++)
                            for (int ch = 0; ch < 3; ch++)
                                sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                        return sum;
                    }

                    int from = yMode ? atY : atX;
                    long baseline = Score(from);
                    var sb = new System.Text.StringBuilder();
                    long best = long.MaxValue; int bestX = from;
                    for (int x = from - 16; x <= from + 112; x += 2)
                    {
                        long v = Score(x);
                        if (v < best) { best = v; bestX = x; }
                        if (x % 8 == 0 || v == best) sb.Append($"  {x}:{v}");
                    }
                    Console.Error.WriteLine($"== '{c}' {parts[0]}@{ppem}{style}"
                        + $" {(yMode ? "y" : "x")}-sweep of ({atX},{atY}),"
                        + $" {moved} emitted point(s) moved;  as fitted {baseline}"
                        + $"  ->  best {best} at x={bestX}/64 ({bestX / 64f:0.000}px)");
                    Console.Error.WriteLine("   sweep:" + sb);
                }
            }
            finally { TrueTypeFont.SubpixelFitting = savedSubpix; }
        }

        /// <summary>IS GDI'S GLYPH OURS, SHIFTED AND SHEARED? `WPF_SHEARSOLVE=family/chars/ppem[/B|I]`.
        /// <para>The per-edge solver answers "where is each edge" and, when it does not converge,
        /// leaves a table nobody can read. This asks a far smaller question with only two unknowns,
        /// so it always converges and its answer means something: take OUR fitted outline, slide it
        /// by dx and lean it by `shear` 64ths per pixel of height above the baseline, and find the
        /// pair that best reproduces GDI's own pixels. A zero at some (dx, shear) says GDI's glyph
        /// IS ours under that rigid motion -- and for an italic that is the whole question, because
        /// a lean that does not match turns into a sideways error that grows with height and looks
        /// like a placement error at every row.</para></summary>
        [Fact]
        public void SolveGdisShearAndShift()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_SHEARSOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_SHEARSOLVE=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            // Same rasterizer configuration the glyph-run path hands out; without it the search
            // inverts a rasterizer GDI is not being compared against.
            bool solveSym = font.WantsSymmetricSmoothing(ppem);
            renderer.SubpixelRowsOverride = solveSym ? 5 : 0;
            renderer.DropoutOverride = font.WantsDropoutControl(ppem, out int scanType) ? scanType + 1 : 0;
            renderer.PpemOverride = ppem;
            var raw = new byte[Width * Height * 4];

            bool savedSubpix = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = true;
            try
            {
                foreach (char c in parts[1])
                {
                    int gid = font.GlyphIndex(c);
                    if (gid <= 0 || !((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem,
                            out List<PathFigure> fitted) || fitted.Count == 0)
                    { Console.Error.WriteLine($"== '{c}': not fitted"); continue; }

                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
                    Gdi.s_rawRgb = null;

                    long Score(int dx, int shear)
                    {
                        // p.Y is measured DOWN from the baseline, so -p.Y is the height above it.
                        Vector2 M(Vector2 p) => new(
                            PenX + p.X + (dx + shear * -p.Y) / 64f, 28f + p.Y);
                        var placed = new List<PathFigure>(fitted.Count);
                        foreach (PathFigure f in fitted)
                        {
                            var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                            foreach (PathSegment sg in f.Segments)
                                nf.Segments.Add(sg switch
                                {
                                    LineSegment l => new LineSegment(M(l.Point)),
                                    QuadraticBezierSegment q =>
                                        new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                                    CubicBezierSegment c3 =>
                                        new CubicBezierSegment(M(c3.Control1), M(c3.Control2), M(c3.Point)),
                                    _ => sg,
                                });
                            placed.Add(nf);
                        }
                        var root = new SceneVisual();
                        root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                            new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)), isGlyph: true)
                            { PixelAligned = true });
                        byte[] ours = renderer.RenderToRgba(root, Width, Height,
                            RgbaColor.FromBytes(255, 255, 255, 255));
                        long sum = 0;
                        for (int i = 0; i < Width * Height; i++)
                            for (int ch = 0; ch < 3; ch++)
                                sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                        return sum;
                    }

                    long best = Score(0, 0); int bdx = 0, bsh = 0;
                    long atZero = best;
                    for (int step = 4; step >= 1; step /= 2)
                        for (int dx = bdx - 10 * step; dx <= bdx + 10 * step; dx += step)
                            for (int sh = bsh - 10 * step; sh <= bsh + 10 * step; sh += step)
                            {
                                long v = Score(dx, sh);
                                if (v < best) { best = v; bdx = dx; bsh = sh; }
                            }
                    // The shear is per PIXEL of height; report it as a slope so it can be read
                    // against the face's own italic angle.
                    Console.Error.WriteLine($"== '{c}' {parts[0]}@{ppem}{style}  as rendered {atZero}"
                        + $"  ->  best {best} at dx {bdx}/64 ({bdx / 64f:+0.000;-0.000}px),"
                        + $" shear {bsh}/64 per px (slope {bsh / 64f:+0.0000;-0.0000})"
                        + (best == 0 ? "   EXACT -- GDI's glyph IS ours, shifted and sheared" : ""));
                }
            }
            finally { TrueTypeFont.SubpixelFitting = savedSubpix; }
        }

        /// <para>WPF_EDGESOLVE=family/chars/ppem[/B|I|BI].</para></summary>
        [Fact]
        public void SolveGdisEdges()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_EDGESOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_EDGESOLVE=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
            float natScale = ppem / (float) font.PixelsPerEm;

            // The bi-level fitting, for reference. Prep runs at construction, so a separate font.
            bool savedBi = TrueTypeInterpreter.BiLevelPass;
            TrueTypeFont biFont;
            try
            {
                TrueTypeInterpreter.BiLevelPass = true;
                biFont = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
            }
            finally { TrueTypeInterpreter.BiLevelPass = savedBi; }

            // ONE renderer for the whole search. The shift solver builds one per render, and at a
            // few thousand renders a glyph that is minutes and gigabytes.
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = true;
            var raw = new byte[Width * Height * 4];
            byte[] lastOurs = Array.Empty<byte>();
            // THE SOLVER MUST MODEL THE SAME RASTERIZER THE GLYPH PATH USES. It renders a
            // GeometryFill, not a glyph run, and only the glyph-run block in WgpuSceneRenderer
            // hands the rasterizer its per-run configuration -- so the search was inverting a
            // rasterizer with NO vertical dropout control and, for a symmetric face, the wrong
            // number of sub-rows. Every edge it reported for a face whose prep asks for dropout
            // control (Times, Arial, Segoe UI, all of them) absorbed the ink of the rows GDI
            // fills and we did not model. Mirrored from the hand-off at WgpuSceneRenderer's
            // per-run block; the two row counts are both five and WPF_SYM_VERTICAL is off.
            bool solveSym = font.WantsSymmetricSmoothing(ppem);
            int solveRows = solveSym ? 5 : 0;
            int solveDropout = font.WantsDropoutControl(ppem, out int solveScanType) ? solveScanType + 1 : 0;
            if (Environment.GetEnvironmentVariable("WPF_EDGESOLVE_MAP") == "1")
                Console.Error.WriteLine($"   [solver models] symmetricRows={solveRows}"
                                        + $" dropout={(solveDropout > 0 ? $"scanType {solveScanType}" : "off")}");
            renderer.SubpixelRowsOverride = solveRows;
            renderer.DropoutOverride = solveDropout;
            renderer.PpemOverride = ppem;
            long Score(List<PathFigure> placed)
            {
                var root = new SceneVisual();
                root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                                                  new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),
                                                  isGlyph: true) { PixelAligned = true });
                byte[] ours = renderer.RenderToRgba(root, Width, Height, RgbaColor.FromBytes(255, 255, 255, 255));
                lastOurs = ours;
                long sum = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch = 0; ch < 3; ch++)
                        sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                return sum;
            }

            // For each edge x, is it a VERTICAL feature (a stem side, neighbours running up
            // and down) or a DIAGONAL? On the fitted outline: a point is vertical when the segment
            // to at least one neighbour is steeper than 2:1.
            static System.Collections.Generic.Dictionary<int, char> EdgeKind(List<PathFigure> figures)
            {
                var vote = new System.Collections.Generic.Dictionary<int, (int v, int d)>();
                foreach (PathFigure f in figures)
                {
                    var pts = new List<Vector2> { f.Start };
                    foreach (PathSegment sg in f.Segments)
                        pts.Add(sg switch
                        {
                            LineSegment l => l.Point,
                            QuadraticBezierSegment q => q.Point,
                            CubicBezierSegment c => c.Point,
                            _ => f.Start,
                        });
                    if (pts.Count > 1 && pts[^1] == pts[0]) pts.RemoveAt(pts.Count - 1);
                    int m = pts.Count;
                    for (int i = 0; i < m; i++)
                    {
                        Vector2 a = pts[(i - 1 + m) % m], p = pts[i], b = pts[(i + 1) % m];
                        // WPF_EDGESOLVE_STEMSLOPE (default 2): how many times steeper than 45
                        // a segment must be to count its point vertical. A true stem side is
                        // near-infinite; an 'A' leg is ~3, an 'X' arm ~1.
                        float stemSlope = float.TryParse(Environment.GetEnvironmentVariable(
                            "WPF_EDGESOLVE_STEMSLOPE"), out float ss) ? ss : 2f;
                        bool Steep(Vector2 u, Vector2 w)
                            => MathF.Abs(w.Y - u.Y) > stemSlope * MathF.Abs(w.X - u.X);
                        bool vert = Steep(a, p) || Steep(p, b);
                        int key = (int) MathF.Round(p.X * 64f);
                        (int v, int d) cur = vote.TryGetValue(key, out var t) ? t : (0, 0);
                        vote[key] = vert ? (cur.v + 1, cur.d) : (cur.v, cur.d + 1);
                    }
                }
                var kind = new System.Collections.Generic.Dictionary<int, char>();
                foreach (var kv in vote) kind[kv.Key] = kv.Value.v >= kv.Value.d ? 'V' : 'D';
                return kind;
            }

            static SortedSet<int> Edges(List<PathFigure> figures)
            {
                var keys = new SortedSet<int>();
                void See(Vector2 p) => keys.Add((int) MathF.Round(p.X * 64f));
                foreach (PathFigure f in figures)
                {
                    See(f.Start);
                    foreach (PathSegment sg in f.Segments)
                        if (sg is LineSegment ls) See(ls.Point);
                        else if (sg is QuadraticBezierSegment qs) { See(qs.Control); See(qs.Point); }
                        else if (sg is CubicBezierSegment cs) { See(cs.Control1); See(cs.Control2); See(cs.Point); }
                }
                return keys;
            }

            // SUBPIXELFITTING IS A STATIC AND IT DEFAULTS TO FALSE. Without setting it this solver
            // asked for the BI-LEVEL fit and compared it against GDI's CLEARTYPE pixels -- so its
            // per-edge numbers described a glyph the oracle never draws, the compatible-width phase
            // never ran (it is gated on SubpixelFitting), and WPF_CT_PHASE=0/1 changed nothing here.
            // That is the source of several wrong readings on Arial Italic 'w' and Times Italic 'l'.
            // WPF_EDGESOLVE_BILEVEL=1 asks for the old behaviour deliberately.
            bool savedSubpix = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting =
                Environment.GetEnvironmentVariable("WPF_EDGESOLVE_BILEVEL") != "1";
            try
            {
            foreach (char c in parts[1])
            {
                int gid = font.GlyphIndex(c);
                if (gid <= 0 || !((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out List<PathFigure> fitted)
                    || fitted.Count == 0)
                {
                    Console.Error.WriteLine($"== {parts[0]} '{c}' @{ppem}: not fitted");
                    continue;
                }
                List<PathFigure> bi;
                try
                {
                    // The glyph program consults the flag too, not only prep.
                    TrueTypeInterpreter.BiLevelPass = true;
                    bi = ((IHintedGlyphFont) biFont).TryGetHintedOutline(gid, ppem, out List<PathFigure> b)
                        ? b : new List<PathFigure>();
                }
                finally { TrueTypeInterpreter.BiLevelPass = savedBi; }
                List<PathFigure> natural = font.TryGetGlyphOutline(gid, out List<PathFigure> plain)
                    ? GlyphRunPainter.ScaleFigures(plain, natScale, 0f, 0f) : new List<PathFigure>();

                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
                Gdi.s_rawRgb = null;

                // WPF_EDGESOLVE_PERPOINT=1: every point its own unknown. The distinct-x grouping
                // cannot say that GDI moved apart two points OUR fit left at the same x -- which is
                // what the crossing strokes of an 'x' or 'k' do -- so this mode unties them, at the
                // price of wider ambiguity runs and a slower sweep.
                bool perPoint = Environment.GetEnvironmentVariable("WPF_EDGESOLVE_PERPOINT") == "1";
                int[] edge;
                if (perPoint)
                {
                    var flat = new List<int>();
                    void SeeP(Vector2 p) => flat.Add((int) MathF.Round(p.X * 64f));
                    foreach (PathFigure f in fitted)
                    {
                        SeeP(f.Start);
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment ls) SeeP(ls.Point);
                            else if (sg is QuadraticBezierSegment qs) { SeeP(qs.Control); SeeP(qs.Point); }
                            else if (sg is CubicBezierSegment cs) { SeeP(cs.Control1); SeeP(cs.Control2); SeeP(cs.Point); }
                    }
                    edge = flat.ToArray();
                }
                else
                {
                    edge = new int[Edges(fitted).Count];
                    Edges(fitted).CopyTo(edge);
                }
                int[] delta = new int[edge.Length];
                int[] runLo = new int[edge.Length], runHi = new int[edge.Length];
                int placedIndex = 0;
                int dyAll = 0;
                List<PathFigure> Placed()
                {
                    placedIndex = 0;
                    Vector2 M(Vector2 p)
                    {
                        int slot = perPoint ? placedIndex++
                                            : Array.IndexOf(edge, (int) MathF.Round(p.X * 64f));
                        return new(PenX + p.X + delta[slot] / 64f, 28f + p.Y + dyAll / 64f);
                    }
                    var placed = new List<PathFigure>(fitted.Count);
                    foreach (PathFigure f in fitted)
                    {
                        var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                        foreach (PathSegment sg in f.Segments)
                            nf.Segments.Add(sg switch
                            {
                                LineSegment l => new LineSegment(M(l.Point)),
                                QuadraticBezierSegment q => new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                                CubicBezierSegment k => new CubicBezierSegment(M(k.Control1), M(k.Control2), M(k.Point)),
                                _ => sg,
                            });
                        placed.Add(nf);
                    }
                    return placed;
                }

                long atStart = Score(Placed()), best = atStart;

                // A RIGID SHIFT FIRST. Some glyphs are our shape half a pixel away -- Verdana's
                // roman 'w' at 12ppem -- and starting the descent from the best whole-glyph offset
                // keeps the single-edge passes from tearing the shape apart on the way there.
                {
                    int bestG = 0; long lowestG = best;
                    for (int g = -48; g <= 48; g++)
                    {
                        if (g == 0) continue;
                        for (int k = 0; k < edge.Length; k++) delta[k] = g;
                        long s = Score(Placed());
                        if (s < lowestG) { lowestG = s; bestG = g; }
                    }
                    for (int k = 0; k < edge.Length; k++) delta[k] = bestG;
                    best = lowestG;
                }

                int passes = 0;
                for (bool moved = true; moved && passes < 8; passes++)
                {
                    moved = false;
                    for (int k = 0; k < edge.Length; k++)
                    {
                        int was = delta[k];
                        long lowest = long.MaxValue;
                        var scores = new long[129];
                        for (int d = -64; d <= 64; d++)
                        {
                            delta[k] = d;
                            scores[d + 64] = Score(Placed());
                            if (scores[d + 64] < lowest) lowest = scores[d + 64];
                        }
                        // The run of the lowest score that contains the current position if one
                        // does, else the run nearest to it; the edge settles at its middle.
                        int bestLo = int.MinValue, bestHi = int.MinValue, bestDist = int.MaxValue;
                        for (int d = -64; d <= 64; d++)
                        {
                            if (scores[d + 64] != lowest) continue;
                            int lo = d;
                            while (d + 1 <= 64 && scores[d + 65] == lowest) d++;
                            int dist = was < lo ? lo - was : was > d ? was - d : 0;
                            if (dist < bestDist) { bestDist = dist; bestLo = lo; bestHi = d; }
                        }
                        int mid = (bestLo + bestHi) / 2;
                        if (bestLo <= was && was <= bestHi) mid = was;   // already inside: hold still
                        delta[k] = mid;
                        runLo[k] = bestLo; runHi[k] = bestHi;
                        best = lowest;
                        if (Environment.GetEnvironmentVariable("WPF_EDGESOLVE_TRACE") == "1")
                            Console.Error.WriteLine($"      pass {passes} edge {k}: was {was} lowest {lowest} run [{bestLo},{bestHi}] -> {mid}   "
                                                    + string.Join(' ', System.Linq.Enumerable.Select(scores, (s, i) => (i - 64) % 8 == 0 ? $"{i - 64}:{s}" : "")));
                        if (mid != was) moved = true;
                    }

                    // PAIRS. Two neighbouring edges are usually the two sides of one stroke, and
                    // for a diagonal that crosses another -- the strokes of an 'x' or a 'k' --
                    // moving either side alone makes the stroke the wrong width, a local minimum
                    // the single-edge sweep cannot leave. Slide each adjacent pair together before
                    // calling the pass settled.
                    for (int k = 0; k + 1 < edge.Length; k++)
                    {
                        int wasA = delta[k], wasB = delta[k + 1];
                        int bestD = 0; long lowestP = best;
                        for (int d = -24; d <= 24; d++)
                        {
                            if (d == 0) continue;
                            delta[k] = wasA + d; delta[k + 1] = wasB + d;
                            long s = Score(Placed());
                            if (s < lowestP) { lowestP = s; bestD = d; }
                        }
                        delta[k] = wasA + bestD; delta[k + 1] = wasB + bestD;
                        if (bestD != 0) { best = lowestP; moved = true; }
                    }
                }
                // Recentre every edge in its run once the others have settled, so the printed
                // answer is the middle of the final run and not where the descent happened to stop.
                if (best == 0)
                    for (int k = 0; k < edge.Length; k++)
                        delta[k] = (runLo[k] + runHi[k]) / 2;
                best = Score(Placed());

                // WPF_EDGESOLVE_DUMP=<dir>: GDI's bitmap and the solved one, side by side on
                // disk, because a residual no move can reach needs to be LOOKED at.
                void DumpPair(string tag)
                {
                    if (Environment.GetEnvironmentVariable("WPF_EDGESOLVE_DUMP") is not { Length: > 0 } dir) return;
                    Directory.CreateDirectory(dir);
                    var g = new byte[Width * Height * 4];
                    var d = new byte[Width * Height * 4];
                    for (int i = 0; i < Width * Height; i++)
                    {
                        for (int ch = 0; ch < 3; ch++)
                        {
                            g[i * 4 + ch] = raw[i * 4 + (2 - ch)];
                            int dd = Math.Abs(raw[i * 4 + (2 - ch)] - lastOurs[i * 4 + ch]);
                            d[i * 4 + ch] = (byte) Math.Max(0, 255 - dd * 4);
                        }
                        g[i * 4 + 3] = d[i * 4 + 3] = 255;
                    }
                    string stem = $"{parts[0]}-{(style == "" ? "R" : style)}-{ppem}-{(int) c}-{tag}";
                    PngWriter.Write(Path.Combine(dir, stem + "-gdi.png"), g, Width, Height);
                    PngWriter.Write(Path.Combine(dir, stem + "-ours.png"), lastOurs, Width, Height);
                    PngWriter.Write(Path.Combine(dir, stem + "-dif.png"), d, Width, Height);
                }

                // WHAT IS LEFT, IS IT Y? A residual the x moves cannot reach may be a glyph whose
                // ClearType y-fit differs from ours. A rigid vertical offset cannot SOLVE that,
                // but it can implicate it: if sliding the whole solved outline up or down takes
                // a real bite out of the residual, the unknown is in y, not x.
                int bestDy = 0;
                if (best > 0)
                {
                    long lowestY = best;
                    for (int dy = -12; dy <= 12; dy++)
                    {
                        if (dy == 0) continue;
                        dyAll = dy;
                        long s = Score(Placed());
                        if (s < lowestY) { lowestY = s; bestDy = dy; }
                    }
                    dyAll = bestDy;
                    if (bestDy != 0)
                    {
                        // One more x pass with the better y, so the report reflects both.
                        for (int k = 0; k < edge.Length; k++)
                        {
                            int was = delta[k]; long lowest = long.MaxValue; int at = was;
                            for (int d = was - 12; d <= was + 12; d++)
                            {
                                delta[k] = d;
                                long s = Score(Placed());
                                if (s < lowest) { lowest = s; at = d; }
                            }
                            delta[k] = at;
                            best = lowest;
                        }
                    }
                    else best = lowestY;
                }
                best = Score(Placed());
                DumpPair("solved");

                int gdiAdvance = Gdi.TextWidth(c.ToString(), parts[0], ppem, bold, italic);
                var sb = new System.Text.StringBuilder();
                sb.Append($"== {parts[0]} '{c}' @{ppem}{(style == "" ? "" : "/" + style)}"
                          + $"  linear {font.Advance(gid) * natScale:0.000}  GDI adv {gdiAdvance}"
                          + $"  at start {atStart}  after {passes} passes {best}"
                          + (bestDy != 0 ? $"  [dy {bestDy}/64 helps]" : "")
                          + (best == 0 ? "  SOLVED EXACTLY" : "  NOT EXACT") + '\n');
                sb.Append("   edge   ours(64)  ours(px)   GDI(64)  GDI(px)   delta   run\n");
                for (int k = 0; k < edge.Length; k++)
                    sb.Append($"   {k,3}   {edge[k],7}  {edge[k] / 64f,8:0.000}   {edge[k] + delta[k],7}"
                              + $"  {(edge[k] + delta[k]) / 64f,7:0.000}   {delta[k],5}   [{runLo[k]},{runHi[k]}]\n");
                sb.Append("   bi-level edges(64): " + string.Join(' ', Edges(bi)) + '\n');
                sb.Append("   natural  edges(64): " + string.Join(' ', Edges(natural)) + '\n');
                sb.AppendLine($"   ISECT executions so far: {TrueTypeInterpreter.s_isectCount}");
                Console.Error.Write(sb.ToString());
                // WPF_EDGESOLVE_MAP=1: the two coverage fields side by side, so a NOT EXACT result
                // can be read as a picture -- WHERE the ink differs, not just how much. Green
                // channel (the middle lamp), 0-9 per pixel, with a signed difference field.
                if (Environment.GetEnvironmentVariable("WPF_EDGESOLVE_MAP") == "1")
                {
                    // Render the UNSOLVED outline: `Score` leaves its bitmap in lastOurs, and by this point
                    // that is the SOLVED one, which is not what anybody wants to look at.
                    var savedDelta = (int[]) delta.Clone();
                    Array.Clear(delta, 0, delta.Length);
                    Score(Placed());
                    Array.Copy(savedDelta, delta, delta.Length);
                    int x0 = Width, x1 = -1, y0 = Height, y1 = -1;
                    for (int y = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++)
                        {
                            int gi = 255 - raw[(y * Width + x) * 4 + 1];
                            int oi = 255 - lastOurs[(y * Width + x) * 4 + 1];
                            if (gi > 8 || oi > 8)
                            { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                        }
                    if (x1 >= x0)
                    {
                        var mp = new System.Text.StringBuilder();
                        char Cell(byte[] img, int x, int y)
                        {
                            int v = 255 - img[(y * Width + x) * 4 + 1];
                            return v == 0 ? '.' : (char) ('0' + Math.Min(9, v * 10 / 256));
                        }
                        mp.AppendLine("   GDI" + new string(' ', Math.Max(1, x1 - x0 - 1))
                                      + "  ours" + new string(' ', Math.Max(1, x1 - x0 - 2)) + "  diff");
                        for (int y = y0; y <= y1; y++)
                        {
                            var g = new System.Text.StringBuilder();
                            var o = new System.Text.StringBuilder();
                            var d = new System.Text.StringBuilder();
                            for (int x = x0; x <= x1; x++)
                            {
                                g.Append(Cell(raw, x, y));
                                o.Append(Cell(lastOurs, x, y));
                                int gv = 255 - raw[(y * Width + x) * 4 + 1];
                                int ov = 255 - lastOurs[(y * Width + x) * 4 + 1];
                                int t = (ov - gv) * 10 / 256;
                                d.Append(t == 0 ? '.' : t > 0 ? (char) ('0' + Math.Min(9, t))
                                                              : (char) ('a' + Math.Min(9, -t) - 1));
                            }
                            mp.AppendLine("   " + g + "  " + o + "  " + d);
                        }
                        mp.AppendLine("   (diff: digits = we have MORE ink, a..i = we have LESS)");
                        Console.Error.Write(mp.ToString());
                    }
                }

                // WPF_EDGESOLVE_TSV: the same answer as one machine-readable line per glyph, so a
                // few hundred solves become a TABLE of GDI's ClearType x rather than scrollback.
                if (Environment.GetEnvironmentVariable("WPF_EDGESOLVE_TSV") is { Length: > 0 } tsv)
                {
                    string L(System.Collections.Generic.IEnumerable<int> xs) => string.Join(',', xs);
                    var solved = new int[edge.Length];
                    var slo = new int[edge.Length];
                    var shi = new int[edge.Length];
                    for (int k = 0; k < edge.Length; k++)
                    {
                        solved[k] = edge[k] + delta[k];
                        slo[k] = edge[k] + runLo[k];
                        shi[k] = edge[k] + runHi[k];
                    }
                    File.AppendAllText(tsv,
                        parts[0] + "\t" + (style == "" ? "R" : style) + "\t" + ppem + "\t" + c
                        + "\t" + atStart + "\t" + best
                        + "\t" + gdiAdvance * 64 + "\t" + (int) MathF.Round(font.Advance(gid) * natScale * 64f)
                        + "\t" + L(edge) + "\t" + L(solved) + "\t" + L(slo) + "\t" + L(shi)
                        + "\t" + L(Edges(bi)) + "\t" + L(Edges(natural))
                        + "\t" + new string(System.Linq.Enumerable.ToArray(
                            System.Linq.Enumerable.Select(edge, e =>
                                EdgeKind(fitted).TryGetValue(e, out char kk) ? kk : '?')))
                        + "\n");
                }
            }
            }
            finally { TrueTypeFont.SubpixelFitting = savedSubpix; }
        }

        /// <summary>Sets <see cref="TrueTypeFont.SubpixelFitting"/> for the body of a diagnostic and
        /// puts it back, exception or not.
        /// <para>IT IS A STATIC WITH NO INITIALIZER, SO IT DEFAULTS TO FALSE. A per-glyph probe that
        /// forgets it asks for the BI-LEVEL fit and then compares it against GDI's CLEARTYPE pixels
        /// -- the compatible-width phase is gated on this flag, so it does not even run. That is not
        /// hypothetical: it is why SolveGdisEdges reported Arial Italic 'w' identical with the phase
        /// on and off, and why a long run of per-glyph conclusions disagreed with the oracle.
        /// THIRTEEN of the seventeen probes in this file had the same omission; the ones that
        /// deliberately compare against GGO (which really is the bi-level fit) must NOT be
        /// changed.</para></summary>
        private readonly struct ClearTypeFitScope : IDisposable
        {
            private readonly bool _saved;
            public ClearTypeFitScope(bool on)
            {
                _saved = TrueTypeFont.SubpixelFitting;
                TrueTypeFont.SubpixelFitting = on;
            }
            public void Dispose() => TrueTypeFont.SubpixelFitting = _saved;
        }

        /// <summary>IS THE PER-GLYPH ERROR A DISPLACEMENT OR A SHAPE?
        /// <para>Everything downstream of x-fitting is proved: the rasterizer reproduces GDI's
        /// lamps exactly for a given outline, and the interpreter reproduces GDI's own fitted
        /// points exactly in the one mode GDI can be asked about. So GDI's PIXELS determine GDI's
        /// POINTS, and the open question is what its ClearType-mode points are.</para>
        /// <para>This separates two very different answers cheaply: take OUR fitted outline, slide
        /// it in sixty-fourths of a pixel, and render each offset through our own rasterizer. If
        /// some offset reproduces GDI's pixels, then GDI fitted the same SHAPE and only placed it
        /// differently, and the rule to find is one number per glyph. If none does, GDI's stems sit
        /// differently WITHIN the glyph and no placement rule can ever match it.</para>
        /// <para>The outline is translated here rather than by moving the pen: the pen is rounded
        /// to whole pixels and the atlas rasterizes once per size, so a sub-pixel pen offset is
        /// quietly discarded -- which the first attempt at this did not notice, and it reported the
        /// same error at every offset.</para>
        /// <para>WPF_SHIFTSOLVE=family/chars/ppem[/B|I|BI].</para></summary>
        [Fact]
        public void CanAShiftOfOursReproduceGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_SHIFTSOLVE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_SHIFTSOLVE=family/chars/ppem[/style]");
            // This probe scores our fit against GDI's CLEARTYPE bitmap, so it must ask for the
            // ClearType fit. WPF_SHIFTSOLVE_BILEVEL=1 asks for the old (bi-level) behaviour.
            using var ctFit = new ClearTypeFitScope(
                Environment.GetEnvironmentVariable("WPF_SHIFTSOLVE_BILEVEL") != "1");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            Console.Error.WriteLine($"== {parts[0]} @{ppem}{(style == "" ? "" : "/" + style)}:"
                                    + " can a sub-pixel shift of OUR fitted outline reproduce GDI's?");
            // Candidate predictors, printed beside the answer. A shift that varies per glyph is
            // only useful if something about the glyph predicts it, and these are what a placement
            // rule could plausibly be made of: where the glyph's ink starts before fitting, where
            // it starts after, and how far the fitting moved it.
            Console.Error.WriteLine("   char   at 0/64    best  sum|d|   unfitted  fitted   moved"
                                    + "   verdict");

            var raw = new byte[Width * Height * 4];
            foreach (char c in parts[1])
            {
                int gid = font.GlyphIndex(c);
                if (gid <= 0) continue;
                if (!((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out List<PathFigure> fitted)
                    || fitted.Count == 0)
                {
                    Console.Error.WriteLine($"   {c}    (we do not grid-fit it at this size)");
                    continue;
                }

                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), parts[0], ppem, PenX, 28, Width, Height, bold, italic);
                Gdi.s_rawRgb = null;

                // AND THE SAME GLYPH FITTED THE OTHER WAY. Our BI-LEVEL fitting reproduces GDI's
                // own fitted points exactly -- 4 of 4 for this glyph, in the one mode GDI can be
                // asked about -- so if GDI's ClearType rendering is its bi-level outline, rendering
                // ours should reach GDI's pixels where the ClearType fitting cannot.
                bool savedBi = TrueTypeInterpreter.BiLevelPass;
                List<PathFigure> biFitted;
                try
                {
                    // BEFORE constructing: 'prep' runs once, at construction, and it is prep that
                    // rounds the stem CVTs according to what GETINFO answered. Setting the flag
                    // afterwards leaves those CVTs as the ClearType pass computed them, and the
                    // outline comes back byte-identical -- which is what the first attempt showed.
                    TrueTypeInterpreter.BiLevelPass = true;
                    var biFont = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
                    if (!((IHintedGlyphFont) biFont).TryGetHintedOutline(gid, ppem, out biFitted))
                        biFitted = new List<PathFigure>();
                }
                finally { TrueTypeInterpreter.BiLevelPass = savedBi; }

                long biBest = long.MaxValue;
                int biStep = 0;
                for (int step = -64; step <= 64 && biFitted.Count > 0; step++)
                {
                    List<PathFigure> placed = GlyphRunPainter.ScaleFigures(
                        biFitted, 1f, PenX + step / 64f, 28f);
                    byte[] ours = OursRgbaFromFigures(placed, font);
                    long sum = 0;
                    for (int i = 0; i < Width * Height; i++)
                        for (int ch = 0; ch < 3; ch++)
                            sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                    if (sum < biBest) { biBest = sum; biStep = step; }
                }

                long atZero = -1, best = long.MaxValue;
                int bestStep = 0, firstExact = int.MinValue, lastExact = int.MinValue;
                // WPF_SHIFTSOLVE_COLS=lo,hi confines the comparison to pixel columns lo..hi
                // (from the pen), so one FEATURE of a glyph -- a stem, a bar's end -- can be
                // asked for its own shift, separately from the rest of the glyph.
                int colLo = int.MinValue, colHi = int.MaxValue;
                string? cols = Environment.GetEnvironmentVariable("WPF_SHIFTSOLVE_COLS");
                if (!string.IsNullOrEmpty(cols))
                {
                    string[] lh = cols.Split(',');
                    colLo = PenX + int.Parse(lh[0]);
                    colHi = PenX + int.Parse(lh[1]);
                }
                for (int step = -64; step <= 64; step++)
                {
                    // The fitted outline is in device pixels with the baseline at y=0 and y up,
                    // which is what GlyphRunPainter places with scale 1.
                    List<PathFigure> placed = GlyphRunPainter.ScaleFigures(
                        fitted, 1f, PenX + step / 64f, 28f);
                    byte[] ours = OursRgbaFromFigures(placed, font);
                    long sum = 0;
                    for (int i = 0; i < Width * Height; i++)
                    {
                        int col = i % Width;
                        if (col < colLo || col > colHi) continue;
                        for (int ch = 0; ch < 3; ch++)
                            sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                    }
                    if (step == 0) atZero = sum;
                    if (sum < best) { best = sum; bestStep = step; }
                    // The whole run of exact shifts, not just the first: the edges resolve to a
                    // sixth of a pixel, so the run is ~10 steps wide and its ENDS bound GDI's edge.
                    if (sum == 0) { if (firstExact == int.MinValue) firstExact = step; lastExact = step; }
                }

                // The left extreme of the outline, unfitted and fitted, in pixels.
                float unfittedLeft = float.MaxValue, fittedLeft = float.MaxValue;
                if (font.TryGetGlyphOutline(gid, out List<PathFigure> plain))
                    foreach (PathFigure f in plain)
                    {
                        if (f.Start.X < unfittedLeft) unfittedLeft = f.Start.X;
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment ls && ls.Point.X < unfittedLeft) unfittedLeft = ls.Point.X;
                            else if (sg is QuadraticBezierSegment qs && qs.Point.X < unfittedLeft) unfittedLeft = qs.Point.X;
                    }
                foreach (PathFigure f in fitted)
                {
                    if (f.Start.X < fittedLeft) fittedLeft = f.Start.X;
                    foreach (PathSegment sg in f.Segments)
                        if (sg is LineSegment ls && ls.Point.X < fittedLeft) fittedLeft = ls.Point.X;
                        else if (sg is QuadraticBezierSegment qs && qs.Point.X < fittedLeft) fittedLeft = qs.Point.X;
                }
                // And the RIGHT extreme, so a single-stem glyph's WIDTH can be compared. For a
                // glyph that is one rectangle, width is the only thing "shape" can mean, so where
                // no shift reaches GDI it is the width that differs.
                float unfittedRight = float.MinValue, fittedRight = float.MinValue;
                if (font.TryGetGlyphOutline(gid, out List<PathFigure> plain2))
                    foreach (PathFigure f in plain2)
                    {
                        if (f.Start.X > unfittedRight) unfittedRight = f.Start.X;
                        foreach (PathSegment sg in f.Segments)
                            if (sg is LineSegment ls && ls.Point.X > unfittedRight) unfittedRight = ls.Point.X;
                            else if (sg is QuadraticBezierSegment qs && qs.Point.X > unfittedRight) unfittedRight = qs.Point.X;
                    }
                foreach (PathFigure f in fitted)
                {
                    if (f.Start.X > fittedRight) fittedRight = f.Start.X;
                    foreach (PathSegment sg in f.Segments)
                        if (sg is LineSegment ls && ls.Point.X > fittedRight) fittedRight = ls.Point.X;
                        else if (sg is QuadraticBezierSegment qs && qs.Point.X > fittedRight) fittedRight = qs.Point.X;
                }
                float scale = ppem / (float) font.PixelsPerEm;
                float unfittedPx = unfittedLeft * scale;

                // EACH END ON ITS OWN. A glyph's left feature and right feature can be placed by
                // different rules -- one hangs off the origin phantom, the other off the advance
                // phantom -- so the exact run is also asked of the first three ink columns and the
                // last three, separately. WPF_SHIFTSOLVE_ENDS=1.
                string ends = "";
                if (Environment.GetEnvironmentVariable("WPF_SHIFTSOLVE_ENDS") == "1")
                {
                    (int first, int last, int bestAt) Sweep(int lo, int hi)
                    {
                        int f = int.MinValue, l = int.MinValue, bAt = 0;
                        long b = long.MaxValue;
                        for (int step = -64; step <= 64; step++)
                        {
                            List<PathFigure> placed = GlyphRunPainter.ScaleFigures(
                                fitted, 1f, PenX + step / 64f, 28f);
                            byte[] ours = OursRgbaFromFigures(placed, font);
                            long sum = 0;
                            for (int i = 0; i < Width * Height; i++)
                            {
                                int col = i % Width;
                                if (col < lo || col > hi) continue;
                                for (int ch = 0; ch < 3; ch++)
                                    sum += Math.Abs(raw[i * 4 + (2 - ch)] - ours[i * 4 + ch]);
                            }
                            if (sum < b) { b = sum; bAt = step; }
                            if (sum == 0) { if (f == int.MinValue) f = step; l = step; }
                        }
                        return (f, l, bAt);
                    }
                    int lc = PenX + (int) Math.Floor(fittedLeft), rc = PenX + (int) Math.Floor(fittedRight);
                    var L = Sweep(lc - 1, lc + 2);
                    var R = Sweep(rc - 2, rc + 1);
                    ends = $"   L{(L.first == int.MinValue ? $"~{L.bestAt}" : $"[{L.first},{L.last}]")}"
                         + $" R{(R.first == int.MinValue ? $"~{R.bestAt}" : $"[{R.first},{R.last}]")}";
                }

                string verdict = best == 0 ? "EXACT -- placement alone explains it"
                               : best * 4 < atZero ? "MOSTLY placement"
                               : best < atZero ? "partly placement"
                               : "NOT placement -- the shape differs";
                Console.Error.WriteLine($"   {c}   {atZero,8}  {bestStep,4}/64 {best,8}"
                                        + $"   {unfittedPx,8:0.000} {fittedLeft,7:0.000}"
                                        + $" {fittedLeft - unfittedPx,7:+0.000;-0.000; 0.000}"
                                        // WHERE GDI PUT IT: our fitted left plus the shift that
                                        // reproduces GDI's pixels. Only meaningful when the shift
                                        // actually reaches GDI -- a glyph whose shape differs has
                                        // no single answer -- so it is marked when it is trustworthy.
                                        + $"   w {(unfittedRight - unfittedLeft) * scale,6:0.000}"
                                        + $"->{fittedRight - fittedLeft,6:0.000}"
                                        + $"   gdiLeft {fittedLeft + bestStep / 64f,6:0.000}"
                                        + $"{(best * 8 < atZero ? "*" : " ")}"
                                        + $"   BI-LEVEL {biStep,4}/64 {biBest,8}"
                                        // The exact run in 64ths, the natural (linear) advance and
                                        // GDI's ClearType advance: the placement rule's inputs.
                                        + (firstExact == int.MinValue ? "   exact none"
                                           : $"   exact [{firstExact},{lastExact}]")
                                        + $"   adv {font.Advance(gid) * scale,6:0.000}->"
                                        + $"{Gdi.TextWidth(c.ToString(), parts[0], ppem, bold, italic)}"
                                        + $"   {verdict}{ends}");
            }
        }

        /// <summary>THE WIDTH WE MEASURE A STRING TO BE, against the width GDI measures.
        /// <para>Layout is built on this number, not on the pixels: a tab is sized to its caption,
        /// a label to its text, a column to its header. If it is a pixel out, every edge downstream
        /// of it is a pixel out, and the ink that lands inside can still be perfect -- which is
        /// exactly what the control window shows, where the tab strip's separators sit a column
        /// apart while the captions themselves match.</para>
        /// <para>GDI's answer is GetTextExtentPoint32W, which is what TextRenderer.MeasureText with
        /// NoPadding reports and what a control's own layout asks for.</para>
        /// <para>WPF_WIDTH_REPORT=&lt;path&gt; to collect it.</para></summary>
        [Fact]
        public void TheWidthWeMeasureAString_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_WIDTH_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_WIDTH_REPORT to collect this");

            var report = new System.Text.StringBuilder();
            report.AppendLine("== string width, ours against GDI's GetTextExtentPoint32W");
            report.AppendLine("   face              ppem   text            ours   gdi   delta");

            string[] samples =
            {
                "Shapes", "Text", "Media", "Label", "Button", "CheckBox", "OK", "Cancel",
                "MonthCalendar", "September 2026", "Today: 9/4/2026", "Handgloves mio",
                "iiiii", "WWWWW", "lll", "...", "  ",
                // Single glyphs, to read one advance rather than a sum of them.
                "I", "II", "l", "n", "W",
            };

            foreach (string family in new[] { "Segoe UI", "Tahoma", "Arial" })
                foreach (int ppem in new[] { 12, 16 })
                {
                    string? file = FontFiles.Find(family, bold: false, italic: false);
                    if (file is null) continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    int sfnt = FontFiles.SfntOffset(bytes, family);
                    if (CffFont.IsCff(bytes, sfnt)) continue;
                    var font = new TrueTypeFont(bytes, false, false, sfnt);

                    foreach (string text in samples)
                    {
                        int gdi = Gdi.TextWidth(text, family, ppem);
                        // Ours the way a caller measures it: the shaped advances, on the device
                        // grid the face itself reports, which is what the renderer steps by.
                        var shaped = new List<ShapedGlyph>();
                        new OpenTypeTextShaper().Shape(font, text, shaped);
                        float ours = 0f;
                        foreach (ShapedGlyph g in shaped)
                            ours += font.TryGetDeviceAdvance(g.GlyphId, ppem, out float dev)
                                    ? dev
                                    : MathF.Round(g.Advance * (ppem / (float) font.PixelsPerEm));
                        int mine = (int) MathF.Round(ours);
                        report.AppendLine($"   {family,-16} {ppem,4}   {text,-16} {mine,5} {gdi,5}"
                                          + $"   {mine - gdi,5}");
                    }
                }

            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>WHERE EACH GLYPH LANDS, ours against GDI's, one isolated stem at a time.
        /// <para>The run's first and last inked columns match GDI's exactly, so the advances add up
        /// to the same total -- and yet the ink-weighted centre of the run moves by up to a pixel.
        /// Both can be true at once if individual glyphs sit on different columns INSIDE the run
        /// and the differences cancel by the end, which is what two different rounding rules for a
        /// per-glyph advance would do.</para>
        /// <para>So this asks each glyph separately. Spaced 'l's are the probe because a lone
        /// vertical stem has an unambiguous centre and nothing to overlap with, and its centroid is
        /// read to a hundredth of a pixel rather than eyeballed off a column of lamp values.</para>
        /// <para>WPF_GLYPH_LANDS=family/ppem[/B|I|BI].</para></summary>
        [Fact]
        public void WhereEachGlyphLands_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_GLYPH_LANDS");
            Assert.SkipWhen(string.IsNullOrEmpty(spec),
                            "set WPF_GLYPH_LANDS=family/ppem[/style[/chars]]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[1]);
            string style = parts.Length > 2 ? parts[2].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            // Each glyph SEPARATED, so that a glyph whose ink sits differently can be named.
            // Repeating one letter proved the pen is right; it cannot say which letters are not.
            string letters = parts.Length > 3 ? parts[3] : "llllllllllll";
            string Sample = string.Join(" ", letters.ToCharArray());
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(Sample, parts[0], ppem, PenX, 28, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;
            byte[] ours = OursRgba(font, Sample, ppem, 28, correction: true);

            List<double> mine = Centres(ours), theirs = Centres(raw);
            // AND THE VERTICAL DISTRIBUTION. On a SLANTED glyph the two are not independent: ink
            // at the top sits further right than ink at the bottom, so a glyph that is a row taller
            // than GDI's has an x centroid further right WITHOUT being displaced in x at all. A
            // horizontal measurement alone cannot tell "moved sideways" from "different height",
            // and on an italic those are the two things worth telling apart.
            List<(double Y, int Top, int Bottom)> mineY = Verticals(ours), theirsY = Verticals(raw);
            Console.Error.WriteLine($"== {parts[0]} @{ppem}{(style == "" ? "" : "/" + style)}:"
                                    + $" {theirs.Count} stems from GDI, {mine.Count} from us");
            Console.Error.WriteLine("   char    GDI x      our x     delta        dy   top  bottom");
            for (int i = 0; i < mine.Count && i < theirs.Count; i++)
            {
                string vertical = i < mineY.Count && i < theirsY.Count
                    ? $"   {mineY[i].Y - theirsY[i].Y,+6:+0.00;-0.00; 0.00}"
                      + $"   {mineY[i].Top - theirsY[i].Top,3}   {mineY[i].Bottom - theirsY[i].Bottom,3}"
                    : "";
                Console.Error.WriteLine($"   {(i < letters.Length ? letters[i] : '?'),3}"
                                        + $"   {theirs[i],9:0.000}   {mine[i],8:0.000}"
                                        + $"   {mine[i] - theirs[i],+7:+0.000;-0.000; 0.000}" + vertical);
            }
        }

        /// <summary>The ink-weighted y centre of each column group, and the rows it spans.</summary>
        private static List<(double Y, int Top, int Bottom)> Verticals(byte[] rgba)
        {
            var columns = new double[Width];
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    for (int ch = 0; ch < 3; ch++)
                        columns[x] += 255 - rgba[(y * Width + x) * 4 + ch];

            var result = new List<(double, int, int)>();
            int at = 0;
            while (at < Width)
            {
                if (columns[at] <= 255) { at++; continue; }
                int from = at;
                while (at < Width && columns[at] > 255) at++;
                double weight = 0, moment = 0;
                int top = -1, bottom = -1;
                for (int y = 0; y < Height; y++)
                {
                    double row = 0;
                    for (int x = from; x < at; x++)
                        for (int ch = 0; ch < 3; ch++) row += 255 - rgba[(y * Width + x) * 4 + ch];
                    weight += row; moment += row * y;
                    if (row > 255) { if (top < 0) top = y; bottom = y; }
                }
                if (weight > 0) result.Add((moment / weight, top, bottom));
            }
            return result;
        }

        /// <summary>The ink-weighted x centre of every separated column group.</summary>
        private static List<double> Centres(byte[] rgba)
        {
            var columns = new double[Width];
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    for (int ch = 0; ch < 3; ch++)
                        columns[x] += 255 - rgba[(y * Width + x) * 4 + ch];

            var centres = new List<double>();
            int at = 0;
            while (at < Width)
            {
                // A threshold, not "any ink": a ClearType fringe is not a glyph.
                if (columns[at] <= 255) { at++; continue; }
                int from = at;
                while (at < Width && columns[at] > 255) at++;
                double weight = 0, moment = 0;
                for (int x = from; x < at; x++) { weight += columns[x]; moment += columns[x] * x; }
                if (weight > 0) centres.Add(moment / weight);
            }
            return centres;
        }

        /// <summary>ONE SCANLINE THROUGH ONE STEM, ours against GDI's, as RGB triples.
        /// <para>At a size the face declines to grid-fit there is no interpreter in the way: the
        /// outline is the scaled outline, which we already know matches GDI's exactly. So whatever
        /// disagrees at 8ppem is the RASTERIZER, THE FILTER AND THE CURVE and nothing else -- the
        /// cleanest view of the ClearType stage available, with the hinting confound removed.</para>
        /// <para>It prints values rather than a score because the question is the SHAPE of the
        /// disagreement: a stem one lamp too far left, one lamp too wide, or the same lamps at
        /// different heights are three different bugs and one number cannot tell them apart. And
        /// the ink is NOT the thing to match -- raising the contrast until the ink agrees leaves
        /// the differing-pixel count exactly where it was.</para>
        /// <para>WPF_SCANLINE=family/char/ppem.</para></summary>
        [Fact]
        public void OneScanlineThroughAStem_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_SCANLINE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_SCANLINE=family/char/ppem");
            string[] parts = spec!.Split('/');
            Assert.True(parts.Length is 3 or 4, "WPF_SCANLINE=family/char/ppem[/B|I|BI]");
            int ppem = int.Parse(parts[2]);
            // The style, because the worst rows in the specimen are ITALIC on the faces that are
            // not Segoe UI and a probe that can only ask about the regular weight cannot see them.
            string style = parts.Length == 4 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            // The file may not declare the style asked for, in which case the face is synthesised
            // -- the same choice the renderer makes, so that the two sides are the same glyph.
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(parts[1], parts[0], ppem, PenX, 28, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;
            byte[] ours = OursRgba(font, parts[1], ppem, 28, correction: true);

            // The row that departs furthest FROM THE PAPER -- not the row with the most "ink".
            // Ink is 255 minus the value, so on a dark background every background row scores
            // higher than the text does and the probe lands on row 0, which is exactly the case
            // the Ink/Paper knobs exist to measure. Distance from the paper colour works for
            // either polarity.
            int paperR = (int) ((Paper >> 16) & 0xFF), paperG = (int) ((Paper >> 8) & 0xFF);
            int paperB = (int) (Paper & 0xFF);
            int best = 0;
            long bestInk = -1;
            for (int y = 0; y < Height; y++)
            {
                long away = 0;
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    away += Math.Abs(raw[i + 2] - paperR) + Math.Abs(raw[i + 1] - paperG)
                            + Math.Abs(raw[i] - paperB);
                }
                if (away > bestInk) { bestInk = away; best = y; }
            }

            Console.Error.WriteLine($"== {parts[0]} '{parts[1]}' @{ppem}{(style == "" ? "" : "/" + style)}, row {best}"
                                    + $" (grid-fit: {font.WantsGridFit(ppem)})");
            // SEVERAL ROWS, not one. A feature can be absent from a row because it is thin and
            // we drew it lightly, or because it is THERE AND ONE ROW UP -- and a single scanline
            // cannot tell those apart. It read as a sampling problem until the rows either side
            // showed the ink sitting a row away.
            Console.Error.WriteLine("   y   x     GDI  (r,g,b)      ours (r,g,b)      delta");
            for (int y = Math.Max(0, best - 2); y <= Math.Min(Height - 1, best + 2); y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                // The DIB is BGRA and ours is RGBA: read each in its own order, which is the
                // channel-order trap that once reported a control glyph as 336 lamps wrong.
                int gr = raw[i + 2], gg = raw[i + 1], gb = raw[i];
                int orr = ours[i], og = ours[i + 1], ob = ours[i + 2];
                if (gr == paperR && gg == paperG && gb == paperB
                    && orr == paperR && og == paperG && ob == paperB)
                    continue;
                Console.Error.WriteLine($"   {y,3} {x,3}   {gr,3} {gg,3} {gb,3}       {orr,3} {og,3} {ob,3}"
                                        + $"      {orr - gr,4} {og - gg,4} {ob - gb,4}");
            }
        }

        /// <summary>EVERY ROW OF ONE GLYPH: where the ink sits, ours against GDI's.
        /// <para>A slanted stem crosses each pixel row at a different x, and the scanline probe
        /// sees one row. The italics are the worst faces left in the specimen (Verdana Italic
        /// alone is an eighth of it) and their error sits on the ascenders and the diagonals --
        /// features that live in x DIFFERENTLY on every row. Per row: the centroid of each side's
        /// ink in lamps, the total ink, and the difference, so a stem that leans by another slope,
        /// starts a row higher, or is simply heavier can be told apart.</para>
        /// <para>WPF_ROWPROFILE=family/char/ppem[/B|I|BI].</para></summary>
        [Fact]
        public void EveryRowOfAGlyph_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_ROWPROFILE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_ROWPROFILE=family/char/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length == 4 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, parts[0], bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(parts[1], parts[0], ppem, PenX, 28, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;
            byte[] ours = OursRgba(font, parts[1], ppem, 28, correction: true);

            var log = new System.Text.StringBuilder();
            log.AppendLine($"== {parts[0]} '{parts[1]}' @{ppem}{(style == "" ? "" : "/" + style)} (grid-fit: {font.WantsGridFit(ppem)})");
            log.AppendLine("   y    gdi centre  ours centre   delta(lamps)   gdi ink  ours ink   |d|");
            long total = 0;
            for (int y = 0; y < Height; y++)
            {
                double gw = 0, gm = 0, ow = 0, om = 0; long d = 0;
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        // The DIB is BGRA, ours RGBA. Lamp positions: r = 3x, g = 3x + 1, b = 3x + 2.
                        int g = 255 - raw[i + (2 - ch)], o = 255 - ours[i + ch];
                        gw += g; gm += g * (3 * x + ch); ow += o; om += o * (3 * x + ch);
                        d += Math.Abs(g - o);
                    }
                }
                total += d;
                if (gw == 0 && ow == 0) continue;
                string gc = gw > 0 ? $"{gm / gw,10:0.00}" : "         -";
                string oc = ow > 0 ? $"{om / ow,10:0.00}" : "         -";
                string dc = gw > 0 && ow > 0 ? $"{om / ow - gm / gw,8:+0.00;-0.00}" : "       -";
                log.AppendLine($"  {y,3}  {gc}   {oc}    {dc}       {gw / 255,7:0.00}  {ow / 255,7:0.00}  {d,6}");
            }
            log.AppendLine($"  total |d| {total}");
            Console.Error.WriteLine(log.ToString());
        }

        /// <summary>HOW OUR WEIGHT TRACKS GDI'S, by size and by boldness.
        /// <para>The specimen is tracked as one absolute total, which cannot be compared across
        /// sizes: a 40ppem line has five times the ink of a 10ppem one, so the same absolute error
        /// is a far smaller disagreement. Measured against the ink Windows actually puts down, the
        /// whole remaining Latin problem is SMALL SIZES -- 11.9% at 10-12ppem against 3.4% at 40 --
        /// and within a size it is THIN STEMS: at 12ppem the bold faces disagree by half as much as
        /// the regular ones, across all six faces.</para>
        /// <para>That is the shape of the residual, so it is worth a report that does not need the
        /// specimen app: ink ratio and differing pixels per face, per weight, per size. A stem two
        /// pixels wide is placed by rounding and a stem one pixel wide is placed by the filter,
        /// which is why the two weights answer differently.</para>
        /// <para>WPF_WEIGHT_REPORT=&lt;path&gt; to collect it.</para></summary>
        private static int[] WeightSizes =>
            Environment.GetEnvironmentVariable("WPF_WEIGHT_SIZES") is { Length: > 0 } ws
                ? Array.ConvertAll(ws.Split(','), int.Parse)
                : new[] { 8, 10, 12, 16, 24 };

        [Fact]
        public void HowOurWeightTracksGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_WEIGHT_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_WEIGHT_REPORT to collect this");

            // THE SPECIMEN, IN PROCESS. The six faces and four styles the text specimen draws,
            // over its sizes -- but rendered offscreen against GDI's in-memory DIB instead of
            // grabbed off the screen. That matters beyond convenience: the screen harness needs an
            // interactive desktop, and when the session stops being capturable it returns two BLACK
            // images and a difference of ZERO, which reads as perfect parity. This cannot do that.
            // WPF_WEIGHT_SAMPLE overrides the text -- to score one word, or one letter.
            string Sample = Environment.GetEnvironmentVariable("WPF_WEIGHT_SAMPLE") is { Length: > 0 } ws
                ? ws : "abcdefghijklmnopqrstuvwxyz 0123456789 AKNRWXYZkvwxyz";
            var report = new System.Text.StringBuilder();
            report.AppendLine("== ink against GDI's, by face, weight and size: \"" + Sample + "\"");
            report.AppendLine("   face              wt   ppem   our ink   gdi ink   ratio   differ"
                              + "    sum|d|   centroid dx   edge deltas");
            // WIDE ENOUGH FOR THE WHOLE SAMPLE, which 460 is not. The specimen string is fifty
            // glyphs; at 18ppem and up it runs off the end of the ordinary bitmap and the row was
            // silently scoring a TRUNCATED run -- nothing lost at 17ppem, 2,646 at 18, 4,588 at 20,
            // 4,390 at 24 for Arial's roman alone, which is how "Arial gets worse at large sizes"
            // came to be written down from data that was measuring fewer glyphs than it thought.
            // Proved by splitting the sample into its three groups and summing: the parts agree
            // with the whole exactly up to 17ppem and exceed it above.
            const int SpecimenWidth = 1400;
            var raw = new byte[SpecimenWidth * Height * 4];
            long grandTotal = 0;
            int rowCount = 0;

            // WPF_WEIGHT_FACES=Times New Roman|I narrows the run to one face, optionally one
            // style, so a single row can be re-scored in a second instead of a minute. Diagnosis
            // only -- the number that counts is still the whole specimen.
            string[] only = (Environment.GetEnvironmentVariable("WPF_WEIGHT_FACES") ?? "").Split('|');
            string onlyFace = only[0], onlyStyle = only.Length > 1 ? only[1].ToUpperInvariant() : "";
            foreach (string family in new[]
                     { "Segoe UI", "Arial", "Times New Roman", "Verdana", "Tahoma", "Consolas" })
                foreach ((bool bold, bool italic) in
                     new[] { (false, false), (true, false), (false, true) })
                    // WPF_WEIGHT_SIZES overrides these. The five are the specimen's, and they are
                    // a BLIND SPOT as well as a sample: a rule can be worth a quarter of a million
                    // over them and cost pixels at 9, 11 and 13, where nothing in this report would
                    // ever see it. The per-glyph parity ratchets do -- which is how the half-pixel
                    // SHPIX cap was caught -- and this is so the question can be asked here first.
                    foreach (int ppem in WeightSizes)
                    {
                        if (onlyFace.Length > 0 && family != onlyFace) continue;
                        if (onlyStyle.Length > 0
                            && onlyStyle != (bold ? "B" : italic ? "I" : "R")) continue;
                        string? file = FontFiles.Find(family, bold, italic);
                        if (file is null) continue;
                        byte[] bytes = File.ReadAllBytes(file);
                        int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
                        if (CffFont.IsCff(bytes, sfnt)) continue;
                        FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
                        var font = new TrueTypeFont(bytes, bold && !fileBold,
                                                    italic && !fileItalic, sfnt);

                        Gdi.s_rawRgb = raw;
                        Gdi.Draw(Sample, family, ppem, PenX, 28, SpecimenWidth, Height, bold, italic);
                        Gdi.s_rawRgb = null;
                        byte[] ours = OursRgba(font, Sample, ppem, 28, correction: true,
                                               widthOverride: SpecimenWidth);

                        long theirs = 0, mine = 0;
                        int differ = 0;
                        // The ink-weighted x CENTROID of each side. Reading a displacement off a
                        // column of lamp values is guesswork -- Times italic looked like one to two
                        // lamps by eye, and by eye is not a measurement. A centroid is one number
                        // per side and it is what says whether a face sits where GDI puts it.
                        double sx = 0, tx = 0;
                        long sumd = 0;
                        for (int i = 0; i < SpecimenWidth * Height; i++)
                        {
                            bool any = false;
                            int x = i % SpecimenWidth;
                            for (int ch = 0; ch < 3; ch++)
                            {
                                int t = 255 - raw[i * 4 + ch], m = 255 - ours[i * 4 + ch];
                                theirs += t; mine += m;
                                tx += t * (double) x; sx += m * (double) x;
                                // MAGNITUDE, not just a count. A count of "any channel differs"
                                // saturates: white text on blue reported 25,722 differing pixels
                                // against ~420 for black on white, and every one of them was off
                                // by one or two. The sum says which of those is actually worse.
                                // CHANNEL ORDER. The DIB is BGRA and ours is RGBA, so comparing
                                // index to index subtracts GDI's blue from our red. Summing 255-v
                                // over all three channels does not care -- which is why the ink and
                                // the centroid were right -- but a per-channel difference does, and
                                // on a coloured ground it is catastrophic: white on blue reported
                                // 25,722 differing pixels and a sum|d| of 11 million, both of them
                                // the background being compared with itself in the wrong order.
                                int gdi = raw[i * 4 + (2 - ch)];
                                sumd += Math.Abs(gdi - ours[i * 4 + ch]);
                                if (gdi != ours[i * 4 + ch]) any = true;
                            }
                            if (any) differ++;
                        }
                        double dx = (mine > 0 ? sx / mine : 0) - (theirs > 0 ? tx / theirs : 0);

                        // WHERE THE RUN STARTS AND WHERE IT ENDS, on each side. A centroid that
                        // has moved says the run is not where GDI's is; it cannot say whether the
                        // pen started somewhere else or the ADVANCES accumulated differently, and
                        // those are different bugs. Edges separate them: a matching left edge with
                        // a moved right edge is width, both moved together is the pen.
                        int ourL = -1, ourR = -1, gdiL = -1, gdiR = -1;
                        // WPF_WEIGHT_COLUMNS=Family|R|ppem prints this row's ink, column by column,
                        // as runs: where each side's ink starts and stops. An edge delta says the
                        // line came out a different length; the runs say WHICH glyph moved.
                        bool columns = Environment.GetEnvironmentVariable("WPF_WEIGHT_COLUMNS")
                                       == $"{family}|{(bold ? "B" : italic ? "I" : "R")}|{ppem}";
                        var oRuns = new System.Text.StringBuilder("      ours runs:");
                        var gRuns = new System.Text.StringBuilder("      gdi  runs:");
                        bool oIn = false, gIn = false;
                        for (int x = 0; x < SpecimenWidth; x++)
                        {
                            long o = 0, g = 0;
                            for (int y = 0; y < Height; y++)
                                for (int ch = 0; ch < 3; ch++)
                                {
                                    o += 255 - ours[(y * SpecimenWidth + x) * 4 + ch];
                                    g += 255 - raw[(y * SpecimenWidth + x) * 4 + ch];
                                }
                            // A threshold, not "any ink": a single ClearType fringe lamp is not an
                            // edge, and reading one as an edge is a trap this suite has hit before.
                            if (o > 255) { if (ourL < 0) ourL = x; ourR = x; }
                            if (g > 255) { if (gdiL < 0) gdiL = x; gdiR = x; }
                            if (columns)
                            {
                                if (o > 255 && !oIn) { oRuns.Append($" {x}"); oIn = true; }
                                if (o <= 255 && oIn) { oRuns.Append($"-{x - 1}"); oIn = false; }
                                if (g > 255 && !gIn) { gRuns.Append($" {x}"); gIn = true; }
                                if (g <= 255 && gIn) { gRuns.Append($"-{x - 1}"); gIn = false; }
                            }
                        }
                        if (columns) { report.AppendLine(oRuns.ToString()); report.AppendLine(gRuns.ToString()); }
                        grandTotal += sumd;
                        rowCount++;
                        report.AppendLine($"   {family,-16} {(bold ? "B" : italic ? "I" : "R")}   {ppem,4}"
                                          + $" {mine,9} {theirs,9}"
                                          + $"   {(theirs == 0 ? 0 : mine / (double) theirs),5:0.000}"
                                          + $"   {differ,6} {sumd,9}   {dx,6:+0.000;-0.000; 0.000}"
                                          + $"   L{ourL - gdiL,3} R{ourR - gdiR,3}"
                                          + $"   width {(ourR - ourL) - (gdiR - gdiL),3}");

                        // WPF_WEIGHT_PERGLYPH=1: the same row, one glyph at a time, so a change
                        // that helps one letter and hurts another can be told apart from one that
                        // helps the face. A face total is the sum of sixty decisions.
                        if (Environment.GetEnvironmentVariable("WPF_WEIGHT_PERGLYPH") == "1")
                            foreach (char c in System.Linq.Enumerable.Distinct(Sample))
                            {
                                if (c == ' ') continue;
                                string one = c.ToString();
                                Gdi.s_rawRgb = raw;
                                Gdi.Draw(one, family, ppem, PenX, 28, SpecimenWidth, Height, bold, italic);
                                Gdi.s_rawRgb = null;
                                byte[] oneOurs = OursRgba(font, one, ppem, 28, correction: true,
                                                          widthOverride: SpecimenWidth);
                                long d1 = 0;
                                for (int i = 0; i < SpecimenWidth * Height; i++)
                                    for (int ch = 0; ch < 3; ch++)
                                        d1 += Math.Abs(raw[i * 4 + (2 - ch)] - oneOurs[i * 4 + ch]);
                                report.AppendLine($"      glyph {family}|{(bold ? "B" : italic ? "I" : "R")}|{ppem}|{c} {d1}");
                            }
                    }

            TrueTypeInterpreter.DumpMirpCensus();
            report.AppendLine($"   TOTAL over {rowCount} rows: sum|d| {grandTotal}");
            Console.Error.WriteLine($"IN-PROCESS SPECIMEN: {grandTotal} over {rowCount} rows");
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>DOES GDI REORDER? The question this suite's oracle cannot answer about itself.
        /// <para>The oracle everywhere else is ExtTextOutW, which shapes a complex script but does
        /// NOT apply the bidirectional algorithm -- that is DrawTextW's job, and DrawText is what
        /// stock WinForms draws its labels through. So "our Arabic agrees with the oracle" leaves
        /// the product question open: a word laid out left to right has exactly the ink of the same
        /// word laid out right to left, and every measurement here is made on ink.</para>
        /// <para>A COLUMN INK PROFILE settles it. Reordering mirrors the profile, so comparing
        /// DrawText's profile against ExtTextOut's -- and against ExtTextOut's REVERSED -- says
        /// which way round DrawText put the letters, with no dependence on where either API chose
        /// to put the baseline. Latin is the control: it must match forwards.</para></summary>
        [Fact]
        public void DoesGdiReorderRightToLeftText()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            // A GUARD, not a report: the numbers are decisive rather than marginal, so this can
            // assert. WPF_RTL_REPORT only decides whether the working is written down.
            string? path = Environment.GetEnvironmentVariable("WPF_RTL_REPORT");

            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no probe face");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {ProbeFamily()} at 16ppem: ExtTextOutW against DrawTextW");
            report.AppendLine("   word                     forward err   reversed err   verdict");
            (string text, string name)[] cases =
            {
                ("Wave", "latin control"),
                ("\u0628\u062A\u062B", "arabic beh teh theh"),
                ("\u0627\u0644\u0639\u0631\u0628\u064A\u0629", "arabic al-arabiyya"),
                ("\u05D0\u05D1\u05D2", "hebrew alef bet gimel"),
            };
            foreach ((string text, string name) in cases)
            {
                double[] eto = Profile(text, useDrawText: false);
                s_lastInk[0] = s_ink; s_lastSpan[0] = s_span;
                double[] dt = Profile(text, useDrawText: true);
                s_lastInk[1] = s_ink; s_lastSpan[1] = s_span;
                // And the same call as a RightToLeft control makes it.
                s_rtl = true;
                double[] rtl = Profile(text, useDrawText: true);
                s_rtl = false;
                var rtlReversed = (double[]) rtl.Clone();
                Array.Reverse(rtlReversed);
                double rtlForward = Distance(eto, rtl), rtlBackward = Distance(eto, rtlReversed);

                // And OURS against GDI's, which is the question that matters: the two GDI APIs
                // agreeing tells us what GDI does, not whether we do the same.
                double[] ours = OurProfile(font, text);
                var oursReversed = (double[]) ours.Clone();
                Array.Reverse(oursReversed);
                double ourForward = Distance(eto, ours), ourBackward = Distance(eto, oursReversed);
                var reversed = (double[]) dt.Clone();
                Array.Reverse(reversed);   // realigned by the shift search below
                double forward = Distance(eto, dt), backward = Distance(eto, reversed);
                report.AppendLine($"   {name,-22} {forward,12:0.000}   {backward,12:0.000}   "
                                  + (forward <= backward ? "same order" : "GDI REORDERS")
                                  + $"   | DT_RTLREADING {rtlForward,6:0.000} vs {rtlBackward,6:0.000} "
                                  + (rtlForward <= rtlBackward ? "same order" : "REORDERS")
                                  + $"   | OURS {ourForward,6:0.000} vs {ourBackward,6:0.000} "
                                  + (ourForward <= ourBackward ? "agrees" : "OUR ORDER IS WRONG"));

                Assert.True(forward < backward,
                            $"GDI's two APIs disagree about the order of '{name}' -- DrawTextW"
                            + $" reorders where ExtTextOutW does not ({forward:0.000} forward,"
                            + $" {backward:0.000} reversed), so the oracle this suite uses"
                            + " everywhere else is the wrong one for this text.");
                Assert.True(ourForward < ourBackward,
                            $"we lay '{name}' out in the opposite order to GDI"
                            + $" ({ourForward:0.000} forward, {ourBackward:0.000} reversed)");
            }
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>What share of GDI's inked pixels are neither black nor white.
        /// <para>A face with EMBEDDED BITMAP STRIKES is drawn from them by GDI at the sizes they
        /// cover, and a 1-bit strike has no intermediate values at all. So this separates "GDI
        /// antialiased an outline" from "GDI blitted a bitmap", which no ink ratio can.</para>
        /// </summary>
        private static int Greys(byte[] raw)
        {
            int inked = 0, grey = 0;
            for (int i = 0; i < Width * Height; i++)
            {
                int b = raw[i * 4], g = raw[i * 4 + 1], r = raw[i * 4 + 2];
                if (r == 255 && g == 255 && b == 255) continue;
                inked++;
                if (r != 0 || g != 0 || b != 0) grey++;
            }
            return inked == 0 ? 0 : grey * 100 / inked;
        }

        /// <summary>What share of the inked pixels are SATURATED -- far from grey.
        /// <para>ClearType fringes are coloured too, but only slightly and only at edges. A big
        /// share here means colour artwork: the renderer drew an emoji rather than a letter.</para>
        /// </summary>
        private static int Colour(byte[] raw)
        {
            int inked = 0, coloured = 0;
            for (int i = 0; i < Width * Height; i++)
            {
                int b = raw[i * 4], g = raw[i * 4 + 1], r = raw[i * 4 + 2];
                if (r == 255 && g == 255 && b == 255) continue;
                inked++;
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max - min > 60) coloured++;
            }
            return inked == 0 ? 0 : coloured * 100 / inked;
        }

        /// <summary>The face the renderer will link to for this text, or the requested one when
        /// it copes -- the same choice EmitText makes.</summary>
        internal static string s_drawnBy = "";

        private static TrueTypeFont FaceThatDraws(TrueTypeFont requested, string text)
        {
            s_drawnBy = ProbeFamily();
            char needed = ' ';
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                if (requested.GlyphIndex(c) > 0) return requested;
                if (needed == ' ') needed = c;
            }
            if (needed == ' ') return requested;
            foreach (string family in FontFiles.LinkCandidates(ProbeFamily(), needed))
            {
                string? file = FontFiles.Find(family, bold: false, italic: false);
                if (file is null) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch (IOException) { continue; }
                int sfnt = FontFiles.SfntOffset(bytes);
                if (CffFont.IsCff(bytes, sfnt)) continue;
                TrueTypeFont candidate;
                try { candidate = new TrueTypeFont(bytes, false, false, sfnt); }
                catch (Exception) { continue; }
                if (candidate.GlyphIndex(needed) > 0) { s_drawnBy = family; return candidate; }
            }
            return requested;
        }

        /// <summary>The same column profile, for what WE draw.</summary>
        private double[] OurProfile(TrueTypeFont font, string text)
        {
            byte[] rgba = OursRgba(font, text, 16, 28, correction: true);
            var columns = new double[Width];
            double total = 0;
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double ink = 255 - rgba[(y * Width + x) * 4 + ch];
                        columns[x] += ink;
                        total += ink;
                    }
            if (total > 0) for (int x = 0; x < Width; x++) columns[x] /= total;
            return columns;
        }

        /// <summary>Ink per column, normalised, so two renderings can
        /// be compared without agreeing about where the text begins.</summary>
        private static readonly double[] s_lastInk = new double[2];
        private static readonly string[] s_lastSpan = new string[2];
        private static double s_ink;
        private static bool s_rtl;
        private static string s_span = "";

        private static double[] Profile(string text, bool useDrawText)
        {
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.s_useDrawText = useDrawText;
            Gdi.s_rtlReading = s_rtl;
            Gdi.Draw(text, ProbeFamily(), 16, PenX, 28, Width, Height, false, false);
            Gdi.s_useDrawText = false;
            Gdi.s_rawRgb = null;

            var columns = new double[Width];
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    for (int ch = 0; ch < 3; ch++)
                        columns[x] += 255 - raw[(y * Width + x) * 4 + ch];

            s_ink = 0;
            foreach (double c in columns) s_ink += c;
            int first = 0, last = Width - 1;
            while (first < Width && columns[first] < 1) first++;
            while (last > first && columns[last] < 1) last--;
            s_span = $"{first}..{last}";
            if (s_ink > 0) for (int x = 0; x < Width; x++) columns[x] /= s_ink;
            return columns;
        }

        /// <summary>Distance at the best rigid shift.
        /// <para>Fixed-width bins made this instrument fail its own control: GDI's two APIs end the
        /// Latin word one pixel apart, every bin boundary moved, and the error came out at 0.996
        /// where the answer is "the same". A shift is not a reordering, so search it out first.
        /// </para></summary>
        private static double Distance(double[] a, double[] b)
        {
            double best = double.MaxValue;
            for (int shift = -8; shift <= 8; shift++)
            {
                double sum = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    int j = i + shift;
                    sum += Math.Abs(a[i] - (j >= 0 && j < b.Length ? b[j] : 0));
                }
                if (sum < best) best = sum;
            }
            return best;
        }

        /// <summary>CHARACTERS THE REQUESTED FACE DOES NOT HAVE.
        /// <para>Everything measured in this file is Latin, drawn in a face that has it. GDI does
        /// not stop at the requested face: for a character it lacks, it font-links to one that has
        /// it, which is why a Chinese word in a Segoe UI label comes out as Chinese and not as a row
        /// of boxes. The WinForms path here has no such step -- WPF's own text stack has
        /// TypefaceMap, the GDI+ one has nothing -- so the question is what we actually draw.</para>
        /// <para>Reports, per character: whether the face has a glyph for it at all, and how much
        /// ink each renderer puts down. Ink near zero on our side against real ink on GDI's is a
        /// character we are dropping; similar ink is a character the face had after all.</para>
        /// <para>WPF_FALLBACK_REPORT=&lt;path&gt; to collect it.</para></summary>
        [Fact]
        public void CharactersTheFaceLacks_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_FALLBACK_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_FALLBACK_REPORT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no probe face");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {ProbeFamily()} at 16ppem: characters the face may not have");
            report.AppendLine("   char        glyph?      our ink   windows ink   verdict");
            (char c, string name)[] cases =
            {
                ('A', "latin A"), ('é', "e acute"), ('Ж', "cyrillic ZHE"),
                ('α', "greek alpha"), ('中', "cjk zhong"), ('あ', "hiragana a"),
                ('한', "hangul han"), ('א', "hebrew alef"), ('ا', "arabic alef"),
                ('☃', "snowman"), ('€', "euro"), ('→', "right arrow"),
            };
            var raw = new byte[Width * Height * 4];
            foreach ((char c, string name) in cases)
            {
                int gid = font.GlyphIndex(c);
                Gdi.s_rawRgb = raw;
                Gdi.Draw(c.ToString(), ProbeFamily(), 16, PenX, 28, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                long theirs = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch = 0; ch < 3; ch++) theirs += 255 - raw[i * 4 + ch];
                byte[] ours = OursRgba(font, c.ToString(), 16, 28, correction: true);
                long mine = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch = 0; ch < 3; ch++) mine += 255 - ours[i * 4 + ch];

                // WHICH FACE WOULD BE LINKED TO. The renderer substitutes for the whole run
                // when the requested face has none of its characters, so the useful thing to
                // report is whether a candidate exists and which one -- the ink columns only
                // say that OUR box and GDI's glyph differ.
                string linked = "-";
                if (gid <= 0)
                    foreach (string cand in FontFiles.LinkCandidates())
                    {
                        string? cf = FontFiles.Find(cand, false, false);
                        if (cf is null) continue;
                        try
                        {
                            byte[] cb = File.ReadAllBytes(cf);
                            int so = FontFiles.SfntOffset(cb);
                            if (CffFont.IsCff(cb, so)) continue;
                            if (new TrueTypeFont(cb, false, false, so).GlyphIndex(c) > 0)
                            { linked = cand; break; }
                        }
                        catch (Exception) { }
                    }
                string verdict = gid <= 0 ? (linked == "-" ? "DROPPED, no candidate" : "links to " + linked)
                               : theirs < 200 ? "neither draws it"
                               : "the face has it";
                report.AppendLine($"   U+{(int) c:X4} {name,-12} {(gid > 0 ? "yes" : "NO "),-6}"
                                  + $"{mine,10}   {theirs,11}   {verdict}");
            }
            // AND WHOLE WORDS, because a script is more than its letters. Arabic letters
            // change shape with their neighbours and Devanagari reorders them, and this shaper is
            // one glyph per character plus kerning -- no substitution and no reordering -- so a
            // word is where that shows and a single letter is not.
            (string text, string name)[] words =
            {
                ("Wave", "latin word"),
                ("بتث", "arabic beh teh theh"),
                ("العربية", "arabic al-arabiyya"),
                ("हिन्दी", "devanagari hindi"),
                // Reph: an initial ra+virama is drawn as a mark ABOVE the end of the cluster,
                // which is the other reordering Indic needs and a different code path from the
                // pre-base matra.
                ("कर्म", "devanagari karma (reph)"),
                ("स्त्री", "devanagari stri (3 conjunct)"),
                // The other Indic scripts, to find out whether this generalises or was fitted to
                // the one script that was measured.
                ("বাংলা", "bengali bangla"),
                ("தமிழ்", "tamil tamizh"),
                ("తెలుగు", "telugu telugu"),
                ("ಕನ್ನಡ", "kannada kannada"),
                ("ગુજરાતી", "gujarati gujarati"),
                ("മലയാളം", "malayalam malayalam"),
                ("中文", "chinese zhongwen"),
                ("あいう", "kana aiu"),
                ("日本語", "japanese nihongo"),
                ("カタカナ", "katakana"),
                ("☃", "snowman"),
                ("☺", "smiling face"),
                // Lam-alef: the one Arabic pair with no unjoined spelling, so it exercises the
                // LIGATURE path rather than the positional one. Two glyphs must become one.
                ("لا", "arabic lam-alef"),
                ("الله", "arabic allah"),
            };
            // The face that actually DRAWS the non-Latin samples is the linked one, not the
            // probe family -- reporting the probe's tables for them measures the wrong font.
            report.AppendLine("   SystemLink[" + ProbeFamily() + "] = "
                              + string.Join(" | ", FontFiles.SystemLink(ProbeFamily())));
            // WHICH FACE DID GDI USE? Asked by reproduction rather than by classifying pixels:
            // render the character in every installed face that has it and see which one matches.
            // A metric that tries to tell "colour artwork" from "text" fails on ClearType, whose
            // fringes are colourful too -- Latin scored 76% on exactly such a test.
            foreach (char probe in new[] { '☃', '☺' })
            {
                var raw0 = new byte[Width * Height * 4];
                Gdi.s_rawRgb = raw0;
                Gdi.Draw(probe.ToString(), ProbeFamily(), 16, PenX, 28, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                long theirInk = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch = 0; ch < 3; ch++) theirInk += 255 - raw0[i * 4 + ch];
                report.AppendLine($"   U+{(int) probe:X4}: GDI ink {theirInk}. Which face reproduces it?");
                foreach (string family in FontFiles.ScannedFamilyNames())
                {
                    string? f = FontFiles.Find(family, false, false);
                    if (f is null) continue;
                    byte[] fb;
                    try { fb = File.ReadAllBytes(f); } catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    int off = FontFiles.SfntOffset(fb, family);
                    if (CffFont.IsCff(fb, off)) continue;
                    TrueTypeFont candidate;
                    try { candidate = new TrueTypeFont(fb, false, false, off); }
                    catch (Exception) { continue; }
                    if (candidate.GlyphIndex(probe) <= 0) continue;
                    byte[] pix = OursRgba(candidate, probe.ToString(), 16, 28, correction: true);
                    int differ = 0;
                    long ink = 0;
                    for (int i = 0; i < Width * Height; i++)
                    {
                        bool any = false;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            ink += 255 - pix[i * 4 + ch];
                            if (raw0[i * 4 + (2 - ch)] != pix[i * 4 + ch]) any = true;
                        }
                        if (any) differ++;
                    }
                    // No filter on the emoji faces: the question is what GDI's ink of 69,233
                    // could possibly be, and every monochrome candidate is a third of it.
                    if (differ < 400 || family.Contains("Emoji") || family.Contains("Symbol"))
                        report.AppendLine($"      {family,-28} ink {ink,7} differ {differ,5}"
                                          + $" colour {Colour(pix)}%");
                }
            }

            foreach (string linked in new[] { "Nirmala UI", "Microsoft YaHei" })
            {
                string? lf = FontFiles.Find(linked, bold: false, italic: false);
                if (lf is null) continue;
                byte[] lbytes = File.ReadAllBytes(lf);
                if (CffFont.IsCff(lbytes, FontFiles.SfntOffset(lbytes))) continue;
                var lfont = new TrueTypeFont(lbytes, false, false, FontFiles.SfntOffset(lbytes));
                if (lfont.Gsub is not GsubTable lg) { report.AppendLine($"   {linked}: no GSUB"); continue; }
                report.AppendLine($"   {linked} scripts: " + string.Join(" ", lg.Scripts()));
                foreach (string sc in new[] { "knd2", "tml2" })
                    foreach (string f in lg.Features(sc))
                        report.AppendLine($"      {sc}/{f}: types "
                                          + string.Join(",", lg.LookupTypes(sc, f)));
            }
            if (font.Gsub is GsubTable g0)
            {
                report.AppendLine("   scripts: " + string.Join(" ", g0.Scripts()));
                report.AppendLine("   arab features: " + string.Join(" ", g0.Features("arab")));
                foreach (string f in g0.Features("arab"))
                    report.AppendLine($"      {f}: lookup types "
                                      + string.Join(",", g0.LookupTypes("arab", f)));
            }
            report.AppendLine("   word                        our ink   windows ink   ratio"
                              + "   differing px");
            foreach ((string text, string name) in words)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(text, ProbeFamily(), 16, PenX, 28, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                long theirs2 = 0;
                for (int i2 = 0; i2 < Width * Height; i2++)
                    for (int ch = 0; ch < 3; ch++) theirs2 += 255 - raw[i2 * 4 + ch];
                byte[] ours2 = OursRgba(font, text, 16, 28, correction: true);
                long mine2 = 0;
                for (int i2 = 0; i2 < Width * Height; i2++)
                    for (int ch = 0; ch < 3; ch++) mine2 += 255 - ours2[i2 * 4 + ch];
                // Ink is blind to ORDER, and Arabic reads right to left: a word laid out
                // backwards has exactly the ink of one laid out correctly. So count pixels too.
                int differing = 0;
                for (int i2 = 0; i2 < Width * Height; i2++)
                    for (int ch = 0; ch < 3; ch++)
                        // BGRA against RGBA: index to index compares GDI's blue with our red.
                        if (raw[i2 * 4 + (2 - ch)] != ours2[i2 * 4 + ch]) { differing++; break; }
                // What the shaper made of it: fewer glyphs than characters means a ligature
                // formed, and a glyph id that differs from the plain cmap mapping means a
                // positional form was substituted. Ratios alone cannot tell either.
                // Shaped with the face that will actually DRAW it. Reporting the probe family's
                // glyph count for a script the probe family does not have was measuring nothing:
                // every non-Latin row said "0 substituted" because every glyph id was 0.
                TrueTypeFont drawn = FaceThatDraws(font, text);
                string drawnBy = s_drawnBy;
                var shaped = new List<ShapedGlyph>();
                new OpenTypeTextShaper().Shape(drawn, text, shaped);
                int substituted = 0;
                for (int g = 0; g < shaped.Count && g < text.Length; g++)
                    if (shaped[g].GlyphId != drawn.GlyphIndex(text[g])) substituted++;
                report.AppendLine($"   {name,-24} {mine2,10}   {theirs2,11}   "
                                  + $"{(theirs2 == 0 ? 0 : mine2 / (double) theirs2),6:0.000}"
                                  + $"   {differing,12}   {text.Length}ch -> {shaped.Count}gl,"
                                  + $" {substituted} substituted   {drawnBy}"
                                  + $"   greys GDI {Greys(raw)}% ours {Greys(ours2)}%"
                                  + $"   colour GDI {Colour(raw)}% ours {Colour(ours2)}%");
            }

            File.AppendAllText(path!, report.ToString());
        }

        private static void CollectXs(PathFigure f, SortedSet<float> xs)
        {
            xs.Add(MathF.Round(f.Start.X, 3));
            foreach (PathSegment seg in f.Segments)
                switch (seg)
                {
                    case LineSegment l: xs.Add(MathF.Round(l.Point.X, 3)); break;
                    case QuadraticBezierSegment q:
                        xs.Add(MathF.Round(q.Control.X, 3)); xs.Add(MathF.Round(q.Point.X, 3)); break;
                    case CubicBezierSegment cu:
                        xs.Add(MathF.Round(cu.Control1.X, 3)); xs.Add(MathF.Round(cu.Control2.X, 3));
                        xs.Add(MathF.Round(cu.Point.X, 3)); break;
                }
        }

        private static List<PathFigure> Remap(List<PathFigure> figures, Dictionary<float, float> move)
        {
            Vector2 M(Vector2 v) => new Vector2(move.TryGetValue(MathF.Round(v.X, 3), out float nx) ? nx : v.X, v.Y);
            var outp = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var g = new PathFigure(M(f.Start)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: g.Segments.Add(new LineSegment(M(l.Point))); break;
                        case QuadraticBezierSegment q:
                            g.Segments.Add(new QuadraticBezierSegment(M(q.Control), M(q.Point))); break;
                        case CubicBezierSegment cu:
                            g.Segments.Add(new CubicBezierSegment(M(cu.Control1), M(cu.Control2), M(cu.Point))); break;
                    }
                outp.Add(g);
            }
            return outp;
        }

        /// <summary>The same figures with x scaled about the glyph origin.</summary>
        private static List<PathFigure> ScaleX(List<PathFigure> figures, float k)
        {
            var outp = new List<PathFigure>(figures.Count);
            Vector2 S(Vector2 v) => new Vector2(v.X * k, v.Y);
            foreach (PathFigure f in figures)
            {
                var g = new PathFigure(S(f.Start)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: g.Segments.Add(new LineSegment(S(l.Point))); break;
                        case QuadraticBezierSegment q:
                            g.Segments.Add(new QuadraticBezierSegment(S(q.Control), S(q.Point))); break;
                        case CubicBezierSegment cu:
                            g.Segments.Add(new CubicBezierSegment(S(cu.Control1), S(cu.Control2), S(cu.Point))); break;
                    }
                outp.Add(g);
            }
            return outp;
        }

        /// <summary>Coverage through the SHIPPING contrast curve, so a probe measures the pipeline
        /// that is actually drawn.
        /// <para>These probes had `pow(cov, 1.15)` written into them, which was the curve at the
        /// time. It is not any more -- the contrast is applied by blending in gamma space, and the
        /// exponent comes from the user's ClearType setting rather than from a constant -- so every
        /// verdict they reach would otherwise be about a renderer we no longer ship.</para></summary>
        private static int InkThroughTheCurve(byte coverage)
        {
            float g = Math.Clamp(Gdi.SystemFontSmoothingContrast() / 1000f, 1.0f, 2.2f);
            return (int) MathF.Round((1f - MathF.Pow(1f - coverage / 255f, 1f / g)) * 255f);
        }

        /// <summary>The leftmost x a figure reaches, control points included.</summary>
        private static float FigureLeft(PathFigure f)
        {
            float min = f.Start.X;
            foreach (PathSegment seg in f.Segments)
                switch (seg)
                {
                    case LineSegment l: min = MathF.Min(min, l.Point.X); break;
                    case QuadraticBezierSegment q: min = MathF.Min(min, MathF.Min(q.Control.X, q.Point.X)); break;
                    case CubicBezierSegment cu:
                        min = MathF.Min(min, MathF.Min(cu.Control1.X, MathF.Min(cu.Control2.X, cu.Point.X))); break;
                }
            return min;
        }

        /// <summary>The first and last lamp carrying ink, ours and GDI's, for one placed glyph.</summary>
        private static bool GlyphLampExtents(List<PathFigure> figures, float x, float baseline,
                                             byte[] raw, out int gdiL, out int gdiR,
                                             out int ourL, out int ourR)
        {
            gdiL = ourL = int.MaxValue; gdiR = ourR = int.MinValue;
            var moved = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var g = new PathFigure(new Vector2(f.Start.X + x, f.Start.Y + baseline)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l:
                            g.Segments.Add(new LineSegment(new Vector2(l.Point.X + x, l.Point.Y + baseline))); break;
                        case QuadraticBezierSegment q:
                            g.Segments.Add(new QuadraticBezierSegment(
                                new Vector2(q.Control.X + x, q.Control.Y + baseline),
                                new Vector2(q.Point.X + x, q.Point.Y + baseline))); break;
                        case CubicBezierSegment cu:
                            g.Segments.Add(new CubicBezierSegment(
                                new Vector2(cu.Control1.X + x, cu.Control1.Y + baseline),
                                new Vector2(cu.Control2.X + x, cu.Control2.Y + baseline),
                                new Vector2(cu.Point.X + x, cu.Point.Y + baseline))); break;
                    }
                moved.Add(g);
            }
            PathRasterizer.SubpixelMask m = PathRasterizer.RasterizeSubpixel(
                new PathGeometry(FillRule.NonZero, moved));
            if (m.IsEmpty) return false;

            const int Lit = 40;                       // a lamp is "on" once it carries this much ink
            for (int y = 0; y < Height; y++)
                for (int px = 0; px < Width; px++)
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int lamp = px * 3 + ch;
                        if (255 - raw[(y * Width + px) * 4 + (2 - ch)] >= Lit)
                        { if (lamp < gdiL) gdiL = lamp; if (lamp > gdiR) gdiR = lamp; }
                        int gy = y - (int) m.OriginY, gx = px - (int) m.OriginX;
                        if (gy >= 0 && gy < m.Height && gx >= 0 && gx < m.Width
                            && m.Rgba[(gy * m.Width + gx) * 4 + ch] >= Lit)
                        { if (lamp < ourL) ourL = lamp; if (lamp > ourR) ourR = lamp; }
                    }
            return gdiL != int.MaxValue && ourL != int.MaxValue;
        }

        /// <summary>Our pipeline's lamps for a plain vertical bar at a given left and right edge.</summary>
        private static int[] BarLamps(float x0, float x1, int row, int ppem)
        {
            var fig = new PathFigure(new Vector2(x0, row - 4)) { Closed = true };
            fig.Segments.Add(new LineSegment(new Vector2(x1, row - 4)));
            fig.Segments.Add(new LineSegment(new Vector2(x1, row + 4)));
            fig.Segments.Add(new LineSegment(new Vector2(x0, row + 4)));
            var geom = new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });

            PathRasterizer.SubpixelMask m = PathRasterizer.RasterizeSubpixel(geom);
            var outp = new int[36];
            if (m.IsEmpty) return outp;
            int y = row - (int)m.OriginY;
            if (y < 0 || y >= m.Height) return outp;
            for (int k = 0; k < 12; k++)
            {
                int x = PenX - 2 + k - (int)m.OriginX;
                if (x < 0 || x >= m.Width) continue;
                int i = (y * m.Width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    // The same curve the renderer applies to a mask before it is composited.
                    float cov = m.Rgba[i + c] / 255f;
                    outp[k * 3 + c] = InkThroughTheCurve((byte) MathF.Round(cov * 255f));
                }
            }
            return outp;
        }

        /// <summary>Our own stem edges for 'l', fitted the shipped way or with x kept.</summary>
        private static void Edges(TrueTypeFont font, int ppem, bool subpixel, out float lo, out float hi)
        {
            bool saved = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = subpixel;
            lo = float.MaxValue; hi = float.MinValue;
            try
            {
                if (!((IHintedGlyphFont)font).TryGetHintedOutline(font.GlyphIndex('l'), ppem,
                                                                  out List<PathFigure> figs)) return;
                foreach (PathFigure f in figs)
                {
                    lo = MathF.Min(lo, f.Start.X); hi = MathF.Max(hi, f.Start.X);
                    foreach (PathSegment seg in f.Segments)
                        if (seg is LineSegment l) { lo = MathF.Min(lo, l.Point.X); hi = MathF.Max(hi, l.Point.X); }
                }
            }
            finally { TrueTypeFont.SubpixelFitting = saved; }
        }

        /// <summary>The ENDS of the range: paper, and fully covered ink.
        /// <para>If our colours are right, a pixel the glyph covers completely must be the same
        /// value on both sides and so must one it does not touch at all -- whatever curve is applied
        /// in between, since a curve fixes 0 and 1. If those ends differ, no gamma can be blamed and
        /// the fault is the colour or the blend space; if they agree, the curve is the only thing
        /// left and its value is a real measurement rather than a fudge.</para>
        /// <para>Reported only: set WPF_ENDS.</para></summary>
        [Fact]
        public void PaperAndFullInk_MatchWindows()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_ENDS");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_ENDS to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine("ppem  paper(ours/gdi)   darkest(ours/gdi)   saturated px(ours/gdi)");

            foreach (int ppem in new[] { 11, 12, 13, 16, 19, 28 })
            {
                int baseline = ppem + 12;
                Gdi.s_rawRgb = raw;
                Gdi.Draw("Hamburgefonstiv", "Segoe UI", ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                byte[] mine = OursRgba(font, "Hamburgefonstiv", ppem, baseline, correction: true);

                int ourPaper = 0, gdiPaper = 0, ourMin = 255, gdiMin = 255, ourZero = 0, gdiZero = 0;
                for (int i = 0; i < Width * Height * 4; i += 4)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if (mine[i + c] > ourPaper) ourPaper = mine[i + c];
                        if (raw[i + c] > gdiPaper) gdiPaper = raw[i + c];
                        if (mine[i + c] < ourMin) ourMin = mine[i + c];
                        if (raw[i + c] < gdiMin) gdiMin = raw[i + c];
                        if (mine[i + c] == 0) ourZero++;
                        if (raw[i + c] == 0) gdiZero++;
                    }
                }
                report.AppendLine($"{ppem,4}      {ourPaper,3}/{gdiPaper,-3}          "
                                  + $"{ourMin,3}/{gdiMin,-3}            {ourZero,5}/{gdiZero,-5}");
            }
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>How WIDE is GDI's stem, measured without touching its gamma?
        /// <para>The lamp VALUES are contrast-corrected, so summing them does not give coverage --
        /// that inversion has been wrong twice in this file. The SUPPORT does not care: a lamp
        /// either carries ink or it does not, whatever curve was applied. A stem of w lamps through
        /// a three-tap filter lights w+2 of them, so counting lit lamps measures w to within one,
        /// and doing it across sizes says whether GDI's width is the design width (which grows with
        /// the size), a constant, or something quantised.</para>
        /// <para>Reported only: set WPF_STEMWIDTH.</para></summary>
        [Theory]
        [InlineData("l")]
        [InlineData("I")]
        [InlineData("H")]
        public void StemWidth_AgainstGdis(string text)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STEMWIDTH");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STEMWIDTH to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine($"'{text}'  ppem   GDI lit lamps   ours lit lamps");

            for (int ppem = 10; ppem <= 20; ppem++)
            {
                int baseline = ppem + 12;
                Gdi.s_rawRgb = raw;
                Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);

                int row = baseline - ppem / 3;
                int gdiLit = 0, ourLit = 0;
                for (int x = PenX - 3; x < PenX + 10; x++)
                {
                    int i = (row * Width + x) * 4;
                    // GDI's buffer is BGRA, ours RGBA; lamp order matters not at all for a COUNT,
                    // but the channel indices still have to be the right three.
                    for (int L = 0; L < 3; L++)
                    {
                        if (255 - raw[i + L] > 8) gdiLit++;
                        if (255 - mine[i + L] > 8) ourLit++;
                    }
                }
                report.AppendLine($"      {ppem,4}   {gdiLit,11}   {ourLit,14}");
            }
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>The FINISHED pixels, ours beside Windows', as character maps.
        /// <para>Everything else here is an aggregate. Aggregates have now said the geometry, the
        /// advances, the lamp grid, the ink and the colour all agree, and yet 59% of inked pixels
        /// differ by a mean of 25 -- so it is worth simply LOOKING at what that disagreement is.</para>
        /// <para>WPF_FINISHED=text@ppem, e.g. "Ham@12".</para></summary>
        [Fact]
        public void FinishedPixels_SideBySide()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_FINISHED");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_FINISHED=text@ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");

            string[] parts = spec!.Split('@');
            string text = parts[0];
            int ppem = int.Parse(parts[1]);
            int baseline = ppem + 12;
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            byte[] windows = Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
            byte[] ours = Ours(font, text, ppem, baseline, correction: true);

            const string Ramp = " .:-=+*#%@";
            Console.Error.WriteLine($"=== '{text}' @{ppem}   ours | windows   (green lamp) ===");
            for (int y = baseline - ppem - 2; y <= baseline + 4; y++)
            {
                var a = new System.Text.StringBuilder();
                var b = new System.Text.StringBuilder();
                for (int x = PenX - 2; x < PenX + 46; x++)
                {
                    int i = y * Width + x;
                    a.Append(Ramp[Math.Min(9, ours[i] * 10 / 256)]);
                    b.Append(Ramp[Math.Min(9, windows[i] * 10 / 256)]);
                }
                Console.Error.WriteLine($"{y,3} |{a}|  |{b}|");
            }
        }

        /// <summary>Whether our LAMPS line up with GDI's, by sliding ours along the lamp grid.
        /// <para>A whole-pixel offset cannot be the residual -- both sides snap a run's origin to a
        /// device pixel -- but a third of a pixel is a whole lamp, and nothing had ever checked that
        /// our lamp grid sits where GDI's does. If it does not, every edge in the page is split
        /// across the wrong three lamps and no curve or filter can repair it. Slide ours by -2..+2
        /// lamps and see which offset agrees best: 0 says the grids are aligned and the residual is
        /// in how coverage is SPLIT; anything else says we are drawing the whole page off-grid.</para>
        /// <para>Reported only: set WPF_LAMP_SHIFT to a file.</para></summary>
        [Fact]
        public void LampAlignment_ReadAgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_LAMP_SHIFT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_LAMP_SHIFT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI to compare against");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine("ppem   shift-2     shift-1      shift0     shift+1     shift+2");

            foreach (int ppem in new[] { 11, 12, 13, 16, 19 })
            {
                int baseline = ppem + 12;
                var sums = new long[5];
                foreach (string text in Repertoire)
                {
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);

                    // Ink per lamp, left to right. GDI's buffer is BGRA and ours is RGBA, so the
                    // lamp order has to be spelled out on each side rather than assumed.
                    int lamps = Width * 3;
                    for (int y = 0; y < Height; y++)
                    {
                        for (int s = -2; s <= 2; s++)
                        {
                            long acc = 0;
                            for (int x = 0; x < Width; x++)
                            {
                                int i = (y * Width + x) * 4;
                                int[] theirs = { 255 - raw[i + 2], 255 - raw[i + 1], 255 - raw[i] };
                                for (int L = 0; L < 3; L++)
                                {
                                    int k = x * 3 + L + s;              // our lamp, slid by s
                                    int ours = 0;
                                    if (k >= 0 && k < lamps)
                                    {
                                        int j = (y * Width + k / 3) * 4 + k % 3;
                                        ours = 255 - mine[j];
                                    }
                                    acc += Math.Abs(ours - theirs[L]);
                                }
                            }
                            sums[s + 2] += acc;
                        }
                    }
                }
                report.AppendLine($"{ppem,4} {sums[0],11} {sums[1],11} {sums[2],11} "
                                  + $"{sums[3],11} {sums[4],11}");
            }

            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>Runs ONE glyph's hinting program with the instruction trace on. The smallest
        /// reproduction of the interpreter fault there is: set WPF_HINT_DUMP to "family/char/ppem",
        /// e.g. "Arial/l/16".</summary>
        [Fact]
        public void OneGlyphsProgram_CanBeWatchedInstructionByInstruction()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_HINT_DUMP");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_HINT_DUMP=family/char/ppem");
            // family/char/ppem, with an optional /B or /I for the styled file.
            string[] parts = spec!.Split('/');
            bool bold = parts.Length > 3 && parts[3].Contains('B');
            bool italic = parts.Length > 3 && parts[3].Contains('I');
            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            int gid = font.GlyphIndex(parts[1][0]);
            float ppem = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"=== {parts[0]} '{parts[1]}' gid={gid} at {ppem}ppem ===");
            // WPF_HINT_DUMP_BILEVEL=1 traces the program in the mode GetGlyphOutline is in,
            // which is the only mode with an exact oracle to check the trace against.
            TrueTypeInterpreter.BiLevelPass =
                Environment.GetEnvironmentVariable("WPF_HINT_DUMP_BILEVEL") == "1";
            bool savedSub = TrueTypeFont.SubpixelFitting;
            if (TrueTypeInterpreter.BiLevelPass) TrueTypeFont.SubpixelFitting = false;
            TrueTypeInterpreter.s_dumpGlyph = true;
            try { ((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out _); }
            finally
            {
                TrueTypeInterpreter.s_dumpGlyph = false;
                TrueTypeInterpreter.BiLevelPass = false;
                TrueTypeFont.SubpixelFitting = savedSub;
            }
        }

        /// <summary>How many glyphs a face's OWN hinting program fits so badly that the fitting has
        /// to be discarded.
        /// <para>The hinter was built and measured against Segoe UI, and nothing ever asked what it
        /// did to anything else. It garbled them: at 16 pixels an em Arial fitted its 'I' and 'l' to
        /// a height of 2 where the outline asks for 11.5, and text in most installed faces came out
        /// as a scatter of wrong letters on screen. TrueTypeFont now checks that a fitted glyph is
        /// still the size the outline says it is and falls back to the unhinted one when it is not;
        /// this counts how often that happens.</para>
        /// <para>Every number here is a BUG in the interpreter, not a budget. They should fall as the
        /// mis-run instructions are found; ratcheted both ways so an improvement has to be recorded
        /// and a regression cannot hide.</para>
        /// <para>They already have. The first cause found -- the projection and freedom vectors being
        /// inherited from 'prep' instead of reset to the x axis before each glyph program -- took
        /// Arial from 93 to 4 and Times New Roman from 72 to 0, and moved Segoe UI not at all.</para>
        /// </summary>
        [Theory]
        [InlineData("Segoe UI", 0)]
        [InlineData("Consolas", 0)]
        // 5 -> 0 when MD's operands were paired with the zones the way the spec and FreeType pair
        // them. Every glyph Arial could not fit before was a glyph whose hinting ran on a measured
        // distance of the wrong SIGN. Kept below as the history of the number, since it is the only
        // record of what the ClearType delta rules cost:
        // 4 -> 5 when the ClearType DELTA rules went in. Isolated, not guessed: WPF_CT_DELTA=all and
        // =inline both bring it back to 4, so it is the suppression of x-direction deltas and
        // nothing else. That rule is measured-correct for rendering -- keeping the deltas costs the
        // text specimen 759,520 -> 1,408,545 -- and this is what it costs: one more Arial glyph
        // whose program, deprived of its deltas, fits to a size the outline does not agree with.
        // The fallback then draws it unhinted, so the output is protected; this is the guard saying
        // the interpreter has one more glyph it cannot run faithfully, which is true.
        // 0 -> 1 with the rasterizer version at 42. The glyph is 'w'@11 and the cause is the same
        // one the version change is about: at 42 the face reaches its ClearType branch, and that
        // branch is code this interpreter has never been validated against (the 55-of-62 agreement
        // that vouches for it was taken against GetGlyphOutline, which renders greyscale). Its
        // fitted box leaves the two-pixel slack, so the fallback draws it unhinted and the output
        // is protected -- this is the guard reporting one more glyph we cannot run faithfully,
        // which is true and should go back to 0 when the branch is fixed. Not a licence to let it
        // climb: raise this only with the glyph named and the reason understood.
        [InlineData("Arial", 0)]
        [InlineData("Times New Roman", 0)]
        [InlineData("Comic Sans MS", 0)]
        // Verdana and Tahoma were not on this list, and Verdana is the worst face on the six-face
        // specimen -- a guard that counts interpreter bugs should look at the face doing worst.
        [InlineData("Verdana", 0)]
        [InlineData("Tahoma", 0)]
        public void EveryFacesOwnHinting_SurvivesItsOwnProgram(string family, int allowed)
        {
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            TrueTypeFont.ResetImplausibleFits();
            int before = TrueTypeFont.ImplausibleFits;

            // Named, not just counted: a number that moves is only actionable if the glyph it
            // belongs to can be found again.
            var culprits = new List<string>();
            foreach (int ppem in new[] { 11, 12, 16, 19 })
                foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
                {
                    int was = TrueTypeFont.ImplausibleFits;
                    ((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem, out _);
                    if (TrueTypeFont.ImplausibleFits > was) culprits.Add($"'{c}'@{ppem}");
                }

            int rejected = TrueTypeFont.ImplausibleFits - before;
            Assert.True(rejected <= allowed,
                $"{family}: {rejected} glyphs fitted implausibly ({string.Join(", ", culprits)}), "
                + $"more than the {allowed} written down "
                + "-- the interpreter has got worse, or this face has found a new way to break it.");
            Assert.True(rejected >= allowed,
                $"{family}: only {rejected} glyphs now fit implausibly, against {allowed} written down. "
                + "Lower the number -- that is the point of it.");
        }

        /// <summary>What our reader makes of a face, character by character: the glyph id, the
        /// advance, and the OUTLINE's bounding box. Reported only (WPF_FACE_REPORT).
        /// <para>Text in some families comes out garbled -- 'i' drawn as its own dot, 'l' as a full
        /// stop -- while Segoe UI is perfect. Spacing survives, which says the ids and their advances
        /// are right and the OUTLINES are not, and a bounding box per glyph is the shortest way to
        /// see that: a letter whose box is a fraction of its advance was not read properly.</para>
        /// </summary>
        [Theory]
        [InlineData("Arial")]
        [InlineData("Times New Roman")]
        [InlineData("Segoe UI")]
        [InlineData("Consolas")]
        public void FaceOutlines_AreWholeGlyphs(string family)
        {
            string? path = Environment.GetEnvironmentVariable("WPF_FACE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_FACE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}  ({Path.GetFileName(file)})  unitsPerEm={font.PixelsPerEm}");
            foreach (char c in "AaBbiIlYyZz")
            {
                int gid = font.GlyphIndex(c);
                bool got = font.TryGetGlyphOutline(gid, out var figures);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                int pts = 0;
                void Take(System.Numerics.Vector2 v)
                {
                    minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y);
                    maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y);
                    pts++;
                }
                if (got)
                    foreach (PathFigure fig in figures)
                    {
                        Take(fig.Start);
                        foreach (PathSegment seg in fig.Segments)
                            switch (seg)
                            {
                                case LineSegment l: Take(l.Point); break;
                                case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                                case CubicBezierSegment cu: Take(cu.Control1); Take(cu.Control2); Take(cu.Point); break;
                            }
                    }
                // The same glyph FITTED, at a size real UI text uses. A fitted outline should sit on
                // top of the unfitted one; one that has collapsed or wandered is the face the hinter
                // is breaking, and naming it is the whole point of this report.
                string fitted = "no hinted outline";
                if (font is IHintedGlyphFont hf && hf.TryGetHintedOutline(gid, 16f, out var hfigs))
                {
                    float hx0 = float.MaxValue, hy0 = float.MaxValue, hx1 = float.MinValue, hy1 = float.MinValue;
                    int hp = 0;
                    void TakeH(System.Numerics.Vector2 v)
                    {
                        hx0 = MathF.Min(hx0, v.X); hy0 = MathF.Min(hy0, v.Y);
                        hx1 = MathF.Max(hx1, v.X); hy1 = MathF.Max(hy1, v.Y);
                        hp++;
                    }
                    foreach (PathFigure fig in hfigs)
                    {
                        TakeH(fig.Start);
                        foreach (PathSegment seg in fig.Segments)
                            switch (seg)
                            {
                                case LineSegment l2: TakeH(l2.Point); break;
                                case QuadraticBezierSegment q2: TakeH(q2.Control); TakeH(q2.Point); break;
                                case CubicBezierSegment c2: TakeH(c2.Control1); TakeH(c2.Control2); TakeH(c2.Point); break;
                            }
                    }
                    fitted = hp == 0 ? "EMPTY"
                        : $"figs={hfigs.Count} pts={hp,-4} box=({hx0:0.##},{hy0:0.##})-({hx1:0.##},{hy1:0.##})"
                          + $" w={hx1 - hx0:0.##} h={hy1 - hy0:0.##}";
                }

                report.AppendLine(pts == 0
                    ? $"  '{c}' gid={gid,-5} NO OUTLINE (got={got})"
                    : $"  '{c}' gid={gid,-5} raw w={maxX - minX,6:0.##} h={maxY - minY,6:0.##}   fitted@16 {fitted}");
            }
            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>WHERE THE FIRST GLYPH LANDS, at a fixed pen, ours against GDI's.
        /// <para>The text specimen shows whole rows displaced by one pixel -- 64% of the error at
        /// 16ppem is a rigid shift of Verdana's four styles -- and a displaced run can only come
        /// from the origin the run starts at or from the first glyph's fitted left edge. This asks
        /// the second question with no Label, no margin and no window in the way: draw at PenX and
        /// report the first inked column.</para>
        /// <para>Set WPF_INKLEFT_REPORT.</para></summary>
        [Fact]
        public void InkLeft_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_INKLEFT_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_INKLEFT_REPORT to collect this");

            var report = new System.Text.StringBuilder();
            report.AppendLine("== first inked column at a fixed pen (PenX=" + PenX + "), ours vs GDI");
            report.AppendLine("   face              ppem  ours  gdi  d");
            foreach (string family in new[] { "Segoe UI", "Arial", "Times New Roman", "Verdana",
                                              "Tahoma", "Consolas" })
            {
                string? file = FontFiles.Find(family, bold: false, italic: false);
                if (file is null) continue;
                var font = new TrueTypeFont(File.ReadAllBytes(file!));
                foreach (int ppem in new[] { 9, 10, 11, 12, 13, 16, 20 })
                {
                    int baseline = ppem + 12;
                    const string Text = "abcdefghijklm";
                    byte[] theirs = Gdi.Draw(Text, family, ppem, PenX, baseline, Width, Height);
                    byte[] ours = Ours(font, Text, ppem, baseline);
                    int a = FirstInkColumn(ours), b = FirstInkColumn(theirs);
                    report.AppendLine($"   {family,-16} {ppem,4} {a,5} {b,4} {a - b,3}"
                                      + ((a - b) != 0 ? "   <--" : ""));
                }
            }
            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>The first column carrying ink in a COVERAGE MASK -- one byte a pixel, which is
        /// what Gdi.Draw and Ours return. Reading them as RGBA made every column look inked and the
        /// probe answered 0 for everything.</summary>
        private static int FirstInkColumn(byte[] mask)
        {
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                    if (mask[y * Width + x] > 8) return x;
            return -1;
        }

        /// <summary>STAGE C of the pipeline decomposition: the finished ClearType pixels, per face.
        /// <para>Stages A and B live in GdiStageTests and compare GEOMETRY -- the unhinted outline,
        /// then the fitted one -- through one rasterizer, so they say nothing about the three lamps,
        /// the filter or the contrast curve. This is the rest of it: GDI's ClearType against our
        /// whole pipeline, over the same repertoire and the same sizes, so the three numbers can be
        /// read side by side and the stage that carries the difference is the one to work on.</para>
        /// <para>Written into the same file (WPF_STAGE_REPORT) as A and B.</para></summary>
        /// <summary>NOTE: every face here is the REGULAR one, and the text specimen now says the
        /// worst rows in the whole matrix are ITALIC -- Segoe UI italic and bold-italic at 20ppem
        /// are 764,521 and 832,847, 41% of that size, at 0.591 and 0.349 error per unit of ink
        /// where every other row sits between 0.057 and 0.116. (Earlier revisions of this note
        /// quoted 1.03M and 0.92M, measured before the specimen's two columns were stopped from
        /// overlapping at that size; the conclusion survived the correction, the numbers did not.)
        /// No controlled instrument covers them.
        /// <para>What is known about those rows, measured: total ink matches Windows to 0.8%, the
        /// R/G/B channels are balanced to 0.8% (so it is not lamp order), no integer shift improves
        /// them, and the ink centroid drifts less than a pixel (so the advances do not accumulate).
        /// The parity suite, which draws at a fixed pen, rates italic at 20ppem its BEST case. So
        /// what is left is per-glyph sub-pixel placement in the app, and extending this theory to
        /// the styled faces is how to see it without a window.</para>
        /// <para>Caveat for anyone measuring the specimen at 20ppem: the line OVERFLOWS its column
        /// there and both sides are clipped at the same x, so ink extents cannot detect advance
        /// drift at that size -- use the centroid.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        [InlineData("Times New Roman")]
        [InlineData("Consolas")]
        // Verdana and Tahoma were missing, and they are the two faces the text specimen now says
        // are worst: at 16ppem Verdana carries a rigid one-pixel x displacement in all four styles
        // (1.19-1.27M each, 82-91% of it removed by shifting the row) while Tahoma at the same
        // Font.Height does not. This stage draws at a FIXED PEN with no Label and no margin in the
        // way, so it is the place to reproduce that without a window capture.
        [InlineData("Verdana")]
        [InlineData("Tahoma")]
        public void StageC_ClearTypeAgainstGdis(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STAGE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STAGE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var raw = new byte[Width * Height * 4];
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}  (stage C, ClearType)");
            report.AppendLine("ppem  stage      differing  sum|d|      our ink / GDI ink");

            // 7 and 8 are below the gasp's gridfit threshold for Segoe UI, so with our own
            // hinting off (WPF_TEXT_HINTING=0) both sides draw the SAME outline and the only
            // thing left to differ is the shading.
            // 9 and 10 added when the text specimen made 10ppem its worst size: the list jumped
            // from 8 to 11 and could not be pointed at it.
            foreach (int ppem in new[] { 7, 8, 9, 10, 11, 12, 13, 16, 19 })
            {
                int baseline = ppem + 12;
                int pixels = 0;
                long total = 0, ourInk = 0, theirInk = 0;

                foreach (string text in Repertoire)
                {
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(text, family, ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);

                    for (int i = 0; i < mine.Length; i += 4)
                        for (int c = 0; c < 3; c++)
                        {
                            // raw is B G R A -- a straight Marshal.Copy out of a Windows DIB, whatever
                            // its name says. Indexing both sides alike compared our RED lamp against
                            // GDI's BLUE one, which on subpixel text is opposite edges of the same stem.
                            int a = 255 - mine[i + c], b = 255 - raw[i + (2 - c)];
                            int d = Math.Abs(a - b);
                            if (d > 8) pixels++;
                            total += d;
                            ourInk += a;
                            theirInk += b;
                        }
                }

                // Divided by three: a fully covered pixel is 765 across three lamps where a
                // grey stage counts 255, and the point of printing these is to hold them
                // beside the grey stages.
                // Is GDI even drawing ClearType here? Below the gasp's threshold it is told to use
                // GREY (GASP_DOGRAY), and comparing our three lamps against its one would be a
                // measurement of nothing.
                // COLOUR, over the whole repertoire and as a MAGNITUDE, not a threshold count on one
                // string. A count of pixels past |lamp difference| > 8 moves in jumps as fringes
                // cross the threshold, which is far too coarse to chase a percent with; the mean
                // spread is continuous and says how strong the fringes are rather than how many
                // cleared a bar.
                int coloured = 0, ourColoured = 0;
                long spread = 0, ourSpread = 0;
                foreach (string probe in Repertoire)
                {
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(probe, family, ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    byte[] mineC = OursRgba(font, probe, ppem, baseline, correction: true);
                    for (int i = 0; i < raw.Length; i += 4)
                    {
                        int tr = 255 - raw[i + 2], tg = 255 - raw[i + 1], tb = 255 - raw[i];
                        int orr = 255 - mineC[i], og = 255 - mineC[i + 1], ob = 255 - mineC[i + 2];
                        if (Math.Abs(tr - tg) > 8 || Math.Abs(tg - tb) > 8) coloured++;
                        if (Math.Abs(orr - og) > 8 || Math.Abs(og - ob) > 8) ourColoured++;
                        spread += Math.Max(tr, Math.Max(tg, tb)) - Math.Min(tr, Math.Min(tg, tb));
                        ourSpread += Math.Max(orr, Math.Max(og, ob)) - Math.Min(orr, Math.Min(og, ob));
                    }
                }

                // And how much colour WE make on the same string. A fringe is what a filter leaves
                // behind, so if the two counts are far apart the filter is the wrong width -- and
                // unlike an ink total, colour cannot be traded against position.


                report.AppendLine($"{ppem,4}  C cleartype {pixels,8}  {total,9}      "
                    + $"{(theirInk == 0 ? 0 : (double)ourInk / theirInk):0.0000}"
                    + $"  ourInk={ourInk / 3} gdiInk={theirInk / 3}"
                    + $" colourN={(coloured == 0 ? 0 : (double)ourColoured / coloured):0.000}"
                    + $" colourMag={(spread == 0 ? 0 : (double)ourSpread / spread):0.000}");
            }

            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>Coverage as an image: ink on white, the way both of them are looked at.</summary>
        private static void Write(string path, byte[] cover)
        {
            var rgba = new byte[cover.Length * 4];
            for (int i = 0; i < cover.Length; i++)
            {
                byte v = (byte)(255 - cover[i]);
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
                rgba[i * 4 + 3] = 255;
            }
            PngWriter.Write(path, rgba, Width, Height);
        }


        /// <summary>How far each face and size may sit from GDI's ink, in hundredths of a percent.
        /// <para>Ratcheted both ways like every other allowance here. Bold, italic and bold-italic
        /// are within a percent everywhere, which is what says the correction curve is right; the
        /// regular face is the one that misses, and its row is the open problem written down.</para>
        /// </summary>
        /// <summary>WHAT GDI DOES WITH A FEATURE THINNER THAN A SCANLINE, measured instead of
        /// argued about. WPF_VSLAB=&lt;ppem&gt;.
        /// <para>Every synthetic probe before this drew a FULL-HEIGHT bar, so none of them could
        /// ask anything about the y direction. This draws a horizontal slab sitting on the
        /// baseline, sweeps its height from a sixteenth of a pixel to two pixels, and reads back
        /// how much ink GDI puts on the page. Three outcomes are distinguishable and they mean
        /// different things: ink PROPORTIONAL to the height means GDI integrates vertical coverage
        /// like we do; a STEP at some height means it takes one sample per row; a FLOOR -- a thin
        /// slab still drawing a full row -- means dropout control.</para>
        /// <para>It exists because Times New Roman's serif rows are the largest single pool left
        /// and the question underneath them is exactly this: our fitted serif slab is 0.219px tall
        /// and GDI draws that row 2.5x a stem row at every size. There is no oracle for GDI's
        /// ClearType Y -- GGO answers bi-level, SolveGdisEdges solves x only -- so this builds
        /// one for the one case that matters.</para></summary>
        [Fact]
        public void HowGdiRendersASubPixelTallSlab()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? spec = Environment.GetEnvironmentVariable("WPF_VSLAB");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_VSLAB=<ppem>");
            int ppem = int.Parse(spec!);
            const string Family = "WpfSlabProbe";
            double unitsPerPixel = SyntheticFont.UnitsPerEm / (double) ppem;

            var bars = new List<SyntheticFont.Bar>();
            var wanted = new List<double>();
            for (int sixteenth = 1; sixteenth <= 32; sixteenth++)
            {
                double px = sixteenth / 16.0;
                int units = (int) Math.Round(px * unitsPerPixel);
                bars.Add(new SyntheticFont.Bar(0, 256, 256 + (int) Math.Round(6 * unitsPerPixel),
                                               round: false, minDistance: false, noProgram: true,
                                               slabHeight: units));
                wanted.Add(px);
            }

            // WPF_VSLAB_GASP=times ships Times New Roman roman's own gasp, so at 16ppem BOTH
            // sides have symmetric smoothing OFF. Without it the probe carries no gasp at all and
            // GDI's no-table fallback turns symmetric ON for it while ours stays off -- the two
            // rasterizers then answer different questions. Default: no table, as every probe
            // before this one.
            SyntheticFont.GaspRanges =
                Environment.GetEnvironmentVariable("WPF_VSLAB_GASP") == "times"
                    ? new (int, int)[] { (8, 0xA), (17, 0x5), (0xFFFF, 0xF) } : null;
            // WPF_VSLAB_SCAN=times adds Times' own SCANCTRL 303 / SCANTYPE 1 to the probe's prep.
            SyntheticFont.ScanControl =
                Environment.GetEnvironmentVariable("WPF_VSLAB_SCAN") == "times" ? (303, 1) : null;
            byte[] fontBytes;
            try { fontBytes = SyntheticFont.Build(Family, bars); }
            finally { SyntheticFont.GaspRanges = null; SyntheticFont.ScanControl = null; }
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI would not accept the slab font");
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== a horizontal slab on the baseline, {Family} at {ppem}ppem,"
                              + " 6px wide, no glyph program");
            report.AppendLine("   wanted(px)   GDI ink   our ink   GDI/height   ours/height");
            try
            {
                var font = new TrueTypeFont(fontBytes);
                var raw = new byte[Width * Height * 4];
                for (int i = 0; i < bars.Count; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    int baseline = ppem + 12;
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Family, ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    double gdi = SlabInk(raw, bgra: true);
                    byte[] oursRgba = OursRgba(font, ch, ppem, baseline, correction: true);
                    double ours = SlabInk(oursRgba, bgra: false);
                    // WPF_VSLAB_DUMP=<n> prints both rasters' inked rows for the n'th slab, as
                    // coverage per channel. The totals above say a row disagrees; only this says
                    // WHERE -- it is how the fringe at the ends of a dropout-filled row was read.
                    if (Environment.GetEnvironmentVariable("WPF_VSLAB_DUMP") == (i + 1).ToString())
                        for (int row = 0; row < Height; row++)
                        {
                            var g = new System.Text.StringBuilder();
                            var o = new System.Text.StringBuilder();
                            for (int col = 0; col < Width; col++)
                            {
                                int k = (row * Width + col) * 4;
                                g.Append($" {255 - raw[k + 2]:000},{255 - raw[k + 1]:000},{255 - raw[k + 0]:000}");
                                o.Append($" {255 - oursRgba[k + 0]:000},{255 - oursRgba[k + 1]:000},{255 - oursRgba[k + 2]:000}");
                            }
                            string gs = g.ToString(), os2 = o.ToString();
                            if (gs.Replace(" 000,000,000", "").Trim().Length == 0
                                && os2.Replace(" 000,000,000", "").Trim().Length == 0) continue;
                            report.AppendLine($"   GDI  row {row}:{gs}");
                            report.AppendLine($"   ours row {row}:{os2}");
                        }
                    report.AppendLine($"   {wanted[i],9:0.0000} {gdi,9:0.00} {ours,9:0.00}"
                                      + $" {gdi / wanted[i],11:0.00} {ours / wanted[i],12:0.00}");
                }
            }
            finally { RemoveFontMemResourceEx(handle); }
            TrueTypeInterpreter.DumpMirpCensus();
            Console.Error.Write(report.ToString());
        }

        /// <summary>WHAT A QUADRATIC ARC WEIGHS, in GDI, in ours, and in closed form.
        /// WPF_ARC=&lt;ppem&gt;.
        /// <para>Every synthetic probe before this one is made of STRAIGHT edges -- bars, slabs,
        /// crossed diagonals -- so "the coverage chain is exact" has only ever been established
        /// for straight edges, and text is nothing but small curves. This draws the one shape
        /// nobody has drawn: a flat base capped by a single quadratic, three points, the middle
        /// one off-curve, and NO glyph program, so both renderers read exactly the same
        /// outline.</para>
        /// <para>It is an ORACLE and not another comparison, because the answer is known: the
        /// region between a quadratic Bezier and its chord is exactly two thirds of the triangle
        /// P0 P1 P2, so with the base flat the area is (Right - Left) * ctrlY / 3 font units
        /// squared, whatever the control's x. Three columns -- GDI, ours, exact -- say which of
        /// the two is wrong rather than merely that they differ.</para>
        /// <para>The question it was built for: the Times Regular 12-16ppem band survived the
        /// flattening fix almost untouched, and every point GDI disagrees with there is an
        /// UNTOUCHED OFF-CURVE CONTROL, never an on-curve one. That is either an outline
        /// difference our interpreter is making, or a curve the two rasterizers weigh
        /// differently, and nothing measured on a real glyph can separate them.</para>
        /// <para>WPF_ARC_GASP=times ships Times New Roman roman's own gasp, for the same reason
        /// the slab probe needs it: with NO table GDI's fallback turns symmetric smoothing on for
        /// the probe while ours stays off, and the two sides then answer different questions.
        /// WPF_ARC_SCAN=times adds Times' SCANCTRL 303 / SCANTYPE 1.</para></summary>
        [Fact]
        public void HowGdiWeighsAQuadraticArc()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? spec = Environment.GetEnvironmentVariable("WPF_ARC");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_ARC=<ppem>");
            int ppem = int.Parse(spec!);
            const string Family = "WpfArcProbe";
            double unitsPerPixel = SyntheticFont.UnitsPerEm / (double) ppem;

            // A base six pixels wide, and controls sweeping from a quarter of a pixel of height to
            // six pixels -- and at three x positions, so a symmetric cap, a skewed one, and the
            // degenerate case where the control sits directly over an end.
            // WPF_ARC_LEFT=<n> offsets the chord by n SIXTY-FOURTHS of a pixel, so the same shape can
            // be walked across a pixel. That is how a tie at a sample is told from a placement
            // difference: a tie shows up at two offsets out of sixteen, a misplacement at all of
            // them.
            int left = 256 + (int) Math.Round(
                int.TryParse(Environment.GetEnvironmentVariable("WPF_ARC_LEFT"), out int lo16)
                    ? lo16 * unitsPerPixel / 64.0 : 0.0);
            int width = (int) Math.Round(6 * unitsPerPixel);
            // WPF_ARC_AXIS=x stands the figure on its side: the CHORD is vertical and the curve
            // bulges sideways, so its extremum is in x. Default y, the cap on a flat base.
            bool axisX = Environment.GetEnvironmentVariable("WPF_ARC_AXIS") == "x";
            var bars = new List<SyntheticFont.Bar>();
            var wanted = new List<(double H, double Skew)>();
            foreach (double skew in new[] { 0.5, 0.25, 0.0 })
                foreach (double h in new[] { 0.25, 0.5, 1.0, 1.5, 2.0, 3.0, 4.5, 6.0 })
                {
                    int bulge = (int) Math.Round(h * unitsPerPixel);
                    int along = (int) Math.Round(skew * width);
                    if (axisX)
                        // Chord from (left, 0) up to (left, width); control out to the right.
                        bars.Add(new SyntheticFont.Bar(0, left, left + bulge, round: false,
                                                       minDistance: false, noProgram: true,
                                                       arcCtrlX: left + bulge, arcCtrlY: along,
                                                       arcEndX: left, arcEndY: width, arc: true));
                    else
                        bars.Add(new SyntheticFont.Bar(0, left, left + width, round: false,
                                                       minDistance: false, noProgram: true,
                                                       arcCtrlX: left + along, arcCtrlY: bulge,
                                                       arc: true));
                    wanted.Add((h, skew));
                }

            // WPF_ARC_GASP=all ships ONE range asking for everything, so symmetric smoothing is on
            // at every size -- which is how the band this probe found is isolated: Times' own gasp
            // leaves it OFF below 18ppem, and that is exactly where the two rasterizers part.
            SyntheticFont.GaspRanges =
                Environment.GetEnvironmentVariable("WPF_ARC_GASP") switch
                {
                    "times" => new (int, int)[] { (8, 0xA), (17, 0x5), (0xFFFF, 0xF) },
                    "all" => new (int, int)[] { (0xFFFF, 0xF) },
                    "nosym" => new (int, int)[] { (0xFFFF, 0x7) },
                    _ => null,
                };
            SyntheticFont.ScanControl =
                Environment.GetEnvironmentVariable("WPF_ARC_SCAN") == "times" ? (303, 1) : null;
            byte[] fontBytes;
            try { fontBytes = SyntheticFont.Build(Family, bars); }
            finally { SyntheticFont.GaspRanges = null; SyntheticFont.ScanControl = null; }
            int count = 0;
            IntPtr handle = AddFontMemResourceEx(fontBytes, fontBytes.Length, IntPtr.Zero, ref count);
            Assert.True(handle != IntPtr.Zero && count > 0, "GDI would not accept the arc font");

            var report = new System.Text.StringBuilder();
            report.AppendLine($"== a quadratic arc on a {width / unitsPerPixel:0.##}px chord,"
                              + $" bulging in {(axisX ? "X" : "Y")},"
                              + $" {Family} at {ppem}ppem, no glyph program");
            report.AppendLine("   bulge   along    exact   GDI ink   our ink    GDI/exact  ours/exact");
            double sumG = 0, sumO = 0, sumE = 0;
            try
            {
                var font = new TrueTypeFont(fontBytes);
                var raw = new byte[Width * Height * 4];
                for (int i = 0; i < bars.Count; i++)
                {
                    string ch = ((char) (0x41 + i)).ToString();
                    int baseline = ppem + 12;
                    Gdi.s_rawRgb = raw;
                    Gdi.Draw(ch, Family, ppem, PenX, baseline, Width, Height, false, false);
                    Gdi.s_rawRgb = null;
                    double gdi = SlabInk(raw, bgra: true);
                    byte[] oursRgba = OursRgba(font, ch, ppem, baseline, correction: true);
                    double ours = SlabInk(oursRgba, bgra: false);
                    // Two thirds of the control triangle, in pixels squared, then three lamps to
                    // the pixel.
                    // Two thirds of the control triangle |(P1-P0) x (P2-P0)| / 2, in pixels
                    // squared, then three lamps to the pixel.
                    double ax = bars[i].ArcCtrlX - bars[i].Left, ay = bars[i].ArcCtrlY - 0;
                    double bx = bars[i].ArcEndX - bars[i].Left, by = bars[i].ArcEndY - 0;
                    double exact = 3.0 * (Math.Abs(ax * by - ay * bx) / 3.0)
                                   / (unitsPerPixel * unitsPerPixel);
                    sumG += gdi; sumO += ours; sumE += exact;
                    // WPF_ARC_DUMP=<n> prints both rasters for the n'th arc, one line per inked
                    // row, as coverage per lamp. The totals say a row disagrees; only this says
                    // which rows and by how much.
                    if (Environment.GetEnvironmentVariable("WPF_ARC_DUMP") == (i + 1).ToString())
                        for (int row = 0; row < Height; row++)
                        {
                            var g = new System.Text.StringBuilder();
                            var o = new System.Text.StringBuilder();
                            for (int col = 0; col < Width; col++)
                            {
                                int k = (row * Width + col) * 4;
                                g.Append($" {255 - raw[k + 2]:000},{255 - raw[k + 1]:000},{255 - raw[k + 0]:000}");
                                o.Append($" {255 - oursRgba[k + 0]:000},{255 - oursRgba[k + 1]:000},{255 - oursRgba[k + 2]:000}");
                            }
                            string gs = g.ToString(), os2 = o.ToString();
                            if (gs.Replace(" 000,000,000", "").Trim().Length == 0
                                && os2.Replace(" 000,000,000", "").Trim().Length == 0) continue;
                            report.AppendLine($"   GDI  row {row}:{gs.Substring(60, Math.Min(216, gs.Length - 60))}");
                            report.AppendLine($"   ours row {row}:{os2.Substring(60, Math.Min(216, os2.Length - 60))}");
                        }
                    report.AppendLine($"   {wanted[i].H,6:0.00} {wanted[i].Skew,5:0.00}"
                                      + $" {exact,9:0.00} {gdi,9:0.00} {ours,9:0.00}"
                                      + $" {gdi / exact,12:0.0000} {ours / exact,11:0.0000}");
                }
                report.AppendLine($"   TOTAL             {sumE,9:0.00} {sumG,9:0.00} {sumO,9:0.00}"
                                  + $" {sumG / sumE,12:0.0000} {sumO / sumE,11:0.0000}");
            }
            finally { RemoveFontMemResourceEx(handle); }
            Console.Error.Write(report.ToString());
        }

        /// <summary>OUR PIXELS BESIDE GDI'S, for any face -- `WPF_PICTURE=family/char/ppem[/B|I]`.
        /// <para>Every picture in this file until now was Segoe UI, because the ratchets are: the
        /// per-glyph tests all draw ProbeFamily. So Times and Arial were being read through totals
        /// and edge lists alone, and "4.2% light over 12 pixels" does not say which row. This
        /// prints the same three character maps the ratchets print -- windows, ours, difference --
        /// for whatever face is asked for.</para></summary>
        [Fact]
        public void HowOneGlyphCompares()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the comparison");
            string? spec = Environment.GetEnvironmentVariable("WPF_PICTURE");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_PICTURE=family/char/ppem[/B|I]");
            string[] parts = spec!.Split('/');
            string family = parts[0], text = parts[1];
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(family, bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {family}");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            const int Base = 28;
            byte[] windows = Gdi.Draw(text, family, ppem, PenX, Base, Width, Height, bold, italic);
            byte[] rgba = OursRgba(font, text, ppem, Base, correction: true);
            var ours = new byte[Width * Height];
            for (int i = 0; i < ours.Length; i++) ours[i] = (byte) (255 - rgba[i * 4 + 1]);

            Difference diff = Difference.Between(windows, ours, Width, Height);
            long theirs = 0, mine = 0;
            for (int i = 0; i < ours.Length; i++) { theirs += windows[i]; mine += ours[i]; }
            Console.Error.WriteLine($"== '{text}' {family}@{ppem}{(style.Length > 0 ? "/" + style : "")}"
                                    + $"  ink ours {mine} gdi {theirs} ratio {(theirs == 0 ? 0 : mine / (double) theirs):0.000}");
            Console.Error.Write(diff.Describe(text, windows, ours, Width, Height));
            // WPF_PICTURE_NUM=1 prints the coverage NUMBERS for every row that holds ink. The
            // character map buckets a lamp into one of five glyphs, which is too coarse to read a
            // stem's width off: 1.19px and 1.00px both print as '#'.
            if (Environment.GetEnvironmentVariable("WPF_PICTURE_NUM") == "1")
                for (int y = 0; y < Height; y++)
                {
                    bool any = false;
                    for (int x = 0; x < Width && !any; x++)
                        any = windows[y * Width + x] > 8 || ours[y * Width + x] > 8;
                    if (!any) continue;
                    var g = new System.Text.StringBuilder($"   row {y,2} gdi ");
                    var o = new System.Text.StringBuilder($"   row {y,2} our ");
                    for (int x = 0; x < Width; x++)
                    {
                        if (windows[y * Width + x] == 0 && ours[y * Width + x] == 0) continue;
                        g.Append($" {x}:{windows[y * Width + x],3}");
                        o.Append($" {x}:{ours[y * Width + x],3}");
                    }
                    Console.Error.WriteLine(g.ToString());
                    Console.Error.WriteLine(o.ToString());
                }
        }

        /// <summary>Total ink in the frame, in lamps (255 = one lamp fully covered).</summary>
        private static double SlabInk(byte[] rgba, bool bgra)
        {
            long total = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
                for (int c = 0; c < 3; c++) total += 255 - rgba[i + c];
            return total / 255.0;
        }

        /// <summary>OUR TWO RENDERING PATHS, SIDE BY SIDE, against GDI.
        /// WPF_TWOPATHS=family/char/ppem[/B|I].
        /// <para>They are supposed to be the same picture and at 8ppem they are not. The weight
        /// report -- which IS the holdout -- draws through GlyphRunDraw, the shipped glyph-run
        /// path; SolveGdisOutlineXy draws the SAME outline as a plain GeometryFill. At 10, 12 and
        /// 16ppem the two agree glyph for glyph. At 8 they disagree in both directions: Times 'p'
        /// scores 477 through the run path and 0 through the geometry one, while Arial 'o' scores
        /// 0 and 86 the other way round. Both ask GDI for the same bitmap at the same pen, so the
        /// difference is ours.</para>
        /// <para>Every candidate checked from the code came back clean -- hintPpem is 8 at that
        /// size, so PixelAligned is set and the painter takes the hinted branch; hintScale is 1;
        /// ScaleFigures is an affine map with no rounding; the pen is the same integer. So the
        /// difference had to be looked at rather than reasoned about.</para>
        /// <para>AND LOOKING AT IT REVERSED THE CONCLUSION. Drawn here, Times 'p'@8 scores 477
        /// through the run path and 1,026 through the plain-geometry one -- the SHIPPED path is the
        /// better of the two, and the solver's 0 is what cannot be reproduced. So "the glyph-run
        /// path adds error" was wrong; the solver's 8ppem numbers are simply not measuring what the
        /// holdout measures, which is what its own caveat now says. 'b' is 228 against 719, 'n' 74
        /// against 575, 'o' 38 against 810: the same way round every time.</para>
        /// <para>What the rasters DO show is a real and consistent signature at 8ppem: one stem
        /// column is a single digit lighter than GDI's, in glyph after glyph. 'N' reads 3/3/3/7/4
        /// down its right stem where GDI reads 4/4/4/9/5; 'b' reads 1 where GDI reads 3. We are
        /// uniformly a shade light -- the ink ratio is 0.994 at 8ppem against 1.000 at 16 -- which
        /// is a coverage question rather than a placement one, since the L and R edge deltas and
        /// the width deltas are all exactly zero.</para>
        /// <para>NOT the contrast palette, which was the obvious suspect because all six faces SET
        /// SYMMETRIC_SMOOTHING at &lt;= 8 and clear it from 9 up, so the auto rule switches exactly
        /// where the error jumps. It does not: WPF_CT_CONTRAST 0 and 2 measure identically
        /// (109,380 over ppem 8-10) and 1 is catastrophic (6,055,865), so the contrast palette is
        /// never selected on this specimen and cannot be what changes at 8.</para></summary>
        [Fact]
        public void HowOurTwoPathsDiffer()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_TWOPATHS");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_TWOPATHS=family/char/ppem[/style]");
            string[] parts = spec!.Split('/');
            string family = parts[0], ch = parts[1];
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');
            string? file = FontFiles.Find(family, bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
            int baseline = 28;

            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(ch, family, ppem, PenX, baseline, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;

            // (1) the shipped glyph-run path, exactly as the weight report draws it
            byte[] runPath = OursRgba(font, ch, ppem, baseline, correction: true);

            // (2) the plain-geometry path, exactly as SolveGdisOutlineXy draws it
            bool savedSubpix = TrueTypeFont.SubpixelFitting;
            byte[] geomPath;
            try
            {
                TrueTypeFont.SubpixelFitting = true;
                int gid = font.GlyphIndex(ch[0]);
                Assert.True(((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem,
                                out List<PathFigure> fitted) && fitted.Count > 0, "no outline");
                var placed = new List<PathFigure>(fitted.Count);
                Vector2 M(Vector2 p) => new(PenX + p.X, baseline + p.Y);
                foreach (PathFigure f in fitted)
                {
                    var nf = new PathFigure(M(f.Start)) { Closed = f.Closed };
                    foreach (PathSegment sg in f.Segments)
                        nf.Segments.Add(sg switch
                        {
                            LineSegment l => new LineSegment(M(l.Point)),
                            QuadraticBezierSegment q =>
                                new QuadraticBezierSegment(M(q.Control), M(q.Point)),
                            CubicBezierSegment c3 =>
                                new CubicBezierSegment(M(c3.Control1), M(c3.Control2), M(c3.Point)),
                            _ => sg,
                        });
                    placed.Add(nf);
                }
                var root = new SceneVisual();
                root.Content.Add(new GeometryFill(new PathGeometry(FillRule.NonZero, placed),
                    new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)), isGlyph: true)
                    { PixelAligned = true });
                var renderer = NewRenderer(font);
                renderer.TextBlendCorrection = true;
                geomPath = renderer.RenderToRgba(root, Width, Height,
                    RgbaColor.FromBytes(255, 255, 255, 255));
            }
            finally { TrueTypeFont.SubpixelFitting = savedSubpix; }

            long Score(byte[] ours)
            {
                long sum = 0;
                for (int i = 0; i < Width * Height; i++)
                    for (int ch2 = 0; ch2 < 3; ch2++)
                        sum += Math.Abs(raw[i * 4 + (2 - ch2)] - ours[i * 4 + ch2]);
                return sum;
            }

            int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    bool ink = raw[(y * Width + x) * 4] < 250 || raw[(y * Width + x) * 4 + 1] < 250
                               || runPath[(y * Width + x) * 4] < 250
                               || geomPath[(y * Width + x) * 4] < 250;
                    if (!ink) continue;
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x); right = Math.Max(right, x);
                }
            Assert.True(bottom >= 0, "nothing drawn");

            var rep = new System.Text.StringBuilder();
            rep.AppendLine($"== '{ch}' {family}@{ppem}{style}   glyph-run path {Score(runPath)},"
                           + $"  plain-geometry path {Score(geomPath)}");
            rep.AppendLine($"   rows {top}..{bottom} cols {left}..{right}"
                           + "   GDI | run path | geometry path   (green channel, digit = ink/255*9)");
            for (int y = top; y <= bottom; y++)
            {
                var line = new System.Text.StringBuilder("   ");
                foreach (byte[] img in new[] { (byte[]) null!, runPath, geomPath })
                {
                    for (int x = left; x <= right; x++)
                    {
                        int i = (y * Width + x) * 4;
                        int v = img is null ? 255 - raw[i + 1] : 255 - img[i + 1];
                        line.Append(v <= 0 ? '.' : (char) ('0' + Math.Min(9, (v * 9 + 127) / 255)));
                    }
                    line.Append("  |  ");
                }
                rep.AppendLine(line.ToString());
            }
            Console.Error.Write(rep.ToString());
        }

        /// <summary>GDI AGAINST ITSELF: the same glyph drawn bi-level, greyscale and ClearType,
        /// side by side. WPF_GDI_QUALITY=family/char/ppem[/B|I].
        /// <para>Built for one question. Times Bold's 'o' at 16ppem squeezes its counter to 1.4px
        /// in the glyph program and re-opens it to 3.4px with four whole-pixel DELTAPs that come
        /// AFTER IUP[y]; itrp_DeltaEngine, as read, drops those in ClearType, and so do we -- yet
        /// GDI's ClearType counter is plainly open. Nothing we render can settle whether the
        /// binary was misread or the deltas are not the mechanism; only GDI's own bi-level
        /// bitmap next to its own ClearType one can.</para></summary>
        [Fact]
        public void HowGdiDrawsOneGlyphAtEachQuality()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? spec = Environment.GetEnvironmentVariable("WPF_GDI_QUALITY");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_GDI_QUALITY=family/char/ppem[/style]");
            string[] parts = spec!.Split('/');
            string family = parts[0], ch = parts[1];
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== GDI '{ch}' {family} @{ppem}{(style.Length > 0 ? "/" + style : "")}"
                              + " at quality 3 (NONANTIALIASED), 4 (ANTIALIASED), 5 (CLEARTYPE)");
            var maps = new List<string[]>();
            int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
            var greys = new List<byte[]>();
            foreach (int q in new[] { 3, 4, 5 })
            {
                byte[] grey = Gdi.Draw(ch, family, ppem, PenX, ppem + 12, Width, Height, bold, italic, quality: q);
                greys.Add(grey);
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        if (grey[y * Width + x] > 0)
                        { top = Math.Min(top, y); bottom = Math.Max(bottom, y); left = Math.Min(left, x); right = Math.Max(right, x); }
            }
            Assert.True(bottom >= 0, "GDI drew nothing");
            report.AppendLine($"   rows {top}..{bottom}, cols {left}..{right}   (digit = ink/255*9, '.' = none)");
            report.AppendLine($"   {"bi-level (3)".PadRight(right - left + 4)}{"grey (4)".PadRight(right - left + 4)}clearType (5)");
            for (int y = top; y <= bottom; y++)
            {
                var line = new System.Text.StringBuilder("   ");
                foreach (byte[] grey in greys)
                {
                    for (int x = left; x <= right; x++)
                    {
                        int v = grey[y * Width + x];
                        line.Append(v == 0 ? '.' : (char) ('0' + Math.Min(9, (v * 9 + 127) / 255)));
                    }
                    line.Append("   ");
                }
                report.AppendLine(line.ToString());
            }
            Console.Error.Write(report.ToString());
        }

        /// <summary>ONE GLYPH, OUR SHIPPED CLEARTYPE PIXELS BESIDE GDI'S, AND THE DIFFERENCE.
        /// WPF_GLYPHDIFF=family/char/ppem[/B|I].
        /// <para>Every other one-glyph probe here shows something else. HowGdiDrawsOneGlyphAtEach-
        /// Quality shows GDI against GDI and never us. OneGlyphAgainstClearTypeGeometry is labelled
        /// "ours (y-only fit)" and really is y-only, so its widths are not our widths and reading x
        /// out of it is a mistake that has been made. StemLamps_ReadAgainstGdis prints exact lamps
        /// but only at ppem 10..14. The per-glyph scorer gives a number and no picture. So when the
        /// score says Arial 'K' and 'z' carry half of a 20ppem row there is nothing that shows what
        /// is actually different about them, and this closes that.</para>
        /// <para>Three maps: ours, GDI's, and |difference| -- each cell a digit, ink/255*9, from the
        /// GREEN lamp, which is the pixel centre. Then the per-channel numbers for the rows that
        /// disagree most, because a fringe is a triple and the digit map averages the argument
        /// away.</para></summary>
        [Fact]
        public void OneGlyphOursBesideGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_GLYPHDIFF");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_GLYPHDIFF=family/char/ppem[/style]");
            string[] parts = spec!.Split('/');
            string family = parts[0], ch = parts[1];
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(family, bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            byte[] bytes = File.ReadAllBytes(file!);
            int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);

            int baseline = ppem + 12;
            var raw = new byte[Width * Height * 4];
            Gdi.s_rawRgb = raw;
            Gdi.Draw(ch, family, ppem, PenX, baseline, Width, Height, bold, italic);
            Gdi.s_rawRgb = null;
            byte[] ours = OursRgba(font, ch, ppem, baseline, correction: true);

            // GDI's DIB is BGRA, ours RGBA. Channel c of pixel i: theirs at 2-c, ours at c.
            int Theirs(int i, int c) => raw[i * 4 + (2 - c)];
            int Ours(int i, int c) => ours[i * 4 + c];

            int top = int.MaxValue, bottom = -1, left = int.MaxValue, right = -1;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    bool ink = false;
                    for (int c = 0; c < 3; c++)
                        if (Theirs(i, c) < 250 || Ours(i, c) < 250) ink = true;
                    if (!ink) continue;
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x); right = Math.Max(right, x);
                }
            Assert.True(bottom >= 0, "neither side drew anything");

            var rep = new System.Text.StringBuilder();
            long total = 0;
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                    for (int c = 0; c < 3; c++)
                        total += Math.Abs(Theirs(y * Width + x, c) - Ours(y * Width + x, c));
            rep.AppendLine($"== '{ch}' {family}@{ppem}{(style.Length > 0 ? "/" + style : "")}"
                           + $"   rows {top}..{bottom}, cols {left}..{right}   sum|d| {total}");
            int w = right - left + 1;
            rep.AppendLine("   " + "ours".PadRight(w + 3) + "gdi".PadRight(w + 3) + "|difference|");
            var rowSum = new long[bottom - top + 1];
            for (int y = top; y <= bottom; y++)
            {
                var a = new System.Text.StringBuilder();
                var b = new System.Text.StringBuilder();
                var d = new System.Text.StringBuilder();
                for (int x = left; x <= right; x++)
                {
                    int i = y * Width + x;
                    int og = 255 - Ours(i, 1), tg = 255 - Theirs(i, 1);
                    int md = 0;
                    for (int c = 0; c < 3; c++)
                        md = Math.Max(md, Math.Abs(Theirs(i, c) - Ours(i, c)));
                    rowSum[y - top] += md;
                    a.Append(og == 0 ? '.' : (char) ('0' + Math.Min(9, (og * 9 + 127) / 255)));
                    b.Append(tg == 0 ? '.' : (char) ('0' + Math.Min(9, (tg * 9 + 127) / 255)));
                    d.Append(md == 0 ? '.' : (char) ('0' + Math.Min(9, (md * 9 + 127) / 255)));
                }
                rep.AppendLine($"   {a}   {b}   {d}");
            }

            // The three worst rows, per channel, because a ClearType edge is a triple and the
            // digit map above has already averaged the interesting part away.
            var order = new List<int>();
            for (int r = 0; r < rowSum.Length; r++) order.Add(r);
            order.Sort((p, q) => rowSum[q].CompareTo(rowSum[p]));
            for (int k = 0; k < Math.Min(3, order.Count) && rowSum[order[k]] > 0; k++)
            {
                int y = top + order[k];
                rep.AppendLine($"   row {y} (|d| {rowSum[order[k]]}):   col  ours(r,g,b)   gdi(r,g,b)");
                for (int x = left; x <= right; x++)
                {
                    int i = y * Width + x;
                    if (Ours(i, 0) == Theirs(i, 0) && Ours(i, 1) == Theirs(i, 1)
                        && Ours(i, 2) == Theirs(i, 2)) continue;
                    rep.AppendLine($"      {x,4}   {Ours(i, 0),3},{Ours(i, 1),3},{Ours(i, 2),3}"
                                   + $"     {Theirs(i, 0),3},{Theirs(i, 1),3},{Theirs(i, 2),3}");
                }
            }
            Console.Error.Write(rep.ToString());
        }

        private static readonly Dictionary<string, int> InkAllowed = new()
        {
            ["b@10"] = 16,
            ["b@11"] = 14,
            ["b@12"] = 14,
            ["b@13"] = 1,
            ["b@14"] = 5,
            ["b@15"] = 2,
            ["b@16"] = 4,
            ["b@17"] = 10,
            ["b@18"] = 2,
            ["b@19"] = 5,
            ["b@20"] = 0,
            ["bi@10"] = 1,
            ["bi@11"] = 0,
            ["bi@12"] = 1,
            ["bi@13"] = 0,
            ["bi@14"] = 1,
            ["bi@15"] = 1,
            ["bi@16"] = 3,
            ["bi@17"] = 1,
            ["bi@18"] = 1,
            ["bi@19"] = 1,
            ["i@10"] = 3,
            ["i@11"] = 1,
            ["i@12"] = 3,
            ["i@13"] = 13,
            ["i@14"] = 1,
            ["i@15"] = 1,
            ["i@16"] = 6,
            ["i@17"] = 5,
            ["i@18"] = 1,
            ["i@19"] = 2,
            ["i@20"] = 1,
            ["regular@10"] = 17,
            ["regular@11"] = 15,
            ["regular@12"] = 27,
            ["regular@13"] = 4,
            ["regular@14"] = 1,
            ["regular@15"] = 12,
            ["regular@16"] = 6,
            ["regular@17"] = 14,
            ["regular@18"] = 10,
            ["regular@19"] = 8,
            ["regular@20"] = 4,
        };



        // ---- is there a POINTWISE relationship at all? ------------------------------------------
        //
        // Every correction tried so far -- a gamma, a contrast curve, a filter width, a blend space --
        // is a FUNCTION OF ONE PIXEL'S COVERAGE. Such a thing can only work if GDI's value at a pixel
        // is predictable from ours at that pixel. Nobody had measured whether it is.
        //
        // So: bin every lamp of every pixel of the whole repertoire by OUR coverage, and report what
        // GDI put there -- the mean, and the spread. A tight spread means a lookup table fixes this
        // and the only question is its shape. A wide one means the difference is structural, no curve
        // can express it, and the six failed attempts were failing for a reason.

        [Theory]
        [MemberData(nameof(FacesAndSizes))]
        public void OurCoverageAgainstGdis_HasAPointwiseShape(bool bold, bool italic, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? report = Environment.GetEnvironmentVariable("WPF_TRANSFER_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(report), "set WPF_TRANSFER_REPORT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no Segoe UI bold={bold} italic={italic}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            int baseline = ppem + 12;
            const int Bins = 16;
            var n = new long[Bins];
            var sum = new double[Bins];
            var sumSq = new double[Bins];
            var raw = new byte[Width * Height * 4];

            foreach (string text in Repertoire)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, bold, italic);
                Gdi.s_rawRgb = null;
                byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);

                for (int i = 0; i < mine.Length; i += 4)
                    for (int c = 0; c < 3; c++)
                    {
                        // Coverage, not colour: black ink on white paper, so 255 - value.
                        int ours = 255 - mine[i + c];
                        int gdi = 255 - raw[i + (2 - c)];      // raw is BGRA; see StageC
                        // BOTH must have ink. Mixing in the pixels where only one side inked at all
                        // measures structural disagreement, not tone, and it swamps everything.
                        if (ours < 8 || gdi < 8) continue;
                        int b = Math.Min(Bins - 1, ours * Bins / 256);
                        n[b]++; sum[b] += gdi; sumSq[b] += (double)gdi * gdi;
                    }
            }

            var report_ = new System.Text.StringBuilder();
            string style = (bold ? "b" : "") + (italic ? "i" : "");
            for (int b = 0; b < Bins; b++)
            {
                if (n[b] == 0) continue;
                double mean = sum[b] / n[b];
                double sd = Math.Sqrt(Math.Max(0, sumSq[b] / n[b] - mean * mean));
                report_.Append($"{(style.Length == 0 ? "regular" : style)}@{ppem} bin{b * 16,4} "
                               + $"n={n[b],8} ourMid={b * 16 + 8,4} gdiMean={mean,7:0.0} sd={sd,6:0.0}")
                       .Append(Environment.NewLine);
            }

            lock (Repertoire) File.AppendAllText(report!, report_.ToString());
        }
        // ---- our HINTED OUTLINE against GDI's, with the filter taken out of it -----------------
        //
        // Every earlier comparison here reads RENDERED coverage, so a difference could be the fitted
        // outline or the ClearType filter over it and there was no way to tell which. GDI will hand
        // over the hinted glyph's own metrics -- GetGlyphOutline(GGO_METRICS) returns the ink box in
        // whole pixels and where it sits against the pen, AFTER the face's program has run and
        // BEFORE anything is filtered. Comparing that with the bounds of our hinted outline asks one
        // question and only one: does our interpreter put the glyph where GDI's does.
        //
        // Reported, not asserted -- and READ IT WITH CARE, because it answers a narrower question than
        // it looks like. GDI's black box is integral by construction and reflects its DEFAULT x+y
        // hinting (lfQuality does not change what the metrics API returns), while ClearType renders
        // from a y-only fitting; and an outline BOUND is not an ink box, since a curve's leftmost
        // point carries almost no coverage. Both of those were mistaken for real differences before
        // being caught. Rounding x to whole pixels makes THIS number four times better and the
        // rendered coverage five times worse. Rendered coverage is the parity reference; this is
        // only "how far our outline is from GDI's FULLY hinted one". WPF_HINT_REPORT names a file.

        [Theory]
        [MemberData(nameof(FacesAndSizes))]
        public void HintedOutlines_SitWhereGdiPutsThem(bool bold, bool italic, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? report = Environment.GetEnvironmentVariable("WPF_HINT_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(report), "set WPF_HINT_REPORT to collect this");
            string? file = FontFiles.Find(ProbeFamily(), bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no Segoe UI bold={bold} italic={italic}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var hinted = (IHintedGlyphFont)(object)font;
            string style = (bold ? "b" : "") + (italic ? "i" : "");
            var lines = new System.Text.StringBuilder();

            foreach (char c in "TlixCmoneaHIBDOSbdfgh0123456789")
            {
                (int index, int gdiOx, int gdiBox, int _) = Gdi.HintedMetrics(c, "Segoe UI", ppem, bold, italic);
                if (index == 0 || gdiBox == 0) continue;
                if (!hinted.TryGetHintedOutline(index, ppem, out List<PathFigure> figs) || figs.Count == 0)
                    continue;

                float minX = float.MaxValue, maxX = float.MinValue;
                foreach (PathFigure f in figs)
                {
                    Bounds(f.Start, ref minX, ref maxX);
                    foreach (PathSegment s in f.Segments)
                        foreach (Vector2 p in Points(s)) Bounds(p, ref minX, ref maxX);
                }
                if (minX > maxX) continue;

                // GDI reports the box in WHOLE pixels: origin is floor of the left edge and the box
                // is wide enough to cover the right. Ours is a real outline, so compare the same way.
                int ourOx = (int)MathF.Floor(minX);
                int ourBox = (int)MathF.Ceiling(maxX) - ourOx;
                if (ourOx != gdiOx || ourBox != gdiBox)
                    lines.Append($"{style}@{ppem} '{c}' gdi ox={gdiOx} box={gdiBox} | "
                                 + $"ours ox={ourOx} box={ourBox} (x {minX:0.00}..{maxX:0.00})")
                         .Append(Environment.NewLine);
            }

            if (lines.Length > 0)
                lock (Repertoire) File.AppendAllText(report!, lines.ToString());
        }

        private static void Bounds(Vector2 p, ref float minX, ref float maxX)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
        }

        private static IEnumerable<Vector2> Points(PathSegment s)
        {
            switch (s)
            {
                case LineSegment l: yield return l.Point; break;
                case QuadraticBezierSegment q: yield return q.Control; yield return q.Point; break;
                case CubicBezierSegment c: yield return c.Control1; yield return c.Control2; yield return c.Point; break;
            }
        }
        // ---- how DARK the ink is -------------------------------------------------------------
        //
        // Everything above measures WHERE the ink is, and says so plainly: "not what shade it is".
        // That left the other half of a glyph unmeasured, and the window showed what it cost --
        // subtracting our WinForms client area against stock Windows', our text carried about a
        // tenth less ink than Windows' wherever it appeared. Nothing here objected, because nothing
        // here was looking.
        //
        // Ink is the sum of the coverage: one solid pixel and four quarter-covered ones weigh the
        // same, which is the point. A stem GDI snaps to one full column and we spread across two
        // agrees about where the glyph is and disagrees about how heavy it is, and the structural
        // test above cannot tell the difference.
        //
        // Drawn WITH the blend correction, unlike the structural test, because this is the question
        // "does the text a user sees weigh what Windows' weighs" and the correction is part of what
        // they see.

        [Theory]
        [MemberData(nameof(FacesAndSizes))]
        public void TheWholeRepertoire_CarriesAsMuchInkAsWindows(bool bold, bool italic, int ppem)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string? file = FontFiles.Find(ProbeFamily(), bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no Segoe UI bold={bold} italic={italic}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            string style = (bold ? "b" : "") + (italic ? "i" : "");
            int baseline = ppem + 12;

            // ALL THREE LAMPS, not the green one the structural test reads. A subpixel filter moves
            // ink sideways between lamps, so a green-only total silently loses whatever leaves the
            // centre lamp for a neighbour's red or blue -- and that is exactly the difference this is
            // here to catch. Read green only and a stem GDI puts in one solid lamp and we spread
            // across five weighs the same.
            long theirs = 0, ours = 0;
            var raw = new byte[Width * Height * 4];
            // Per LAMP as well as in total. Summing the three hides which of them is short, and they
            // are not short together: a stem saturates the middle lamp on both sides while the
            // FRINGE lives in the outer two, so a curve that crushes small values costs the outer
            // lamps everything and the middle one nothing.
            var theirLamp = new long[3];
            var ourLamp = new long[3];
            foreach (string text in Repertoire)
            {
                Gdi.s_rawRgb = raw;
                Gdi.Draw(text, ProbeFamily(), ppem, PenX, baseline, Width, Height, bold, italic);
                Gdi.s_rawRgb = null;
                for (int i = 0; i < raw.Length; i += 4)
                {
                    theirs += 765 - raw[i] - raw[i + 1] - raw[i + 2];
                    for (int c = 0; c < 3; c++) theirLamp[c] += 255 - raw[i + c];
                }

                byte[] mine = OursRgba(font, text, ppem, baseline, correction: true);
                long oneTheirs = 0, oneOurs = 0;
                for (int i = 0; i < raw.Length; i += 4) oneTheirs += 765 - raw[i] - raw[i + 1] - raw[i + 2];
                for (int i = 0; i < mine.Length; i += 4)
                {
                    ours += 765 - mine[i] - mine[i + 1] - mine[i + 2];
                    oneOurs += 765 - mine[i] - mine[i + 1] - mine[i + 2];
                    for (int c = 0; c < 3; c++) ourLamp[c] += 255 - mine[i + c];
                }

                // Per STRING, so a face-and-size that is light says WHICH letters are light. An
                // aggregate can only ever say that something is.
                string? detail = Environment.GetEnvironmentVariable("WPF_INK_DETAIL");
                if (!string.IsNullOrEmpty(detail) && oneTheirs > 0)
                    lock (Repertoire)
                        File.AppendAllText(detail!,
                            $"{(style.Length == 0 ? "regular" : style)}@{ppem}	{text}	"
                            + $"{(double)oneOurs / oneTheirs:0.0000}" + Environment.NewLine);
            }

            string? lampRep = Environment.GetEnvironmentVariable("WPF_LAMP_REPORT");
            if (!string.IsNullOrEmpty(lampRep))
                lock (Repertoire)
                    File.AppendAllText(lampRep!,
                        $"{(style.Length == 0 ? "regular" : style)}@{ppem}"
                        + $" b={(theirLamp[0] == 0 ? 0 : (double)ourLamp[0] / theirLamp[0]):0.0000}"
                        + $" g={(theirLamp[1] == 0 ? 0 : (double)ourLamp[1] / theirLamp[1]):0.0000}"
                        + $" r={(theirLamp[2] == 0 ? 0 : (double)ourLamp[2] / theirLamp[2]):0.0000}"
                        + Environment.NewLine);

            Assert.True(theirs > 0, "GDI drew nothing to compare against");
            double ratio = (double)ours / theirs;
            int off = (int)Math.Round(Math.Abs(ratio - 1.0) * 10000);

            string? inkRep = Environment.GetEnvironmentVariable("WPF_INK_REPORT");
            if (!string.IsNullOrEmpty(inkRep))
                lock (Repertoire)
                    File.AppendAllText(inkRep!, $"{(style.Length == 0 ? "regular" : style)}@{ppem} {ratio:0.0000}"
                                                + Environment.NewLine);
            string key = $"{(style.Length == 0 ? "regular" : style)}@{ppem}";
            RecordActual(key, off);
            int allowed = InkAllowed.TryGetValue(key, out int n) ? n : 0;
            string said = $"{key}: our ink is {ratio:0.0000} of Windows' "
                        + $"({(ratio < 1 ? "lighter" : "heavier")} by {off / 100.0:0.00}%, "
                        + $"ours {ours} against {theirs})";
            Assert.True(off <= allowed, said + $" -- more than the {allowed / 100.0:0.00}% allowed.");
            Assert.True(off >= allowed,
                        said + $" -- closer than the {allowed / 100.0:0.00}% allowed says. Lower its "
                        + "entry, or drop it.");
        }

        // ---- the comparison ----------------------------------------------------------------------

        private readonly struct Difference
        {
            public readonly int Pixels;      // how many disagree at all
            public readonly int Worst;       // by how much, at the worst one
            public readonly long Total;      // and in total
            public readonly int Structural;  // and how many disagree about whether there is ink

            private Difference(int pixels, int worst, long total, int structural)
            {
                Pixels = pixels; Worst = worst; Total = total; Structural = structural;
            }

            public static Difference Between(byte[] a, byte[] b, int w, int h)
            {
                int pixels = 0, worst = 0, structural = 0;
                long total = 0;
                for (int i = 0; i < w * h; i++)
                {
                    // Covered by one and all but bare in the other. The band between is where the
                    // two antialiasers disagree about shade, which they always will.
                    if ((a[i] >= 128 && b[i] < 32) || (b[i] >= 128 && a[i] < 32)) structural++;

                    int d = Math.Abs(a[i] - b[i]);
                    if (d == 0) continue;
                    pixels++;
                    total += d;
                    if (d > worst) worst = d;
                }
                return new Difference(pixels, worst, total, structural);
            }

            /// <summary>What differs, as something that can be acted on: the numbers, then the two
            /// images as character maps of the region that actually holds ink. A count on its own
            /// says a test failed; the maps say which letter and which edge of it.</summary>
            public string Describe(string text, byte[] windows, byte[] ours, int w, int h)
            {
                var marks = new byte[w * h];
                for (int i = 0; i < w * h; i++) marks[i] = (byte)Math.Abs(windows[i] - ours[i]);

                // ONE box for all three, covering whatever any of them touches. Cropping each to its
                // own ink lines the two images up on their left edges and hides the very thing being
                // looked for -- a glyph half a pixel over reads as identical.
                (int X0, int Y0, int X1, int Y1) box = Union(Box(windows, w, h), Box(ours, w, h));
                if (box.X1 < box.X0) return $"'{text}': neither image has any ink.";

                var report = new System.Text.StringBuilder();
                report.AppendLine($"'{text}': {Structural} pixels covered differently; {Pixels} "
                                  + $"differ in shade (worst {Worst}/255, {Total} in total).");
                report.AppendLine($"columns {box.X0}..{box.X1}, rows {box.Y0}..{box.Y1}");
                report.AppendLine("windows:");
                report.Append(Map(windows, w, box));
                report.AppendLine("ours:");
                report.Append(Map(ours, w, box));
                report.AppendLine("difference:");
                report.Append(Map(marks, w, box));
                return report.ToString();
            }

            private static (int X0, int Y0, int X1, int Y1) Box(byte[] cover, int w, int h)
            {
                int x0 = w, x1 = -1, y0 = h, y1 = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (cover[y * w + x] > 8)
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
                return (x0, y0, x1, y1);
            }

            private static (int X0, int Y0, int X1, int Y1) Union((int X0, int Y0, int X1, int Y1) a,
                                                                 (int X0, int Y0, int X1, int Y1) b)
            {
                if (a.X1 < a.X0) return b;
                if (b.X1 < b.X0) return a;
                return (Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0),
                        Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));
            }

            private static string Map(byte[] cover, int w, (int X0, int Y0, int X1, int Y1) box)
            {
                const string Ramp = " .:*#";
                var text = new System.Text.StringBuilder();
                for (int y = box.Y0; y <= box.Y1; y++)
                {
                    text.Append("  ");
                    for (int x = box.X0; x <= box.X1; x++)
                        text.Append(Ramp[Math.Min(4, cover[y * w + x] * 5 / 256)]);
                    text.AppendLine();
                }
                return text.ToString();
            }
        }

        // ---- Windows' own rasterizer -------------------------------------------------------------

        /// <summary>GDI, drawing into a memory bitmap we can read back. Not System.Drawing: this
        /// process may have a ported System.Drawing loaded, and the whole point is to ask the real
        /// Windows for its answer.</summary>
        [SupportedOSPlatform("windows")]
        /// <summary>The face every probe here measures. WPF_FACE picks another.
        /// <para>These were all pinned to Segoe UI, which is exactly how the renderer came to be
        /// tuned against the one face that barely exercises the hinter -- the probes could not see
        /// any other. A diagnostic that can only look at the case that works is not a diagnostic.</para>
        /// </summary>
        private static string ProbeFamily() =>
            Environment.GetEnvironmentVariable("WPF_FACE") is string f && f.Length > 0 ? f : "Segoe UI";


        /// <summary>Per character: the advance GDI lays out with, against the advance we lay out
        /// with. Set WPF_ADVANCES to family@ppem, with :B or :I for bold or italic.
        /// <para>An advance is the one glyph property whose error ACCUMULATES. A quarter of a pixel
        /// per letter is nothing to look at on one letter and thirteen pixels of drift by the end
        /// of a line -- which is what Verdana Regular was doing. Every glyph past the third landed
        /// on different pixels from Windows', and the band read as a fitting problem when it was a
        /// spacing one.</para>
        /// <para>Reported only: set WPF_ADVANCES.</para></summary>
        [Fact]
        public void LayoutAdvances_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows lays out the reference");
            string spec = Environment.GetEnvironmentVariable("WPF_ADVANCES") ?? "";
            Assert.SkipWhen(spec.Length == 0, "set WPF_ADVANCES to family@ppem to collect this");

            bool bold = spec.EndsWith(":B", StringComparison.Ordinal);
            bool italic = spec.EndsWith(":I", StringComparison.Ordinal);
            if (bold || italic) spec = spec.Substring(0, spec.Length - 2);
            string[] parts = spec.Split('@');
            string family = parts[0];
            int ppem = parts.Length > 1 ? int.Parse(parts[1]) : 12;

            string? file = FontFiles.Find(family, bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {family}");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            // AS THE RENDERER HAS THE STATICS, or as a bare process has them. The weight report
            // renders through a ClearType renderer, which sets both of these; this probe runs
            // before any renderer exists and measured with both off -- and Arial at 8ppem came
            // out exact here and ten pixels wide in the specimen. WPF_ADVANCES_CT=1 asks the
            // question the specimen asks.
            var log = new System.Text.StringBuilder();
            bool ct = Environment.GetEnvironmentVariable("WPF_ADVANCES_CT") == "1";
            bool savedSub = TrueTypeFont.SubpixelFitting, savedCt = TrueTypeFont.ClearTypeRendering;
            if (ct) { TrueTypeFont.SubpixelFitting = true; TrueTypeFont.ClearTypeRendering = true; }
            try
            {

            // THE SPACE IS IN HERE ON PURPOSE. It is a glyph with no ink, so every instrument
            // that looks at pixels is blind to it, and its advance displaces everything after it
            // just as surely as a letter's does.
            const string Letters = "abcdefghijklmnopqrstuvwxyz 0123456789 AKNRWXYZkvwxyz";
            int ourTotal = 0, gdiTotal = 0;
            log.AppendLine($"== {family}{(bold ? " Bold" : italic ? " Italic" : "")} @ {ppem}ppem");
            log.AppendLine($"   our file: {file}");
            foreach (char c in Letters)
            {
                int gid = font.GlyphIndex(c);
                int gdi = Gdi.LayoutAdvance(c, family, ppem, bold, italic);
                // THE PRODUCT'S OWN NUMBER, from the method the renderer steps the pen with. This
                // used to re-derive it -- the device advance where the face has one, a banker's
                // rounding of the scaled design advance where it does not -- and the re-derivation
                // put Times' SPACE at 10ppem (2.5 pixels) at 2 where the renderer, rounding away
                // from zero like GDI, has 3: a two-pixel drift that was the probe's and not ours.
                bool device = font.TryGetDeviceAdvance(gid, ppem, out _);
                float ours = font.DeviceAdvance(gid, ppem);
                int oursI = (int) MathF.Round(ours);
                ourTotal += oursI; gdiTotal += gdi;
                bool hinted = font.FaceHintsGlyph(gid, ppem);
                float linear = font.LinearAdvanceForTest(gid, ppem);
                var g = Gdi.HintedMetrics(c, family, ppem, bold, italic);
                // The right side bearing in DESIGN units, scaled: advance less the left bearing and
                // less the ink's own width. If GDI builds the advance from the hinted ink plus this,
                // the model below reproduces it.
                float rsb = font.RightSideBearingForTest(gid, ppem);
                // WPF_ADVANCES_ALL=1 lists every glyph, =linear those where the linear advance
                // rounded once disagrees with GDI -- the question being whether GDI hinted at all.
                string? listing = Environment.GetEnvironmentVariable("WPF_ADVANCES_ALL");
                bool list = listing == "1"
                    || (listing == "linear" && (int) MathF.Round(linear, MidpointRounding.AwayFromZero) != gdi);
                if (oursI != gdi || list)
                    log.AppendLine($"  '{c}' gid {gid,4}  gdi {gdi,3}  ours {oursI,3}"
                                   + $"  linear {linear,6:0.000}  faceHints={hinted}"
                                   + $"  pp {font.HintedPhantomsForTest(gid, ppem).Pp1,6:0.000}"
                                   + $" .. {font.HintedPhantomsForTest(gid, ppem).Pp2,6:0.000}"
                                   + $"  ggo {g}"
                                   + $"  inkR {g.OriginX + g.BlackBoxX}"
                                   + $"  rsb {rsb,6:0.000}"
                                   + $"  model {g.OriginX + g.BlackBoxX + (int) MathF.Round(rsb),3}"
                                   + (device ? "" : "   NO device advance"));
            }
            log.AppendLine($"  TOTAL gdi {gdiTotal}  ours {ourTotal}  drift {ourTotal - gdiTotal}");

            // The RUNNING difference, which is what the screen shows. A total of zero can hide a
            // letter that gains a pixel and another that loses one, and everything between the two
            // lands on the wrong column.
            int running = 0;
            var trail = new System.Text.StringBuilder("  running:");
            foreach (char c in Letters)
            {
                int gid = font.GlyphIndex(c);
                int gdi = Gdi.LayoutAdvance(c, family, ppem, bold, italic);
                float ours = font.DeviceAdvance(gid, ppem);
                running += (int) MathF.Round(ours) - gdi;
                trail.Append(running == 0 ? "." : running.ToString("+0;-0"));
            }
            log.AppendLine(trail.ToString());

            // And the same trail under the FALLBACK the renderer uses when it has no device advance
            // to hand -- the scaled design advance, rounded. If the screen matches this one and not
            // the one above, the renderer is not reaching the device advances at all.
            int r2 = 0;
            var t2 = new System.Text.StringBuilder("  linear :");
            foreach (char c in Letters)
            {
                int gid = font.GlyphIndex(c);
                int gdi = Gdi.LayoutAdvance(c, family, ppem, bold, italic);
                r2 += (int) MathF.Round(font.LinearAdvanceForTest(gid, ppem)) - gdi;
                t2.Append(r2 == 0 ? "." : r2.ToString("+0;-0"));
            }
            log.AppendLine(t2.ToString());

            // Where each character STARTS, so a column on a screen capture can be named.
            int cx = 0;
            var t3 = new System.Text.StringBuilder("  cumulative:");
            foreach (char c in Letters)
            {
                t3.Append($" {(c == ' ' ? '_' : c)}@{cx}");
                cx += Gdi.LayoutAdvance(c, family, ppem, bold, italic);
            }
            log.AppendLine(t3.ToString());

            // What the kerning shaper makes of a string that is nothing but kern pairs.
            string kernLine = Environment.GetEnvironmentVariable("WPF_KERNLINE") ?? "";
            if (kernLine.Length > 0)
            {
                var shaped = new List<Microsoft.Wpf.Interop.WebGpu.Composition.Text.ShapedGlyph>();
                new Microsoft.Wpf.Interop.WebGpu.Composition.Text.KerningTextShaper().Shape(font, kernLine, shaped);
                float sc = ppem / (float) font.PixelsPerEm;
                var t4 = new System.Text.StringBuilder($"  basePx={font.PixelsPerEm} sc={sc} kerns:");
                float totalKern = 0f;
                foreach (Microsoft.Wpf.Interop.WebGpu.Composition.Text.ShapedGlyph g in shaped)
                {
                    t4.Append($" {g.Kern:0.0000}/{g.Kern * sc:0.00}");
                    totalKern += MathF.Round(g.Kern * sc);
                }
                log.AppendLine(t4.ToString());
                log.AppendLine($"  rounded kern total {totalKern}");
            }
            }
            finally { TrueTypeFont.SubpixelFitting = savedSub; TrueTypeFont.ClearTypeRendering = savedCt; }
            throw new Xunit.Sdk.XunitException(log.ToString());
        }


        /// <summary>Renders the specimen line through OUR renderer and through GDI, and reports the
        /// best lamp shift in each window along it. Set WPF_RUNDRIFT to family@ppem.
        /// <para>The screen said Arial's line is exact until the space before the capitals and one
        /// whole pixel behind from there on, while every per-character advance matches GDI. This is
        /// the same measurement without a window, a compositor or a screen grab in the way.</para>
        /// </summary>
        [Fact]
        public void ARunsPen_KeepsUpWithGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string spec = Environment.GetEnvironmentVariable("WPF_RUNDRIFT") ?? "";
            Assert.SkipWhen(spec.Length == 0, "set WPF_RUNDRIFT to family@ppem");
            string[] parts = spec.Split('@');
            string family = parts[0];
            int ppem = parts.Length > 1 ? int.Parse(parts[1]) : 12;
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            // OursRgba renders into the class's own Width x Height buffer, so GDI has to be given
            // the same rectangle or the two are not pictures of the same thing.
            const string Line = "abcdefghijklmnopqrstuvwxyz 0123456789 AKNRWXYZkvwxyz";
            const int W = Width, H = Height;
            int baseline = ppem + 12;

            var gdiRgb = new byte[W * H * 4];
            Gdi.s_rawRgb = gdiRgb;
            Gdi.Draw(Line, family, ppem, PenX, baseline, W, H, false, false);
            Gdi.s_rawRgb = null;
            byte[] ours = OursRgba(font, Line, ppem, baseline, correction: true);

            var log = new System.Text.StringBuilder();
            log.AppendLine($"== {family} @ {ppem}ppem, run pen against GDI's");
            for (int x0 = 0; x0 + 20 <= W; x0 += 20)
            {
                int best = 0; long bestErr = long.MaxValue;
                for (int k = -3; k <= 3; k++)
                {
                    long err = 0;
                    for (int y = 0; y < H; y++)
                        for (int x = x0; x < x0 + 20; x++)
                            for (int lamp = 0; lamp < 3; lamp++)
                            {
                                int src = x * 3 + lamp + k;
                                if (src < 0) continue;           // C# division truncates toward zero
                                int sx = src / 3, sl = src - sx * 3;
                                if (sx >= W) continue;
                                err += Math.Abs(ours[(y * W + sx) * 4 + sl] - gdiRgb[(y * W + x) * 4 + sl]);
                            }
                    if (err < bestErr) { bestErr = err; best = k; }
                }
                if (bestErr > 0) log.AppendLine($"  x {x0,3}..{x0 + 19,3}  best {best,2} lamps  err {bestErr}");
            }
            throw new Xunit.Sdk.XunitException(log.ToString());
        }


        /// <summary>How far GDI leans a SIMULATED italic, measured off its own pixels.
        /// <para>A face that ships no italic file gets one by shearing, and the shear is a constant
        /// somebody chose. Rather than sweep ours against a specimen and keep whatever wins, ask
        /// GDI: draw a vertical stem tall enough to measure, take the leftmost inked column on each
        /// row, and the slope of that line IS the shear. Set WPF_SLANT to family@ppem.</para>
        /// </summary>
        [Fact]
        public void GdisSimulatedItalic_LeansByAMeasurableAmount()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string spec = Environment.GetEnvironmentVariable("WPF_SLANT") ?? "";
            Assert.SkipWhen(spec.Length == 0, "set WPF_SLANT to family@ppem");
            string[] parts = spec.Split('@');
            string family = parts[0];
            int ppem = parts.Length > 1 ? int.Parse(parts[1]) : 48;

            var log = new System.Text.StringBuilder();
            foreach (string glyph in new[] { "l", "H", "I" })
            {
                var raw = new byte[Width * Height * 4];
                Gdi.s_rawRgb = raw;
                Gdi.Draw(glyph, family, ppem, PenX, Height - 8, Width, Height, false, italic: true);
                Gdi.s_rawRgb = null;

                // The CENTROID of each row's ink, not its leftmost inked column. A threshold
                // quantizes the edge to whole pixels and the slope it gives wanders by a degree
                // between glyphs; a centroid is sub-pixel, and for a single vertical stem it is the
                // stem's own centre line. ('H' has two stems, so its centroid is the midpoint
                // between them -- which leans by exactly the same amount.)
                var ys = new List<double>();
                var xs = new List<double>();
                for (int y = 0; y < Height; y++)
                {
                    double mass = 0, moment = 0;
                    for (int x = 0; x < Width; x++)
                    {
                        int o = (y * Width + x) * 4;
                        double cov = (765 - raw[o] - raw[o + 1] - raw[o + 2]) / 765.0;
                        if (cov <= 0) continue;
                        mass += cov; moment += cov * x;
                    }
                    // Only rows the stem passes cleanly through: a row clipped by the glyph's top or
                    // bottom serif carries a different shape and pulls the fit.
                    if (mass > 0.5) { ys.Add(y); xs.Add(moment / mass); }
                }
                if (xs.Count < 4) { log.AppendLine($"  '{glyph}': too little ink"); continue; }

                double my = 0, mx = 0;
                for (int i = 0; i < xs.Count; i++) { my += ys[i]; mx += xs[i]; }
                my /= ys.Count; mx /= xs.Count;
                double num = 0, den = 0;
                for (int i = 0; i < xs.Count; i++) { num += (ys[i] - my) * (xs[i] - mx); den += (ys[i] - my) * (ys[i] - my); }
                // x decreases as y increases (the top leans right), so negate for a positive shear.
                double shear = den > 0 ? -num / den : 0;
                log.AppendLine($"  '{glyph}' rows={xs.Count} shear={shear:0.0000}"
                               + $"  ({Math.Atan(shear) * 180 / Math.PI:0.00} degrees)");
            }
            throw new Xunit.Sdk.XunitException($"{family} @ {ppem}ppem simulated italic" + Environment.NewLine + log);
        }


        /// <summary>How much ink GDI puts in a plain vertical stem, in LAMPS, against how much we
        /// put there and against what the face's control value asks for. WPF_STEMINK=family.
        /// <para>The contrast curve is undone first, per lamp, so the numbers are coverage and not
        /// luminance -- a three-tap box conserves coverage, so the total over a stem's run IS the
        /// raw lamp count the rasterizer lit. Constant against size means a rasterizer rule;
        /// proportional to size means a fitting one.</para></summary>
        [Fact]
        public void AStemsInk_AgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows draws the reference");
            string family = Environment.GetEnvironmentVariable("WPF_STEMINK") ?? "";
            Assert.SkipWhen(family.Length == 0, "set WPF_STEMINK to a family name");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            // Undo the contrast curve: ours applies c' = 1 - (1-c)^(1/g), so c = 1 - (1-c')^g.
            float g = WgpuSceneRenderer.TextGammaForTest;
            static float Lin(float shown, float gamma) => 1f - MathF.Pow(1f - shown, gamma);

            var log = new System.Text.StringBuilder();
            log.AppendLine($"== {family} 'l' stem, coverage in lamps (curve undone, gamma {g:0.00})");
            foreach (int ppem in new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 24 })
            {
                var raw = new byte[Width * Height * 4];
                Gdi.s_rawRgb = raw;
                Gdi.Draw("l", family, ppem, PenX, ppem + 12, Width, Height, false, false);
                Gdi.s_rawRgb = null;
                byte[] ours = OursRgba(font, "l", ppem, ppem + 12, correction: true);

                // One row through the middle of the stem, well clear of top and bottom.
                int row = ppem + 12 - ppem / 3;

                // The SAME glyph through GDI's greyscale rasterizer (ANTIALIASED_QUALITY). Greyscale
                // and ClearType share the hinting and differ only in how a covered pixel is shaded,
                // so if the two disagree about a stem's WIDTH the extra half-lamp is added by the
                // ClearType rasterizer; if they agree, it is in the fitting.
                var greyRaw = new byte[Width * Height * 4];
                Gdi.s_rawRgb = greyRaw;
                Gdi.Draw("l", family, ppem, PenX, ppem + 12, Width, Height, false, false, quality: 4);
                Gdi.s_rawRgb = null;
                double greyInk = 0;
                for (int x = 0; x < Width; x++)
                    greyInk += (255 - greyRaw[(row * Width + x) * 4 + 1]) / 255f;
                double gdiInk = 0, ourInk = 0;
                for (int x = 0; x < Width; x++)
                    for (int lamp = 0; lamp < 3; lamp++)
                    {
                        int o = (row * Width + x) * 4 + lamp;
                        gdiInk += Lin((255 - raw[o]) / 255f, g);
                        ourInk += Lin((255 - ours[o]) / 255f, g);
                    }
                log.AppendLine($"  {ppem,2}ppem  gdi {gdiInk,6:0.00} lamps ({gdiInk / 3,5:0.000} px)"
                               + $"   ours {ourInk,6:0.00} ({ourInk / 3,5:0.000} px)"
                               + $"   gdi/ours {(ourInk > 0 ? gdiInk / ourInk : 0),5:0.000}"
                               + $"   gdiGREY {greyInk,5:0.00}px");

                // WHERE the lit lamps are, not just how many. A stem one half-lamp wider and a stem
                // shifted by half a lamp give the SAME total, and the total is all this probe used to
                // report -- so it could never say which of the two GDI was doing. Each group of three
                // is one pixel's red, green and blue lamp, in sixths, so a full lamp reads 6.
                for (int pass = 0; pass < 2; pass++)
                {
                    var line = new System.Text.StringBuilder(pass == 0 ? "          gdi " : "          our ");
                    for (int x = PenX - 2; x < PenX + 4; x++)
                    {
                        for (int lamp = 0; lamp < 3; lamp++)
                        {
                            // GDI's bitmap is a Windows DIB and so is BGRA, ours is RGBA: read
                            // GDI's lamps backwards or every triple comes out mirrored, which reads
                            // convincingly as "their subpixel order is reversed" and is not.
                            float c = pass == 0
                                ? Lin((255 - raw[(row * Width + x) * 4 + (2 - lamp)]) / 255f, g)
                                : Lin((255 - ours[(row * Width + x) * 4 + lamp]) / 255f, g);
                            line.Append($"{MathF.Round(c * 6),2:0}");
                        }
                        line.Append(' ');
                    }
                    log.AppendLine(line.ToString());
                }
            }
            throw new Xunit.Sdk.XunitException(log.ToString());
        }

        private static class Gdi
        {
            /// <summary>The user's ClearType contrast, 1000..2200, or 1200 if it cannot be read.
            /// It IS the gamma the ClearType blend uses -- measured, at three settings.</summary>
            internal static int SystemFontSmoothingContrast()
            {
                try
                {
                    uint value = 0;
                    return SystemParametersInfo(0x200C, 0, ref value, 0) && value >= 1000 && value <= 2200
                        ? (int) value : 1200;
                }
                catch (EntryPointNotFoundException) { return 1200; }
                catch (DllNotFoundException) { return 1200; }
            }

            [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
            private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

            // ClearType, because ClearType is what we draw. Asking GDI for grey and comparing it with
            // subpixel output measures the difference between two rendering modes, which is not a
            // fault anyone can fix.
            // WPF_GDI_LFQUALITY overrides it, and 6 -- CLEARTYPE_NATURAL_QUALITY -- is the reason
            // it exists. Natural widths mean GDI does NOT run the compatible-width phase
            // (itrp_IUP's phase block is gated on globals[0x16b] == 2), so quality 6 is the only
            // way to see GDI's ClearType outline BEFORE the phase. Every claim about which part of
            // the pipeline a difference belongs to has until now had to assume GDI's pre-phase
            // outline equals ours, because GGO answers bi-level and the solver only ever sees the
            // finished glyph. This is the missing half.
            private const int ClearTypeQuality = 5;

            private static readonly int LfQuality =
                int.TryParse(Environment.GetEnvironmentVariable("WPF_GDI_LFQUALITY"), out int q)
                    ? q : ClearTypeQuality;
            private const int TaBaseline = 24, TaLeft = 0;
            private const int Transparent = 1;
            private const int BiRgb = 0;
            private const int DibRgbColors = 0;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct LOGFONTW
            {
                public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
                public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet;
                public byte lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFOHEADER
            {
                public int biSize, biWidth, biHeight;
                public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter;
                public int biClrUsed, biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT { public int Left, Top, Right, Bottom; }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

            [StructLayout(LayoutKind.Sequential)]
            private struct SIZE { public int cx, cy; }

            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out SIZE size);

            /// <summary>The width GDI measures a string to be -- what a control's layout asks for,
            /// and what TextRenderer.MeasureText with NoPadding reports.</summary>
            public static int TextWidth(string text, string family, int ppem,
                                        bool bold = false, bool italic = false)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem,
                    lfWeight = bold ? 700 : 400,
                    lfItalic = (byte) (italic ? 1 : 0),
                    lfCharSet = 1,
                    lfQuality = (byte) LfQuality,
                    lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                IntPtr old = SelectObject(dc, font);
                GetTextExtentPoint32W(dc, text, text.Length, out SIZE size);
                SelectObject(dc, old);
                DeleteObject(font);
                DeleteDC(dc);
                return size.cx;
            }

            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
            [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateFontIndirectW(ref LOGFONTW lf);
            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, int usage,
                                                          out IntPtr bits, IntPtr section, int offset);
            [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
            [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
            [DllImport("gdi32.dll")] private static extern uint SetTextAlign(IntPtr hdc, uint align);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect,
                                                   string text, uint count, IntPtr dx);
            [DllImport("gdi32.dll")] private static extern bool GdiFlush();

            [StructLayout(LayoutKind.Sequential)]
            private struct FIXED { public short fract, value; }
            [StructLayout(LayoutKind.Sequential)]
            private struct MAT2 { public FIXED eM11, eM12, eM21, eM22; }
            [StructLayout(LayoutKind.Sequential)]
            private struct POINTFX { public FIXED x, y; }
            [StructLayout(LayoutKind.Sequential)]
            private struct GLYPHMETRICS
            {
                public uint gmBlackBoxX, gmBlackBoxY;
                public int gmptGlyphOriginX, gmptGlyphOriginY;
                public short gmCellIncX, gmCellIncY;
            }

            [DllImport("gdi32.dll")]
            private static extern uint GetGlyphOutlineW(IntPtr hdc, uint ch, uint format,
                                                        out GLYPHMETRICS gm, uint cbBuffer,
                                                        IntPtr buffer, ref MAT2 mat2);
            [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
            private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int c,
                                                        [Out] ushort[] indices, uint flags);

            private const uint GgoMetrics = 0, GgoGlyphIndex = 0x0080;

            /// <summary>The glyph's HINTED metrics, straight from GDI's own interpreter: the ink box
            /// in whole pixels and where that box sits relative to the pen. This is the only view of
            /// GDI's hinting that is not filtered through ClearType, so it separates "our outline is
            /// fitted differently" from "our filter spreads it differently".</summary>
            [DllImport("gdi32.dll")]
            private static extern bool GetCharWidthI(IntPtr hdc, uint first, uint count,
                                                     ushort[] gi, int[] widths);

            [DllImport("gdi32.dll")]
            private static extern uint GetFontData(IntPtr hdc, uint table, uint offset,
                                                   byte[]? buffer, uint length);

            /// <summary>'hmtx' and 'head' straight out of the font GDI SELECTED, not the file we
            /// guessed at. The two are not always the same file, and every metric comparison is
            /// meaningless when they differ.</summary>
            public static (int Upem, int Advance) SelectedFaceMetrics(
                int glyphId, string family, int ppem, bool bold = false, bool italic = false)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1,
                    lfQuality = (byte) LfQuality, lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                IntPtr oldFont = SelectObject(dc, font);

                static uint Tag(string t) => (uint)(t[3] << 24 | t[2] << 16 | t[1] << 8 | t[0]);
                static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];

                var head = new byte[54];
                GetFontData(dc, Tag("head"), 0, head, (uint)head.Length);
                var hhea = new byte[36];
                GetFontData(dc, Tag("hhea"), 0, hhea, (uint)hhea.Length);
                int numH = U16(hhea, 34);
                int index = Math.Min(glyphId, Math.Max(numH - 1, 0));
                var entry = new byte[4];
                GetFontData(dc, Tag("hmtx"), (uint)(index * 4), entry, 4);

                SelectObject(dc, oldFont);
                DeleteObject(font);
                DeleteDC(dc);
                return (U16(head, 18), U16(entry, 0));
            }

            /// <summary>A DC with a 32bpp surface selected, which is what a font has to be
            /// realized on for GDI to answer as it draws.
            /// <para>A bare CreateCompatibleDC is a ONE-BIT surface, and GDI realizes a ClearType
            /// font on it as bi-level: for Arial Regular at 7 and 8ppem GetCharWidthI then returns
            /// the hinted widths (195, 203 over the probe's alphabet) where the same call on the
            /// 32bpp DC that ExtTextOutW draws into returns the linear ones (205, 213). The face's
            /// program branches on GETINFO's ClearType answer, and the answer depends on the
            /// surface. Every other face, style and size measured agrees between the two -- which is
            /// how the wrong DC went unnoticed for as long as it did.</para></summary>
            private static IntPtr ColourDc(out IntPtr dib)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = 8, biHeight = -8, biPlanes = 1, biBitCount = 32, biCompression = BiRgb,
                };
                dib = CreateDIBSection(dc, ref header, DibRgbColors, out _, IntPtr.Zero, 0);
                Assert.True(dib != IntPtr.Zero, "GDI would not give us a bitmap");
                SelectObject(dc, dib);
                return dc;
            }

            /// <summary>The advance GDI lays this glyph out with under a ClearType DC -- the number
            /// that decides where the NEXT glyph starts. A different question from how wide the ink
            /// is, and the only glyph property whose error ACCUMULATES.</summary>
            public static int LayoutAdvance(char c, string family, int ppem,
                                            bool bold = false, bool italic = false)
            {
                IntPtr dc = ColourDc(out IntPtr dib);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1,
                    lfQuality = (byte) LfQuality, lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                IntPtr oldFont = SelectObject(dc, font);
                var idx = new ushort[1];
                GetGlyphIndicesW(dc, c.ToString(), 1, idx, 0);
                var widths = new int[1];
                GetCharWidthI(dc, 0, 1, idx, widths);
                SelectObject(dc, oldFont);
                DeleteObject(font);
                DeleteDC(dc);
                DeleteObject(dib);
                return widths[0];
            }

            public static (int Index, int OriginX, int BlackBoxX, int CellIncX) HintedMetrics(
                char c, string family, int ppem, bool bold = false, bool italic = false)
            {
                IntPtr dc = ColourDc(out IntPtr dib);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1,
                    lfQuality = (byte) LfQuality, lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                IntPtr oldFont = SelectObject(dc, font);

                var idx = new ushort[1];
                GetGlyphIndicesW(dc, c.ToString(), 1, idx, 0);
                var mat = new MAT2 { eM11 = new FIXED { value = 1 }, eM22 = new FIXED { value = 1 } };
                GetGlyphOutlineW(dc, idx[0], GgoMetrics | GgoGlyphIndex, out GLYPHMETRICS gm, 0,
                                 IntPtr.Zero, ref mat);

                SelectObject(dc, oldFont);
                DeleteObject(font);
                DeleteDC(dc);
                DeleteObject(dib);
                return (idx[0], gm.gmptGlyphOriginX, (int)gm.gmBlackBoxX, gm.gmCellIncX);
            }

            /// <summary>Coverage of the string, 0 where the paper shows through and 255 where the ink
            /// is solid -- the same thing our renderer's mask holds.</summary>
            /// <summary>Set to draw through DrawTextW instead of ExtTextOutW.
            /// <para>They are not the same API for a complex script: ExtTextOutW shapes but does
            /// NOT apply the bidirectional algorithm, while DrawTextW does. Stock WinForms draws
            /// its labels through DrawText, so which of the two we must agree with is a question
            /// about the product, not about this probe.</para></summary>
            internal static bool s_useDrawText;

            /// <summary>DT_RTLREADING, which is what WinForms adds for a RightToLeft control.
            /// </summary>
            internal static bool s_rtlReading;

            public static byte[] Draw(string text, string family, int ppem, int penX, int baseline,
                                      int w, int h, bool bold = false, bool italic = false,
                                      int quality = -1)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // top-down, so row 0 is the top
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BiRgb,
                };
                IntPtr dib = CreateDIBSection(dc, ref header, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
                Assert.True(dib != IntPtr.Zero && bits != IntPtr.Zero, "GDI would not give us a bitmap");

                // White paper. CreateDIBSection hands back zeroed memory, which is black, and text
                // drawn black on black is invisible -- and identical everywhere, which would pass.
                var paper = new byte[w * h * 4];
                for (int i = 0; i < paper.Length; i += 4)
                {
                    paper[i] = (byte)(Paper & 0xFF);            // B, as the DIB wants it
                    paper[i + 1] = (byte)((Paper >> 8) & 0xFF);
                    paper[i + 2] = (byte)((Paper >> 16) & 0xFF);
                    paper[i + 3] = 0xFF;
                }
                Marshal.Copy(paper, 0, bits, paper.Length);

                var lf = new LOGFONTW
                {
                    lfHeight = -ppem,       // negative: the EM size, not the cell height
                    // A weight and a slant, not a face name: this is how an application asks for the
                    // bold, and it is how GDI picks the family's OWN bold file rather than smearing
                    // the regular one. Our side has to be handed the same file for the comparison to
                    // mean anything -- FontFiles.Find(family, bold, italic) is the other half of it.
                    lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0),
                    lfCharSet = 1,          // DEFAULT_CHARSET
                    lfQuality = (byte) (quality < 0 ? LfQuality : quality),
                    lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                Assert.True(font != IntPtr.Zero, $"GDI would not make a {family} at {ppem}px");

                IntPtr oldBitmap = SelectObject(dc, dib);
                IntPtr oldFont = SelectObject(dc, font);
                SetTextColor(dc, (int)(((Ink & 0xFF) << 16) | (Ink & 0xFF00) | ((Ink >> 16) & 0xFF)));
                SetBkMode(dc, Transparent);
                SetTextAlign(dc, TaBaseline | TaLeft);
                bool drew;
                if (s_useDrawText)
                {
                    // TA_TOP, because DrawText requires it and the TA_BASELINE this DC is set up
                    // with put the whole line above the bitmap -- it drew ZERO ink and the probe
                    // read that as "the two APIs agree".
                    SetTextAlign(dc, TaLeft);
                    // DT_NOCLIP | DT_SINGLELINE | DT_NOPREFIX. DrawText lays a LINE out, so the
                    // baseline lands where the font's ascent puts it rather than where we asked;
                    // the comparison this serves is GDI against GDI, aligned on ink.
                    var rect = new RECT { Left = penX, Top = 4, Right = w, Bottom = h };
                    uint flags = 0x0100 | 0x0020 | 0x0800 | (s_rtlReading ? 0x00020000u : 0u);
                    drew = DrawTextW(dc, text, text.Length, ref rect, flags) != 0;
                }
                else
                {
                    drew = ExtTextOutW(dc, penX, baseline, 0, IntPtr.Zero, text, (uint)text.Length, IntPtr.Zero);
                }
                GdiFlush();

                var rgba = new byte[w * h * 4];
                Marshal.Copy(bits, rgba, 0, rgba.Length);

                SelectObject(dc, oldFont);
                SelectObject(dc, oldBitmap);
                DeleteObject(font);
                DeleteObject(dib);
                DeleteDC(dc);
                Assert.True(drew, "GDI would not draw the string");

                if (s_rawRgb != null) { Array.Copy(rgba, s_rawRgb, Math.Min(rgba.Length, s_rawRgb.Length)); }
                var grey = new byte[w * h];
                for (int i = 0; i < grey.Length; i++)
                    grey[i] = (byte)(255 - rgba[i * 4 + 1]);   // the GREEN lamp: the centre one, and the one the eye weighs most
                return grey;
            }

            /// <summary>Set to a buffer to receive the last Draw's raw BGRA. Summing one lamp is not
            /// energy-preserving under a subpixel filter -- ink that moves sideways out of the green
            /// lamp into a neighbour's red or blue simply vanishes from a green-only total -- so a
            /// weight comparison has to see all three.</summary>
            [ThreadStatic] public static byte[]? s_rawRgb;

        }
    }
}
