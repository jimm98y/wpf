// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Glyph outlines, read from the font file.
//
// This closes the last hole in the managed font stack. GlyphTypeface.ComputeGlyphOutline was a
// wpfgfx_cor3 entry point over a DirectWrite font face -- MilGlyphRun_GetGlyphOutline -- and this
// port ships wpfgfx on no platform, so every caller got DllNotFoundException. The callers are not
// obscure: GlyphRun.BuildGeometry, FormattedText.BuildGeometry and GlyphTypeface.GetGlyphOutline
// are all public API, and printing, text-as-path effects and geometry hit-testing all reach them.
//
// Two formats, because OpenType has two. 'glyf' holds quadratic contours and is what almost every
// system font uses; 'CFF ' holds Type 2 charstrings and is what OTF and most CJK fonts use. A font
// with neither -- a colour bitmap face, CBDT or sbix -- has no outlines to give, and says so.
//
// Coordinates come out in FONT UNITS with y up, exactly as the tables store them. Scaling to an em
// size and flipping into WPF's y-down space is the caller's job, done once in the sink, so nothing
// here has to know what a rendering em size is.
//
// The WebGPU engine next door parses the same two formats, and this does not call it, for the same
// reason OpenTypeFontData does not: that reader is the RENDERER's, and it works in pixels. It
// scales every point by BaseEmPixels/unitsPerEm, flips to y-down and bakes in synthetic bold and
// oblique on the way out -- all correct for filling a glyph atlas, all wrong for an outline that
// has to be exact at an arbitrary em size. A page printed at 600 dpi asks for glyphs forty times
// the size the atlas rasterizes them at, and would get the quantisation of the smaller one.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MS.Internal.Text.TextInterface.Managed
{
    /// <summary>
    /// Receives a glyph's contours.
    ///
    /// A sink rather than a returned data structure: the shapes go straight into whatever the
    /// caller is building -- a WPF PathGeometry, a bounds accumulator -- without an intermediate
    /// list of points per glyph, which for a page of text is a great deal of garbage.
    /// </summary>
    /// <remarks>
    /// Public because FontFace is: the stub mirrors the real DirectWriteForwarder's surface, which
    /// PresentationCore consumes as public types rather than through friend access.
    /// </remarks>
    public interface IGlyphOutlineSink
    {
        void BeginFigure(double x, double y);

        void LineTo(double x, double y);

        /// <summary>A quadratic, which is what a TrueType contour is made of.</summary>
        void QuadraticTo(double cx, double cy, double x, double y);

        /// <summary>A cubic, which is what a Type 2 charstring is made of.</summary>
        void CubicTo(double c1x, double c1y, double c2x, double c2y, double x, double y);

        void EndFigure();
    }

    internal static class GlyphOutlines
    {
        /// <summary>
        /// Writes one glyph's contours to the sink, in font units with y up.
        ///
        /// False means the glyph has no outline. That is a normal answer and not an error: a space
        /// has no contours, and neither does any glyph of a bitmap-only colour font.
        /// </summary>
        internal static bool TryGetOutline(OpenTypeFontData font, int glyphIndex, IGlyphOutlineSink sink)
        {
            if (font == null || sink == null) return false;
            if (glyphIndex < 0 || glyphIndex >= font.NumGlyphs) return false;

            try
            {
                return font.IsCff || !font.HasGlyfOutlines
                    ? CffOutlines.TryGetOutline(font, glyphIndex, sink)
                    : TrueTypeOutlines.TryGetOutline(font, glyphIndex, sink);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A malformed or truncated table. A font that lies about its own offsets should
                // cost the application one blank glyph, not an exception out of a layout pass.
                return false;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }
    }

    /// <summary>Contours from the 'glyf' table: quadratic B-splines, and composites of them.</summary>
    internal static class TrueTypeOutlines
    {
        /// <summary>
        /// How deep a composite glyph may nest.
        ///
        /// Real fonts go two deep at most -- an accented letter whose accent is itself composite.
        /// The limit is here because the format allows a glyph to reference itself, and a font that
        /// does would otherwise recurse until the stack ran out.
        /// </summary>
        private const int MaxCompositeDepth = 5;

        internal static bool TryGetOutline(OpenTypeFontData font, int glyphIndex, IGlyphOutlineSink sink)
        {
            var contours = new List<Contour>();
            Read(font, glyphIndex, Transform.Identity, 0, contours);

            if (contours.Count == 0) return false;

            foreach (Contour contour in contours) Emit(contour, sink);

            return true;
        }

        /// <summary>A 2x2 with an offset, which is all a composite component can apply.</summary>
        private readonly struct Transform
        {
            internal readonly double A, B, C, D, X, Y;

            internal Transform(double a, double b, double c, double d, double x, double y)
            {
                A = a; B = b; C = c; D = d; X = x; Y = y;
            }

            internal static Transform Identity => new Transform(1, 0, 0, 1, 0, 0);

            internal (double X, double Y) Apply(double x, double y)
                => (A * x + C * y + X, B * x + D * y + Y);

            /// <summary>This transform followed by <paramref name="outer"/>.</summary>
            internal Transform Then(Transform outer)
                => new Transform(A * outer.A + B * outer.C,
                                 A * outer.B + B * outer.D,
                                 C * outer.A + D * outer.C,
                                 C * outer.B + D * outer.D,
                                 X * outer.A + Y * outer.C + outer.X,
                                 X * outer.B + Y * outer.D + outer.Y);
        }

        private sealed class Contour
        {
            internal readonly List<(double X, double Y, bool OnCurve)> Points =
                new List<(double, double, bool)>();
        }

        private static void Read(OpenTypeFontData font, int glyphIndex, Transform transform, int depth,
                                 List<Contour> contours)
        {
            if (depth > MaxCompositeDepth) return;
            if (glyphIndex < 0 || glyphIndex >= font.NumGlyphs) return;

            if (!font.TryGetGlyfRange(glyphIndex, out int start, out int end)) return;
            if (end <= start) return;                       // no contours: a space, most often

            byte[] data = font.Raw;
            int p = start;

            int numContours = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p));
            p += 10;                                        // past numberOfContours and the bbox

            if (numContours >= 0) ReadSimple(data, p, numContours, transform, contours);
            else ReadComposite(font, data, p, transform, depth, contours);
        }

        private static void ReadSimple(byte[] data, int p, int numContours, Transform transform,
                                       List<Contour> contours)
        {
            if (numContours == 0) return;

            var endPoints = new int[numContours];
            for (int i = 0; i < numContours; i++)
            {
                endPoints[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                p += 2;
            }

            int pointCount = endPoints[numContours - 1] + 1;
            if (pointCount <= 0) return;

            // Hinting instructions, which this stack does not run: outlines are used at print and
            // geometry resolution, where grid fitting has nothing to fit to.
            int instructions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
            p += 2 + instructions;

            var flags = new byte[pointCount];
            for (int i = 0; i < pointCount;)
            {
                byte flag = data[p++];
                flags[i++] = flag;

                if ((flag & 0x08) != 0)                     // repeat
                {
                    int repeat = data[p++];
                    while (repeat-- > 0 && i < pointCount) flags[i++] = flag;
                }
            }

            // Coordinates are stored as deltas, x for every point and then y for every point, each
            // one, two or zero bytes depending on its flags.
            var xs = new int[pointCount];
            int x = 0;

            for (int i = 0; i < pointCount; i++)
            {
                byte flag = flags[i];

                if ((flag & 0x02) != 0)
                {
                    int dx = data[p++];
                    x += (flag & 0x10) != 0 ? dx : -dx;
                }
                else if ((flag & 0x10) == 0)
                {
                    x += BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p));
                    p += 2;
                }

                xs[i] = x;
            }

            var ys = new int[pointCount];
            int y = 0;

            for (int i = 0; i < pointCount; i++)
            {
                byte flag = flags[i];

                if ((flag & 0x04) != 0)
                {
                    int dy = data[p++];
                    y += (flag & 0x20) != 0 ? dy : -dy;
                }
                else if ((flag & 0x20) == 0)
                {
                    y += BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p));
                    p += 2;
                }

                ys[i] = y;
            }

            int first = 0;

            for (int c = 0; c < numContours; c++)
            {
                int last = endPoints[c];
                if (last - first + 1 >= 2)
                {
                    var contour = new Contour();

                    for (int i = first; i <= last; i++)
                    {
                        (double tx, double ty) = transform.Apply(xs[i], ys[i]);
                        contour.Points.Add((tx, ty, (flags[i] & 0x01) != 0));
                    }

                    contours.Add(contour);
                }

                first = last + 1;
            }
        }

        private static void ReadComposite(OpenTypeFontData font, byte[] data, int p, Transform transform,
                                          int depth, List<Contour> contours)
        {
            const int ArgsAreWords = 0x0001;
            const int ArgsAreXY = 0x0002;
            const int HaveScale = 0x0008;
            const int MoreComponents = 0x0020;
            const int HaveXAndYScale = 0x0040;
            const int HaveTwoByTwo = 0x0080;

            bool more = true;

            while (more)
            {
                int flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                p += 2;

                int component = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                p += 2;

                int arg1, arg2;

                if ((flags & ArgsAreWords) != 0)
                {
                    arg1 = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p)); p += 2;
                    arg2 = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p)); p += 2;
                }
                else
                {
                    arg1 = (sbyte)data[p++];
                    arg2 = (sbyte)data[p++];
                }

                double a = 1, b = 0, c = 0, d = 1;

                if ((flags & HaveScale) != 0)
                {
                    a = d = F2Dot14(data, p); p += 2;
                }
                else if ((flags & HaveXAndYScale) != 0)
                {
                    a = F2Dot14(data, p); p += 2;
                    d = F2Dot14(data, p); p += 2;
                }
                else if ((flags & HaveTwoByTwo) != 0)
                {
                    a = F2Dot14(data, p); p += 2;
                    b = F2Dot14(data, p); p += 2;
                    c = F2Dot14(data, p); p += 2;
                    d = F2Dot14(data, p); p += 2;
                }

                // Point matching -- align component point i to base point j -- instead of an offset.
                // Vanishingly rare, and getting it wrong misplaces an accent rather than losing it,
                // so the component is placed unshifted.
                double dx = (flags & ArgsAreXY) != 0 ? arg1 : 0;
                double dy = (flags & ArgsAreXY) != 0 ? arg2 : 0;

                Read(font, component, new Transform(a, b, c, d, dx, dy).Then(transform), depth + 1, contours);

                more = (flags & MoreComponents) != 0;
            }
        }

        private static double F2Dot14(byte[] data, int offset)
            => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset)) / 16384.0;

        /// <summary>
        /// One quadratic contour, as figure and segments.
        ///
        /// TrueType stores a contour as a cycle of points that are alternately on and off the
        /// curve, and it compresses the common case: two consecutive off-curve points imply an
        /// on-curve point exactly between them. Reconstructing those implied points is the whole
        /// job, and getting it wrong shows up as letters with corners where they should be round.
        /// </summary>
        private static void Emit(Contour contour, IGlyphOutlineSink sink)
        {
            List<(double X, double Y, bool OnCurve)> points = contour.Points;
            int count = points.Count;
            if (count < 2) return;

            // A contour may begin off the curve, in which case the start point has to be
            // manufactured: the following on-curve point if there is one, otherwise the midpoint
            // between the first and last off-curve points.
            int start = -1;

            for (int i = 0; i < count; i++)
            {
                if (points[i].OnCurve) { start = i; break; }
            }

            double startX, startY;

            if (start < 0)
            {
                startX = (points[0].X + points[count - 1].X) / 2;
                startY = (points[0].Y + points[count - 1].Y) / 2;
                start = 0;
            }
            else
            {
                startX = points[start].X;
                startY = points[start].Y;
                start++;
            }

            sink.BeginFigure(startX, startY);

            double controlX = 0, controlY = 0;
            bool pending = false;

            for (int i = 0; i < count; i++)
            {
                (double X, double Y, bool OnCurve) point = points[(start + i) % count];

                if (point.OnCurve)
                {
                    if (pending)
                    {
                        sink.QuadraticTo(controlX, controlY, point.X, point.Y);
                        pending = false;
                    }
                    else
                    {
                        sink.LineTo(point.X, point.Y);
                    }
                }
                else if (pending)
                {
                    // Two controls in a row: the on-curve point between them is implied.
                    double midX = (controlX + point.X) / 2;
                    double midY = (controlY + point.Y) / 2;

                    sink.QuadraticTo(controlX, controlY, midX, midY);

                    controlX = point.X;
                    controlY = point.Y;
                }
                else
                {
                    controlX = point.X;
                    controlY = point.Y;
                    pending = true;
                }
            }

            // Close back onto the start, through a control still in hand if there is one.
            if (pending) sink.QuadraticTo(controlX, controlY, startX, startY);

            sink.EndFigure();
        }
    }
}
