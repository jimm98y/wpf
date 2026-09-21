// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.EffectTest and BlurKernelTest.
//
// Effects render a visual's subtree into an offscreen layer and post-process it, so these also cover
// the layer machinery, not just the filter maths.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public sealed class EffectTests : RendererTestBase
    {
        public EffectTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 96;

        private static SceneVisual Wrap(SceneVisual effected, Microsoft.Wpf.Interop.WebGpu.Composition.Geometry geometry)
        {
            var root = new SceneVisual();
            effected.Content.Add(new GeometryFill(geometry, RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(effected);
            return root;
        }

        private static SceneVisual BlurScene()
            => Wrap(new SceneVisual { Effect = new BlurEffect(5) },
                    new RectangleGeometry(new Rect(20, 20, 40, 40)));   // (20,20)-(60,60)

        /// <summary>
        /// The blur matches WPF (BlurLayer: sd = radius/3, kernel half-extent == radius), so a
        /// radius-5 blur carries ink at most 5px past the last inked column (x=59), i.e. out to x=64.
        /// The "far" probe at x=85 sits outside the kernel entirely and could never show ink at this
        /// radius -- it is the control that catches a blur which smears the whole layer.
        /// </summary>
        [Fact]
        public void BlurEffect_SoftensTheEdgeAndSpreadsInk()
        {
            var img = new Image(Render(BlurScene(), W, H), W, H);

            int centre = img[40, 40].R, edge = img[60, 40].R, outside = img[62, 40].R, far = img[85, 40].R;

            Assert.True(centre < 40, $"blurred interior should stay solid, was {centre}");
            Assert.True(edge >= 50 && edge <= 205, $"the original edge should become a soft grey, was {edge}");
            Assert.True(outside < 240, $"ink should spread beyond the original bounds, x=62 was {outside}");
            Assert.True(far > 245, $"pixels outside the kernel must stay clear, x=85 was {far}");
        }

        [Fact]
        public void BlurEffect_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(BlurScene(), W, H);

        private static SceneVisual ShadowScene()
            => Wrap(new SceneVisual
            {
                Effect = new DropShadowEffect(new RgbaColor(0f, 0f, 0f, 0.7f), 2, 12, 12),
            }, new RectangleGeometry(new Rect(20, 20, 24, 24)));        // (20,20)-(44,44)

        /// <summary>
        /// A blurred, tinted silhouette drawn OFFSET BENEATH the sharp content. The up-left probe is
        /// the discriminator: a shadow that ignored its offset (or drew on top) would fail it while
        /// still producing grey down-right.
        /// </summary>
        [Fact]
        public void DropShadowEffect_DrawsOffsetBeneathTheContent()
        {
            var img = new Image(Render(ShadowScene(), W, H), W, H);

            int rect = img[30, 30].R, shadow = img[52, 52].R, upLeft = img[12, 12].R;

            Assert.True(rect < 40, $"the content should stay solid on top, was {rect}");
            Assert.True(shadow >= 40 && shadow <= 175, $"a grey shadow should appear down-right, was {shadow}");
            Assert.True(upLeft > 245, $"there should be no shadow up-left of an offset shadow, was {upLeft}");
        }

        [Fact]
        public void DropShadowEffect_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(ShadowScene(), W, H);
    }

    /// <summary>
    /// BlurEffect.KernelType (Gaussian vs Box).
    ///
    /// MILCMD_BLUREFFECT carries KernelType at offset 20, but the decoder used to stop after
    /// Radius@8, so a Box blur silently rendered as a Gaussian one. Both halves below are needed:
    /// that Box DIFFERS from Gaussian (otherwise the plumbing is dead and nothing notices), and that
    /// each matches an independent CPU convolution (otherwise both could be wrong the same way).
    /// </summary>
    public sealed class BlurKernelTests : RendererTestBase
    {
        public BlurKernelTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 96;
        private const double Radius = 12;

        private static SceneVisual Scene(BlurEffect? effect)
        {
            var root = new SceneVisual();
            var child = new SceneVisual { Effect = effect };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(24, 24, 48, 48)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(child);
            return root;
        }

        private byte[] RenderBlur(BlurEffect? effect) => Render(Scene(effect), W, H);

        [Fact]
        public void BoxKernel_DiffersFromGaussian()
        {
            byte[] gauss = RenderBlur(new BlurEffect(Radius, BlurKernelType.Gaussian));
            byte[] box = RenderBlur(new BlurEffect(Radius, BlurKernelType.Box));

            int diffPx = 0, maxDelta = 0;
            for (int i = 0; i < W * H; i++)
            {
                int d = Math.Abs(gauss[i * 4] - box[i * 4]);
                if (d > 0) diffPx++;
                if (d > maxDelta) maxDelta = d;
            }

            Assert.True(diffPx > 200 && maxDelta > 20,
                $"Box must differ from Gaussian, got {diffPx} differing pixels with max delta {maxDelta}. " +
                "If they are identical the KernelType field is being ignored.");
        }

        /// <summary>
        /// Both kernels must match a CPU convolution of the UNBLURRED image. The renderer blurs
        /// premultiplied RGBA separably in two passes and the reference does the same on the CPU, so
        /// agreement is a genuine cross-check rather than a restatement of the shader.
        /// </summary>
        [Theory]
        [InlineData(true)]    // Gaussian
        [InlineData(false)]   // Box
        public void Kernel_MatchesACpuReferenceConvolution(bool gaussian)
        {
            byte[] actual = RenderBlur(new BlurEffect(Radius, gaussian ? BlurKernelType.Gaussian : BlurKernelType.Box));
            byte[] sharp = RenderBlur(null);

            int taps = Math.Clamp((int)Math.Ceiling(Radius), 1, 48);
            double sigma = Math.Max(0.5, Radius / 3.0);
            var weights = new double[2 * taps + 1];
            double wsum = 0;
            for (int i = -taps; i <= taps; i++)
            {
                double w = gaussian ? Math.Exp(-(i * (double)i) / (2 * sigma * sigma)) : 1.0;
                weights[i + taps] = w; wsum += w;
            }
            for (int i = 0; i < weights.Length; i++) weights[i] /= wsum;

            byte[] reference = Convolve(Convolve(sharp, weights, taps, horizontal: true), weights, taps, horizontal: false);

            // Interior only: the renderer blurs a region-sized layer whose edges clamp differently
            // from this reference, and that difference is not what is under test.
            int worst = 0; long sum = 0; int n = 0;
            for (int y = taps + 2; y < H - taps - 2; y++)
                for (int x = taps + 2; x < W - taps - 2; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int d = Math.Abs(actual[(y * W + x) * 4 + c] - reference[(y * W + x) * 4 + c]);
                        worst = Math.Max(worst, d); sum += d; n++;
                    }
            double mean = n > 0 ? sum / (double)n : 0;

            Assert.True(worst <= 12 && mean <= 3.0,
                $"{(gaussian ? "Gaussian" : "Box")} does not match the CPU reference: max delta {worst}, mean {mean:F2}");
        }

        private static byte[] Convolve(byte[] src, double[] weights, int taps, bool horizontal)
        {
            var dst = new byte[src.Length];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    for (int i = -taps; i <= taps; i++)
                    {
                        int sx = horizontal ? Math.Clamp(x + i, 0, W - 1) : x;
                        int sy = horizontal ? y : Math.Clamp(y + i, 0, H - 1);
                        int o = (sy * W + sx) * 4;
                        double w = weights[i + taps];
                        r += src[o] * w; g += src[o + 1] * w; b += src[o + 2] * w; a += src[o + 3] * w;
                    }
                    int d = (y * W + x) * 4;
                    dst[d] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                    dst[d + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                    dst[d + 2] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
                    dst[d + 3] = (byte)Math.Clamp((int)Math.Round(a), 0, 255);
                }
            return dst;
        }

        /// <summary>The kernel must survive encode/decode -- where a newly added field is easiest to forget.</summary>
        [Theory]
        [InlineData(true)]    // Gaussian
        [InlineData(false)]   // Box
        public void ProtocolRoundTrip_PreservesTheKernelType(bool gaussian)
        {
            BlurKernelType kernel = gaussian ? BlurKernelType.Gaussian : BlurKernelType.Box;
            SceneVisual root = Scene(new BlurEffect(Radius, kernel));

            var engine = new Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.CompositionEngine();
            engine.ProcessBatch(Microsoft.Wpf.Interop.WebGpu.Composition.Protocol.CompositionChannel.EncodeScene(root));

            var rebuilt = engine.Root?.Children[0].Effect as BlurEffect;
            Assert.True(rebuilt is not null, "the effect did not survive the round-trip at all");
            Assert.Equal(kernel, rebuilt!.Kernel);
            Assert.Equal(Radius, rebuilt.Radius);
        }
    }
}
