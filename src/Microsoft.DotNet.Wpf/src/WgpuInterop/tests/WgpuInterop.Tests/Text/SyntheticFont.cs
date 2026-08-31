// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;

namespace WgpuInterop.Tests.Text
{
    /// <summary>A TrueType font built from nothing, so that a test can choose the glyph program and
    /// then ask GDI what it did with it.
    /// <para>Everything else in this suite reads a shipping face and infers the rasterizer's rules
    /// from glyphs somebody else hinted. That has taken the parity work a long way and it has a
    /// floor: when GDI and we disagree about a stem, the font's own program, the pre-program, the
    /// control values and the interpreter are all in the way at once, and no measurement separates
    /// them. Here there is nothing in the way. One contour, four points, a program of six
    /// instructions, and one control value per glyph -- so what comes back IS the answer to "what
    /// does GDI's interpreter do with this MIRP", with no font in the way to argue about.</para>
    /// <para>Deliberately minimal: no fpgm, and a prep that only sets up the graphics state. A face
    /// that does nothing cannot be blamed for the result.</para></summary>
    internal static class SyntheticFont
    {
        public const int UnitsPerEm = 2048;
        public const int Ascender = 1638;
        public const int Descender = -410;

        /// <summary>One test case: a vertical bar whose right edge is moved by a MIRP against a
        /// control value, so the rendered stem width is the interpreter's answer.</summary>
        public readonly struct Bar
        {
            /// <summary>The control value, in font units.</summary>
            public readonly int Cvt;
            /// <summary>Where the bar's edges start, in font units, before any hinting.</summary>
            public readonly int Left, Right;
            /// <summary>MIRP's round bit.</summary>
            public readonly bool Round;
            /// <summary>MIRP's "keep at least the minimum distance" bit.</summary>
            public readonly bool MinDistance;

            public Bar(int cvt, int left, int right, bool round, bool minDistance)
            {
                Cvt = cvt; Left = left; Right = right; Round = round; MinDistance = minDistance;
            }
        }

        /// <summary>Build a font whose glyph i+1 is bars[i], mapped to codepoint 0x41 + i.</summary>
        public static byte[] Build(string family, IReadOnlyList<Bar> bars)
        {
            int numGlyphs = bars.Count + 1;                       // .notdef first

            // ---- glyf + loca ------------------------------------------------------------------
            var glyf = new MemoryStream();
            var loca = new List<uint> { 0 };
            // .notdef gets REAL CONTOURS. An empty glyph 0 is legal and GDI+ accepts it, but
            // AddFontMemResourceEx and AddFontResourceExW both refuse the font outright -- no
            // error code, just a null handle -- and a box here is what makes them take it.
            WriteBar(glyf, new Bar(0, 200, 800, false, false), 0, instructions: false);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            loca.Add((uint)glyf.Length);
            var cvts = new List<short>();
            foreach (Bar b in bars)
            {
                int cvtIndex = cvts.Count;
                cvts.Add((short)b.Cvt);
                WriteBar(glyf, b, cvtIndex, instructions: true);
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);
                loca.Add((uint)glyf.Length);
            }
            byte[] glyfData = glyf.ToArray();

            bool longLoca = glyfData.Length > 0x1FFFE;
            var locaData = new MemoryStream();
            foreach (uint off in loca)
            {
                if (longLoca) WriteU32(locaData, off);
                else WriteU16(locaData, (int)(off / 2));
            }

            // ---- the rest ---------------------------------------------------------------------
            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["OS/2"] = BuildOs2(),
                ["cmap"] = BuildCmap(numGlyphs),
                ["cvt "] = BuildCvt(cvts),
                ["glyf"] = glyfData,
                ["head"] = BuildHead(longLoca),
                ["hhea"] = BuildHhea(numGlyphs),
                ["hmtx"] = BuildHmtx(numGlyphs),
                ["loca"] = locaData.ToArray(),
                ["maxp"] = BuildMaxp(numGlyphs),
                ["name"] = BuildName(family),
                ["post"] = BuildPost(),
                ["prep"] = BuildPrep(),
            };

