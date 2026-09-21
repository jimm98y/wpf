// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.OpcodeTest.
//
// MILCMD opcodes the decoder used to SKIP. Every one is emitted by real WPF and was silently
// dropped -- an unknown record is skipped by its size header, so the shape simply did not appear
// (or, for guidelines, appeared unsnapped). Nothing failed; things were just missing.
//
// The offsets these drive come from Common/Graphics/Generated/wgx_commands.cs and
// Media/Generated/RenderData.cs, NOT from the decoder's own comments: a decoder and a test that
// share an assumption cannot disprove it. They now live in MilCmd, which is why this file is short.
//
//   0x7b GeometryGroup       <GeometryGroup>, and Path data with several figures
//   0x7c CombinedGeometry    <CombinedGeometry>, Geometry.Combine
//   0x8c GuidelineSet        the GuidelineSet resource
//   0x53 PushGuidelineY1     emitted by SimpleTextLine.Draw for EVERY text line
//   0x54 PushGuidelineY2     emitted by LineServicesCallbacks for underlines
//   0x4a DrawDrawing         DrawingContext.DrawDrawing
//   0x41 DrawRectangleAnimate + 0x11 RectResource
//   0x84 BitmapCacheBrush
//   0x4e PushOpacityMask
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class OpcodeDecodeTests : RendererTestBase
    {
        public OpcodeDecodeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 120, H = 80;

        private byte[] RenderOps(MilcoreEngine e, byte[] content) => RenderContent(e, content, W, H);

        // ---- 0x7b GeometryGroup -------------------------------------------------------------

        [Fact]
        public void GeometryGroup_FillsEveryChild()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 1, 0, 0, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(20, 10, 20, 20, 20));
            e.SubmitCommand(MilCmd.RectangleGeometry(21, 60, 20, 20, 20));
            e.SubmitCommand(MilCmd.GeometryGroup(22, fillRule: 0, 20, 21));

            var img = new Image(RenderOps(e, MilCmd.DrawGeometryRecord(10, 0, 22)), W, H);
            img.AssertPixel(20, 20, 255, 0, 0, 6, "first child of the group is filled");
            img.AssertPixel(70, 20, 255, 0, 0, 6, "second child of the group is filled");
            img.AssertPixel(45, 20, 255, 255, 255, 6, "the gap between them is not");
        }

        // ---- 0x7c CombinedGeometry ----------------------------------------------------------

        [Fact]
        public void CombinedGeometry_Exclude_RemovesTheSecondShape()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 1, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(20, 20, 20, 40, 40));
            e.SubmitCommand(MilCmd.RectangleGeometry(21, 40, 20, 20, 40));
            e.SubmitCommand(MilCmd.CombinedGeometry(22, mode: 2 /* Exclude */, 20, 21));

            var img = new Image(RenderOps(e, MilCmd.DrawGeometryRecord(10, 0, 22)), W, H);
            img.AssertPixel(28, 40, 0, 0, 255, 6, "the part of geometry1 outside geometry2 survives");
            img.AssertPixel(50, 40, 255, 255, 255, 6, "the excluded region is gone");
        }

        // ---- 0x8c / 0x53 / 0x54 guidelines --------------------------------------------------

        /// <summary>
        /// Asserts the guidelines reach the VISUAL, not that the snapped pixels look right --
        /// GuidelineSnappingTests already covers the snapping, and restating it here would only
        /// re-test the renderer. What is new is that these records are decoded at all.
        /// </summary>
        [Fact]
        public void GuidelineRecords_ReachTheVisual()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 0, 1));
            e.SubmitCommand(MilCmd.GuidelineSet(30, new double[] { 12.0 }, new double[] { 5.5 }));

            byte[] content = MilCmd.Concat(
                MilCmd.PushGuidelineSetRecord(30),
                MilCmd.DrawRectangleRecord(10, 0, 4, 5.5, 40, 1),
                MilCmd.PopRecord(),
                MilCmd.PushGuidelineY1Record(20.5),
                MilCmd.DrawRectangleRecord(10, 0, 4, 20.5, 40, 1),
                MilCmd.PopRecord(),
                // A text decoration: baseline at 40.5, rule 3.25 below it.
                MilCmd.PushGuidelineY2Record(40.5, 3.25),
                MilCmd.DrawRectangleRecord(10, 0, 4, 43.75, 40, 1),
                MilCmd.PopRecord());

            SceneVisual root = Realize(e, content);
            float[] gx = root.GuidelinesX ?? Array.Empty<float>();
            float[] gy = root.GuidelinesY ?? Array.Empty<float>();

            AssertHas(gx, 12.0f, "GuidelineSet's X guideline");
            AssertHas(gy, 5.5f, "GuidelineSet's Y guideline");
            AssertHas(gy, 20.5f, "PushGuidelineY1's baseline");
            AssertHas(gy, 40.5f, "PushGuidelineY2's leading coordinate");
            AssertHas(gy, 43.75f, "PushGuidelineY2's driven coordinate (leading + offset)");
        }

        private static void AssertHas(float[] set, float want, string what)
            => Assert.True(Array.Exists(set, v => Math.Abs(v - want) < 0.01f),
                $"{what} ({want}) is missing; decoded set was [{string.Join(", ", set)}]");

        // ---- 0x4a DrawDrawing ---------------------------------------------------------------

        /// <summary>
        /// The DrawingGroup carries opacity 0.5, so a correct flatten shows a HALF-strength fill.
        /// Full black would mean the group's state was dropped when its children were folded into
        /// the record stream -- which still draws, just wrongly.
        /// </summary>
        [Fact]
        public void DrawDrawing_FoldsTheGroupsOpacityIntoItsChildren()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 0, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(20, 20, 20, 40, 30));
            e.SubmitCommand(MilCmd.GeometryDrawing(40, hBrush: 10, hPen: 0, hGeometry: 20));
            e.SubmitCommand(MilCmd.DrawingGroup(41, opacity: 0.5, children: new uint[] { 40 }));

            var img = new Image(RenderOps(e, MilCmd.DrawDrawingRecord(41)), W, H);
            img.AssertPixel(40, 35, 128, 128, 128, 10, "the drawing renders at the group's 0.5 opacity");
            img.AssertPixel(5, 5, 255, 255, 255, 6, "outside the drawing is untouched");
        }

        // ---- 0x41 DrawRectangleAnimate + 0x11 RectResource -----------------------------------

        /// <summary>
        /// DrawRectangleAnimate carries a static rect AND a handle to a RectResource that
        /// AnimationClockResource re-sends every tick. The animated value must WIN, or the drawing
        /// is pinned to its base value -- which presents as "the animation does not run".
        /// </summary>
        [Fact]
        public void AnimatedRect_PrefersThePublishedClockValue()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 1, 0, 1));
            // The clock's current value, far from the static rect so the two are unmistakable.
            e.SubmitCommand(MilCmd.RectResource(50, 60, 10, 40, 30));

            var img = new Image(RenderOps(e,
                MilCmd.DrawRectangleAnimateRecord(10, 0, 10, 10, 40, 30, hRectAnim: 50)), W, H);
            img.AssertPixel(80, 25, 0, 255, 0, 6, "the ANIMATED rect is drawn");
            img.AssertPixel(30, 25, 255, 255, 255, 6, "the static rect is not");
        }

        /// <summary>
        /// Before the clock publishes anything -- frame one -- the static value has to stand in, or
        /// the drawing flickers out for a frame.
        /// </summary>
        [Fact]
        public void AnimatedRect_FallsBackToTheStaticValue_BeforeTheClockPublishes()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 1, 0, 1));

            var img = new Image(RenderOps(e,
                MilCmd.DrawRectangleAnimateRecord(10, 0, 10, 10, 40, 30, hRectAnim: 50)), W, H);
            img.AssertPixel(30, 25, 0, 255, 0, 6, "the static rect is used until the clock publishes");
        }

        // ---- 0x84 BitmapCacheBrush ----------------------------------------------------------

        [Fact]
        public void BitmapCacheBrush_PaintsItsCachedTarget()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.CreateOrAddRef(5, MilResourceTypeId.Visual);              // the cached target
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 1, 0, 0, 1));    // it paints itself red

            byte[] targetContent = MilCmd.DrawRectangleRecord(10, 0, 0, 0, 20, 20);
            e.CreateOrAddRef(6, MilResourceTypeId.RenderData);
            e.BeginCommand(MilCmd.RenderDataHeader(6, targetContent.Length));
            e.AppendCommandData(targetContent);
            e.EndCommand();
            e.SubmitCommand(MilCmd.VisualSetContent(5, 6));
            // Target is normally a visual in the tree -- that is what makes caching it worthwhile --
            // and the GPU content-brush path samples the texture it rendered.
            e.SubmitCommand(MilCmd.VisualInsertChildAt(1, 5, 0));
            e.SubmitCommand(MilCmd.BitmapCacheBrush(11, opacity: 1.0, hInternalTarget: 5));

            // The fill sits clear of where the target draws ITSELF (0,0,20,20), so red inside it can
            // only have arrived through the brush.
            var img = new Image(RenderOps(e, MilCmd.DrawRectangleRecord(11, 0, 30, 30, 60, 40)), W, H);
            img.AssertPixel(60, 50, 255, 0, 0, 6, "the cached target paints the filled area");
            img.AssertPixel(100, 50, 255, 255, 255, 6, "outside the fill is untouched");

            bool found = e.TryGetContentBrushForTest(11, out uint source, out bool isDrawing,
                out uint stretch, out TileMode tile);
            Assert.True(found && source == 5 && !isDrawing,
                $"it should resolve to the hInternalTarget visual, got source={source} isDrawing={isDrawing}");
            Assert.True(stretch == 1, $"the target should be stretched to Fill, got stretch={stretch}");
            Assert.True(tile == TileMode.None, $"it should not tile, got {tile}");
        }

        /// <summary>A targetless brush must register NOTHING, not a brush pointing at handle 0.</summary>
        [Fact]
        public void BitmapCacheBrush_WithNoTarget_RegistersNothing()
        {
            var e = new MilcoreEngine();
            e.SubmitCommand(MilCmd.BitmapCacheBrush(12, opacity: 1.0, hInternalTarget: 0));
            Assert.False(e.TryGetContentBrushForTest(12, out _, out _, out _, out _),
                "a targetless BitmapCacheBrush registered a content brush");
        }

        // ---- 0x4e PushOpacityMask -----------------------------------------------------------

        /// <summary>
        /// PushOpacityMask brackets a run of draw records with a mask brush. The Scene layer models
        /// masks per-VISUAL, so the enclosed records collect into a nested visual carrying the mask.
        ///
        /// The mask gradates, and both ends are asserted: "something changed" would pass even with
        /// the mask applied inside out.
        /// </summary>
        [Fact]
        public void PushOpacityMask_GradatesAcrossTheEnclosedRecords()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 0, 1));
            e.SubmitCommand(MilCmd.LinearGradientAlphaBrush(30, 0, 0, 1, 0));

            var masked = new Image(RenderOps(e, MilCmd.Concat(
                MilCmd.PushOpacityMaskRecord(30),
                MilCmd.DrawRectangleRecord(10, 0, 10, 10, 100, 40),
                MilCmd.PopRecord())), W, H);

            // The same rect with no mask, as the control for "fully painted".
            var e2 = new MilcoreEngine();
            e2.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e2.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 0, 1));
            var plain = new Image(RenderOps(e2, MilCmd.DrawRectangleRecord(10, 0, 10, 10, 100, 40)), W, H);

            int left = masked[15, 30].R, right = masked[105, 30].R, control = plain[105, 30].R;

            Assert.True(control < 40, $"the control rect should be solid without a mask, was {control}");
            Assert.True(left < 60, $"the opaque end of the mask should keep the fill, was {left}");
            Assert.True(right > 200, $"the transparent end should reveal the background, was {right}");
            Assert.True(right - left > 120,
                $"the mask must gradate across the group, not switch on/off: left={left}, right={right}");
        }

        // ---- unknown opcodes ----------------------------------------------------------------

        /// <summary>
        /// Families that are deliberately NOT decoded must stay harmless. An unknown COMMAND is
        /// ignored; an unknown render-data PUSH still has to keep the state stack balanced, or its
        /// matching Pop unwinds a level that was never pushed and every later record loses its clip.
        ///
        /// That is why the assertion is about the CLIP surviving rather than about the unknown record
        /// itself: the damage from an unbalanced push lands on unrelated content further down.
        /// </summary>
        [Fact]
        public void UnknownOpcodes_StayHarmlessAndKeepTheStateStackBalanced()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 0, 0, 1, 1));
            e.SubmitCommand(MilCmd.RectangleGeometry(20, 0, 0, 30, 30));

            // 0x14 Point3DResource: sent by IndependentAnimationStorage, referenced by nothing.
            var q = new Buf(); q.U32(0x14); q.U32(99); q.F32(1); q.F32(2); q.F32(3);
            e.SubmitCommand(q.ToArray());

            e.SubmitCommand(MilCmd.RectangleGeometry(21, 0, 0, 15, 30));   // clip: left half only

            byte[] content = MilCmd.Concat(
                MilCmd.PushClipRecord(21),
                MilCmd.PushEffectRecord(),     // unknown push
                MilCmd.PopRecord(),
                MilCmd.DrawGeometryRecord(10, 0, 20),
                MilCmd.PopRecord());

            var img = new Image(RenderOps(e, content), W, H);
            img.AssertPixel(7, 15, 0, 0, 255, 6, "inside the clip still draws");
            img.AssertPixel(22, 15, 255, 255, 255, 6, "the clip survived the unknown push/pop");
        }
    }
}
