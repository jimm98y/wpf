// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
// Description: Implementation of the class Geometry
//
//

using MS.Internal;
using MS.Win32.PresentationCore;
using System.ComponentModel;
using System.Windows.Media.Composition;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;

namespace System.Windows.Media
{
    #region Geometry
    /// <summary>
    /// This is the base class for all Geometry classes.  A geometry has bounds,
    /// can be used to clip, fill or stroke.
    /// </summary>
    [Localizability(LocalizationCategory.None, Readability = Readability.Unreadable)]
    public abstract partial class Geometry : Animatable, DUCE.IResource
    {
        #region Constructors

        internal Geometry()
        {
        }

        #endregion

        #region Public properties
        /// <summary>
        ///     Singleton empty model.
        /// </summary>
        public static Geometry Empty
        {
            get
            {
                return s_empty;
            }
        }
        
        /// <summary>
        /// Gets the bounds of this Geometry as an axis-aligned bounding box
        /// </summary>
        public virtual Rect Bounds 
        { 
            get
            {
                return PathGeometry.GetPathBounds(
                    GetPathGeometryData(),
                    null,   // pen
                    Matrix.Identity, 
                    StandardFlatteningTolerance, 
                    ToleranceType.Absolute,
                    false); // Do not skip non-fillable figures
            }
        }

        /// <summary>
        /// Standard error tolerance (0.25) used for polygonal approximation of curved segments 
        /// </summary>
        public static double StandardFlatteningTolerance
        {
            get
            {
                return c_tolerance;
            }
        }

        #endregion Public properties

        #region GetRenderBounds
        /// <summary>
        /// Returns the axis-aligned bounding rectangle when stroked with a pen.
        /// </summary>
        /// <param name="pen">The pen</param>
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        public virtual Rect GetRenderBounds(Pen pen, double tolerance, ToleranceType type)
        {
            ReadPreamble();

            Matrix matrix = Matrix.Identity;
            return GetBoundsInternal(pen, matrix, tolerance, type);
        }

        /// <summary>
        /// Returns the axis-aligned bounding rectangle when stroked with a pen.
        /// </summary>
        /// <param name="pen">The pen</param>
        public Rect GetRenderBounds(Pen pen)
        {
            ReadPreamble();

            Matrix matrix = Matrix.Identity;
            return GetBoundsInternal(pen, matrix, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        #endregion GetRenderBounds


        #region Internal Methods

        /// <summary>
        ///     Used to optimize Visual.ChangeVisualClip. This is not meant
        ///     to be used generically since not all geometries implement
        ///     the method (currently only RectangleGeometry is implemented).
        /// </summary>
        internal virtual bool AreClose(Geometry geometry)
        {
           return false;
        }

        /// <summary>
        /// Returns the axis-aligned bounding rectangle when stroked with a pen, after applying
        /// the supplied transform (if non-null).
        /// </summary>
        internal virtual Rect GetBoundsInternal(Pen pen, Matrix matrix, double tolerance, ToleranceType type)
        {
            if (IsObviouslyEmpty())
            {
                return Rect.Empty;
            }

            PathGeometryData pathData = GetPathGeometryData();

            return PathGeometry.GetPathBounds(
                pathData,
                pen,
                matrix,
                tolerance,
                type,
                skipHollows: true);
        }
        
        /// <summary>
        /// Returns the axis-aligned bounding rectangle when stroked with a pen, after applying
        /// the supplied transform (if non-null).
        /// </summary>
        internal Rect GetBoundsInternal(Pen pen, Matrix matrix)
        {
            return GetBoundsInternal(pen, matrix, StandardFlatteningTolerance, ToleranceType.Absolute);
        }


        // Axis-aligned bounds of an affine-transformed rect (transform the 4 corners, re-bound).
        private static Rect TransformRectAffine(Rect r, Matrix m)
        {
            if (m.IsIdentity || r.IsEmpty)
            {
                return r;
            }

            Point p0 = m.Transform(new Point(r.Left, r.Top));
            Point p1 = m.Transform(new Point(r.Right, r.Top));
            Point p2 = m.Transform(new Point(r.Right, r.Bottom));
            Point p3 = m.Transform(new Point(r.Left, r.Bottom));

            double left = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
            double top = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
            double right = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
            double bottom = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));

            return new Rect(left, top, right - left, bottom - top);
        }

