// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A pen's three caps, each where WPF puts it: the start cap at the start of an open figure, the end cap at its
// end, and the dash cap at the ends of the dashes inside it. The end cap and the dash cap were drawn as the
// start cap: an arrow-like line flat at its start and round at its end came out flat at both.
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Geometry
{
    public sealed class StrokeCapTests
    {
        private static PathGeometry Line(float x0, float x1)
        {
            var f = new PathFigure(new Vector2(x0, 0)) { Closed = false };
            f.Segments.Add(new LineSegment(new Vector2(x1, 0)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        private static (float Min, float Max) ExtentX(PathGeometry g)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (PathFigure f in g.Figures)
            {
                void Take(Vector2 p) { if (p.X < min) min = p.X; if (p.X > max) max = p.X; }
                Take(f.Start);
                foreach (PathSegment s in f.Segments)
                    if (s is LineSegment l) Take(l.Point);
            }
            return (min, max);
        }

        [Fact]
        public void TheStartAndTheEnd_HaveTheirOwnCaps()
        {
            // thickness 10: a square cap reaches 5 past its end, a flat one not at all
            var style = new StrokeStyle(10, LineCap.Butt, LineJoin.Miter, endCap: LineCap.Square);
            (float min, float max) = ExtentX(PathStroker.Stroke(Line(0, 100), style));
            Assert.Equal(0f, min, 3);
            Assert.Equal(105f, max, 3);
        }

        [Fact]
        public void TheDashesInside_EndInTheDashCap()
        {
            // two dashes of 40 with a gap of 20: 0..40 and 60..100. Flat caps at the line's ends, square ones at the
            // dashes' inner ends -- the first dash reaches 45, the second starts at 55.
            var style = new StrokeStyle(10, LineCap.Butt, LineJoin.Miter, dashArray: new double[] { 40, 20 }, endCap: LineCap.Butt, dashCap: LineCap.Square);
            PathGeometry stroked = PathStroker.Stroke(Line(0, 100), style);
            (float min, float max) = ExtentX(stroked);
            Assert.Equal(0f, min, 3);       // the line's own start: flat
            Assert.Equal(100f, max, 3);     // and its end
            bool reaches45 = false, from55 = false;
            foreach (PathFigure f in stroked.Figures)
            {
                (float a, float b) = ExtentX(new PathGeometry(FillRule.NonZero, new List<PathFigure> { f }));
                if (System.MathF.Abs(b - 45) < 1e-3) reaches45 = true;
                if (System.MathF.Abs(a - 55) < 1e-3) from55 = true;
            }
            Assert.True(reaches45, "the first dash's inner end has the square dash cap");
            Assert.True(from55, "and so has the second's");
        }
    }
}
