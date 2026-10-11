// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The sink on its render thread (WpfCompositionSink.UseRenderThread, WPF_RENDER_THREAD=1).
//
// Every call is recorded on the calling (UI) thread and replayed in order on the render thread; a
// Commit hands the batch over without waiting, and calls made while the render thread is busy join
// the next batch. What the UI thread still answers synchronously -- resource handles and whether a
// release took a resource off the channel -- must agree with the engine exactly, because WPF acts on
// those answers immediately.
//
// These run headless where there is no GPU: the recording, ordering and reference counting are the
// same either way, and that is what they are about.
//

using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public class RenderThreadTests
    {
        private const uint HBrush = 5;

        private static WpfCompositionSink Threaded() => new WpfCompositionSink { UseRenderThread = true };

        private static RgbaColor BrushColour(WpfCompositionSink sink)
            => sink.Engine.SolidBrushColour(HBrush);

        [Fact]
        public void CallsAreReplayedInOrder_OnceHandedOver()
        {
            using WpfCompositionSink sink = Threaded();
            uint h = sink.CreateOrAddRef(0, HBrush, (uint)MilResourceTypeId.SolidColorBrush, out _);
            Assert.Equal(HBrush, h);
            sink.SendCommand(0, MilCmd.SolidColorBrush(HBrush, 1f, 0f, 0f, 1f), false);
            sink.SendCommand(0, MilCmd.SolidColorBrush(HBrush, 0f, 1f, 0f, 1f), false);

            sink.SyncFlush(0);

            // The LAST command wins, which it only does if the replay kept their order.
            RgbaColor c = BrushColour(sink);
            Assert.Equal(0f, c.R, 3);
            Assert.Equal(1f, c.G, 3);
        }

        [Fact]
        public void NothingReachesTheEngine_BeforeItIsHandedOver()
        {
            using WpfCompositionSink sink = Threaded();
            sink.CreateOrAddRef(0, HBrush, (uint)MilResourceTypeId.SolidColorBrush, out _);
            sink.SendCommand(0, MilCmd.SolidColorBrush(HBrush, 1f, 0f, 0f, 1f), false);
            Thread.Sleep(50);
            Assert.False(sink.Engine.HasSolidBrush(HBrush),
                "a recorded call reached the engine from the UI thread, before any commit");
            sink.SyncFlush(0);
            Assert.True(sink.Engine.HasSolidBrush(HBrush));
        }

        [Fact]
        public void ReleaseAnswersAsTheEngineWould()
        {
            using WpfCompositionSink sink = Threaded();
            sink.CreateOrAddRef(0, HBrush, (uint)MilResourceTypeId.SolidColorBrush, out _);
            sink.CreateOrAddRef(0, HBrush, (uint)MilResourceTypeId.SolidColorBrush, out _);

            Assert.False(sink.ReleaseOnChannel(0, HBrush));   // still referenced
            Assert.True(sink.ReleaseOnChannel(0, HBrush));    // gone
            sink.SyncFlush(0);
            Assert.False(sink.Engine.HasSolidBrush(HBrush));

            // A handle the sink allocates stays ahead of client-assigned ones, as before.
            uint fresh = sink.CreateOrAddRef(0, 0, (uint)MilResourceTypeId.SolidColorBrush, out bool created);
            Assert.True(created);
            Assert.True(fresh > HBrush);
        }

        [Fact]
        public void CommitsThatDoNotWait_LoseNothing()
        {
            using WpfCompositionSink sink = Threaded();
            sink.CreateOrAddRef(0, HBrush, (uint)MilResourceTypeId.SolidColorBrush, out _);
            for (int i = 0; i <= 200; i++)
            {
                sink.SendCommand(0, MilCmd.SolidColorBrush(HBrush, i / 200f, 0f, 0f, 1f), false);
                sink.Commit(0);
            }
            sink.SyncFlush(0);
            Assert.Equal(1f, BrushColour(sink).R, 3);
        }

        [Fact]
        public void ReadbackOfAnUnknownTarget_ReturnsNullWithoutHanging()
        {
            using WpfCompositionSink sink = Threaded();
            Assert.Null(sink.ReadbackTarget(0, 0xDEAD));
        }
    }
}