        internal static unsafe Rect GetBoundsHelper(
            Pen pen,
            Matrix *pWorldMatrix,
            Point* pPoints,
            byte *pTypes, 
            uint pointCount,
            uint segmentCount,
            Matrix *pGeometryMatrix,
            double tolerance, 
            ToleranceType type,
            bool fSkipHollows)
        {
            MIL_PEN_DATA penData;
            double[] dashArray = null;

            // If the pen contributes to the bounds, populate the CMD struct
            bool fPenContributesToBounds = Pen.ContributesToBounds(pen);

            if (!OperatingSystem.IsWindows())
            {
                // Off-Windows the native milcore polygon-bounds helper is unavailable; union the
                // flattened points managed (control points give a valid superset), apply the
                // geometry+world transforms, then inflate by the pen.
                double left = double.PositiveInfinity, top = double.PositiveInfinity;
                double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
                for (uint pi = 0; pi < pointCount; pi++)
                {
                    Point pt = pPoints[pi];
                    if (double.IsNaN(pt.X) || double.IsNaN(pt.Y)) continue;
                    if (pt.X < left) left = pt.X;
                    if (pt.X > right) right = pt.X;
                    if (pt.Y < top) top = pt.Y;
                    if (pt.Y > bottom) bottom = pt.Y;
                }

                if (right < left || bottom < top)
                {
                    return Rect.Empty;
                }

                Rect rawBounds = new Rect(left, top, right - left, bottom - top);

                Matrix combined = (pGeometryMatrix != null) ? *pGeometryMatrix : Matrix.Identity;
                if (pWorldMatrix != null) combined.Append(*pWorldMatrix);
                Rect managedBounds = TransformRectAffine(rawBounds, combined);

                if (fPenContributesToBounds)
                {
                    double half = pen.Thickness / 2.0;
                    managedBounds.Inflate(half, half);
                }

                return managedBounds;
            }

            if (fPenContributesToBounds)
            {
                pen.GetBasicPenData(&penData, out dashArray);
            }

            MilMatrix3x2D geometryMatrix;
            if (pGeometryMatrix != null)
            {
                geometryMatrix = CompositionResourceManager.MatrixToMilMatrix3x2D(ref (*pGeometryMatrix));
            }

            Debug.Assert(pWorldMatrix != null);
            MilMatrix3x2D worldMatrix =
                CompositionResourceManager.MatrixToMilMatrix3x2D(ref (*pWorldMatrix));

            Rect bounds;

            fixed (double *pDashArray = dashArray)
            {
                int hr = MilCoreApi.MilUtility_PolygonBounds(
                    &worldMatrix,
                    (fPenContributesToBounds) ? &penData : null,
                    (dashArray == null) ? null : pDashArray,
                    pPoints,
                    pTypes,
                    pointCount,
                    segmentCount,
                    (pGeometryMatrix == null) ? null : &geometryMatrix,
                    tolerance,
                    type == ToleranceType.Relative,
                    fSkipHollows,
                    &bounds
                );

                if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                {
                    // When we encounter NaNs in the renderer, we absorb the error and draw
                    // nothing. To be consistent, we report that the geometry has empty bounds.
                    bounds = Rect.Empty;
                }
                else
                {
                    HRESULT.Check(hr);
                }
            }

            return bounds;
        }

        internal virtual void TransformPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            // Do nothing here -- Overriden by PathGeometry to clear cached bounds.
        }

        internal Geometry GetTransformedCopy(Transform transform)
        {
            Geometry copy = Clone();
            Transform internalTransform = Transform;

            if (transform != null && !transform.IsIdentity)
            {
                if (internalTransform == null || internalTransform.IsIdentity)
                {
                    copy.Transform = transform;
                }
                else
                {
                    copy.Transform = new MatrixTransform(internalTransform.Value * transform.Value);
                }
            }

            return copy;
        }

        #endregion Internal Methods

        /// <summary>
        /// ShouldSerializeTransform - this is called by the serializer to determine whether or not to
        /// serialize the Transform property.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool ShouldSerializeTransform()
        {
            Transform transform = Transform;
            return transform != null && !(transform.IsIdentity);
        }

        #region Public Methods

        /// <summary>
        /// Gets the area of this geometry
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// </summary>
        public virtual double GetArea(double tolerance, ToleranceType type)
        {
            ReadPreamble();

            if (IsObviouslyEmpty())
            {
                return 0;
            }

            PathGeometryData pathData = GetPathGeometryData();

            if (pathData.IsEmpty())
            {
                return 0;
            }

            double area;

            unsafe
            {
                // Call the core method on the path data
                fixed (byte* pbPathData = pathData.SerializedData)
                {
                    Debug.Assert(pbPathData != (byte*)0);

                    int hr = MilCoreApi.MilUtility_GeometryGetArea(
                        pathData.FillRule,
                        pbPathData,
                        pathData.Size,
                        &pathData.Matrix,
                        tolerance,
                        type == ToleranceType.Relative,
                        &area);

                    if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                    {
                        // When we encounter NaNs in the renderer, we absorb the error and draw
                        // nothing. To be consistent, we report that the geometry has 0 area.
                        area = 0.0;
                    }
                    else
                    {
                        HRESULT.Check(hr);
                    }
                }
            }

            return area;
        }

