// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A glyph's outline is built once per font, not once per request.
//
// MilcoreEngine builds the geometry of EVERY glyph of every text run while PARSING render data, so a
// visual holding a paragraph asks for hundreds of outlines each time its render data changes -- and
// each of those used to re-read the glyph out of the font tables: contours, composite components and
// variation deltas, into a freshly allocated list of figures.
//
// Nothing about that is visible while an application sits still, because unchanged visuals are not
// re-parsed at all. It shows the moment layout moves. Profiling a splitter drag in a real WPF IDE on
// macOS put 70-94 ms per frame inside ParseRenderData, on frames parsing as few as 121 visuals --
// half a millisecond each, none of it decoding.
//
// The outline in font units is identical every time it is asked for, so it is kept. Handing back the
// same list is only safe because no consumer mutates it: GlyphRunPainter.ScaleFigures and
// WgpuSceneRenderer.TransformGeometry both build new figures rather than moving these. That is what
// the second test pins -- if either ever starts transforming in place, a cached outline would be
// corrupted for every later use and the damage would be silent.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class GlyphOutlineCacheTests
    {
        private static int FirstOutlineGlyph(TrueTypeFont font)
        {
            for (char c = 'A'; c <= 'Z'; c++)
            {
                int gid = font.GlyphIndex(c);
                if (gid != 0 && font.TryGetGlyphOutline(gid, out List<PathFigure> f) && f.Count > 0) return gid;
            }
            return -1;
        }

        [Fact]
        public void AskingTwiceReturnsTheSameOutline()
        {
            var font = new TrueTypeFont(System.IO.File.ReadAllBytes(TestFonts.Require()));
            int gid = FirstOutlineGlyph(font);
            Assert.True(gid > 0, "the test font mapped no letters with outlines");

            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> first));
            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> second));

            Assert.Same(first, second);
        }

        /// <summary>
        /// The safety condition for handing out the cached list: painting a glyph must not disturb it.
        /// Paint it at two different positions and scales, then check the source is where it started.
        /// </summary>
        [Fact]
        public void PaintingAGlyphDoesNotMoveTheCachedOutline()
        {
            var font = new TrueTypeFont(System.IO.File.ReadAllBytes(TestFonts.Require()));
            int gid = FirstOutlineGlyph(font);
            Assert.True(gid > 0);

            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> cached));
            Vector2 startBefore = cached[0].Start;
            int figuresBefore = cached.Count;
            int segmentsBefore = cached[0].Segments.Count;

            var fills = new List<GlyphFill>();
            GlyphRunPainter.Paint(font, null, gid, 1.0f, 100f, 200f, fills);
            GlyphRunPainter.Paint(font, null, gid, 0.25f, -50f, 7f, fills);

            Assert.NotEmpty(fills);
            Assert.Equal(figuresBefore, cached.Count);
            Assert.Equal(segmentsBefore, cached[0].Segments.Count);
            Assert.Equal(startBefore, cached[0].Start);
        }
    }
}
