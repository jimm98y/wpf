// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.HitTestTest.
//
// GPU hit testing: the renderer writes a visual-id buffer and a query is a 1-pixel readback.
//
// The scene is round-tripped through the composition protocol first, because that is what assigns
// each visual the id the hit test returns. Building the tree in-process would leave the ids
// unassigned and the test would be checking numbers it invented.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class HitTestTests : RendererTestBase
    {
        public HitTestTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 200, H = 160;

        // Back rect (20,20)-(110,90); front rect (80,60)-(170,130); overlap (80,60)-(110,90).
        private static SceneVisual Scene()
        {
            var root = new SceneVisual();

            var back = new SceneVisual();
            back.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 20, 90, 70)),
                RgbaColor.FromBytes(200, 40, 40, 255)));
            root.Children.Add(back);

            var front = new SceneVisual();
            front.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(80, 60, 90, 70)),
                RgbaColor.FromBytes(40, 80, 200, 255)));
            root.Children.Add(front);

            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("Hello", new Vector2(30, 145), 12f,
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(text);

            return root;
        }

        /// <summary>The rebuilt tree plus the ids the engine assigned, in child order.</summary>
        private static (SceneVisual Root, uint Back, uint Front, uint Text) Rebuilt()
        {
            byte[] batch = CompositionChannel.EncodeScene(Scene());
            var engine = new CompositionEngine();
            engine.ProcessBatch(batch);
            SceneVisual? root = engine.Root;
            Assert.True(root is not null, "the protocol round-trip produced no root");
            return (root!, root!.Children[0].Id, root.Children[1].Id, root.Children[2].Id);
        }

        [Fact]
        public void HitTest_ResolvesTopmostVisualAtAPoint()
        {
            (SceneVisual root, uint back, uint front, uint text) = Rebuilt();
            using WgpuSceneRenderer renderer = NewRenderer();

            Assert.Equal(back, renderer.HitTest(root, 40, 40, W, H));
            Assert.Equal(front, renderer.HitTest(root, 150, 110, W, H));

            // The overlap is the important one: both visuals cover it, and the FRONT must win.
            Assert.Equal(front, renderer.HitTest(root, 95, 75, W, H));

            Assert.Equal(text, renderer.HitTest(root, 45, 143, W, H));
        }

        /// <summary>
        /// Misses must return 0. The (150,40) probe is the meaningful one: it lies inside the union
        /// of the two BOUNDING BOXES but outside both shapes' coverage, so a hit test that worked on
        /// bounds rather than on rendered coverage would wrongly report a hit there.
        /// </summary>
        [Theory]
        [InlineData(5, 5, "empty corner")]
        [InlineData(150, 40, "gap between the rects, inside their combined bounds")]
        [InlineData(-1, 10, "out of bounds")]
        public void HitTest_ReturnsZeroWhereNothingIsDrawn(int x, int y, string what)
        {
            (SceneVisual root, _, _, _) = Rebuilt();
            using WgpuSceneRenderer renderer = NewRenderer();
            Assert.True(renderer.HitTest(root, x, y, W, H) == 0u, $"{what} should hit nothing");
        }

        /// <summary>
        /// The id buffer is retained per frame: the first HitTest(root, ...) renders it, and later
        /// point-only queries are a pure readback with no scene re-walk. Those must agree with the
        /// full queries, or a second click in the same frame lands somewhere else.
        /// </summary>
        [Fact]
        public void CachedIdBuffer_AgreesWithTheFullQuery()
        {
            (SceneVisual root, uint back, uint front, _) = Rebuilt();
            using WgpuSceneRenderer renderer = NewRenderer();

            renderer.HitTest(root, 40, 40, W, H);      // renders and retains the id buffer

            Assert.Equal(back, renderer.HitTest(40, 40));
            Assert.Equal(front, renderer.HitTest(95, 75));
            Assert.True(renderer.HitTest(5, 5) == 0u, "cached readback should miss on the empty corner");
        }
    }
}