            return Assemble(tables);
        }

        /// <summary>A rectangle, and the six instructions that decide how wide it comes out.
        /// <para>SVTCA[x] so everything happens on the axis in question; MDAP[R] pins the LEFT edge
        /// to the grid, which is what a real face does and what Visual TrueType shows GDI doing;
        /// MIRP moves the right edge to the control value; IUP[x] carries the two top corners along
        /// with the corners below them.</para></summary>
        private static void WriteBar(Stream s, Bar b, int cvtIndex, bool instructions)
        {
            const int Bottom = 0, Top = 1400;

            // MIRP[abcde]: 0xE0 + a(set rp0) + b(min distance)<<1 + c(round)<<2 + distance-type<<3.
            byte mirp = (byte)(0xE0 | (b.MinDistance ? 0x02 : 0) | (b.Round ? 0x04 : 0));
            byte[] program =
            {
                0x01,                                    // SVTCA[1]  -- x axis
                0xB0, 0x00,                              // PUSHB[1] 0
                0x2F,                                    // MDAP[1]   -- round point 0 to the grid
                0xB1, 0x01, (byte)cvtIndex,              // PUSHB[2] 1, cvtIndex
                mirp,                                    // MIRP      -- move point 1 to it
                0x31,                                    // IUP[1]    -- x
            };

            WriteI16(s, 1);                              // numberOfContours
            WriteI16(s, b.Left); WriteI16(s, Bottom);    // xMin yMin
            WriteI16(s, b.Right); WriteI16(s, Top);      // xMax yMax
            WriteU16(s, 3);                              // endPtsOfContours[0]
            WriteU16(s, instructions ? program.Length : 0);
            if (instructions) s.Write(program, 0, program.Length);

            // Four points, all on-curve, x and y as signed 16-bit deltas.
            for (int i = 0; i < 4; i++) s.WriteByte(0x01);        // ON_CURVE, 16-bit deltas
            WriteI16(s, b.Left); WriteI16(s, b.Right - b.Left); WriteI16(s, 0); WriteI16(s, b.Left - b.Right);
            WriteI16(s, Bottom); WriteI16(s, 0); WriteI16(s, Top - Bottom); WriteI16(s, 0);
        }

        private static byte[] BuildCvt(List<short> cvts)
        {
            var m = new MemoryStream();
            foreach (short v in cvts) WriteI16(m, v);
            if (cvts.Count == 0) WriteI16(m, 0);
            return m.ToArray();
        }

        /// <summary>Only what the interpreter needs to be in a defined state: scan control off, and
        /// a cut-in wide enough that it never fires. A prep that decides things would be a prep
        /// whose decisions we would then be measuring.</summary>
        private static byte[] BuildPrep()
        {
            return new byte[]
            {
                0xB0, 0x00, 0x85,        // PUSHB[1] 0, SCANCTRL  -- no dropout control
                0xB0, 0x00, 0x8D,        // PUSHB[1] 0, SCANTYPE
                0xB0, 0x40, 0x1D,        // PUSHB[1] 64, SCVTCI   -- cut-in one pixel
                // No INSTCTRL: it pops TWO values and pushing one underflowed our own
                // interpreter's stack. A probe that faults the thing it is probing is no probe.
            };
        }

        private static byte[] BuildHead(bool longLoca)
        {
            var m = new MemoryStream();
            WriteU32(m, 0x00010000);                  // version
            WriteU32(m, 0x00010000);                  // fontRevision
            WriteU32(m, 0);                           // checkSumAdjustment, patched by Assemble
            WriteU32(m, 0x5F0F3CF5);                  // magic
            WriteU16(m, 0x000B);                      // flags
            WriteU16(m, UnitsPerEm);
            for (int i = 0; i < 16; i++) m.WriteByte(0);   // created, modified
            WriteI16(m, 0); WriteI16(m, -500);        // xMin yMin
            WriteI16(m, 2048); WriteI16(m, 2000);     // xMax yMax
            WriteU16(m, 0);                           // macStyle
            WriteU16(m, 6);                           // lowestRecPPEM
            WriteI16(m, 2);                           // fontDirectionHint
            WriteI16(m, longLoca ? 1 : 0);            // indexToLocFormat
            WriteI16(m, 0);                           // glyphDataFormat
            return m.ToArray();
        }

        private static byte[] BuildHhea(int numGlyphs)
        {
            var m = new MemoryStream();
            WriteU32(m, 0x00010000);
            WriteI16(m, Ascender); WriteI16(m, Descender); WriteI16(m, 0);
            WriteU16(m, UnitsPerEm);                  // advanceWidthMax
            WriteI16(m, 0); WriteI16(m, 0); WriteI16(m, UnitsPerEm);
            WriteI16(m, 1); WriteI16(m, 0); WriteI16(m, 0);
            for (int i = 0; i < 4; i++) WriteI16(m, 0);
            WriteI16(m, 0);                           // metricDataFormat
            WriteU16(m, numGlyphs);                   // numberOfHMetrics
            return m.ToArray();
        }

        private static byte[] BuildHmtx(int numGlyphs)
        {
            var m = new MemoryStream();
            for (int i = 0; i < numGlyphs; i++) { WriteU16(m, 1200); WriteI16(m, 0); }
            return m.ToArray();
        }

        private static byte[] BuildMaxp(int numGlyphs)
        {
            var m = new MemoryStream();
            WriteU32(m, 0x00010000);
            WriteU16(m, numGlyphs);
            WriteU16(m, 8);      // maxPoints
            WriteU16(m, 2);      // maxContours
            WriteU16(m, 0); WriteU16(m, 0);
            WriteU16(m, 2);      // maxZones
            WriteU16(m, 16);     // maxTwilightPoints
            WriteU16(m, 16);     // maxStorage
            WriteU16(m, 16);     // maxFunctionDefs
            WriteU16(m, 0);      // maxInstructionDefs
            WriteU16(m, 64);     // maxStackElements
            WriteU16(m, 64);     // maxSizeOfInstructions
            WriteU16(m, 0); WriteU16(m, 0);
            return m.ToArray();
        }

        private static byte[] BuildOs2()
        {
            var m = new MemoryStream();
            WriteU16(m, 1);                       // version
            WriteI16(m, 1200);                    // xAvgCharWidth
            WriteU16(m, 400);                     // usWeightClass
            WriteU16(m, 5);                       // usWidthClass
            WriteU16(m, 0);                       // fsType
            // FOUR fields each, not five: XSize, YSize, XOffset, YOffset. Five made OS/2 90
            // bytes where version 1 is 86, and GDI refused the font outright.
            for (int i = 0; i < 4; i++) WriteI16(m, 0);          // subscript
            for (int i = 0; i < 4; i++) WriteI16(m, 0);          // superscript
            WriteI16(m, 100); WriteI16(m, 800);                  // strikeout
            WriteI16(m, 0);                                      // sFamilyClass
            for (int i = 0; i < 10; i++) m.WriteByte(0);         // panose
            for (int i = 0; i < 4; i++) WriteU32(m, 0);          // unicode ranges
            for (int i = 0; i < 4; i++) m.WriteByte((byte)'X');  // achVendID
            WriteU16(m, 0x0040);                                 // fsSelection: regular
            WriteU16(m, 0x20); WriteU16(m, 0xFFFF);              // first/last char
            WriteI16(m, Ascender); WriteI16(m, Descender); WriteI16(m, 0);
            WriteU16(m, Ascender); WriteU16(m, (ushort)(-Descender));
            WriteU32(m, 0); WriteU32(m, 0);                      // code page ranges
            return m.ToArray();
        }

        private static byte[] BuildPost()
        {
            var m = new MemoryStream();
            WriteU32(m, 0x00030000);
            WriteU32(m, 0);
            WriteI16(m, 0); WriteI16(m, 0);
            WriteU32(m, 0);
            for (int i = 0; i < 4; i++) WriteU32(m, 0);
            return m.ToArray();
        }

        /// <summary>Format 4, one contiguous run from 0x41 upward.</summary>
        private static byte[] BuildCmap(int numGlyphs)
        {
            int last = 0x41 + numGlyphs - 2;
            var sub = new MemoryStream();
            WriteU16(sub, 4);
            WriteU16(sub, 32);                      // length, patched below
            WriteU16(sub, 0);
            WriteU16(sub, 4);                       // segCountX2 = 2 segments
            WriteU16(sub, 4); WriteU16(sub, 1); WriteU16(sub, 0);
            WriteU16(sub, last); WriteU16(sub, 0xFFFF);            // endCode
            WriteU16(sub, 0);                                      // reservedPad
            WriteU16(sub, 0x41); WriteU16(sub, 0xFFFF);            // startCode
            // glyph = code + idDelta, so 0x41 -> 1 needs 1 - 0x41, not 0x41 - 1.
            WriteU16(sub, (1 - 0x41) & 0xFFFF); WriteU16(sub, 1);  // idDelta
            WriteU16(sub, 0); WriteU16(sub, 0);                    // idRangeOffset
            byte[] subData = sub.ToArray();
            subData[2] = (byte)(subData.Length >> 8); subData[3] = (byte)subData.Length;

            var m = new MemoryStream();
            WriteU16(m, 0); WriteU16(m, 1);
            WriteU16(m, 3); WriteU16(m, 1); WriteU32(m, 12);
            m.Write(subData, 0, subData.Length);
            return m.ToArray();
        }

        private static byte[] BuildName(string family)
        {
            var strings = new MemoryStream();
            var records = new List<(int id, int off, int len)>();
            // ALL SIX, and 3 and 5 are the ones that matter. With only {1,2,4,6} -- family,
            // subfamily, full name, PostScript name -- GDI refuses the font from both
            // AddFontMemResourceEx and AddFontResourceExW, with a null handle and no error
            // code, while GDI+ loads it happily and reports the family. Adding 3 (unique id)
            // and 5 (version) is what makes GDI take it; Mac platform records are not needed.
            // Bisected by swapping tables against Arial's one at a time.
            foreach (int id in new[] { 1, 2, 3, 4, 5, 6 })
            {
                string v = id == 2 ? "Regular"
                         : id == 3 ? family + ";probe"
                         : id == 5 ? "Version 1.000"
                         : family;
                int off = (int)strings.Length;
                foreach (char c in v) { strings.WriteByte((byte)(c >> 8)); strings.WriteByte((byte)c); }
                records.Add((id, off, v.Length * 2));
            }
            byte[] sd = strings.ToArray();

            var m = new MemoryStream();
            WriteU16(m, 0);
            WriteU16(m, records.Count);
            WriteU16(m, 6 + records.Count * 12);
            foreach ((int id, int off, int len) in records)
            {
                WriteU16(m, 3); WriteU16(m, 1); WriteU16(m, 0x0409); WriteU16(m, id);
                WriteU16(m, len); WriteU16(m, off);
            }
            m.Write(sd, 0, sd.Length);
            return m.ToArray();
        }

        private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
        {
            int n = tables.Count;
            int headerLen = 12 + n * 16;
            int offset = headerLen;
            var offsets = new Dictionary<string, int>();
            foreach (KeyValuePair<string, byte[]> t in tables)
            {
                offsets[t.Key] = offset;
                offset += (t.Value.Length + 3) & ~3;
            }

            var m = new MemoryStream();
            WriteU32(m, 0x00010000);
            int searchRange = 16, entrySelector = 0;
            while (searchRange * 2 <= n * 16) { searchRange *= 2; entrySelector++; }
            WriteU16(m, n); WriteU16(m, searchRange); WriteU16(m, entrySelector);
            WriteU16(m, n * 16 - searchRange);
            foreach (KeyValuePair<string, byte[]> t in tables)
            {
                foreach (char c in t.Key) m.WriteByte((byte)c);
                WriteU32(m, CheckSum(t.Value));
                WriteU32(m, (uint)offsets[t.Key]);
                WriteU32(m, (uint)t.Value.Length);
            }
            foreach (KeyValuePair<string, byte[]> t in tables)
            {
                m.Write(t.Value, 0, t.Value.Length);
                while (m.Length % 4 != 0) m.WriteByte(0);
            }

            byte[] font = m.ToArray();
            // head.checkSumAdjustment = 0xB1B0AFBA - checksum(whole font)
            uint total = CheckSum(font);
            uint adj = unchecked(0xB1B0AFBAu - total);
            int headOff = offsets["head"] + 8;
            font[headOff] = (byte)(adj >> 24); font[headOff + 1] = (byte)(adj >> 16);
            font[headOff + 2] = (byte)(adj >> 8); font[headOff + 3] = (byte)adj;
            return font;
        }

        private static uint CheckSum(byte[] data)
        {
            uint sum = 0;
            for (int i = 0; i + 3 < data.Length; i += 4)
                sum = unchecked(sum + (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]));
            int rem = data.Length & ~3;
            if (rem < data.Length)
            {
                uint tail = 0;
                for (int i = 0; i < 4; i++) tail = (tail << 8) | (uint)(rem + i < data.Length ? data[rem + i] : 0);
                sum = unchecked(sum + tail);
            }
            return sum;
        }

        private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void WriteI16(Stream s, int v) => WriteU16(s, v & 0xFFFF);
        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }
    }
}
