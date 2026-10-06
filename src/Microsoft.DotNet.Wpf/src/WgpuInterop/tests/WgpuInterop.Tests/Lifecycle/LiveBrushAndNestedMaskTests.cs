// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Two things that made a static card re-render every frame.
//
// 1. A GPU-live brush (a VisualBrush or DrawingBrush rendered from its source visual) put the FRAME
//    NUMBER into the layer-cache key, so any layer holding one could never hit the cache. It is keyed
//    on the source visual's content now -- which must still move the key when that content changes.
//
// 2. A descendant opacity mask or clip forced its enclosing layer to FULL-TARGET size, a leftover from
//    when nested layers were themselves full-target. A card holding one was drawn, and its shadow
//    blurred, over the whole window. Nested layers are region-sized and composite at absolute device
//    coordinates, so a card-sized parent has to draw the masked child exactly where it belongs.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Lifecycle
{
    public sealed class LiveBrushAndNestedMaskTests : RendererTestBase
    {
        public LiveBrushAndNestedMaskTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 240, H = 160;
        private static readonly RgbaColor Paper = RgbaColor.FromBytes(236, 239, 243, 255);

        private static SceneVisual Source(RgbaColor colour)
        {
            var src = new SceneVisual();
            src.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 40, 20)), new SolidColorBrush(colour)));
            return src;
        }

        /// <summary>A layer (an opacity group) holding a fill painted by a live brush of Source.</summary>
        private static SceneVisual WithLiveBrush(RgbaColor sourceColour)
        {
            var root = new SceneVisual();
            var group = new SceneVisual { Opacity = 0.9 };
            group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 20, 120, 60)),
                                               new SolidColorBrush(RgbaColor.FromBytes(255, 255, 255, 255))));
            group.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(40, 40, 80, 40)),
                new ImageBrush(Source(sourceColour), 7, 80, 40, 0f, 0f, 1f, 1f, 80f, 40f, 1f)));
            root.Children.Add(group);
            return root;
        }

        /// <summary>One frame the way the sink draws it: BeginFrame advances the frame number that
        /// the old key mixed in, and EndFrame evicts.</summary>
        private static byte[] Frame(WgpuSceneRenderer r, SceneVisual scene)
        {
            r.BeginFrame();
            byte[] px = r.RenderToRgba(scene, W, H, Paper);
            r.EndFrame();
            return px;
        }

        [Fact]
        public void ALayerHoldingAnUnchangedLiveBrush_HitsTheCache()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            Frame(renderer, WithLiveBrush(RgbaColor.FromBytes(200, 30, 30, 255)));
            Frame(renderer, WithLiveBrush(RgbaColor.FromBytes(200, 30, 30, 255)));

            WgpuSceneRenderer.PerfReset();
            Frame(renderer, WithLiveBrush(RgbaColor.FromBytes(200, 30, 30, 255)));
            Assert.True(WgpuSceneRenderer.PerfLayerHits > 0, "the layer never hit the cache");
            Assert.Equal(0, WgpuSceneRenderer.PerfLayerMiss);
        }

        [Fact]
        public void ALiveBrushWhoseSourceChanges_IsRedrawn()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            Frame(renderer, WithLiveBrush(RgbaColor.FromBytes(200, 30, 30, 255)));
            byte[] changed = Frame(renderer, WithLiveBrush(RgbaColor.FromBytes(30, 30, 200, 255)));
            byte[] fresh = NewRenderer().RenderToRgba(WithLiveBrush(RgbaColor.FromBytes(30, 30, 200, 255)), W, H, Paper);
            for (int i = 0; i < fresh.Length; i++)
                Assert.True(fresh[i] == changed[i],
                            $"pixel ({i / 4 % W},{i / 4 / W}) kept the brush's old source: {changed[i]} where a fresh render draws {fresh[i]}");
        }

        /// <summary>A masked child, alone or inside a drop-shadowed card.</summary>
        private static SceneVisual MaskedChild(bool inCard)
        {
            var root = new SceneVisual();
            var card = new SceneVisual();
            if (inCard) card.Effect = new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 255), 12, 0, 3);
            card.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(30, 20, 180, 120), 10, 10),
                                              new SolidColorBrush(RgbaColor.FromBytes(255, 255, 255, 255))));
            var masked = new SceneVisual
            {
                OpacityMask = new LinearGradientBrush(new Vector2(0, 0), new Vector2(0, 1), new GradientStop[]
                {
                    new(0f, RgbaColor.FromBytes(0, 0, 0, 255)),
                    new(1f, RgbaColor.FromBytes(0, 0, 0, 0)),
                }),
            };
            masked.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(60, 50, 120, 50)),
                                                new SolidColorBrush(RgbaColor.FromBytes(240, 120, 20, 255))));
            card.Children.Add(masked);
            root.Children.Add(card);
            return root;
        }

        [Fact]
        public void AMaskedChildInsideACardLayer_IsDrawnWhereItIsDrawnWithoutOne()
        {
            byte[] inCard = NewRenderer().RenderToRgba(MaskedChild(inCard: true), W, H, Paper);
            byte[] alone = NewRenderer().RenderToRgba(MaskedChild(inCard: false), W, H, Paper);
            // Inside the card background, well clear of the shadow: the layer round trip may move a
            // level through 8-bit premultiplied storage, no more.
            int worst = 0;
            for (int y = 45; y < 105; y++)
                for (int x = 55; x < 185; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = (y * W + x) * 4 + c;
                        worst = System.Math.Max(worst, System.Math.Abs(inCard[i] - alone[i]));
                    }
            Assert.True(worst <= 2, $"the masked child moved or vanished inside the card layer (worst channel difference {worst})");
        }
    }
}
