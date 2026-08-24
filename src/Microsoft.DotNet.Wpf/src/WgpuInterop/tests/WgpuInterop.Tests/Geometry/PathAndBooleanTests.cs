// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.AAPathTest, CombinedGeometryTest and TileTest.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    /// <summary>
    /// The arbitrary-path pipeline: figures flattened, rasterized to an analytic-AA coverage mask,
    /// uploaded as an R8 texture and composited on the GPU.
    /// </summary>
    public sealed class PathFillTests : RendererTestBase
    {
        public PathFillTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 96, H = 64;

        private static PathFigure Square(float x, float y, float size)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + size, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + size, y + size)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + size)));
            return f;
        }

        private static SceneVisual PathScene()
        {
            var root = new SceneVisual();
            var black = RgbaColor.FromBytes(0, 0, 0, 255);

            // Even-odd ring at offset (4,4): outer 0..24, inner 8..16 in local space.
            var ring = new SceneVisual { Offset = new Vector2(4, 4) };
            ring.Content.Add(new GeometryFill(
                new PathGeometry(FillRule.EvenOdd, new List<PathFigure> { Square(0, 0, 24), Square(8, 8, 8) }),
                black));
            root.Children.Add(ring);

            // Right triangle at offset (40,4): local (0,40), (40,40), (40,0).
            var tri = new SceneVisual { Offset = new Vector2(40, 4) };
            var figure = new PathFigure(new Vector2(0, 40));
            figure.Segments.Add(new LineSegment(new Vector2(40, 40)));
            figure.Segments.Add(new LineSegment(new Vector2(40, 0)));
            tri.Content.Add(new GeometryFill(
                new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure }), black));
            root.Children.Add(tri);

            return root;
        }

        /// <summary>
        /// The ring probes prove the fill RULE and multi-contour holes: under NonZero the two
        /// same-wound squares would fill solid, so the hollow centre is what distinguishes EvenOdd.
        /// </summary>
        [Theory]
        [InlineData(50, 2, 255, 255, 255, "background")]
        [InlineData(6, 16, 0, 0, 0, "ring band (even-odd filled)")]
        [InlineData(16, 16, 255, 255, 255, "ring centre (even-odd hole)")]
        [InlineData(74, 40, 0, 0, 0, "triangle interior")]
        [InlineData(44, 8, 255, 255, 255, "triangle exterior")]
        public void ArbitraryPaths_FillWithTheFillRule(int x, int y, int r, int g, int b, string what)
            => new Image(Render(PathScene(), W, H), W, H).AssertPixel(x, y, r, g, b, tol: 6, what);

        /// <summary>
        /// A pixel whose centre sits ON the 45-degree edge (device x+y=83) must carry PARTIAL
        /// coverage. A non-anti-aliased fill would make it hard black or hard white, so this is the
        /// one probe that cannot be satisfied by a correct-but-aliased rasterizer.
        /// </summary>
        [Fact]
        public void DiagonalEdge_HasIntermediateCoverage()
        {
            var img = new Image(Render(PathScene(), W, H), W, H);
            int v = img[60, 23].R;
            Assert.True(v > 20 && v < 235,
                $"the pixel on the 45-degree edge should be partially covered, was {v} (hard black/white means no AA)");
        }

        [Fact]
        public void ArbitraryPaths_SurviveProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(PathScene(), W, H);
    }

    /// <summary>
    /// Boolean geometry ops. Combining happens at the COVERAGE level, so the result is anti-aliased
    /// rather than a hard-edged stencil.
    /// </summary>
    public sealed class CombinedGeometryTests : RendererTestBase
    {
        public CombinedGeometryTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 64, H = 48;

        /// <summary>Two overlapping circles: A centred at x=26, B at x=40, both r=14 on row y=24.</summary>
        private static SceneVisual Scene(GeometryCombineMode mode)
        {
            var root = new SceneVisual();
            var v = new SceneVisual();
            v.Content.Add(new GeometryFill(
                new CombinedGeometry(mode,
                    new EllipseGeometry(new Vector2(26, 24), 14, 14),
                    new EllipseGeometry(new Vector2(40, 24), 14, 14)),
                RgbaColor.FromBytes(0, 0, 0, 255)));
            root.Children.Add(v);
            return root;
        }

        private static GeometryCombineMode Mode(int i) => i switch
        {
            0 => GeometryCombineMode.Union,
            1 => GeometryCombineMode.Intersect,
            2 => GeometryCombineMode.Xor,
            _ => GeometryCombineMode.Exclude,
        };

        /// <summary>
        /// Three probes on row y=24 distinguish all four modes: A-only (x=18), overlap (x=33) and
        /// B-only (x=48). No pair of modes agrees on all three, which is what makes this exhaustive
        /// rather than merely suggestive.
        ///
        /// The mode arrives as an int because GeometryCombineMode is internal to the renderer and a
        /// public xunit theory cannot take a less-accessible parameter type.
        /// </summary>
        [Theory]
        [InlineData(0, true, true, true)]     // Union
        [InlineData(1, false, true, false)]   // Intersect
        [InlineData(2, true, false, true)]    // Xor
        [InlineData(3, true, false, false)]   // Exclude: in A but not B
        public void BooleanOps_ProduceTheRightRegions(int modeIndex, bool aOnly, bool overlap, bool bOnly)
        {
            GeometryCombineMode mode = Mode(modeIndex);
            var img = new Image(Render(Scene(mode), W, H), W, H);

            AssertRegion(img, 18, aOnly, $"{mode}: A-only");
            AssertRegion(img, 33, overlap, $"{mode}: overlap");
            AssertRegion(img, 48, bOnly, $"{mode}: B-only");
        }

        private static void AssertRegion(Image img, int x, bool filled, string what)
        {
            int v = img[x, 24].R;
            Assert.True(filled ? v < 40 : v > 215,
                $"{what} should be {(filled ? "filled" : "empty")}, luminance was {v}");
        }

        [Fact]
        public void BooleanResult_IsAntiAliased()
            => AssertAntiAliased(Render(Scene(GeometryCombineMode.Union), W, H), "boolean result");

        /// <summary>Geometry is serialized recursively, so this covers nested resource encoding.</summary>
        [Fact]
        public void CombinedGeometry_SurvivesProtocolRoundTrip()
            => AssertProtocolRoundTripIsIdentical(Scene(GeometryCombineMode.Union), W, H);
    }

    public sealed class TileBrushTests : RendererTestBase
    {
        public TileBrushTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 32, H = 16;

        // 2x2 checker: TL red, TR green, BL blue, BR yellow.
        private static readonly byte[] Checker =
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 0, 255,
        };

        private static SceneVisual Scene(TileMode mode)
        {
            var root = new SceneVisual();
            var fill = new SceneVisual();
            fill.Content.Add(new GeometryFill(
                new RectangleGeometry(new Rect(0, 0, 32, 16)),
                new ImageBrush(Checker, 2, 2, mode, 8, 8)));
            root.Children.Add(fill);
            return root;
        }

        // Sampling note: image brushes are bilinear-sampled (matching WPF), so a 2x2 checker scaled
        // to an 8x8 tile blends across texel boundaries. The probes sit at clamped corners of each
        // texel region (x in {1,6} per 8px tile, y=1) where bilinear equals the pure texel colour --
        // sampling mid-tile would measure the interpolation, not the tiling.

        [Theory]
        [InlineData(1, 255, 0, 0, "first tile column 0 (red)")]
        [InlineData(6, 0, 255, 0, "first tile column 1 (green)")]
        [InlineData(9, 255, 0, 0, "second tile column 0 repeats (red)")]
        [InlineData(14, 0, 255, 0, "second tile column 1 repeats (green)")]
        public void Tile_RepeatsIdentically(int x, int r, int g, int b, string what)
            => new Image(Render(Scene(TileMode.Tile), W, H), W, H).AssertPixel(x, 1, r, g, b, tol: 8, what);

        /// <summary>
        /// FlipX mirrors ALTERNATE tiles, so the second tile's columns swap. Tile 0 is identical
        /// under both modes, which is why only the second tile discriminates.
        /// </summary>
        [Theory]
        [InlineData(1, 255, 0, 0, "first tile column 0 (red)")]
        [InlineData(9, 0, 255, 0, "second tile mirrored, column 0 is green")]
        [InlineData(14, 255, 0, 0, "second tile mirrored, column 1 is red")]
        public void FlipX_MirrorsAlternateTiles(int x, int r, int g, int b, string what)
            => new Image(Render(Scene(TileMode.FlipX), W, H), W, H).AssertPixel(x, 1, r, g, b, tol: 8, what);

        [Theory]
        [InlineData(false)]   // Tile
        [InlineData(true)]    // FlipX
        public void TileModes_SurviveProtocolRoundTrip(bool flip)
            => AssertProtocolRoundTripIsIdentical(Scene(flip ? TileMode.FlipX : TileMode.Tile), W, H);
    }
}
