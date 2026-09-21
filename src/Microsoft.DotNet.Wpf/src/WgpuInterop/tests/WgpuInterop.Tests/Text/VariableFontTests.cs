// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Variable fonts: fvar/avar/gvar, over a font built here.
//
// A synthetic font is the only way to assert NUMBERS. Against a real face all a test can honestly
// check is that Bold came out wider than Regular, which passes just as happily on a font being
// emboldened synthetically -- the very thing this work replaces. So the font below has one axis, one
// square glyph and deltas chosen to land on values that can be worked out by hand, and every
// assertion states the arithmetic it expects.
//
// The axis is deliberately 100..400..1000 rather than the conventional 100..400..900: it makes the
// Bold request (700) normalize to exactly 0.5, which is representable in F2DOT14. At 0.6 the
// encoding rounds and every expected value would need a tolerance wide enough to hide a real error.
//
// What each test pins down:
//
//   * normalization and the tuple scalar   -- a delta applies at half strength halfway along the axis
//   * phantom points                       -- which is how an instance changes the ADVANCE, since
//                                             this stack reads no HVAR
//   * avar                                 -- the same request, warped, moves less far
//   * inferred deltas (IUP)                -- points a tuple does not list follow the ones it does,
//                                             which is the part that tears outlines when it is wrong
//   * static fonts are untouched           -- no fvar means the old synthetic path, unchanged
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class VariableFontTests
    {
        private const int UnitsPerEm = 1000;
        private const int BaseEmPixels = 48;                       // TrueTypeFont's fixed raster size
        private const float Scale = BaseEmPixels / (float)UnitsPerEm;

        private const int GSquare = 1;      // deltas for every point
        private const int GPartial = 2;     // deltas for two points; the rest are inferred
        private const int Advance = 600;    // font units, both glyphs

        // The square both glyphs start as: (0,0) (500,0) (500,500) (0,500).
        private const int Side = 500;

        /// <summary>Bold asks for 700 on an axis of 100..400..1000, which normalizes to exactly 0.5.</summary>
        private const float BoldScalar = 0.5f;

        // ---- the design space ----

        [Fact]
        public void ADeltaAppliesInProportionToHowFarAlongTheAxisTheInstanceSits()
        {
            var regular = new TrueTypeFont(BuildFont());
            var bold = new TrueTypeFont(BuildFont(), synthesizeBold: true);

            // The tuple widens the square's right edge by 100 units at the axis peak.
            Assert.Equal(Side * Scale, MaxX(regular, GSquare), 3);
            Assert.Equal((Side + 100 * BoldScalar) * Scale, MaxX(bold, GSquare), 3);
        }

        /// <summary>
        ///  The advance rides on the phantom points, which is the whole reason they are carried
        ///  through the delta machinery as if they were outline points.
        /// </summary>
        [Fact]
        public void TheInstanceMovesTheAdvanceWidth()
        {
            var regular = new TrueTypeFont(BuildFont());
            var bold = new TrueTypeFont(BuildFont(), synthesizeBold: true);

            Assert.Equal(Advance * Scale, regular.Advance(GSquare), 3);
            Assert.Equal((Advance + 120 * BoldScalar) * Scale, bold.Advance(GSquare), 3);
        }

        /// <summary>
        ///  avar warps the axis. With a map sending 0.5 to 0.25, the same Bold request must move the
        ///  outline half as far -- that is what a font uses avar to say.
        /// </summary>
        [Fact]
        public void AvarWarpsHowFarTheRequestActuallyGoes()
        {
            var plain = new TrueTypeFont(BuildFont(), synthesizeBold: true);
            var warped = new TrueTypeFont(BuildFont(withAvar: true), synthesizeBold: true);

            Assert.Equal((Side + 100 * 0.5f) * Scale, MaxX(plain, GSquare), 3);
            Assert.Equal((Side + 100 * 0.25f) * Scale, MaxX(warped, GSquare), 3);
        }

        /// <summary>
        ///  A tuple that lists only some points does not leave the others where they were: they
        ///  follow their neighbours around the contour. Here points 0 and 2 are moved by +100 and
        ///  +200; point 1 sits at the far end of that span and takes +200, and point 3 sits at the
        ///  near end and takes +100. Leave them at zero instead and the square tears open.
        /// </summary>
        [Fact]
        public void PointsATupleDoesNotListAreInterpolatedFromTheOnesItDoes()
        {
            var bold = new TrueTypeFont(BuildFont(), synthesizeBold: true);

            List<Vector2> pts = Points(bold, GPartial);

            // The contour closes back onto its start, so four corners come out as five points.
            float left = MinX(pts), right = MaxX(pts);

            Assert.Equal((0 + 100 * BoldScalar) * Scale, left, 3);
            Assert.Equal((Side + 200 * BoldScalar) * Scale, right, 3);
        }

        /// <summary>A font with no fvar keeps the synthetic path, which must still visibly embolden.</summary>
        [Fact]
        public void AStaticFontStillSynthesisesBold()
        {
            var regular = new TrueTypeFont(BuildFont(variable: false));
            var bold = new TrueTypeFont(BuildFont(variable: false), synthesizeBold: true);

            Assert.Equal(Side * Scale, MaxX(regular, GSquare), 3);
            Assert.True(MaxX(bold, GSquare) > MaxX(regular, GSquare),
                "a static font must still be emboldened by dilating its outline; nothing else can make it bold");
        }

        // ---- a real face, when the machine has one ----

        /// <summary>
        ///  The same behaviour on a font nobody here built. It can only assert THAT the outlines
        ///  moved -- which is why the synthetic tests above exist to say by how much -- but it is the
        ///  only check that the table parsing survives a real face: four axes, shared tuples, long
        ///  offsets, thousands of glyphs and composites among them.
        /// </summary>
        /// <remarks>
        ///  Outlines, not advances. A weight axis must move outlines or it is not a weight axis, but
        ///  it need not move advances at all -- the first font this found on macOS was SF Mono, where
        ///  every advance is identical at every weight because that is what monospaced means. The
        ///  synthetic font above pins the advance arithmetic instead, where the answer is known.
        /// </remarks>
        [Fact]
        public void ARealVariableFontVariesItsOutlines()
        {
            string? path = FindVariableFont();
            Assert.SkipWhen(path is null, "no variable font with a weight axis and 'gvar' on this machine");

            byte[] bytes = File.ReadAllBytes(path!);
            var regular = new TrueTypeFont(bytes);
            var bold = new TrueTypeFont(bytes, synthesizeBold: true);

            // Over a spread of glyphs: any single one may be a space, or a mark the axis leaves alone.
            int compared = 0, differing = 0;
            for (int gid = 1; gid < Math.Min(400, regular.GlyphCount); gid++)
            {
                if (!regular.TryGetGlyphOutline(gid, out List<PathFigure> a)) continue;
                if (!bold.TryGetGlyphOutline(gid, out List<PathFigure> b)) continue;

                compared++;
                if (Extent(a) != Extent(b)) differing++;
            }

            Assert.True(compared > 0, $"{path} produced no outlines at all to compare");
            Assert.True(differing > 0,
                $"none of {compared} glyphs of {path} changed shape between the default instance and " +
                "weight 700, so the deltas were parsed but not applied");
        }

        /// <summary>A cheap shape summary: the sum of every coordinate the outline visits.</summary>
        private static float Extent(List<PathFigure> figures)
        {
            float sum = 0f;
            foreach (PathFigure f in figures)
            {
                sum += f.Start.X + f.Start.Y;
                foreach (PathSegment s in f.Segments)
                {
                    switch (s)
                    {
                        case LineSegment l: sum += l.Point.X + l.Point.Y; break;
                        case QuadraticBezierSegment q: sum += q.Control.X + q.Control.Y + q.Point.X + q.Point.Y; break;
                        case CubicBezierSegment c:
                            sum += c.Control1.X + c.Control1.Y + c.Control2.X + c.Control2.Y + c.Point.X + c.Point.Y;
                            break;
                    }
                }
            }
            return sum;
        }

        /// <summary>
        ///  A face this test can actually draw a conclusion from: TrueType outlines with per-glyph
        ///  deltas, and a weight axis with somewhere to go.
        /// </summary>
        /// <remarks>
        ///  All three conditions are the test's PRECONDITION, not a convenience. "Has an fvar" is not
        ///  enough -- a font whose weight axis is a single point, or which keeps its outlines in CFF2,
        ///  cannot make the assertion below true however correct the code is, and picking one would
        ///  produce a failure that says nothing.
        /// </remarks>
        private static string? FindVariableFont()
        {
            string[] roots =
            {
                "/System/Library/Fonts", "/Library/Fonts",
                "/usr/share/fonts", "C:\\Windows\\Fonts",
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.ttf", SearchOption.AllDirectories); }
                catch (UnauthorizedAccessException) { continue; }

                foreach (string file in files)
                {
                    if (HasVaryingWeight(file)) return file;
                }
            }
            return null;
        }

        private static bool HasVaryingWeight(string path)
        {
            try
            {
                byte[] d = File.ReadAllBytes(path);
                if (d.Length < 12) return false;
                if (d[0] == 't' && d[1] == 't' && d[2] == 'c') return false;   // collection

                int numTables = (d[4] << 8) | d[5];
                if (12 + numTables * 16 > d.Length) return false;

                int fvar = -1;
                bool gvar = false;
                for (int i = 0; i < numTables; i++)
                {
                    int o = 12 + i * 16;
                    string tag = Encoding.ASCII.GetString(d, o, 4);
                    int offset = (int)(((uint)d[o + 8] << 24) | ((uint)d[o + 9] << 16) | ((uint)d[o + 10] << 8) | d[o + 11]);
                    if (tag == "fvar") fvar = offset;
                    else if (tag == "gvar") gvar = true;
                }
                if (fvar < 0 || !gvar || fvar + 16 > d.Length) return false;

                int axesOffset = fvar + ((d[fvar + 4] << 8) | d[fvar + 5]);
                int axisCount = (d[fvar + 8] << 8) | d[fvar + 9];
                int axisSize = (d[fvar + 10] << 8) | d[fvar + 11];
                if (axesOffset + axisCount * axisSize > d.Length) return false;

                for (int i = 0; i < axisCount; i++)
                {
                    int q = axesOffset + i * axisSize;
                    if (Encoding.ASCII.GetString(d, q, 4) != "wght") continue;
                    float def = Fixed(d, q + 8), max = Fixed(d, q + 12);
                    return max > def;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        private static float Fixed(byte[] d, int o)
            => (int)(((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3]) / 65536f;

        // ---- reading the result ----

        private static List<Vector2> Points(TrueTypeFont font, int gid)
        {
            Assert.True(font.TryGetGlyphOutline(gid, out List<PathFigure> figures), $"glyph {gid} has no outline");
            var pts = new List<Vector2>();
            foreach (PathFigure f in figures)
            {
                pts.Add(f.Start);
                foreach (PathSegment s in f.Segments)
                {
                    if (s is LineSegment l) pts.Add(l.Point);
                }
            }
            return pts;
        }

        private static float MaxX(TrueTypeFont font, int gid) => MaxX(Points(font, gid));

        private static float MaxX(List<Vector2> pts)
        {
            float max = float.MinValue;
            foreach (Vector2 p in pts) max = Math.Max(max, p.X);
            return max;
        }

        private static float MinX(List<Vector2> pts)
        {
            float min = float.MaxValue;
            foreach (Vector2 p in pts) min = Math.Min(min, p.X);
            return min;
        }

        // ---- a two-glyph variable TrueType font ----

        private static byte[] BuildFont(bool variable = true, bool withAvar = false)
        {
            byte[] glyphSquare = SimpleGlyph();
            byte[] glyf = Concat(Array.Empty<byte>(), glyphSquare, glyphSquare);   // gid 0 empty, 1 and 2 square

            // loca, short format: offsets in units of 2 bytes.
            var loca = new byte[(3 + 1) * 2];
            int off = 0;
            WriteU16(loca, 0, off / 2);
            off += 0; WriteU16(loca, 2, off / 2);                       // gid 0: empty
            off += glyphSquare.Length; WriteU16(loca, 4, off / 2);      // gid 1
            off += glyphSquare.Length; WriteU16(loca, 6, off / 2);      // gid 2

            var tables = new List<(string Tag, byte[] Data)>
            {
                ("cmap", new byte[] { 0, 0, 0, 0 }),                    // no subtables; glyphs are asked for by id
                ("glyf", glyf),
                ("head", Head()),
                ("hhea", Hhea(3)),
                ("hmtx", Hmtx(3)),
                ("loca", loca),
                ("maxp", Maxp(3)),
            };

            if (variable)
            {
                tables.Add(("fvar", Fvar()));
                tables.Add(("gvar", Gvar()));
                if (withAvar) tables.Add(("avar", Avar()));
            }

            tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
            return Sfnt(tables);
        }

        /// <summary>A 500x500 square, four on-curve points, one contour.</summary>
        private static byte[] SimpleGlyph()
        {
            var g = new List<byte>();
            WriteI16(g, 1);                       // numberOfContours
            WriteI16(g, 0); WriteI16(g, 0);       // xMin, yMin
            WriteI16(g, Side); WriteI16(g, Side); // xMax, yMax
            WriteU16(g, 3);                       // endPtsOfContours[0]
            WriteU16(g, 0);                       // instructionLength

            for (int i = 0; i < 4; i++) g.Add(0x01);   // on-curve, 16-bit deltas

            // x deltas then y deltas, each relative to the previous point.
            foreach (int dx in new[] { 0, Side, 0, -Side }) WriteI16(g, dx);
            foreach (int dy in new[] { 0, 0, Side, 0 }) WriteI16(g, dy);
            return g.ToArray();
        }

        private static byte[] Fvar()
        {
            var f = new List<byte>();
            WriteU16(f, 1); WriteU16(f, 0);   // version
            WriteU16(f, 16);                  // axesArrayOffset
            WriteU16(f, 2);                   // reserved
            WriteU16(f, 1);                   // axisCount
            WriteU16(f, 20);                  // axisSize
            WriteU16(f, 0); WriteU16(f, 0);   // instanceCount, instanceSize

            f.AddRange(Encoding.ASCII.GetBytes("wght"));
            WriteFixed(f, 100f); WriteFixed(f, 400f); WriteFixed(f, 1000f);
            WriteU16(f, 0); WriteU16(f, 0);   // flags, nameID
            return f.ToArray();
        }

        /// <summary>Sends 0.5 to 0.25 and leaves the ends alone.</summary>
        private static byte[] Avar()
        {
            var a = new List<byte>();
            WriteU16(a, 1); WriteU16(a, 0);   // version
            WriteU16(a, 0);                   // reserved
            WriteU16(a, 1);                   // axisCount

            WriteU16(a, 4);                   // positionMapCount
            WriteF2Dot14(a, -1f); WriteF2Dot14(a, -1f);
            WriteF2Dot14(a, 0f); WriteF2Dot14(a, 0f);
            WriteF2Dot14(a, 0.5f); WriteF2Dot14(a, 0.25f);
            WriteF2Dot14(a, 1f); WriteF2Dot14(a, 1f);
            return a.ToArray();
        }

        private static byte[] Gvar()
        {
            // gid 1: every point listed. x deltas widen the right edge and stretch the advance.
            byte[] all = TupleData(
                pointNumbers: null,
                dx: new[] { 0, 100, 100, 0, /* phantom */ 0, 120, 0, 0 },
                dy: new int[8]);

            // gid 2: points 0 and 2 only; 1 and 3 have to be inferred from them.
            byte[] partial = TupleData(
                pointNumbers: new[] { 0, 2 },
                dx: new[] { 100, 200 },
                dy: new[] { 0, 0 });

            byte[] gid1 = GlyphVariationData(all, privatePoints: false);
            byte[] gid2 = GlyphVariationData(partial, privatePoints: true);

            var g = new List<byte>();
            WriteU16(g, 1); WriteU16(g, 0);   // version
            WriteU16(g, 1);                   // axisCount
            WriteU16(g, 0);                   // sharedTupleCount
            WriteU32(g, 0);                   // sharedTuplesOffset (none)
            WriteU16(g, 3);                   // glyphCount
            WriteU16(g, 0);                   // flags: short offsets

            int headerSize = 20 + (3 + 1) * 2;
            int dataStart = Align4(headerSize);
            WriteU32(g, (uint)dataStart);     // glyphVariationDataArrayOffset

            // Offsets are halved in short form, so every entry must land on an even byte.
            int o0 = 0;
            int o1 = 0;                        // gid 0: no data
            int o2 = o1 + Align2(gid1.Length);
            int o3 = o2 + Align2(gid2.Length);
            WriteU16(g, o0 / 2); WriteU16(g, o1 / 2); WriteU16(g, o2 / 2); WriteU16(g, o3 / 2);

            while (g.Count < dataStart) g.Add(0);
            g.AddRange(gid1);
            while (g.Count < dataStart + o2) g.Add(0);
            g.AddRange(gid2);
            while (g.Count < dataStart + o3) g.Add(0);
            return g.ToArray();
        }

        /// <summary>One glyph's variation data: a single tuple peaking at the far end of the axis.</summary>
        private static byte[] GlyphVariationData(byte[] serialized, bool privatePoints)
        {
            const int TupleHeaderSize = 4 + 2;   // size + index + one F2DOT14 peak

            var d = new List<byte>();
            WriteU16(d, 1);                                   // tupleVariationCount, no shared points
            WriteU16(d, 4 + TupleHeaderSize);                 // dataOffset: past this header and the tuple's

            WriteU16(d, serialized.Length);                   // variationDataSize
            WriteU16(d, 0x8000 | (privatePoints ? 0x2000 : 0));  // EMBEDDED_PEAK_TUPLE [+ PRIVATE_POINT_NUMBERS]
            WriteF2Dot14(d, 1f);                              // peak: the top of the axis

            d.AddRange(serialized);
            return d.ToArray();
        }

        private static byte[] TupleData(int[]? pointNumbers, int[] dx, int[] dy)
        {
            var d = new List<byte>();
            if (pointNumbers is not null)
            {
                d.Add((byte)pointNumbers.Length);
                d.Add((byte)(pointNumbers.Length - 1));       // one run, byte-sized, no POINTS_ARE_WORDS
                int previous = 0;
                foreach (int n in pointNumbers) { d.Add((byte)(n - previous)); previous = n; }
            }
            WritePackedDeltas(d, dx);
            WritePackedDeltas(d, dy);
            return d.ToArray();
        }

        private static void WritePackedDeltas(List<byte> d, int[] values)
        {
            bool allZero = true;
            foreach (int v in values) if (v != 0) { allZero = false; break; }

            if (allZero)
            {
                d.Add((byte)(0x80 | (values.Length - 1)));    // DELTAS_ARE_ZERO
                return;
            }

            d.Add((byte)(0x40 | (values.Length - 1)));        // DELTAS_ARE_WORDS
            foreach (int v in values) WriteI16(d, v);
        }

        // ---- the sfnt around it ----

        private static byte[] Head()
        {
            var head = new byte[54];
            WriteU16(head, 18, UnitsPerEm);
            WriteU16(head, 50, 0);   // indexToLocFormat: short
            return head;
        }

        private static byte[] Maxp(int numGlyphs)
        {
            var maxp = new byte[32];
            maxp[2] = 0x50;                      // version 0.5... 1.0 in the high word; unread here
            WriteU16(maxp, 4, numGlyphs);
            return maxp;
        }

        private static byte[] Hhea(int numHMetrics)
        {
            var hhea = new byte[36];
            WriteU16(hhea, 34, numHMetrics);
            return hhea;
        }

        private static byte[] Hmtx(int numHMetrics)
        {
            var hmtx = new byte[numHMetrics * 4];
            for (int i = 0; i < numHMetrics; i++) WriteU16(hmtx, i * 4, Advance);
            return hmtx;
        }

        private static byte[] Sfnt(List<(string Tag, byte[] Data)> tables)
        {
            var file = new List<byte>();
            WriteU32(file, 0x00010000);          // sfnt version: TrueType outlines
            WriteU16(file, tables.Count);
            WriteU16(file, 0); WriteU16(file, 0); WriteU16(file, 0);   // searchRange etc: unread

            int offset = 12 + tables.Count * 16;
            var directory = new List<byte>();
            var body = new List<byte>();
            foreach ((string tag, byte[] data) in tables)
            {
                directory.AddRange(Encoding.ASCII.GetBytes(tag));
                WriteU32(directory, 0);          // checksum: unread
                WriteU32(directory, (uint)offset);
                WriteU32(directory, (uint)data.Length);

                body.AddRange(data);
                int pad = (4 - data.Length % 4) % 4;
                body.AddRange(new byte[pad]);
                offset += data.Length + pad;
            }

            file.AddRange(directory);
            file.AddRange(body);
            return file.ToArray();
        }

        // ---- big-endian writers ----

        private static int Align2(int v) => (v + 1) & ~1;
        private static int Align4(int v) => (v + 3) & ~3;

        private static byte[] Concat(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (byte[] p in parts) all.AddRange(p);
            return all.ToArray();
        }

        private static void WriteU16(List<byte> d, int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        private static void WriteI16(List<byte> d, int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        private static void WriteU32(List<byte> d, uint v)
        {
            d.Add((byte)(v >> 24)); d.Add((byte)(v >> 16)); d.Add((byte)(v >> 8)); d.Add((byte)v);
        }

        private static void WriteU16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }

        private static void WriteFixed(List<byte> d, float v) => WriteU32(d, (uint)(int)MathF.Round(v * 65536f));
        private static void WriteF2Dot14(List<byte> d, float v) => WriteI16(d, (int)MathF.Round(v * 16384f));
    }
}
