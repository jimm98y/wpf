// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// TextOptions.TextRenderingMode is honoured: Grayscale draws a run antialiased in grey (no colour fringes),
// Aliased draws every pixel in or out, ClearType (and the default) resolves the run onto the subpixels. It was
// read past: text an application asked to have in grey -- over a transparent or animated surface, where
// ClearType's fringes show -- was ClearType all the same.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class TextRenderingModeTests : RendererTestBase
    {
        public TextRenderingModeTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 200, H = 40;
        private const string Text = "Hamburgefonts";

        private byte[] Render(uint mode) => Render(mode, layered: false, hint: false);

        // layered: the run in a group at opacity 0.99 (two drawables, so a layer of its own); hint: ClearTypeHint Enabled
        private byte[] Render(uint mode, bool layered, bool hint)
        {
            TrueTypeFont font = TestFonts.Load();
            var indices = new ushort[Text.Length];
            var advances = new float[Text.Length];
            float em = 14f, advScale = em / font.PixelsPerEm;
            for (int i = 0; i < Text.Length; i++)
            {
                int gid = font.GlyphIndex(Text[i]);
                indices[i] = (ushort)gid;
                advances[i] = font.Advance(gid) * advScale;
            }
            var engine = new MilcoreEngine { FontResolver = _ => font, NaturalText = true };
            engine.CreateOrAddRef(1, MilResourceTypeId.Visual);
            if (mode != 0 || hint)
                engine.SubmitCommand(MilCmd.SetRenderOptions(1, flags: (mode != 0 ? 0x10u : 0u) | (hint ? 0x8u : 0u), edgeMode: 0, compositingMode: 0, bitmapScalingMode: 0,
                                                             clearTypeHint: hint ? 1u : 0u, textRenderingMode: mode, textHintingMode: 0));
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 0, 0, 0, 1));
            engine.CreateOrAddRef(20, MilResourceTypeId.Null);
            engine.BeginCommand(MilCmd.GlyphRun(20, 0, 6, 26, em, indices, advances));
            engine.EndCommand();
            SceneVisual scene = Realize(engine, MilCmd.DrawGlyphRunRecord(3, 20));
            if (layered)
            {
                scene.Content.Add(scene.Content[0]);
                scene.Opacity = 0.99;
            }
            WgpuSceneRenderer renderer = NewRenderer();
            renderer.ClearType = true;
            return renderer.RenderToRgba(scene, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
        }

        private static (int Fringe, int Grey, int Ink) Measure(byte[] px)
        {
            int fringe = 0, grey = 0, ink = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                int r = px[i], g = px[i + 1], b = px[i + 2];
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) fringe++;
                int lum = (r + g + b) / 3;
                if (lum < 250) ink++;
                if (lum > 30 && lum < 225) grey++;
            }
            return (fringe, grey, ink);
        }

        [Fact]
        public void ClearType_HasColourFringes()
        {
            (int fringe, _, int ink) = Measure(Render(3));
            Assert.True(ink > 50, "nothing was drawn");
            Assert.True(fringe > 10, $"ClearType text resolves onto the subpixels: {fringe} coloured pixels");
        }

        [Fact]
        public void Grayscale_HasNoColourButIsAntialiased()
        {
            (int fringe, int grey, int ink) = Measure(Render(2));
            Assert.True(ink > 50, "nothing was drawn");
            Assert.True(fringe == 0, $"greyscale text has no colour: {fringe} coloured pixels");
            Assert.True(grey > 10, $"...and is antialiased: {grey} part-covered pixels");
        }

        [Fact]
        public void Aliased_HasEveryPixelInOrOut()
        {
            (int fringe, int grey, int ink) = Measure(Render(1));
            Assert.True(ink > 50, "nothing was drawn");
            Assert.True(fringe == 0 && grey == 0, $"aliased text is black or white: {fringe} coloured, {grey} grey pixels");
        }

        // ClearType is wpfgfx's for an opaque target: text in a layer WPF composites with transparency (a group opacity,
        // an opacity mask, an effect) is grey, unless RenderOptions.ClearTypeHint = Enabled keeps it ClearType
        [Fact]
        public void Text_in_a_transparent_layer_is_grey_unless_ClearTypeHint_says()
        {
            (int fringe, int grey, int ink) = Measure(Render(0, layered: true, hint: false));
            Assert.True(ink > 50, "nothing was drawn");
            Assert.True(fringe == 0 && grey > 10, $"text in an opacity layer is grey: {fringe} coloured pixels");
            (int hinted, _, _) = Measure(Render(0, layered: true, hint: true));
            Assert.True(hinted > 10, $"ClearTypeHint Enabled keeps it ClearType: {hinted} coloured pixels");
        }
    }
}
