// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The ASSERTIONS from WgpuInterop.ScaleProbe. The probe itself stays as a tool: it prints trend
// tables under --cost / --segscale / --strokegeom that a test runner has nowhere to put, and those
// numbers are how the adaptive-flattening work was justified in the first place.
//
// Property under test: rendered edge accuracy must not fall apart as the world transform scales
// geometry up. With the old fixed 24-step subdivision it did, badly -- measured rms alpha error from
// 1x to 24x zoom on the CPU raster path:
//
//     fill          0.0151 -> 0.1217   (8.1x)
//     stroke round  0.0135 -> 0.2329  (17.2x), max error 0.998, i.e. the edge is essentially wrong
//     stroke miter  0.0108 -> 0.1412  (13.0x)
//
// The bounds are ABSOLUTE rather than ratios. A ratio passes trivially when the 1x case is also bad,
// which is the failure mode a "no worse than before" check invites.
//
// RECORDED CORRECTION, because it was reported the other way for a while: the ~4x stroke drift these
// cases used to show was NOT a defect in stroke-to-fill. The harness was feeding the stroker a FIXED
// 4-arc kappa circle, which carries ~2.7e-4*r of its own error -- 0.13px at 24x, five times the
// flattening tolerance -- so the stroker was being charged for its input. The stroke cases now build
// their centre-line through the renderer's own adaptive ellipse-to-path conversion, the same way the
// filled control already did.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class ScaleStabilityTests : RendererTestBase
    {
        public ScaleStabilityTests(GpuFixture gpu) : base(gpu) { }

        // Measured worst case across all scales is maxErr 0.123 / rms 0.024, so these leave room;
        // the pre-adaptive baseline exceeds both by 4-8x.
        private const double MaxErrBound = 0.25;
        private const double RmsBound = 0.09;

        [Theory]
        [InlineData(1f)] [InlineData(4f)] [InlineData(12f)] [InlineData(24f)]
        public void FilledCircle_HoldsEdgeAccuracyUnderWorldScale(float worldScale)
        {
            (double maxErr, double rms) = Measure(worldScale, strokeHalf: null,
                (c, r, _) => new GeometryFill(new EllipseGeometry(c, r, r), RgbaColor.FromBytes(0, 0, 0, 255)));

            Assert.True(maxErr <= MaxErrBound && rms <= RmsBound,
                $"fill at {worldScale}x: maxErr={maxErr:F4} (bound {MaxErrBound}), rms={rms:F4} (bound {RmsBound})");
        }

        [Theory]
        [InlineData(1f, true)] [InlineData(4f, true)] [InlineData(12f, true)] [InlineData(24f, true)]
        [InlineData(1f, false)] [InlineData(4f, false)] [InlineData(12f, false)] [InlineData(24f, false)]
        public void StrokedCircle_HoldsEdgeAccuracyUnderWorldScale(float worldScale, bool round)
        {
            LineJoin join = round ? LineJoin.Round : LineJoin.Miter;
            LineCap cap = round ? LineCap.Round : LineCap.Butt;

            (double maxErr, double rms) = Measure(worldScale, strokeHalf: 6f,
                (c, r, ws) => new GeometryStroke(
                    WgpuSceneRenderer.EllipseToPathForTest(c, r, r, CurveFlattener.ToleranceForScale(ws)),
                    RgbaColor.FromBytes(0, 0, 0, 255), new StrokeStyle(12.0, cap, join)));

            Assert.True(maxErr <= MaxErrBound && rms <= RmsBound,
                $"stroke-{(round ? "round" : "miter")} at {worldScale}x: maxErr={maxErr:F4} " +
                $"(bound {MaxErrBound}), rms={rms:F4} (bound {RmsBound})");
        }

        /// <summary>
        /// Renders the shape at the given world scale and compares its alpha against an ANALYTIC
        /// reference -- exact disc (or annulus) coverage by 24x24 supersampling of the true shape.
        /// Only partially-covered reference pixels are scored: fully inside or fully outside says
        /// nothing about edge accuracy and would dilute the error toward zero.
        /// </summary>
        private (double MaxErr, double Rms) Measure(float ws, float? strokeHalf,
            Func<Vector2, float, float, DrawingPrimitive> make)
        {
            const float localRadius = 20f;
            float r = localRadius * ws;
            float pad = strokeHalf.HasValue ? strokeHalf.Value * ws + 6 : 8;
            int size = (int)MathF.Ceiling(2 * r + 2 * pad);
            var centre = new Vector2(size / 2f, size / 2f);

            var root = new SceneVisual { Transform = Matrix3x2.CreateScale(ws) };
            root.Content.Add(make(new Vector2(centre.X / ws, centre.Y / ws), localRadius, ws));

            byte[] px = Render(root, size, size);

            float inner = strokeHalf.HasValue ? r - strokeHalf.Value * ws : 0f;
            float outer = strokeHalf.HasValue ? r + strokeHalf.Value * ws : r;

            double sumSq = 0, maxErr = 0;
            int n = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double reference = Coverage(x, y, centre, inner, outer);
                    if (reference <= 0.001 || reference >= 0.999) continue;
                    double actual = 1.0 - px[(y * size + x) * 4] / 255.0;
                    double e = Math.Abs(actual - reference);
                    sumSq += e * e; n++;
                    if (e > maxErr) maxErr = e;
                }
            return (maxErr, Math.Sqrt(sumSq / Math.Max(n, 1)));
        }

        private static double Coverage(int x, int y, Vector2 c, float inner, float outer)
        {
            const int S = 24;
            int hit = 0;
            for (int j = 0; j < S; j++)
                for (int i = 0; i < S; i++)
                {
                    float dx = x + (i + 0.5f) / S - c.X, dy = y + (j + 0.5f) / S - c.Y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= outer * outer && d2 >= inner * inner) hit++;
                }
            return hit / (double)(S * S);
        }
    }
}
