// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Internal;
using System.Windows.Media.Composition;
using System.Windows.Markup;

using UnsafeNativeMethods = MS.Win32.PresentationCore.UnsafeNativeMethods;

namespace System.Windows.Media
{
    #region PathGeometryInternalFlags
    [System.Flags]
    internal enum PathGeometryInternalFlags
    {
        None            = 0x0,
        Invalid         = 0x1,
        Dirty           = 0x2,
        BoundsValid     = 0x4
    }
    #endregion

    #region PathGeometry
    /// <summary>
    /// PathGeometry
    /// </summary>
    [ContentProperty("Figures")]
    public sealed partial class PathGeometry : Geometry
    {
        #region Constructors
        /// <summary>
        ///
        /// </summary>
        public PathGeometry()
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="figures">A collection of figures</param>
        public PathGeometry(IEnumerable<PathFigure> figures)
        {
            ArgumentNullException.ThrowIfNull(figures);

            foreach (PathFigure item in figures)
            {
                Figures.Add(item);
            }

            SetDirty();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="figures">A collection of figures</param>
        /// <param name="fillRule">The fill rule (OddEven or NonZero)</param>
        /// <param name="transform">A transformation to apply to the input</param>
        public PathGeometry(IEnumerable<PathFigure> figures, FillRule fillRule, Transform transform)
        {
            Transform = transform;
            if (ValidateEnums.IsFillRuleValid(fillRule))
            {
                FillRule = fillRule;

                ArgumentNullException.ThrowIfNull(figures);

                foreach (PathFigure item in figures)
                {
                    Figures.Add(item);
                }

                SetDirty();
            }
        }

        /// <summary>
        /// Static "CreateFromGeometry" method which creates a new PathGeometry from the Geometry specified.
        /// </summary>
        /// <param name="geometry"> 
        /// Geometry - The Geometry which will be used as the basis for the newly created
        /// PathGeometry.  The new Geometry will be based on the current value of all properties.
        /// </param>
        public static PathGeometry CreateFromGeometry(Geometry geometry)
        {
            if (geometry == null)
            {
                return null;
            }

            return geometry.GetAsPathGeometry();
        }

        /// <summary>
        /// Static method which parses a PathGeometryData and makes calls into the provided context sink.
        /// This can be used to build a PathGeometry, for readback, etc.
        /// </summary>
        internal static void ParsePathGeometryData(PathGeometryData pathData, CapacityStreamGeometryContext ctx)
        {
            if (pathData.IsEmpty())
            {
                return;
            }

            unsafe
            {
                int currentOffset = 0;

                fixed (byte* pbData = pathData.SerializedData)
                {
                    // This assert is a logical correctness test
                    Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_PATHGEOMETRY));

                    // ... while this assert tests "physical" correctness (i.e. are we running out of buffer).
                    Invariant.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_PATHGEOMETRY));

                    MIL_PATHGEOMETRY *pPathGeometry = (MIL_PATHGEOMETRY*)pbData;

                    // Move the current offset to after the Path's data
                    currentOffset += sizeof(MIL_PATHGEOMETRY);

                    // Are there any Figures to add?
                    if (pPathGeometry->FigureCount > 0)
                    {
                        // Allocate the correct number of Figures up front
                        ctx.SetFigureCount((int)pPathGeometry->FigureCount);

                        // ... and iterate on the Figures.
                        for (int i = 0; i < pPathGeometry->FigureCount; i++)
                        {
                            // We only expect well-formed data, but we should assert that we're not reading
                            // too much data.
                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_PATHFIGURE));

                            MIL_PATHFIGURE *pPathFigure = (MIL_PATHFIGURE*)(pbData + currentOffset);

                            // Move the current offset to the after of the Figure's data
                            currentOffset += sizeof(MIL_PATHFIGURE);

                            ctx.BeginFigure(pPathFigure->StartPoint, 
                                            ((pPathFigure->Flags & MilPathFigureFlags.IsFillable) != 0),
                                            ((pPathFigure->Flags & MilPathFigureFlags.IsClosed) != 0));

