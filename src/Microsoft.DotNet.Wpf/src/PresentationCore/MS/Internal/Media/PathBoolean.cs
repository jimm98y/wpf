// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Boolean combination of two shapes: the managed stand-in for milcore's MilUtility_PathGeometryCombine,
// and for MilUtility_PathGeometryOutline, which is the same machinery run against one operand.
//
// What this replaces is worth stating, because it did not fail loudly. The previous managed answer
// reduced each operand to THE BOUNDING RECTANGLE OF EACH OF ITS FIGURES and combined those. That is
// exact for the case it was written for -- rectangle-vs-rectangle Intersect, which is what layout
// clips are -- and silently wrong for everything else: a circle intersected with a circle came back
// a square, and Exclude and Xor were not implemented at all, returning the first operand unchanged.
// Clipping runs underneath rendering, hit testing and layout, so it was wrong in three places at once.
//
// The approach is the standard one for polygon booleans, chosen for being explainable rather than
// clever, since robustness is the whole difficulty here:
//
//   1. Flatten both operands to polylines (PathFlattener), so everything downstream is line segments.
//   2. Split every edge wherever it meets another, so no two edges cross except at shared endpoints.
//   3. Classify each resulting edge by sampling just off either side of it and asking whether that
//      point is inside the RESULT. An edge survives only if the answer differs across it, which is
//      the definition of a boundary.
//   4. Chain the survivors head to tail into closed contours.
//
// Classification by sampling is the part that makes this tractable. The alternative -- tracking
// in/out state along a sweep and threading it through every degeneracy -- is where implementations
// of this algorithm usually go wrong, on coincident edges, on vertices shared by three contours, on
// a shape that touches another at a single point. Sampling asks a question with an unambiguous
// answer, and asks it independently for every edge, so no error can propagate.
//
// Coordinates are snapped to a grid fine enough to be invisible (a billionth of the shapes' size)
// and coarse enough that two computed intersection points that ought to coincide actually do. Exact
// endpoint matching is what makes step 4 a lookup rather than a search.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MS.Internal.Media
{
    internal static class PathBoolean
    {
        /// <summary>
        /// Combines two geometries. <paramref name="transform"/> applies to the RESULT, matching
        /// Geometry.Combine. Never null.
        /// </summary>
        internal static PathGeometry Combine(Geometry geometry1, Geometry geometry2, GeometryCombineMode mode,
                                             Transform transform, double tolerance)
        {
            List<List<Point>> a = ClosedContours(geometry1, tolerance, out bool evenOddA);
            List<List<Point>> b = ClosedContours(geometry2, tolerance, out bool evenOddB);

            PathGeometry result = TryRectangleFastPath(a, b, mode, evenOddA, evenOddB)
                                  ?? Resolve(a, b, evenOddA, evenOddB, mode);

            if (transform != null && !transform.Value.IsIdentity) result.Transform = transform;
            return result;
        }

        /// <summary>
        /// The outline of a geometry's fill region: the same resolution run against one operand, which
        /// removes self-intersections and re-expresses the shape as non-overlapping contours.
        /// </summary>
        internal static PathGeometry Outline(Geometry geometry, double tolerance)
        {
            List<List<Point>> a = ClosedContours(geometry, tolerance, out bool evenOdd);
            return Resolve(a, new List<List<Point>>(), evenOdd, false, GeometryCombineMode.Union);
        }

        // ---- input -----------------------------------------------------------------

        /// <summary>
        /// Flattens a geometry to closed rings. Open figures are closed, because a boolean operates on
        /// REGIONS: an unclosed figure still bounds the area a fill would cover, and WPF fills it as
        /// though the last point joined the first.
        /// </summary>
        private static List<List<Point>> ClosedContours(Geometry geometry, double tolerance, out bool evenOdd)
        {
            evenOdd = false;
            var contours = new List<List<Point>>();
            if (geometry == null) return contours;

            try
            {
                PathGeometry asPath = geometry.GetAsPathGeometry();
                if (asPath != null) evenOdd = asPath.FillRule == FillRule.EvenOdd;
            }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }

            foreach (PolylineFigure figure in PathFlattener.Flatten(geometry, tolerance))
            {
                if (figure.Points.Count >= 3) contours.Add(figure.Points);
            }

            return contours;
        }

        // ---- the fast path ---------------------------------------------------------

        /// <summary>
        /// Intersecting two axis-aligned rectangles, answered directly.
        ///
        /// Not an optimization for its own sake: this is what a layout clip is, it is evaluated on
        /// every arrange pass of every clipped element, and the general path would flatten, split,
        /// ray-cast and chain to reach an answer that is one Rect.Intersect. Returns null whenever the
        /// shapes are anything else.
        /// </summary>
        private static PathGeometry TryRectangleFastPath(List<List<Point>> a, List<List<Point>> b,
                                                         GeometryCombineMode mode, bool evenOddA, bool evenOddB)
        {
            if (mode != GeometryCombineMode.Intersect) return null;
            if (a.Count != 1 || b.Count != 1) return null;
            if (!IsAxisAlignedRectangle(a[0], out Rect ra) || !IsAxisAlignedRectangle(b[0], out Rect rb)) return null;

            _ = evenOddA;
            _ = evenOddB;   // a single rectangle is the same region under either fill rule

            Rect intersection = Rect.Intersect(ra, rb);
            var result = new PathGeometry { FillRule = FillRule.Nonzero };
            if (intersection.IsEmpty) return result;

            AddContour(result, new List<Point>
            {
                new Point(intersection.Left, intersection.Top),
                new Point(intersection.Right, intersection.Top),
                new Point(intersection.Right, intersection.Bottom),
                new Point(intersection.Left, intersection.Bottom),
            });
            return result;
        }

        private static bool IsAxisAlignedRectangle(List<Point> contour, out Rect rect)
        {
            rect = Rect.Empty;

            // A flattened rectangle is four corners, possibly with the first repeated at the end.
            int n = contour.Count;
            if (n == 5 && Same(contour[0], contour[4])) n = 4;
            if (n != 4) return false;

            for (int i = 0; i < 4; i++)
            {
                Point p = contour[i], q = contour[(i + 1) % 4];
                bool horizontal = p.Y == q.Y, vertical = p.X == q.X;
                if (horizontal == vertical) return false;   // neither, or a degenerate point
            }

            double left = Math.Min(contour[0].X, contour[2].X), right = Math.Max(contour[0].X, contour[2].X);
            double top = Math.Min(contour[0].Y, contour[2].Y), bottom = Math.Max(contour[0].Y, contour[2].Y);

            rect = new Rect(left, top, right - left, bottom - top);
            return true;
        }

        private static bool Same(Point p, Point q) => p.X == q.X && p.Y == q.Y;

        // ---- the general resolution ------------------------------------------------

        private struct Edge
        {
            internal Point A;
            internal Point B;
        }

        private static PathGeometry Resolve(List<List<Point>> a, List<List<Point>> b,
                                            bool evenOddA, bool evenOddB, GeometryCombineMode mode)
        {
            var result = new PathGeometry { FillRule = FillRule.Nonzero };

            if (a.Count == 0 && b.Count == 0) return result;

            // Whole-operand shortcuts. Beyond saving work these keep the common "clip by something
            // that turns out to be empty" cases exact rather than routing them through sampling.
            if (a.Count == 0 || b.Count == 0)
            {
                switch (mode)
                {
                    case GeometryCombineMode.Intersect:
                        return result;
                    case GeometryCombineMode.Exclude:
                        if (a.Count == 0) return result;
                        break;
                }
            }

            double grid = GridFor(a, b);
            SnapAll(a, grid);
            SnapAll(b, grid);

            List<Edge> edges = Split(BuildEdges(a, b), grid);

            double probe = grid * 8.0;   // comfortably outside the snapping grid, still invisible
            var kept = new List<Edge>();

            foreach (Edge edge in edges)
            {
                double dx = edge.B.X - edge.A.X, dy = edge.B.Y - edge.A.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (!(length > 0)) continue;

                double mx = (edge.A.X + edge.B.X) / 2, my = (edge.A.Y + edge.B.Y) / 2;

                // The left normal in a y-down space: walking along (1,0), "up" is to the left.
                double lx = dy / length * probe, ly = -dx / length * probe;

                bool insideLeft = Classify(new Point(mx + lx, my + ly), a, b, evenOddA, evenOddB, mode);
                bool insideRight = Classify(new Point(mx - lx, my - ly), a, b, evenOddA, evenOddB, mode);

                if (insideLeft == insideRight) continue;   // not a boundary of the result

                // Orient every survivor with the inside on its left, so that outer contours and the
                // holes within them come out wound oppositely and the nonzero rule punches the holes.
                kept.Add(insideLeft ? edge : new Edge { A = edge.B, B = edge.A });
            }

            foreach (List<Point> contour in Chain(kept)) AddContour(result, contour);
            return result;
        }

        private static bool Classify(Point p, List<List<Point>> a, List<List<Point>> b,
                                     bool evenOddA, bool evenOddB, GeometryCombineMode mode)
        {
            bool inA = Inside(p, a, evenOddA);
            bool inB = Inside(p, b, evenOddB);

            return mode switch
            {
                GeometryCombineMode.Union => inA || inB,
                GeometryCombineMode.Intersect => inA && inB,
                GeometryCombineMode.Xor => inA ^ inB,
                GeometryCombineMode.Exclude => inA && !inB,
                _ => inA || inB,
            };
        }

        /// <summary>
        /// Whether a point lies inside a set of contours, under the given fill rule. A horizontal ray
        /// to the right; the half-open comparison on Y is what makes a vertex lying exactly on the ray
        /// count once rather than twice or not at all.
        /// </summary>
        private static bool Inside(Point p, List<List<Point>> contours, bool evenOdd)
        {
            int winding = 0;
            int crossings = 0;

            foreach (List<Point> contour in contours)
            {
                int n = contour.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    Point s = contour[j], e = contour[i];
                    if ((s.Y > p.Y) == (e.Y > p.Y)) continue;

                    double x = s.X + (e.X - s.X) * (p.Y - s.Y) / (e.Y - s.Y);
                    if (x <= p.X) continue;

                    crossings++;
                    winding += e.Y > s.Y ? 1 : -1;
                }
            }

            return evenOdd ? (crossings & 1) != 0 : winding != 0;
        }

        // ---- edge preparation ------------------------------------------------------

        private static List<Edge> BuildEdges(List<List<Point>> a, List<List<Point>> b)
        {
            var edges = new List<Edge>();
            AppendEdges(a, edges);
            AppendEdges(b, edges);
            return edges;
        }

        private static void AppendEdges(List<List<Point>> contours, List<Edge> edges)
        {
            foreach (List<Point> contour in contours)
            {
                int n = contour.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    if (!Same(contour[j], contour[i])) edges.Add(new Edge { A = contour[j], B = contour[i] });
                }
            }
        }

        /// <summary>
        /// Splits every edge at every point where another edge meets it, so that afterwards no two
        /// edges cross anywhere but at a shared endpoint. Duplicate edges (two shapes sharing a
        /// border, which is extremely common) collapse to one, since two copies would be classified
        /// identically and then chained twice.
        /// </summary>
        private static List<Edge> Split(List<Edge> edges, double grid)
        {
            int count = edges.Count;
            var cuts = new List<List<double>>(count);
            for (int i = 0; i < count; i++) cuts.Add(null);

            for (int i = 0; i < count; i++)
            {
                Edge ei = edges[i];
                double iMinX = Math.Min(ei.A.X, ei.B.X), iMaxX = Math.Max(ei.A.X, ei.B.X);
                double iMinY = Math.Min(ei.A.Y, ei.B.Y), iMaxY = Math.Max(ei.A.Y, ei.B.Y);

                for (int j = i + 1; j < count; j++)
                {
                    Edge ej = edges[j];

                    // Bounding rejection first: without it this is quadratic in earnest, and shapes
                    // that do not overlap at all are the common case inside one operand.
                    if (Math.Max(ej.A.X, ej.B.X) < iMinX || Math.Min(ej.A.X, ej.B.X) > iMaxX) continue;
                    if (Math.Max(ej.A.Y, ej.B.Y) < iMinY || Math.Min(ej.A.Y, ej.B.Y) > iMaxY) continue;

                    AddCrossings(ei, ej, EnsureCuts(cuts, i), EnsureCuts(cuts, j));
                }
            }

            var result = new List<Edge>(count * 2);
            var seen = new HashSet<(double, double, double, double)>();

            for (int i = 0; i < count; i++)
            {
                Edge e = edges[i];
                List<double> parameters = cuts[i];

                if (parameters == null || parameters.Count == 0)
                {
                    AddUnique(result, seen, e.A, e.B);
                    continue;
                }

                parameters.Sort();

                Point previous = e.A;
                foreach (double t in parameters)
                {
                    Point at = Snap(new Point(e.A.X + (e.B.X - e.A.X) * t, e.A.Y + (e.B.Y - e.A.Y) * t), grid);
                    if (!Same(at, previous)) AddUnique(result, seen, previous, at);
                    previous = at;
                }

                if (!Same(previous, e.B)) AddUnique(result, seen, previous, e.B);
            }

            return result;
        }

        // Allocated only once an edge's bounding box has actually met another's; most edges in a
        // typical pair of shapes never do.
        private static List<double> EnsureCuts(List<List<double>> lists, int index)
            => lists[index] ??= new List<double>(4);

        private static void AddCrossings(Edge e1, Edge e2, List<double> cuts1, List<double> cuts2)
        {
            double d1x = e1.B.X - e1.A.X, d1y = e1.B.Y - e1.A.Y;
            double d2x = e2.B.X - e2.A.X, d2y = e2.B.Y - e2.A.Y;

            double denominator = d1x * d2y - d1y * d2x;
            double sx = e2.A.X - e1.A.X, sy = e2.A.Y - e1.A.Y;

            if (Math.Abs(denominator) > 1e-12)
            {
                double t = (sx * d2y - sy * d2x) / denominator;
                double u = (sx * d1y - sy * d1x) / denominator;

                if (t > 0 && t < 1) cuts1.Add(t);
                if (u > 0 && u < 1) cuts2.Add(u);
                return;
            }

            // Parallel. Only collinear overlap matters, and it matters a lot: two shapes sharing a
            // border produce it every time. Each segment is cut where the other one's endpoints fall
            // inside it, which turns an overlap into shared edges that then dedupe away.
            double cross = sx * d1y - sy * d1x;
            double scale = Math.Max(Math.Abs(d1x) + Math.Abs(d1y), 1e-12);
            if (Math.Abs(cross) > scale * 1e-9) return;

            double length1 = d1x * d1x + d1y * d1y;
            double length2 = d2x * d2x + d2y * d2y;
            if (!(length1 > 0) || !(length2 > 0)) return;

            AddIfInterior(cuts1, ((e2.A.X - e1.A.X) * d1x + (e2.A.Y - e1.A.Y) * d1y) / length1);
            AddIfInterior(cuts1, ((e2.B.X - e1.A.X) * d1x + (e2.B.Y - e1.A.Y) * d1y) / length1);
            AddIfInterior(cuts2, ((e1.A.X - e2.A.X) * d2x + (e1.A.Y - e2.A.Y) * d2y) / length2);
            AddIfInterior(cuts2, ((e1.B.X - e2.A.X) * d2x + (e1.B.Y - e2.A.Y) * d2y) / length2);
        }

        private static void AddIfInterior(List<double> cuts, double t)
        {
            if (t > 1e-12 && t < 1 - 1e-12) cuts.Add(t);
        }

        private static void AddUnique(List<Edge> result, HashSet<(double, double, double, double)> seen,
                                      Point a, Point b)
        {
            // Unordered: an edge walked in opposite directions by two shapes is still one border.
            var key = (a.X < b.X || (a.X == b.X && a.Y <= b.Y))
                ? (a.X, a.Y, b.X, b.Y)
                : (b.X, b.Y, a.X, a.Y);

            if (seen.Add(key)) result.Add(new Edge { A = a, B = b });
        }

        // ---- chaining --------------------------------------------------------------

        /// <summary>
        /// Links the surviving edges into closed rings by following each one's end to an edge that
        /// starts there.
        ///
        /// Where more than one candidate leaves a vertex -- which happens wherever contours touch --
        /// the sharpest LEFT turn is taken. Choosing consistently is what keeps the rings from
        /// crossing each other; choosing left specifically keeps the interior, which every edge
        /// already has on its left, on the inside of the ring being built.
        /// </summary>
        private static List<List<Point>> Chain(List<Edge> edges)
        {
            var contours = new List<List<Point>>();
            if (edges.Count == 0) return contours;

            var outgoing = new Dictionary<(double, double), List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                var key = (edges[i].A.X, edges[i].A.Y);
                if (!outgoing.TryGetValue(key, out List<int> list)) outgoing[key] = list = new List<int>(2);
                list.Add(i);
            }

            var used = new bool[edges.Count];

            for (int start = 0; start < edges.Count; start++)
            {
                if (used[start]) continue;

                var points = new List<Point>();
                int current = start;
                Point origin = edges[start].A;

                // A hard bound rather than trusting the walk to terminate: a malformed edge set must
                // not be able to spin here, and drawing code has no business hanging.
                for (int guard = 0; guard <= edges.Count; guard++)
                {
                    used[current] = true;
                    points.Add(edges[current].A);

                    Point tip = edges[current].B;
                    if (Same(tip, origin)) break;

                    int next = NextEdge(edges, used, outgoing, current, tip);
                    if (next < 0) break;

                    current = next;
                }

                if (points.Count >= 3) contours.Add(points);
            }

            return contours;
        }

        private static int NextEdge(List<Edge> edges, bool[] used, Dictionary<(double, double), List<int>> outgoing,
                                    int current, Point tip)
        {
            if (!outgoing.TryGetValue((tip.X, tip.Y), out List<int> candidates)) return -1;

            double inX = tip.X - edges[current].A.X, inY = tip.Y - edges[current].A.Y;

            int best = -1;
            double bestTurn = double.MaxValue;

            foreach (int candidate in candidates)
            {
                if (used[candidate]) continue;

                double outX = edges[candidate].B.X - tip.X, outY = edges[candidate].B.Y - tip.Y;

                // atan2 of the turn, measured so that a hard left is smallest. One candidate is the
                // usual case and this collapses to picking it.
                double turn = Math.Atan2(inX * outY - inY * outX, inX * outX + inY * outY);
                if (turn <= -Math.PI + 1e-12) turn += 2 * Math.PI;

                if (turn < bestTurn)
                {
                    bestTurn = turn;
                    best = candidate;
                }
            }

            return best;
        }

        // ---- snapping and output ---------------------------------------------------

        /// <summary>
        /// The quantum coordinates are rounded to. Fine enough to be invisible at any zoom, coarse
        /// enough that two intersection points computed by different routes land on the same value --
        /// which is what lets chaining match endpoints by equality instead of by search.
        /// </summary>
        private static double GridFor(List<List<Point>> a, List<List<Point>> b)
        {
            double extent = 0;
            foreach (List<List<Point>> contours in new[] { a, b })
            {
                foreach (List<Point> contour in contours)
                {
                    foreach (Point p in contour)
                    {
                        extent = Math.Max(extent, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
                    }
                }
            }

            return Math.Max(extent, 1.0) * 1e-9;
        }

        private static void SnapAll(List<List<Point>> contours, double grid)
        {
            foreach (List<Point> contour in contours)
            {
                for (int i = 0; i < contour.Count; i++) contour[i] = Snap(contour[i], grid);
            }
        }

        private static Point Snap(Point p, double grid)
            => new Point(Math.Round(p.X / grid) * grid, Math.Round(p.Y / grid) * grid);

        private static void AddContour(PathGeometry geometry, List<Point> points)
        {
            if (points.Count < 3) return;

            var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };

            var rest = new PointCollection(points.Count - 1);
            for (int i = 1; i < points.Count; i++) rest.Add(points[i]);
            figure.Segments.Add(new PolyLineSegment(rest, true));

            geometry.Figures.Add(figure);
        }
    }
}
