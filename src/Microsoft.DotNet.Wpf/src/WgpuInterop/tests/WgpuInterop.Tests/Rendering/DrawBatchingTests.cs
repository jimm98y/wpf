// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Adjacent draws that share their state are recorded as one.
//
// Almost every DrawItem this renderer emits is a single quad -- six indices -- and they were each
// getting a SetPipeline, a SetBindGroup, a SetScissorRect and a DrawIndexed of their own. A scene
// made of a hundred rectangles is a hundred of everything, when the indices for all hundred are
// already sitting contiguously in one buffer and want nothing but the same state.
//
// So a run of items that agree on pipeline, bind group and scissor, and whose indices follow one
// another, becomes one drawIndexed; and the three setters are skipped whenever what they would bind
// is already bound. Order is preserved -- one drawIndexed walks its indices in order exactly as the
// separate calls did -- which is what makes this safe for overlapping translucent geometry.
//
// The assertions here are about the RECORDING. That the pixels are unchanged is asserted by the
// hundred-odd rendering tests around this one, which is the right place for it: if merging ever
// changed what a frame looks like, they would say so far more precisely than a ratio could.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class DrawBatchingTests : RendererTestBase
    {
        public DrawBatchingTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 240, H = 240;
        private const int Rows = 10, Columns = 10;

        /// <summary>
        /// A grid of solid rectangles: the same pipeline, no bind group, one clip. This is the shape
        /// of a real list, a data grid, or any panel of flat chrome.
        /// </summary>
        private static SceneVisual Grid(int frame)
        {
            float d = frame * 0.3f;
            var root = new SceneVisual();
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Columns; c++)
                {
                    root.Content.Add(new GeometryFill(
                        new RectangleGeometry(new Rect(4 + c * 23 + d, 4 + r * 23, 18, 18)),
                        RgbaColor.FromBytes((byte)(30 + c * 20), (byte)(40 + r * 18), 200, 255)));
                }
            }
            return root;
        }

        private (int Items, int Calls) Record(SceneVisual scene)
        {
            WgpuSceneRenderer renderer = NewRenderer();
            renderer.RenderToRgba(scene, W, H, White);          // warm the caches

            WgpuSceneRenderer.PerfReset();
            renderer.RenderToRgba(scene, W, H, White);
            return (WgpuSceneRenderer.PerfDrawItems, WgpuSceneRenderer.PerfDrawCalls);
        }

        [Fact]
        public void AGridOfRectanglesIsRecordedAsFarFewerDrawCallsThanRectangles()
        {
            (int items, int calls) = Record(Grid(0));

            Assert.True(items >= Rows * Columns,
                $"the scene only produced {items} draw items for {Rows * Columns} rectangles; it is no " +
                "longer the scene this test was written for");

            // When this was written the whole grid came out as ONE call -- 100 items, and 400 in the
            // same one when the grid was made bigger. The threshold is well below that on purpose:
            // the exact ratio depends on how the scene walker orders its emissions and on which fills
            // the coverage cache has already absorbed, and what must hold is only that a grid of
            // identical-state quads does not cost a call each.
            Assert.True(calls * 10 <= items,
                $"{items} draw items were recorded as {calls} draw calls -- a grid of rectangles that " +
                "share a pipeline, a bind group and a clip should merge far harder than that");
        }

        /// <summary>
        /// Merging must not reach across a change of state. Alternating two fills that need different
        /// bind groups gives every item its own call, and that is the correct answer -- a test that
        /// only checked "fewer calls" would be satisfied by a renderer that wrongly merged them.
        /// </summary>
        [Fact]
        public void DrawsThatNeedDifferentStateAreNotMerged()
        {
            var root = new SceneVisual();
            for (int i = 0; i < 8; i++)
            {
                // Alternating clips: adjacent items can never share a scissor.
                var clipped = new SceneVisual { Clip = new Rect(0, 0, 100 + i * 10, 240) };
                clipped.Content.Add(new GeometryFill(
                    new RectangleGeometry(new Rect(10 + i * 12, 10, 10, 200)),
                    RgbaColor.FromBytes(200, 40, 40, 255)));
                root.Children.Add(clipped);
            }

            (int items, int calls) = Record(root);

            Assert.Equal(items, calls);
        }
    }
}
