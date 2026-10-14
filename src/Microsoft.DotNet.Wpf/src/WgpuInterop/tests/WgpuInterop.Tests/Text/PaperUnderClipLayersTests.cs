// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Text over a clip layer whose content leaves the text's box alone -- a layer whose only draw is a
// nested clip layer with nothing there -- sits on the paper UNDER the layer. It used to be taken for
// unknown paper and blended as plain coverage: DotNetBar's ribbon tab captions (a strip clipped
// inside the ribbon's own clip) came out a level darker than stock on every partly lit subpixel.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class PaperUnderClipLayersTests : RendererTestBase
    {
        public PaperUnderClipLayersTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 160, H = 40;

        private static PathGeometry Box(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        private static SceneVisual Scene(bool layers)
        {
            var root = new SceneVisual();
            root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, W, H)), RgbaColor.FromBytes(255, 255, 255, 255)));
            if (layers)
            {
                var outer = new SceneVisual { ClipGeometry = Box(1, 1, W - 2, H - 2) };
                var inner = new SceneVisual { ClipGeometry = Box(2, 2, W - 4, H - 4) };
                // Drawn, but far from the text.
                inner.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(W - 12, 2, 8, 8)), RgbaColor.FromBytes(200, 30, 30, 255)));
                outer.Children.Add(inner);
                root.Children.Add(outer);
            }
            var text = new SceneVisual();
            // GDI+'s DrawString, ClearType (TextRenderingHint 5), as DotNetBar draws its captions.
            var face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face("Segoe UI", 0);
            Assert.SkipWhen(face is null, "Segoe UI is not installed");
            var run = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Layout(face!, "Segoe UI", 9f, "Dynamics", 8, 10, 140, 24,
                                                                                        0, false, 0, 0, false, 5)!;
            run.Contrast = 4;
            var fallback = new GlyphRunDraw("Dynamics", new Vector2(run.OriginX, run.OriginY), run.Em, RgbaColor.FromBytes(102, 102, 102, 255), 0, "Segoe UI");
            text.Content.Add(new GdiPlusTextDraw(run, "Segoe UI", 0, unchecked((int)0xFF666666), fallback));
            root.Children.Add(text);
            return root;
        }

        [Fact]
        public void TextOverAClipLayerThatLeavesItsBoxAlone_BlendsAsOnThePaperBelow()
        {
            byte[] plain = Render(Scene(false), W, H);
            byte[] layered = Render(Scene(true), W, H);
            int differ = 0;
            for (int y = 12; y < 34; y++)
                for (int x = 6; x < 100; x++)
                {
                    int i = (y * W + x) * 4;
                    if (plain[i] != layered[i] || plain[i + 1] != layered[i + 1] || plain[i + 2] != layered[i + 2]) differ++;
                }
            Assert.Equal(0, differ);
        }
    }
}
