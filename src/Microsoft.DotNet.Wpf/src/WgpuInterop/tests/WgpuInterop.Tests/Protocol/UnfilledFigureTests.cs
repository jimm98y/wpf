// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A path figure carries IsFillable (BeginFigure's isFilled, PathFigure.IsFilled). One that is not filled is
// stroked but takes no part in the fill, nor in a clip. app's Trend Viewer draws its trend line as
// DrawGeometry(fillBrush, pen, StreamGeometry) with BeginFigure(points[0], isFilled: false, isClosed: false):
// stock WPF shows only the line; the compositor ignored the flag and filled the polyline, implicit closing
// edge and all, as a light-blue polygon.
//
// The expectations are stock WPF's (RenderTargetBitmap of the same drawings): an unfilled open figure is a
// line only, a figure drawn with no brush is a line only, and a filled open figure is filled across its
// implicit close but not stroked along it.
//

using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class UnfilledFigureTests : RendererTestBase
    {
        public UnfilledFigureTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;
        private const uint HRoot = 2, HContent = 3, HBlue = 4, HRed = 5, HPen = 6, HPath = 7, HClipPath = 8, HGreen = 9;

        // The open polyline (8,8) -> (56,8) -> (56,56): a right triangle when filled, two lines when stroked.
        private byte[] Render(bool filled, bool withBrush, bool clipByUnfilled = false)
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(HBlue, 0, 0, 1, 1));
            e.SubmitCommand(MilCmd.SolidColorBrush(HRed, 1, 0, 0, 1));
            e.SubmitCommand(MilCmd.SolidColorBrush(HGreen, 0, 1, 0, 1));
            e.SubmitCommand(MilCmd.Pen(HPen, HRed, 2));
            e.SubmitCommand(MilCmd.PathGeometryTriangle(HPath, (8, 8), (56, 8), (56, 56), poly: true, closed: false, filled: filled));
            e.SubmitCommand(MilCmd.PathGeometryTriangle(HClipPath, (0, 0), (64, 0), (64, 64), poly: true, closed: false, filled: false));
            byte[] content = clipByUnfilled
                ? MilCmd.Concat(
                    MilCmd.PushClipRecord(HClipPath),
                    MilCmd.DrawRectangleRecord(HGreen, 0, 0, 0, 64, 64),
                    MilCmd.PopRecord())
                : MilCmd.DrawGeometryRecord(withBrush ? HBlue : 0, HPen, HPath);
            return RenderContent(e, content, W, H, hVisual: HRoot, hContent: HContent);
        }

        [Fact]
        public void AnOpenUnfilledFigure_WithAFillBrush_IsOnlyStroked()
        {
            var img = new Image(Render(filled: false, withBrush: true), W, H);
            img.AssertPixel(44, 20, 255, 255, 255, tol: 6, "inside the would-be triangle: not filled");
            img.AssertPixel(32, 8, 255, 0, 0, tol: 40, "the first leg is stroked");
            img.AssertPixel(56, 32, 255, 0, 0, tol: 40, "the second leg is stroked");
            img.AssertPixel(32, 32, 255, 255, 255, tol: 6, "the implicit closing edge is not stroked");
        }

        [Fact]
        public void AFigureWithNoBrush_IsOnlyStroked()
        {
            var img = new Image(Render(filled: true, withBrush: false), W, H);
            img.AssertPixel(44, 20, 255, 255, 255, tol: 6, "no brush: nothing filled");
            img.AssertPixel(32, 8, 255, 0, 0, tol: 40, "stroked");
        }

        [Fact]
        public void AnOpenFilledFigure_FillsAcrossItsImplicitClose()
        {
            var img = new Image(Render(filled: true, withBrush: true), W, H);
            img.AssertPixel(44, 20, 0, 0, 255, tol: 6, "filled");
            img.AssertPixel(20, 44, 255, 255, 255, tol: 6, "the other side of the closing edge is not");
            img.AssertPixel(32, 8, 255, 0, 0, tol: 40, "the legs are stroked over the fill");
        }

        [Fact]
        public void AClipOfOnlyUnfilledFigures_ShowsNothing()
        {
            var img = new Image(Render(filled: false, withBrush: true, clipByUnfilled: true), W, H);
            img.AssertPixel(50, 10, 255, 255, 255, tol: 6, "clipped away: an unfilled figure has no area");
        }
    }
}
