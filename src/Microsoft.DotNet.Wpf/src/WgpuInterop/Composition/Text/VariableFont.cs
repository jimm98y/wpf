// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OpenType Font Variations: 'fvar', 'avar' and 'gvar'.
//
// A variable font ships ONE outline per glyph plus a set of deltas, and every weight, width and
// optical size the family offers is a point in that design space. Nearly every UI family now
// published is packaged this way -- Inter, Roboto Flex, Source Sans, the system faces -- and a
// reader that ignores the variation tables draws every one of them at the DEFAULT instance. The
// visible result is not an error but something worse: Bold silently renders as Regular, and the
// synthetic emboldening this stack falls back to dilates the Regular outline instead of using the
// weight axis the font actually has.
//
// Three tables, three jobs:
//
//   fvar  the design space -- which axes exist ('wght', 'wdth', 'ital', 'slnt', ...) and each one's
//         minimum, default and maximum in USER units (weight 100..900, and so on).
//   avar  an optional per-axis warp of that space. A font uses it when the designer's masters are
//         not evenly spaced: without it, asking for weight 600 on a font whose masters are at 400
//         and 700 lands somewhere the designer never looked at.
//   gvar  the deltas themselves, per glyph: a set of TUPLES, each a region of the design space with
//         a per-point offset, scaled by how far into that region the instance sits and summed.
//
// The two parts that are easy to get subtly wrong, and are therefore commented where they happen,
// are the scalar computation (a tuple contributes on a triangular ramp, and an axis a tuple does not
// mention contributes nothing rather than zero) and INFERRED DELTAS: a tuple may list only the
// points it moves, and every other point in the contour has to be interpolated from its neighbours.
// Skipping that does not lose a little precision, it tears the outline apart.
//
// Scope: TrueType outlines ('glyf' + 'gvar'). CFF2 is a different delta encoding inside the
// charstrings and is not handled here, matching CffFont's own note.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>One axis of a variable font's design space, in user units.</summary>
    internal readonly struct VariationAxis
    {
        public readonly uint Tag;
        public readonly float Min, Default, Max;

        public VariationAxis(uint tag, float min, float def, float max)
        {
            Tag = tag; Min = min; Default = def; Max = max;
        }

        /// <summary>
        ///  Maps a user value onto the [-1, 1] normalized coordinate the delta tables are indexed by.
        /// </summary>
        public float Normalize(float user)
        {
            float v = Math.Clamp(user, Min, Max);
            if (v == Default) return 0f;
            return v < Default
                ? (Default - Min) > 0f ? -(Default - v) / (Default - Min) : 0f
                : (Max - Default) > 0f ? (v - Default) / (Max - Default) : 0f;
        }
    }

    /// <summary>
    /// The variation tables of one face, and the instance currently selected from them.
    /// </summary>
    internal sealed class VariableFont
    {
        /// <summary>OpenType axis tags, big-endian four-character codes.</summary>
        public const uint AxisWeight = 0x77676874;   // 'wght'
        public const uint AxisWidth = 0x77647468;    // 'wdth'
        public const uint AxisItalic = 0x6974616C;   // 'ital'
        public const uint AxisSlant = 0x736C6E74;    // 'slnt'

        private readonly byte[] _data;
        private readonly VariationAxis[] _axes;

        // avar: per axis, the (from, to) pairs of its piecewise-linear warp. Null when absent.
        private readonly (float From, float To)[][]? _segments;

        // gvar.
        private readonly int _gvarOffset;
        private readonly int _sharedTuplesOffset;
        private readonly int _sharedTupleCount;
        private readonly uint[] _glyphDataOffsets;   // glyphCount + 1, absolute
        private readonly int _gvarAxisCount;

        /// <summary>The instance in normalized coordinates, one per axis. All zero = default.</summary>
        private float[] _coords;

        public IReadOnlyList<VariationAxis> Axes => _axes;

        /// <summary>True when this face carries per-glyph outline deltas we can apply.</summary>
        public bool HasOutlineDeltas => _glyphDataOffsets.Length > 0;

        /// <summary>True when the selected instance is not simply the default one.</summary>
        public bool IsVaried
        {
            get
            {
                foreach (float c in _coords) if (c != 0f) return true;
                return false;
            }
        }

        private VariableFont(byte[] data, VariationAxis[] axes, (float, float)[][]? segments,
                             int gvarOffset, int sharedTuplesOffset, int sharedTupleCount,
                             uint[] glyphDataOffsets, int gvarAxisCount)
        {
            _data = data;
            _axes = axes;
            _segments = segments;
            _gvarOffset = gvarOffset;
            _sharedTuplesOffset = sharedTuplesOffset;
            _sharedTupleCount = sharedTupleCount;
            _glyphDataOffsets = glyphDataOffsets;
            _gvarAxisCount = gvarAxisCount;
            _coords = new float[axes.Length];
        }

        /// <summary>
        ///  Reads the variation tables of a face, or returns null when it is not a variable font.
        /// </summary>
        public static VariableFont? TryRead(byte[] data, Dictionary<string, int> tables)
        {
            if (!tables.TryGetValue("fvar", out int fvar)) return null;

            VariationAxis[]? axes = ReadFvar(data, fvar);
            if (axes is null || axes.Length == 0) return null;

            (float, float)[][]? segments = tables.TryGetValue("avar", out int avar)
                ? ReadAvar(data, avar, axes.Length)
                : null;

            int gvarOffset = 0, sharedTuplesOffset = 0, sharedTupleCount = 0, gvarAxisCount = 0;
            uint[] glyphOffsets = Array.Empty<uint>();
            if (tables.TryGetValue("gvar", out int gvar) && gvar + 20 <= data.Length)
            {
                gvarAxisCount = U16(data, gvar + 4);
                sharedTupleCount = U16(data, gvar + 6);
                sharedTuplesOffset = gvar + (int)U32(data, gvar + 8);
                int glyphCount = U16(data, gvar + 12);
                bool longOffsets = (U16(data, gvar + 14) & 1) != 0;
                int dataArray = gvar + (int)U32(data, gvar + 16);

                // One more offset than glyphs: each glyph's data runs to the next offset, and an
                // entry equal to its successor means that glyph simply has no deltas.
                glyphOffsets = new uint[glyphCount + 1];
                int p = gvar + 20;
                for (int i = 0; i <= glyphCount; i++)
                {
                    if (longOffsets)
                    {
                        if (p + 4 > data.Length) return null;
                        glyphOffsets[i] = (uint)(dataArray + U32(data, p)); p += 4;
                    }
                    else
                    {
                        if (p + 2 > data.Length) return null;
                        glyphOffsets[i] = (uint)(dataArray + U16(data, p) * 2); p += 2;
                    }
                }
                gvarOffset = gvar;
            }

            return new VariableFont(data, axes, segments, gvarOffset, sharedTuplesOffset,
                                    sharedTupleCount, glyphOffsets, gvarAxisCount);
        }

        // ---- the design space ----

        private static VariationAxis[]? ReadFvar(byte[] data, int fvar)
        {
            if (fvar + 16 > data.Length) return null;

            int axesOffset = fvar + U16(data, fvar + 4);
            int axisCount = U16(data, fvar + 8);
            int axisSize = U16(data, fvar + 10);
            if (axisCount == 0 || axisSize < 20) return null;
            if (axesOffset + axisCount * axisSize > data.Length) return null;

            var axes = new VariationAxis[axisCount];
            for (int i = 0; i < axisCount; i++)
            {
                int p = axesOffset + i * axisSize;
                axes[i] = new VariationAxis(U32(data, p),
                                            Fixed(data, p + 4), Fixed(data, p + 8), Fixed(data, p + 12));
            }
            return axes;
        }

        private static (float, float)[][]? ReadAvar(byte[] data, int avar, int axisCount)
        {
            if (avar + 8 > data.Length || U16(data, avar + 6) != axisCount) return null;

            var maps = new (float, float)[axisCount][];
            int p = avar + 8;
            for (int a = 0; a < axisCount; a++)
            {
                if (p + 2 > data.Length) return null;
                int count = U16(data, p); p += 2;
                if (p + count * 4 > data.Length) return null;

                var pairs = new (float, float)[count];
                for (int i = 0; i < count; i++)
                {
                    pairs[i] = (F2Dot14(data, p), F2Dot14(data, p + 2));
                    p += 4;
                }
                maps[a] = pairs;
            }
            return maps;
        }

        /// <summary>
        ///  Selects the instance nearest the requested user values. Axes not named keep their default.
        /// </summary>
        public void SetInstance(IReadOnlyDictionary<uint, float> userValues)
        {
            var coords = new float[_axes.Length];
            for (int i = 0; i < _axes.Length; i++)
            {
                if (!userValues.TryGetValue(_axes[i].Tag, out float user)) continue;
                coords[i] = ApplyAvar(i, _axes[i].Normalize(user));
            }
            _coords = coords;
        }

        /// <summary>The user value an axis currently sits at, or its default when it has none.</summary>
        public bool TryGetAxis(uint tag, out VariationAxis axis)
        {
            foreach (VariationAxis a in _axes)
            {
                if (a.Tag == tag) { axis = a; return true; }
            }
            axis = default;
            return false;
        }

        /// <summary>
        ///  Warps a normalized coordinate through the axis's segment map. Piecewise linear between
        ///  the listed points; the identity when the font supplies no map for this axis.
        /// </summary>
        private float ApplyAvar(int axisIndex, float coord)
        {
            if (_segments is null) return coord;
            (float From, float To)[] pairs = _segments[axisIndex];
            if (pairs is null || pairs.Length < 2) return coord;

            for (int i = 1; i < pairs.Length; i++)
            {
                (float from0, float to0) = pairs[i - 1];
                (float from1, float to1) = pairs[i];
                if (coord > from1) continue;
                if (coord == from1) return to1;
                if (coord < from0) return to0;

                float span = from1 - from0;
                return span <= 0f ? to0 : to0 + (to1 - to0) * (coord - from0) / span;
            }
            return pairs[pairs.Length - 1].To;
        }

        // ---- gvar ----

        /// <summary>
        ///  The deltas for one glyph at the current instance, or null when it has none.
        /// </summary>
        /// <param name="pointCount">
        ///  Points in the glyph INCLUDING the four phantom points. gvar addresses those as ordinary
        ///  points, which is how an instance changes a glyph's advance width without an HVAR table.
        /// </param>
        /// <param name="contourEnds">
        ///  The last point index of each contour, needed to infer the deltas of points a tuple does
        ///  not list. Phantom points are not part of any contour and are excluded from that.
        /// </param>
        /// <param name="points">The glyph's points, which inferred deltas interpolate against.</param>
        public Vector2[]? GetGlyphDeltas(int gid, int pointCount, int[] contourEnds, Vector2[] points)
        {
            if (!IsVaried || gid < 0 || gid + 1 >= _glyphDataOffsets.Length || pointCount <= 0) return null;

            uint start = _glyphDataOffsets[gid], end = _glyphDataOffsets[gid + 1];
            if (end <= start || end > (uint)_data.Length || start + 4 > end) return null;

            int p = (int)start;
            int tupleCount = U16(_data, p);
            bool sharedPoints = (tupleCount & 0x8000) != 0;
            tupleCount &= 0x0FFF;
            int serialized = (int)start + U16(_data, p + 2);
            p += 4;

            if (tupleCount == 0 || serialized >= end) return null;

            int[]? shared = null;
            if (sharedPoints) shared = ReadPointNumbers(ref serialized, (int)end, pointCount);

            var total = new Vector2[pointCount];
            bool any = false;

            for (int t = 0; t < tupleCount && p + 4 <= end; t++)
            {
                int variationDataSize = U16(_data, p);
                int tupleIndex = U16(_data, p + 2);
                p += 4;

                float[] peak = new float[_gvarAxisCount];
                if ((tupleIndex & 0x8000) != 0)   // EMBEDDED_PEAK_TUPLE
                {
                    for (int a = 0; a < _gvarAxisCount; a++, p += 2) peak[a] = F2Dot14(_data, p);
                }
                else
                {
                    int index = tupleIndex & 0x0FFF;
                    if (index >= _sharedTupleCount) { p += 0; continue; }
                    int sp = _sharedTuplesOffset + index * _gvarAxisCount * 2;
                    for (int a = 0; a < _gvarAxisCount; a++) peak[a] = F2Dot14(_data, sp + a * 2);
                }

                float[]? interStart = null, interEnd = null;
                if ((tupleIndex & 0x4000) != 0)   // INTERMEDIATE_REGION
                {
                    interStart = new float[_gvarAxisCount];
                    interEnd = new float[_gvarAxisCount];
                    for (int a = 0; a < _gvarAxisCount; a++, p += 2) interStart[a] = F2Dot14(_data, p);
                    for (int a = 0; a < _gvarAxisCount; a++, p += 2) interEnd[a] = F2Dot14(_data, p);
                }

                int tupleData = serialized;
                serialized += variationDataSize;

                float scalar = Scalar(peak, interStart, interEnd);
                if (scalar == 0f) continue;   // this region does not contain the instance

                int q = tupleData;
                int[]? pointNumbers = (tupleIndex & 0x2000) != 0    // PRIVATE_POINT_NUMBERS
                    ? ReadPointNumbers(ref q, (int)end, pointCount)
                    : shared;

                // A null list here means "all points", which is the commonest case by far: a tuple
                // that moves the whole glyph writes no point numbers at all.
                int deltaCount = pointNumbers?.Length ?? pointCount;
                int[] dx = ReadPackedDeltas(ref q, (int)end, deltaCount);
                int[] dy = ReadPackedDeltas(ref q, (int)end, deltaCount);

                var tupleDeltas = new Vector2[pointCount];
                if (pointNumbers is null)
                {
                    for (int i = 0; i < pointCount && i < dx.Length && i < dy.Length; i++)
                        tupleDeltas[i] = new Vector2(dx[i], dy[i]);
                }
                else
                {
                    var touched = new bool[pointCount];
                    for (int i = 0; i < pointNumbers.Length && i < dx.Length && i < dy.Length; i++)
                    {
                        int idx = pointNumbers[i];
                        if ((uint)idx >= (uint)pointCount) continue;
                        tupleDeltas[idx] = new Vector2(dx[i], dy[i]);
                        touched[idx] = true;
                    }
                    InferUntouchedDeltas(tupleDeltas, touched, contourEnds, points);
                }

                for (int i = 0; i < pointCount; i++) total[i] += tupleDeltas[i] * scalar;
                any = true;
            }

            return any ? total : null;
        }

        /// <summary>
        ///  How much of this tuple applies at the current instance: 1 at its peak, falling linearly
        ///  to 0 at the edges of its region, and 0 outside it.
        /// </summary>
        /// <remarks>
        ///  An axis whose peak is 0 is one the tuple does not care about, and it must leave the
        ///  scalar ALONE rather than zero it -- that distinction is what lets a tuple that varies
        ///  only with weight still apply on a font that also has a width axis.
        /// </remarks>
        private float Scalar(float[] peak, float[]? interStart, float[]? interEnd)
        {
            float scalar = 1f;
            for (int a = 0; a < peak.Length; a++)
            {
                float peakA = peak[a];
                if (peakA == 0f) continue;

                float coord = a < _coords.Length ? _coords[a] : 0f;
                if (coord == 0f) return 0f;

                if (interStart is null || interEnd is null)
                {
                    if (coord < Math.Min(0f, peakA) || coord > Math.Max(0f, peakA)) return 0f;
                    scalar *= coord / peakA;
                }
                else
                {
                    float s = interStart[a], e = interEnd[a];
                    if (coord <= s || coord >= e) return 0f;
                    if (coord < peakA) scalar *= (coord - s) / (peakA - s);
                    else if (coord > peakA) scalar *= (e - coord) / (e - peakA);
                }
            }
            return scalar;
        }

        /// <summary>
        ///  Fills in the points a tuple did not list, per contour, per axis.
        /// </summary>
        /// <remarks>
        ///  A tuple may name only the points it moves, and the rest are NOT left at zero: they follow
        ///  their neighbours. Between two touched points, an untouched one keeps its relative
        ///  position if it lies between them, and otherwise simply shifts with the nearer of the two.
        ///  A contour with one touched point moves entirely with it; a contour with none does not
        ///  move. Leaving them at zero instead tears the outline open along every contour that has a
        ///  partial delta -- which is most of them, since that is the whole point of listing points.
        /// </remarks>
        private static void InferUntouchedDeltas(Vector2[] deltas, bool[] touched, int[] contourEnds, Vector2[] points)
        {
            int first = 0;
            foreach (int last in contourEnds)
            {
                if (last >= deltas.Length || last < first) { first = last + 1; continue; }
                InferContour(deltas, touched, points, first, last);
                first = last + 1;
            }
        }

        private static void InferContour(Vector2[] deltas, bool[] touched, Vector2[] points, int first, int last)
        {
            int n = last - first + 1;
            if (n <= 0) return;

            // Any touched point at all?
            int firstTouched = -1, touchedCount = 0;
            for (int i = first; i <= last; i++)
            {
                if (!touched[i]) continue;
                if (firstTouched < 0) firstTouched = i;
                touchedCount++;
            }
            if (touchedCount == 0) return;

            if (touchedCount == 1)
            {
                Vector2 d = deltas[firstTouched];
                for (int i = first; i <= last; i++) deltas[i] = d;
                return;
            }

            // Walk the touched points in order, filling each gap between consecutive ones. The
            // contour is a ring, so the last gap wraps from the final touched point to the first.
            int prev = firstTouched;
            for (int step = 1; step <= n; step++)
            {
                int i = first + ((firstTouched - first) + step) % n;
                if (!touched[i]) continue;
                FillGap(deltas, points, first, last, prev, i);
                prev = i;
                if (i == firstTouched) break;
            }
        }

        private static void FillGap(Vector2[] deltas, Vector2[] points, int first, int last, int a, int b)
        {
            int n = last - first + 1;
            for (int step = 1; ; step++)
            {
                int i = first + ((a - first) + step) % n;
                if (i == b) return;
                deltas[i] = new Vector2(
                    Interpolate(points[i].X, points[a].X, points[b].X, deltas[a].X, deltas[b].X),
                    Interpolate(points[i].Y, points[a].Y, points[b].Y, deltas[a].Y, deltas[b].Y));
                if (step > n) return;   // malformed contour; do not spin
            }
        }

        private static float Interpolate(float v, float va, float vb, float da, float db)
        {
            if (va > vb) { (va, vb) = (vb, va); (da, db) = (db, da); }

            // Outside the pair: follow the nearer endpoint rather than extrapolating.
            if (v <= va) return da;
            if (v >= vb) return db;
            if (va == vb) return da;

            float t = (v - va) / (vb - va);
            return da + (db - da) * t;
        }

        // ---- the two packed encodings gvar uses ----

        /// <summary>
        ///  Reads a packed point-number list. Returns null for "every point", which is how a tuple
        ///  that moves the whole glyph is spelled.
        /// </summary>
        private int[]? ReadPointNumbers(ref int p, int end, int pointCount)
        {
            if (p >= end) return Array.Empty<int>();

            int count = _data[p++];
            if ((count & 0x80) != 0)
            {
                if (p >= end) return Array.Empty<int>();
                count = ((count & 0x7F) << 8) | _data[p++];
            }
            if (count == 0) return null;   // all points

            var numbers = new int[count];
            int written = 0, value = 0;
            while (written < count && p < end)
            {
                int control = _data[p++];
                bool words = (control & 0x80) != 0;
                int run = (control & 0x7F) + 1;

                for (int i = 0; i < run && written < count; i++)
                {
                    if (words)
                    {
                        if (p + 2 > end) return numbers[..written];
                        value += U16(_data, p); p += 2;
                    }
                    else
                    {
                        if (p >= end) return numbers[..written];
                        value += _data[p++];
                    }
                    numbers[written++] = value;
                }
            }
            return written == count ? numbers : numbers[..written];
        }

        private int[] ReadPackedDeltas(ref int p, int end, int count)
        {
            var deltas = new int[count];
            int written = 0;
            while (written < count && p < end)
            {
                int control = _data[p++];
                int run = (control & 0x3F) + 1;

                if ((control & 0x80) != 0)          // DELTAS_ARE_ZERO
                {
                    written += Math.Min(run, count - written);
                }
                else if ((control & 0x40) != 0)     // DELTAS_ARE_WORDS
                {
                    for (int i = 0; i < run && written < count; i++)
                    {
                        if (p + 2 > end) return deltas;
                        deltas[written++] = (short)U16(_data, p); p += 2;
                    }
                }
                else
                {
                    for (int i = 0; i < run && written < count; i++)
                    {
                        if (p >= end) return deltas;
                        deltas[written++] = (sbyte)_data[p++];
                    }
                }
            }
            return deltas;
        }

        // ---- big-endian primitives ----

        private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        private static uint U32(byte[] d, int o)
            => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

        /// <summary>16.16 fixed point, as fvar states its axis bounds.</summary>
        private static float Fixed(byte[] d, int o) => (int)U32(d, o) / 65536f;

        /// <summary>2.14 fixed point, the normalized-coordinate encoding.</summary>
        private static float F2Dot14(byte[] d, int o) => (short)U16(d, o) / 16384f;
    }
}