                            if (pPathFigure->Count > 0)
                            {
                                // Allocate the correct number of Segments up front
                                ctx.SetSegmentCount((int)pPathFigure->Count);

                                // ... and iterate on the Segments.
                                for (int j = 0; j < pPathFigure->Count; j++)
                                {
                                    // We only expect well-formed data, but we should assert that we're not reading too much data.
                                    Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT));
                                    Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT));

                                    MIL_SEGMENT *pSegment = (MIL_SEGMENT*)(pbData + currentOffset);

                                    switch (pSegment->Type)
                                    {
                                    case MIL_SEGMENT_TYPE.MilSegmentLine:
                                        {
                                            // We only expect well-formed data, but we should assert that we're not reading too much data.
                                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT_LINE));
                                            Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT_LINE));

                                            MIL_SEGMENT_LINE *pSegmentLine = (MIL_SEGMENT_LINE*)(pbData + currentOffset);

                                            ctx.LineTo(pSegmentLine->Point, 
                                                       ((pSegmentLine->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                       ((pSegmentLine->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));

                                            currentOffset += sizeof(MIL_SEGMENT_LINE);
                                        }
                                        break;
                                    case MIL_SEGMENT_TYPE.MilSegmentBezier:
                                        {
                                            // We only expect well-formed data, but we should assert that we're not reading too much data.
                                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT_BEZIER));
                                            Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT_BEZIER));

                                            MIL_SEGMENT_BEZIER *pSegmentBezier = (MIL_SEGMENT_BEZIER*)(pbData + currentOffset);

                                            ctx.BezierTo(pSegmentBezier->Point1, 
                                                         pSegmentBezier->Point2,
                                                         pSegmentBezier->Point3,
                                                         ((pSegmentBezier->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                         ((pSegmentBezier->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                            
                                            currentOffset += sizeof(MIL_SEGMENT_BEZIER);
                                        }
                                        break;
                                    case MIL_SEGMENT_TYPE.MilSegmentQuadraticBezier:
                                        {
                                            // We only expect well-formed data, but we should assert that we're not reading too much data.
                                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT_QUADRATICBEZIER));
                                            Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT_QUADRATICBEZIER));

                                            MIL_SEGMENT_QUADRATICBEZIER *pSegmentQuadraticBezier = (MIL_SEGMENT_QUADRATICBEZIER*)(pbData + currentOffset);

                                            ctx.QuadraticBezierTo(pSegmentQuadraticBezier->Point1, 
                                                                  pSegmentQuadraticBezier->Point2,
                                                                  ((pSegmentQuadraticBezier->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                                  ((pSegmentQuadraticBezier->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                            
                                            currentOffset += sizeof(MIL_SEGMENT_QUADRATICBEZIER);
                                        }
                                        break;
                                    case MIL_SEGMENT_TYPE.MilSegmentArc:
                                        {
                                            // We only expect well-formed data, but we should assert that we're not reading too much data.
                                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT_ARC));
                                            Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT_ARC));

                                            MIL_SEGMENT_ARC *pSegmentArc = (MIL_SEGMENT_ARC*)(pbData + currentOffset);

                                            ctx.ArcTo(pSegmentArc->Point,
                                                      pSegmentArc->Size,
                                                      pSegmentArc->XRotation,
                                                      (pSegmentArc->LargeArc != 0),
                                                      (pSegmentArc->Sweep == 0) ? SweepDirection.Counterclockwise : SweepDirection.Clockwise,
                                                      ((pSegmentArc->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                      ((pSegmentArc->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                            
                                            currentOffset += sizeof(MIL_SEGMENT_ARC);
                                        }
                                        break;
                                    case MIL_SEGMENT_TYPE.MilSegmentPolyLine:
                                    case MIL_SEGMENT_TYPE.MilSegmentPolyBezier:
                                    case MIL_SEGMENT_TYPE.MilSegmentPolyQuadraticBezier:
                                        {
                                            // We only expect well-formed data, but we should assert that we're not reading too much data.
                                            Debug.Assert(pathData.SerializedData.Length >= currentOffset + sizeof(MIL_SEGMENT_POLY));
                                            Debug.Assert(pathData.Size >= currentOffset + sizeof(MIL_SEGMENT_POLY));

                                            MIL_SEGMENT_POLY *pSegmentPoly = (MIL_SEGMENT_POLY*)(pbData + currentOffset);

                                            Debug.Assert(pSegmentPoly->Count <= Int32.MaxValue);

                                            if (pSegmentPoly->Count > 0)
                                            {
                                                List<Point> points = new List<Point>((int)pSegmentPoly->Count);

                                                // We only expect well-formed data, but we should assert that we're not reading too much data.
                                                Debug.Assert(pathData.SerializedData.Length >= 
                                                             currentOffset + 
                                                             sizeof(MIL_SEGMENT_POLY) +
                                                             (int)pSegmentPoly->Count * sizeof(Point));
                                                Debug.Assert(pathData.Size >= 
                                                             currentOffset + 
                                                             sizeof(MIL_SEGMENT_POLY) +
                                                             (int)pSegmentPoly->Count * sizeof(Point));

                                                Point* pPoint = (Point*)(pbData + currentOffset + sizeof(MIL_SEGMENT_POLY));

                                                for (uint k = 0; k < pSegmentPoly->Count; k++)
                                                {
                                                    points.Add(*pPoint);
                                                    pPoint++;
                                                }

                                                switch (pSegment->Type)
                                                {
                                                case MIL_SEGMENT_TYPE.MilSegmentPolyLine:
                                                    ctx.PolyLineTo(points,
                                                                   ((pSegmentPoly->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                                   ((pSegmentPoly->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                                    break;
                                                case MIL_SEGMENT_TYPE.MilSegmentPolyBezier:
                                                    ctx.PolyBezierTo(points,
                                                                     ((pSegmentPoly->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                                     ((pSegmentPoly->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                                    break;
                                                case MIL_SEGMENT_TYPE.MilSegmentPolyQuadraticBezier:
                                                    ctx.PolyQuadraticBezierTo(points,
                                                                   ((pSegmentPoly->Flags & MILCoreSegFlags.SegIsAGap) == 0),
                                                                   ((pSegmentPoly->Flags & MILCoreSegFlags.SegSmoothJoin) != 0));
                                                    break;
                                                }
                                            }

                                            currentOffset += sizeof(MIL_SEGMENT_POLY) + (int)pSegmentPoly->Count * sizeof(Point);
                                        }
                                        break;
#if DEBUG
                                    case MIL_SEGMENT_TYPE.MilSegmentNone:
                                        throw new System.InvalidOperationException();
                                    default:
                                        throw new System.InvalidOperationException();
#endif
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Implementation of <see cref="System.Windows.Freezable.OnChanged">Freezable.OnChanged</see>.
        /// </summary>
        protected override void OnChanged()
        {
            SetDirty();
            
            base.OnChanged();
        }

        #region GetTransformedFigureCollection
        internal override PathFigureCollection GetTransformedFigureCollection(Transform transform)
        {
            // Combine the transform argument with the internal transform
            Matrix matrix = GetCombinedMatrix(transform);

            // Get the figure collection
            PathFigureCollection result;

            if (matrix.IsIdentity)
            {
                // There is no need to transform, return the figure collection
                result = Figures;
                if (result == null)
                {
                    result = new PathFigureCollection();
                }
            }
            else
            {
                // Return a transformed copy of the figure collection
                result = new PathFigureCollection();
                PathFigureCollection figures = Figures;
                int count = figures != null ? figures.Count : 0;
                for (int i = 0; i < count; ++i)
                {
                    PathFigure figure = figures.Internal_GetItem(i);
                    result.Add(figure.GetTransformedCopy(matrix));
                }
            }

            Debug.Assert(result != null);
            return result;
        }
        #endregion

        #region PathFigure/Geometry
        /// <summary>
        ///
        /// </summary>
        public void AddGeometry(Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(geometry);

            if (geometry.IsEmpty())
            {
                return;
            }

            PathFigureCollection figureCollection = geometry.GetPathFigureCollection();
            Debug.Assert(figureCollection != null);

            PathFigureCollection figures = Figures;

            if (figures == null)
            {
                figures = Figures = new PathFigureCollection();
            }

            for (int i = 0; i < figureCollection.Count; ++i)
            {
                figures.Add(figureCollection.Internal_GetItem(i));
            }
        }

        #endregion

        #region FigureList class
        ///<summary>
        /// List of figures, populated by callbacks from unmanaged code
        ///</summary>
        internal class FigureList
        {
            ///<summary>
            /// Constructor
            ///</summary>
            internal FigureList()
            {
                _figures = new PathFigureCollection();
            }

            ///<summary>
            /// Figures - the array of figures
            ///</summary>
            internal PathFigureCollection Figures
            {
                get
                {
                    return _figures;
                }
            }

            #endregion FigureList class

            ///<summary>
            /// Callback method, used for adding a figure to the list
            ///</summary>
            ///<param name="isFilled">
            /// The figure is filled
            ///</param>
            ///<param name="isClosed">
            /// The figure is closed
            ///</param>
            ///<param name="pPoints">
            /// The array of the figure's defining points
            ///</param>
            ///<param name="pointCount">
            /// The size of the points array
            ///</param>
            ///<param name="pSegTypes">
            /// The array of the figure's defining segment types
            ///</param>
            ///<param name="segmentCount">
            /// The size of the types array
            ///</param>
            internal unsafe void AddFigureToList(bool isFilled, bool isClosed, MilPoint2F* pPoints, UInt32 pointCount, byte* pSegTypes, UInt32 segmentCount)
            {
                if (pointCount >=1 && segmentCount >= 1)
                {
                    PathFigure figure = new PathFigure
                    {
                        IsFilled = isFilled,
                        StartPoint = new Point(pPoints->X, pPoints->Y)
                    };

                    int pointIndex = 1;
                    int sameSegCount = 0;

                    for (int segIndex=0; segIndex<segmentCount; segIndex += sameSegCount)
                    {
                        byte segType = (byte)(pSegTypes[segIndex] & (byte)MILCoreSegFlags.SegTypeMask);

                        sameSegCount = 1;

                        // Look for a run of same-type segments for a PolyXXXSegment.
                        while (((segIndex + sameSegCount) < segmentCount) &&
                            (pSegTypes[segIndex] == pSegTypes[segIndex+sameSegCount]))
                        {
                            sameSegCount++;
                        }

                        bool fStroked = (pSegTypes[segIndex] & (byte)MILCoreSegFlags.SegIsAGap) == (byte)0;
                        bool fSmooth = (pSegTypes[segIndex] & (byte)MILCoreSegFlags.SegSmoothJoin) != (byte)0;

                        if (segType == (byte)MILCoreSegFlags.SegTypeLine)
                        {
                            if (pointIndex+sameSegCount > pointCount)
                            {
                                throw new System.InvalidOperationException(SR.PathGeometry_InternalReadBackError);
                            }

                            if (sameSegCount>1)
                            {
                                PointCollection ptCollection = new PointCollection();
                                for (int i=0; i<sameSegCount; i++)
                                {
                                    ptCollection.Add(new Point(pPoints[pointIndex+i].X, pPoints[pointIndex+i].Y));
                                }
                                ptCollection.Freeze();

                                PolyLineSegment polySeg = new PolyLineSegment(ptCollection, fStroked, fSmooth);
                                polySeg.Freeze();

                                figure.Segments.Add(polySeg);
                            }
                            else
                            {
                                Debug.Assert(sameSegCount == 1);
                                figure.Segments.Add(new LineSegment(new Point(pPoints[pointIndex].X, pPoints[pointIndex].Y), fStroked, fSmooth));
                            }

                            pointIndex += sameSegCount;
                        }
                        else if (segType == (byte)MILCoreSegFlags.SegTypeBezier)
                        {
                            int pointBezierCount = sameSegCount*3;

                            if (pointIndex+pointBezierCount > pointCount)
                            {
                                throw new System.InvalidOperationException(SR.PathGeometry_InternalReadBackError);
                            }

                            if (sameSegCount>1)
                            {
                                PointCollection ptCollection = new PointCollection();
                                for (int i=0; i<pointBezierCount; i++)
                                {
                                    ptCollection.Add(new Point(pPoints[pointIndex+i].X, pPoints[pointIndex+i].Y));
                                }
                                ptCollection.Freeze();

                                PolyBezierSegment polySeg = new PolyBezierSegment(ptCollection, fStroked, fSmooth);
                                polySeg.Freeze();

                                figure.Segments.Add(polySeg);
                            }
                            else
                            {
                                Debug.Assert(sameSegCount == 1);

                                figure.Segments.Add(new BezierSegment(
                                    new Point(pPoints[pointIndex].X, pPoints[pointIndex].Y),
                                    new Point(pPoints[pointIndex+1].X, pPoints[pointIndex+1].Y),
                                    new Point(pPoints[pointIndex+2].X, pPoints[pointIndex+2].Y),
                                    fStroked,
                                    fSmooth));
                            }

                            pointIndex += pointBezierCount;
                        }
                        else
                        {
                            throw new System.InvalidOperationException(SR.PathGeometry_InternalReadBackError);
                        }
                    }

                    if (isClosed)
                    {
                        figure.IsClosed = true;
                    }

                    figure.Freeze();
                    Figures.Add(figure);

                    // Do not bother adding empty figures.
                }
            }

            /// <summary>
            /// The array of figures
            /// </summary>
            internal PathFigureCollection _figures;
        };

        internal unsafe delegate void AddFigureToListDelegate(bool isFilled, bool isClosed, MilPoint2F *pPoints, UInt32 pointCount, byte *pTypes, UInt32 typeCount);

        #region GetPointAtFractionLength
        /// <summary>
        /// </summary>
        public void GetPointAtFractionLength(
            double progress,
            out Point point,
            out Point tangent)
        {
            if (IsEmpty())
            {
                point = new Point();
                tangent = new Point();
                return;
            }

            // milcore's MilUtility_GetPointAtLengthFraction is a wpfgfx_cor3.dll entry point, which this
            // port ships nowhere. Walk the flattened outline instead: arc length along a polyline is a
            // running sum, so the point is a lerp within the segment the fraction lands in and the
            // tangent is that segment's direction. This is what drives PathAnimation / DoubleAnimation
            // UsingPath, so a degenerate path has to answer rather than throw.
            point = new Point();
            tangent = new Point(1, 0);

            System.Collections.Generic.List<MS.Internal.Media.PolylineFigure> figures =
                MS.Internal.Media.PathFlattener.Flatten(this, Geometry.StandardFlatteningTolerance);

            double total = 0.0;
            foreach (MS.Internal.Media.PolylineFigure f in figures)
            {
                for (int i = 1; i < f.Points.Count; i++)
                {
                    total += (f.Points[i] - f.Points[i - 1]).Length;
                }
            }

            if (total <= 0.0)
            {
                foreach (MS.Internal.Media.PolylineFigure f in figures)
                {
                    if (f.Points.Count > 0) { point = f.Points[0]; break; }
                }

                return;
            }

            double target = Math.Clamp(progress, 0.0, 1.0) * total;
            double walked = 0.0;

            foreach (MS.Internal.Media.PolylineFigure f in figures)
            {
                for (int i = 1; i < f.Points.Count; i++)
                {
                    Point a = f.Points[i - 1], b = f.Points[i];
                    Vector d = b - a;
                    double len = d.Length;
                    if (len <= 0.0) continue;

                    if (walked + len >= target)
                    {
                        double t = (target - walked) / len;
                        point = new Point(a.X + (d.X * t), a.Y + (d.Y * t));
                        tangent = new Point(d.X / len, d.Y / len);
                        return;
                    }

                    walked += len;
                    point = b;
                    tangent = new Point(d.X / len, d.Y / len);
                }
            }
        }
        #endregion

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
        internal static PathGeometry InternalCombine(
            Geometry geometry1,
            Geometry geometry2,
            GeometryCombineMode mode,
            Transform transform,
            double tolerance,
            ToleranceType type)
        {
            // milcore's MilUtility_PathGeometryCombine is a wpfgfx_cor3.dll entry point, which this port
            // ships on no platform, so the boolean is computed here. What this replaced approximated each
            // operand by the bounding rectangles of its figures -- exact for rectangle Intersect (layout
            // clips, the case it was written for) and silently wrong for every other shape.
            return MS.Internal.Media.PathBoolean.Combine(
                geometry1, geometry2, mode, transform, AbsoluteTolerance(geometry1, tolerance, type));
        }
        #endregion Combine

        /// <summary>
        /// Remove all figures
        /// </summary>
        #region Clear
        public void Clear()
        {
            PathFigureCollection figures = Figures;

            figures?.Clear();
        }
        #endregion

        #region Bounds
        /// <summary>
        /// Gets the bounds of this PathGeometry as an axis-aligned bounding box
        /// </summary>
        public override Rect Bounds
        {
            get
            {
                ReadPreamble();

                if (IsEmpty())
                {
                    return Rect.Empty;
                }
                else
                {
                    if ((_flags & PathGeometryInternalFlags.BoundsValid) == 0)
                    {
                        // Update the cached bounds
                        _bounds = GetPathBoundsAsRB(
                            GetPathGeometryData(),
                            null,   // pen
                            Matrix.Identity,
                            StandardFlatteningTolerance,
                            ToleranceType.Absolute,
                            false);  // Do not skip non-fillable figures

                        _flags |= PathGeometryInternalFlags.BoundsValid;
                    }

                    return _bounds.AsRect;
                }
            }
        }

        /// <summary>
        /// Gets the bounds of this PathGeometry as an axis-aligned bounding box with pen and/or transform
        /// </summary>
        internal static Rect GetPathBounds(
            PathGeometryData pathData,
            Pen pen, 
            Matrix worldMatrix, 
            double tolerance, 
            ToleranceType type, 
            bool skipHollows)
        {
            if (pathData.IsEmpty())
            {
                return Rect.Empty;
            }
            else
            {
                MilRectD bounds = PathGeometry.GetPathBoundsAsRB(
                    pathData,
                    pen,
                    worldMatrix, 
                    tolerance, 
                    type,
                    skipHollows);

                return bounds.AsRect;
            }
        }
        
        /// <summary>
        /// Gets the bounds of this PathGeometry as an axis-aligned bounding box with pen and/or transform
        /// 
        /// This function should not be called with a PathGeometryData that's known to be empty, since MilRectD
        /// does not offer a standard way of representing this.
        /// </summary>
        // Axis-aligned bounds of an affine-transformed rect (transform the 4 corners, re-bound).
        private static Rect TransformRectManaged(Rect r, Matrix m)
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

        internal static MilRectD GetPathBoundsAsRB(
            PathGeometryData pathData,
            Pen pen,
            Matrix worldMatrix,
            double tolerance,
            ToleranceType type,
            bool skipHollows)
        {
            // This method can't handle the empty geometry case, as it's impossible for us to
            // return Rect.Empty. Callers should do their own check.
            Debug.Assert(!pathData.IsEmpty());

            {
                // Bounds are computed managed on EVERY platform: the native helper lives in
                // wpfgfx_cor3.dll, which this port does not ship, and this is not an obscure path --
                // Shape.MeasureOverride asks for it, so anything with a Path in its template reaches
                // it. A scrollbar does, which is how a plain FlowDocumentScrollViewer used to fail
                // with DllNotFoundException on Windows.
                //
                // PathBoundsAccumulator solves each curve rather than approximating it, so the answer
                // is the same tight box the native helper produced; see the notes in that file for
                // why looseness would have shown up as layout drift around curved shapes.
                PathBoundsAccumulator acc = new PathBoundsAccumulator();
                ParsePathGeometryData(pathData, acc);
                Rect r = acc.Bounds;
                if (r.IsEmpty)
                {
                    return MilRectD.Empty;
                }

                Matrix m = new Matrix(pathData.Matrix.S_11, pathData.Matrix.S_12,
                                      pathData.Matrix.S_21, pathData.Matrix.S_22,
                                      pathData.Matrix.DX, pathData.Matrix.DY);
                m.Append(worldMatrix);
                Rect tr = TransformRectManaged(r, m);

                if (pen != null)
                {
                    double half = pen.Thickness / 2.0;
                    tr.Inflate(half, half);
                }

                return new MilRectD(tr.Left, tr.Top, tr.Right, tr.Bottom);
            }
        }

        #endregion

        
        #region HitTestWithPathGeometry
        internal static IntersectionDetail HitTestWithPathGeometry(
            Geometry geometry1,
            Geometry geometry2,
            double tolerance,
            ToleranceType type)
        {
            // milcore's MilUtility_PathGeometryHitTestPathGeometry is a wpfgfx_cor3.dll entry point,
            // which this port ships nowhere. The alpha flattener asks this whether one clip fully
            // covers another before deciding whether the clip can be dropped.
            return MS.Internal.Media.PathBoolean.Detail(
                geometry1, geometry2, Geometry.AbsoluteTolerance(geometry1, tolerance, type));
        }
        #endregion

        #region IsEmpty

        /// <summary>
        /// Returns true if this geometry is empty
        /// </summary>
        public override bool IsEmpty()
        {
            PathFigureCollection figures = Figures;
            return (figures == null) || (figures.Count <= 0);
        }

        #endregion

        /// <summary>
        /// Returns true if this geometry may have curved segments
        /// </summary>
        public override bool MayHaveCurves()
        {
            PathFigureCollection figures = Figures;

            int count = (figures != null) ? figures.Count : 0;

            for (int i=0; i<count; i++)
            {
                if (figures.Internal_GetItem(i).MayHaveCurves())
                {
                    return true;
                }
            }

            return false;
        }

        #region Internal
                
        /// <summary>
        /// GetAsPathGeometry - return a PathGeometry version of this Geometry
        /// </summary>
        internal override PathGeometry GetAsPathGeometry()
        {
            return CloneCurrentValue();
        }

        /// <summary>
        /// Creates a string representation of this object based on the format string 
        /// and IFormatProvider passed in.  
        /// If the provider is null, the CurrentCulture is used.
        /// See the documentation for IFormattable for more information.
        /// </summary>
        /// <returns>
        /// A string representation of this object.
        /// </returns>
        internal override string ConvertToString(string format, IFormatProvider provider)
        {
            PathFigureCollection figures = Figures;
            FillRule fillRule = FillRule;

            string figuresString = String.Empty;
            
            if (figures != null)
            {
                figuresString = figures.ConvertToString(format, provider);
            }

            if (fillRule != FillRule.EvenOdd)
            {
                return "F1" + figuresString;
            }
            else
            {
                return figuresString;
            }
        }

        internal void SetDirty()
        {
            _flags = PathGeometryInternalFlags.Dirty;
        }

        /// <summary>
        /// GetPathGeometryData - returns a struct which contains this Geometry represented
        /// as a path geometry's serialized format.
        /// </summary>
        internal override PathGeometryData GetPathGeometryData()
        {
            PathGeometryData data = new PathGeometryData
            {
                FillRule = FillRule,
                Matrix = CompositionResourceManager.TransformToMilMatrix3x2D(Transform)
            };

            if (IsObviouslyEmpty())
            {
                return Geometry.GetEmptyPathGeometryData();                
            }

            ByteStreamGeometryContext ctx = new ByteStreamGeometryContext();

            PathFigureCollection figures = Figures;

            int figureCount = figures == null ? 0 : figures.Count;

            for (int i = 0; i < figureCount; i++)
            {
                figures.Internal_GetItem(i).SerializeData(ctx);
            }

            ctx.Close();
            data.SerializedData = ctx.GetData();

            return data;
        }

        private void ManualUpdateResource(DUCE.Channel channel, bool skipOnChannelCheck)
        {
            // If we're told we can skip the channel check, then we must be on channel
            Debug.Assert(!skipOnChannelCheck || _duceResource.IsOnChannel(channel));

            if (skipOnChannelCheck || _duceResource.IsOnChannel(channel))
            {
                checked
                {
                    Transform vTransform = Transform;

                    // Obtain handles for properties that implement DUCE.IResource
                    DUCE.ResourceHandle hTransform;
                    if (vTransform == null ||
                        Object.ReferenceEquals(vTransform, Transform.Identity)
                       )
                    {
                        hTransform = DUCE.ResourceHandle.Null;
                    }
                    else
                    {
                        hTransform = ((DUCE.IResource)vTransform).GetHandle(channel);
                    }

                    DUCE.MILCMD_PATHGEOMETRY data;
                    data.Type = MILCMD.MilCmdPathGeometry;
                    data.Handle = _duceResource.GetHandle(channel);
                    data.hTransform = hTransform;
                    data.FillRule = FillRule;

                    PathGeometryData pathData = GetPathGeometryData();

                    data.FiguresSize = pathData.Size;

                    unsafe
                    {
                        channel.BeginCommand(
                            (byte*)&data,
                            sizeof(DUCE.MILCMD_PATHGEOMETRY),
                            (int)data.FiguresSize
                            ); 

                        fixed (byte *pPathData = pathData.SerializedData)
                        {
                            channel.AppendCommandData(pPathData, (int)data.FiguresSize);
                        }
                    }

                    channel.EndCommand();
                }
            }
        }

        internal override void TransformPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            // PathGeometry caches the transformed bounds.  We hook the changed event
            // on the Transformed bounds so we can clear the cache.
            if ((_flags & PathGeometryInternalFlags.BoundsValid) != 0)
            {
                SetDirty();

                // The UCE slave already has a notifier registered on its transform to
                // invalidate its cache.  No need to call InvalidateResource() here to
                // marshal the MIL_PATHGEOMETRY.Flags.
            }
        }
        
        internal void FiguresPropertyChangedHook(DependencyPropertyChangedEventArgs e)
        {
            // This is necessary to invalidate the cached bounds.
            SetDirty();
        }

        #endregion

        #region Data

        internal PathGeometryInternalFlags _flags = PathGeometryInternalFlags.None;
        internal MilRectD _bounds;                  // Cached Bounds

        #endregion
    }
    #endregion
}

