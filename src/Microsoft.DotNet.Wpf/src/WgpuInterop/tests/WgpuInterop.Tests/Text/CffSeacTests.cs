// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// 'seac' accent composition in CFF, over a font built here rather than found on the machine.
//
// The other CFF tests take whatever .otf the box happens to have and skip when there is none. That
// cannot work for seac: the operator is legacy, most shipping faces do not use it, and a test that
// skips everywhere proves nothing. So this file assembles a five-glyph OpenType/CFF font with
// squares for outlines -- small enough to read, and every byte of it is a fact the decoder has to
// get right (INDEX offsets, the top DICT, a format-0 charset, Type 2 charstrings).
//
// What makes the assertions non-circular: the font's charset names its glyphs by the SIDs "A" and
// "acute" have in the CFF standard strings, while the composite names them by their StandardEncoding
// CODES. Those are two different tables and only the decoder joins them, so a wrong entry in its
// code -> SID table drops the accent rather than quietly agreeing with the test.
//
// No GPU: outlines only.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public sealed class CffSeacTests
    {
        private const int UnitsPerEm = 1000;

        // Glyph ids in the font built below.
        private const int GNotdef = 0, GBase = 1, GAccent = 2, GComposite = 3, GCompositeWithWidth = 4;

        // The SIDs "A" and "acute" hold in the CFF standard strings, which is what a charset stores.
        private const int SidA = 34, SidAcute = 125;

        // The codes "A" and "acute" hold in StandardEncoding, which is what seac's arguments are.
        private const int CodeA = 65, CodeAcute = 194;

        // Where the composite puts the accent, in font units.
        private const int Adx = 500, Ady = 600;

        [Fact]
        public void ACompositeGlyphDrawsBothOfItsParts()
        {
            var font = new CffFont(BuildFont());

            Assert.True(font.TryGetGlyphOutline(GComposite, out List<PathFigure> composite),
                "the seac composite produced no outline at all -- the operator was ignored");
            Assert.Equal(2, composite.Count);

            Assert.True(font.TryGetGlyphOutline(GBase, out List<PathFigure> baseGlyph));
            Assert.True(font.TryGetGlyphOutline(GAccent, out List<PathFigure> accent));

            // Font units are y-up and the outline comes back y-down, so the accent's rise is negative.
            float scale = font.PixelsPerEm / (float)UnitsPerEm;
            var shift = new Vector2(Adx * scale, -Ady * scale);

            AssertSamePoints(Points(baseGlyph[0]), Points(composite[0]), Vector2.Zero, "base");
            AssertSamePoints(Points(accent[0]), Points(composite[1]), shift, "accent");
        }

        /// <summary>
        /// The same composition written with an explicit width in front of it. endchar takes 0, 1, 4
        /// or 5 arguments, so only odd parity means a leading width -- reading "more than none" as one
        /// consumes adx and leaves a three-argument seac that cannot be resolved.
        /// </summary>
        [Fact]
        public void AWidthBeforeTheCompositionDoesNotEatItsArguments()
        {
            var font = new CffFont(BuildFont());

            Assert.True(font.TryGetGlyphOutline(GComposite, out List<PathFigure> plain));
            Assert.True(font.TryGetGlyphOutline(GCompositeWithWidth, out List<PathFigure> withWidth));

            Assert.Equal(plain.Count, withWidth.Count);
            for (int i = 0; i < plain.Count; i++)
                AssertSamePoints(Points(plain[i]), Points(withWidth[i]), Vector2.Zero, $"figure {i}");
        }

        /// <summary>A width-only endchar is a BLANK glyph, not a composition with missing arguments.</summary>
        [Fact]
        public void AWidthOnlyEndcharStaysBlank()
        {
            var font = new CffFont(BuildFont());

            Assert.False(font.TryGetGlyphOutline(GNotdef, out List<PathFigure> figures));
            Assert.Empty(figures);
        }

        // ---- assertions ----

        private static List<Vector2> Points(PathFigure fig)
        {
            var pts = new List<Vector2> { fig.Start };
            foreach (PathSegment s in fig.Segments)
                if (s is LineSegment l) pts.Add(l.Point);
            return pts;
        }

        private static void AssertSamePoints(List<Vector2> expected, List<Vector2> actual, Vector2 shift, string what)
        {
            Assert.True(expected.Count == actual.Count,
                $"{what}: expected {expected.Count} points, got {actual.Count}");
            for (int i = 0; i < expected.Count; i++)
            {
                Vector2 want = expected[i] + shift, got = actual[i];
                Assert.True(Vector2.Distance(want, got) < 0.01f, $"{what} point {i}: expected {want}, got {got}");
            }
        }

        // ---- a five-glyph OpenType/CFF font ----

        /// <summary>
        /// .notdef (width only), a 300-unit square named "A", a 50-unit square named "acute", and two
        /// composites of the two -- one bare, one behind a width.
        /// </summary>
        private static byte[] BuildFont()
        {
            byte[][] charStrings =
            {
                CharString(w => { w.Num(250); w.Op(14); }),                                   // .notdef: width, endchar
                CharString(w => w.Square(100, 100, 300)),                                     // "A"
                CharString(w => w.Square(0, 0, 50)),                                          // "acute"
                CharString(w => { w.Num(Adx); w.Num(Ady); w.Num(CodeA); w.Num(CodeAcute); w.Op(14); }),
                CharString(w => { w.Num(250); w.Num(Adx); w.Num(Ady); w.Num(CodeA); w.Num(CodeAcute); w.Op(14); }),
            };

            // Glyph -> SID. The two composites need names of their own; anything past 390 is a custom
            // string, and nothing here looks them up, so two placeholders in the String INDEX do.
            int[] sids = { 0, SidA, SidAcute, 391, 392 };

            return Sfnt(Cff(charStrings, sids), charStrings.Length);
        }

        // ---- CFF ----

        private static byte[] Cff(byte[][] charStrings, int[] sids)
        {
            byte[] header = { 1, 0, 4, 4 };   // major, minor, hdrSize, offSize
            byte[] nameIndex = Index(new[] { Encoding.ASCII.GetBytes("SeacTest") });
            byte[] stringIndex = Index(new[] { Encoding.ASCII.GetBytes("Aacute"), Encoding.ASCII.GetBytes("Aacute.alt") });
            byte[] gsubrIndex = Index(Array.Empty<byte[]>());
            byte[] charset = Charset(sids);
            byte[] charStringIndex = Index(charStrings);

            // The top DICT's operands use the fixed-width 5-byte integer form, so the DICT is the same
            // length whatever the offsets are -- which is what lets them be computed from a layout
            // that already includes the DICT.
            int topLen = Index(new[] { TopDict(0, 0) }).Length;
            int prefix = header.Length + nameIndex.Length + topLen + stringIndex.Length + gsubrIndex.Length;
            byte[] topIndex = Index(new[] { TopDict(charsetOffset: prefix, charStringsOffset: prefix + charset.Length) });
            Assert.Equal(topLen, topIndex.Length);

            var cff = new List<byte>();
            cff.AddRange(header);
            cff.AddRange(nameIndex);
            cff.AddRange(topIndex);
            cff.AddRange(stringIndex);
            cff.AddRange(gsubrIndex);
            cff.AddRange(charset);
            cff.AddRange(charStringIndex);
            return cff.ToArray();
        }

        private static byte[] TopDict(int charsetOffset, int charStringsOffset)
        {
            var d = new List<byte>();
            d.AddRange(DictInt(charsetOffset)); d.Add(15);        // charset
            d.AddRange(DictInt(charStringsOffset)); d.Add(17);    // CharStrings
            return d.ToArray();
        }

        private static byte[] DictInt(int v)
            => new byte[] { 29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        /// <summary>Format 0: one SID per glyph after .notdef, which is implicitly SID 0.</summary>
        private static byte[] Charset(int[] sids)
        {
            var c = new List<byte> { 0 };
            for (int gid = 1; gid < sids.Length; gid++) { c.Add((byte)(sids[gid] >> 8)); c.Add((byte)sids[gid]); }
            return c.ToArray();
        }

        /// <summary>A CFF INDEX. offSize 4 throughout, so no size ever has to be predicted.</summary>
        private static byte[] Index(byte[][] items)
        {
            var idx = new List<byte>();
            idx.Add((byte)(items.Length >> 8)); idx.Add((byte)items.Length);
            if (items.Length == 0) return idx.ToArray();

            idx.Add(4);   // offSize
            int off = 1;  // offsets are 1-based
            AddOffset(idx, off);
            foreach (byte[] item in items) { off += item.Length; AddOffset(idx, off); }
            foreach (byte[] item in items) idx.AddRange(item);
            return idx.ToArray();

            static void AddOffset(List<byte> to, int v)
            {
                to.Add((byte)(v >> 24)); to.Add((byte)(v >> 16)); to.Add((byte)(v >> 8)); to.Add((byte)v);
            }
        }

        // ---- Type 2 charstrings ----

        private sealed class CharStringWriter
        {
            public readonly List<byte> Bytes = new();

            /// <summary>Every number goes out in the 16-bit form, whatever its size: uniform, legal,
            /// and it keeps the charstrings readable as bytes.</summary>
            public void Num(int v) { Bytes.Add(28); Bytes.Add((byte)(v >> 8)); Bytes.Add((byte)v); }
            public void Op(int op) => Bytes.Add((byte)op);

            /// <summary>rmoveto to (x, y), three rlinetos round a square, then endchar.</summary>
            public void Square(int x, int y, int size)
            {
                Num(x); Num(y); Op(21);              // rmoveto
                Num(size); Num(0); Op(5);            // rlineto
                Num(0); Num(size); Op(5);
                Num(-size); Num(0); Op(5);
                Op(14);                              // endchar
            }
        }

        private static byte[] CharString(Action<CharStringWriter> write)
        {
            var w = new CharStringWriter();
            write(w);
            return w.Bytes.ToArray();
        }

        // ---- the sfnt around it ----

        private static byte[] Sfnt(byte[] cff, int numGlyphs)
        {
            var head = new byte[54];
            head[18] = (byte)(UnitsPerEm >> 8); head[19] = unchecked((byte)UnitsPerEm);

            var maxp = new byte[6];
            maxp[0] = 0x00; maxp[1] = 0x00; maxp[2] = 0x50; maxp[3] = 0x00;   // version 0.5
            maxp[4] = (byte)(numGlyphs >> 8); maxp[5] = (byte)numGlyphs;

            var hhea = new byte[36];
            hhea[34] = (byte)(numGlyphs >> 8); hhea[35] = (byte)numGlyphs;    // numberOfHMetrics

            var hmtx = new byte[numGlyphs * 4];
            for (int i = 0; i < numGlyphs; i++) { hmtx[i * 4] = 0x01; hmtx[i * 4 + 1] = 0xF4; }   // advance 500

            byte[] cmap = { 0, 0, 0, 0 };   // version 0, no subtables: nothing here maps characters

            (string Tag, byte[] Data)[] tables =
            {
                ("CFF ", cff), ("cmap", cmap), ("head", head), ("hhea", hhea), ("hmtx", hmtx), ("maxp", maxp),
            };

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("OTTO"));
            file.Add((byte)(tables.Length >> 8)); file.Add((byte)tables.Length);
            file.AddRange(new byte[6]);   // searchRange / entrySelector / rangeShift: unread here

            int offset = 12 + tables.Length * 16;
            var directory = new List<byte>();
            var body = new List<byte>();
            foreach ((string tag, byte[] data) in tables)
            {
                directory.AddRange(Encoding.ASCII.GetBytes(tag));
                directory.AddRange(new byte[4]);                  // checksum: unread here
                directory.AddRange(BE(offset));
                directory.AddRange(BE(data.Length));

                body.AddRange(data);
                int pad = (4 - data.Length % 4) % 4;              // tables start on 4-byte boundaries
                body.AddRange(new byte[pad]);
                offset += data.Length + pad;
            }

            file.AddRange(directory);
            file.AddRange(body);
            return file.ToArray();
        }

        private static byte[] BE(int v)
            => new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }
}
