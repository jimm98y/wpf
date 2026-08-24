// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.RenderTest and EffectDecodeTest.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    /// <summary>
    /// The headless stand-in for an eventual pixel-diff-against-milcore gate: opaque fills,
    /// premultiplied-alpha opacity blending, a child offset, a scale transform and a rectangular
    /// clip, all in one tree with exact pixel expectations.
    ///
    /// Tolerance is 1, not the 6-8 the shape tests use. Every probe here sits in the middle of a flat
    /// region, so anti-aliasing cannot reach it -- anything that moves these pixels is a blend or
    /// transform error, not an edge.
    /// </summary>
    public sealed class SceneRenderTests : RendererTestBase
    {
        public SceneRenderTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        private static SceneVisual Scene()
        {
            var root = new SceneVisual();

            // childA: opaque red, device (8,8)-(40,40).
            var childA = new SceneVisual();
            childA.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(8, 8, 32, 32)),
                RgbaColor.FromBytes(255, 0, 0, 255)));
            root.Children.Add(childA);

            // childB: 50% black at offset (40,40) -> must resolve to mid-grey over white.
            var childB = new SceneVisual { Offset = new Vector2(40, 40) };
            childB.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 16, 16)),
                new RgbaColor(0f, 0f, 0f, 0.5f)));
            root.Children.Add(childB);

            // childC: 50%-opacity green, clipped to its left half. The geometry is 32 wide but the
            // clip keeps only x<24, so the right half proves the clip is applied rather than the
            // geometry being narrower than claimed.
            var childC = new SceneVisual
            {
                Offset = new Vector2(8, 40),
                Opacity = 0.5,
                Clip = new Rect(0, 0, 16, 32),
            };
            childC.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 32, 32)),
                RgbaColor.FromBytes(0, 255, 0, 255)));
            root.Children.Add(childC);

            // childE: scale x2, so Rect(20,2,4,4) lands at device (40,4)-(48,12). Those pixels are
            // only covered if the scale is genuinely applied.
            var childE = new SceneVisual { Transform = Matrix3x2.CreateScale(2f, 2f) };
            childE.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 2, 4, 4)),
                RgbaColor.FromBytes(0, 0, 255, 255)));
            root.Children.Add(childE);

            return root;
        }

        [Theory]
        [InlineData(60, 2, 255, 255, 255, "background")]
        [InlineData(16, 16, 255, 0, 0, "opaque red fill")]
        [InlineData(48, 48, 128, 128, 128, "opacity blend, 50% black over white")]
        [InlineData(12, 50, 128, 255, 128, "clip kept, green left half")]
        [InlineData(30, 50, 255, 255, 255, "clip removed, green right half")]
        [InlineData(44, 8, 0, 0, 255, "scale transform, blue")]
        public void VisualTree_RendersExactPixels(int x, int y, int r, int g, int b, string what)
            => new Image(Render(Scene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 1, what);
    }

    /// <summary>
    /// WPF's Effect property emits MILCMD_VISUAL_SETEFFECT referencing a BlurEffect or
    /// DropShadowEffect resource. Driven through real MILCMD rather than the object model, because
    /// the decode is the subject -- particularly the drop shadow, where WPF sends DEPTH and
    /// DIRECTION and the decoder has to convert them into an offset.
    /// </summary>
    public sealed class EffectDecodeTests : RendererTestBase
    {
        public EffectDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 80, H = 80;

        /// <summary>A visual (handle 2) with a black 24x24 square at (20,20); effect at handle 7.</summary>
        private byte[] RenderWithEffect(byte[] effectResource)
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));

            SceneVisual root = Realize(engine, MilCmd.DrawRectangleRecord(3, 0, 20, 20, 24, 24),
                                       hVisual: 2, hContent: 5);
            engine.SubmitCommand(effectResource);
            engine.SubmitCommand(MilCmd.VisualSetEffect(2, 7));
            engine.Realize();

            return NewRenderer().RenderToRgba(engine.VisualByHandle(2)!, W, H, White);
        }

        [Fact]
        public void BlurEffect_DecodesAndSoftensTheEdge()
        {
            var img = new Image(RenderWithEffect(MilCmd.BlurEffect(7, 5.0)), W, H);

            int interior = img[30, 30].R, edge = img[46, 30].R, far = img[70, 30].R;

            Assert.True(interior < 60, $"the blurred interior should stay solid, was {interior}");
            Assert.True(edge > 60 && edge < 240, $"the edge should be a soft grey, was {edge}");
            Assert.True(far > 245, $"pixels beyond the kernel should stay clear, was {far}");
        }

        /// <summary>
        /// Depth 17 at direction 315 degrees converts to an offset of about (+12,+12), i.e.
        /// down-right. The up-left probe is what proves the DIRECTION was decoded: a decoder that
        /// ignored it would still produce a shadow, just in the wrong place, and every other probe
        /// here would pass.
        /// </summary>
        [Fact]
        public void DropShadowEffect_ConvertsDepthAndDirectionIntoAnOffset()
        {
            var img = new Image(RenderWithEffect(
                MilCmd.DropShadowEffect(7, depth: 17, directionDegrees: 315, opacity: 0.7, blurRadius: 2)), W, H);

            int content = img[30, 30].R, shadow = img[52, 52].R, upLeft = img[12, 12].R;

            Assert.True(content < 40, $"the content should stay solid on top, was {content}");
            Assert.True(shadow > 40 && shadow < 200,
                $"a grey shadow should appear down-right from depth+direction, was {shadow}");
            Assert.True(upLeft > 245, $"there should be no shadow up-left, was {upLeft}");
        }
    }
}
