// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.BrushTest, RadialGradientTest and SpreadTest.
//
// Gradient midpoints depend on the COMPOSITING MODE, and every one of these tests has to account for
// it. Stops always interpolate in sRGB space (WPF's default SRgbLinearInterpolation); what changes is
// the space the result is STORED in. Gamma mode (the default) keeps the gamma value verbatim on a
// UNORM target, so a black-to-white midpoint reads ~127; linear mode (WPF_WEBGPU_GAMMA=0) decodes it
// to linear, so the same midpoint reads ~54-60. ENDPOINTS are 0/255 under both, which is why only
// midpoints discriminate -- and why a test that only checked endpoints would pass in either mode
// while proving nothing about the blend space.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class GradientAndImageBrushTests : RendererTestBase
    {
        public GradientAndImageBrushTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        /// <summary>Whether stored values are gamma-encoded, which sets every midpoint expectation below.</summary>
        private static bool GammaMode => WgpuSceneRenderer.s_gammaComposite;

        // ---- linear gradient + image brush --------------------------------------------------

        private static SceneVisual BrushScene()
        {
            var root = new SceneVisual();

            var gradientVisual = new SceneVisual();
            gradientVisual.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(4, 4, 56, 16)),
                new LinearGradientBrush(new Vector2(4, 4), new Vector2(60, 4), new[]
                {
                    new GradientStop(0f, RgbaColor.FromBytes(255, 0, 0, 255)),
                    new GradientStop(1f, RgbaColor.FromBytes(0, 0, 255, 255)),
                })));
            root.Children.Add(gradientVisual);

            // 2x2 checker, row-major: TL red, TR green, BL blue, BR yellow.
            byte[] checker =
            {
                255, 0, 0, 255,   0, 255, 0, 255,
                0, 0, 255, 255,   255, 255, 0, 255,
            };
            var imageVisual = new SceneVisual();
            imageVisual.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(8, 36, 16, 16)), new ImageBrush(checker, 2, 2)));
            root.Children.Add(imageVisual);

            return root;
        }

        /// <summary>
        /// Ramp Rect(4,4,56,16) along (4,4)->(60,4), red at 0 to blue at 1. The gradient is
        /// continuous at ~4.5 per channel per pixel over 56px, so a sample even a couple of pixels in
        /// is already slightly shaded; the tolerances reflect that rate rather than demanding pure
        /// endpoints.
        /// </summary>
        [Fact]
        public void LinearGradient_RampsAcrossItsAxis()
        {
            var img = new Image(Render(BrushScene(), W, H), W, H);
            byte mid = GammaMode ? (byte)127 : (byte)54;

            img.AssertPixel(5, 12, 250, 0, 5, tol: 14, "gradient near start (red)");
            img.AssertPixel(58, 12, 5, 0, 250, tol: 14, "gradient near end (blue)");
            img.AssertPixel(32, 12, mid, 0, mid, tol: 12,
                $"gradient midpoint (purple) in {(GammaMode ? "gamma" : "linear")} mode");
        }

        /// <summary>The 2x2 checker over Rect(8,36,16,16), sampled nearest: each quadrant must land on its own texel.</summary>
        [Theory]
        [InlineData(11, 39, 255, 0, 0, "image TL (red)")]
        [InlineData(21, 39, 0, 255, 0, "image TR (green)")]
        [InlineData(11, 49, 0, 0, 255, "image BL (blue)")]
        [InlineData(21, 49, 255, 255, 0, "image BR (yellow)")]
        public void ImageBrush_SamplesTheRightTexel(int x, int y, int r, int g, int b, string what)
            => new Image(Render(BrushScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 2, what);

        [Fact]
        public void GradientAndImageBrushes_SurviveProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(BrushScene(), W, H);

        // ---- radial gradient ----------------------------------------------------------------

        /// <summary>
        /// A radial gradient is evaluated PER PIXEL -- unlike a linear one it cannot be expressed by
        /// per-vertex interpolation, so this exercises a genuinely different shader path.
        /// </summary>
        private static SceneVisual RadialScene()
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            var stops = new[]
            {
                new GradientStop(0f, RgbaColor.FromBytes(255, 255, 255, 255)),
                new GradientStop(1f, RgbaColor.FromBytes(0, 0, 0, 255)),
            };
            fill.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(8, 8, 48, 48)),
                new RadialGradientBrush(new Vector2(32, 32), 24, 24, stops)));
            root.Children.Add(fill);
            return root;
        }

        [Fact]
        public void RadialGradient_FadesFromCentreAndClampsBeyondTheRadius()
        {
            var img = new Image(Render(RadialScene(), W, H), W, H);

            int centre = img[32, 32].R;
            int up = img[32, 20].R;      // half radius up
            int left = img[20, 32].R;    // half radius left, for symmetry
            int edge = img[32, 8].R;     // one radius up: the last stop
            int corner = img[8, 8].R;    // beyond the radius: clamped

            int midGrey = GammaMode ? 127 : 60;
            Assert.True(centre > 230, $"centre should be the first stop (white), was {centre}");
            Assert.True(Math.Abs(up - midGrey) <= 28,
                $"half radius should be mid grey (~{midGrey} in {(GammaMode ? "gamma" : "linear")} mode), was {up}");
            Assert.True(Math.Abs(up - left) <= 8,
                $"equidistant points must match (radial symmetry): up={up}, left={left}");
            Assert.True(edge < 40, $"one radius out should be the last stop (black), was {edge}");
            Assert.True(corner < 40, $"beyond the radius the last stop must be held, was {corner}");
        }

        [Fact]
        public void RadialGradient_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(RadialScene(), W, H);

        // ---- spread methods -----------------------------------------------------------------

        private const int SW = 64, SH = 16;

        /// <summary>A black-to-white ramp with a SHORT axis (0..16) across a wide rect, so most of the fill is "beyond" it.</summary>
        private static SceneVisual SpreadScene(GradientSpreadMethod spread)
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            var stops = new[]
            {
                new GradientStop(0f, RgbaColor.FromBytes(0, 0, 0, 255)),
                new GradientStop(1f, RgbaColor.FromBytes(255, 255, 255, 255)),
            };
            fill.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(0, 0, 64, 16)),
                new LinearGradientBrush(new Vector2(0, 8), new Vector2(16, 8), stops, spread)));
            root.Children.Add(fill);
            return root;
        }

        private int SpreadLum(GradientSpreadMethod spread, int x)
            => new Image(Render(SpreadScene(spread), SW, SH), SW, SH)[x, 8].R;

        // The theory parameter is a bool rather than the enum: GradientSpreadMethod is internal to
        // the renderer (reachable here only through InternalsVisibleTo), and a PUBLIC xunit test
        // method cannot take a less-accessible parameter type.
        [Theory]
        [InlineData(false)]   // Repeat
        [InlineData(true)]    // Reflect
        public void Spread_FirstRampMidpointIsGrey(bool reflect)
        {
            GradientSpreadMethod spread = reflect ? GradientSpreadMethod.Reflect : GradientSpreadMethod.Repeat;
            (int lo, int hi) = GammaMode ? (100, 155) : (40, 90);
            int v = SpreadLum(spread, 8);
            Assert.True(v >= lo && v <= hi,
                $"{spread}: first ramp midpoint should be grey ({lo}..{hi} in {(GammaMode ? "gamma" : "linear")} mode), was {v}");
        }

        /// <summary>
        /// x=17 (just past the first ramp) and x=31 (just before the next boundary) are the
        /// discriminating samples: Repeat restarts dark then runs light, Reflect does the opposite.
        /// Any sample inside the first ramp would look identical for both.
        /// </summary>
        [Fact]
        public void Repeat_RestartsTheRamp()
        {
            int justPast = SpreadLum(GradientSpreadMethod.Repeat, 17);
            int beforeNext = SpreadLum(GradientSpreadMethod.Repeat, 31);
            Assert.True(justPast < 70, $"Repeat should restart dark just past the axis, was {justPast}");
            Assert.True(beforeNext > 190, $"Repeat should reach light before the next tile, was {beforeNext}");
        }

        [Fact]
        public void Reflect_MirrorsTheRamp()
        {
            int justPast = SpreadLum(GradientSpreadMethod.Reflect, 17);
            int beforeNext = SpreadLum(GradientSpreadMethod.Reflect, 31);
            Assert.True(justPast > 190, $"Reflect should mirror to light just past the axis, was {justPast}");
            Assert.True(beforeNext < 70, $"Reflect should mirror to dark before the next tile, was {beforeNext}");
        }

        [Theory]
        [InlineData(false)]   // Repeat
        [InlineData(true)]    // Reflect
        public void Spread_SurvivesProtocolRoundTrip(bool reflect)
            => AssertProtocolRoundTripIsIdentical(
                SpreadScene(reflect ? GradientSpreadMethod.Reflect : GradientSpreadMethod.Repeat), SW, SH);
    }
}
