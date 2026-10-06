// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's own text and a WinForms string run share one renderer, and they must not share its
// per-run configuration.
//
// A string run (GlyphRunDraw -- WinForms text) sets up the rasterizer for its face and size: GDI's
// symmetric rows, dropout control, the simulated-bold smear, the contrast palette. WPF's text never
// comes through that path; it arrives as glyph OUTLINES. It used to be rasterized with whatever the
// last string run had left behind, so in an app hosting WinForms controls WPF text changed with the
// WinForms text drawn before it -- and where that was a size GDI samples once per pixel row, a WPF
// horizontal thinner than a pixel that fell between two row centres simply vanished. In the gallery
// that was the top bars of "File" and "Edit" in a WPF menu.
//
// And the paper under WinForms text: an analytic rounded rectangle (fs_shape) is found as paper just
// like a plain rectangle, so the text on it blends exactly (WgpuSceneRenderer.PaperUnder).
//

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class WpfGlyphConfigTests : RendererTestBase
    {
        public WpfGlyphConfigTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 200, H = 60;

        private TrueTypeFont UiFont()
        {
            string? file = FontFiles.Find("Segoe UI", bold: false, italic: false);
            Assert.SkipWhen(file is null, "this machine has no Segoe UI");
            return new TrueTypeFont(File.ReadAllBytes(file!));
        }

        private static PathFigure Box(float x0, float y0, float x1, float y1)
        {
            var f = new PathFigure(new Vector2(x0, y0));
            f.Segments.Add(new LineSegment(new Vector2(x1, y0)));
            f.Segments.Add(new LineSegment(new Vector2(x1, y1)));
            f.Segments.Add(new LineSegment(new Vector2(x0, y1)));
            return f;
        }

        /// <summary>An "F" the way WPF hands one over: an outline, flagged as a glyph, not fitted to
        /// the grid. Its top bar is three quarters of a pixel thick and sits between the centres of
        /// rows 40 and 41 (40.6 to 41.35), so one sample per row finds nothing in it.</summary>
        private static GeometryFill WpfF()
            => new GeometryFill(new PathGeometry(FillRule.NonZero, new List<PathFigure>
               {
                   Box(20f, 40.6f, 21.4f, 49f),       // stem
                   Box(21.4f, 40.6f, 27f, 41.35f),    // top bar
                   Box(21.4f, 44.6f, 26f, 45.35f),    // middle bar
               }), new SolidColorBrush(RgbaColor.FromBytes(0, 0, 0, 255)), isGlyph: true,
               baselineAnchor: new Vector2(20f, 49f));

        private static int InkIn(byte[] rgba, int x0, int y0, int x1, int y1)
        {
            int ink = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    for (int c = 0; c < 3; c++)
                        ink += 255 - rgba[(y * W + x) * 4 + c];
            return ink;
        }

        [Fact]
        public void AWpfGlyphsThinBar_BetweenTwoRowCentres_IsStillDrawn()
        {
            var root = new SceneVisual();
            root.Content.Add(WpfF());
            byte[] px = NewRenderer(UiFont()).RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            // The top bar's columns right of the stem, over the two rows it straddles.
            Assert.True(InkIn(px, 23, 40, 26, 42) > 255,
                        "the top bar of a WPF glyph vanished: it was sampled once per pixel row");
        }

        [Fact]
        public void AWpfGlyph_IsTheSame_WhateverStringRunCameBeforeIt()
        {
            // Segoe UI's gasp asks for symmetric smoothing at 22ppem and not at 13, so the two
            // string runs leave the renderer configured two different ways.
            byte[] After(float ppem)
            {
                var root = new SceneVisual();
                root.Content.Add(new GlyphRunDraw("Menu", new Vector2(60, 30), ppem, RgbaColor.FromBytes(0, 0, 0, 255)));
                root.Content.Add(WpfF());
                return NewRenderer(UiFont()).RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            }
            byte[] a = After(13f), b = After(22f);
            for (int y = 36; y < 54; y++)
                for (int x = 14; x < 34; x++)
                    for (int c = 0; c < 4; c++)
                        Assert.True(a[(y * W + x) * 4 + c] == b[(y * W + x) * 4 + c],
                                    $"the WPF glyph at ({x},{y}) changed with the string run drawn before it");
        }

        /// <summary>Mid-tone ink on mid-tone paper, where only the exact (paper-known) blend and the
        /// fallback disagree.</summary>
        private byte[] StringRunOn(params Microsoft.Wpf.Interop.WebGpu.Composition.Geometry[] paper)
        {
            var root = new SceneVisual();
            var colour = new SolidColorBrush(RgbaColor.FromBytes(0x3C, 0x3C, 0x3C, 255));
            foreach (var g in paper) root.Content.Add(new GeometryFill(g, colour));
            root.Content.Add(new GlyphRunDraw("Handgloves", new Vector2(24, 36), 13f,
                                              RgbaColor.FromBytes(0xD4, 0xD4, 0xD4, 255)));
            WgpuSceneRenderer r = NewRenderer(UiFont());
            r.TextBlendCorrection = true;
            return r.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
        }

        [Fact]
        public void TextOnARoundedRectangle_BlendsAsOnARectangleOfTheSameColour()
        {
            byte[] rect = StringRunOn(new RectangleGeometry(new Rect(4, 4, 190, 52)));
            byte[] round = StringRunOn(new RoundedRectangleGeometry(new Rect(4, 4, 190, 52), 8, 8));
            // The same paper, but as two halves neither of which contains the run: unknown, so
            // the fallback blend. Proves the comparison above can fail.
            byte[] halves = StringRunOn(new RectangleGeometry(new Rect(4, 4, 46, 52)),
                                        new RectangleGeometry(new Rect(50, 4, 144, 52)));
            int fallbackDiffers = 0;
            for (int y = 20; y < 44; y++)
                for (int x = 20; x < 180; x++)
                {
                    int i = (y * W + x) * 4;
                    bool eq = rect[i] == round[i] && rect[i + 1] == round[i + 1] && rect[i + 2] == round[i + 2];
                    Assert.True(eq, $"text on the rounded rectangle differs from text on the rectangle at ({x},{y})");
                    if (rect[i] != halves[i] || rect[i + 1] != halves[i + 1] || rect[i + 2] != halves[i + 2])
                        fallbackDiffers++;
                }
            Assert.True(fallbackDiffers > 0, "the fallback blend matched the exact one, so this test proves nothing");
        }
    }
}
