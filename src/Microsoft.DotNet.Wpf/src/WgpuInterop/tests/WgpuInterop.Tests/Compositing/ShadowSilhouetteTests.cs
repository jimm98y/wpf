// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A drop shadow cached by its SILHOUETTE (WgpuSceneRenderer.ShadowSilhouetteKey).
//
// A card -- a Border with an opaque rounded background and a DropShadowEffect -- whose content keeps
// changing used to re-blur its shadow every frame: the subtree pass plus two blur passes, for a shadow
// that depends only on the background's outline. Now the blurred shadow is shared between bakes of
// the same silhouette. These tests hold it to being EXACT (the frame after a content change is pixel
// for pixel what a fresh renderer draws) and to refusing when the content could change the alpha.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public sealed class ShadowSilhouetteTests : RendererTestBase
    {
        public ShadowSilhouetteTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 200, H = 160;
        private static readonly RgbaColor Paper = RgbaColor.FromBytes(236, 239, 243, 255);

        /// <summary>A card: opaque rounded background with a shadow, and one child square.</summary>
        private static SceneVisual Card(float squareX, RgbaColor squareColour, float squareSize = 30f)
        {
            var root = new SceneVisual();
            var card = new SceneVisual
            {
                Effect = new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 255), 16, 0, 3),
            };
            card.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(30, 30, 140, 100), 12, 12),
                                              new SolidColorBrush(RgbaColor.FromBytes(255, 255, 255, 255))));
            var child = new SceneVisual();
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(squareX, 60, squareSize, squareSize)),
                                               new SolidColorBrush(squareColour)));
            card.Children.Add(child);
            root.Children.Add(card);
            return root;
        }

        private static void AssertSame(byte[] expected, byte[] actual, string what)
        {
            for (int i = 0; i < expected.Length; i++)
                Assert.True(expected[i] == actual[i],
                            $"{what}: pixel ({i / 4 % W},{i / 4 / W}) channel {i % 4} is {actual[i]}, a fresh render draws {expected[i]}");
        }

        [Fact]
        public void ACardWhoseContentChanges_ReusesItsShadow_AndDrawsExactlyWhatAFreshRenderDraws()
        {
            WgpuSceneRenderer renderer = NewRenderer();
            renderer.RenderToRgba(Card(60, RgbaColor.FromBytes(200, 30, 30, 255)), W, H, Paper);

            WgpuSceneRenderer.PerfReset();
            byte[] second = renderer.RenderToRgba(Card(100, RgbaColor.FromBytes(30, 30, 200, 255)), W, H, Paper);
            Assert.True(WgpuSceneRenderer.PerfLayerMiss > 0, "the changed card should have been re-baked");
            Assert.True(WgpuSceneRenderer.PerfShadowHits > 0, "the re-baked card blurred its shadow again");

            byte[] fresh = NewRenderer().RenderToRgba(Card(100, RgbaColor.FromBytes(30, 30, 200, 255)), W, H, Paper);
            AssertSame(fresh, second, "a reused shadow");
        }

        [Fact]
        public void ContentReachingPastTheBackground_IsNotGivenTheBackgroundsShadow()
        {
            // The square pokes out of the card's right edge, so the layer's alpha -- and its shadow --
            // is not the background's.
            WgpuSceneRenderer renderer = NewRenderer();
            renderer.RenderToRgba(Card(150, RgbaColor.FromBytes(200, 30, 30, 255)), W, H, Paper);

            WgpuSceneRenderer.PerfReset();
            byte[] second = renderer.RenderToRgba(Card(155, RgbaColor.FromBytes(30, 30, 200, 255)), W, H, Paper);
            Assert.Equal(0, WgpuSceneRenderer.PerfShadowHits);

            byte[] fresh = NewRenderer().RenderToRgba(Card(155, RgbaColor.FromBytes(30, 30, 200, 255)), W, H, Paper);
            AssertSame(fresh, second, "a card whose content spills over its background");
        }
    }
}
