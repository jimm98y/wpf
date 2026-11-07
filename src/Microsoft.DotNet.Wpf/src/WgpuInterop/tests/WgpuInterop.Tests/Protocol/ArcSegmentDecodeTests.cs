// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A MIL_SEGMENT_ARC's LargeArc flag is at offset 12, in the slot the other non-poly segments leave as
// padding -- not after Sweep, where the struct's last field (ForcePacking, always 0) sits. Decoding it
// from there made every large arc the SMALL arc between the same two endpoints.
//
// The case that showed it is a common "loading" spinner: a Path whose Data is the 270-degree ring
// "M1,0 A1,1,90,1,1,0,-1", Stretch=Fill into 10x10, spun by an animated RotateTransform. Layout bounds
// the real geometry managed-side, so it sized and stretched the ring correctly -- and the compositor
// then drew a quarter circle about the far corner instead, bulging the wrong way, which under the
// spin read as a squashed, off-centre ellipse. Stock WPF draws the round ring at every angle.
//
// The scene is that ring, scaled by its geometry transform the way Stretch does it, under a world
// transform of a rotation times the 2x backing scale, at several angles: the ring must stay round and
// cover three quarters of the circle wherever it is turned.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class ArcSegmentDecodeTests : RendererTestBase
    {
        public ArcSegmentDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 128, H = 128;
        private const uint HRoot = 2, HContent = 3, HBlack = 4, HPen = 5, HRing = 6, HStretch = 7, HWorld = 8;

        // Ring of radius 20 about (32,32) in the visual's space; the world transform doubles it, so on
        // screen it is radius 40 about (64,64) with a 6 px pen.
        private const double R = 20, C = 32, Scale = 2, Pen = 3;

        private byte[] RenderRing(double angleDegrees)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(HBlack, 0, 0, 0, 1));
            e.SubmitCommand(MilCmd.Pen(HPen, HBlack, Pen));

            // What Stretch=Fill hands the geometry: a uniform scale and an offset onto the unit ring.
            e.SubmitCommand(MilCmd.MatrixTransform(HStretch, R, 0, 0, R, C, C));
            e.SubmitCommand(MilCmd.PathGeometryArc(HRing, HStretch, (1, 0), (0, -1), 1, 1,
                xRotationDegrees: 90, largeArc: true, clockwise: true));

            // Rotation about the ring's centre, then the 2x backing scale.
            double a = angleDegrees * Math.PI / 180.0, cos = Math.Cos(a), sin = Math.Sin(a);
            double dx = C - (C * cos - C * sin), dy = C - (C * sin + C * cos);
            e.SubmitCommand(MilCmd.MatrixTransform(HWorld,
                cos * Scale, sin * Scale, -sin * Scale, cos * Scale, dx * Scale, dy * Scale));
            e.SubmitCommand(MilCmd.VisualSetTransform(HRoot, HWorld));

            return RenderContent(e, MilCmd.DrawGeometryRecord(0, HPen, HRing), W, H,
                                 hVisual: HRoot, hContent: HContent);
        }

        private static bool IsInk(Image img, double x, double y)
        {
            Rgb c = img[(int)Math.Round(x), (int)Math.Round(y)];
            return c.R < 128;
        }

        /// <summary>
        /// The arc runs clockwise (y down) from east through south and west to north: geometry angles
        /// 0..270. Probing 45/135/225 keeps clear of both ends; 315 is the gap. Each is checked on the
        /// ring's radius and well inside and outside it, so a ring drawn elliptical, displaced, or as
        /// the small arc fails at least one probe.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(45)]
        [InlineData(90)]
        [InlineData(135)]
        [InlineData(200)]
        public void ALargeArc_DrawsTheLargeArc_RoundAtEveryRotation(double angleDegrees)
        {
            var img = new Image(RenderRing(angleDegrees), W, H);
            double cx = C * Scale, cy = C * Scale, r = R * Scale;

            foreach (double along in new[] { 45.0, 135.0, 225.0, 315.0 })
            {
                double t = (along + angleDegrees) * Math.PI / 180.0;
                double ux = Math.Cos(t), uy = Math.Sin(t);
                bool inGap = along == 315.0;

                Assert.True(IsInk(img, cx + ux * r, cy + uy * r) != inGap,
                    $"rotated {angleDegrees}: the ring at {along} degrees along it should be " +
                    $"{(inGap ? "the gap" : "inked")}, pixel was {img[(int)Math.Round(cx + ux * r), (int)Math.Round(cy + uy * r)]}");
                Assert.False(IsInk(img, cx + ux * (r - 10), cy + uy * (r - 10)),
                    $"rotated {angleDegrees}: nothing inside the ring at {along} degrees");
                Assert.False(IsInk(img, cx + ux * (r + 10), cy + uy * (r + 10)),
                    $"rotated {angleDegrees}: nothing outside the ring at {along} degrees");
            }
            Assert.False(IsInk(img, cx, cy), $"rotated {angleDegrees}: the ring's centre is empty");
        }
    }
}
