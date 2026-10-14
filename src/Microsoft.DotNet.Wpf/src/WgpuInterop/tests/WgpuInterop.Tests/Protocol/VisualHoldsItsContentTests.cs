// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A milcore visual holds its own reference to the render data it shows: WPF may release the render
// data's channel handle while the visual still shows it, and the content stays on screen until the
// visual is given other content. The engine used to drop the bytes with the handle, and Telerik's
// RadRibbonWindow (Workbench) came up with a blank caption, frame and ribbon.
//

using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class VisualHoldsItsContentTests
    {
        private static void SetRenderData(MilcoreEngine e, uint handle, byte[] content)
        {
            e.CreateOrAddRef(handle, MilResourceTypeId.RenderData);
            e.BeginCommand(MilCmd.RenderDataHeader(handle, content.Length));
            e.AppendCommandData(content);
            e.EndCommand();
        }

        [Fact]
        public void ReleasedContentStaysUntilTheVisualIsGivenOther()
        {
            var e = new MilcoreEngine();
            e.CreateOrAddRef(1, MilResourceTypeId.Visual);
            e.SubmitCommand(MilCmd.SolidColorBrush(10, 1, 0, 0, 1));
            SetRenderData(e, 6, MilCmd.DrawRectangleRecord(10, 0, 0, 0, 20, 20));
            e.SubmitCommand(MilCmd.VisualSetContent(1, 6));
            e.Realize();
            Assert.Single(e.VisualByHandle(1)!.Content);

            Assert.True(e.Release(6));                    // the handle leaves the channel...
            e.Realize();
            Assert.Single(e.VisualByHandle(1)!.Content);  // ...the visual still shows it

            // The freed handle is reused for unrelated render data: the visual is not affected.
            byte[] two = Concat(MilCmd.DrawRectangleRecord(10, 0, 0, 0, 5, 5), MilCmd.DrawRectangleRecord(10, 0, 10, 10, 5, 5));
            SetRenderData(e, 6, two);
            e.Realize();
            Assert.Single(e.VisualByHandle(1)!.Content);

            e.SubmitCommand(MilCmd.VisualSetContent(1, 6));   // now it is given it
            e.Realize();
            Assert.Equal(2, e.VisualByHandle(1)!.Content.Count);

            e.SubmitCommand(MilCmd.VisualSetContent(1, 0));   // and then nothing
            e.Realize();
            Assert.Empty(e.VisualByHandle(1)!.Content);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }
    }
}
