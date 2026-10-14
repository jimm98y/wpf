// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ClearType text inside a translucent group (a visual at opacity < 1 with more than one thing in it,
// which renders through a layer). wpfgfx draws such a group over a copy of what is under it and
// lerps that copy back in, so each channel of each pixel ends up between the paper and the text drawn
// on it at full opacity. The layer used to be composited by its one alpha -- the mean of the three
// coverages -- which took the paper out by that mean in every channel: white text on grey grew
// fringes darker than the paper (GraphWorX's licence notice), black text went grey.
//

using System;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class OpacityLayerSubpixelTests : RendererTestBase
    {
        public OpacityLayerSubpixelTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 160, H = 40;
        private static readonly RgbaColor Paper = RgbaColor.FromBytes(211, 211, 211, 255);

        private static SceneVisual Text(int argb)
        {
            var face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face("Segoe UI", 0);
            Assert.SkipWhen(face is null, "Segoe UI is not installed");
            var run = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Layout(face!, "Segoe UI", 12f, "Notification", 8, 6, 150, 30,
                                                                                        0, false, 0, 0, false, 5)!;
            run.Contrast = 4;
            var ink = RgbaColor.FromBytes((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255);
            var fallback = new GlyphRunDraw("Notification", new Vector2(run.OriginX, run.OriginY), run.Em, ink, 0, "Segoe UI");
            var v = new SceneVisual();
            v.Content.Add(new GdiPlusTextDraw(run, "Segoe UI", 0, argb, fallback));
            return v;
        }

        private static SceneVisual Scene(int argb, double groupOpacity)
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, W, H)), Paper));
            if (groupOpacity >= 1.0)
            {
                root.Children.Add(Text(argb));
                return root;
            }
            // Two things in the group, so it is a layer: the text and a mark far from it.
            var group = new SceneVisual { Opacity = groupOpacity };
            group.Children.Add(Text(argb));
            var mark = new SceneVisual();
            mark.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(W - 6, H - 6, 4, 4)), RgbaColor.FromBytes(0, 0, 0, 255)));
            group.Children.Add(mark);
            root.Children.Add(group);
            return root;
        }

        [Theory]
        [InlineData(unchecked((int)0xFFFFFFFF))]   // white ink: the fringes went darker than the paper
        [InlineData(unchecked((int)0xFF000000))]   // black ink: the text went grey
        [InlineData(unchecked((int)0xFFFF0000))]
        public void EachChannelIsTheLerpOfThePaperAndTheOpaqueText(int argb)
        {
            byte[] opaque = Render(Scene(argb, 1.0), W, H);
            byte[] half = Render(Scene(argb, 0.5), W, H);
            int inked = 0, worst = 0;
            for (int y = 0; y < H - 8; y++)
                for (int x = 0; x < W - 8; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = (y * W + x) * 4 + c;
                        const int p = 211;
                        if (opaque[i] != p) inked++;
                        float want = 0.5f * (opaque[i] + p);
                        worst = Math.Max(worst, (int)MathF.Ceiling(MathF.Abs(half[i] - want)));
                    }
            Assert.True(inked > 50, $"the text drew nothing ({inked} channels inked)");
            Assert.True(worst <= 1, $"a channel is {worst} levels from the lerp of the paper and the opaque text");
        }
    }
}
