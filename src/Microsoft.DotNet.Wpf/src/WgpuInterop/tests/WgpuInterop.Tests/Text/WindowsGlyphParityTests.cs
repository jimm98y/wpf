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
        /// stopped.</para></summary>
        private static readonly Dictionary<string, int> Allowed = new()
        {
            ["!"] = 15,
            ["#"] = 4,
            ["$"] = 33,
            ["%"] = 44,
            ["&"] = 18,
            ["("] = 18,
            [")"] = 9,
            ["*"] = 2,
            ["+"] = 11,
            [","] = 6,
            ["."] = 4,
            ["/"] = 1,
            ["0"] = 8,
            ["0123456789@11"] = 21,
            ["0123456789@12"] = 85,
            ["0123456789@12b"] = 59,
            ["0123456789@12bi"] = 25,
            ["0123456789@12i"] = 21,
            ["0123456789@13"] = 20,
            ["0123456789@16"] = 83,
            ["0123456789@16b"] = 70,
            ["0123456789@16bi"] = 20,
            ["0123456789@16i"] = 24,
            ["0123456789@19"] = 89,
            ["1"] = 19,
            ["2"] = 9,
            ["3"] = 2,
            ["4"] = 15,
            ["5"] = 8,
            ["6"] = 10,
            ["7"] = 1,
            ["8"] = 7,
            ["9"] = 6,
            [":"] = 6,
            [";"] = 9,
            ["<"] = 17,
            ["="] = 6,
            [">"] = 17,
            ["?"] = 12,
            ["@"] = 46,
            ["A"] = 3,
            ["ABCDEFGHIJKLM@11"] = 177,
            ["ABCDEFGHIJKLM@12"] = 223,
            ["ABCDEFGHIJKLM@13"] = 262,
            ["ABCDEFGHIJKLM@16"] = 84,
            ["ABCDEFGHIJKLM@19"] = 134,
            ["B"] = 20,
            ["C"] = 10,
            ["Cancel Apply@12b"] = 143,
            ["Cancel Apply@12bi"] = 36,
            ["Cancel Apply@12i"] = 8,
            ["Cancel Apply@16b"] = 78,
            ["Cancel Apply@16bi"] = 23,
            ["Cancel Apply@16i"] = 13,
            ["D"] = 26,
            ["E"] = 9,
            ["F"] = 9,
            ["G"] = 23,
            ["H"] = 35,
            ["Handgloves@12b"] = 101,
            ["Handgloves@12bi"] = 28,
            ["Handgloves@12i"] = 11,
            ["Handgloves@16b"] = 76,
            ["Handgloves@16bi"] = 19,
            ["Handgloves@16i"] = 11,
            ["I"] = 18,
            ["Illinois still@11"] = 124,
            ["Illinois still@12"] = 118,
            ["Illinois still@13"] = 153,
            ["Illinois still@16"] = 73,
            ["Illinois still@19"] = 80,
            ["J"] = 7,
            ["K"] = 12,
            ["L"] = 10,
            ["M"] = 41,
            ["N"] = 20,
            ["NOPQRSTUVWXYZ@11"] = 178,
            ["NOPQRSTUVWXYZ@12"] = 161,
            ["NOPQRSTUVWXYZ@13"] = 209,
            ["NOPQRSTUVWXYZ@16"] = 59,
            ["NOPQRSTUVWXYZ@19"] = 105,
            ["O"] = 29,
            ["P"] = 20,
            ["Příliš@12b"] = 78,
            ["Příliš@12bi"] = 13,
            ["Příliš@12i"] = 20,
            ["Příliš@16b"] = 98,
            ["Příliš@16bi"] = 27,
            ["Příliš@16i"] = 28,
            ["Q"] = 7,
            ["R"] = 14,
            ["S"] = 9,
            ["Shapes 2026@11"] = 93,
            ["Shapes 2026@12"] = 98,
            ["Shapes 2026@13"] = 81,
            ["Shapes 2026@16"] = 111,
            ["Shapes 2026@19"] = 83,
            ["T"] = 1,
            ["U"] = 15,
            ["V"] = 6,
            ["W"] = 20,
            ["X"] = 5,
            ["Y"] = 5,
            ["Z"] = 10,
            ["["] = 13,
            ["]"] = 22,
            ["^"] = 1,
            ["`"] = 2,
            ["a"] = 11,
            ["abcdefghijklm@11"] = 215,
            ["abcdefghijklm@12"] = 192,
            ["abcdefghijklm@13"] = 240,
            ["abcdefghijklm@16"] = 130,
            ["abcdefghijklm@19"] = 123,
            ["b"] = 15,
            ["c"] = 14,
            ["d"] = 20,
            ["e"] = 13,
            ["f"] = 18,
            ["g"] = 20,
            ["h"] = 13,
            ["i"] = 8,
            ["j"] = 9,
            ["k"] = 11,
            ["l"] = 9,
            ["m"] = 31,
            ["n"] = 11,
            ["nopqrstuvwxyz@11"] = 167,
            ["nopqrstuvwxyz@12"] = 122,
            ["nopqrstuvwxyz@13"] = 152,
            ["nopqrstuvwxyz@16"] = 111,
            ["nopqrstuvwxyz@19"] = 146,
            ["o"] = 11,
            ["p"] = 15,
            ["q"] = 20,
            ["r"] = 6,
            ["repertoire@10"] = 653,
            ["repertoire@10b"] = 811,
            ["repertoire@10bi"] = 509,
            ["repertoire@10i"] = 334,
            ["repertoire@11"] = 1691,
            ["repertoire@11b"] = 891,
            ["repertoire@11bi"] = 443,
            ["repertoire@11i"] = 340,
            ["repertoire@12"] = 1817,
            ["repertoire@12b"] = 1294,
            ["repertoire@12bi"] = 587,
            ["repertoire@12i"] = 386,
            ["repertoire@13"] = 1989,
            ["repertoire@13b"] = 926,
            ["repertoire@13bi"] = 416,
            ["repertoire@13i"] = 372,
            ["repertoire@14"] = 1011,
            ["repertoire@14b"] = 1099,
            ["repertoire@14bi"] = 468,
            ["repertoire@14i"] = 1029,
            ["repertoire@15"] = 1150,
            ["repertoire@15b"] = 1108,
            ["repertoire@15bi"] = 444,
            ["repertoire@15i"] = 436,
            ["repertoire@16"] = 1125,
            ["repertoire@16b"] = 1176,
            ["repertoire@16bi"] = 494,
            ["repertoire@16i"] = 526,
            ["repertoire@17"] = 1102,
            ["repertoire@17b"] = 1303,
            ["repertoire@17bi"] = 565,
            ["repertoire@17i"] = 484,
            ["repertoire@18"] = 1214,
            ["repertoire@18b"] = 1194,
            ["repertoire@18bi"] = 585,
            ["repertoire@18i"] = 468,
            ["repertoire@19"] = 1519,
            ["repertoire@19b"] = 1700,
            ["repertoire@19bi"] = 703,
            ["repertoire@19i"] = 528,
            ["repertoire@20"] = 2948,
            ["repertoire@20b"] = 2762,
            ["repertoire@20bi"] = 2239,
            ["repertoire@20i"] = 2031,
            ["s"] = 1,
            ["t"] = 16,
            ["u"] = 23,
            ["v"] = 2,
            ["w"] = 2,
            ["x"] = 8,
            ["y"] = 3,
            ["z"] = 4,
            ["{"] = 24,
            ["|"] = 12,
            ["}"] = 26,
            ["~"] = 8,
            ["Á@11"] = 3,
            ["Á@12"] = 5,
            ["Á@13"] = 2,
            ["Á@16"] = 2,
            ["Á@19"] = 6,
            ["Ä@11"] = 5,
            ["Ä@12"] = 3,
            ["Ä@13"] = 1,
            ["Ä@16"] = 6,
            ["Ä@19"] = 5,
            ["É@11"] = 8,
            ["É@12"] = 11,
            ["É@13"] = 13,
            ["É@16"] = 4,
            ["É@19"] = 19,
            ["Ñ@11"] = 20,
            ["Ñ@12"] = 22,
            ["Ñ@13"] = 51,
            ["Ñ@16"] = 11,
            ["Ñ@19"] = 17,
            ["Ö@11"] = 30,
            ["Ö@12"] = 29,
            ["Ö@13"] = 29,
            ["Ö@16"] = 11,
            ["Ö@19"] = 11,
            ["Ü@11"] = 21,
            ["Ü@12"] = 16,
            ["Ü@13"] = 34,
            ["Ü@16"] = 6,
            ["Ü@19"] = 11,
            ["à@11"] = 16,
            ["à@12"] = 13,
            ["à@13"] = 14,
            ["à@16"] = 13,
            ["à@19"] = 12,
            ["á@11"] = 14,
            ["á@12"] = 15,
            ["á@13"] = 16,
            ["á@16"] = 12,
            ["á@19"] = 12,
            ["â@11"] = 18,
            ["â@12"] = 16,
            ["â@13"] = 18,
            ["â@16"] = 12,
            ["â@19"] = 17,
            ["ã@11"] = 14,
            ["ã@12"] = 13,
            ["ã@13"] = 18,
            ["ã@16"] = 14,
            ["ã@19"] = 16,
            ["ä@11"] = 14,
            ["ä@12"] = 12,
            ["ä@13"] = 15,
            ["ä@16"] = 15,
            ["ä@19"] = 12,
            ["å@11"] = 19,
            ["å@12"] = 17,
            ["å@13"] = 16,
            ["å@16"] = 10,
            ["å@19"] = 17,
            ["ç@11"] = 18,
            ["ç@12"] = 22,
            ["ç@13"] = 23,
            ["ç@16"] = 17,
            ["ç@19"] = 10,
            ["è@11"] = 20,
            ["è@12"] = 16,
            ["è@13"] = 24,
            ["è@16"] = 14,
            ["è@19"] = 5,
            ["é@11"] = 21,
            ["é@12"] = 18,
            ["é@13"] = 22,
            ["é@16"] = 10,
            ["é@19"] = 8,
            ["ê@11"] = 19,
            ["ê@12"] = 17,
            ["ê@13"] = 25,
            ["ê@16"] = 13,
            ["ê@19"] = 8,
            ["ë@11"] = 21,
            ["ë@12"] = 15,
            ["ë@13"] = 21,
            ["ë@16"] = 15,
            ["ë@19"] = 3,
            ["ì@11"] = 10,
            ["ì@12"] = 8,
            ["ì@13"] = 11,
            ["ì@16"] = 4,
            ["ì@19"] = 3,
            ["í@11"] = 9,
            ["í@12"] = 10,
            ["í@13"] = 10,
            ["í@16"] = 2,
            ["í@19"] = 2,
            ["î@11"] = 12,
            ["î@12"] = 12,
            ["î@13"] = 12,
            ["î@16"] = 3,
            ["î@19"] = 11,
            ["ï@11"] = 8,
            ["ï@12"] = 8,
            ["ï@13"] = 8,
            ["ï@19"] = 4,
            ["ñ@11"] = 18,
            ["ñ@12"] = 15,
            ["ñ@13"] = 17,
            ["ñ@16"] = 20,
            ["ñ@19"] = 41,
            ["ò@11"] = 29,
            ["ò@12"] = 13,
            ["ò@13"] = 27,
            ["ò@16"] = 13,
            ["ò@19"] = 10,
            ["ó@11"] = 27,
            ["ó@12"] = 13,
            ["ó@13"] = 24,
            ["ó@16"] = 14,
            ["ó@19"] = 10,
            ["ô@11"] = 31,
            ["ô@12"] = 16,
            ["ô@13"] = 28,
            ["ô@16"] = 13,
            ["ô@19"] = 10,
            ["õ@11"] = 26,
            ["õ@12"] = 14,
            ["õ@13"] = 28,
            ["õ@16"] = 11,
            ["õ@19"] = 15,
            ["ö@11"] = 25,
            ["ö@12"] = 12,
            ["ö@13"] = 25,
            ["ö@16"] = 13,
            ["ö@19"] = 11,
            ["ù@11"] = 5,
            ["ù@12"] = 26,
            ["ù@13"] = 15,
            ["ù@16"] = 3,
            ["ù@19"] = 36,
            ["ú@11"] = 6,
            ["ú@12"] = 28,
            ["ú@13"] = 18,
            ["ú@16"] = 3,
            ["ú@19"] = 39,
            ["û@11"] = 9,
            ["û@12"] = 28,
            ["û@13"] = 19,
            ["û@16"] = 5,
            ["û@19"] = 37,
            ["ü@11"] = 3,
            ["ü@12"] = 24,
            ["ü@13"] = 14,
            ["ü@16"] = 8,
            ["ü@19"] = 40,
            ["ý@11"] = 16,
            ["ý@12"] = 5,
            ["ý@13"] = 5,
            ["ý@16"] = 4,
            ["ý@19"] = 5,
            ["ÿ@11"] = 17,
            ["ÿ@12"] = 4,
            ["ÿ@13"] = 5,
            ["ÿ@16"] = 6,
            ["ÿ@19"] = 4,
            ["Č@11"] = 21,
            ["Č@12"] = 14,
            ["Č@13"] = 20,
            ["Č@16"] = 6,
            ["Č@19"] = 6,
            ["Ř@11"] = 17,
            ["Ř@12"] = 21,
            ["Ř@13"] = 30,
            ["Ř@16"] = 19,
            ["Ř@19"] = 20,
            ["Š@11"] = 17,
            ["Š@12"] = 12,
            ["Š@13"] = 12,
            ["Š@16"] = 11,
            ["Š@19"] = 4,
            ["š@11"] = 16,
            ["š@12"] = 5,
            ["š@13"] = 15,
            ["š@16"] = 8,
            ["š@19"] = 7,
            ["ů@11"] = 11,
            ["ů@12"] = 28,
            ["ů@13"] = 19,
            ["ů@16"] = 1,
            ["ů@19"] = 38,
            ["Ž@11"] = 9,
            ["Ž@12"] = 14,
            ["Ž@13"] = 8,
            ["Ž@16"] = 8,
            ["Ž@19"] = 12,
            ["ž@11"] = 7,
            ["ž@12"] = 8,
            ["ž@13"] = 5,
            ["ž@16"] = 3,
            ["ž@19"] = 6,
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

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
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
        private byte[] OursRgba(TrueTypeFont font, string text, int ppem, int baseline, bool correction,
                                float dx = 0f)
        {
            var root = new SceneVisual();
            root.Content.Add(new GlyphRunDraw(text, new Vector2(PenX + dx, baseline), ppem,
                                              RgbaColor.FromBytes(0, 0, 0, 255)));
            var renderer = NewRenderer(font);
            renderer.TextBlendCorrection = correction;
            return renderer.RenderToRgba(root, Width, Height, RgbaColor.FromBytes(255, 255, 255, 255));
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
                                          (byte)(Ink & 0xFF), 255)));
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
        private static double GlyphLampError(List<PathFigure> figures, float x, float baseline, byte[] raw)
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
            for (int y = 0; y < Height; y++)
                for (int px = 0; px < Width; px++)
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
        /// <para>WPF_SOLVEGLYPH=char@ppem, e.g. "H@12".</para></summary>
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
                    for (int k = -24; k <= 24; k++)
                    {
                        move[x] = keep + k / 64f;
                        double e = Err();
                        if (e < best - 1e-9) { best = e; bestAt = move[x]; moved = true; }
                    }
                    move[x] = bestAt;
                }
                if (!moved) break;
            }
            Console.Error.WriteLine($"    solved  {best:0}");
            var sb = new System.Text.StringBuilder("    ours -> gdi : ");
            foreach (float x in order) sb.Append($"{x:0.000}->{move[x]:0.000}  ");
            Console.Error.WriteLine(sb.ToString());
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
            string[] parts = spec!.Split('/');
            string? file = FontFiles.Find(parts[0], bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            int gid = font.GlyphIndex(parts[1][0]);
            float ppem = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"=== {parts[0]} '{parts[1]}' gid={gid} at {ppem}ppem ===");
            TrueTypeInterpreter.s_dumpGlyph = true;
            try { ((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out _); }
            finally { TrueTypeInterpreter.s_dumpGlyph = false; }
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

            foreach (int ppem in new[] { 11, 12, 16, 19 })
                foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
                    ((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem, out _);

            int rejected = TrueTypeFont.ImplausibleFits - before;
            Assert.True(rejected <= allowed,
                $"{family}: {rejected} glyphs fitted implausibly, more than the {allowed} written down "
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

        /// <summary>STAGE C of the pipeline decomposition: the finished ClearType pixels, per face.
        /// <para>Stages A and B live in GdiStageTests and compare GEOMETRY -- the unhinted outline,
        /// then the fitted one -- through one rasterizer, so they say nothing about the three lamps,
        /// the filter or the contrast curve. This is the rest of it: GDI's ClearType against our
        /// whole pipeline, over the same repertoire and the same sizes, so the three numbers can be
        /// read side by side and the stage that carries the difference is the one to work on.</para>
        /// <para>Written into the same file (WPF_STAGE_REPORT) as A and B.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        [InlineData("Times New Roman")]
        [InlineData("Consolas")]
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
            foreach (int ppem in new[] { 7, 8, 11, 12, 13, 16, 19 })
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
                            int a = 255 - mine[i + c], b = 255 - raw[i + c];
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
        private static readonly Dictionary<string, int> InkAllowed = new()
        {
            ["b@10"] = 45,
            ["b@11"] = 78,
            ["b@12"] = 127,
            ["b@13"] = 149,
            ["b@14"] = 193,
            ["b@15"] = 41,
            ["b@16"] = 50,
            ["b@17"] = 16,
            ["b@18"] = 10,
            ["b@19"] = 86,
            ["b@20"] = 626,
            ["bi@10"] = 120,
            ["bi@11"] = 83,
            ["bi@12"] = 183,
            ["bi@13"] = 116,
            ["bi@14"] = 121,
            ["bi@15"] = 49,
            ["bi@16"] = 17,
            ["bi@17"] = 25,
            ["bi@18"] = 16,
            ["bi@19"] = 67,
            ["bi@20"] = 636,
            ["i@10"] = 214,
            ["i@11"] = 195,
            ["i@12"] = 174,
            ["i@13"] = 144,
            ["i@14"] = 178,
            ["i@15"] = 104,
            ["i@16"] = 82,
            ["i@17"] = 68,
            ["i@18"] = 49,
            ["i@19"] = 73,
            ["i@20"] = 846,
            ["regular@10"] = 183,
            ["regular@11"] = 220,
            ["regular@12"] = 504,
            ["regular@13"] = 468,
            ["regular@14"] = 8,
            ["regular@15"] = 112,
            ["regular@16"] = 103,
            ["regular@17"] = 44,
            ["regular@18"] = 50,
            ["regular@19"] = 19,
            ["regular@20"] = 101,
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
                        int gdi = 255 - raw[i + c];
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

            const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789AKNRWXYZkvwxyz";
            int ourTotal = 0, gdiTotal = 0;
            var log = new System.Text.StringBuilder();
            log.AppendLine($"== {family}{(bold ? " Bold" : italic ? " Italic" : "")} @ {ppem}ppem");
            log.AppendLine($"   our file: {file}");
            foreach (char c in Letters)
            {
                int gid = font.GlyphIndex(c);
                int gdi = Gdi.LayoutAdvance(c, family, ppem, bold, italic);
                bool device = font.TryGetDeviceAdvance(gid, ppem, out float ours);
                int oursI = (int) MathF.Round(ours);
                ourTotal += oursI; gdiTotal += gdi;
                bool hinted = font.FaceHintsGlyph(gid, ppem);
                float linear = font.LinearAdvanceForTest(gid, ppem);
                if (true)
                    log.AppendLine($"  '{c}' gid {gid,4}  gdi {gdi,3}  ours {oursI,3}"
                                   + $"  linear {linear,6:0.000}  faceHints={hinted}"
                                   + $"  pp {font.HintedPhantomsForTest(gid, ppem).Pp1,6:0.000}"
                                   + $" .. {font.HintedPhantomsForTest(gid, ppem).Pp2,6:0.000}"
                                   + $"  ggo {Gdi.HintedMetrics(c, family, ppem, bold, italic)}"
                                   + (device ? "" : "   NO device advance"));
            }
            log.AppendLine($"  TOTAL gdi {gdiTotal}  ours {ourTotal}  drift {ourTotal - gdiTotal}");
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
            private const int ClearTypeQuality = 5;
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
                    lfQuality = ClearTypeQuality, lfFaceName = family,
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

            /// <summary>The advance GDI lays this glyph out with under a ClearType DC -- the number
            /// that decides where the NEXT glyph starts. A different question from how wide the ink
            /// is, and the only glyph property whose error ACCUMULATES.</summary>
            public static int LayoutAdvance(char c, string family, int ppem,
                                            bool bold = false, bool italic = false)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1,
                    lfQuality = ClearTypeQuality, lfFaceName = family,
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
                return widths[0];
            }

            public static (int Index, int OriginX, int BlackBoxX, int CellIncX) HintedMetrics(
                char c, string family, int ppem, bool bold = false, bool italic = false)
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400,
                    lfItalic = (byte)(italic ? 1 : 0), lfCharSet = 1,
                    lfQuality = ClearTypeQuality, lfFaceName = family,
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
                return (idx[0], gm.gmptGlyphOriginX, (int)gm.gmBlackBoxX, gm.gmCellIncX);
            }

            /// <summary>Coverage of the string, 0 where the paper shows through and 255 where the ink
            /// is solid -- the same thing our renderer's mask holds.</summary>
            public static byte[] Draw(string text, string family, int ppem, int penX, int baseline,
                                      int w, int h, bool bold = false, bool italic = false)
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
                    lfQuality = ClearTypeQuality,
                    lfFaceName = family,
                };
                IntPtr font = CreateFontIndirectW(ref lf);
                Assert.True(font != IntPtr.Zero, $"GDI would not make a {family} at {ppem}px");

                IntPtr oldBitmap = SelectObject(dc, dib);
                IntPtr oldFont = SelectObject(dc, font);
                SetTextColor(dc, (int)(((Ink & 0xFF) << 16) | (Ink & 0xFF00) | ((Ink >> 16) & 0xFF)));
                SetBkMode(dc, Transparent);
                SetTextAlign(dc, TaBaseline | TaLeft);
                bool drew = ExtTextOutW(dc, penX, baseline, 0, IntPtr.Zero, text, (uint)text.Length, IntPtr.Zero);
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
