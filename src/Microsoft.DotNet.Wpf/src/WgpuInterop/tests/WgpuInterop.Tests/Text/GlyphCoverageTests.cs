// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.TextCoverageTest and WgpuInterop.GammaTest.
//
// Between them these cover the two halves of the text-gamma contract, and they must BOTH stay: the
// LUT has to fire on the linear-blending path and must NOT fire on the gamma-space one. Only one
// side was covered before, which is how a double-applied correction survived for the life of the
// renderer (Documentation/linux-head.md).
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class GlyphCoverageTests : RendererTestBase
    {
        public GlyphCoverageTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 320, H = 64;
        private const float EmSize = 32f;
        private const float OriginX = 8f, OriginY = 44f;
        private const string Text = "Hello World";

        // The rasterizer samples 4x vertically with exact horizontal spans, so a few percent of
        // disagreement with the analytic area is the rasterizer, not a bug. The defect this catches
        // was +23% at 13px and +13.4% at this size; the fixed renderer measures within ~2%.
        private const double Tolerance = 0.10;

        /// <summary>
        /// Anti-aliasing conserves area, so rendered ink must equal the area the outlines enclose.
        /// Asserted on the DEFAULT DISPLAY PATH (gamma-space compositing into a plain-UNORM target),
        /// which is what every real window uses and what nothing covered before.
        /// </summary>
        [Theory]
        [InlineData(false)]   // light: black glyphs on white
        [InlineData(true)]    // dark:  white glyphs on black
        public void RenderedInk_EqualsOutlineArea(bool dark)
        {
            TrueTypeFont font = TestFonts.Load();
            BuildRun(font, out ushort[] indices, out float[] advances, out float advScale);

            double expected = ExpectedInkArea(font, indices, advScale);
            Assert.True(expected > 0, "font exposed no outlines to measure");

            var engine = new MilcoreEngine { FontResolver = _ => font };
            engine.CreateOrAddRef(1, MilResourceTypeId.Visual);
            float c = dark ? 1f : 0f;
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, c, c, c, 1));
            engine.CreateOrAddRef(20, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(20, 0, OriginX, OriginY, EmSize, indices, advances));
            engine.EndCommand();

            byte[] px = RenderContent(engine, MilCmd.DrawGlyphRunRecord(3, 20), W, H,
                dark ? RgbaColor.FromBytes(0, 0, 0, 255) : RgbaColor.FromBytes(255, 255, 255, 255));
            var img = new Image(px, W, H);

            // Coverage reads back directly in either polarity.
            double measured = 0;
            int inkPx = 0, edgePx = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double cov = dark ? img[x, y].R / 255.0 : (255.0 - img[x, y].R) / 255.0;
                    if (cov <= 0.002) continue;
                    measured += cov;
                    inkPx++;
                    if (cov < 0.98) edgePx++;
                }

            double ratio = measured / expected;
            Assert.True(inkPx > 200, $"the run did not render (only {inkPx} ink pixels)");
            Assert.True(edgePx > 40, $"edges are not anti-aliased (only {edgePx} partial pixels)");
            Assert.True(Math.Abs(ratio - 1.0) <= Tolerance,
                $"coverage not conserved: expected={expected:0.0} measured={measured:0.0} ratio={ratio:0.000}. " +
                "If ratio > 1, coverage is being boosted -- check the text-gamma LUT gate in " +
                "EmitCoverageMask/EmitMask: it must fire ONLY where the pass blends linearly " +
                "(_srgbOutput), never in gamma-space mode, or the correction is counted twice.");
        }

        /// <summary>
        /// The other half of the contract: on the LINEAR path the LUT must still fire, so text there
        /// is measurably heavier than an uncorrected linear blend, without flooding the counters.
        /// </summary>
        [Fact]
        public void TextGamma_StillAppliesOnTheLinearPath()
        {
            TrueTypeFont font = TestFonts.Load();
            BuildRun(font, out ushort[] indices, out float[] advances, out _);

            var engine = new MilcoreEngine { FontResolver = _ => font };
            engine.CreateOrAddRef(1, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));
            engine.CreateOrAddRef(20, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(20, 0, OriginX, OriginY, EmSize, indices, advances));
            engine.EndCommand();
            SceneVisual root = Realize(engine, MilCmd.DrawGlyphRunRecord(3, 20));

            var bg = RgbaColor.FromBytes(255, 255, 255, 255);
            byte[] linear = NewRenderer().RenderToRgba(root, W, H, bg, srgbOutput: false);
            byte[] srgb = NewRenderer().RenderToRgba(root, W, H, bg, srgbOutput: true);

            // Put both in display space so they differ only by the coverage gamma.
            double linearDark = 0, gammaDark = 0;
            int linInk = 0, srgbInk = 0, nearWhite = 0;
            for (int y = 6; y < H - 8; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    double lin = SrgbEncode(linear[i] / 255.0) * 255.0;
                    double gam = srgb[i];
                    linearDark += 255 - lin;
                    gammaDark += 255 - gam;
                    if (lin < 200) linInk++;
                    if (gam < 200) srgbInk++;
                    if (gam > 240) nearWhite++;
                }

            Assert.True(linInk > 80 && srgbInk > 80, $"both renders must produce ink (linear={linInk}, srgb={srgbInk})");
            Assert.True(gammaDark > linearDark * 1.05,
                $"gamma must make text heavier on the linear path (gamma={gammaDark:0}, linear={linearDark:0})");
            Assert.True(nearWhite > srgbInk, "gamma must not flood the glyph counters solid");
        }

        private static double SrgbEncode(double c)
            => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;

        private static void BuildRun(TrueTypeFont font, out ushort[] indices, out float[] advances, out float advScale)
        {
            advScale = EmSize / font.PixelsPerEm;
            indices = new ushort[Text.Length];
            advances = new float[Text.Length];
            for (int i = 0; i < Text.Length; i++)
            {
                int gid = font.GlyphIndex(Text[i]);
                indices[i] = (ushort)gid;
                advances[i] = font.Advance(gid) * advScale;
            }
        }

        /// <summary>
        /// Area enclosed by the run's outlines, by the shoelace formula over the flattened contours.
        /// Computed from the FONT, never from the renderer, so it cannot agree with a broken
        /// rasterizer by construction. Signed areas are summed PER GLYPH and the magnitude taken once
        /// -- per contour would add an 'e' or 'o' counter instead of subtracting it, over-stating the
        /// expected ink by ~60% on ordinary text.
        /// </summary>
        private static double ExpectedInkArea(TrueTypeFont font, ushort[] indices, float advScale)
        {
            double total = 0;
            foreach (ushort gid in indices)
            {
                if (!font.TryGetGlyphOutline(gid, out List<PathFigure> figures)) continue;
                double glyph = 0;
                foreach (PathFigure f in figures) glyph += SignedArea(f);
                total += Math.Abs(glyph);
            }
            return total * advScale * advScale;
        }

        private const int Steps = 24;

        private static double SignedArea(PathFigure f)
        {
            var pts = new List<Vector2> { f.Start };
            Vector2 cur = f.Start;
            foreach (PathSegment seg in f.Segments)
            {
                switch (seg)
                {
                    case LineSegment l: pts.Add(l.Point); cur = l.Point; break;
                    case QuadraticBezierSegment q:
                        for (int i = 1; i <= Steps; i++) pts.Add(Quad(cur, q.Control, q.Point, i / (float)Steps));
                        cur = q.Point; break;
                    case CubicBezierSegment c:
                        for (int i = 1; i <= Steps; i++) pts.Add(Cubic(cur, c.Control1, c.Control2, c.Point, i / (float)Steps));
                        cur = c.Point; break;
                }
            }
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q2 = pts[(i + 1) % pts.Count];
                a += (double)p.X * q2.Y - (double)q2.X * p.Y;
            }
            return a * 0.5;
        }

        private static Vector2 Quad(Vector2 p0, Vector2 c, Vector2 p1, float t)
        { float u = 1f - t; return u * u * p0 + 2f * u * t * c + t * t * p1; }

        private static Vector2 Cubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
        { float u = 1f - t; return u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p1; }
    }
}
