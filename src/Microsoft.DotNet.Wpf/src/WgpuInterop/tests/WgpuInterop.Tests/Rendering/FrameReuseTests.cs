// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A frame in which nothing moved costs nothing, and one in which a caret moved costs the caret.
//
// This file used to assert the opposite, and said why: CollectVisual walked the whole visual tree and
// emitted every primitive on every frame, so a frame where one caret moved paid for the 600 static
// items beside it. Measured then on an 800x800 window, per frame, with the caches warm:
//
//     600 items    collect 0.34 ms   encode 0.07 ms
//    2000 items    collect 1.03 ms   encode 0.11 ms
//    8000 items    collect 3.61 ms   encode 0.37 ms
//
// -- about 0.45 microseconds an item, and its closing line was that skipping unchanged content "needs
// a way to know, which the scene graph does not currently offer". WgpuSceneRenderer.PartialTarget is
// that way: its DamageTracker diffs each frame's tree against the last one, and RenderFrameDamaged
// redraws only the damage into a texture that keeps the previous frame. A primitive whose
// conservative box misses the damage is not collected at all -- that is the half that cost -- and
// what is collected is scissored to the damage. PartialRedrawTests proves the pixels identical to a
// full frame; this file pins down that the work actually went away.
//
// The assertion is on draw ITEMS rather than on milliseconds, so it is deterministic.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class FrameReuseTests : RendererTestBase
    {
        public FrameReuseTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 400, H = 400;
        private const int Tiles = 200;

        /// <summary>Flat chrome. <paramref name="caretRow"/> &lt; 0 leaves the scene identical.</summary>
        private static SceneVisual Chrome(int caretRow)
        {
            var root = new SceneVisual();
            for (int i = 0; i < Tiles; i++)
            {
                root.Content.Add(new GeometryFill(
                    new RectangleGeometry(new Rect(4 + (i % 20) * 19, 4 + (i / 20) * 19, 15, 15)),
                    RgbaColor.FromBytes((byte)(40 + i % 200), 120, 200, 255)));
            }
            if (caretRow >= 0)
            {
                root.Content.Add(new GeometryFill(
                    new RectangleGeometry(new Rect(200, 4 + (caretRow % 10) * 19, 2, 15)),
                    RgbaColor.FromBytes(10, 10, 10, 255)));
            }
            return root;
        }

        /// <summary>A full frame, as every frame used to be.</summary>
        private (int Items, int Calls) FullFrame(WgpuSceneRenderer r, SceneVisual scene)
        {
            WgpuSceneRenderer.PerfReset();
            r.BeginFrame();
            r.RenderToRgba(scene, W, H, White);
            r.EndFrame();
            return (WgpuSceneRenderer.PerfDrawItems, WgpuSceneRenderer.PerfDrawCalls);
        }

        /// <summary>A frame through partial redraw, into a stand-in swap chain.</summary>
        private (int Items, int Calls) PartialFrame(WgpuSceneRenderer r, WgpuSceneRenderer.PartialTarget t, IntPtr view, SceneVisual scene)
        {
            WgpuSceneRenderer.PerfReset();
            r.BeginFrame();
            t.Render(scene, view, WGPUTextureFormat.RGBA8Unorm, W, H, White, false);
            r.EndFrame();
            return (WgpuSceneRenderer.PerfDrawItems, WgpuSceneRenderer.PerfDrawCalls);
        }

        [Fact]
        public void AFullFrameStillEmitsEveryPrimitive()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            SceneVisual scene = Chrome(-1);

            FullFrame(renderer, scene);                              // warm
            (int items, int calls) = FullFrame(renderer, scene);     // the same instance, unchanged

            // A full frame is still a full frame -- the first one through partial redraw is one too.
            Assert.Equal(Tiles, items);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void AnUnchangedFrameEmitsNothing()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            using var target = new WgpuSceneRenderer.PartialTarget(renderer);
            (IntPtr tex, IntPtr view) = renderer.CreatePersistentTarget(W, H, WGPUTextureFormat.RGBA8Unorm);
            try
            {
                SceneVisual scene = Chrome(-1);
                (int first, _) = PartialFrame(renderer, target, view, scene);
                Assert.Equal(Tiles, first);
                (int items, int calls) = PartialFrame(renderer, target, view, scene);
                Assert.Equal(0, items);
                Assert.Equal(0, calls);
                Assert.Equal(0, target.LastPixels);
            }
            finally { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); }
        }

        [Fact]
        public void MovingOneSmallThingCostsOnlyWhatItTouches()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            using var target = new WgpuSceneRenderer.PartialTarget(renderer);
            (IntPtr tex, IntPtr view) = renderer.CreatePersistentTarget(W, H, WGPUTextureFormat.RGBA8Unorm);
            try
            {
                // One scene, kept, with only its caret primitive replaced -- as a retained tree changes.
                SceneVisual scene = Chrome(0);
                PartialFrame(renderer, target, view, scene);
                scene.Content[scene.Content.Count - 1] = Chrome(1).Content[Tiles];
                (int oneMoved, _) = PartialFrame(renderer, target, view, scene);

                // The caret's old and new places: the clear of each damage rectangle, the caret, and
                // the handful of tiles its damage (caret plus margin) reaches. Not 201.
                Assert.False(target.LastFull);
                Assert.True(oneMoved < 20, $"{oneMoved} items emitted for a moved caret");
            }
            finally { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); }
        }
    }
}
