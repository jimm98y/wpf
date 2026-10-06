// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.VideoTest.
//
// MediaElement / VideoDrawing: the MILCMD_DRAW_VIDEO path, end to end.
//
// MediaElement.OnRender calls DrawingContext.DrawVideo(player, rect), which serializes to record
// 0x4b. That record carries only a HANDLE -- the pixels arrive out of band from the platform media
// backend through IMilRenderTargetSink.SendVideoFrame, and the decoder pairs the two up.
//
// The subtle half is not "does a frame draw" but "does the SECOND frame draw". A DrawVideo visual's
// render-data byte[] is IDENTICAL every frame; only the out-of-band pixels change. So the decoder's
// static-visual skip would happily keep sampling frame 1 forever, and the video would silently
// freeze while playback ran on -- no error, correct-looking first frame, wrong everything after.
// MilcoreEngine sets _parseTouchedContentBrush on every DrawVideo to force a re-parse.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class VideoDrawTests : RendererTestBase
    {
        public VideoDrawTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 48;
        private const uint HRoot = 1, HContent = 2, HPlayer = 3;

        /// <summary>Straight RGBA, as SetVideoFrame stores it; SendVideoFrame does the BGRA turn.</summary>
        private static byte[] Solid(int w, int h, byte r, byte g, byte b)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
            return px;
        }

        private static byte[] TopGreenBottomBlack(int w, int h)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = 0; px[i + 1] = (byte)(y < h / 2 ? 255 : 0); px[i + 2] = 0; px[i + 3] = 255;
                }
            return px;
        }

        /// <summary>An engine with a video visual whose rect is (8,8) 48x32, as MediaElement emits.</summary>
        private static MilcoreEngine VideoEngine()
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
            engine.CreateOrAddRef(HPlayer, MilResourceTypeId.MediaPlayer);

            byte[] content = MilCmd.DrawVideoRecord(HPlayer, 8, 8, 48, 32);
            engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
            engine.BeginCommand(MilCmd.RenderDataHeader(HContent, content.Length));
            engine.AppendCommandData(content);
            engine.EndCommand();
            engine.SubmitCommand(MilCmd.VisualSetContent(HRoot, HContent));
            return engine;
        }

        private Image RenderVideo(MilcoreEngine engine)
        {
            engine.Realize();
            SceneVisual? root = engine.VisualByHandle(HRoot);
            Assert.True(root is not null, "no root visual for the video scene");
            return new Image(NewRenderer().RenderToRgba(root!, W, H, White), W, H);
        }

        /// <summary>
        /// A MediaElement renders before the backend produces its first frame. That must leave the
        /// background alone -- not throw, and not paint garbage from an unset texture.
        /// </summary>
        [Fact]
        public void BeforeTheFirstFrame_NothingIsDrawn()
        {
            var img = RenderVideo(VideoEngine());
            img.AssertPixel(32, 24, 255, 255, 255, tol: 4, "nothing should be drawn before the first frame arrives");
        }

        /// <summary>The rect is 48x32 at (8,8), so (54,38) is inside and (60,44) is past it.</summary>
        [Fact]
        public void VideoFrame_FillsItsRectAndNoMore()
        {
            MilcoreEngine engine = VideoEngine();
            engine.SetVideoFrame(HPlayer, Solid(4, 4, 255, 0, 0), 4, 4);

            var img = RenderVideo(engine);
            img.AssertPixel(32, 24, 255, 0, 0, tol: 4, "the video frame paints inside the rect");
            img.AssertPixel(2, 2, 255, 255, 255, tol: 4, "outside the video rect is untouched");
            img.AssertPixel(54, 38, 255, 0, 0, tol: 4, "the frame is stretched to fill the whole rect");
            img.AssertPixel(60, 44, 255, 255, 255, tol: 4, "and does not spill past it");
        }

        /// <summary>
        /// THE freeze case. Nothing about the visual changes between frames -- same handle, same
        /// bytes, same tree -- so a decoder that skips unchanged visuals keeps sampling frame 1 while
        /// playback runs on.
        /// </summary>
        [Fact]
        public void SecondFrame_ReplacesTheFirst()
        {
            MilcoreEngine engine = VideoEngine();

            engine.SetVideoFrame(HPlayer, Solid(4, 4, 255, 0, 0), 4, 4);
            RenderVideo(engine);

            engine.SetVideoFrame(HPlayer, Solid(4, 4, 0, 0, 255), 4, 4);
            Rgb second = RenderVideo(engine)[32, 24];

            Assert.True(second.R < 40 && second.B > 200,
                $"the second frame should replace the first, was {second}. Red here means playback froze on frame 1.");
        }

        /// <summary>
        /// A SOLID frame cannot tell a vertical flip or a channel swap from correct output, so this
        /// uses a frame that differs top from bottom.
        /// </summary>
        [Fact]
        public void VideoFrame_IsUprightNotFlipped()
        {
            MilcoreEngine engine = VideoEngine();
            engine.SetVideoFrame(HPlayer, TopGreenBottomBlack(4, 4), 4, 4);

            var img = RenderVideo(engine);
            Rgb top = img[32, 14], bottom = img[32, 34];

            Assert.True(top.G > 200 && top.R < 40, $"the top of the frame should be green, was {top}");
            Assert.True(bottom.G < 40 && bottom.R < 40, $"the bottom should be black, was {bottom}");
        }
    }
}
