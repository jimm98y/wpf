// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.VisualClipTest and RenderOptionsTest.
//
// Both drive MILCMD directly rather than building a scene in-process, because the thing under test
// IS the decode. A scene assembled from objects would bypass the command that carries the state, so
// these cannot use the scene-graph helpers the geometry tests use.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    /// <summary>
    /// WPF's UIElement.Clip / ClipToBounds emit MILCMD_VISUAL_SETCLIP referencing a geometry
    /// resource, masking the visual's whole subtree. A RECTANGLE clip takes the fast axis-aligned
    /// scissor; any other geometry becomes a coverage mask. Both routes are covered here because
    /// they are different code, and a test of only the rectangle would miss the mask path entirely.
    /// </summary>
    public sealed class VisualClipTests : RendererTestBase
    {
        public VisualClipTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 48, H = 48;

        /// <summary>A red rect (0,0,40,40) clipped by the geometry at <paramref name="hClip"/>.</summary>
        private byte[] RenderClipped(byte[] clipGeometry, uint hClip)
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 1, 0, 0, 1));
            engine.SubmitCommand(clipGeometry);

            SceneVisual root = Realize(engine, MilCmd.DrawRectangleRecord(3, 0, 0, 0, 40, 40),
                                       hVisual: 2, hContent: 5);
            // The clip is applied after the content is realized, exactly as WPF sends it.
            engine.SubmitCommand(MilCmd.VisualSetClip(2, hClip));
            engine.Realize();
            root = engine.VisualByHandle(2)!;
            return NewRenderer().RenderToRgba(root, W, H, White);
        }

        [Theory]
        [InlineData(14, 14, 255, 0, 0, "inside the rect clip is painted")]
        [InlineData(4, 4, 255, 255, 255, "above-left of the rect clip is masked")]
        [InlineData(30, 30, 255, 255, 255, "below-right of the rect clip is masked")]
        public void RectangleClip_UsesTheScissor(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = RenderClipped(MilCmd.RectangleGeometry(4, 8, 8, 12, 12), 4);
            new Image(px, W, H).AssertPixel(x, y, r, g, b, tol: 6, what);
        }

        [Theory]
        [InlineData(16, 16, 255, 0, 0, "ellipse clip centre is painted")]
        [InlineData(2, 2, 255, 255, 255, "the corner is outside the ellipse")]
        [InlineData(27, 16, 255, 255, 255, "past the ellipse radius is masked")]
        public void EllipseClip_UsesAGeometryMask(int x, int y, int r, int g, int b, string what)
        {
            byte[] px = RenderClipped(MilCmd.EllipseGeometry(6, 8, 8, 16, 16), 6);
            new Image(px, W, H).AssertPixel(x, y, r, g, b, tol: 6, what);
        }
    }

    /// <summary>
    /// MILCMD_VISUAL_SETRENDEROPTIONS (0x21) was decoded by nobody, so RenderOptions.SetEdgeMode and
    /// SetBitmapScalingMode were silently ignored. Both are deliberate opt-OUTS of smoothing -- pixel
    /// art, QR codes, crisp diagrams -- so ignoring them produces exactly the blurring the app asked
    /// to avoid.
    ///
    /// Asserted on PIXELS, not on decoded flags: a decode that stores a field nothing reads would
    /// satisfy any structural check while changing no output.
    /// </summary>
    public sealed class RenderOptionsTests : RendererTestBase
    {
        public RenderOptionsTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        /// <summary>Pixels along a rotated edge that are neither background nor solid fill.</summary>
        private int EdgePartials(bool aliased)
        {
            var root = new SceneVisual();
            var child = new SceneVisual
            {
                Transform = Matrix3x2.CreateRotation(0.30f, new Vector2(32, 32)),
                AliasedEdges = aliased,
            };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(12, 12, 40, 40)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(child);

            byte[] px = Render(root, W, H);
            int partial = 0;
            for (int i = 0; i < W * H; i++)
            {
                int v = px[i * 4];
                if (v > 20 && v < 235) partial++;
            }
            return partial;
        }

        [Fact]
        public void EdgeMode_Aliased_RemovesEveryPartialPixel()
        {
            int antiAliased = EdgePartials(aliased: false);
            int aliased = EdgePartials(aliased: true);

            Assert.True(antiAliased > 20,
                $"the default rotated edge should be anti-aliased, only {antiAliased} partial pixels");
            Assert.True(aliased == 0,
                $"EdgeMode.Aliased must remove every partial pixel, {aliased} remain");
        }

        /// <summary>Pixels that are none of the four source colours, i.e. produced by filtering.</summary>
        private int ImageBlendPixels(bool nearest)
        {
            (byte, byte, byte)[] cols = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) };
            var rgba = new byte[2 * 2 * 4];
            for (int i = 0; i < 4; i++)
            {
                rgba[i * 4] = cols[i].Item1; rgba[i * 4 + 1] = cols[i].Item2;
                rgba[i * 4 + 2] = cols[i].Item3; rgba[i * 4 + 3] = 255;
            }

            var root = new SceneVisual();
            var child = new SceneVisual { NearestBitmapScaling = nearest };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(4, 4, 56, 56)),
                new ImageBrush(rgba, 2, 2)));
            root.Children.Add(child);

            byte[] px = Render(root, W, H);
            int blended = 0;
            for (int y = 8; y < H - 8; y++)
                for (int x = 8; x < W - 8; x++)
                {
                    int i = (y * W + x) * 4;
                    bool exact = false;
                    foreach ((byte r, byte g, byte b) in cols)
                        if (Math.Abs(px[i] - r) < 12 && Math.Abs(px[i + 1] - g) < 12 && Math.Abs(px[i + 2] - b) < 12)
                            exact = true;
                    if (!exact) blended++;
                }
            return blended;
        }

        [Fact]
        public void BitmapScalingMode_NearestNeighbor_ProducesOnlySourceColours()
        {
            int linear = ImageBlendPixels(nearest: false);
            int nearest = ImageBlendPixels(nearest: true);

            Assert.True(linear > 20,
                $"the default magnified image should be filtered, only {linear} blended pixels");
            Assert.True(nearest == 0,
                $"NearestNeighbor must produce only source colours, {nearest} pixels were blended");
        }

        /// <summary>
        /// Decode the real command with EVERY field non-zero and distinct.
        ///
        /// MilRenderOptions is seven u32s and the two fields the renderer honours sit either side of
        /// ones it skips. With the others left zero, an off-by-one in the struct walk still passes --
        /// it reads CompositingMode as EdgeMode and the two alias. Populating everything makes a
        /// misread land on the wrong value instead of coincidentally on the right one.
        /// </summary>
        [Fact]
        public void RenderOptions_FieldsAreReadFromTheRightOffsets()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(2, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SetRenderOptions(2,
                flags: 0x1 | 0x2 | 0x8 | 0x10 | 0x20,  // BitmapScalingMode|EdgeMode|ClearType|TextRendering|TextHinting
                edgeMode: 1,                           // Aliased
                compositingMode: 5,                    // SourceUnder: read past, must not shift the rest
                bitmapScalingMode: 3,                  // NearestNeighbor
                clearTypeHint: 1, textRenderingMode: 2, textHintingMode: 1));
            e.Realize();

            SceneVisual? v = e.VisualByHandle(2);
            Assert.True(v is not null, "no visual after SETRENDEROPTIONS");
            Assert.True(v!.AliasedEdges, "EdgeMode was not read correctly with every other field populated");
            Assert.True(v.NearestBitmapScaling,
                "BitmapScalingMode was not read from the right offset past CompositingMode");
        }
    }
}
