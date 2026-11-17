// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A DrawingGroup's ClipGeometry and OpacityMask, as a DrawingContext draws the group: what its children draw is
// clipped to the clip and masked by the mask. Both were dropped -- a vector icon whose group clips it drew past
// its clip, and a group faded by a gradient mask drew whole.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Brushes
{
    public sealed class DrawingGroupClipMaskTests : RendererTestBase
    {
        public DrawingGroupClipMaskTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;
        private const uint HRoot = 2, HContent = 3, HRed = 4, HRect = 5, HDraw = 6, HGroup = 7, HClip = 8, HMask = 9;

        private static MilcoreEngine RedSquare(uint hClip, uint hMask)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(HRed, 1, 0, 0, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(HRect, 0, 0, 64, 64));
            e.SubmitCommand(MilCmd.GeometryDrawing(HDraw, HRed, 0, HRect));
            return e;
        }

        [Fact]
        public void TheGroupsClip_CutsWhatItsChildrenDraw()
        {
            MilcoreEngine e = RedSquare(HClip, 0);
            e.SubmitCommand(MilCmd.RectangleGeometry(HClip, 16, 16, 32, 32));
            e.SubmitCommand(MilCmd.DrawingGroup(HGroup, 1.0, new[] { HDraw }, hClip: HClip));
            var img = new Image(RenderContent(e, MilCmd.DrawDrawingRecord(HGroup), W, H, hVisual: HRoot, hContent: HContent), W, H);

            // red where it is drawn; the white background where it is not
            Assert.True(img[32, 32].R > 200 && img[32, 32].G < 40, $"inside the clip it is drawn, was {img[32, 32]}");
            Assert.True(img[6, 6].G > 200, $"outside it nothing is, was {img[6, 6]}");
            Assert.True(img[58, 32].G > 200, $"outside it nothing is, was {img[58, 32]}");
        }

        [Fact]
        public void TheGroupsOpacityMask_FadesWhatItsChildrenDraw()
        {
            MilcoreEngine e = RedSquare(0, HMask);
            // opaque on the left, transparent on the right
            e.SubmitCommand(MilCmd.LinearGradientBrush(HMask, 0, 0.5, 1, 0.5, mappingMode: 1, new[]
            {
                (0f, 0f, 0f, 0f, 1f),
                (1f, 0f, 0f, 0f, 0f),
            }));
            e.SubmitCommand(MilCmd.DrawingGroup(HGroup, 1.0, new[] { HDraw }, hMask: HMask));
            var img = new Image(RenderContent(e, MilCmd.DrawDrawingRecord(HGroup), W, H, hVisual: HRoot, hContent: HContent), W, H);

            // red where the mask is opaque; the white background where it is transparent
            Assert.True(img[4, 32].R > 200 && img[4, 32].G < 40, $"where the mask is opaque it shows, was {img[4, 32]}");
            Assert.True(img[60, 32].G > 200, $"where it is transparent it does not, was {img[60, 32]}");
        }
    }
}
