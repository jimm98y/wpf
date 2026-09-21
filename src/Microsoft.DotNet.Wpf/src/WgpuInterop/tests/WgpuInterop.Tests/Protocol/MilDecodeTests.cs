// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.MilDecodeTest.
//
// The render ops a real WPF tree actually emits, driven as byte-exact MILCMD: a MatrixTransform on
// the root (2x scale) plus DrawRoundedRectangle / DrawEllipse / DrawGeometry, a serialized
// PathGeometry in both segment encodings, PushClip and PushOpacity.
//
// One scene renders once and every case probes it, because the transform is shared: each probe's
// expected DEVICE position is its local position doubled, and separating them into scenes would
// lose the property that makes the transform testable at all.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class MilDecodeTests : RendererTestBase
    {
        public MilDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;
        private const uint HRoot = 2, HRed = 3, HBlue = 5, HContent = 6, HGreen = 7,
                           HRectGeom = 8, HGrad = 9, HXform = 10, HPath = 11, HMagenta = 12,
                           HCyan = 13, HPathPoly = 14, HClip = 15, HBlack = 16;

        /// <summary>
        /// The 2x root transform mirrors WPF's DPI/root transform: it is what makes a real window's
        /// content fill the device-pixel target, so every expectation below is a LOCAL coordinate
        /// doubled.
        /// </summary>
        private byte[] RenderScene()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);

            e.SubmitCommand(MilCmd.MatrixTransform(HXform, 2, 0, 0, 2, 0, 0));
            e.SubmitCommand(MilCmd.VisualSetTransform(HRoot, HXform));

            e.SubmitCommand(MilCmd.SolidColorBrush(HRed, 1, 0, 0, 1));
            e.SubmitCommand(MilCmd.SolidColorBrush(HBlue, 0, 0, 1, 1));
            e.SubmitCommand(MilCmd.SolidColorBrush(HGreen, 0, 1, 0, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(HRectGeom, 2, 16, 10, 8));

            // Horizontal red->blue gradient, RelativeToBoundingBox (WPF's default).
            e.SubmitCommand(MilCmd.LinearGradientBrush(HGrad, 0, 0, 1, 0, mappingMode: 1, new[]
            {
                (0f, 1f, 0f, 0f, 1f),
                (1f, 0f, 0f, 1f, 1f),
            }));

            // Two triangles through a real serialized PathGeometry, in BOTH segment encodings.
            e.SubmitCommand(MilCmd.SolidColorBrush(HMagenta, 1, 0, 1, 1));
            e.SubmitCommand(MilCmd.PathGeometryTriangle(HPath, (22, 14), (30, 14), (26, 22), poly: false));
            e.SubmitCommand(MilCmd.SolidColorBrush(HCyan, 0, 1, 1, 1));
            e.SubmitCommand(MilCmd.PathGeometryTriangle(HPathPoly, (16, 24), (24, 24), (20, 30), poly: true));

            e.SubmitCommand(MilCmd.SolidColorBrush(HBlack, 0, 0, 0, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(HClip, 28, 1, 3, 4));

            byte[] content = MilCmd.Concat(
                MilCmd.DrawRoundedRectangleRecord(HRed, 2, 2, 10, 10),
                MilCmd.DrawEllipseRecord(HBlue, 22, 6, 4, 4),
                MilCmd.DrawGeometryRecord(HGreen, 0, HRectGeom),
                MilCmd.DrawRoundedRectangleRecord(HGrad, 2, 26, 12, 4),
                MilCmd.DrawGeometryRecord(HMagenta, 0, HPath),
                MilCmd.DrawGeometryRecord(HCyan, 0, HPathPoly),
                // PushClip: only the clipped sliver of this magenta rect may show.
                MilCmd.PushClipRecord(HClip),
                MilCmd.DrawRoundedRectangleRecord(HMagenta, 26, 0, 6, 10),
                MilCmd.PopRecord(),
                // PushOpacity: 50% black over white -> grey, in a clear area.
                MilCmd.PushOpacityRecord(0.5),
                MilCmd.DrawRoundedRectangleRecord(HBlack, 15, 19, 4, 4),
                MilCmd.PopRecord());

            return RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent);
        }

        /// <summary>
        /// The (22,22) probe is the transform proof: it is local (11,11), inside the red rect ONLY
        /// because of the 2x scale -- untransformed the rect would end at device 12.
        /// </summary>
        [Theory]
        [InlineData(12, 12, 255, 0, 0, "red rounded rect, scaled")]
        [InlineData(22, 22, 255, 0, 0, "2x transform applied (device 22 = local 11, inside the rect)")]
        [InlineData(44, 12, 0, 0, 255, "blue ellipse, scaled")]
        [InlineData(12, 40, 0, 255, 0, "green DrawGeometry(RectangleGeometry)")]
        [InlineData(2, 2, 255, 255, 255, "white background")]
        public void PrimitiveRecords_DecodeUnderTheRootTransform(int x, int y, int r, int g, int b, string what)
            => new Image(RenderScene(), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        /// <summary>
        /// Serialized PathGeometry in BOTH segment encodings. They are not interchangeable:
        /// StreamGeometry emits the PolyLine form and a PathGeometry of LineSegment objects emits the
        /// Line form, so a decoder can support one and silently drop the other.
        /// </summary>
        [Theory]
        [InlineData(52, 33, 255, 0, 255, "magenta triangle (Line segments)")]
        [InlineData(40, 52, 0, 255, 255, "cyan triangle (PolyLine segment, the StreamGeometry encoding)")]
        [InlineData(40, 40, 255, 255, 255, "outside the triangles is background")]
        public void SerializedPathGeometry_DecodesInBothSegmentEncodings(int x, int y, int r, int g, int b, string what)
            => new Image(RenderScene(), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        /// <summary>
        /// Clip region local (28,1,3,4) -> device (56..62, 2..10); the magenta rect spans device
        /// (52..64, 0..20). Both "outside" probes lie INSIDE the rect, so they fail unless the clip
        /// is genuinely applied.
        /// </summary>
        [Theory]
        [InlineData(58, 6, 255, 0, 255, "inside the clip")]
        [InlineData(54, 6, 255, 255, 255, "left of the clip, but inside the rect")]
        [InlineData(58, 16, 255, 255, 255, "below the clip, but inside the rect")]
        public void PushClip_RestrictsTheEnclosedRecords(int x, int y, int r, int g, int b, string what)
            => new Image(RenderScene(), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        [Fact]
        public void PushOpacity_HalvesTheEnclosedFill()
        {
            var img = new Image(RenderScene(), W, H);
            // Black rect local (15,19,4,4) -> device (30..38, 38..46), at 0.5 over white.
            img.AssertPixel(34, 42, 128, 128, 128, tol: 10, "50% black over white through PushOpacity");
        }

        /// <summary>
        /// The gradient is asserted by DOMINANT CHANNEL rather than an exact colour: it is
        /// RelativeToBoundingBox, so the exact value at a sample depends on the rect's bounds and on
        /// the compositing mode, while "red end is reddest, blue end is bluest" holds under both.
        /// </summary>
        [Fact]
        public void LinearGradient_RampsAcrossTheFilledRect()
        {
            var img = new Image(RenderScene(), W, H);

            Rgb left = img[6, 56], right = img[26, 56];
            Assert.True(left.R > left.B, $"the gradient's left edge should be reddest, was {left}");
            Assert.True(right.B > right.R, $"the gradient's right edge should be bluest, was {right}");
        }
    }
}
