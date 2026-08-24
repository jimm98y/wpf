// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a frame of text costs in RENDER PASSES, and why the second one costs nothing.
//
// Text does not go through the glyph atlas on any real font. EmitText prefers crisp outline
// coverage -- the analytic-AA path WPF's own glyph fills take -- because the atlas rasterizes at a
// fixed 48px and minifies, which aliases small runs. So each glyph becomes a coverage mask in its
// own R8 texture, and each of those is a render pass.
//
// That is one pass per distinct glyph on the frame that first shows a string, which sounds alarming
// and is measured here so that it stays honest. What makes it acceptable is the second measurement:
// the per-shape coverage cache serves every one of those masks on every later frame, so a page of
// static text settles to the passes needed to composite it and no more. The cache is the thing
// carrying text performance, which is why it is worth a test that fails if it stops.
//
// A mask per glyph is also what makes the cache work at all: the same letter at the same size is one
// entry however many words it appears in, where a mask per RUN would be a new entry per string.
//

using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class TextPassCostTests : RendererTestBase
    {
        public TextPassCostTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 640, H = 64;
        private const float EmSize = 28f;
        private const float OriginX = 8f, OriginY = 44f;

        // Distinct letters: a repeat would already be in the cache and rasterize nothing.
        private const string Text = "abcdefghijklmnopqrstuvwxyz";

        private SceneVisual BuildRun(TrueTypeFont font, out int glyphs)
        {
            var indices = new ushort[Text.Length];
            var advances = new float[Text.Length];
            float advScale = EmSize / font.PixelsPerEm;
            glyphs = 0;
            for (int i = 0; i < Text.Length; i++)
            {
                int gid = font.GlyphIndex(Text[i]);
                indices[i] = (ushort)gid;
                advances[i] = font.Advance(gid) * advScale;
                if (font.TryGetGlyphOutline(gid, out _)) glyphs++;
            }

            var engine = new MilcoreEngine { FontResolver = _ => font };
            engine.CreateOrAddRef(1, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));
            engine.CreateOrAddRef(20, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(20, 0, OriginX, OriginY, EmSize, indices, advances));
            engine.EndCommand();
            return Realize(engine, MilCmd.DrawGlyphRunRecord(3, 20));
        }

        /// <summary>
        /// The same renderer, twice over the same scene. The first frame pays for the masks; the
        /// second must pay for none of them.
        /// </summary>
        [Fact]
        public void TheSecondFrameOfTheSameTextCostsNoMaskPasses()
        {
            TrueTypeFont font = TestFonts.Load();
            SceneVisual scene = BuildRun(font, out int glyphs);
            Assert.True(glyphs > 8, "the test font mapped too few of these letters to be worth measuring");

            WgpuSceneRenderer renderer = NewRenderer();

            WgpuSceneRenderer.PerfReset();
            renderer.RenderToRgba(scene, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            int first = WgpuSceneRenderer.PerfPasses;

            WgpuSceneRenderer.PerfReset();
            renderer.RenderToRgba(scene, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            int second = WgpuSceneRenderer.PerfPasses;

            // First frame: a mask apiece, plus the pass that composites them.
            Assert.True(first > glyphs,
                $"a first frame of {glyphs} new glyphs took only {first} passes; the mask-per-glyph " +
                "shape this test describes has changed and its premise needs rechecking");

            // Second frame: the coverage cache answers every one of them.
            Assert.True(second * 4 < first,
                $"a repeat frame of the same text took {second} passes against the first frame's " +
                $"{first}. The per-shape coverage cache is what makes text affordable here, and it " +
                "looks like it has stopped serving these glyphs.");
        }
    }
}
