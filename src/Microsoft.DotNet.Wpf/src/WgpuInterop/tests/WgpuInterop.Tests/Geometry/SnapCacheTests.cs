// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Guideline snapping must not throw away the geometry's path cache.
//
// Geometry.PathCache exists because converting a shape to a path allocates a whole PathGeometry, and
// the comment on it says why it works: "Geometry instances are stable across frames". Snapping broke
// that quietly. SnapGeometry returned a NEW RectangleGeometry holding the snapped rectangle on every
// frame, and a new instance starts with a cold PathCache -- so any visual carrying a GuidelineSet
// re-converted its shape on every single frame and the memo never once hit for it.
//
// Nothing rendered wrong, which is why it survived: the only symptom was allocation. Profiling the
// hex editor put guideline snapping at a steady 177 KB per frame, in a collect phase allocating
// about 1.4 MB per frame in total.
//
// Snapping the same shape under the same transform lands on the same rectangle every time, so the
// snapped instance is memoized and handed back. Two consequences are asserted here: the same object
// comes back across frames (so its own path cache survives with it), and a snap that moves nothing
// returns the ORIGINAL primitive rather than a copy of it.
//

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class SnapCacheTests : RendererTestBase
    {
        public SnapCacheTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 64;

        /// <summary>A bar on a half-pixel boundary with a guideline that pulls it onto a whole one.</summary>
        private static (SceneVisual Root, RectangleGeometry Bar) Scene()
        {
            var bar = new RectangleGeometry(new Rect(6, 20.5f, 28, 1));
            var child = new SceneVisual { GuidelinesY = new[] { 20.5f, 21.5f } };
            child.Content.Add(new GeometryFill(bar, RgbaColor.FromBytes(20, 20, 20, 255)));

            var root = new SceneVisual();
            root.Children.Add(child);
            return (root, bar);
        }

        [Fact]
        public void TheSnappedGeometryIsReusedAcrossFrames()
        {
            (SceneVisual root, RectangleGeometry bar) = Scene();
            WgpuSceneRenderer renderer = NewRenderer();

            renderer.BeginFrame();
            renderer.RenderToRgba(root, W, H, White);
            renderer.EndFrame();
            Microsoft.Wpf.Interop.WebGpu.Composition.Geometry first = bar.SnapCache;

            renderer.BeginFrame();
            renderer.RenderToRgba(root, W, H, White);
            renderer.EndFrame();

            Assert.NotNull(first);
            Assert.Same(first, bar.SnapCache);
        }

        /// <summary>
        /// A shape already on a guideline needs no copy at all, and must not get one: the original
        /// carries a path cache that a copy would leave behind.
        /// </summary>
        [Fact]
        public void AShapeThatDoesNotMoveKeepsItsOwnInstance()
        {
            var bar = new RectangleGeometry(new Rect(6, 20, 28, 1));
            var child = new SceneVisual { GuidelinesY = new[] { 20f, 21f } };
            child.Content.Add(new GeometryFill(bar, RgbaColor.FromBytes(20, 20, 20, 255)));
            var root = new SceneVisual();
            root.Children.Add(child);

            WgpuSceneRenderer renderer = NewRenderer();
            renderer.BeginFrame();
            renderer.RenderToRgba(root, W, H, White);
            renderer.EndFrame();

            Assert.Null(bar.SnapCache);
        }
    }
}
