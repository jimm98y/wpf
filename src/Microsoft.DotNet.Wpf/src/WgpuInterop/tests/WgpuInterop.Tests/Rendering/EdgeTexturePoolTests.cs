// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An animating path does not create a GPU texture every frame.
//
// Coverage masks and strokes hand their segment lists to the shader as an RG32Uint texture, and each
// one used to be a texture created and destroyed inside a single frame. Creating and destroying
// textures per frame is exactly the cost the layer pool next door already exists to avoid -- it says
// so, and names the GL backend where it hurts most.
//
// What made this worth doing was measuring it first. On a static scene, and on one animating forty
// simple ellipses, the answer is ZERO: the per-shape coverage cache answers those before any edge
// data is uploaded at all. It is only paths complex enough to miss that cache -- a chart line of two
// hundred segments, redrawn every frame -- that reach this code, and there it was one texture per
// path per frame. That is the scene below.
//
// The heights are bucketed to powers of two so that a path which gains a segment between frames
// still matches last frame's texture. Extra rows cost nothing and are never read: the shader maps
// slot i to (i % EdgeTexWidth, i / EdgeTexWidth), which does not involve the height.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class EdgeTexturePoolTests : RendererTestBase
    {
        public EdgeTexturePoolTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 400, H = 400;
        private const int Paths = 8;
        private const int Frames = 10;

        /// <summary>
        /// Paths long enough to miss the coverage cache, moving every frame. Anything simpler is
        /// served from the cache and never reaches the edge upload at all.
        /// </summary>
        private static SceneVisual Wiggling(int frame)
        {
            var root = new SceneVisual();
            for (int p = 0; p < Paths; p++)
            {
                var fig = new PathFigure(new Vector2(5, 200 + p));
                for (int i = 1; i <= 200; i++)
                {
                    float x = 5 + i * 1.95f;
                    float y = 200 + p + 60f * MathF.Sin((i + frame + p * 7) * 0.15f);
                    fig.Segments.Add(new LineSegment(new Vector2(x, y)));
                }
                fig.Segments.Add(new LineSegment(new Vector2(395, 395)));
                fig.Segments.Add(new LineSegment(new Vector2(5, 395)));
                fig.Closed = true;

                root.Content.Add(new GeometryFill(
                    new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig }),
                    RgbaColor.FromBytes(40, 90, 180, 255)));
            }
            return root;
        }

        [Fact]
        public void ASteadyStreamOfComplexPathsStopsCreatingTextures()
        {
            WgpuSceneRenderer renderer = NewRenderer();

            // The first frames legitimately create one texture per bucket in play.
            renderer.RenderToRgba(Wiggling(0), W, H, White);
            renderer.RenderToRgba(Wiggling(1), W, H, White);

            int created = 0, drawn = 0;
            for (int f = 2; f < 2 + Frames; f++)
            {
                WgpuSceneRenderer.PerfReset();
                renderer.RenderToRgba(Wiggling(f), W, H, White);
                created += WgpuSceneRenderer.PerfEdgeTextures;
                drawn += WgpuSceneRenderer.PerfDrawItems;
            }

            Assert.True(drawn > 0,
                "the scene drew nothing over " + Frames + " frames; it is no longer exercising the " +
                "path this measures");

            // Every frame changes every path, so each one re-uploads its segments. None of them
            // should need a new texture to upload into.
            Assert.True(created == 0,
                $"{Frames} frames of {Paths} continuously changing complex paths created {created} GPU " +
                "textures. They differ in content, not in size, so the pool should have had one ready " +
                "for each.");
        }
    }
}
