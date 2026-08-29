// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WHERE our text stops agreeing with GDI, stage by stage.
//
// Every measurement before this one compared the FINISHED pixels -- outline, fitting, rasterizing,
// filtering and the contrast curve, all folded into one number. When that number will not go down,
// there is no way to tell which of the five is responsible, and the record shows the cost: gamma,
// filter width, quantization, x-hinting and stem width were each tried and rejected on a number that
// could not have told them apart anyway.
//
// GDI will hand over its intermediate results, which is what makes the decomposition possible:
//
//   Stage A  UNHINTED OUTLINE, rasterized to grey.  GetGlyphOutline(GGO_GRAY8_BITMAP|GGO_UNHINTED)
//            against our own scaled outline through our own rasterizer. Everything here is font
//            parsing, scaling and area coverage. Hinting is not in it, and neither is ClearType.
//
//   Stage B  HINTED OUTLINE, rasterized to grey.    GetGlyphOutline(GGO_GRAY8_BITMAP)
//            against our hinted outline through the same rasterizer. The DIFFERENCE between B and A
//            is the interpreter and nothing else.
//
//   Stage C  ClearType, which the parity suite already measures end to end.
//
// A caveat that has to be stated or stage B misleads: GGO reports GDI's DEFAULT hinting, which fits
// x as well as y, while ClearType renders from a y-only fitting. So B says "does our interpreter run
// this program the way GDI's does", which is worth knowing on its own -- it is how the vectors bug
// was found -- but it is NOT the fitting that ends up on screen under ClearType.
//
// Reported only. Set WPF_STAGE_REPORT to a file.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class GdiStageTests
    {
        private const int Cell = 72;        // room for the tallest glyph at the largest size measured
        private const int PenX = 20;
        private const int BaseY = 52;

        // ---- GDI ----------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONTW
        {
            public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
            public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet;
            public byte lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential)] private struct FIXED { public short fract, value; }
        [StructLayout(LayoutKind.Sequential)] private struct MAT2 { public FIXED eM11, eM12, eM21, eM22; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GLYPHMETRICS
        {
            public uint gmBlackBoxX, gmBlackBoxY;
            public int gmptGlyphOriginX, gmptGlyphOriginY;
            public short gmCellIncX, gmCellIncY;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontIndirectW(ref LOGFONTW lf);
        [DllImport("gdi32.dll")]
        private static extern uint GetGlyphOutlineW(IntPtr hdc, uint ch, uint format, out GLYPHMETRICS gm,
                                                    uint cbBuffer, byte[] buffer, ref MAT2 mat2);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int c,
                                                    [Out] ushort[] indices, uint flags);

        private const uint GgoGray8 = 6, GgoNative = 2, GgoGlyphIndex = 0x0080, GgoUnhinted = 0x0100;
        private const ushort TtPrimLine = 1, TtPrimQSpline = 2, TtPrimCSpline = 3;

        /// <summary>Every glyph GDI declined, so an empty stage is not read as agreement.</summary>
        private static readonly List<string> s_refusals = new();
        private const byte ClearTypeQuality = 5;

        /// <summary>GDI's own greyscale coverage for one glyph, laid into the shared cell. GGO_GRAY8
        /// counts 0..64, so it is stretched to 0..255 to sit beside ours.</summary>
        private static byte[] GdiGray(char c, string family, int ppem, bool unhinted)
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var lf = new LOGFONTW
            {
                lfHeight = -ppem, lfWeight = 400, lfCharSet = 1,
                lfQuality = ClearTypeQuality, lfFaceName = family,
            };
            IntPtr font = CreateFontIndirectW(ref lf);
            IntPtr old = SelectObject(dc, font);
            try
            {
                    var idx = new ushort[1];
                    GetGlyphIndicesW(dc, c.ToString(), 1, idx, 0);

                    var mat = new MAT2 { eM11 = new FIXED { value = 1 }, eM22 = new FIXED { value = 1 } };
                    uint format = GgoGray8 | GgoGlyphIndex | (unhinted ? GgoUnhinted : 0);

                    uint size = GetGlyphOutlineW(dc, idx[0], format, out GLYPHMETRICS gm, 0, null, ref mat);
                    var cell = new byte[Cell * Cell];
                    if (size == 0xFFFFFFFF)
                    {
                        lock (s_refusals) s_refusals.Add($"{family} '{c}' @{ppem} unhinted={unhinted}: GDI_ERROR");
                        return cell;
                    }
                    if (size == 0) return cell;                            // a blank glyph

                    var bits = new byte[size];
                    GetGlyphOutlineW(dc, idx[0], format, out gm, size, bits, ref mat);

                    int stride = ((int)gm.gmBlackBoxX + 3) / 4 * 4;
                    for (int row = 0; row < gm.gmBlackBoxY; row++)
                        for (int col = 0; col < gm.gmBlackBoxX; col++)
                        {
                            int x = PenX + gm.gmptGlyphOriginX + col;
                            int y = BaseY - gm.gmptGlyphOriginY + row;
                            if ((uint)x >= Cell || (uint)y >= Cell) continue;
                            int v = bits[row * stride + col];
                            cell[y * Cell + x] = (byte)Math.Min(255, v * 255 / 64);
                        }
                    return cell;
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(font);
                DeleteDC(dc);
            }
        }

        /// <summary>GDI's OUTLINE for one glyph, as PathFigures in our own space.
        /// <para>GGO_UNHINTED is only honoured for the OUTLINE formats -- asked for alongside
        /// GGO_GRAY8_BITMAP it returns a zero-length bitmap and no error, which reads as "the glyph is
        /// blank". So the unhinted stage compares GEOMETRY, rasterized by us on both sides, which is
        /// the cleaner comparison anyway: the rasterizer cancels out and what is left is the outline
        /// and its scaling.</para>
        /// <para>GGO gives y UP from the baseline; ours runs down, hence the negation.</para></summary>
        /// <summary>GDI's outline, optionally through a MAT2 that stretches x.
        /// <para>The one door left into GDI's CLEARTYPE geometry. GetGlyphOutline hints AFTER the
        /// transform, so asking for the glyph three times as wide asks for it fitted on the LAMP
        /// grid -- the space ClearType rasterizes in. Divide x back by three and the result is, if
        /// the theory holds, the outline GDI's ClearType actually draws, which no plain GGO call
        /// reports and whose absence is what the whole decomposition has been stuck behind.</para>
        /// </summary>
        private static List<PathFigure> GdiOutline(char c, string family, int ppem, bool unhinted,
                                                   int xScale = 1)
        {
            var figures = new List<PathFigure>();
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var lf = new LOGFONTW
            {
                lfHeight = -ppem, lfWeight = 400, lfCharSet = 1,
                lfQuality = ClearTypeQuality, lfFaceName = family,
            };
            IntPtr font = CreateFontIndirectW(ref lf);
            IntPtr previous = SelectObject(dc, font);
            try
            {
                var idx = new ushort[1];
                GetGlyphIndicesW(dc, c.ToString(), 1, idx, 0);
                var mat = new MAT2 { eM11 = new FIXED { value = (short) xScale },
                                     eM22 = new FIXED { value = 1 } };
                s_xDiv = xScale;
                uint format = GgoNative | GgoGlyphIndex | (unhinted ? GgoUnhinted : 0);

                uint size = GetGlyphOutlineW(dc, idx[0], format, out GLYPHMETRICS _, 0, null, ref mat);
                if (size == 0 || size == 0xFFFFFFFF) return figures;

                var buf = new byte[size];
                GetGlyphOutlineW(dc, idx[0], format, out GLYPHMETRICS _, size, buf, ref mat);

                int at = 0;
                while (at + 16 <= buf.Length)
                {
                    int cb = BitConverter.ToInt32(buf, at);
                    if (cb <= 0 || at + cb > buf.Length) break;
                    int end = at + cb;
                    var fig = new PathFigure(Fx(buf, at + 8)) { Closed = true };
                    int q = at + 16;

                    while (q + 4 <= end)
                    {
                        ushort type = BitConverter.ToUInt16(buf, q);
                        int n = BitConverter.ToUInt16(buf, q + 2);
                        int pts = q + 4;

                        if (type == TtPrimLine)
                        {
                            for (int i = 0; i < n; i++)
                                fig.Segments.Add(new LineSegment(Fx(buf, pts + i * 8)));
                        }
                        else if (type == TtPrimQSpline)
                        {
                            // Every point but the last is a control point, and between two consecutive
                            // controls the curve passes through their MIDPOINT -- TrueType's own
                            // convention, which GGO does not spell out.
                            for (int i = 0; i < n - 1; i++)
                            {
                                Vector2 ctrl = Fx(buf, pts + i * 8);
                                Vector2 next = Fx(buf, pts + (i + 1) * 8);
                                Vector2 on = i == n - 2
                                    ? next
                                    : new Vector2((ctrl.X + next.X) / 2f, (ctrl.Y + next.Y) / 2f);
                                fig.Segments.Add(new QuadraticBezierSegment(ctrl, on));
                            }
                        }
                        else if (type == TtPrimCSpline)
                        {
                            for (int i = 0; i + 2 < n; i += 3)
                                fig.Segments.Add(new CubicBezierSegment(Fx(buf, pts + i * 8),
                                                                       Fx(buf, pts + (i + 1) * 8),
                                                                       Fx(buf, pts + (i + 2) * 8)));
                        }
                        else break;

                        q = pts + n * 8;
                    }

                    figures.Add(fig);
                    at = end;
                }
                return figures;
            }
            finally
            {
                s_xDiv = 1f;
                SelectObject(dc, previous);
                DeleteObject(font);
                DeleteDC(dc);
            }
        }

        private static long Ink(byte[] cell)
        {
            long n = 0;
            foreach (byte b in cell) n += b;
            return n;
        }

        private static string Box(List<PathFigure> figures)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            int pts = 0;
            void Take(Vector2 v)
            {
                x0 = MathF.Min(x0, v.X); y0 = MathF.Min(y0, v.Y);
                x1 = MathF.Max(x1, v.X); y1 = MathF.Max(y1, v.Y);
                pts++;
            }
            foreach (PathFigure f in figures)
            {
                Take(f.Start);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: Take(l.Point); break;
                        case QuadraticBezierSegment q: Take(q.Control); Take(q.Point); break;
                        case CubicBezierSegment c: Take(c.Control1); Take(c.Control2); Take(c.Point); break;
                    }
            }
            return pts == 0 ? "(empty)" : $"({x0:0.##},{y0:0.##})-({x1:0.##},{y1:0.##}) pts={pts}";
        }

        /// <summary>One POINTFX: 16.16 fixed, y up, turned into our y-down space.</summary>
        /// <summary>STAGE D: what geometry does GDI's CLEARTYPE actually draw?
        /// <para>The decomposition has been stuck on this. Stage A proves our unhinted outline is
        /// GDI's; stage B proves our fitted outline is GDI's to 0.994-1.007 of its ink. But shipping
        /// that fitted outline makes the finished ClearType pixels much WORSE than shipping a y-only
        /// fit, so GDI's ClearType cannot be drawing the outline GetGlyphOutline reports -- and no
        /// API reports the other one.</para>
        /// <para>Except, perhaps, this one. GGO hints AFTER applying its MAT2, so asking for the
        /// glyph stretched three times in x asks for it fitted on the LAMP grid, which is the space
        /// ClearType rasterizes in. Divide x back by three and compare against what we ship (y-only)
        /// and against a full fit. Whichever it lands on is the answer; if it lands on neither, that
        /// difference IS the missing geometry, in a form we can finally look at.</para>
        /// <para>Reported only: set WPF_STAGE_REPORT.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Consolas")]
        [InlineData("Arial")]
        [InlineData("Times New Roman")]
        [InlineData("Tahoma")]
        [InlineData("Verdana")]
        [InlineData("Courier New")]
        [InlineData("Lucida Console")]
        [InlineData("Georgia")]
        public void StageD_WhatGeometryDoesClearTypeDraw(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STAGE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STAGE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            const string Repertoire =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}  (stage D: GDI's 3x-fitted outline against ours)");
            report.AppendLine("ppem   gdi1x     gdi3x    ourY     ourXY  |  d(3x,1x) d(3x,ourY) d(1x,ourY) d(3x,ourXY)");

            bool saved = TrueTypeFont.SubpixelFitting;
            try
            {
                foreach (int ppem in new[] { 11, 12, 13, 16, 19 })
                {
                    long g1 = 0, g3 = 0, oy = 0, oxy = 0;
                    int d31 = 0, d3y = 0, d3xy = 0, d1y = 0;
                    // Is what is left a systematic sideways offset? Slide our fit along the lamp
                    // grid and score each position. A minimum away from zero would say we are
                    // drawing the right shape in the wrong place, which is a different repair from
                    // drawing the wrong shape.
                    var slide = new int[5];
                    foreach (char c in Repertoire)
                    {
                        byte[] gdi1 = RasterizeIntoCell(GdiOutline(c, family, ppem, false));
                        byte[] gdi3 = RasterizeIntoCell(GdiOutline(c, family, ppem, false, xScale: 3));
                        TrueTypeFont.SubpixelFitting = true;
                        byte[] ourY = OurGray(font, c, ppem, unhinted: false);
                        TrueTypeFont.SubpixelFitting = false;
                        byte[] ourXY = OurGray(font, c, ppem, unhinted: false);

                        var (p1, _, i3, i1) = Compare(gdi3, gdi1);
                        var (py, _, _, iy) = Compare(gdi3, ourY);
                        var (pxy, _, _, ixy) = Compare(gdi3, ourXY);
                        var (p1y, _, _, _) = Compare(gdi1, ourY);
                        // The slide is about the fit we SHIP, so put the mode back before taking it
                        // (ourXY above leaves it off, and the first run of this measured the full
                        // fit by accident -- slide[0] came back exactly equal to d(3x,ourXY), which
                        // is the tell).
                        TrueTypeFont.SubpixelFitting = true;
                        for (int k = 0; k < 5; k++)
                        {
                            float dx = (k - 2) / 3f;
                            byte[] slid = RasterizeIntoCell(
                                Translate(HintedFigures(font, c, ppem), dx, 0f));
                            slide[k] += Compare(gdi3, slid).Pixels;
                        }
                        g1 += i1; g3 += i3; oy += iy; oxy += ixy;
                        d31 += p1; d3y += py; d3xy += pxy; d1y += p1y;
                    }
                    report.AppendLine($"{ppem,4} {g1,9} {g3,9} {oy,8} {oxy,8}  | "
                                      + $"{d31,8} {d3y,10} {d1y,10} {d3xy,11}"
                                      + $"  slide[-2/3..+2/3]={string.Join("/", slide)}");
                }
            }
            finally { TrueTypeFont.SubpixelFitting = saved; }

            lock (s_reportLock) File.AppendAllText(path!, report.ToString());
        }

        private static readonly object s_reportLock = new object();

        /// <summary>Divides x back down after a stretched MAT2 request; 1 for an ordinary one.</summary>
        private static float s_xDiv = 1f;

        private static Vector2 Fx(byte[] b, int at)
        {
            float x = (BitConverter.ToInt16(b, at + 2) + BitConverter.ToUInt16(b, at) / 65536f) / s_xDiv;
            float y = BitConverter.ToInt16(b, at + 6) + BitConverter.ToUInt16(b, at + 4) / 65536f;
            return new Vector2(x, -y);
        }

        /// <summary>Rasterize figures into the shared cell, so both sides go through the SAME
        /// rasterizer and only their geometry can differ.</summary>
        private static byte[] RasterizeIntoCell(List<PathFigure> figures)
        {
            var cell = new byte[Cell * Cell];
            if (figures.Count == 0) return cell;
            var path = new PathGeometry(FillRule.NonZero, Translate(figures, PenX, BaseY));
            CoverageMask mask = PathRasterizer.Rasterize(path);
            if (mask.Coverage == null) return cell;
            for (int row = 0; row < mask.Height; row++)
                for (int col = 0; col < mask.Width; col++)
                {
                    int x = (int) mask.OriginX + col;
                    int y = (int) mask.OriginY + row;
                    if ((uint) x >= Cell || (uint) y >= Cell) continue;
                    cell[y * Cell + x] = mask.Coverage[row * mask.Width + col];
                }
            return cell;
        }

        // ---- ours ---------------------------------------------------------------------------------

        /// <summary>Our coverage for the same glyph in the same cell, through our own rasterizer.
        /// Unhinted takes the outline as the face draws it and scales it; hinted asks the face's
        /// program, which is the only difference between the two stages.</summary>
        /// <summary>One glyph, our fit beside GDI's LAMP-GRID fit, as character maps.
        /// <para>Stage D says the two differ on about a quarter of the inked pixels and that it is
        /// not an offset -- sliding ours along the lamp grid doubles the disagreement either way. So
        /// it is shape, and this is where the shape can be looked at.</para>
        /// <para>WPF_STAGE_GLYPH3=family/char/ppem, e.g. "Segoe UI/m/12".</para></summary>
        [Fact]
        public void OneGlyphAgainstClearTypeGeometry()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_STAGE_GLYPH3");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_STAGE_GLYPH3=family/char/ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string[] parts = spec!.Split('/');
            string? file = FontFiles.Find(parts[0], bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            char c = parts[1][0];
            int ppem = int.Parse(parts[2]);
            bool saved = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = true;
            byte[] mine, theirs;
            try
            {
                mine = RasterizeIntoCell(HintedFigures(font, c, ppem));
                theirs = RasterizeIntoCell(GdiOutline(c, parts[0], ppem, false, xScale: 3));
            }
            finally { TrueTypeFont.SubpixelFitting = saved; }

            Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}   ours (y-only fit) | GDI (lamp grid) ===");
            const string Ramp = " .:-=+*#%@";
            for (int y = 0; y < Cell; y++)
            {
                var a = new System.Text.StringBuilder();
                var b = new System.Text.StringBuilder();
                bool any = false;
                for (int x = 0; x < Cell; x++)
                {
                    int u = mine[y * Cell + x], v = theirs[y * Cell + x];
                    if (u > 0 || v > 0) any = true;
                    a.Append(Ramp[Math.Min(9, u * 10 / 256)]);
                    b.Append(Ramp[Math.Min(9, v * 10 / 256)]);
                }
                if (any)
                    Console.Error.WriteLine($"{y,3} |{a.ToString().TrimEnd()}| |{b.ToString().TrimEnd()}|");
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool GetCharWidthI(IntPtr hdc, uint first, uint count, ushort[]? gi,
                                                 int[] widths);

        /// <summary>ADVANCES, ours against GDI's, per glyph and size.
        /// <para>Everything measured so far compares a glyph drawn at a known origin. A run is not
        /// that: each glyph starts where the previous one's ADVANCE puts it, so one glyph a pixel
        /// wide of Windows displaces every glyph after it, and that shows up in the parity numbers
        /// as text that disagrees without any glyph being the wrong shape. Nothing has ever checked
        /// them.</para>
        /// <para>Reported only: set WPF_ADVANCE_REPORT.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        [InlineData("Consolas")]
        public void Advances_MatchWindows(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_ADVANCE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_ADVANCE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            const string Repertoire =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}  advances (ours - GDI), per size");

            foreach (int ppem in new[] { 11, 12, 13, 16, 19 })
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = 400, lfCharSet = 1,
                    lfQuality = ClearTypeQuality, lfFaceName = family,
                };
                IntPtr hf = CreateFontIndirectW(ref lf);
                IntPtr prev = SelectObject(dc, hf);
                int off = 0, worstBy = 0; char worst = ' ';
                var diffs = new List<string>();
                try
                {
                    foreach (char c in Repertoire)
                    {
                        var gi = new ushort[1];
                        GetGlyphIndicesW(dc, c.ToString(), 1, gi, 0);
                        var w = new int[1];
                        if (!GetCharWidthI(dc, 0, 1, gi, w)) continue;

                        int mine = ((IHintedGlyphFont) font).TryGetDeviceAdvance(gi[0], ppem, out float a)
                            ? (int) MathF.Round(a)
                            : (int) MathF.Round(font.Advance(gi[0]) * ppem / font.PixelsPerEm);
                        if (mine != w[0])
                        {
                            off++;
                            if (Math.Abs(mine - w[0]) > Math.Abs(worstBy)) { worstBy = mine - w[0]; worst = c; }
                            if (diffs.Count < 8) diffs.Add($"'{c}'{mine - w[0]:+0;-0}");
                        }
                    }
                }
                finally { SelectObject(dc, prev); DeleteObject(hf); DeleteDC(dc); }
                report.AppendLine($"{ppem,4}  {off,3} of {Repertoire.Length} glyphs differ"
                                  + (off == 0 ? "" : $"; worst '{worst}' by {worstBy:+0;-0}"
                                                     + $"   {string.Join(" ", diffs)}"));
            }
            lock (s_reportLock) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>The fitted X COORDINATES, ours against GDI's, for one glyph.
        /// <para>Stage B compares the two outlines by rasterizing them, and ink survives a small
        /// systematic error. This compares the numbers. If our interpreter runs the x instructions
        /// correctly, the stem edges land on the same values GDI's do -- and where the program said
        /// `MDAP[r]`, on whole pixels.</para>
        /// <para>WPF_XCOORDS=family/char/ppem.</para></summary>
        [Fact]
        public void FittedXCoordinates_AgainstGdis()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_XCOORDS");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_XCOORDS=family/char/ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string[] parts = spec!.Split('/');
            string? file = FontFiles.Find(parts[0], bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            char c = parts[1][0];
            int ppem = int.Parse(parts[2]);

            bool saved = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = false;              // the FULL fit, both axes
            List<PathFigure> ours;
            try { ours = HintedFigures(font, c, ppem); }
            finally { TrueTypeFont.SubpixelFitting = saved; }
            List<PathFigure> theirs = GdiOutline(c, parts[0], ppem, unhinted: false);

            Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  fitted x coordinates ===");
            Console.Error.WriteLine("  ours : " + Xs(ours));
            Console.Error.WriteLine("  gdi  : " + Xs(theirs));
            List<PathFigure> plain = GdiOutline(c, parts[0], ppem, unhinted: true);
            Console.Error.WriteLine("  (unhinted, both agree): " + Xs(plain));
        }

        /// <summary>How many glyphs' fitted X COORDINATES match GDI's exactly, over the repertoire.
        /// <para>The purest test of the interpreter there is: no rasterizer on either side, no
        /// filter, no curve -- just the numbers the program produced. Stage B looked like this test
        /// and is not: it compares OUR outline through OUR rasterizer against GDI's own grey BITMAP,
        /// so a difference there can be either geometry or rasterizer and it cannot say which.</para>
        /// <para>Reported only: set WPF_XMATCH.</para></summary>
        [Theory]
        [InlineData("Segoe UI")]
        [InlineData("Arial")]
        [InlineData("Consolas")]
        public void FittedXCoordinates_MatchRate(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_XMATCH");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_XMATCH to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            const string Repertoire =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            report.AppendLine($"== {family}   glyphs whose fitted x coordinates equal GDI's");

            bool saved = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = false;
            try
            {
                foreach (int ppem in new[] { 11, 12, 13, 16, 19 })
                {
                    int exact = 0, counted = 0;
                    double err = 0;
                    foreach (char c in Repertoire)
                    {
                        bool onY = Environment.GetEnvironmentVariable("WPF_XMATCH_AXIS") == "y";
                        string a = onY ? Ys(HintedFigures(font, c, ppem))
                                       : Xs(HintedFigures(font, c, ppem));
                        string b = onY ? Ys(GdiOutline(c, family, ppem, unhinted: false))
                                       : Xs(GdiOutline(c, family, ppem, unhinted: false));
                        if (a.Length == 0 || b.Length == 0) continue;
                        counted++;
                        if (a == b) { exact++; continue; }
                        // Not equal: how far apart, over the values they can be paired by order.
                        string[] xa = a.Split(' '), xb = b.Split(' ');
                        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
                            err += Math.Abs(double.Parse(xa[i], System.Globalization.CultureInfo.InvariantCulture)
                                          - double.Parse(xb[i], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    report.AppendLine($"{ppem,4}   {exact,3} of {counted,3} exact"
                                      + $"   total |dx| on the rest = {err,8:0.0} px");
                }
            }
            finally { TrueTypeFont.SubpixelFitting = saved; }
            lock (s_reportLock) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>The same, on the y axis -- so a change can be charged to the axis it moved.</summary>
        private static string Ys(List<PathFigure> figures)
        {
            var seen = new SortedSet<float>();
            foreach (PathFigure f in figures)
            {
                seen.Add(MathF.Round(f.Start.Y, 3));
                foreach (PathSegment seg in f.Segments)
                    foreach (Vector2 v in Points(seg))
                        seen.Add(MathF.Round(v.Y, 3));
            }
            return string.Join(" ", seen);
        }

        private static string Xs(List<PathFigure> figures)
        {
            var seen = new SortedSet<float>();
            foreach (PathFigure f in figures)
            {
                seen.Add(MathF.Round(f.Start.X, 3));
                foreach (PathSegment seg in f.Segments)
                    foreach (Vector2 v in Points(seg))
                        seen.Add(MathF.Round(v.X, 3));
            }
            return string.Join(" ", seen);
        }

        private static IEnumerable<Vector2> Points(PathSegment seg)
        {
            switch (seg)
            {
                case LineSegment l: yield return l.Point; break;
                case QuadraticBezierSegment q: yield return q.Control; yield return q.Point; break;
                case CubicBezierSegment cu: yield return cu.Control1; yield return cu.Control2; yield return cu.Point; break;
            }
        }

        /// <summary>Our fitted figures for a glyph, in the same units the cell rasterizer wants --
        /// the same ones OurGray draws, exposed so they can be moved before rasterizing.</summary>
        private static List<PathFigure> HintedFigures(TrueTypeFont font, char c, int ppem)
        {
            int gid = font.GlyphIndex(c);
            if (((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out List<PathFigure> figures))
                return figures;
            return font.TryGetGlyphOutline(gid, out figures)
                ? Scale(figures, ppem / (float) font.PixelsPerEm)
                : new List<PathFigure>();
        }

        private static byte[] OurGray(TrueTypeFont font, char c, int ppem, bool unhinted)
        {
            var cell = new byte[Cell * Cell];
            int gid = font.GlyphIndex(c);

            List<PathFigure> figures;
            if (unhinted)
            {
                if (!font.TryGetGlyphOutline(gid, out figures)) return cell;
                float k = ppem / font.PixelsPerEm;
                figures = Scale(figures, k);
            }
            else if (!((IHintedGlyphFont)font).TryGetHintedOutline(gid, ppem, out figures))
            {
                // The renderer falls back to the outline as drawn when there is no fitting to be had
                // -- below the face's gasp threshold there deliberately is none -- so this has to do
                // the same, or the stage reads as "we draw nothing" where we draw the plain glyph.
                if (!font.TryGetGlyphOutline(gid, out figures)) return cell;
                figures = Scale(figures, ppem / (float) font.PixelsPerEm);
            }

            if (figures.Count == 0) return cell;

            var path = new PathGeometry(FillRule.NonZero, Translate(figures, PenX, BaseY));
            CoverageMask mask = PathRasterizer.Rasterize(path);
            if (mask.Coverage == null) return cell;

            for (int row = 0; row < mask.Height; row++)
                for (int col = 0; col < mask.Width; col++)
                {
                    int x = (int)mask.OriginX + col;
                    int y = (int)mask.OriginY + row;
                    if ((uint)x >= Cell || (uint)y >= Cell) continue;
                    cell[y * Cell + x] = mask.Coverage[row * mask.Width + col];
                }
            return cell;
        }

        private static List<PathFigure> OurOutline(TrueTypeFont font, char c, int ppem)
        {
            if (!font.TryGetGlyphOutline(font.GlyphIndex(c), out List<PathFigure> figures))
                return new List<PathFigure>();
            // (float), because PixelsPerEm is an int and 16/48 in integer arithmetic is ZERO --
            // which collapsed every one of our outlines onto the origin and made stage A read
            // as "GDI draws nothing".
            return Scale(figures, ppem / (float) font.PixelsPerEm);
        }

        private static List<PathFigure> Scale(List<PathFigure> figures, float k)
            => Map(figures, v => new Vector2(v.X * k, v.Y * k));

        private static List<PathFigure> Translate(List<PathFigure> figures, float dx, float dy)
            => Map(figures, v => new Vector2(v.X + dx, v.Y + dy));

        private static List<PathFigure> Map(List<PathFigure> figures, Func<Vector2, Vector2> f)
        {
            var outp = new List<PathFigure>(figures.Count);
            foreach (PathFigure fig in figures)
            {
                var copy = new PathFigure(f(fig.Start)) { Closed = fig.Closed };
                foreach (PathSegment seg in fig.Segments)
                    switch (seg)
                    {
                        case LineSegment l: copy.Segments.Add(new LineSegment(f(l.Point))); break;
                        case QuadraticBezierSegment q:
                            copy.Segments.Add(new QuadraticBezierSegment(f(q.Control), f(q.Point))); break;
                        case CubicBezierSegment c:
                            copy.Segments.Add(new CubicBezierSegment(f(c.Control1), f(c.Control2), f(c.Point)));
                            break;
                    }
                outp.Add(copy);
            }
            return outp;
        }

        // ---- the comparison -----------------------------------------------------------------------

        private static (int Pixels, long Total, long OurInk, long TheirInk) Compare(byte[] ours, byte[] theirs)
        {
            int pixels = 0;
            long total = 0, ourInk = 0, theirInk = 0;
            for (int i = 0; i < ours.Length; i++)
            {
                int d = Math.Abs(ours[i] - theirs[i]);
                if (d > 8) pixels++;
                total += d;
                ourInk += ours[i];
                theirInk += theirs[i];
            }
            return (pixels, total, ourInk, theirInk);
        }

        /// <summary>ONE glyph from one stage, as two character maps side by side. An aggregate says
        /// a face is wrong at a size; this says whether it is shifted, resized, or differently
        /// shaped, which are three different faults with three different causes.
        /// <para>WPF_STAGE_GLYPH=family/char/ppem, e.g. Consolas/Q/19.</para></summary>
        [Fact]
        public void OneGlyphSideBySide()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_STAGE_GLYPH");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_STAGE_GLYPH=family/char/ppem");
            string[] parts = spec!.Split('/');
            string? file = FontFiles.Find(parts[0], bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            char c = parts[1][0];
            int ppem = int.Parse(parts[2]);

            bool saved = TrueTypeFont.SubpixelFitting;
            try
            {
                TrueTypeFont.SubpixelFitting = false;
                byte[] mine = OurGray(font, c, ppem, unhinted: false);
                byte[] theirs = GdiGray(c, parts[0], ppem, unhinted: false);
                Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  fitted (x+y), ours | GDI ===");
                Dump(mine, theirs);
            }
            finally { TrueTypeFont.SubpixelFitting = saved; }
        }

        private static void Dump(byte[] a, byte[] b)
        {
            const string Ramp = " .:-=+*#%@";
            int x0 = Cell, x1 = 0, y0 = Cell, y1 = 0;
            for (int y = 0; y < Cell; y++)
                for (int x = 0; x < Cell; x++)
                    if (a[y * Cell + x] > 8 || b[y * Cell + x] > 8)
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
            if (x1 < x0) { Console.Error.WriteLine("  (both blank)"); return; }

            for (int y = y0; y <= y1; y++)
            {
                var line = new System.Text.StringBuilder("  ");
                for (int x = x0; x <= x1; x++) line.Append(Ramp[a[y * Cell + x] * (Ramp.Length - 1) / 255]);
                line.Append("   |   ");
                for (int x = x0; x <= x1; x++) line.Append(Ramp[b[y * Cell + x] * (Ramp.Length - 1) / 255]);
                Console.Error.WriteLine(line.ToString());
            }
        }

        public static TheoryData<string> Faces()
        {
            var d = new TheoryData<string>();
            foreach (string f in new[] { "Segoe UI", "Arial", "Times New Roman", "Consolas" }) d.Add(f);
            return d;
        }

        [Theory]
        [MemberData(nameof(Faces))]
        public void EachStageOfTheTextPipeline_AgainstGdis(string family)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI draws the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_STAGE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_STAGE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold: false, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            const string Repertoire =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

            var report = new System.Text.StringBuilder();
            bool savedFitting = TrueTypeFont.SubpixelFitting;
            try
            {
            report.AppendLine($"== {family}");
            report.AppendLine("ppem  stage      differing  sum|d|      our ink / GDI ink");

            // 7 and 8 sit below Segoe UI's gasp gridfit threshold, where GDI is expected NOT to
            // fit -- which stage B can confirm rather than leave assumed.
            foreach (int ppem in new[] { 7, 8, 11, 12, 13, 16, 19 })
            {
                foreach (string stage in new[] { "A", "B", "Y" })
                {
                    bool unhinted = stage == "A";
                    // Stage Y fits the outline the way ClearType asks for it -- y only, x left
                    // where the scaled outline puts it. Without this the decomposition has a hole
                    // in it: stage B fits BOTH axes, so everything between B and C could be the
                    // lamps or could be the fitting mode, and there would be no way to tell.
                    TrueTypeFont.SubpixelFitting = stage == "Y";
                    int pixels = 0;
                    long total = 0, ourInk = 0, theirInk = 0;
                    // WHICH letters, not just how many pixels. An aggregate says a face is wrong at a
                    // size; the names say which glyph's program to step through, which is the only
                    // thing that leads anywhere.
                    var worst = new List<(int Pixels, char Ch)>();
                    foreach (char c in Repertoire)
                    {
                        byte[] mine = unhinted
                            ? RasterizeIntoCell(OurOutline(font, c, ppem))
                            : OurGray(font, c, ppem, unhinted: false);
                        byte[] theirs = unhinted
                            ? RasterizeIntoCell(GdiOutline(c, family, ppem, unhinted: true))
                            : GdiGray(c, family, ppem, unhinted: false);
                        var (p, t, o, th) = Compare(mine, theirs);
                        pixels += p; total += t; ourInk += o; theirInk += th;
                        if (p > 0) worst.Add((p, c));
                    }
                    string label = stage switch
                    {
                        "A" => "A unhinted",
                        "B" => "B hinted  ",
                        _   => "Y y-only  ",
                    };
                    worst.Sort((x, y) => y.Pixels.CompareTo(x.Pixels));
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(6, worst.Count); i++)
                        names.Append($" '{worst[i].Ch}'={worst[i].Pixels}");

                    report.AppendLine($"{ppem,4}  {label}"
                        + $" {pixels,9}  {total,9}      "
                        + $"{(theirInk == 0 ? 0 : (double)ourInk / theirInk):0.0000}"
                        + $"  ourInk={ourInk} gdiInk={theirInk}"
                        + $"  worst:{names}");
                }
            }

            }
            finally
            {
                // Left as it was found whatever happened. SubpixelFitting is process-wide and
                // the parity tests read it; leaking it re-renders every one of them, and the
                // failure would look like a change in the renderer rather than in this test.
                TrueTypeFont.SubpixelFitting = savedFitting;
            }

            lock (s_refusals)
                if (s_refusals.Count > 0)
                {
                    report.AppendLine($"  GDI declined {s_refusals.Count} requests, first few:");
                    for (int i = 0; i < Math.Min(3, s_refusals.Count); i++)
                        report.AppendLine("    " + s_refusals[i]);
                    s_refusals.Clear();
                }

            lock (Repertoire) File.AppendAllText(path!, report.ToString());
        }
    }
}
