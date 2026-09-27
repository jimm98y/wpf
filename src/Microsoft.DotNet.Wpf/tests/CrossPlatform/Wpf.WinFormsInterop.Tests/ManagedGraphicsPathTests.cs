// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GraphicsPath is managed on every platform (System.Drawing.Drawing2D.ManagedPath): GDI+ never holds
// a path, so the browser -- which has no GDI+ -- builds and flattens paths exactly as the desktop
// does. These pin the path model to what GDI+ produces, because callers read PathPoints/PathTypes
// and the recorder flattens them into what reaches the screen: a rectangle is four points closing on
// the last, an ellipse is four Beziers starting at angle 0, an open segment continues the figure.
//

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ManagedGraphicsPathTests
    {
        private const byte Start = 0, Line = 1, Bezier = 3, Close = 0x80;

        private static void Near(PointF expected, PointF actual, float tol = 1e-3f)
            => Assert.True(Math.Abs(expected.X - actual.X) <= tol && Math.Abs(expected.Y - actual.Y) <= tol,
                           $"expected {expected}, got {actual}");

        [Fact]
        public void ARectangleIsFourPointsClosingOnTheLast()
        {
            using var p = new GraphicsPath();
            p.AddRectangle(new RectangleF(10, 20, 30, 40));
            Assert.Equal(new[] { new PointF(10, 20), new PointF(40, 20), new PointF(40, 60), new PointF(10, 60) }, p.PathPoints);
            Assert.Equal(new byte[] { Start, Line, Line, Line | Close }, p.PathTypes);
        }

        [Fact]
        public void AnEllipseIsFourBeziersFromAngleZero()
        {
            using var p = new GraphicsPath();
            p.AddEllipse(0, 0, 100, 50);
            Assert.Equal(13, p.PointCount);
            byte[] t = p.PathTypes;
            Assert.Equal(Start, t[0]);
            for (int i = 1; i < 12; i++) Assert.Equal(Bezier, t[i]);
            Assert.Equal(Bezier | Close, t[12]);
            Near(new PointF(100, 25), p.PathPoints[0]);
            Near(new PointF(50, 50), p.PathPoints[3]);    // a quarter turn: the bottom
            Near(new PointF(100, 25), p.PathPoints[12]);
            RectangleF b = p.GetBounds();
            Near(new PointF(0, 0), b.Location); Near(new PointF(100, 50), new PointF(b.Right, b.Bottom));
        }

        [Fact]
        public void ArcAnglesAreRayAnglesOnTheEllipse()
        {
            // 45 degrees on a 200x100 ellipse is where the 45-degree RAY meets it, not the parametric
            // point at 45 degrees: x = y there.
            using var p = new GraphicsPath();
            p.AddArc(0, 0, 200, 100, 0, 45);
            PointF end = p.GetLastPoint();
            Assert.True(Math.Abs((end.X - 100) - (end.Y - 50)) < 0.01f, $"{end} is not on the 45-degree ray");
            float nx = (end.X - 100) / 100, ny = (end.Y - 50) / 50;
            Assert.True(Math.Abs(nx * nx + ny * ny - 1) < 1e-3f, $"{end} is not on the ellipse");
        }

        [Fact]
        public void OpenSegmentsContinueTheFigure_UntilItIsStartedOrClosed()
        {
            using var p = new GraphicsPath();
            p.AddLine(0, 0, 10, 0);
            p.AddLine(20, 0, 30, 0);          // joined by a line from (10,0)
            p.AddLine(30, 0, 30, 10);         // shares its first point: nothing to join
            p.StartFigure();
            p.AddLine(0, 50, 10, 50);
            p.CloseFigure();
            Assert.Equal(new byte[] { Start, Line, Line, Line, Line, Start, Line | Close }, p.PathTypes);
            Assert.Equal(7, p.PointCount);
        }

        [Fact]
        public void ACurvePassesThroughItsPoints()
        {
            var pts = new[] { new PointF(0, 0), new PointF(50, 40), new PointF(100, 0) };
            using var p = new GraphicsPath();
            p.AddCurve(pts);
            Assert.Equal(7, p.PointCount);
            Near(pts[1], p.PathPoints[3]);
            Near(pts[2], p.PathPoints[6]);
        }

        [Fact]
        public void FlatteningLeavesOnlyLinesOnTheCurve()
        {
            using var p = new GraphicsPath();
            p.AddEllipse(0, 0, 100, 100);
            p.Flatten();
            foreach (byte t in p.PathTypes) Assert.NotEqual(Bezier, t & 7);
            Assert.True(p.PointCount > 13);
            foreach (PointF q in p.PathPoints)
            {
                double r = Math.Sqrt((q.X - 50) * (q.X - 50) + (q.Y - 50) * (q.Y - 50));
                Assert.True(Math.Abs(r - 50) < 0.3, $"{q} is {r} from the centre");
            }
            Assert.Equal(Line | Close, p.PathTypes[p.PointCount - 1]);
        }

        [Fact]
        public void HitTestingFollowsTheFillMode()
        {
            using var p = new GraphicsPath(FillMode.Alternate);
            p.AddRectangle(new RectangleF(0, 0, 100, 100));
            p.AddRectangle(new RectangleF(25, 25, 50, 50));
            Assert.True(p.IsVisible(10, 10));
            Assert.False(p.IsVisible(50, 50));    // inside both: even-odd leaves a hole
            p.FillMode = FillMode.Winding;
            Assert.True(p.IsVisible(50, 50));     // same direction twice: winding fills it
            Assert.False(p.IsVisible(150, 50));
        }

        [Fact]
        public void ARoundedRectangleOfArcs_AsTheWin11ThemeBuildsIt()
        {
            using var p = new GraphicsPath();
            float x = 2, y = 2, w = 16, h = 16, d = 8;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            Assert.True(p.IsVisible(10, 10));
            Assert.False(p.IsVisible(2.3f, 2.3f));            // cut away by the corner
            RectangleF b = p.GetBounds();
            Assert.True(Math.Abs(b.Left - 2) < 1e-3 && Math.Abs(b.Right - 18) < 1e-3);
            using var clone = (GraphicsPath)p.Clone();
            Assert.Equal(p.PathPoints, clone.PathPoints);
        }

        [Fact]
        public void ReversingKeepsEachSegmentsKind()
        {
            using var p = new GraphicsPath();
            p.AddLine(0, 0, 10, 0);
            p.AddBezier(10, 0, 15, 5, 20, 5, 25, 0);
            p.Reverse();
            Assert.Equal(new PointF(25, 0), p.PathPoints[0]);
            Assert.Equal(new byte[] { Start, Bezier, Bezier, Bezier, Line }, p.PathTypes);
            Assert.Equal(new PointF(0, 0), p.PathPoints[4]);
        }
    }
}
