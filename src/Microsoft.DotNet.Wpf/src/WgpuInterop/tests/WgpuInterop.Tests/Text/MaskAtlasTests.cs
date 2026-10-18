// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The ClearType masks share one atlas texture (WgpuSceneRenderer.MaskAtlas). What comes out has to
// be what the one-texture-per-mask path draws, byte for byte; masks too big for it, and masks that
// arrive once it is full, still draw; and a full atlas is emptied between frames, not during one.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class MaskAtlasTests : RendererTestBase
    {
        public MaskAtlasTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 640, H = 480;

        private static GdiPlusTextDraw? Caption(string text, float x, float y, float size, int argb, float layoutWidth = 620)
        {
            var face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face("Segoe UI", 0);
            if (face is null) return null;
            var run = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Layout(face, "Segoe UI", size, text, x, y, layoutWidth, 400,
                                                                                        0, false, 0, 0, false, 5);
            if (run is null) return null;
            run.Contrast = 4;
            var fallback = new GlyphRunDraw(text, new Vector2(run.OriginX, run.OriginY), run.Em, RgbaColor.FromBytes(0, 0, 0, 255), 0, "Segoe UI");
            return new GdiPlusTextDraw(run, "Segoe UI", 0, argb, fallback);
        }

        /// <summary>Captions in several colours on several papers, WPF ClearType text beside them, and
        /// one caption far bigger than the atlas takes.</summary>
        private static SceneVisual Scene(int variant, bool huge)
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, W, H)), RgbaColor.FromBytes(255, 255, 255, 255)));
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 200, W, 120)), RgbaColor.FromBytes(220, 230, 245, 255)));
            for (int i = 0; i < 24; i++)
            {
                var t = new SceneVisual();
                GdiPlusTextDraw? c = Caption($"Caption {i + variant} Ag", 8 + (i % 4) * 155, 8 + (i / 4) * 34, 9f + (i % 3),
                    unchecked((int)(0xFF000000 | (uint)((i * 40) % 200) << 16 | 0x2040)));
                Assert.SkipWhen(c is null, "Segoe UI is not installed");
                t.Content.Add(c!);
                root.Children.Add(t);
            }
            var wpf = new SceneVisual();
            wpf.Content.Add(new GlyphRunDraw("WPF ClearType text beside the captions " + variant, new Vector2(12, 360), 14f, RgbaColor.FromBytes(30, 30, 90, 255)));
            root.Children.Add(wpf);
            if (huge)
            {
                var big = new SceneVisual();
                // wider than MaskAtlasMaxSide (512): its mask is a texture of its own
                GdiPlusTextDraw? c = Caption("A caption wider than the atlas takes", 4, 400, 40f, unchecked((int)0xFF103080), 2000);
                Assert.SkipWhen(c is null, "Segoe UI is not installed");
                big.Content.Add(c!);
                root.Children.Add(big);
            }
            return root;
        }

        /// <summary>One frame, ended as a presenter ends it (EndFrame is where a full atlas is emptied).</summary>
        private static byte[] Frame(WgpuSceneRenderer r, SceneVisual scene)
        {
            r.BeginFrame();
            try { return r.RenderToRgba(scene, W, H, White); }
            finally { r.EndFrame(); }
        }

        private byte[] RenderWith(bool atlas, SceneVisual scene)
        {
            bool saved = WgpuSceneRenderer.MaskAtlasEnabled;
            WgpuSceneRenderer.MaskAtlasEnabled = atlas;
            try
            {
                using WgpuSceneRenderer r = NewRenderer(TestFonts.Load());
                return Frame(r, scene);
            }
            finally { WgpuSceneRenderer.MaskAtlasEnabled = saved; }
        }

        [Fact]
        public void TextFromTheAtlas_IsTheSamePixelsAsFromTexturesOfItsOwn()
        {
            long masks0 = WgpuSceneRenderer.PerfAtlasMasks;
            byte[] withAtlas = RenderWith(true, Scene(0, huge: false));
            Assert.True(WgpuSceneRenderer.PerfAtlasMasks > masks0, "nothing went into the atlas");
            byte[] without = RenderWith(false, Scene(0, huge: false));
            Assert.Equal(without, withAtlas);
        }

        [Fact]
        public void AMaskTooBigForTheAtlas_HasATextureOfItsOwn_AndTheSamePixels()
        {
            SceneVisual scene = Scene(0, huge: true);
            bool saved = WgpuSceneRenderer.MaskAtlasEnabled;
            WgpuSceneRenderer.MaskAtlasEnabled = true;
            byte[] withAtlas;
            try
            {
                using WgpuSceneRenderer r = NewRenderer(TestFonts.Load());
                long fallbacks0 = WgpuSceneRenderer.PerfAtlasFallbacks, resets0 = WgpuSceneRenderer.PerfAtlasResets;
                withAtlas = Frame(r, scene);
                // Too big is not full: the atlas is not emptied for it.
                Assert.Equal(fallbacks0, WgpuSceneRenderer.PerfAtlasFallbacks);
                withAtlas = Frame(r, scene);
                Assert.Equal(resets0, WgpuSceneRenderer.PerfAtlasResets);
            }
            finally { WgpuSceneRenderer.MaskAtlasEnabled = saved; }
            Assert.Equal(RenderWith(false, scene), withAtlas);
        }

        [Fact]
        public void AFullAtlas_IsEmptiedBetweenFrames_AndEveryFrameIsExact()
        {
            bool saved = WgpuSceneRenderer.MaskAtlasEnabled;
            WgpuSceneRenderer.MaskAtlasEnabled = true;
            try
            {
                using WgpuSceneRenderer r = NewRenderer(TestFonts.Load());
                long resets0 = WgpuSceneRenderer.PerfAtlasResets, fallbacks0 = WgpuSceneRenderer.PerfAtlasFallbacks;
                // Every frame new captions (new masks), until the atlas has filled and been emptied.
                int frame = 0;
                for (; frame < 400 && WgpuSceneRenderer.PerfAtlasResets == resets0; frame++)
                {
                    SceneVisual scene = Scene(frame * 24, huge: false);
                    byte[] got = Frame(r, scene);
                    if (frame % 25 == 0 || WgpuSceneRenderer.PerfAtlasFallbacks != fallbacks0)
                        Assert.Equal(RenderWith(false, scene), got);
                }
                Assert.True(WgpuSceneRenderer.PerfAtlasFallbacks > fallbacks0, "the atlas never filled");
                Assert.True(WgpuSceneRenderer.PerfAtlasResets > resets0, $"a full atlas was not emptied ({frame} frames)");
                // After it was emptied, masks go into it again, and come out exact.
                long masks0 = WgpuSceneRenderer.PerfAtlasMasks;
                SceneVisual after = Scene(100_000, huge: false);
                byte[] again = Frame(r, after);
                Assert.True(WgpuSceneRenderer.PerfAtlasMasks > masks0, "nothing went into the emptied atlas");
                Assert.Equal(RenderWith(false, after), again);
            }
            finally { WgpuSceneRenderer.MaskAtlasEnabled = saved; }
        }
    }
}
