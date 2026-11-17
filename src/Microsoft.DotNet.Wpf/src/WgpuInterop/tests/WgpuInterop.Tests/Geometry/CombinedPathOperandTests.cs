// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A CombinedGeometry whose operands are paths is filled as the combination of those paths. A path given to the
// coverage rasterizer as "a shape's path" came back empty (the conversion knew rectangles, ellipses and groups,
// not paths), so a path intersected with or excluded from anything drew nothing.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class CombinedPathOperandTests : RendererTestBase
    {
        public CombinedPathOperandTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 48, H = 32;

        private static PathGeometry Box(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        // A: x 4..28, B: x 20..44, both y 4..28; probes at x 10 (A only), 24 (both), 38 (B only)
        [Theory]
        [InlineData(1, false, true, false)]   // Intersect
        [InlineData(3, true, false, false)]   // Exclude
        [InlineData(2, true, false, true)]    // Xor
        public void TwoPaths_CombineIntoTheRightRegions(int mode, bool aOnly, bool both, bool bOnly)
        {
            var m = mode switch { 1 => GeometryCombineMode.Intersect, 2 => GeometryCombineMode.Xor, _ => GeometryCombineMode.Exclude };
            var root = new SceneVisual();
            var v = new SceneVisual();
            v.Content.Add(new GeometryFill(new CombinedGeometry(m, Box(4, 4, 24, 24), Box(20, 4, 24, 24)), RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(v);
            var img = new Image(Render(root, W, H), W, H);
            void Check(int x, bool filled, string what)
                => Assert.True(filled ? img[x, 16].R < 40 : img[x, 16].R > 215, $"{m}: {what} should be {(filled ? "filled" : "empty")}, was {img[x, 16]}");
            Check(10, aOnly, "A only");
            Check(24, both, "the overlap");
            Check(38, bOnly, "B only");
        }
    }
}
