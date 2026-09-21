// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Anything that changes a layer's pixels must change its cache key.
//
// A visual with an effect, a clip geometry, an opacity mask or a group opacity is rendered once into
// a texture and that texture is reused for as long as its key matches. The key is a hash over the
// subtree's transforms, geometry and brushes -- so any render input MISSING from that hash is
// invisible to it: the key matches, the stale texture is composited, and the layer keeps showing the
// old picture. It does not present as a caching bug. It presents as content that has frozen, or that
// animates at some fraction of the frame rate while everything around it is smooth.
//
// These are all the same shape: render a scene through a cached layer, change exactly ONE property,
// render again with the SAME renderer (so the cache is live between the two), and require the image
// to change. A property that is missing from the key fails here and only here -- rendering either
// scene on its own is perfectly correct, which is why this class of bug survives normal pixel tests.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Lifecycle
{
    public sealed class LayerCacheKeyTests : RendererTestBase
    {
        public LayerCacheKeyTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 96;

        /// <summary>
        /// Wraps content in a visual that is guaranteed to become a CACHED layer: a group opacity
        /// below 1 over more than one drawable is what CollectVisual's needsLayer keys off.
        /// </summary>
        private static SceneVisual Layered(params DrawingPrimitive[] content)
        {
            var root = new SceneVisual();
            var layer = new SceneVisual { Opacity = 0.9 };
            foreach (DrawingPrimitive p in content) layer.Content.Add(p);
            // A second drawable, unchanging, so the opacity actually forces a layer.
            layer.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(2, 2, 6, 6)),
                new SolidColorBrush(RgbaColor.FromBytes(10, 10, 10, 255))));
            root.Children.Add(layer);
            return root;
        }

        /// <summary>
        /// How many pixels differ between the two scenes, rendered in order through ONE renderer so
        /// the first render's layer bake is in the cache when the second runs.
        /// </summary>
        private int PixelsChangedBetween(SceneVisual first, SceneVisual second)
        {
            WgpuSceneRenderer renderer = NewRenderer();
            var a = new Image(renderer.RenderToRgba(first, W, H, White), W, H);
            var b = new Image(renderer.RenderToRgba(second, W, H, White), W, H);

            int changed = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (Math.Abs(a[x, y].R - b[x, y].R) > 6 || Math.Abs(a[x, y].G - b[x, y].G) > 6 ||
                        Math.Abs(a[x, y].B - b[x, y].B) > 6)
                        changed++;
            return changed;
        }

        private void AssertChangeIsVisible(SceneVisual first, SceneVisual second, string what)
        {
            int changed = PixelsChangedBetween(first, second);
            Assert.True(changed > 20,
                $"{what}: only {changed} pixels changed, so the second frame reused the first frame's " +
                "layer bake -- this property is missing from the layer cache key, and content that " +
                "animates it will render frozen inside any cached layer");
        }

        private static PathGeometry Line(float x0, float y0, float x1, float y1)
        {
            var fig = new PathFigure(new Vector2(x0, y0)) { Closed = false };
            fig.Segments.Add(new LineSegment(new Vector2(x1, y1)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
        }

        private static SolidColorBrush Ink => new(RgbaColor.FromBytes(20, 40, 200, 255));

        /// <summary>
        /// The dash PHASE, which is what "marching ants" and indeterminate progress indicators
        /// animate -- nothing else about the drawing moves, so if the phase is not in the key the
        /// whole animation is one static frame.
        /// </summary>
        [Fact]
        public void ADashOffsetChangeIsVisible()
        {
            SceneVisual Dashed(double offset) => Layered(new GeometryStroke(
                Line(12, 48, 84, 48), Ink,
                new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10.0, new[] { 10.0, 10.0 }, offset)));

            AssertChangeIsVisible(Dashed(0), Dashed(10), "dash offset");
        }

        [Fact]
        public void ADashPatternChangeIsVisible()
        {
            SceneVisual Dashed(double[] pattern) => Layered(new GeometryStroke(
                Line(12, 48, 84, 48), Ink,
                new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10.0, pattern)));

            AssertChangeIsVisible(Dashed(new[] { 10.0, 10.0 }), Dashed(new[] { 30.0, 4.0 }), "dash pattern");
        }

        /// <summary>Solid to dashed: the most visible pen change there is.</summary>
        [Fact]
        public void GainingADashPatternIsVisible()
        {
            SceneVisual Stroked(double[]? pattern) => Layered(new GeometryStroke(
                Line(12, 48, 84, 48), Ink,
                new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10.0, pattern)));

            AssertChangeIsVisible(Stroked(null), Stroked(new[] { 8.0, 8.0 }), "gaining a dash pattern");
        }

        /// <summary>
        /// Text whose colour animates -- a readout that turns red past a threshold, a label fading in.
        /// The run's text, position and size are all unchanged, so the colour is the only thing that
        /// can move the key.
        /// </summary>
        [Fact]
        public void AGlyphRunColourChangeIsVisible()
        {
            SceneVisual Run(RgbaColor colour) => Layered(
                new GlyphRunDraw("MMMMMMMM", new Vector2(8, 56), 28f, colour));

            // The run has to actually paint something before its colour can be asserted on: if this
            // renderer has no glyph source the text is silently nothing, and the comparison below
            // would then "pass" or "fail" for reasons that have nothing to do with the cache key.
            var alone = new Image(NewRenderer().RenderToRgba(Run(RgbaColor.FromBytes(20, 20, 20, 255)), W, H, White), W, H);
            // Only where the TEXT is: the helper's own little rect sits at (2,2) and would otherwise
            // satisfy this on its own, which is exactly how a "does it render" check gives a false yes.
            int inked = 0;
            for (int y = 30; y < H; y++)
                for (int x = 12; x < W; x++)
                    if (alone[x, y].R < 200) inked++;
            Assert.SkipWhen(inked < 20, "this renderer draws no glyphs, so glyph colour cannot be asserted here");

            AssertChangeIsVisible(
                Run(RgbaColor.FromBytes(20, 20, 20, 255)),
                Run(RgbaColor.FromBytes(230, 30, 30, 255)),
                "glyph run colour");
        }

        /// <summary>
        /// A stroke's CAP. A thick line with butt versus square caps differs at both ends by half the
        /// thickness -- small, but it is real content and the key has to see it.
        /// </summary>
        [Fact]
        public void AStrokeCapChangeIsVisible()
        {
            SceneVisual Capped(LineCap cap) => Layered(new GeometryStroke(
                Line(24, 48, 72, 48), Ink, new StrokeStyle(14, cap, LineJoin.Miter)));

            AssertChangeIsVisible(Capped(LineCap.Butt), Capped(LineCap.Square), "stroke cap");
        }

        /// <summary>
        /// The 3D case: a moving light. Camera, models and their transforms are all unchanged, so the
        /// lighting is the only input that differs -- and a scene lit by an animated light is exactly
        /// where a missing hash shows as a frozen picture.
        /// </summary>
        [Fact]
        public void AChangedLightIsVisible()
        {
            static MeshGeometry3D Quad()
            {
                var positions = new[]
                {
                    new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
                };
                var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
                return new MeshGeometry3D(positions, normals, new[] { 0, 1, 2, 0, 2, 3 });
            }

            SceneVisual Lit(Vector3 direction) => Layered(new Viewport3DDraw(
                new Camera3D(new Vector3(0, 0, 3), new Vector3(0, 0, -1), new Vector3(0, 1, 0), 45f),
                new DirectionalLight3D(direction, RgbaColor.FromBytes(255, 255, 255, 255)),
                new RgbaColor(0.15f, 0.15f, 0.15f, 1f),
                new List<Model3D> { new(Quad(), new RgbaColor(0.6f, 0.6f, 0.6f, 1f)) },
                new Rect(8, 8, 80, 80)));

            AssertChangeIsVisible(Lit(new Vector3(0, 0, -1)), Lit(new Vector3(0, 0, 1)), "light direction");
        }

        /// <summary>
        /// The counterpart that stops all of the above passing for the wrong reason: two IDENTICAL
        /// scenes must still hit the cache. A key that hashed something unstable (an object identity,
        /// a frame counter) would satisfy every test above by never hitting at all.
        /// </summary>
        [Fact]
        public void AnUnchangedSceneStillHitsTheCache()
        {
            SceneVisual Scene() => Layered(new GeometryStroke(
                Line(12, 48, 84, 48), Ink,
                new StrokeStyle(6, LineCap.Butt, LineJoin.Miter, 10.0, new[] { 10.0, 10.0 }, 4.0)));

            WgpuSceneRenderer renderer = NewRenderer();
            renderer.RenderToRgba(Scene(), W, H, White);
            renderer.RenderToRgba(Scene(), W, H, White);

            WgpuSceneRenderer.PerfReset();
            renderer.RenderToRgba(Scene(), W, H, White);

            Assert.True(WgpuSceneRenderer.PerfLayerHits > 0,
                "an unchanged scene produced no layer cache hits at all; the key has become unstable " +
                "and every frame now re-bakes its layers");
            Assert.Equal(0, WgpuSceneRenderer.PerfLayerMiss);
        }
    }
}
