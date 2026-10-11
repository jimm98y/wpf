// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Bind group layouts are acquired once, not once per draw.
//
// wgpuRenderPipelineGetBindGroupLayout returns a NEW reference on every call. The 2D renderer used
// to call it inline wherever it built a bind group -- six places -- and never released the result,
// so a layout leaked for every draw of every frame. Nothing about that is visible in a rendered
// image: the frames come out correct and the process grows until it is killed, which is why it
// survived so long and why the guard has to be a counter rather than a pixel comparison.
//
// The 3D path had already learnt this and cached its layouts (Get3DBindGroupLayout); the 2D path
// now does the same, keyed the way the pipelines themselves are keyed.
//
// Two things are asserted together, and the pairing is the point. That steady-state frames acquire
// NO layouts is the leak assertion. That those same frames still create bind groups is what stops
// the first one passing vacuously -- a scene the renderer had optimised into doing nothing would
// satisfy "acquires no layouts" while proving nothing at all.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Lifecycle
{
    public sealed class BindGroupLayoutTests : RendererTestBase
    {
        public BindGroupLayoutTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 160, H = 160;
        private const int Frames = 12;

        /// <summary>
        /// A scene that MOVES, and that fills through several different pipelines.
        ///
        /// Moving matters: a static tree is served from the per-shape coverage cache and issues no
        /// bind groups at all on a repeat frame (see BitmapCacheTests), which would leave nothing for
        /// this to measure. Shifting it every frame misses that cache the way a real animation does.
        /// The mix of fills matters for the other direction -- solid, gradient and a clip reach
        /// different FillKinds, and the leak was per-kind.
        /// </summary>
        private static SceneVisual MovingScene(int frame)
        {
            float d = frame * 0.7f;
            var root = new SceneVisual();

            root.Content.Add(new GeometryFill(
                new EllipseGeometry(new Vector2(50 + d, 50 + d), 30, 22),
                RgbaColor.FromBytes(40, 90, 180, 255)));

            root.Content.Add(new GeometryFill(
                new RoundedRectangleGeometry(new Rect(20 + d, 95, 90, 34), 10, 10),
                new LinearGradientBrush(
                    new Vector2(0, 0), new Vector2(1, 1),
                    new[]
                    {
                        new GradientStop(0f, RgbaColor.FromBytes(220, 60, 40, 255)),
                        new GradientStop(1f, RgbaColor.FromBytes(40, 200, 120, 255)),
                    })));

            var clipped = new SceneVisual { Clip = new Rect(10, 10, 120 + d, 120) };
            clipped.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(30, 30 + d, 70, 60)),
                RgbaColor.FromBytes(250, 200, 30, 200)));
            root.Children.Add(clipped);

            return root;
        }

        [Fact]
        public void SteadyStateFramesAcquireNoNewLayouts()
        {
            WgpuSceneRenderer renderer = NewRenderer();

            // Warm: the first frames are where the layouts legitimately come from, one per pipeline
            // this scene reaches. Two of them, because a moving scene takes a frame to settle.
            renderer.RenderToRgba(MovingScene(0), W, H, White);
            renderer.RenderToRgba(MovingScene(1), W, H, White);

            int acquiresBefore = WgpuSceneRenderer.PerfLayoutAcquires;
            int bindGroups = 0;

            for (int f = 2; f < 2 + Frames; f++)
            {
                WgpuSceneRenderer.PerfReset();
                renderer.RenderToRgba(MovingScene(f), W, H, White);
                bindGroups += WgpuSceneRenderer.PerfBindGroups;
            }

            int acquired = WgpuSceneRenderer.PerfLayoutAcquires - acquiresBefore;

            Assert.True(bindGroups > 0,
                "the scene issued no bind groups at all over " + Frames + " frames, so this proves " +
                "nothing about layouts; it has stopped exercising the path it was written for.");

            Assert.True(acquired == 0,
                $"{Frames} steady-state frames acquired {acquired} bind group layouts for " +
                $"{bindGroups} bind groups. A layout is a property of a PIPELINE, and the pipelines " +
                "are cached, so a settled frame must acquire none -- every one of these is a native " +
                "reference that is never released.");
        }

        /// <summary>
        /// The count is bounded by the pipelines, not by the drawing. Rendering four times the work
        /// must not acquire more layouts than rendering it once, which is the invariant the per-draw
        /// version broke.
        /// </summary>
        [Fact]
        public void MoreDrawingDoesNotMeanMoreLayouts()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            renderer.RenderToRgba(MovingScene(0), W, H, White);
            renderer.RenderToRgba(MovingScene(1), W, H, White);

            int before = WgpuSceneRenderer.PerfLayoutAcquires;

            // The same kinds of fill, four times as many of them.
            for (int f = 2; f < 6; f++)
            {
                var root = new SceneVisual();
                for (int i = 0; i < 4; i++)
                {
                    SceneVisual copy = MovingScene(f * 4 + i);
                    root.Children.Add(copy);
                }
                renderer.RenderToRgba(root, W, H, White);
            }

            Assert.Equal(before, WgpuSceneRenderer.PerfLayoutAcquires);
        }
    }
}
