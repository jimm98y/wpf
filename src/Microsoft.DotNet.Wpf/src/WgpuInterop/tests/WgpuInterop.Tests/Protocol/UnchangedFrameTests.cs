// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A frame that would draw the same picture as the last one is not drawn.
//
// Frames arrive here whenever WPF ticks, and WPF ticks continuously for as long as anything holds a
// CompositionTarget.Rendering handler or a running clock -- whether or not the picture is moving.
// Every one of those used to walk the scene, encode a command buffer and present it, producing a
// byte-identical image. Measured on an idle full-screen editor that is exactly what happened: about
// a hundred a second, each reporting zero visuals parsed, zero rasterizations, every layer served
// from cache. A laptop battery spent redrawing a still picture.
//
// The signal is what ARRIVED, not a pixel comparison: a dispatched command, a bitmap, or a video
// frame. That is cheap and exact, and it is also the thing that can go wrong -- a mutation that
// forgets to mark the engine dirty shows up not as a slow frame but as a window that stops
// updating. So these assert both directions, and the "still renders" half matters more than the
// "skips" half.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public class UnchangedFrameTests
    {
        private const uint HRoot = 1, HContent = 2, HBrush = 5, HImg = 7;

        /// <summary>A sink with one visual carrying some drawn content, as a live window would have.</summary>
        private static WpfCompositionSink NewSinkWithContent()
        {
            var sink = new WpfCompositionSink();
            MilcoreEngine engine = sink.Engine;
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(HBrush, 1f, 0f, 0f, 1f));
            // What RendererTestBase.Realize does, inlined: these tests never render, so pulling in
            // the GPU fixture would make them need a device they have no use for.
            byte[] content = MilCmd.DrawRectangleRecord(HBrush, 0, 0, 0, 16, 16);
            engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
            engine.BeginCommand(MilCmd.RenderDataHeader(HContent, content.Length));
            engine.AppendCommandData(content);
            engine.EndCommand();
            engine.SubmitCommand(MilCmd.VisualSetContent(HRoot, HContent));
            engine.Realize();
            return sink;
        }

        /// <summary>
        /// The engine starts dirty and stays dirty until told otherwise: the first frame of a window
        /// has nothing on screen to preserve, so it can never be the one that is skipped.
        /// </summary>
        [Fact]
        public void AFreshEngineIsDirty()
        {
            using var sink = new WpfCompositionSink();
            Assert.True(sink.Engine.Dirty);
        }

        [Fact]
        public void ClearingLeavesItClean()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.ClearDirty();
            Assert.False(sink.Engine.Dirty);
        }

        // ---- what must mark it dirty ------------------------------------------------------

        /// <summary>
        /// A dispatched command changes the scene. This is the one that matters most: miss it and a
        /// window stops updating, which is a far worse bug than the wasted frames this avoids.
        /// </summary>
        [Fact]
        public void ACommandMarksItDirty()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.ClearDirty();

            sink.Engine.SubmitCommand(MilCmd.VisualSetContent(HRoot, HContent));

            Assert.True(sink.Engine.Dirty);
        }

        /// <summary>
        /// A command that arrives in pieces counts once it is DISPATCHED. Begin and Append only
        /// accumulate bytes -- nothing is decoded until End -- so marking them would be noise, but
        /// failing to mark the end would lose the change entirely.
        /// </summary>
        [Fact]
        public void ASplitCommandMarksItDirtyWhenItEnds()
        {
            using var sink = NewSinkWithContent();
            byte[] command = MilCmd.VisualSetContent(HRoot, HContent);

            sink.Engine.ClearDirty();
            sink.BeginCommand(0, command, 0);
            sink.AppendCommandData(0, System.Array.Empty<byte>());
            sink.EndCommand(0);

            Assert.True(sink.Engine.Dirty);
        }

        /// <summary>
        /// New bitmap pixels change the picture without any visual being touched -- an ImageBrush
        /// whose source decoded, a WriteableBitmap the application wrote into.
        /// </summary>
        [Fact]
        public void ABitmapMarksItDirty()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.ClearDirty();

            sink.SendBitmap(0, HImg, 1, 1, 4, new byte[] { 0, 0, 255, 255 });

            Assert.True(sink.Engine.Dirty);
        }

        /// <summary>Video is the case where nothing but the pixels ever changes.</summary>
        [Fact]
        public void AVideoFrameMarksItDirty()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.ClearDirty();

            sink.SendVideoFrame(0, HImg, 1, 1, 4, new byte[] { 0, 0, 255, 255 });

            Assert.True(sink.Engine.Dirty);
        }

        // ---- what must NOT ----------------------------------------------------------------

        /// <summary>
        /// Realize is what runs every frame, and it must not itself be a change: it re-derives the
        /// scene from state that has not moved. If this ever marks the engine dirty the skip can
        /// never fire and the whole exercise is undone silently, with everything still correct on
        /// screen -- which is why it is asserted rather than assumed.
        /// </summary>
        [Fact]
        public void RealizingAgainChangesNothing()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.Realize();
            sink.Engine.ClearDirty();

            sink.Engine.Realize();
            sink.Engine.Realize();

            Assert.False(sink.Engine.Dirty);
        }

        /// <summary>
        /// A scene painting a VisualBrush or DrawingBrush re-parses every frame by design -- the
        /// brush is rasterized after the parse loop, so its fill only resolves on the FOLLOWING frame
        /// -- so an unchanged command stream there does not mean an unchanged picture, and the sink
        /// must be told not to skip. Without content brushes there is nothing to hold it back.
        /// </summary>
        [Fact]
        public void APlainSceneReportsNoContentBrushes()
        {
            using var sink = NewSinkWithContent();
            sink.Engine.Realize();

            Assert.False(sink.Engine.HasContentBrushes);
        }
    }
}
