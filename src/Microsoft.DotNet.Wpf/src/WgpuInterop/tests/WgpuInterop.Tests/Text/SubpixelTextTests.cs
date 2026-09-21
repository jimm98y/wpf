// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Subpixel text: that it happens, and that it STOPS happening where it must.
//
// The parity suite next door measures how closely our glyphs land on Windows'. This one is about the
// switch itself, because the three conditions ClearType is gated on are the three places it does
// active harm if it leaks through, and none of them is visible in a parity number:
//
//   * A ROTATED glyph. The three lamps are laid out along the screen's x axis, so subpixel coverage
//     is a statement about that axis and nothing else. Turned on its side it is not a sharper glyph,
//     it is a coloured one.
//   * A TRANSPARENT target. Subpixel coverage says how much of a lamp a known background shows
//     through. Composited into something that is itself see-through, the fringe surfaces later
//     against whatever the layer lands on, in colours nobody chose. WPF drops ClearType on layered
//     windows for the same reason.
//   * TEXT THAT IS NOT ON THE PIXEL GRID gains nothing from three times the horizontal resolution
//     and picks up a fringe that crawls as it scrolls.
//
// The test for all three is the same: does the ink carry COLOUR. Grey text has none by construction
// -- every lamp of a pixel gets the same coverage -- so a channel spread is proof of subpixel
// rendering, and its absence is proof of the fallback.
//

using System;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class SubpixelTextTests : RendererTestBase
    {
        public SubpixelTextTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 260, H = 44;
        private const int Ppem = 14;

        /// <summary>How many pixels carry a colour, and how far apart the lamps get at the widest.
        /// <para>A tolerance of eight, not zero: the renderer works in bytes and a rounding of one
        /// between channels is not a fringe.</para></summary>
        private static (int Fringed, int Widest) Colour(byte[] rgba)
        {
            int fringed = 0, widest = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                int spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                if (spread > 8) fringed++;
                if (spread > widest) widest = spread;
            }
            return (fringed, widest);
        }

        private static SceneVisual Run(Matrix3x2? transform = null)
        {
            var root = new SceneVisual();
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("Handgloves", new Vector2(8, 30), Ppem,
                                              RgbaColor.FromBytes(0, 0, 0, 255)));
            if (transform is Matrix3x2 m) text.Transform = m;
            root.Children.Add(text);
            return root;
        }

        private TrueTypeFont UiFont()
        {
            string? file = FontFiles.Find("Segoe UI", bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");
            return new TrueTypeFont(File.ReadAllBytes(file!));
        }

        [Fact]
        public void OrdinaryText_IsDrawnOnTheThreeLamps()
        {
            byte[] px = NewRenderer(UiFont())
                .RenderToRgba(Run(), W, H, RgbaColor.FromBytes(255, 255, 255, 255));

            (int fringed, int widest) = Colour(px);
            Assert.True(fringed > 100,
                        $"only {fringed} pixels carry any colour (widest spread {widest}); this text "
                        + "is not being resolved onto the subpixels at all");
        }

        [Fact]
        public void RotatedText_FallsBackToGrey()
        {
            // Eleven degrees: enough that no lamp of the glyph lines up with a lamp of the screen,
            // and little enough that the run stays inside the bitmap.
            Matrix3x2 tilt = Matrix3x2.CreateRotation(0.19f, new Vector2(8, 30));
            byte[] px = NewRenderer(UiFont())
                .RenderToRgba(Run(tilt), W, H, RgbaColor.FromBytes(255, 255, 255, 255));

            (int fringed, int widest) = Colour(px);
            Assert.True(fringed == 0,
                        $"{fringed} pixels of rotated text carry colour (widest spread {widest}). The "
                        + "lamps run along the SCREEN's x axis, so a turned glyph cannot be resolved "
                        + "onto them -- it just comes out coloured.");
        }

        [Fact]
        public void TextOnATransparentTarget_FallsBackToGrey()
        {
            // A transparent target is what a layered window renders into, and it is the case WPF
            // itself turns ClearType off for.
            byte[] px = NewRenderer(UiFont()).RenderToRgba(
                Run(), W, H, RgbaColor.FromBytes(255, 255, 255, 255), transparentTarget: true);

            (int fringed, int widest) = Colour(px);
            Assert.True(fringed == 0,
                        $"{fringed} pixels carry colour on a transparent target (widest spread "
                        + $"{widest}). The fringe would surface against whatever the layer lands on.");
        }
    }
}
