// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A frame that draws the same thing as the last one allocates almost nothing.
//
// The collect phase walks the scene and turns it into draw items, and it used to REDERIVE, every
// frame, several things that depend only on inputs that had not changed:
//
//   * the device-space guidelines of every visual carrying a GuidelineSet (four float[] each),
//   * the guideline-snapped rebuild of every primitive those guidelines apply to,
//   * the GeometryFill and GeometryStroke wrappers that split a GeometryDrawing into its two draws,
//   * and, dominating all of them, the CPU stroke-to-outline of every stroke the analytic SDF path
//     cannot express -- which is most of them, because WPF's default Pen joins are MITER and the
//     SDF path handles round.
//
// Measured on a full-screen IDE that was doing nothing at all, that came to 1.9MB per frame and a
// gen0 collection every four frames. None of it was visible in a rendered image, which is why it
// needs an allocation counter to guard rather than a pixel comparison.
//
// The scene below is built to hit every one of those paths at once: bordered, guideline-snapped,
// miter-joined, rounded-cornered controls, which is what a themed application is made of.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class CollectAllocationTests : RendererTestBase
    {
        public CollectAllocationTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 320, H = 320;
        private const int Rows = 6, Columns = 6;
        private const int Frames = 20;

        /// <summary>
        /// A grid of "controls": each its own visual with a GuidelineSet, drawing a rounded rectangle
        /// filled and outlined by a 1px miter-joined pen -- a Border, in other words.
        /// </summary>
        private static SceneVisual Chrome(float shift = 0f)
        {
            var root = new SceneVisual();
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    float x = 6 + c * 51 + shift, y = 6 + r * 51;
                    var cell = new SceneVisual
                    {
                        // Guidelines are what put a 1px border ON a pixel boundary, and carrying them
                        // is what sent this visual down the resolve-and-snap path every frame.
                        GuidelinesX = new[] { x, x + 44 },
                        GuidelinesY = new[] { y, y + 44 },
                    };
                    cell.Content.Add(new GeometryDrawing(
                        new RoundedRectangleGeometry(new Rect(x, y, 44, 44), 4, 4),
                        new SolidColorBrush(RgbaColor.FromBytes(60, 70, 90, 255)),
                        new SolidColorBrush(RgbaColor.FromBytes(200, 205, 215, 255)),
                        new StrokeStyle(1.0, LineCap.Butt, LineJoin.Miter)));

                    // An open polyline stroked with the same pen -- a focus underline, a chevron, a
                    // separator. This one MATTERS: a closed-form shape with a solid pen is drawn as
                    // an analytic ring and never touches the CPU stroker, so a scene of nothing but
                    // bordered rectangles would not exercise the outline memo at all.
                    var chevron = new PathFigure(new Vector2(x + 8, y + 30)) { Closed = false };
                    chevron.Segments.Add(new LineSegment(new Vector2(x + 22, y + 38)));
                    chevron.Segments.Add(new LineSegment(new Vector2(x + 36, y + 30)));
                    cell.Content.Add(new GeometryDrawing(
                        new PathGeometry(FillRule.NonZero, new List<PathFigure> { chevron }),
                        null,
                        new SolidColorBrush(RgbaColor.FromBytes(240, 240, 240, 255)),
                        new StrokeStyle(1.5, LineCap.Butt, LineJoin.Miter)));
                    root.Children.Add(cell);
                }
            }
            return root;
        }

        /// <summary>Bytes allocated by the collect phase of one render of <paramref name="scene"/>.</summary>
        private long CollectAllocOf(WgpuSceneRenderer renderer, SceneVisual scene)
        {
            long before = WgpuSceneRenderer.PerfCollectAlloc;
            renderer.RenderToRgba(scene, W, H, White);
            return WgpuSceneRenderer.PerfCollectAlloc - before;
        }

        [Fact]
        public void RepeatFramesOfAnUnchangedSceneAllocateAlmostNothing()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            SceneVisual scene = Chrome();

            // The first frames are where the guidelines, the snapped primitives and the stroke
            // outlines are legitimately built. Three, because a scene takes a frame or two to settle.
            long first = CollectAllocOf(renderer, scene);
            CollectAllocOf(renderer, scene);
            CollectAllocOf(renderer, scene);

            long steady = 0;
            for (int f = 0; f < Frames; f++) steady += CollectAllocOf(renderer, scene);
            steady /= Frames;

            // Guard against a vacuous pass: if the scene stopped producing draws there would be
            // nothing to allocate FOR, and the assertion below would hold for the wrong reason.
            Assert.True(WgpuSceneRenderer.PerfDrawItems > 0,
                "the scene produced no draw items, so it is no longer exercising the collect phase");

            // The first frame is the honest cost of deriving all of it once. A settled frame derives
            // none of it again, so it must come in FAR under that -- the ratio is what this is really
            // asserting, and it is deliberately loose (an eighth) because the absolute numbers depend
            // on the GPU backend and on which fills the coverage cache has already absorbed.
            Assert.True(steady * 8 < first,
                $"a settled frame allocated {steady} bytes in collect against {first} for the first " +
                "frame; the per-frame derivations in the collect phase are no longer being reused");

            // And an absolute ceiling, because a ratio alone would be satisfied by a first frame that
            // had merely become enormous. 64KB is roughly an eightieth of what this cost before.
            Assert.True(steady < 64 * 1024,
                $"a settled frame allocated {steady} bytes in the collect phase; an unchanged scene " +
                "should re-derive nothing and allocate near zero");
        }

        /// <summary>
        /// Guidelines specifically, in a scene where nothing else can dominate.
        ///
        /// Resolving them allocates four float[] per visual and nothing else about the visual has to
        /// change for it to happen again next frame, so the cost scales with the SIZE of the tree --
        /// which is why it needs its own scene rather than riding on the one above, where a few dozen
        /// controls hide it under the stroke outlines.
        /// </summary>
        [Fact]
        public void AGuidelineHeavySceneResolvesThemOnce()
        {
            static SceneVisual Panel()
            {
                var root = new SceneVisual();
                for (int i = 0; i < 400; i++)
                {
                    float x = 4 + (i % 20) * 15, y = 4 + (i / 20) * 15;
                    var cell = new SceneVisual
                    {
                        GuidelinesX = new[] { x, x + 12 },
                        GuidelinesY = new[] { y, y + 12 },
                    };
                    cell.Content.Add(new GeometryFill(
                        new RectangleGeometry(new Rect(x, y, 12, 12)),
                        new SolidColorBrush(RgbaColor.FromBytes(90, 110, 140, 255))));
                    root.Children.Add(cell);
                }
                return root;
            }

            WgpuSceneRenderer renderer = NewRenderer();
            SceneVisual scene = Panel();
            for (int f = 0; f < 3; f++) CollectAllocOf(renderer, scene);

            long steady = 0;
            for (int f = 0; f < Frames; f++) steady += CollectAllocOf(renderer, scene);
            steady /= Frames;

            Assert.True(WgpuSceneRenderer.PerfDrawItems > 0,
                "the guideline scene produced no draw items");

            // 400 visuals x four arrays is around 50KB a frame when they are re-resolved; memoized it
            // is nothing at all. 16KB sits well clear of both.
            Assert.True(steady < 16 * 1024,
                $"a settled frame allocated {steady} bytes in collect for a scene whose only per-frame " +
                "work is resolving guidelines that have not changed");
        }

        /// <summary>
        /// The memos are keyed on the world transform, so a scene that MOVES must still get correct
        /// output -- the risk of caching by transform is a stale answer, not a slow one. Moving also
        /// has to stay bounded: refilling in place is the whole point of how the guides cache works.
        /// </summary>
        [Fact]
        public void AMovingSceneStaysBoundedAndKeepsRendering()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            for (int f = 0; f < 3; f++) CollectAllocOf(renderer, Chrome(f * 0.5f));

            long moving = 0;
            for (int f = 3; f < 3 + Frames; f++) moving += CollectAllocOf(renderer, Chrome(f * 0.5f));
            moving /= Frames;

            Assert.True(WgpuSceneRenderer.PerfDrawItems > 0,
                "the moving scene produced no draw items");

            // A moving scene legitimately re-snaps and re-strokes, so this is a far weaker bound than
            // the static one -- it exists to catch a cache that has started allocating a fresh entry
            // per frame per visual on top of the work it already had to do.
            Assert.True(moving < 2 * 1024 * 1024,
                $"a moving frame allocated {moving} bytes in the collect phase");
        }
    }
}
