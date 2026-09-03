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

            /// <summary>No glyph program at all: the bar renders at whatever width its outline
            /// has. The control for whether GDI force-rounds a stem or only rounds one a MIRP
            /// asked it to.</summary>
            public readonly bool NoProgram;

            /// <summary>Ask GDI what it answers GETINFO, by making the ANSWER visible as ink.
            /// <para>Nothing else can: GETINFO's result never reaches an API, it only steers the
            /// font's own program. So the glyph shifts itself right by the answer -- version
            /// pixels for selector 1, ten pixels for a bit that comes back set -- and the
            /// rendered position reads it back. 0 means an ordinary bar.</para></summary>
            public readonly int Probe;

            /// <summary>The left side bearing to DECLARE, which need not be the outline's own left
            /// edge.
            /// <para>It matters for any probe that sweeps the bar's position. hmtx writes the
            /// bearing as the bar's own Left by default, so moving the outline moves the declared
            /// bearing with it and the sweep varies TWO things at once -- which is enough to make
            /// the measured output advance faster than the input and no quantiser fit. Pin this to
            /// hold the bearing still and vary only the outline's phase.</para></summary>
            public readonly int Lsb;

            /// <summary>Shear the bar: how far right its TOP edge sits from its bottom, in
            /// font units. Zero is an upright bar.
            /// <para>Every coverage check this suite has ever run used upright bars, so it
            /// only ever tested VERTICAL edges. A third of the remaining disagreement with GDI
            /// sits on diagonals, and a diagonal edge is the one place where the decision to
            /// take a single vertical sample per row -- right for a horizontal edge, because
            /// GDI has no vertical antialiasing -- has never been checked against GDI at
            /// all.</para></summary>
            public readonly int Slant;

            public Bar(int cvt, int left, int right, bool round, bool minDistance,
                       bool noProgram = false, int probe = 0, int lsb = int.MinValue,
                       int slant = 0)
            {
                Cvt = cvt; Left = left; Right = right; Round = round; MinDistance = minDistance;
                NoProgram = noProgram; Probe = probe; Slant = slant;
                Lsb = lsb == int.MinValue ? left : lsb;
            }
        }

        /// <summary>Build a font whose glyph i+1 is bars[i], mapped to codepoint 0x41 + i.</summary>
        /// <summary>Build a font whose glyph i+1 is bars[i], mapped to codepoint 0x41 + i.
        /// <para><paramref name="prepSelectors"/> asks the same GETINFO questions from the
        /// PRE-PROGRAM instead of from a glyph, which is a different question and turned out to
        /// matter: a face computes its rendering-mode variable in 'prep', so what GDI answers
        /// THERE is what decides which hinting program every glyph then runs. Each selector's
        /// exact-bit answer is written to cvt[100+i] as ten pixels or none, and a bar whose probe
        /// is -10000-i shifts itself by that control value.</para></summary>
        public static byte[] Build(string family, IReadOnlyList<Bar> bars,
                                   IReadOnlyList<int>? prepSelectors = null)
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
                WriteBar(glyf, b, cvtIndex, instructions: !b.NoProgram);
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
                ["hmtx"] = BuildHmtx(numGlyphs, bars),
                ["loca"] = locaData.ToArray(),
                ["maxp"] = BuildMaxp(numGlyphs),
                ["name"] = BuildName(family),
                ["post"] = BuildPost(),
                ["prep"] = BuildPrep(prepSelectors),
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
            byte[] program = b.Probe != 0 ? ProbeProgram(b.Probe) : new byte[]
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
            // A PROBE DECLARES A WIDE BOX, because it shifts itself a long way and GDI rasterizes
            // only inside the glyph's own bounding box -- a 42-pixel shift off a 300-unit box came
            // back as no ink at all, which reads exactly like 'the program did nothing'.
            WriteI16(s, b.Probe != 0 ? b.Right + 8000 : b.Right + (b.Slant > 0 ? b.Slant : 0));
            WriteI16(s, Top);                            // xMax yMax
            WriteU16(s, 3);                              // endPtsOfContours[0]
            WriteU16(s, instructions ? program.Length : 0);
            if (instructions) s.Write(program, 0, program.Length);

            // Four points, all on-curve, x and y as signed 16-bit deltas.
            for (int i = 0; i < 4; i++) s.WriteByte(0x01);        // ON_CURVE, 16-bit deltas
            // Bottom-left, bottom-right, top-right, top-left. The slant displaces the two TOP
            // points, so the bar keeps its horizontal width and leans.
            WriteI16(s, b.Left); WriteI16(s, b.Right - b.Left);
            WriteI16(s, b.Slant); WriteI16(s, b.Left - b.Right);
            WriteI16(s, Bottom); WriteI16(s, 0); WriteI16(s, Top - Bottom); WriteI16(s, 0);
        }

        /// <summary>A glyph program that renders GETINFO's answer as a horizontal displacement.
        /// <para>SLOOP 4 then SHPIX moves the whole bar, so the shape is undistorted and its left
        /// edge carries the number. Selector 1 is the rasterizer VERSION, which is an integer, so
        /// it is multiplied into pixels (MUL is F26Dot6, and 4096 = 64*64, so v -> v*64 = v px).
        /// Every other selector answers with a single high BIT, which would be an absurd shift, so
        /// it is tested against zero and turned into a flat ten pixels.</para></summary>
        private static byte[] ProbeProgram(int selector)
        {
            var p = new List<byte>
            {
                0x01,                          // SVTCA[x]
                0xB0, 0x04, 0x17,              // PUSHB[1] 4 ; SLOOP  -- shift all four points
                0xB3, 0x00, 0x01, 0x02, 0x03,  // PUSHB[4] 0 1 2 3
            };
            // A NEGATIVE selector asks the question the FONT asks: not "is the answer non-zero"
            // but "is it EXACTLY this bit". Segoe UI's fpgm compares GETINFO(2048) against 262144
            // and only then takes its symmetric branch, so a probe that settles for non-zero can
            // report a bit the face would reject. The value is built the way the face builds it,
            // 16384 * (bit/256) through MUL, because it does not fit a PUSHW.
            if (selector <= -10000)
            {
                // Shift by what the PRE-PROGRAM decided, read back out of the control value.
                p.Add(0xB0); p.Add((byte) (100 + (-10000 - selector)));   // PUSHB[1] cvt index
                p.Add(0x45);                                              // RCVT
                p.Add(0x38);                                              // SHPIX
                return p.ToArray();
            }
            bool exact = selector < 0;
            if (exact)
            {
                selector = -selector;
                int bit = 7; for (int t = selector; t > 1; t >>= 1) bit++;   // 32 -> 12, 2048 -> 18
                int val = 1 << bit;
                p.Add(0xB8); p.Add(0x40); p.Add(0x00);                       // PUSHW[1] 16384
                p.Add(0xB8); p.Add((byte) ((val / 256) >> 8)); p.Add((byte) (val / 256));
                p.Add(0x63);                                                 // MUL -> the bit
            }
            if (selector <= 255) { p.Add(0xB0); p.Add((byte) selector); }
            else { p.Add(0xB8); p.Add((byte) (selector >> 8)); p.Add((byte) selector); }
            p.Add(0x88);                                            // GETINFO
            if (selector == 1)
            {
                // MINUS 32 FIRST. Shifting by the version itself put the bar 42 pixels out and
                // GDI drew nothing at all -- a shift that large leaves whatever region it
                // rasterizes, and 'no ink' reads exactly like 'the program did nothing', which is
                // the one answer this probe must never fake. Every version this gate cares about
                // is between 35 and 64, so the row reports version - 32 and stays in the range a
                // ten-pixel bit shift has already been shown to render.
                p.Add(0xB0); p.Add(0x20);                           // PUSHB[1] 32
                p.Add(0x61);                                        // SUB  -> version - 32
                p.Add(0xB8); p.Add(0x10); p.Add(0x00);              // PUSHW[1] 4096
                p.Add(0x63);                                        // MUL  -> (version-32) * 64
            }
            else
            {
                if (exact)
                {
                    p.Add(0x54);                                    // EQ against the bit above
                }
                else
                {
                    p.Add(0xB0); p.Add(0x00);                       // PUSHB[1] 0
                    p.Add(0x55);                                    // NEQ
                }
                p.Add(0x58);                                        // IF
                p.Add(0xB8); p.Add(0x02); p.Add(0x80);              // PUSHW[1] 640 -- ten pixels
                p.Add(0x1B);                                        // ELSE
                p.Add(0xB0); p.Add(0x00);                           // PUSHB[1] 0
                p.Add(0x59);                                        // EIF
            }
            p.Add(0x38);                                            // SHPIX
            return p.ToArray();
        }

        private static byte[] BuildCvt(List<short> cvts)
        {
            var m = new MemoryStream();
            foreach (short v in cvts) WriteI16(m, v);
            // Padded so the prep probe's cvt[100..] slots exist to be written.
            for (int i = cvts.Count; i < 128; i++) WriteI16(m, 0);
            if (cvts.Count == 0 && 128 == 0) WriteI16(m, 0);
            return m.ToArray();
        }

        /// <summary>Only what the interpreter needs to be in a defined state: scan control off, and
        /// a cut-in wide enough that it never fires. A prep that decides things would be a prep
        /// whose decisions we would then be measuring.</summary>
        private static byte[] BuildPrep(IReadOnlyList<int>? prepSelectors = null)
        {
            var extra = new List<byte>();
            if (prepSelectors is not null)
                for (int i = 0; i < prepSelectors.Count; i++)
                {
                    int sel = prepSelectors[i];
                    int bit = 7; for (int t = sel; t > 1; t >>= 1) bit++;
                    int val = 1 << bit;
                    extra.Add(0xB0); extra.Add((byte) (100 + i));          // PUSHB[1] cvt index
                    extra.Add(0xB8); extra.Add(0x40); extra.Add(0x00);      // PUSHW 16384
                    extra.Add(0xB8); extra.Add((byte) ((val / 256) >> 8)); extra.Add((byte) (val / 256));
                    extra.Add(0x63);                                        // MUL -> the bit
                    if (sel <= 255) { extra.Add(0xB0); extra.Add((byte) sel); }
                    else { extra.Add(0xB8); extra.Add((byte) (sel >> 8)); extra.Add((byte) sel); }
                    extra.Add(0x88);                                        // GETINFO
                    extra.Add(0x54);                                        // EQ
                    extra.Add(0x58);                                        // IF
                    extra.Add(0xB8); extra.Add(0x02); extra.Add(0x80);      // PUSHW 640 (ten px)
                    extra.Add(0x1B);                                        // ELSE
                    extra.Add(0xB0); extra.Add(0x00);                       // PUSHB 0
                    extra.Add(0x59);                                        // EIF
                    extra.Add(0x44);                                        // WCVTP
                }
            var head = new List<byte>
            {
                0xB0, 0x00, 0x85,        // PUSHB[1] 0, SCANCTRL  -- no dropout control
                0xB0, 0x00, 0x8D,        // PUSHB[1] 0, SCANTYPE
                0xB0, 0x40, 0x1D,        // PUSHB[1] 64, SCVTCI   -- cut-in one pixel
                // No INSTCTRL: it pops TWO values and pushing one underflowed our own
                // interpreter's stack. A probe that faults the thing it is probing is no probe.
            };
            head.AddRange(extra);
            return head.ToArray();
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

        /// <summary>Advance and LEFT SIDE BEARING per glyph.
        /// <para>The bearing has to be the glyph's own xMin. TrueType requires them equal, and a
        /// font that breaks it does not fail -- it renders, differently on each side. This wrote a
        /// flat 0 while every bar's contour starts at Left, and GDI honoured the declared 0 by
        /// bringing the ink back to the pen while we drew the contour where it says it is. The
        /// result was a clean 400-unit offset that scales with ppem, 4 lamps at 8ppem up to 8 at
        /// 14, which read exactly like a placement bug in the renderer and was a lie in the
        /// fixture. A probe has to be right about the thing it is not testing.</para></summary>
        private static byte[] BuildHmtx(int numGlyphs, IReadOnlyList<Bar> bars)
        {
            var m = new MemoryStream();
            for (int i = 0; i < numGlyphs; i++)
            {
                WriteU16(m, 1200);
                WriteI16(m, i > 0 && i - 1 < bars.Count ? bars[i - 1].Lsb : 0);
            }
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
