// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.MediaContextTest.
//
// Proves the WebGPU backend consumes WPF's REAL milcore command protocol -- the exact MILCMD_*
// binary PresentationCore's DUCE.Channel emits -- and not the WgpuInterop mini-protocol that the
// scene-graph tests use.
//
// The distinction is the entire point. Almost every other test in this suite builds a SceneVisual
// directly or drives a handful of commands; this one assembles a full partition the way a channel
// really does, including the Begin/Append/End marshalling of variable-length render data and the
// TargetSetRoot that hooks a tree onto a composition target. A backend can pass everything else and
// still fail to consume a real app's command stream.
//
//     root visual
//       +-- content: DrawRectangle (8,8,20,20), opaque red
//       +-- child visual @ offset (32,32), alpha 0.5
//             +-- content: DrawRectangle (0,0,20,20), opaque black
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class MediaContextWiringTests : RendererTestBase
    {
        public MediaContextWiringTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        // Handles allocated by the "client" exactly as WPF's MultiChannelResource would.
        private const uint HTarget = 1, HRoot = 2, HRedBrush = 3, HRedContent = 4,
                           HChild = 5, HBlackBrush = 6, HBlackContent = 7;

        private byte[] RenderPartition()
        {
            var engine = new MilcoreEngine();

            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);

            engine.CreateOrAddRef(HRedBrush, MilResourceTypeId.SolidColorBrush);
            engine.SubmitCommand(MilCmd.SolidColorBrush(HRedBrush, 1, 0, 0, 1));

            // Render data arrives the way the channel really marshals it: a Begin/Append/End
            // sequence carrying the fixed MILCMD_RENDERDATA struct, then the record bytes.
            engine.CreateOrAddRef(HRedContent, MilResourceTypeId.RenderData);
            byte[] redRecord = MilCmd.DrawRectangleRecord(HRedBrush, 0, 8, 8, 20, 20);
            engine.BeginCommand(MilCmd.RenderDataHeader(HRedContent, redRecord.Length));
            engine.AppendCommandData(redRecord);
            engine.EndCommand();
            engine.SubmitCommand(MilCmd.VisualSetContent(HRoot, HRedContent));

            engine.CreateOrAddRef(HChild, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.VisualSetOffset(HChild, 32, 32));
            engine.SubmitCommand(MilCmd.VisualSetAlpha(HChild, 0.5));

            engine.CreateOrAddRef(HBlackBrush, MilResourceTypeId.SolidColorBrush);
            engine.SubmitCommand(MilCmd.SolidColorBrush(HBlackBrush, 0, 0, 0, 1));

            // The same render data, this time submitted as ONE command rather than Begin/Append/End.
            // Both marshalling shapes occur in a real channel depending on payload size, so both are
            // exercised here rather than picking whichever is convenient.
            engine.CreateOrAddRef(HBlackContent, MilResourceTypeId.RenderData);
            engine.SubmitCommand(MilCmd.RenderData(HBlackContent,
                MilCmd.DrawRectangleRecord(HBlackBrush, 0, 0, 0, 20, 20)));
            engine.SubmitCommand(MilCmd.VisualSetContent(HChild, HBlackContent));

            engine.SubmitCommand(MilCmd.VisualInsertChildAt(HRoot, HChild, 0));

            // Hook the root onto the composition target, as a real app does.
            engine.SubmitCommand(MilCmd.TargetSetRoot(HTarget, HRoot));

            engine.Realize();
            SceneVisual? root = engine.Root;
            Assert.True(root is not null, "MilcoreEngine produced no root from a real partition");
            return NewRenderer().RenderToRgba(root!, W, H, White);
        }

        /// <summary>
        /// Tolerance 1: every probe sits mid-flat-region, so nothing here can be excused as an edge.
        /// The (40,40) grey is the composite one -- it only lands correctly if the child's OFFSET and
        /// its ALPHA were both decoded, since either alone gives white or solid black.
        /// </summary>
        [Theory]
        [InlineData(18, 18, 255, 0, 0, "opaque red rectangle (root content)")]
        [InlineData(40, 40, 128, 128, 128, "50% black over white (child at offset, alpha 0.5)")]
        [InlineData(2, 2, 255, 255, 255, "white background")]
        [InlineData(40, 5, 255, 255, 255, "outside the child rect stays background")]
        public void RealMilcorePartition_Renders(int x, int y, int r, int g, int b, string what)
            => new Image(RenderPartition(), W, H).AssertPixel(x, y, r, g, b, tol: 1, what);
    }
}