        /// <summary>
        /// Gets the area of this geometry
        /// </summary>
        public double GetArea()
        {
            return GetArea(StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        /// <summary>
        /// Returns true if this geometry is empty
        /// </summary>
        public abstract bool IsEmpty();

        /// <summary>
        /// Returns true if this geometry may have curved segments
        /// </summary>
        public abstract bool MayHaveCurves();

        #endregion Public Methods
        
        #region Hit Testing
        /// <summary>
        /// Returns true if point is inside the fill region defined by this geometry.
        /// </summary>
        /// <param name="hitPoint">The point tested for containment</param>
        /// <param name="tolerance">Acceptable margin of error in distance computation</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        public bool FillContains(Point hitPoint, double tolerance, ToleranceType type)
        {
            return ContainsInternal(null, hitPoint, tolerance, type);
        }

        /// <summary>
        /// Returns true if point is inside the fill region defined by this geometry.
        /// </summary>
        /// <param name="hitPoint">The point tested for containment</param>
        public bool FillContains(Point hitPoint)
        {
            return ContainsInternal(null, hitPoint, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        /// <summary>
        /// Returns true if point is inside the stroke of a pen on this geometry.
        /// </summary>
        /// <param name="pen">The pen used to define the stroke</param>
        /// <param name="hitPoint">The point tested for containment</param>
        /// <param name="tolerance">Acceptable margin of error in distance computation</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        public bool StrokeContains(Pen pen, Point hitPoint, double tolerance, ToleranceType type)
        {
            if (pen == null)
            {
                return false;
            }

            return ContainsInternal(pen, hitPoint, tolerance, type);
        }

        /// <summary>
        /// Returns true if point is inside the stroke of a pen on this geometry.
        /// </summary>
        /// <param name="pen">The pen used to define the stroke</param>
        /// <param name="hitPoint">The point tested for containment</param>
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        internal virtual bool ContainsInternal(Pen pen, Point hitPoint, double tolerance, ToleranceType type)
        {
            if (IsObviouslyEmpty())
            {
                return false;
            }

            // Off-Windows there is no milcore MilUtility_PathGeometryHitTest; run the containment test
            // in managed code over the geometry's flattened figures (used for PathGeometry/
            // StreamGeometry/etc. control art like check marks and scrollbar arrows).
            if (!OperatingSystem.IsWindows())
            {
                return ManagedGeometryContains(pen, hitPoint, tolerance);
            }

            PathGeometryData pathData = GetPathGeometryData();

            if (pathData.IsEmpty())
            {
                return false;
            }

            bool contains = false;

            unsafe
            {   
                MIL_PEN_DATA penData;
                double[] dashArray = null;

                // If we have a pen, populate the CMD struct
                pen?.GetBasicPenData(&penData, out dashArray);

                fixed (byte* pbPathData = pathData.SerializedData)
                {
                    Debug.Assert(pbPathData != (byte*)0);

                    fixed (double * dashArrayFixed = dashArray)
                    {
                        int hr = MilCoreApi.MilUtility_PathGeometryHitTest(
                                &pathData.Matrix,
                                (pen == null) ? null : &penData,
                                dashArrayFixed,
                                pathData.FillRule,
                                pbPathData,
                                pathData.Size,
                                tolerance,
                                type == ToleranceType.Relative,
                                &hitPoint,
                                out contains);

                        if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                        {
                            // When we encounter NaNs in the renderer, we absorb the error and draw
                            // nothing. To be consistent, we report that the geometry is never hittable.
                            contains = false;
                        }
                        else
                        {
                            HRESULT.Check(hr);
                        }
                    }
                }
            }

            return contains;
        }

        /// <summary>
        /// Helper method to be used by derived implementations of ContainsInternal.
        /// </summary>
        internal unsafe bool ContainsInternal(Pen pen, Point hitPoint, double tolerance, ToleranceType type,
                                                Point *pPoints, uint pointCount, byte *pTypes, uint typeCount)
        {
            // Off-Windows there is no milcore MilUtility_PolygonHitTest; do the containment test in
            // managed code by flattening the figure and running a point-in-polygon (fill) or
            // distance-to-outline (stroke) test. This is what makes mouse hit-testing work on macOS.
            if (!OperatingSystem.IsWindows())
            {
                return ManagedPolygonContains(Transform, pen, hitPoint, tolerance, pPoints, pointCount, pTypes, typeCount);
            }

            bool contains = false;

            MilMatrix3x2D matrix = CompositionResourceManager.TransformToMilMatrix3x2D(Transform);

            MIL_PEN_DATA penData;
            double[] dashArray = null;

            pen?.GetBasicPenData(&penData, out dashArray);

            fixed (double *dashArrayFixed = dashArray)
            {
                int hr = MilCoreApi.MilUtility_PolygonHitTest(
                        &matrix,
                        (pen == null) ? null : &penData,
                        dashArrayFixed,
                        pPoints,
                        pTypes,
                        pointCount,
                        typeCount,
                        tolerance,
                        type == ToleranceType.Relative,
                        &hitPoint,
                        out contains);

                if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                {
                    // When we encounter NaNs in the renderer, we absorb the error and draw
                    // nothing. To be consistent, we report that the geometry is never hittable.
                    contains = false;
                }
                else
                {
                    HRESULT.Check(hr);
                }
            }

            return contains;
        }

        /// <summary>
        /// Managed replacement for milcore's MilUtility_PolygonHitTest (used off-Windows). Flattens the
        /// figure described by pPoints/pTypes (transformed by the geometry's Transform), then tests the
        /// hit point against it: fill containment (even-odd) when there is no pen, otherwise whether the
        /// point lies within half the pen thickness of the outline.
        /// </summary>
        private static unsafe bool ManagedPolygonContains(Transform transform, Pen pen, Point hitPoint,
            double tolerance, Point* pPoints, uint pointCount, byte* pTypes, uint segCount)
        {
            if (pointCount == 0 || segCount == 0)
            {
                return false;
            }

            Matrix m = (transform != null) ? transform.Value : Matrix.Identity;

            var poly = new System.Collections.Generic.List<Point>((int)pointCount);
            Point cur = m.Transform(pPoints[0]);
            poly.Add(cur);

            int idx = 1;
            bool closed = (pTypes[0] & (byte)MILCoreSegFlags.SegClosed) != 0;

            for (uint s = 0; s < segCount; s++)
            {
                byte segType = (byte)(pTypes[s] & (byte)MILCoreSegFlags.SegTypeMask);
                if (segType == (byte)MILCoreSegFlags.SegTypeLine)
                {
                    if (idx >= pointCount) break;
                    cur = m.Transform(pPoints[idx++]);
                    poly.Add(cur);
                }
                else if (segType == (byte)MILCoreSegFlags.SegTypeBezier)
                {
                    if (idx + 2 >= pointCount) break;
                    Point c1 = m.Transform(pPoints[idx++]);
                    Point c2 = m.Transform(pPoints[idx++]);
                    Point end = m.Transform(pPoints[idx++]);
                    const int steps = 24;
                    for (int i = 1; i <= steps; i++)
                    {
                        double t = i / (double)steps;
                        poly.Add(FlattenBezier(cur, c1, c2, end, t));
                    }
                    cur = end;
                }
            }

            if (pen == null)
            {
                return PointInPolygon(poly, hitPoint);
            }

            // Stroke containment: within half the pen thickness (plus tolerance) of any outline edge.
            double half = pen.Thickness / 2.0 + Math.Max(tolerance, 0.0);
            int n = poly.Count;
            int edges = closed ? n : n - 1;
            for (int i = 0; i < edges; i++)
            {
                if (DistanceToSegment(hitPoint, poly[i], poly[(i + 1) % n]) <= half)
                {
                    return true;
                }
            }
            return false;
        }

        private static Point FlattenBezier(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
            return new Point(w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
                             w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
        }

        // Even-odd ray-cast containment; the polygon is treated as implicitly closed.
        private static bool PointInPolygon(System.Collections.Generic.List<Point> poly, Point pt)
        {
            int n = poly.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point pi = poly[i], pj = poly[j];
                if (((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                    (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        // Managed replacement for milcore's MilUtility_PathGeometryHitTest (used off-Windows). Converts
        // the geometry to a PathGeometry, flattens each figure to a polyline, and tests the hit point:
        // fill (even-odd / nonzero per FillRule) with no pen, or distance-to-outline with a pen. Falls
        // back to bounding-box containment if the geometry can't be converted (e.g. boolean combine).
        private bool ManagedGeometryContains(Pen pen, Point hitPoint, double tolerance)
        {
            PathGeometry pg = null;
            try { pg = GetAsPathGeometry(); } catch { pg = null; }

            if (pg == null || pg.Figures == null || pg.Figures.Count == 0)
            {
                Rect b = Bounds;
                return !b.IsEmpty && b.Contains(hitPoint);
            }

            Matrix m = (pg.Transform != null) ? pg.Transform.Value : Matrix.Identity;

            var figures = new System.Collections.Generic.List<(System.Collections.Generic.List<Point> Pts, bool Closed)>();
            foreach (PathFigure fig in pg.Figures)
            {
                var pts = new System.Collections.Generic.List<Point>();
                Point cur = m.Transform(fig.StartPoint);
                pts.Add(cur);
                foreach (PathSegment seg in fig.Segments)
                {
                    FlattenSegment(seg, m, ref cur, pts);
                }
                if (pts.Count >= 2)
                {
                    figures.Add((pts, fig.IsClosed));
                }
            }

            if (pen == null)
            {
                // Fill: accumulate ray-crossings across all figures (each treated as closed).
                bool parity = false;
                int winding = 0;
                foreach (var (pts, _) in figures)
                {
                    int n = pts.Count;
                    for (int i = 0; i < n; i++)
                    {
                        Point a = pts[i], b = pts[(i + 1) % n];
                        if ((a.Y > hitPoint.Y) != (b.Y > hitPoint.Y))
                        {
                            double xCross = (b.X - a.X) * (hitPoint.Y - a.Y) / (b.Y - a.Y) + a.X;
                            if (hitPoint.X < xCross)
                            {
                                parity = !parity;
                                winding += (b.Y > a.Y) ? 1 : -1;
                            }
                        }
                    }
                }
                return (pg.FillRule == FillRule.Nonzero) ? (winding != 0) : parity;
            }

            // Stroke: within half the pen thickness of any (closed figures include the closing edge).
            double half = pen.Thickness / 2.0 + Math.Max(tolerance, 0.0);
            foreach (var (pts, closed) in figures)
            {
                int n = pts.Count;
                int edges = closed ? n : n - 1;
                for (int i = 0; i < edges; i++)
                {
                    if (DistanceToSegment(hitPoint, pts[i], pts[(i + 1) % n]) <= half)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Appends the flattened points of a single path segment (excluding its start point, already in
        // the list) and advances 'cur' to the segment's end point. All points are in transformed space.
        private static void FlattenSegment(PathSegment seg, Matrix m, ref Point cur, System.Collections.Generic.List<Point> pts)
        {
            switch (seg)
            {
                case LineSegment ls:
                    cur = m.Transform(ls.Point); pts.Add(cur);
                    break;
                case PolyLineSegment pls:
                    foreach (Point p in pls.Points) { cur = m.Transform(p); pts.Add(cur); }
                    break;
                case BezierSegment bs:
                {
                    Point c1 = m.Transform(bs.Point1), c2 = m.Transform(bs.Point2), e = m.Transform(bs.Point3);
                    for (int i = 1; i <= 24; i++) pts.Add(FlattenBezier(cur, c1, c2, e, i / 24.0));
                    cur = e;
                    break;
                }
                case PolyBezierSegment pbs:
                {
                    var pc = pbs.Points;
                    for (int k = 0; k + 2 < pc.Count; k += 3)
                    {
                        Point c1 = m.Transform(pc[k]), c2 = m.Transform(pc[k + 1]), e = m.Transform(pc[k + 2]);
                        for (int i = 1; i <= 24; i++) pts.Add(FlattenBezier(cur, c1, c2, e, i / 24.0));
                        cur = e;
                    }
                    break;
                }
                case QuadraticBezierSegment qs:
                {
                    Point c = m.Transform(qs.Point1), e = m.Transform(qs.Point2);
                    for (int i = 1; i <= 16; i++) pts.Add(FlattenQuadratic(cur, c, e, i / 16.0));
                    cur = e;
                    break;
                }
                case PolyQuadraticBezierSegment pqs:
                {
                    var pc = pqs.Points;
                    for (int k = 0; k + 1 < pc.Count; k += 2)
                    {
                        Point c = m.Transform(pc[k]), e = m.Transform(pc[k + 1]);
                        for (int i = 1; i <= 16; i++) pts.Add(FlattenQuadratic(cur, c, e, i / 16.0));
                        cur = e;
                    }
                    break;
                }
                case ArcSegment arc:
                    // Arcs are rare in control art; approximate by a line to the end point.
                    cur = m.Transform(arc.Point); pts.Add(cur);
                    break;
            }
        }

        private static Point FlattenQuadratic(Point p0, Point p1, Point p2, double t)
        {
            double u = 1 - t;
            double w0 = u * u, w1 = 2 * u * t, w2 = t * t;
            return new Point(w0 * p0.X + w1 * p1.X + w2 * p2.X,
                             w0 * p0.Y + w1 * p1.Y + w2 * p2.Y);
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= double.Epsilon)
            {
                double ex = p.X - a.X, ey = p.Y - a.Y;
                return Math.Sqrt(ex * ex + ey * ey);
            }
            double u = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            if (u < 0) u = 0; else if (u > 1) u = 1;
            double cx = a.X + u * dx, cy = a.Y + u * dy;
            double ox = p.X - cx, oy = p.Y - cy;
            return Math.Sqrt(ox * ox + oy * oy);
        }

        /// <summary>
        /// Returns true if point is inside the stroke of a pen on this geometry.
        /// </summary>
        /// <param name="pen">The pen used to define the stroke</param>
        /// <param name="hitPoint">The point tested for containment</param>
        public bool StrokeContains(Pen pen, Point hitPoint)
        {
            return StrokeContains(pen, hitPoint, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        /// <summary>
        /// Returns true if a given geometry is contained inside this geometry.
        /// </summary>
        /// <param name="geometry">The geometry tested for containment</param>
        /// <param name="tolerance">Acceptable margin of error in distance computation</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        public bool FillContains(Geometry geometry, double tolerance, ToleranceType type)
        {
            IntersectionDetail detail = FillContainsWithDetail(geometry, tolerance, type);

            return (detail == IntersectionDetail.FullyContains);
        }

        /// <summary>
        /// Returns true if a given geometry is contained inside this geometry.
        /// </summary>
        /// <param name="geometry">The geometry tested for containment</param>
        public bool FillContains(Geometry geometry)
        {
            return FillContains(geometry, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        /// <summary>
        /// Returns true if a given geometry is inside this geometry.
        /// <param name="geometry">The geometry to test for containment in this Geometry</param>
        /// <param name="tolerance">Acceptable margin of error in distance computation</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// </summary>
        public virtual IntersectionDetail FillContainsWithDetail(Geometry geometry, double tolerance, ToleranceType type)
        {
            ReadPreamble();

            if (IsObviouslyEmpty() || geometry == null || geometry.IsObviouslyEmpty())
            {
                return IntersectionDetail.Empty;
            }

            return PathGeometry.HitTestWithPathGeometry(this, geometry, tolerance, type);
        }


        /// <summary>
        /// Returns if geometry is inside this geometry.
        /// <param name="geometry">The geometry to test for containment in this Geometry</param>
        /// </summary>
        public IntersectionDetail FillContainsWithDetail(Geometry geometry)
        {
            return FillContainsWithDetail(geometry, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        /// <summary>
        /// Returns if a given geometry is inside the stroke defined by a given pen on this geometry.
        /// <param name="pen">The pen</param>
        /// <param name="geometry">The geometry to test for containment in this Geometry</param>
        /// <param name="tolerance">Acceptable margin of error in distance computation</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// </summary>
        public IntersectionDetail StrokeContainsWithDetail(Pen pen, Geometry geometry, double tolerance, ToleranceType type)
        {
            if (IsObviouslyEmpty() || geometry == null || geometry.IsObviouslyEmpty() || pen == null)
            {
                return IntersectionDetail.Empty;
            }

            PathGeometry pathGeometry1 = GetWidenedPathGeometry(pen);

            return PathGeometry.HitTestWithPathGeometry(pathGeometry1, geometry, tolerance, type);
        }               

        /// <summary>
        /// Returns if a given geometry is inside the stroke defined by a given pen on this geometry.
        /// <param name="pen">The pen</param>
        /// <param name="geometry">The geometry to test for containment in this Geometry</param>
        /// </summary>
        public IntersectionDetail StrokeContainsWithDetail(Pen pen, Geometry geometry)
        {
            return StrokeContainsWithDetail(pen, geometry, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        #endregion

        #region Geometric Flatten

        /// <summary>
        /// Approximate this geometry with a polygonal PathGeometry
        /// </summary>
        /// <param name="tolerance">The approximation error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// <returns>Returns the polygonal approximation as a PathGeometry.</returns>
        public virtual PathGeometry GetFlattenedPathGeometry(double tolerance, ToleranceType type)
        {
            ReadPreamble();

            if (IsObviouslyEmpty())
            {
                return new PathGeometry();
            }

            PathGeometryData pathData = GetPathGeometryData();

            if (pathData.IsEmpty())
            {
                return new PathGeometry();
            }

            PathGeometry resultGeometry = null;

            unsafe
            {
                fixed (byte *pbPathData = pathData.SerializedData)
                {
                    Debug.Assert(pbPathData != (byte*)0);

                    FillRule fillRule = FillRule.Nonzero;

                    PathGeometry.FigureList list = new PathGeometry.FigureList();
                    
                    int hr = UnsafeNativeMethods.MilCoreApi.MilUtility_PathGeometryFlatten(
                        &pathData.Matrix,
                        pathData.FillRule,
                        pbPathData,
                        pathData.Size,
                        tolerance,
                        type == ToleranceType.Relative,
                        new PathGeometry.AddFigureToListDelegate(list.AddFigureToList),
                        out fillRule);

                    if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                    {
                        // When we encounter NaNs in the renderer, we absorb the error and draw
                        // nothing. To be consistent, we return an empty geometry.
                        resultGeometry = new PathGeometry();
                    }
                    else
                    {
                        HRESULT.Check(hr);

                        resultGeometry = new PathGeometry(list.Figures, fillRule, null);
                    }
                }

                return resultGeometry;
            }
        }

        /// <summary>
        /// Approximate this geometry with a polygonal PathGeometry
        /// </summary>
        /// <returns>Returns the polygonal approximation as a PathGeometry.</returns>
        public PathGeometry GetFlattenedPathGeometry()
        {
            // Use the default tolerance interpreted as absolute
            return GetFlattenedPathGeometry(StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        #endregion Flatten

        #region Geometric Widen

        /// <summary>
        /// Create the contour of the stroke defined by given pen when it draws this path
        /// </summary>
        /// <param name="pen">The pen used for stroking this path</param>
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// <returns>Returns the contour as a PathGeometry.</returns>
        public virtual PathGeometry GetWidenedPathGeometry(Pen pen, double tolerance, ToleranceType type)
        {
            ReadPreamble();

            ArgumentNullException.ThrowIfNull(pen);

            if (IsObviouslyEmpty())
            {
                return new PathGeometry();
            }

            PathGeometryData pathData = GetPathGeometryData();

            if (pathData.IsEmpty())
            {
                return new PathGeometry();
            }

            PathGeometry resultGeometry = null;

            unsafe
            {
                MIL_PEN_DATA penData;

                pen.GetBasicPenData(&penData, out double[] dashArray);

                fixed (byte* pbPathData = pathData.SerializedData)
                {
                    Debug.Assert(pbPathData != (byte*)0);

                    FillRule fillRule = FillRule.Nonzero;
                    PathGeometry.FigureList list = new();

                    int hr; //If we don't have dashArray, we call without it (its optional)
                    if (dashArray is null)
                    {
                        hr = UnsafeNativeMethods.MilCoreApi.MilUtility_PathGeometryWiden(&penData,
                                                                                         null,
                                                                                         &pathData.Matrix,
                                                                                         pathData.FillRule,
                                                                                         pbPathData,
                                                                                         pathData.Size,
                                                                                         tolerance,
                                                                                         type == ToleranceType.Relative,
                                                                                         new PathGeometry.AddFigureToListDelegate(list.AddFigureToList),
                                                                                         out fillRule);
                    }
                    else // Pin the dashArray and use it, if we have one.
                    {
                        fixed (double* ptrDashArray = dashArray)
                        {
                            hr = UnsafeNativeMethods.MilCoreApi.MilUtility_PathGeometryWiden(&penData,
                                                                                             ptrDashArray,
                                                                                             &pathData.Matrix,
                                                                                             pathData.FillRule,
                                                                                             pbPathData,
                                                                                             pathData.Size,
                                                                                             tolerance,
                                                                                             type == ToleranceType.Relative,
                                                                                             new PathGeometry.AddFigureToListDelegate(list.AddFigureToList),
                                                                                             out fillRule);
                        }
                    }

                    if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                    {
                        // When we encounter NaNs in the renderer, we absorb the error and draw
                        // nothing. To be consistent, we return an empty geometry.
                        resultGeometry = new PathGeometry();
                    }
                    else
                    {
                        HRESULT.Check(hr);

                        resultGeometry = new PathGeometry(list.Figures, fillRule, null);
                    }

                    return resultGeometry;
                }
            }
        }

        /// <summary>
        /// Create the contour of the stroke defined by given pen when it draws this path
        /// </summary>
        /// <param name="pen">The pen used for stroking this path</param>
        /// <returns>Returns the contour as a PathGeometry.</returns>
        public PathGeometry GetWidenedPathGeometry(Pen pen)
        {
            // Use the default tolerance interpreted as absolute
            return GetWidenedPathGeometry(pen, StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        #endregion Widen
       
        #region Combine

        /// <summary>
        /// Returns the result of a Boolean combination of two Geometry objects.
        /// </summary>
        /// <param name="geometry1">The first Geometry object</param>
        /// <param name="geometry2">The second Geometry object</param>
        /// <param name="mode">The mode in which the objects will be combined</param>
        /// <param name="transform">A transformation to apply to the result, or null</param>
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        public static PathGeometry Combine(
            Geometry geometry1,
            Geometry geometry2,
            GeometryCombineMode mode,
            Transform transform,
            double tolerance,
            ToleranceType type)
        {
            return PathGeometry.InternalCombine(geometry1, geometry2, mode, transform, tolerance, type);
        }

        /// <summary>
        /// Returns the result of a Boolean combination of two Geometry objects.
        /// </summary>
        /// <param name="geometry1">The first Geometry object</param>
        /// <param name="geometry2">The second Geometry object</param>
        /// <param name="mode">The mode in which the objects will be combined</param>
        /// <param name="transform">A transformation to apply to the result, or null</param>
        public static PathGeometry Combine(
            Geometry geometry1,
            Geometry geometry2,
            GeometryCombineMode mode,
            Transform transform)
        {
            return PathGeometry.InternalCombine(
                geometry1, 
                geometry2, 
                mode, 
                transform, 
                Geometry.StandardFlatteningTolerance, 
                ToleranceType.Absolute);
        }

        #endregion Combine

        #region Outline

        /// <summary>
        /// Get a simplified contour of the filled region of this PathGeometry
        /// </summary>
        /// <param name="tolerance">The computational error tolerance</param>
        /// <param name="type">The way the error tolerance will be interpreted - relative or absolute</param>
        /// <returns>Returns an equivalent geometry, properly oriented with no self-intersections.</returns>
        public virtual PathGeometry GetOutlinedPathGeometry(double tolerance, ToleranceType type)
        {
            ReadPreamble();

            if (IsObviouslyEmpty())
            {
                return new PathGeometry();
            }

            PathGeometryData pathData = GetPathGeometryData();

            if (pathData.IsEmpty())
            {
                return new PathGeometry();
            }

            PathGeometry resultGeometry = null;

            unsafe
            {
                fixed (byte* pbPathData = pathData.SerializedData)
                {
                    Invariant.Assert(pbPathData != (byte*)0);

                    FillRule fillRule = FillRule.Nonzero;
                    PathGeometry.FigureList list = new PathGeometry.FigureList();

                    int hr = UnsafeNativeMethods.MilCoreApi.MilUtility_PathGeometryOutline(
                        &pathData.Matrix,
                        pathData.FillRule,
                        pbPathData,
                        pathData.Size,
                        tolerance,
                        type == ToleranceType.Relative,
                        new PathGeometry.AddFigureToListDelegate(list.AddFigureToList),
                        out fillRule);

                    if (hr == (int)MILErrors.WGXERR_BADNUMBER)
                    {
                        // When we encounter NaNs in the renderer, we absorb the error and draw
                        // nothing. To be consistent, we return an empty geometry.
                        resultGeometry = new PathGeometry();
                    }
                    else
                    {
                        HRESULT.Check(hr);

                        resultGeometry = new PathGeometry(list.Figures, fillRule, null);
                    }
                }
            }

            return resultGeometry;
        }

        /// <summary>
        /// Get a simplified contour of the filled region of this PathGeometry
        /// </summary>
        /// <returns>Returns an equivalent geometry, properly oriented with no self-intersections.</returns>
        public PathGeometry GetOutlinedPathGeometry()
        {
            return GetOutlinedPathGeometry(StandardFlatteningTolerance, ToleranceType.Absolute);
        }

        #endregion Outline

        #region Internal

        internal abstract PathGeometry GetAsPathGeometry();
        
        /// <summary>
        /// GetPathGeometryData - returns a struct which contains this Geometry represented
        /// as a path geometry's serialized format.
        /// </summary>
        internal abstract PathGeometryData GetPathGeometryData();

        internal PathFigureCollection GetPathFigureCollection()
        {
            return GetTransformedFigureCollection(null);
        }

        // Get the combination of the internal transform with a given transform.
        // Return true if the result is nontrivial.
        internal Matrix GetCombinedMatrix(Transform transform)
        {
            Matrix matrix = Matrix.Identity;
            Transform internalTransform = Transform;

            if (internalTransform != null && !internalTransform.IsIdentity)
            {
                matrix = internalTransform.Value;

                if (transform != null && !transform.IsIdentity)
                {
                    matrix *= transform.Value;
                }
            }
            else if (transform != null && !transform.IsIdentity)
            {
                matrix = transform.Value;
            }

            return matrix;
        }

        internal abstract PathFigureCollection GetTransformedFigureCollection(Transform transform);

        // This method is used for eliminating unnecessary work when the geometry is obviously empty.
        // For most Geometry types the definite IsEmpty() query is just as cheap.  The exceptions will 
        // be CombinedGeometry and GeometryGroup.
        internal virtual bool IsObviouslyEmpty() { return IsEmpty(); }

        /// <summary>
        /// Can serialize "this" to a string
        /// </summary>
        internal virtual bool CanSerializeToString()
        {
            return false;
        }

        internal struct PathGeometryData
        {
            internal bool IsEmpty()
            {
                if ((SerializedData == null) || (SerializedData.Length <= 0))
                {
                    return true;
                }

                unsafe
                {
                    fixed (byte *pbPathData = SerializedData)
                    {
                        MIL_PATHGEOMETRY* pPathGeometry = (MIL_PATHGEOMETRY*)pbPathData;

                        return pPathGeometry->FigureCount <= 0;
                    }
                }
            }

            internal FillRule FillRule;
            internal MilMatrix3x2D Matrix;
            internal byte[] SerializedData;

            internal uint Size
            {
                get
                {
                    if ((SerializedData == null) || (SerializedData.Length <= 0))
                    {
                        return 0;
                    }

                    unsafe
                    {
                        fixed (byte *pbPathData = SerializedData)
                        {
                            MIL_PATHGEOMETRY* pPathGeometryData = (MIL_PATHGEOMETRY*)pbPathData;
                            uint size = pPathGeometryData == null ? 0 : pPathGeometryData->Size;
                            
                            Invariant.Assert(size <= (uint)SerializedData.Length);

                            return size;
                        }
                    }
                }
            }
        }

        internal static PathGeometryData GetEmptyPathGeometryData()
        {
            return s_emptyPathGeometryData;
        }
        
        #endregion Internal 

        #region Private

        private static PathGeometryData MakeEmptyPathGeometryData()
        {
            PathGeometryData data = new PathGeometryData
            {
                FillRule = FillRule.EvenOdd,
                Matrix = CompositionResourceManager.MatrixToMilMatrix3x2D(Matrix.Identity)
            };

            unsafe
            {
                int size = sizeof(MIL_PATHGEOMETRY);

                data.SerializedData = new byte[size];

                fixed (byte *pbData = data.SerializedData)
                {
                    MIL_PATHGEOMETRY *pPathGeometry = (MIL_PATHGEOMETRY*)pbData;

                    // implicitly set pPathGeometry->Flags = 0;
                    pPathGeometry->FigureCount = 0;
                    pPathGeometry->Size = (UInt32)size;
                }
            }

            return data;
        }

        private static Geometry MakeEmptyGeometry()
        {
            Geometry empty = new StreamGeometry();
            empty.Freeze();
            return empty;
        }
        
        private const double c_tolerance = 0.25;

        private static Geometry s_empty = MakeEmptyGeometry();
        private static PathGeometryData s_emptyPathGeometryData = MakeEmptyPathGeometryData();
        #endregion Private
    }
    #endregion
}
