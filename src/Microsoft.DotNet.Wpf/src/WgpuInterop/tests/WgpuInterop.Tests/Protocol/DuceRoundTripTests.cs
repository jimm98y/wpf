// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.DuceTest and OpacityMaskDecodeTest.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    /// <summary>
    /// The command-stream seam: a scene rendered directly, versus the same scene serialized to a
    /// DUCE batch, decoded into a fresh visual tree and rendered again. Nothing may be lost going
    /// through the protocol that WPF already emits in the full port.
    ///
    /// The per-primitive round-trips elsewhere in the suite cover individual resources; this one
    /// covers the visual TREE -- offsets, transforms, opacity and clips together, where a decoder
    /// can lose a property without dropping a primitive.
    /// </summary>
    public sealed class DuceRoundTripTests : RendererTestBase
    {
        public DuceRoundTripTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        private static SceneVisual Scene()
        {
            var root = new SceneVisual();

            var childA = new SceneVisual();
            childA.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(8, 8, 32, 32)),
                RgbaColor.FromBytes(255, 0, 0, 255)));
            root.Children.Add(childA);

            var childB = new SceneVisual { Offset = new Vector2(40, 40) };
            childB.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 16, 16)),
                new RgbaColor(0f, 0f, 0f, 0.5f)));
            root.Children.Add(childB);

            var childC = new SceneVisual { Offset = new Vector2(8, 40), Opacity = 0.5, Clip = new Rect(0, 0, 16, 32) };
            childC.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 32, 32)),
                RgbaColor.FromBytes(0, 255, 0, 255)));
            root.Children.Add(childC);

            var childE = new SceneVisual { Transform = Matrix3x2.CreateScale(2f, 2f) };
            childE.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 2, 4, 4)),
                RgbaColor.FromBytes(0, 0, 255, 255)));
            root.Children.Add(childE);

            return root;
        }

        [Fact]
        public void WholeVisualTree_SurvivesTheProtocolByteIdentically()
            => AssertProtocolRoundTripIsIdentical(Scene(), W, H);

        /// <summary>
        /// Absolute pixel checks on the DECODED image, so the round-trip test cannot pass by both
        /// paths being equally broken -- two identical blank images are byte-identical too.
        /// </summary>
        [Theory]
        [InlineData(16, 16, 255, 0, 0, "opaque red")]
        [InlineData(48, 48, 128, 128, 128, "50% black over white")]
        [InlineData(12, 50, 128, 255, 128, "clipped green, kept")]
        [InlineData(30, 50, 255, 255, 255, "clipped green, removed")]
        [InlineData(44, 8, 0, 0, 255, "scaled blue")]
        public void DecodedScene_RendersTheExpectedPixels(int x, int y, int r, int g, int b, string what)
        {
            byte[] batch = CompositionChannel.EncodeScene(Scene());
            var engine = new CompositionEngine();
            engine.ProcessBatch(batch);
            SceneVisual? rebuilt = engine.Root;
            Assert.True(rebuilt is not null, "CompositionEngine produced no root");

            byte[] px = NewRenderer().RenderToRgba(rebuilt!, W, H, White);
            new Image(px, W, H).AssertPixel(x, y, r, g, b, tol: 6, what);
        }
    }

    /// <summary>
    /// WPF's UIElement.OpacityMask emits MILCMD_VISUAL_SETALPHAMASK referencing a brush whose ALPHA
    /// modulates the visual. Driven through MILCMD rather than the object model because the decode
    /// step under test is "resolve the mask against the content bounds during Realize" -- a
    /// RelativeToBoundingBox mask has no meaning until those bounds are known.
    /// </summary>
    public sealed class OpacityMaskDecodeTests : RendererTestBase
    {
        public OpacityMaskDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 48, H = 48;

        private byte[] RenderMasked()
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));   // black content

            // Horizontal gradient, alpha 1 (left) to 0 (right), RelativeToBoundingBox.
            engine.SubmitCommand(MilCmd.LinearGradientBrush(4, 0, 0, 1, 0, mappingMode: 1, new[]
            {
                (0f, 1f, 1f, 1f, 1f),
                (1f, 1f, 1f, 1f, 0f),
            }));

            SceneVisual root = Realize(engine, MilCmd.DrawRectangleRecord(3, 0, 4, 4, 40, 40),
                                       hVisual: 2, hContent: 5);
            engine.SubmitCommand(MilCmd.VisualSetOpacityMask(2, 4));
            engine.Realize();
            return NewRenderer().RenderToRgba(engine.VisualByHandle(2)!, W, H, White);
        }

        [Fact]
        public void RelativeGradientMask_FadesTheVisualAcrossItsBounds()
        {
            var img = new Image(RenderMasked(), W, H);
            int left = img[8, 24].R, mid = img[24, 24].R, right = img[40, 24].R;

            Assert.True(left < 60, $"the left edge should stay opaque (alpha 1), was {left}");
            Assert.True(mid > 90 && mid < 190, $"the middle should be half-faded (alpha ~0.5), was {mid}");
            Assert.True(right > 220, $"the right edge should fade to background (alpha 0), was {right}");
            Assert.True(left < mid && mid < right,
                $"the fade must be monotonic left-to-right: {left}, {mid}, {right}");
        }
    }
}
