// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+ text presented at another scale than it was realized at -- a system-aware application's window on a
// display of another DPI -- is the pixels GDI+ made, stretched, as Windows stretches such a window. It was
// drawn again from the run's string: each run of a trimmed caption ("Exp" and its ellipsis) carries the whole
// caption, so DotNetBar's narrow dock tabs showed every caption whole, over each other.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;
using GdiPlusText = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText;

namespace WgpuInterop.Tests.Text
{
    public sealed class GdiPlusTextOtherScaleTests : RendererTestBase
    {
        public GdiPlusTextOtherScaleTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 120, H = 40;

        [Fact]
        public void APrecomposedRunAtHalfScale_IsItsPixelsHalved_NotItsString()
        {
            // The visible part of a trimmed caption: a 20 x 10 block of full coverage at (10, 10).
            var lv = new GdiPlusText.Levels { Grey = true, Left = 10, Top = 10, Width = 20, Height = 10, Index = new byte[200] };
            for (int i = 0; i < lv.Index.Length; i++) lv.Index[i] = 15;
            var run = new GdiPlusText.Run { Pre = lv, Mode = 4, Em = 24f, OriginX = 10, OriginY = 20 };
            const string caption = "Explorer Explorer Explorer";
            var fallback = new GlyphRunDraw(caption, new Vector2(run.OriginX, run.OriginY), run.Em, RgbaColor.FromBytes(0, 0, 0, 255), 0, "Segoe UI");

            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, W, H)), RgbaColor.FromBytes(255, 255, 255, 255)));
            var text = new SceneVisual { Transform = Matrix3x2.CreateScale(0.5f) };
            text.Content.Add(new GdiPlusTextDraw(run, "Segoe UI", 0, unchecked((int)0xFF000000), fallback));
            root.Children.Add(text);

            byte[] px = Render(root, W, H);
            int G(int x, int y) => px[(y * W + x) * 4 + 1];
            Assert.True(G(10, 7) < 64, $"the block, halved, is inked at its middle (green {G(10, 7)})");
            for (int y = 0; y < H; y++)
                for (int x = 17; x < W; x++)
                    Assert.True(G(x, y) > 240, $"nothing past the halved block: ({x},{y}) green {G(x, y)}");
        }
    }
}
