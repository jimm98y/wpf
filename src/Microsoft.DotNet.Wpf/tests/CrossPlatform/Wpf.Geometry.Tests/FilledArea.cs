// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The area a geometry actually covers, measured under its fill rule.
//
// Needed because a widened stroke is deliberately a pile of OVERLAPPING contours -- one quad per
// segment, one wedge per join, one disc per round cap -- unioned by the nonzero rule. Summing their
// shoelace areas would count every overlap twice, so a test written that way would "fail" against a
// perfectly correct stroker and, worse, could be made to pass by a broken one.
//
// So this integrates the covered region by scanline: at each of N sample rows, find where the edges
// cross, sort them, and add up the spans the fill rule says are inside. The error is O(1/N) and
// lives entirely at the boundary, which for the shapes here means four or five significant figures.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Wpf.Geometry.Tests
{
    internal static class FilledArea
    {
        /// <summary>Area covered by the geometry, honouring its fill rule.</summary>
        internal static double Of(PathGeometry geometry, int scanlines = 4000)
        {
            List<List<Point>> contours = Contours(geometry);
            if (contours.Count == 0) return 0.0;

            double top = double.MaxValue, bottom = double.MinValue;
            foreach (List<Point> contour in contours)
            {
                foreach (Point p in contour)
                {
                    if (p.Y < top) top = p.Y;
                    if (p.Y > bottom) bottom = p.Y;
                }
            }

            if (!(bottom > top)) return 0.0;

            bool nonZero = geometry.FillRule == FillRule.Nonzero;
            double height = (bottom - top) / scanlines;
            double area = 0.0;

            var crossings = new List<(double X, int Direction)>();

            for (int row = 0; row < scanlines; row++)
            {
                // Sample at the middle of each band, so a horizontal edge lying exactly on a shape's
                // boundary is never sampled and cannot register as an ambiguous crossing.
                double y = top + (row + 0.5) * height;

                crossings.Clear();
                foreach (List<Point> contour in contours)
                {
                    int n = contour.Count;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        Point a = contour[j], b = contour[i];
                        if ((a.Y > y) == (b.Y > y)) continue;   // no crossing, horizontals included

                        double x = a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y);
                        crossings.Add((x, b.Y > a.Y ? 1 : -1));
                    }
                }

                if (crossings.Count < 2) continue;
                crossings.Sort((l, r) => l.X.CompareTo(r.X));

                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].Direction;

                    // Nonzero: inside wherever the accumulated winding is not zero. Even-odd:
                    // inside after an odd number of crossings, which is just the index parity.
                    bool inside = nonZero ? winding != 0 : ((i + 1) & 1) != 0;
                    if (inside) area += (crossings[i + 1].X - crossings[i].X) * height;
                }
            }

            return area;
        }

        /// <summary>
        /// The geometry's figures as closed point rings, with its Transform applied. Accepts only
        /// polyline content, which is what both the flattener and the stroker produce.
        /// </summary>
        internal static List<List<Point>> Contours(PathGeometry geometry)
        {
            var contours = new List<List<Point>>();
            Matrix m = geometry.Transform?.Value ?? Matrix.Identity;

            foreach (PathFigure figure in geometry.Figures)
            {
                var points = new List<Point> { m.Transform(figure.StartPoint) };

                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case PolyLineSegment poly:
                            foreach (Point p in poly.Points) points.Add(m.Transform(p));
                            break;
                        case LineSegment line:
                            points.Add(m.Transform(line.Point));
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"{segment.GetType().Name} in a geometry that should hold only polylines");
                    }
                }

                if (points.Count >= 3) contours.Add(points);
            }

            return contours;
        }

        /// <summary>Asserts a measured area is within a relative tolerance of the exact one.</summary>
        internal static void AssertClose(double exact, double measured, double relative, string what)
        {
            double slack = Math.Abs(exact) * relative;
            if (measured < exact - slack || measured > exact + slack)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{what}: expected {exact:0.####}, measured {measured:0.####} " +
                    $"({(measured - exact) / exact * 100:0.##}% off, allowed {relative * 100:0.##}%)");
            }
        }
    }
}
