// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A frame in which nothing moved costs the same as the first one.
//
// This describes what the renderer does NOW, and it is here because the number behind it is the
// argument for changing it. CollectVisual walks the whole visual tree and emits every primitive on
// every frame; nothing above it asks whether the scene changed, and no damage region narrows it.
//
// Measured on an 800x800 window, per frame, with the caches warm:
//
//     600 items    collect 0.34 ms   encode 0.07 ms
//    2000 items    collect 1.03 ms   encode 0.11 ms
//    8000 items    collect 3.61 ms   encode 0.37 ms
//
// About 0.45 microseconds an item, linear, and paid in full by a frame where one caret moved -- 601
// items collected in the same 0.32 ms as the 600 static ones beside it. At eight thousand items that
// is over half a 60Hz budget spent rebuilding a description of a picture that did not change.
//
// Note WHICH resource that is. The GPU side is already cheap: the same frames record a single draw
// call and a single render pass, because adjacent same-state draws merge. A damage region that
// scissored the GPU would be optimising the half that is not the problem. What would pay is not
// walking, or not re-emitting, a subtree that did not change -- and the honest reason that is not in
// this commit is that it needs a way to know, which the scene graph does not currently offer.
//
// The assertion is on draw ITEMS rather than on milliseconds, so it is deterministic. If someone
// teaches the renderer to skip unchanged content, this fails, and it should: the failure is the
// prompt to come back and rewrite this file's numbers.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

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

        private (int Items, int Calls) Frame(WgpuSceneRenderer r, SceneVisual scene)
        {
            WgpuSceneRenderer.PerfReset();
            r.BeginFrame();
            r.RenderToRgba(scene, W, H, White);
            r.EndFrame();
            return (WgpuSceneRenderer.PerfDrawItems, WgpuSceneRenderer.PerfDrawCalls);
        }

        [Fact]
        public void AnUnchangedFrameStillEmitsEveryPrimitive()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            SceneVisual scene = Chrome(-1);

            Frame(renderer, scene);                              // warm
            (int items, int calls) = Frame(renderer, scene);     // the same instance, unchanged

            Assert.Equal(Tiles, items);

            // The GPU side of that same frame is already one call, which is the point: what costs is
            // building the description, not drawing it.
            Assert.Equal(1, calls);
        }

        [Fact]
        public void MovingOneSmallThingCostsAsMuchAsMovingEverything()
        {
            WgpuSceneRenderer renderer = NewRenderer();

            Frame(renderer, Chrome(0));
            (int oneMoved, _) = Frame(renderer, Chrome(1));

            Assert.Equal(Tiles + 1, oneMoved);
        }
    }
}
