// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What grid fitting is for, asserted on the thing it was measured on: at a text size an unfitted
// outline puts the bar of an 'E' across two rows and neither of them ends up solid, so the glyph is
// grey where Windows draws it black. These check that the fitted outline lands on the grid and that
// the ink follows -- structurally, so they hold for whatever face the machine has rather than for
// one file.
//
// These cover the ANALYSIS -- GlyphHinter -- which is what a face carrying no hints of its own gets.
// A face that does carry them is run through TrueTypeInterpreter instead and is checked against GDI
// directly, in WindowsGlyphParityTests. So these call TryGetFittedOutline, not TryGetHintedOutline:
// going through the latter on a machine with Segoe UI would quietly test the interpreter and leave
// the analysis with no cover at all.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class GlyphHintingTests : RendererTestBase
    {
        public GlyphHintingTests(GpuFixture gpu) : base(gpu) { }

        // Nine point on a 96-DPI screen, the size the shell draws its own text at and the size every
        // measurement against Windows in this area was taken at.
        private const float TextPpem = 12f;

        private static CoverageMask Rasterize(List<PathFigure> figures)
            => PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero, figures));

        [Fact]
        public void FittedGlyph_HasASolidBar_WhereTheUnfittedOneIsAllGrey()
        {
            TrueTypeFont font = TestFonts.Load();
            int gid = font.GlyphIndex('E') is int e and > 0 ? e : font.GlyphIndex('x');
            Assert.SkipWhen(gid <= 0, "face has neither 'E' nor 'x'");

            Assert.True(font.TryGetFittedOutline(gid, TextPpem, out List<PathFigure> fitted) && fitted.Count > 0,
                "no fitted outline came back");
            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> plain) && plain.Count > 0);

            CoverageMask fittedMask = Rasterize(fitted);
            CoverageMask plainMask = Rasterize(ScaleFigures(plain, TextPpem / font.PixelsPerEm));
            Assert.False(fittedMask.IsEmpty, "the fitted outline rasterized to nothing");

            // An 'E's bars are about four fifths of a pixel thick at this size. Unfitted they lie
            // across two rows and cover neither, so nothing in the glyph is solid; fitted, each bar
            // occupies one row and the middle of it is black.
            Assert.True(Solid(fittedMask) > 0,
                "a fitted glyph should have solid pixels where its horizontal bars are");
            Assert.True(Solid(fittedMask) > Solid(plainMask),
                $"fitting put down {Solid(fittedMask)} solid pixels against the unfitted outline's "
                + $"{Solid(plainMask)} -- the vertical grid is not being applied");
        }

        [Fact]
        public void FittedGlyph_KeepsItsShape()
        {
            TrueTypeFont font = TestFonts.Load();
            int gid = font.GlyphIndex('o');
            Assert.SkipWhen(gid <= 0, "face has no 'o'");

            Assert.True(font.TryGetFittedOutline(gid, 24f, out List<PathFigure> fitted) && fitted.Count > 0);
            CoverageMask mask = Rasterize(fitted);
            Assert.False(mask.IsEmpty);

            // Still a ring with a hole: fitting moves edges by a fraction of a pixel, and a glyph that
            // came back filled in or inside out would say the interpolation had lost the contours.
            int middle = mask.Height / 2;
            byte centre = mask.Coverage[middle * mask.Width + (mask.Width / 2)];
            // The heaviest pixel in the left third of that row. Not one fixed column: the horizontal
            // is no longer snapped, so which column the side falls in depends on the face.
            byte ring = 0;
            for (int x = 0; x < Math.Max(2, mask.Width / 3); x++)
                if (mask.Coverage[middle * mask.Width + x] > ring)
                    ring = mask.Coverage[middle * mask.Width + x];
            Assert.True(centre < 60, $"'o' should still be hollow after fitting, centre was {centre}");
            Assert.True(ring > 180, $"'o' should still have an inked ring, it was {ring}");

            // And it is still about as big as it was: within a pixel of the unfitted outline's size,
            // which is all fitting is allowed to move it.
            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> plain) && plain.Count > 0);
            CoverageMask reference = Rasterize(ScaleFigures(plain, 24f / font.PixelsPerEm));
            Assert.InRange(mask.Width, reference.Width - 2, reference.Width + 2);
            Assert.InRange(mask.Height, reference.Height - 2, reference.Height + 2);
        }

        [Fact]
        public void TheBaselineAndTheXHeight_LandOnWholePixels()
        {
            TrueTypeFont font = TestFonts.Load();
            int gid = font.GlyphIndex('x');
            Assert.SkipWhen(gid <= 0, "face has no 'x'");

            Assert.True(font.TryGetFittedOutline(gid, TextPpem, out List<PathFigure> fitted) && fitted.Count > 0);

            float top = float.MaxValue, bottom = float.MinValue;
            foreach (PathFigure f in fitted)
            {
                Track(f.Start, ref top, ref bottom);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment line: Track(line.Point, ref top, ref bottom); break;
                        case QuadraticBezierSegment q: Track(q.Point, ref top, ref bottom); break;
                        case CubicBezierSegment c: Track(c.Point, ref top, ref bottom); break;
                    }
            }

            // 'x' is flat top and bottom, so both of its edges are blue-zone edges and both must have
            // been put on a whole pixel. (y is DOWN here, which is how the outline leaves the face.)
            Assert.True(MathF.Abs(top - MathF.Round(top)) < 0.02f,
                $"the x-height edge should be on a whole pixel, it was at {top}");
            Assert.True(MathF.Abs(bottom - MathF.Round(bottom)) < 0.02f,
                $"the baseline edge should be on a whole pixel, it was at {bottom}");
        }

        /// <summary>Every upright in a face comes out the same width as every other, and that width
        /// is ONE pixel. A regular face at a text size draws a stem a little over a pixel wide and
        /// fitting rounds it to one -- GDI puts Segoe UI's 'm' stems on single columns, fully covered.
        /// Two means the edges were found outside the stem, usually because the run that found them
        /// ran on into the curve the stem turns into, and the whole page comes out bold.</summary>
        [Fact]
        public void EveryStemInAFace_ComesOutTheSameWidth()
        {
            TrueTypeFont font = TestFonts.Load();
            int n = font.GlyphIndex('n'), m = font.GlyphIndex('m');
            Assert.SkipWhen(n <= 0 || m <= 0, "face has no 'n'/'m'");

            Assert.True(font.TryGetFittedOutline(n, TextPpem, out List<PathFigure> fn) && fn.Count > 0);
            Assert.True(font.TryGetFittedOutline(m, TextPpem, out List<PathFigure> fm) && fm.Count > 0);

            CoverageMask maskN = Rasterize(fn), maskM = Rasterize(fm);
            Assert.Equal(StemWidth(maskN), StemWidth(maskM));
            Assert.Equal(1, StemWidth(maskN));
        }

        /// <summary>A letter with a hole in it still has one. An 'A' is the test because its sides
        /// are DIAGONAL: a rule that decides what counts as an upright edge too generously takes the
        /// whole diagonal for one, moves every point on it onto that edge's pixel, and the letter
        /// comes out as a solid block. That is what a too-forgiving rule looks like on the page, and
        /// it is not visible in any measurement of stems or blue zones.</summary>
        [Fact]
        public void ALetterWithACounter_StillHasIt()
        {
            TrueTypeFont font = TestFonts.Load();
            int gid = font.GlyphIndex('A');
            Assert.SkipWhen(gid <= 0, "face has no 'A'");

            Assert.True(font.TryGetFittedOutline(gid, TextPpem, out List<PathFigure> fitted) && fitted.Count > 0);
            CoverageMask mask = Rasterize(fitted);
            Assert.False(mask.IsEmpty);

            float ink = Ink(mask) / (mask.Width * mask.Height);
            Assert.True(ink < 0.72f,
                $"'A' covers {ink:P0} of its own box -- it has been filled in. Its counter and the "
                + "white either side of its apex should leave a good deal of the box empty.");
        }

        /// <summary>Fitting may not cost the page its colour. A stem narrowed to a whole pixel puts
        /// down a little less ink than the outline drew it with, and that is the trade -- but only a
        /// little: a run that comes out much lighter than the unfitted one is not sharper, it is
        /// thinner, and that is what "the text is not legible" looks like as a number.</summary>
        [Fact]
        public void Fitting_KeepsTheWeightOfTheText()
        {
            TrueTypeFont font = TestFonts.Load();
            float unfittedInk = 0f, fittedInk = 0f;
            int darkestFitted = 0;

            foreach (char c in "abcdefghijklmnopqrstuvwxyz")
            {
                int gid = font.GlyphIndex(c);
                if (gid <= 0) continue;

                if (font.TryGetGlyphOutline(gid, out List<PathFigure> plain) && plain.Count > 0)
                    unfittedInk += Ink(Rasterize(ScaleFigures(plain, TextPpem / font.PixelsPerEm)));

                if (font.TryGetFittedOutline(gid, TextPpem, out List<PathFigure> fitted) && fitted.Count > 0)
                {
                    CoverageMask mask = Rasterize(fitted);
                    fittedInk += Ink(mask);
                    foreach (byte b in mask.Coverage) if (b > darkestFitted) darkestFitted = b;
                }
            }

            Assert.True(unfittedInk > 0f, "nothing rasterized");
            Assert.True(darkestFitted >= 250, $"fitting should give solid pixels; darkest was {darkestFitted}");
            float ratio = fittedInk / unfittedInk;
            Assert.True(ratio > 0.85f,
                $"fitted text has {ratio:P0} of the ink the unfitted outlines put down -- it has been thinned, not sharpened");
        }

        /// <summary>The whole chain, not just the fitting: a run drawn through the renderer at a
        /// text size must come out on the grid it was fitted to. Fitting a glyph and then seating its
        /// mask on a fraction of a pixel undoes the fitting, and the way that shows is the BASELINE --
        /// fitted, every letter in the line stops on the same row and the row under it is empty;
        /// re-seated, the whole line bleeds into the next row and goes grey.</summary>
        [Fact]
        public void ARunDrawnThroughTheRenderer_LandsOnTheGrid()
        {
            TrueTypeFont font = TestFonts.Load();
            const int W = 200, H = 40;

            var root = new SceneVisual();
            root.Content.Add(new GlyphRunDraw("Illinois still", new Vector2(6f, 26f), 12f,
                                              RgbaColor.FromBytes(0, 0, 0, 255)));

            byte[] px = NewRenderer(font).RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

            var perRow = new int[H];
            int inked = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (px[(y * W + x) * 4] < 250) { perRow[y]++; inked++; }

            Assert.True(inked > 100, $"the run did not draw (only {inked} inked pixels)");

            // The last row the letters really occupy, and the row under it. "Illinois still" sits on
            // its baseline with nothing below -- no descenders -- so that row should be all but empty.
            int last = -1;
            for (int y = 0; y < H; y++)
                if (perRow[y] >= 6) last = y;
            Assert.True(last > 0 && last < H - 1, "could not find the run's baseline");
            Assert.True(perRow[last + 1] <= perRow[last] / 8,
                $"the row under the baseline has {perRow[last + 1]} inked pixels against the "
                + $"baseline row's {perRow[last]}. The run is bleeding across the grid it was fitted "
                + "to -- its mask was re-seated on a fraction of a pixel, see the pixelAligned path "
                + "in EmitCoverageMask.");
        }

        /// <summary>Bold capitals stand as tall as regular ones. The two faces draw their cap height
        /// within a few units of each other, so they must fit to the same number of pixels -- a bold
        /// word a pixel shorter than the text around it is plainly wrong on the page.
        /// <para>They did differ, and only because of where the rounding fell: the regular face's cap
        /// height comes to 8.59 pixels at nine point and the bold face's to 8.40. Rounded to nearest
        /// that is 9 and 8. A blue zone is biased upwards instead, which is what Windows does.</para>
        /// </summary>
        [Fact]
        public void BoldCapitals_StandAsTallAsRegularOnes()
        {
            TrueTypeFont regular = TestFonts.Load();
            string? boldFile = FontFiles.Find("Segoe UI", bold: true, italic: false);
            Assert.SkipWhen(boldFile is null, "this machine has no bold face to compare");
            var bold = new TrueTypeFont(System.IO.File.ReadAllBytes(boldFile!));

            Assert.Equal(CapRows(regular), CapRows(bold));
        }

        /// <summary>How many pixel rows an 'H' occupies once fitted.</summary>
        private static int CapRows(TrueTypeFont font)
        {
            int gid = font.GlyphIndex('H');
            Assert.True(gid > 0, "face has no 'H'");
            Assert.True(font.TryGetFittedOutline(gid, TextPpem, out List<PathFigure> fitted) && fitted.Count > 0);
            return Rasterize(fitted).Height;
        }

        /// <summary>How many pixels are covered outright.</summary>
        private static int Solid(CoverageMask mask)
        {
            int n = 0;
            foreach (byte b in mask.Coverage) if (b >= 250) n++;
            return n;
        }

        /// <summary>Total coverage: every pixel's share of a whole one, added up.</summary>
        private static float Ink(CoverageMask mask)
        {
            float total = 0f;
            foreach (byte b in mask.Coverage) total += b / 255f;
            return total;
        }

        /// <summary>How wide this face's upright is, in whole covered pixels: the NARROWEST run of
        /// solid pixels found low in the glyph, where every upright is on its own. Taking the widest
        /// instead measures an 'm' at the point its arch joins its stem, which is two stems' worth of
        /// ink and nothing to do with how wide either of them is.</summary>
        private static int StemWidth(CoverageMask mask)
        {
            int best = int.MaxValue;
            for (int row = mask.Height * 3 / 4; row < mask.Height - 1; row++)
            {
                int run = 0;
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.Coverage[row * mask.Width + x] >= 250) { run++; continue; }
                    if (run > 0) best = Math.Min(best, run);
                    run = 0;
                }
                if (run > 0) best = Math.Min(best, run);
            }
            return best == int.MaxValue ? 0 : best;
        }

        private static void Track(System.Numerics.Vector2 p, ref float top, ref float bottom)
        {
            top = MathF.Min(top, p.Y);
            bottom = MathF.Max(bottom, p.Y);
        }

        private static List<PathFigure> ScaleFigures(List<PathFigure> figures, float scale)
        {
            var scaled = new List<PathFigure>(figures.Count);
            foreach (PathFigure f in figures)
            {
                var copy = new PathFigure(f.Start * scale) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                    copy.Segments.Add(seg switch
                    {
                        LineSegment line => new LineSegment(line.Point * scale),
                        QuadraticBezierSegment q => new QuadraticBezierSegment(q.Control * scale, q.Point * scale),
                        CubicBezierSegment c => new CubicBezierSegment(c.Control1 * scale, c.Control2 * scale, c.Point * scale),
                        _ => seg,
                    });
                scaled.Add(copy);
            }
            return scaled;
        }
    }
}
