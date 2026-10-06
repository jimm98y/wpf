// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.GuidelineTest.
//
// WPF controls emit a GuidelineSet so a 1px border lands ON a device pixel boundary. This renderer
// used to drop MILCMD_VISUAL_SETGUIDELINECOLLECTION entirely -- 441 of them arrived in a 20-second
// WPF Gallery session -- so every thin line at a fractional position painted as two half-covered
// rows.
//
// The measure is CRISPNESS, not "something changed": a snapped 1px line produces ONE fully-covered
// row, an unsnapped one splits across two partial rows.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class GuidelineSnappingTests : RendererTestBase
    {
        public GuidelineSnappingTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 40, H = 24;

        /// <summary>
        /// No partially covered rows AT ALL. A single half-covered row IS the soft edge this is
        /// about -- allowing one masked the fractional-DPI case entirely.
        /// </summary>
        private static bool Crisp(int peak, int partialRows) => peak >= 250 && partialRows == 0;

        /// <summary>Peak coverage over the bar's column, and how many rows are partially covered.</summary>
        private (int Peak, int PartialRows) RenderBar(float fracY, bool snap)
        {
            const float barY = 8f;
            var child = new SceneVisual { Offset = new Vector2(0, fracY) };
            // What a Border emits: the two edges of the 1px line, in LOCAL space.
            if (snap) child.GuidelinesY = new[] { barY, barY + 1f };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(6, barY, 28, 1)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            var root = new SceneVisual();
            root.Children.Add(child);

            byte[] px = Render(root, W, H);
            int peak = 0, partial = 0;
            for (int y = 0; y < H; y++)
            {
                int cov = 255 - px[(y * W + 20) * 4];   // black on white, mid-bar column
                peak = Math.Max(peak, cov);
                if (cov > 12 && cov < 243) partial++;
            }
            return (peak, partial);
        }

        /// <summary>The same 1px LOGICAL bar under a DPI scale, so its device height is fractional.</summary>
        private (int Peak, int PartialRows) RenderScaledBar(float scale, bool snap)
        {
            // 8.4 dip, not 8: at 1.25x, 8*1.25 is exactly 10.0 and the bar is accidentally
            // aligned, which hid the effect completely. Real layout rarely lands on such values.
            const float barY = 8.4f;
            int w = (int)(W * scale) + 4, h = (int)(H * scale) + 4;

            var child = new SceneVisual { Transform = Matrix3x2.CreateScale(scale) };
            if (snap) child.GuidelinesY = new[] { barY, barY + 1f };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(6, barY, 28, 1)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            var root = new SceneVisual();
            root.Children.Add(child);

            byte[] px = Render(root, w, h);
            int peak = 0, partial = 0, col = (int)(20 * scale);
            for (int y = 0; y < h; y++)
            {
                int cov = 255 - px[(y * w + col) * 4];
                peak = Math.Max(peak, cov);
                if (cov > 12 && cov < 243) partial++;
            }
            return (peak, partial);
        }

        /// <summary>
        /// WITH guidelines the line must be crisp at EVERY sub-pixel offset. Without them it is a
        /// coin flip -- the renderer's own half-pixel phase snapping happens to save some fractions
        /// -- so that side is measured in the self-check below rather than asserted here.
        /// </summary>
        [Theory]
        [InlineData(0.0f)] [InlineData(0.1f)] [InlineData(0.25f)] [InlineData(0.3f)]
        [InlineData(0.5f)] [InlineData(0.6f)] [InlineData(0.75f)] [InlineData(0.9f)]
        public void Guidelines_SnapAOnePixelBar_AtEveryOffset(float frac)
        {
            (int peak, int rows) = RenderBar(frac, snap: true);
            Assert.True(Crisp(peak, rows),
                $"guidelines did not snap offset +{frac:0.00}: peak={peak}, partially-covered rows={rows}");
        }

        /// <summary>
        /// The everyday Windows configuration, and the case guidelines exist for: at 1.25x/1.5x a 1px
        /// logical border is 1.25/1.5 DEVICE pixels, so both edges land mid-pixel and it straddles no
        /// matter what the sub-pixel phase is.
        /// </summary>
        [Theory]
        [InlineData(1.25f)] [InlineData(1.5f)] [InlineData(1.75f)]
        public void Guidelines_SnapUnderFractionalDpi(float scale)
        {
            (int peak, int rows) = RenderScaledBar(scale, snap: true);
            Assert.True(Crisp(peak, rows),
                $"guidelines did not snap at {scale}x: peak={peak}, partially-covered rows={rows}");
        }

        /// <summary>
        /// SELF-CHECK, and the most important test here: if the UNSNAPPED path were already crisp at
        /// every offset, everything above would be vacuous -- it would pass with guidelines
        /// unimplemented.
        ///
        /// Exempt whenever the CPU rasterizer is in use: it bakes each mask at an integer local
        /// origin, so it is already pixel-aligned for axis-aligned rects and guidelines are a no-op
        /// there by construction.
        ///
        /// The gate reads the renderer's ACTUAL mode, not the WPF_WEBGPU_CPU_RASTER variable. The
        /// original standalone test checked only the variable and would therefore have failed on this
        /// very machine: the renderer also falls back to CPU rasterization on a virgl adapter (a
        /// driver bug in virgl's GLSL re-translation, see WgpuSceneRenderer near IsVirgl), so the
        /// variable is unset while the CPU path is what runs. Gating on the observable state rather
        /// than on the thing that usually causes it is the difference between a self-check that works
        /// everywhere and one that fails on any box with a fallback.
        /// </summary>
        [Fact]
        public void WithoutGuidelines_SomeOffsetsAreSoft_SoTheComparisonIsNotVacuous()
        {
            Assert.SkipWhen(!WgpuSceneRenderer.s_gpuRaster,
                "the CPU rasterizer is active (explicitly, or as the virgl fallback), and it is " +
                "already pixel-aligned for axis-aligned rects, so guidelines are inert");

            int soft = 0, total = 0;
            foreach (float frac in new[] { 0.0f, 0.1f, 0.25f, 0.3f, 0.5f, 0.6f, 0.75f, 0.9f })
            {
                (int peak, int rows) = RenderBar(frac, snap: false);
                total++;
                if (!Crisp(peak, rows)) soft++;
            }

            Assert.True(soft > 0,
                $"the unsnapped path was crisp at all {total} offsets, so the guideline tests prove nothing");
        }

        // ---- Stroked borders -------------------------------------------------------------------
        //
        // Everything above draws the border as a FILLED rect. A Border with a uniform thickness --
        // which is every themed Button, TextBox and ComboBox -- does not: it emits
        // DrawRectangle(brush, pen, rect), a STROKE centred on the rectangle's edge. Snapping the
        // rectangle is not enough for those, because a 1px pen on an edge that sits exactly on a
        // pixel boundary still puts half of itself on either side; the stroke's outer boundary is
        // what has to land on the grid. Strokes used to be skipped by the snapper entirely, so
        // every themed control had soft edges however much its template asked for snapping.

        /// <summary>
        /// A 1px stroked box, built exactly the way Border does it: the rectangle inset by half the
        /// thickness, and the guidelines on the element's own edges (what SnapsToDevicePixels emits).
        /// Scans a column that crosses the top and bottom edges but no fill.
        /// </summary>
        private (int Peak, int PartialRows) RenderStrokedBox(float fracY, bool snap, float scale = 1f)
        {
            const float boxY = 6f, boxH = 10f, thickness = 1f;
            int w = (int)(W * scale) + 4, h = (int)(H * scale) + 4;

            var child = new SceneVisual
            {
                Offset = new Vector2(0, fracY * scale),
                Transform = Matrix3x2.CreateScale(scale),
            };
            if (snap) child.GuidelinesY = new[] { boxY, boxY + boxH };

            child.Content.Add(new GeometryDrawing(
                new RectangleGeometry(new Rect(6, boxY + thickness / 2f, 28, boxH - thickness)),
                fill: null,
                stroke: new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)),
                strokeStyle: new StrokeStyle(thickness)));

            var root = new SceneVisual();
            root.Children.Add(child);

            byte[] px = Render(root, w, h);
            int peak = 0, partial = 0, col = (int)(20 * scale);
            for (int y = 0; y < h; y++)
            {
                int cov = 255 - px[(y * w + col) * 4];
                peak = Math.Max(peak, cov);
                if (cov > 12 && cov < 243) partial++;
            }
            return (peak, partial);
        }

        [Theory]
        [InlineData(0.0f)] [InlineData(0.1f)] [InlineData(0.25f)] [InlineData(0.3f)]
        [InlineData(0.5f)] [InlineData(0.6f)] [InlineData(0.75f)] [InlineData(0.9f)]
        public void Guidelines_SnapAStrokedBorder_AtEveryOffset(float frac)
        {
            (int peak, int rows) = RenderStrokedBox(frac, snap: true);
            Assert.True(Crisp(peak, rows),
                $"guidelines did not snap the stroked border at +{frac:0.00}: peak={peak}, partial rows={rows}");
        }

        /// <summary>
        /// Under a fractional scale a 1px logical stroke is a fractional number of device pixels, so
        /// snapping has to change the pen's WIDTH as well as its position -- moving it alone leaves
        /// the far side mid-pixel.
        /// </summary>
        [Theory]
        [InlineData(1.25f)] [InlineData(1.5f)] [InlineData(1.75f)]
        public void Guidelines_SnapAStrokedBorder_UnderFractionalDpi(float scale)
        {
            (int peak, int rows) = RenderStrokedBox(0.4f, snap: true, scale);
            Assert.True(Crisp(peak, rows),
                $"guidelines did not snap the stroked border at {scale}x: peak={peak}, partial rows={rows}");
        }

        /// <summary>The same self-check: without guidelines the stroked path must be soft somewhere,
        /// or the two tests above would pass with stroke snapping removed again.</summary>
        [Fact]
        public void WithoutGuidelines_SomeStrokedOffsetsAreSoft_SoTheComparisonIsNotVacuous()
        {
            int soft = 0, total = 0;
            foreach (float frac in new[] { 0.0f, 0.1f, 0.25f, 0.3f, 0.5f, 0.6f, 0.75f, 0.9f })
            {
                (int peak, int rows) = RenderStrokedBox(frac, snap: false);
                total++;
                if (!Crisp(peak, rows)) soft++;
            }

            Assert.True(soft > 0,
                $"the unsnapped stroked path was crisp at all {total} offsets, so the tests above prove nothing");
        }
    }
}
