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
        private static byte[] GdiGray(char c, string family, int ppem, bool unhinted, bool bold = false)
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var lf = new LOGFONTW
            {
                lfHeight = -ppem, lfWeight = bold ? 700 : 400, lfCharSet = 1,
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
        /// <summary>internal, not private: stage CR in WindowsGlyphParityTests feeds GDI's own
        /// fitted outline through OUR ClearType pipeline, and this is where that outline comes
        /// from. Same assembly, same namespace.</summary>
        internal static List<PathFigure> GdiOutline(char c, string family, int ppem, bool unhinted,
                                                   int xScale = 1, bool bold = false,
                                                   bool italic = false)
        {
            var figures = new List<PathFigure>();
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var lf = new LOGFONTW
            {
                lfHeight = -ppem, lfWeight = bold ? 700 : 400, lfItalic = (byte) (italic ? 1 : 0), lfCharSet = 1,
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
        /// <para>THE PREMISE IS FALSE, MEASURED 2026-09-03 -- and this stage has been steering
        /// the x search ever since it was written, so the correction matters more than the stage
        /// does. Asked through a stretched MAT2, GDI answers:</para>
        /// <code>
        ///   selector             drawn(ClearType)   GGO   GGO stretched 3x
        ///   4  stretched                        0     0                 10   &lt;- set
        ///   64 ClearType enabled               10     0                  0   &lt;- still clear
        /// </code>
        /// <para>So tripling x does NOT ask for the glyph the way ClearType asks for it. It asks
        /// the bi-level program, on a three-times-finer grid, having additionally told the face it
        /// is being STRETCHED -- a third program again, and one every face is entitled to branch
        /// on (Segoe UI's prep asks selector 4 in its first four questions).</para>
        /// <para>Which retires what this stage was read as saying. "GDI puts each stem on a lamp,
        /// and our 'm' carries its second and third stems a third of a pixel right of GDI's" is a
        /// statement about the stretched bi-level program, not about ClearType geometry -- and it
        /// is the observation the whole lamp-grid family of x rules was built on. Those rules
        /// measure badly on the text oracle (XHintMode 6 costs 883,255 at 12ppem against the
        /// sixteenth grid), and this is why.</para>
        /// <para>It also says something the stage cannot: GDI tells the face it is stretched when
        /// it IS, and does not when drawing ClearType. So GDI's ClearType hinting runs in ordinary
        /// unstretched space and the three-times supersampling happens after it, inside the
        /// rasterizer, where the font program cannot see it.</para>
        /// <para>THE LAST SENTENCE OF THAT USED TO READ "a face therefore never fits anything to
        /// the lamp grid, and no rule that puts our x on it can be right", AND THAT PART IS WRONG.
        /// The coordinates are unstretched, but the GRID IS NOT THE PIXEL GRID. GDI keeps two
        /// complete sets of rounding functions and picks between them on the ClearType flag
        /// (itrp_SVTCA_1 selects table entry n or n+8; the pointer array is at
        /// PTR_itrp_RoundToDoubleGrid). Read side by side in fontdrvhost.exe, the binary that
        /// actually renders, the subpixel set is the ordinary set with the grid quartered and the
        /// engine compensation halved:</para>
        /// <code>
        ///   itrp_RoundToGrid        (v + c + 0x20) &amp; ~0x3f        RoundToGridSP        (v + c/2 + 2) &amp; ~3
        ///   itrp_RoundToHalfGrid    (v + c &amp; ~0x3f) + 0x20         RoundToHalfGridSP    (v + c/2 &amp; ~3) + 2
        ///   itrp_RoundToDoubleGrid  grid 32                        RoundToDoubleGridSP  grid 2
        /// </code>
        /// <para>So a face fitted under ClearType DOES round x on a sixteenth of a pixel -- in
        /// unstretched coordinates, which is the part this stage got right. The two readings are
        /// not in conflict once separated: STRETCHING the space is not how the finer grid is
        /// reached, the round-function table is. Our ClearTypeGrid of 16 is that grid, and scaling
        /// the value by 16 before rounding on 64 is algebraically the SP column above.</para>
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
        // EVERY STYLE, not just regular. The specimen's worst rows are italic -- Segoe UI italic
        // at 20ppem drifts progressively left, a pixel by mid-line and two by the end, which is an
        // advance deficit -- and this probe could not see it because it only ever built the
        // regular face. An instrument that tests one style cannot find a bug in another.
        [InlineData("Segoe UI", false, false)]
        [InlineData("Segoe UI", true, false)]
        [InlineData("Segoe UI", false, true)]
        [InlineData("Segoe UI", true, true)]
        [InlineData("Arial", false, false)]
        [InlineData("Arial", true, false)]
        [InlineData("Arial", false, true)]
        [InlineData("Arial", true, true)]
        [InlineData("Times New Roman", false, false)]
        [InlineData("Times New Roman", true, false)]
        [InlineData("Times New Roman", false, true)]
        [InlineData("Times New Roman", true, true)]
        [InlineData("Verdana", false, false)]
        [InlineData("Verdana", true, false)]
        [InlineData("Verdana", false, true)]
        [InlineData("Verdana", true, true)]
        [InlineData("Tahoma", false, false)]
        [InlineData("Tahoma", true, false)]
        [InlineData("Tahoma", false, true)]
        [InlineData("Tahoma", true, true)]
        [InlineData("Consolas", false, false)]
        [InlineData("Consolas", true, false)]
        [InlineData("Consolas", false, true)]
        [InlineData("Consolas", true, true)]
        public void Advances_MatchWindows(string family, bool bold, bool italic)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? path = Environment.GetEnvironmentVariable("WPF_ADVANCE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_ADVANCE_REPORT to collect this");
            string? file = FontFiles.Find(family, bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {family}");

            const string Repertoire =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,";
            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            var report = new System.Text.StringBuilder();
            string style = (bold ? "B" : "") + (italic ? "I" : "");
            report.AppendLine($"== {family} {(style.Length == 0 ? "R" : style),-2}  advances (ours - GDI), per size");

            foreach (int ppem in new[] { 9, 10, 11, 12, 13, 16, 19, 20 })
            {
                IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                var lf = new LOGFONTW
                {
                    lfHeight = -ppem, lfWeight = bold ? 700 : 400, lfItalic = (byte) (italic ? 1 : 0), lfCharSet = 1,
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

                        // Ask the PRODUCT, do not reimplement it. This duplicated the fallback
                        // arithmetic and kept reporting a difference after the product was fixed.
                        int mine = (int) font.CompatibleAdvance(gi[0], ppem, ppem);
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

            // WPF_XMATCH_AXIS=y reads the same comparison on the OTHER axis, where
            // GetGlyphOutline is authoritative -- y is hinted bi-level in both renderers -- so a
            // disagreement there is a plain interpreter bug rather than a ClearType question.
            bool onY = Environment.GetEnvironmentVariable("WPF_XMATCH_AXIS") == "y";
            Func<List<PathFigure>, string> show = onY ? Ys : Xs;
            Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  fitted {(onY ? "y" : "x")} coordinates ===");
            Console.Error.WriteLine("  ours : " + show(ours));
            Console.Error.WriteLine("  gdi  : " + show(theirs));
            List<PathFigure> plain = GdiOutline(c, parts[0], ppem, unhinted: true);
            Console.Error.WriteLine("  (unhinted, both agree): " + show(plain));

            // And the same glyph through the per-call BI-LEVEL pass, which is meant to reproduce the
            // gdi line above without any environment variable set. If it does not, the pass and the
            // configuration it was modelled on have drifted apart.
            saved = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = false;
            TrueTypeInterpreter.BiLevelPass = true;
            List<PathFigure> bi;
            // A FRESH font: the hinted outline is cached by (glyph, size), so asking the same
            // instance twice returns the first answer and any second configuration looks like a
            // no-op. That is exactly how this line first reported "the bi-level pass changes
            // nothing" for every glyph.
            var fresh = new TrueTypeFont(File.ReadAllBytes(file!));
            try { bi = HintedFigures(fresh, c, ppem); }
            finally { TrueTypeInterpreter.BiLevelPass = false; TrueTypeFont.SubpixelFitting = saved; }
            Console.Error.WriteLine("  bilevel pass : " + Xs(bi));
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
            // GGO hints as a GREYSCALE rasterizer -- measured: through it GDI reports the
            // ClearType, compatible-width and symmetric bits all clear. Without
            // WPF_XMATCH_BILEVEL=1 this compares our ClearType-mode fit against a bi-level
            // oracle, which is two different programs and not a measure of anything.
            TrueTypeInterpreter.BiLevelPass =
                Environment.GetEnvironmentVariable("WPF_XMATCH_BILEVEL") == "1";
            try
            {
                foreach (int ppem in new[] { 11, 12, 13, 16, 19 })
                {
                    int exact = 0, counted = 0;
                    double err = 0;
                    var differing = new System.Text.StringBuilder();
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
                        differing.Append(c);
                        // Not equal: how far apart, over the values they can be paired by order.
                        string[] xa = a.Split(' '), xb = b.Split(' ');
                        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
                            err += Math.Abs(double.Parse(xa[i], System.Globalization.CultureInfo.InvariantCulture)
                                          - double.Parse(xb[i], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    report.AppendLine($"{ppem,4}   {exact,3} of {counted,3} exact"
                                      + $"   total |dx| on the rest = {err,8:0.0} px"
                                      + $"   [{differing}]");
                }
            }
            finally
            {
                TrueTypeFont.SubpixelFitting = saved;
                TrueTypeInterpreter.BiLevelPass = false;
            }
            lock (s_reportLock) File.AppendAllText(path!, report.ToString());
        }

        /// <summary>AND WHAT IT SAYS: THE INTERPRETER IS EXACT.
        /// <para>Asked in the mode the oracle is actually in, our grid-fitting reproduces GDI's
        /// point for point:</para>
        /// <code>
        ///   Consolas  @16   62 of 62 glyphs exact, 0 of 1751 points differ in x or y
        ///   Segoe UI  @16   62 of 62 glyphs exact, 0 of 1504 points differ in x or y
        ///   Consolas  @12   60 of 62,  45 points in x
        ///   Segoe UI  @12   49 of 62,   1 in x, 27 in y
        ///   Arial     @12   49 of 53,   4 in x,  8 in y
        /// </code>
        /// <para>Two whole faces, every glyph, every point, both axes. Whatever is wrong with our
        /// ClearType text, the machine that runs the hinting programs is not it -- and that retires
        /// a suspicion the investigation has carried from the start.</para>
        /// <para>THE THING THAT MADE IT LOOK BROKEN WAS ONE BIT. Before this, the same comparison
        /// said 0 of 62 on x and 33 of 62 on y for Arial, and both numbers were artefacts:</para>
        /// <para>First, mode. GGO hints as a GREYSCALE rasterizer -- asked through it, GDI reports
        /// the ClearType, compatible-width and symmetric bits all clear while reporting the same
        /// rasterizer version 42. Comparing our ClearType-mode fit against it compares two
        /// different programs. WPF_GGOPTS_BILEVEL=1 and WPF_XMATCH_BILEVEL=1 exist for that.</para>
        /// <para>Second, the greyscale bit itself. The bi-level pass answers GETINFO "greyscale,
        /// yes", which is right for GDI rendering a grey bitmap and wrong for GGO, which the
        /// oracle measures answering NO. Consolas branches on it: its prep writes an extra half
        /// pixel into stem control value 420 on the greyscale branch (1.0469 -> 1.0156 -> 1.5156),
        /// so every stem in the face came out half a pixel fat -- 'H' 1.52 wide against GDI's 1.00,
        /// and 1054 of 1751 points differing. WPF_CT_GREY=0 answers as GGO answers, and Consolas
        /// goes to 62 of 62. That is a TEST facility and not a product fix: the two contexts really
        /// do get different answers from GDI, and the shipping ClearType path already answers no.
        /// </para>
        /// <para>Third, the instrument. The coordinate tests compare SORTED SETS OF DISTINCT
        /// VALUES, so they pair the third-smallest of ours with the third-smallest of theirs, and
        /// GGO's implied on-curve midpoints -- which our figures do not carry -- count as
        /// differences on their own. Arial's V, X, K, M and A each report several disagreements
        /// there and are exact point for point.</para>
        /// <para>Where that leaves the search: the face's own program, the interpreter that runs
        /// it, and the mode it is dispatched in are all now accounted for. What is left in the
        /// ClearType path is the layer we invented on top of it.</para></summary>
        /// <summary>OUR FITTED POINTS AGAINST GDI'S OWN, one point at a time, with no solver.
        /// <para>The x-coordinate work has had to infer GDI's geometry from its lamps, because
        /// GetGlyphOutline renders greyscale and cannot see a face's ClearType branch. On Y there is
        /// no such problem: y is hinted the same way in both renderers, so GGO is AUTHORITATIVE and
        /// a disagreement is a plain interpreter bug rather than a question about ClearType.</para>
        /// <para>Pairing the two point lists is the whole difficulty, and GGO solves it itself:
        /// asked for the same glyph unhinted it returns the SAME sequence in the SAME order, so
        /// matching our unfitted points against GGO's unfitted ones fixes the correspondence, and
        /// the fitted values can then be compared index for index. Comparing sorted sets of
        /// distinct values -- which is what the coordinate tests do -- cannot do this: it pairs the
        /// third-smallest of ours with the third-smallest of theirs, which are not the same
        /// point.</para>
        /// <para>WPF_GGOPTS=family/char/ppem.</para></summary>
        [Fact]
        public void FittedPoints_AgainstGdisOwn()
        {
            string? spec = Environment.GetEnvironmentVariable("WPF_GGOPTS");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_GGOPTS=family/char/ppem");
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            // family/chars/ppem, with an optional /B, /I or /BI. The styles are not a luxury:
            // Times New Roman's ITALIC is the worst face and size in the whole specimen and on the
            // parity suite alike, and until this parsed a style suffix the exact oracle could only
            // ever be pointed at romans.
            string[] parts = spec!.Split('/');
            string styleSpec = parts.Length > 3 ? parts[3] : "";
            bool bold = styleSpec.Contains('B'), italic = styleSpec.Contains('I');
            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}");
            int ppem = int.Parse(parts[2]);

            byte[] faceBytes = File.ReadAllBytes(file!);
            FontFiles.DeclaredStyle(faceBytes, 0, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(faceBytes, bold && !fileBold, italic && !fileItalic);
            bool saved = TrueTypeFont.SubpixelFitting;
            // WPF_GGOPTS_CT=1: fit the way the RENDERER does (SubpixelFitting on). The two
            // settings below are a THIRD mode -- neither the bi-level oracle nor the ClearType
            // path the pixels come from -- so a y difference seen here need not be one the
            // renderer has. Ask for the render path explicitly before blaming it.
            TrueTypeFont.SubpixelFitting =
                Environment.GetEnvironmentVariable("WPF_GGOPTS_CT") == "1";
            // GGO ANSWERS AS A GREYSCALE RASTERIZER -- measured, not assumed: asked through
            // GGO, GDI reports the ClearType, compatible-width and symmetric bits all CLEAR
            // while reporting the same rasterizer version 42. Comparing our ClearType-mode
            // fit against it is comparing two different programs, so WPF_GGOPTS_BILEVEL=1
            // puts this side into the mode the oracle is actually in.
            bool bilevel = Environment.GetEnvironmentVariable("WPF_GGOPTS_BILEVEL") == "1";
            bool quiet = Environment.GetEnvironmentVariable("WPF_GGOPTS_QUIET") == "1";
            TrueTypeInterpreter.BiLevelPass = bilevel;
            TrueTypeInterpreter.s_capturePoints = true;
            try
            {
                string chars = parts[1] == "*"
                    ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
                    : parts[1];
                int gTotal = 0, gExact = 0, pTotal = 0, pOffX = 0, pOffY = 0;
                int unpairable = 0, notFitted = 0, gdiFittedAnyway = 0;
                foreach (char c in chars)
                {
                    if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem, out _))
                    { Console.Error.WriteLine($"'{c}': we decline to fit it"); continue; }
                    TrueTypeInterpreter.GlyphPoints? pts = font.LastHintedPoints;
                    // WPF_GGOPTS_RAW=1: print every captured point and skip the GDI pairing.
                    // THE PAIRING DROPS EXACTLY THE GLYPHS THAT NEED LOOKING AT -- grid-fitting
                    // changes how GGO segments a curve, so its two reports differ in length and
                    // five of Times Bold's six bowls come back "unpairable". This side of the
                    // comparison is still worth seeing on its own: it is where our fitted extremes
                    // ended up, which is the question for a counter.
                    // WPF_GGOPTS_RAW=2 also prints GDI's OWN fitted outline, unpaired -- every
                    // on-curve point GGO reports, in order -- so a bowl can be read even when the
                    // pairing cannot line it up with ours.
                    if (Environment.GetEnvironmentVariable("WPF_GGOPTS_RAW") == "2")
                    {
                        foreach (bool unh in new[] { true, false })
                        {
                            List<Vector2> g = FlattenOnCurve(GdiOutline(c, parts[0], ppem, unhinted: unh,
                                                                        bold: bold, italic: italic));
                            Console.Error.WriteLine($"GDI-{(unh ? "PLAIN " : "FITTED")} '{c}' @{ppem}: {g.Count} on-curve points (y up)");
                            for (int i = 0; i < g.Count; i++)
                                Console.Error.WriteLine($"   g{i,-3} ({g[i].X,8:0.000},{-g[i].Y,8:0.000})");
                        }
                    }
                    if (pts is not null && Environment.GetEnvironmentVariable("WPF_GGOPTS_RAW") is "1" or "2")
                    {
                        Console.Error.WriteLine($"RAW '{c}' @{ppem}: {pts.PointCount} points");
                        for (int i = 0; i < pts.PointCount; i++)
                            Console.Error.WriteLine($"   pt{i,-3} {(pts.OnCurve[i] ? "on " : "off")}"
                                + $" start({pts.StartX[i],8:0.000},{pts.StartY[i],8:0.000})"
                                + $"  fit({pts.FitX[i],8:0.000},{pts.FitY[i],8:0.000})"
                                + $"  d({(pts.FitX[i] - pts.StartX[i]) * 64,6:+0;-0;0},"
                                + $"{(pts.FitY[i] - pts.StartY[i]) * 64,6:+0;-0;0})/64");
                        continue;
                    }
                    // NOT a silent skip. This said nothing, and a face whose interpreter never
                    // runs at a size reported "0 of 0 glyphs exact" -- which reads as "nothing to
                    // report" and means "we did not grid-fit this face at all". Consolas at 10ppem
                    // was in that state, and it is the worst face in the pixel comparison.
                    if (pts is null || pts.PointCount == 0)
                    {
                        notFitted++;
                        // ...and DID GDI? Declining to fit is only a bug if GDI fitted. Its own
                        // two reports answer it: identical fitted and unfitted outlines mean GDI
                        // declined as well, which is gasp doing its job and not a disagreement.
                        List<Vector2> gdiPlain = Flatten(GdiOutline(c, parts[0], ppem, unhinted: true,
                                                                    bold: bold, italic: italic));
                        List<Vector2> gdiFitted = Flatten(GdiOutline(c, parts[0], ppem, unhinted: false,
                                                                     bold: bold, italic: italic));
                        bool gdiFitTooo = gdiPlain.Count != gdiFitted.Count;
                        for (int i = 0; !gdiFitTooo && i < gdiPlain.Count; i++)
                            if (gdiPlain[i] != gdiFitted[i]) gdiFitTooo = true;
                        if (gdiFitTooo) gdiFittedAnyway++;
                        // WPF_GGOPTS_UNHINTED=1: GDI's UNHINTED points, in 64ths, so the unfitted
                        // outline we draw at a no-gridfit size can be checked against GDI's.
                        if (Environment.GetEnvironmentVariable("WPF_GGOPTS_UNHINTED") == "1")
                        {
                            var sbU = new System.Text.StringBuilder();
                            sbU.Append($"      GDI unhinted '{c}' @{ppem}: {gdiPlain.Count} pts:");
                            foreach (Vector2 v in gdiPlain)
                                sbU.Append($" ({v.X * 128,0:0.#},{v.Y * 128,0:0.#})");
                            Console.Error.WriteLine(sbU.ToString());
                            // ...and OURS, the outline TryGetHintedOutline hands the renderer at
                            // this size (flattened, 64ths, y-down like GDI's).
                            if (((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem, out List<PathFigure> oursPlain))
                            {
                                var sbO = new System.Text.StringBuilder();
                                List<Vector2> op = Flatten(oursPlain);
                                sbO.Append($"      ours unfitted '{c}' @{ppem}: {op.Count} pts:");
                                foreach (Vector2 v in op)
                                    sbO.Append($" ({v.X * 128,0:0.#},{v.Y * 128,0:0.#})");
                                Console.Error.WriteLine(sbO.ToString());
                            }
                        }
                        // "BUT GDI DID" MEANS GetGlyphOutline DID, NOT THAT THE CLEARTYPE
                        // RASTERIZER DOES. GGO hints unless it is handed GGO_UNHINTED, whatever
                        // the 'gasp' says, so this line fires for any face whose program still
                        // moves points at this size -- Arial Regular, Times New Roman Regular and
                        // all three Consolas at 8ppem, every one of which has GRIDFIT and
                        // SYMMETRIC_GRIDFIT CLEAR in its version 1 table and must not be fitted.
                        // Reading it as "we are missing a fit" and forcing one (WPF_GASP_FIT=
                        // always) measures Times Regular at 8ppem 1,200 -> 183,251, Arial Regular
                        // 630 -> 179,651, Consolas Regular 259 -> 99,918. The faces where it says
                        // "nor did GDI" are the ones whose PREP switches instructions off with
                        // INSTCTRL, which GGO does obey.
                        if (!quiet)
                            Console.Error.WriteLine($"'{c}': WE DID NOT GRID-FIT IT"
                                + (gdiFitTooo ? " -- BUT GGO DID (which is not the rasterizer;"
                                                + " see the note above)" : " (nor did GDI)"));
                        continue;
                    }

                    List<Vector2> plain = Flatten(GdiOutline(c, parts[0], ppem, unhinted: true,
                                                             bold: bold, italic: italic));
                    List<Vector2> fitted = Flatten(GdiOutline(c, parts[0], ppem, unhinted: false,
                                                              bold: bold, italic: italic));
                    // FITTING CHANGES HOW GGO SEGMENTS THE OUTLINE, so its two reports need not be
                    // the same length and CANNOT ALWAYS BE PAIRED. Grid-fitting leaves points
                    // collinear or coincident, GDI emits a line where it emitted a spline, and the
                    // implied on-curve midpoints go with it -- in BOTH directions: Times New
                    // Roman's 'B' at 12ppem reports 70 points unfitted against 59 fitted, and
                    // counting only the on-curve ones reverses it to 40 against 49. So the
                    // on-curve fallback below is a partial remedy, not a fix, and it buys nothing
                    // on Times: the round glyphs stay unpairable.
                    // This is a LIMIT OF THE ORACLE and it is not neutral -- the glyphs it drops
                    // are the curved ones, which is to say the ones this investigation cares
                    // about. The count of them is reported, so the sample is never silently
                    // biased; do not read a face's total without it.
                    List<Vector2> fittedFull = fitted;
                    // WPF_GGOPTS_DUMP=1: GGO's fitted list beside our captured points, so the
                    // correspondence can be established by eye on a glyph the automatic pairing
                    // refuses. Curved glyphs are where it refuses, and curved glyphs -- the Times
                    // bowls -- are the largest remaining pool, so "our bi-level fit is GDI's" has
                    // never actually been checked on one.
                    if (Environment.GetEnvironmentVariable("WPF_GGOPTS_DUMP") == "1")
                    {
                        Console.Error.WriteLine($"-- '{c}' GGO fitted {fittedFull.Count},"
                            + $" GGO unfitted {plain.Count}, ours {pts.PointCount}");
                        // IN SIXTY-FOURTHS, to three places. GGO reports POINTFX, which is
                        // 16.16, so a coordinate that is NOT a whole sixty-fourth is visible here
                        // and nowhere else -- and the implied midpoints are the only places one
                        // could arise, since every real point comes out of a 26.6 interpreter.
                        // Whether GDI's midpoint of two odd coordinates is the half, the floor or
                        // the ceiling is a fact about GDI, readable off this line.
                        for (int i = 0; i < fittedFull.Count; i++)
                            Console.Error.WriteLine($"   ggo[{i,3}] ({fittedFull[i].X * 64f,10:0.###},"
                                + $"{-fittedFull[i].Y * 64f,10:0.###})");
                        for (int i = 0; i < pts.PointCount; i++)
                            Console.Error.WriteLine($"   our[{i,3}] ({pts.FitX[i] * 64f,10:0.###},"
                                + $"{pts.FitY[i] * 64f,10:0.###})  {(pts.OnCurve[i] ? "on " : "off")}"
                                + $" {(pts.TouchedX[i] ? "X" : ".")}");
                    }
                    bool onCurveOnly = plain.Count != fitted.Count;
                    if (onCurveOnly)
                    {
                        plain = FlattenOnCurve(GdiOutline(c, parts[0], ppem, unhinted: true,
                                                          bold: bold, italic: italic));
                        fitted = FlattenOnCurve(GdiOutline(c, parts[0], ppem, unhinted: false,
                                                           bold: bold, italic: italic));
                    }
                    // PAIR BY INDEX WHEN GGO'S FITTED REPORT IS ALREADY OUR POINT LIST. The
                    // correspondence above goes the long way round -- find our unfitted point in
                    // GGO's unfitted list, then read the fitted list at that index -- which needs
                    // GGO's two reports to be the same length, and on a curved glyph they are not.
                    // But the FITTED report frequently has exactly as many points as the font's
                    // glyph does, which is exactly as many as the interpreter captured, and in the
                    // same order; Times New Roman's '0' at 14ppem reports 23 unfitted against 37
                    // fitted, and 37 is the glyph's own point count. So when the fitted count
                    // matches ours, index i pairs with index i and the detour is unnecessary.
                    // <para>GUARDED, because a wrong pairing would invent differences rather than
                    // fail: every GDI fitted point must land within 1.5px of OUR scaled unfitted
                    // point at the same index. A fitted point does not travel that far at these
                    // sizes -- the largest legitimate move seen across the Times repertoire is
                    // about 0.7px -- so a misalignment fails the check instead of reporting
                    // nonsense.</para>
                    // <para>THE GUARD STARTED AT 3px AND THAT WAS TOO LOOSE. Times 'P' at 13 and
                    // 14ppem came back with four points differing, by up to 58/64 in x and 48/64
                    // in y, and all four were OFF-CURVE CONTROLS with GDI's values suspiciously
                    // snapped (2.000, 2.203, 2.797). That is what a drift of one index across a
                    // run of implied on-curve midpoints looks like, not a fitting difference, and
                    // at 1.6px it slipped under a 3px bound. Off-curve points are exactly where
                    // GGO's segmentation and ours disagree, so they are where a by-index pairing
                    // fails first.</para>
                    // <para>This is what makes the bi-level oracle reach CURVED glyphs at all. It
                    // was blind to every one of them -- and those are the Times bowls, which is
                    // the largest remaining pool -- so "our bi-level fit is GDI's" had only ever
                    // been established on glyphs GGO happens to segment identically both ways,
                    // which is to say the straight and diagonal ones. WPF_GGOPTS_BYINDEX=0 goes
                    // back to requiring the round trip.</para>
                    // <para>AND THE COUNTS DO NOT HAVE TO MATCH EXACTLY, because the way they
                    // differ is known: GGO MATERIALISES THE IMPLIED ON-CURVE MIDPOINTS. TrueType
                    // lets two off-curve control points sit next to each other and leaves the
                    // on-curve point between them implied at their midpoint; GGO's report writes
                    // it out. So GGO's fitted list is our point list with one extra entry wherever
                    // a contour has two consecutive off-curve points -- Times New Roman's 'b' at
                    // 14ppem is 37 of ours against 40 of GGO's, 'd' 46 against 54, 'm' 88 against
                    // 101 -- and reconstructing those insertions gives an exact index map.
                    // <para>This is what finally lets the oracle see a Times BOWL. Before it, 29 of
                    // the 62 glyphs at 14ppem were unpairable and they were precisely the round
                    // ones, which is to say the pool the whole Times investigation is about. When
                    // no contour has consecutive off-curve points the map is the identity and this
                    // is exactly the rule it replaces.</para>
                    // <para>The 1.5px proximity guard below still applies to every mapped point, so
                    // a wrong reconstruction is REFUSED rather than reported as a difference -- and
                    // it has to be, because off-curve points are where GGO's segmentation and ours
                    // disagree, which is where a by-index pairing fails first.</para>
                    float shearForPair = font.ObliqueShearApplied;
                    int reconCount = -1; bool reconGuard = false;
                    bool byIndex = false;
                    int[] gdiIdx = Array.Empty<int>();
                    if (plain.Count != fitted.Count
                        && Environment.GetEnvironmentVariable("WPF_GGOPTS_BYINDEX") != "0"
                        && pts.PointCount > 0 && pts.EndPoints.Length > 0)
                    {
                        // AND SOME CONTOURS CARRY A CLOSING REPEAT OF THEIR FIRST POINT.
                        // Times' '9' at 14ppem is 45 GGO points against 40 of ours: its first
                        // contour is 24 points + 3 materialised midpoints, and its second is 16 + 1
                        // + the first point AGAIN at the end. Only a contour whose last point is
                        // OFF the curve can carry one -- the closing curve needs an on-curve end to
                        // land on -- but not every such contour does, and assuming they all do
                        // breaks glyphs that were pairing before ('0' is 37 against 37 and a blanket
                        // repeat makes it 38). So enumerate: the candidates are the contours ending
                        // off-curve, and each is either repeated or not. With eight or fewer of them
                        // that is at most 256 reconstructions, and the count test plus the 1.5px
                        // proximity guard below decide which one GGO actually produced.
                        var tails = new List<int>();
                        int scan = 0;
                        foreach (int e in pts.EndPoints)
                        {
                            if (e >= scan && e < pts.PointCount && !pts.OnCurve[e]) tails.Add(e);
                            scan = e + 1;
                        }
                        int combos = tails.Count is > 0 and <= 8 ? 1 << tails.Count : 1;
                        var map = new List<int>(fittedFull.Count);   // GGO index -> ours, -1 implied
                        for (int combo = 0; combo < combos; combo++)
                        {
                        map.Clear();
                        int first = 0;
                        foreach (int end in pts.EndPoints)
                        {
                            if (end < first || end >= pts.PointCount) { map.Clear(); break; }
                            for (int k = first; k <= end; k++)
                            {
                                map.Add(k);
                                int nxt = k == end ? first : k + 1;
                                if (!pts.OnCurve[k] && !pts.OnCurve[nxt]) map.Add(-1);
                            }
                            int slot = tails.IndexOf(end);
                            if (slot >= 0 && slot < 8 && (combo >> slot & 1) != 0) map.Add(first);
                            first = end + 1;
                        }
                        reconCount = map.Count;
                        if (map.Count > 0 && map.Count == fittedFull.Count)
                        {
                            // CHECK THE RECONSTRUCTION AGAINST ITSELF, not against a distance.
                            // The guard used to demand every GDI fitted point sit within 1.5px of
                            // OUR scaled unfitted point at the same index, which confuses two
                            // different things: a misaligned map, and a point the fit genuinely
                            // moved a long way. Times' '9' at 14ppem is the second -- its map is
                            // exact, every x agrees to the 64th, and it was refused because GGO's
                            // fitted y for P27 is 1.55px from its unfitted y, which is simply how
                            // far that counter's top gets snapped at 14ppem. Nine of the fifteen
                            // Times glyphs at 14ppem were being thrown away like that.
                            // <para>The insertions are exactly checkable instead. Every entry the
                            // reconstruction marks as an implied midpoint must BE the midpoint of
                            // its neighbours in GGO's own list, and every closing repeat must equal
                            // the contour's first entry exactly -- both in GDI's coordinates, with
                            // no reference to ours. A map that is off by one fails these at once,
                            // because a real point is not the average of its neighbours.</para>
                            var idx = new int[pts.PointCount];
                            for (int k = 0; k < idx.Length; k++) idx[k] = -1;
                            bool near = true;
                            for (int k = 0; k < map.Count && near; k++)
                            {
                                if (map[k] < 0)
                                {
                                    if (k == 0 || k + 1 >= map.Count) { near = false; break; }
                                    float mx = (fittedFull[k - 1].X + fittedFull[k + 1].X) / 2f;
                                    float my = (fittedFull[k - 1].Y + fittedFull[k + 1].Y) / 2f;
                                    if (Math.Abs(fittedFull[k].X - mx) > 0.02f
                                        || Math.Abs(fittedFull[k].Y - my) > 0.02f) near = false;
                                    continue;
                                }
                                if (idx[map[k]] >= 0)
                                {
                                    // a closing repeat: must match the entry it repeats, exactly
                                    int at = idx[map[k]];
                                    if (Math.Abs(fittedFull[k].X - fittedFull[at].X) > 0.02f
                                        || Math.Abs(fittedFull[k].Y - fittedFull[at].Y) > 0.02f)
                                        near = false;
                                    continue;
                                }
                                idx[map[k]] = k;
                            }
                            // and a loose sanity bound, so a map that passes the structural tests
                            // by coincidence on a glyph with few insertions is still refused
                            for (int k = 0; k < map.Count && near; k++)
                            {
                                if (map[k] < 0 || idx[map[k]] != k) continue;
                                float wantX = pts.StartX[map[k]] + shearForPair * pts.StartY[map[k]];
                                if (Math.Abs(fittedFull[k].X - wantX) > 3f
                                    || Math.Abs(-fittedFull[k].Y - pts.StartY[map[k]]) > 3f)
                                {
                                    near = false;
                                    if (Environment.GetEnvironmentVariable("WPF_GGOPTS_DUMP") == "1")
                                        Console.Error.WriteLine($"   guard fails at ggo[{k}]"
                                            + $" -> our P{map[k]}: ggo ({fittedFull[k].X:0.###},"
                                            + $"{-fittedFull[k].Y:0.###}) unfitted ({wantX:0.###},"
                                            + $"{pts.StartY[map[k]]:0.###})");
                                }
                            }
                            reconGuard = near;
                            if (near)
                            {
                                byIndex = true; onCurveOnly = false;
                                fitted = fittedFull; gdiIdx = idx;
                                break;
                            }
                        }
                        }
                    }
                    if (!byIndex && (plain.Count != fitted.Count || plain.Count == 0))
                    {
                        unpairable++;
                        if (!quiet)
                            Console.Error.WriteLine($"'{c}': unpairable, GGO segments it"
                                + $" {plain.Count} points unfitted against {fitted.Count} fitted"
                                + $", and {fittedFull.Count} fitted against our {pts.PointCount}"
                                + " points plus their implied midpoints"
                                + $" (reconstruction made {reconCount}, guard {(reconGuard ? "passed" : "FAILED")})");
                        continue;
                    }

                    // GGO reports y DOWNWARD from the baseline; the interpreter works upward.
                    int shown = 0, offX = 0, offY = 0, paired = 0;
                    bool showAll = Environment.GetEnvironmentVariable("WPF_GGOPTS_ALL") == "1";
                    double worstX = 0, worstY = 0;
                    var lines = new List<string>();
                    // A SYNTHESIZED OBLIQUE -- Tahoma has no italic face -- is hinted upright and
                    // sheared afterwards, on both sides. The interpreter's captured points are the
                    // upright ones; GDI reports the sheared outline. Shear ours the same way before
                    // pairing, or only the baseline pairs (6 of 24 points of Tahoma's 'o') and
                    // every "difference" above it is the slant itself.
                    float shear = font.ObliqueShearApplied;
                    // A RIGID TRANSLATION IN X IS A FRAME DIFFERENCE, NOT A SHAPE ONE, and this
                    // oracle was reporting one as 129 wrong points. GGO hands back the outline in
                    // the frame the glyph was DESIGNED in -- side bearing and all -- while the
                    // interpreter array we capture has already had the side-bearing snap of
                    // fsg_SimpleInnerGridFit folded into it, the block that rounds orgX[pp1] to the
                    // grid and moves every real point and phantom with it. For 41 of Times New
                    // Roman Italic's 44 glyphs that delta is zero, because hmtx's lsb equals glyf's
                    // xMin and pp1 lands on nothing; for 'c', 'j' and 'y' the two differ by four
                    // font units and the delta is 2/64 at 12-18ppem and 3/64 at 20-24, which is
                    // EXACTLY what every one of their points was reported as being out by, at every
                    // size. Our renderer puts the frame back (fs__Contour's re-anchor onto pp1),
                    // which is why the holdout does not move a single count either way.
                    // <para>So: measure the common translation, take it out, and report it
                    // separately. What is left is shape, which is what this instrument is for.
                    // WPF_GGOPTS_RIGID=0 scores the raw offsets again.</para>
                    var dxs = new List<double>(); var dys = new List<double>();
                    var pi = new List<int>(); var pj = new List<int>();
                    for (int i = 0; i < pts.PointCount; i++)
                    {
                        if (onCurveOnly && !pts.OnCurve[i]) continue;
                        int j0 = byIndex ? gdiIdx[i]
                               : Nearest(plain, pts.StartX[i] + shear * pts.StartY[i], -pts.StartY[i]);
                        if (j0 < 0 || j0 >= fitted.Count) continue;
                        pi.Add(i); pj.Add(j0);
                        dxs.Add(pts.FitX[i] + shear * pts.FitY[i] - fitted[j0].X);
                        dys.Add(pts.FitY[i] - -fitted[j0].Y);
                    }
                    // STRICTLY ALL POINTS, and the reason the weaker rule is wrong is the
                    // finding. A frame shift applied BEFORE hinting does not survive as a
                    // translation: a point the program ROUNDS lands on the same absolute grid
                    // whatever frame it started in, while an untouched point carries the shift. So
                    // a glyph whose program pins some of its points shows a MIXTURE, and taking
                    // the commonest offset out then makes the pinned points look wrong by the same
                    // amount the others were -- Times New Roman Italic 'j' goes from 31 differing
                    // to 15, and neither number means anything. Only a shift that EVERY point
                    // shares is a frame difference, and that is what this removes.
                    double tx = 0;
                    if (Environment.GetEnvironmentVariable("WPF_GGOPTS_RIGID") != "0" && dxs.Count > 0)
                    {
                        bool all = true;
                        foreach (double d in dxs) if (Math.Abs(d - dxs[0]) > 0.5 / 64) { all = false; break; }
                        if (all && Math.Abs(dxs[0]) > 1.0 / 64) tx = dxs[0];
                    }
                    for (int k = 0; k < pi.Count; k++)
                    {
                        int i = pi[k], j = pj[k];
                        paired++;
                        float gdiY = -fitted[j].Y, gdiX = fitted[j].X;
                        double dy = dys[k], dx = dxs[k] - tx;
                        bool badX = Math.Abs(dx) > 1.0 / 64, badY = Math.Abs(dy) > 1.0 / 64;
                        if (badX) { offX++; if (Math.Abs(dx) > Math.Abs(worstX)) worstX = dx; }
                        if (badY) { offY++; if (Math.Abs(dy) > Math.Abs(worstY)) worstY = dy; }
                        if ((badX || badY || showAll) && shown++ < (showAll ? 999 : 12) && !quiet)
                            lines.Add($"      pt{i,-3} {(pts.OnCurve[i] ? "on " : "off")}"
                                + $" start({pts.StartX[i]:0.00},{pts.StartY[i]:0.00})"
                                + $" ours ({pts.FitX[i] + shear * pts.FitY[i]:0.000},{pts.FitY[i]:0.000})"
                                + $" gdi ({gdiX:0.000},{gdiY:0.000})"
                                + $"  d {dx * 64:+0.0;-0.0},{dy * 64:+0.0;-0.0} /64");
                    }
                    gTotal++;
                    if (offX == 0 && offY == 0) gExact++;
                    pTotal += paired; pOffX += offX; pOffY += offY;
                    if (!quiet || offX + offY > 0 || tx != 0)
                        Console.Error.WriteLine($"'{c}' @{ppem}: {paired} of {pts.PointCount}"
                            + $" points paired, {offX} differ in x"
                            + $" (worst {worstX * 64:+0.0;-0.0}/64), {offY} in y"
                            + $" (worst {worstY * 64:+0.0;-0.0}/64)"
                            + (tx == 0 ? "" : $"   [rigid x shift {tx * 64:+0.0;-0.0}/64 removed]"));
                    foreach (string l in lines) Console.Error.WriteLine(l);
                }
                Console.Error.WriteLine($"TOTAL {parts[0]}{(styleSpec == "" ? "" : " " + styleSpec)} @{ppem}"
                    + $"{(bilevel ? " bi-level" : " ClearType")}: {gExact} of {gTotal} glyphs"
                    + $" exact; of {pTotal} points {pOffX} differ in x, {pOffY} in y"
                    + (unpairable == 0 ? "" : $"   [{unpairable} glyphs unpairable]")
                    + (notFitted == 0 ? "" : $"   [{notFitted} NOT GRID-FITTED BY US,"
                                                  + $" GDI fitted {gdiFittedAnyway} of them]"));
            }
            finally
            {
                TrueTypeInterpreter.s_capturePoints = false;
                TrueTypeFont.SubpixelFitting = saved;
                TrueTypeInterpreter.BiLevelPass = false;
            }
        }

        /// <summary>Only the points the curve passes THROUGH, in order -- the ones a grid fit
        /// cannot add or remove, so they pair by index even when the two reports differ in
        /// length.</summary>
        private static List<Vector2> FlattenOnCurve(List<PathFigure> figures)
        {
            var outp = new List<Vector2>();
            foreach (PathFigure f in figures)
            {
                outp.Add(f.Start);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: outp.Add(l.Point); break;
                        case QuadraticBezierSegment q: outp.Add(q.Point); break;
                        case CubicBezierSegment cu: outp.Add(cu.Point); break;
                    }
            }
            return outp;
        }

        /// <summary>Every point of an outline in the order it was reported, so two outlines of the
        /// same glyph can be paired by index.</summary>
        internal static List<Vector2> Flatten(List<PathFigure> figures)
        {
            var outp = new List<Vector2>();
            foreach (PathFigure f in figures)
            {
                outp.Add(f.Start);
                foreach (PathSegment seg in f.Segments)
                    foreach (Vector2 v in Points(seg)) outp.Add(v);
            }
            return outp;
        }

        /// <summary>Which reported point is the one the interpreter calls <paramref name="x"/>,
        /// <paramref name="y"/>. GGO's unfitted outline is the same numbers the interpreter starts
        /// from, so this is an identity in all but rounding -- a match further than a quarter pixel
        /// away is not a match, and saying so is what keeps a mispairing from being read as a
        /// disagreement.</summary>
        internal static int Nearest(List<Vector2> pts, float x, float y)
        {
            int best = -1;
            double bestD = 0.25;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = Math.Abs(pts[i].X - x) + Math.Abs(pts[i].Y - y);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
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

        /// <summary>The outline's AREA, in units of one pixel's worth of ink (255), got by
        /// rasterizing it eight times life size and dividing by 64.
        /// <para>This is the arbiter for stage R. For one fixed outline, total ink IS total area --
        /// for ANY rasterizer that computes coverage linearly -- so when ours and GDI's disagree by
        /// a fifth, one of them is not linear, and the outline's own area says which. Ours at 1x
        /// against ours at 8x also checks our rasterizer against itself, which costs nothing here
        /// and would catch a sampling error masquerading as a difference of opinion with GDI.</para>
        /// </summary>
        private static long OutlineArea(List<PathFigure> figures) => OutlineArea(figures, out _);

        /// <summary>Area, and the PERIMETER that comes free with it: at scale N the partially
        /// covered pixels of a shape number about perimeter x N, so counting them and dividing by N
        /// estimates it without a flattener. Wanted because if GDI renders a shape dilated by w,
        /// its extra ink is about perimeter x w -- so w falls out, and whether w is CONSTANT across
        /// sizes or grows with them is the difference between two quite different models.</summary>
        private static long OutlineArea(List<PathFigure> figures, out long perimeter)
        {
            perimeter = 0;
            if (figures.Count == 0) return 0;
            const int N = 8;
            List<PathFigure> big = Map(figures, v => new Vector2((v.X + PenX) * N, (v.Y + BaseY) * N));
            CoverageMask m = PathRasterizer.Rasterize(new PathGeometry(FillRule.NonZero, big));
            if (m.Coverage == null) return 0;
            long sum = 0, partial = 0;
            foreach (byte b in m.Coverage) { sum += b; if (b != 0 && b != 255) partial++; }
            perimeter = partial / N;
            return sum / (N * N);
        }

        private static List<PathFigure> Translate(List<PathFigure> figures, float dx, float dy)
            => Map(figures, v => new Vector2(v.X + dx, v.Y + dy));

        /// <summary>GDI's fitted outline, placed at a pen. For stage CR, which draws it with OUR

        /// <summary>OUTLINE AREA AGAINST GDI'S, for the glyphs the point oracle cannot pair.
        /// <para>FittedPoints_AgainstGdisOwn drops every glyph whose GGO point lists differ in
        /// length between the fitted and unfitted reports -- the CURVED ones, which on Times New
        /// Roman at 10-12ppem is half the face. Worse, the dropped glyphs are exactly the ones that
        /// render badly, so its "exact" verdict is measured on the easy cases.</para>
        /// <para>This compares the two fitted outlines by AREA, which needs no pairing: both are
        /// rasterized non-zero at AreaSub samples per pixel over their common bounding box and the
        /// cells covered by one and not the other are counted. Validate the coordinate frame on a
        /// glyph known to render exactly (Times 'H' at 16) before trusting a curved result.</para>
        /// <para>WPF_AREACMP=family/chars/ppem[/B|I].</para></summary>
        [Fact]
        public void OutlineAreaAgainstGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the reference");
            string? spec = Environment.GetEnvironmentVariable("WPF_AREACMP");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_AREACMP=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains("B"), italic = style.Contains("I");

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            byte[] faceBytes = File.ReadAllBytes(file!);
            FontFiles.DeclaredStyle(faceBytes, 0, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(faceBytes, bold && !fileBold, italic && !fileItalic);

            var sb = new System.Text.StringBuilder();
            // GGO's "fitted" outline is GDI's BI-LEVEL fit (see the trap note in
            // ggo-cleartype-points-are-bilevel), so ask ours for the same thing -- exactly as
            // FittedPoints_AgainstGdisOwn does. Comparing our ClearType outline against GDI's bi-level
            // one measures the difference between two different questions.
            bool savedSub = TrueTypeFont.SubpixelFitting;
            bool savedBi = TrueTypeInterpreter.BiLevelPass;
            TrueTypeFont.SubpixelFitting = false;
            TrueTypeInterpreter.BiLevelPass = Environment.GetEnvironmentVariable("WPF_AREACMP_CT") != "1";
            try {
            sb.AppendLine("== " + parts[0] + " @" + ppem + " " + style + "  outline area vs GDI's");
            sb.AppendLine("   ch   symDiff(px)   pct of GDI area    ourArea   gdiArea");
            double totDiff = 0, totGdi = 0;
            string chars = parts[1] == "*"
                ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" : parts[1];
            foreach (char c in chars)
            {
                if (!((IHintedGlyphFont) font).TryGetHintedOutline(font.GlyphIndex(c), ppem,
                        out List<PathFigure>? ours) || ours is null) continue;
                List<PathFigure> gdi = GdiOutline(c, parts[0], ppem, unhinted: false,
                                                  bold: bold, italic: italic);
                List<List<Vector2>> op = AreaPolys(ours, 1f), gp = AreaPolys(gdi, 1f);   // both already y-up-negative
                if (op.Count == 0 || gp.Count == 0) continue;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (List<List<Vector2>> set in new[] { op, gp })
                    foreach (List<Vector2> p in set)
                        foreach (Vector2 v in p)
                        {
                            if (v.X < x0) x0 = v.X;
                            if (v.Y < y0) y0 = v.Y;
                            if (v.X > x1) x1 = v.X;
                            if (v.Y > y1) y1 = v.Y;
                        }
                if (Environment.GetEnvironmentVariable("WPF_AREACMP_BBOX") == "1")
                {
                    static (float,float,float,float) BB(List<List<Vector2>> s) {
                        float a=float.MaxValue,b=float.MaxValue,cc=float.MinValue,d=float.MinValue;
                        foreach (var q in s) foreach (Vector2 v in q) { if(v.X<a)a=v.X; if(v.Y<b)b=v.Y; if(v.X>cc)cc=v.X; if(v.Y>d)d=v.Y; }
                        return (a,b,cc,d); }
                    var ob = BB(op); var gb = BB(gp);
                    sb.AppendLine("   " + c + "  ours bbox x[" + ob.Item1.ToString("0.00") + "," + ob.Item3.ToString("0.00")
                        + "] y[" + ob.Item2.ToString("0.00") + "," + ob.Item4.ToString("0.00") + "]   gdi x["
                        + gb.Item1.ToString("0.00") + "," + gb.Item3.ToString("0.00") + "] y[" + gb.Item2.ToString("0.00")
                        + "," + gb.Item4.ToString("0.00") + "]");
                }
                x0 -= 1; y0 -= 1; x1 += 1; y1 += 1;
                int w = (int) MathF.Ceiling((x1 - x0) * AreaSub);
                int h = (int) MathF.Ceiling((y1 - y0) * AreaSub);
                if (w <= 0 || h <= 0 || (long) w * h > 40000000) continue;
                bool[] a = AreaFill(op, x0, y0, w, h), b2 = AreaFill(gp, x0, y0, w, h);
                long diff = 0, ourA = 0, gdiA = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i]) ourA++;
                    if (b2[i]) gdiA++;
                    if (a[i] != b2[i]) diff++;
                }
                double cell = 1.0 / (AreaSub * AreaSub);
                totDiff += diff * cell; totGdi += gdiA * cell;
                sb.AppendLine("   " + c + "    " + (diff * cell).ToString("0.000").PadLeft(9)
                    + "      " + (gdiA > 0 ? 100.0 * diff / gdiA : 0).ToString("0.00").PadLeft(7) + "%"
                    + "    " + (ourA * cell).ToString("0.00").PadLeft(8)
                    + "  " + (gdiA * cell).ToString("0.00").PadLeft(8));
            }
            sb.AppendLine("   TOTAL symmetric difference " + totDiff.ToString("0.00")
                + " px against GDI area " + totGdi.ToString("0.00") + " px  ("
                + (totGdi > 0 ? 100 * totDiff / totGdi : 0).ToString("0.00") + "%)");
            }
            finally { TrueTypeFont.SubpixelFitting = savedSub; TrueTypeInterpreter.BiLevelPass = savedBi; }
            Console.Error.Write(sb.ToString());
        }

        private const int AreaSub = 8;

        private static List<List<Vector2>> AreaPolys(List<PathFigure> figs, float ySign)
        {
            var outp = new List<List<Vector2>>();
            foreach (PathFigure f in figs)
            {
                var pts = new List<Vector2>();
                Vector2 cur = f.Start;
                pts.Add(new Vector2(cur.X, ySign * cur.Y));
                foreach (PathSegment sg in f.Segments)
                {
                    if (sg is LineSegment l)
                    {
                        cur = l.Point;
                        pts.Add(new Vector2(cur.X, ySign * cur.Y));
                    }
                    else if (sg is QuadraticBezierSegment q)
                    {
                        for (int k = 1; k <= 8; k++)
                        {
                            float t = k / 8f, u = 1 - t;
                            Vector2 p = u * u * cur + 2 * u * t * q.Control + t * t * q.Point;
                            pts.Add(new Vector2(p.X, ySign * p.Y));
                        }
                        cur = q.Point;
                    }
                    else if (sg is CubicBezierSegment c3)
                    {
                        for (int k = 1; k <= 8; k++)
                        {
                            float t = k / 8f, u = 1 - t;
                            Vector2 p = u * u * u * cur + 3 * u * u * t * c3.Control1
                                      + 3 * u * t * t * c3.Control2 + t * t * t * c3.Point;
                            pts.Add(new Vector2(p.X, ySign * p.Y));
                        }
                        cur = c3.Point;
                    }
                }
                if (pts.Count >= 3) outp.Add(pts);
            }
            return outp;
        }

        private static bool[] AreaFill(List<List<Vector2>> polys, float x0, float y0, int w, int h)
        {
            var grid = new bool[w * h];
            var xs = new List<(float X, int D)>();
            for (int r = 0; r < h; r++)
            {
                float sy = y0 + (r + 0.5f) / AreaSub;
                xs.Clear();
                foreach (List<Vector2> p in polys)
                    for (int i = 0; i < p.Count; i++)
                    {
                        Vector2 a = p[i], b = p[(i + 1) % p.Count];
                        if (a.Y == b.Y) continue;
                        if (sy < MathF.Min(a.Y, b.Y) || sy >= MathF.Max(a.Y, b.Y)) continue;
                        float t = (sy - a.Y) / (b.Y - a.Y);
                        xs.Add((a.X + t * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                    }
                xs.Sort((u, v) => u.X.CompareTo(v.X));
                int wind = 0;
                for (int i = 0; i + 1 < xs.Count; i++)
                {
                    wind += xs[i].D;
                    if (wind == 0) continue;
                    int cA = (int) MathF.Ceiling((xs[i].X - x0) * AreaSub - 0.5f);
                    int cB = (int) MathF.Ceiling((xs[i + 1].X - x0) * AreaSub - 0.5f);
                    if (cA < 0) cA = 0;
                    if (cB > w) cB = w;
                    for (int cc = cA; cc < cB; cc++) grid[r * w + cc] = true;
                }
            }
            return grid;
        }
        /// ClearType pipeline and needs it in the same cell GDI drew into.</summary>
        internal static List<PathFigure> GdiOutlineAt(char c, string family, int ppem, float dx, float dy)
            => Map(GdiOutline(c, family, ppem, unhinted: false), v => new Vector2(v.X + dx, v.Y + dy));

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
            // family/char/ppem, optionally /b for bold. The weight is not decoration: the
            // half-pixel widening this dump measured off the REGULAR face does not hold for bold
            // (see WPF_STEM_NATURAL's numbers), so a width rule has to be read at both weights.
            string[] parts = spec!.Split('/');
            bool bold = parts.Length > 3 && parts[3].StartsWith("b");
            string? file = FontFiles.Find(parts[0], bold: bold, italic: false);
            Assert.SkipWhen(file is null, $"this machine has no {parts[0]}" + (bold ? " Bold" : ""));

            var font = new TrueTypeFont(File.ReadAllBytes(file!));
            char c = parts[1][0];
            int ppem = int.Parse(parts[2]);

            bool saved = TrueTypeFont.SubpixelFitting;
            try
            {
                TrueTypeFont.SubpixelFitting = false;
                byte[] mine = OurGray(font, c, ppem, unhinted: false);
                byte[] theirs = GdiGray(c, parts[0], ppem, unhinted: false, bold: bold);
                Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  fitted (x+y), ours | GDI ===");
                Dump(mine, theirs);

                // And the same glyph as stage R sees it: GDI's OWN fitted outline drawn by our
                // rasterizer, beside GDI drawing it. The geometry is identical in this pair, so
                // anything that differs is the rasterizer and nothing else -- which is the only way
                // to look at the largest single term in the text disagreement.
                Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  GDI's fitted outline: "
                    + "our rasterizer | GDI's ===");
                byte[] gdiOutlineOurs = RasterizeIntoCell(GdiOutline(c, parts[0], ppem, unhinted: false, bold: bold));
                Dump(gdiOutlineOurs, theirs);

                // WHICH SIDE gains the ink. The aggregate says GDI renders a shape fatter than the
                // outline it reports, and that the dilation and the centroid shift are the same
                // number -- which would mean the extra ink is all on one side. On a single vertical
                // stem that is not an inference: the column profile shows it.
                // THREE profiles, because two cannot answer the question they raise. GDI's
                // greyscale stem is wider than its own hinted outline's by a quantised fraction
                // that grows with size -- 0.25px at 9-11 up to 1.00 at 18 -- and expressed as a
                // width that is about 0.115 em at EVERY size, which is what an UNHINTED stem is.
                // So the third column here is the unhinted outline. GDI's stem does NOT match it --
                // it is wider than BOTH the hinted and the unhinted outline -- and that is the
                // answer. Segoe UI 'l', all three widths in pixels, our hinted stem being exactly
                // 1.0 at every size:
                //
                //   ppem      9    10    11    12    13    14    15    16    17    18
                //   GDI    1.25  1.25  1.25  1.50  1.50  1.62  1.75  1.75  1.87  2.00
                //   unhtd  0.70  0.85  0.90  0.97  1.02  1.14  1.20  1.25  1.30  1.37
                //   diff  +0.55 +0.39 +0.35 +0.53 +0.48 +0.49 +0.55 +0.50 +0.57 +0.63
                //
                // GDI'S GREYSCALE STEM IS THE NATURAL WIDTH PLUS ABOUT HALF A PIXEL, at every size,
                // then quantised to an eighth -- which is what the 0.35-0.63 spread is. It is not a
                // whole-pixel snap (our hinted 1.00 is), not the natural width (that is the row
                // above), and not a rasterizer or a contrast curve. It is stem darkening, and half
                // a pixel is a much larger and much simpler number than the tuned +6/64 the
                // interpreter currently applies over ppem 11-13 only.
                byte[] unhintedOurs = RasterizeIntoCell(GdiOutline(c, parts[0], ppem, unhinted: true, bold: bold));
                // OURS is the fourth column and the only one that says what to DO. The other three
                // describe GDI; this one is the geometry we actually ship, so the correction any
                // fix has to apply is (GDI - ours) and not (GDI - anything of GDI's).
                Console.Error.WriteLine($"=== {parts[0]} '{c}' @{ppem}  column ink: "
                    + "GDI-hinted(ours) | GDI bitmap | GDI-UNhinted(ours) | OURS fitted ===");
                for (int x = 0; x < Cell; x++)
                {
                    long a = 0, b = 0, u = 0, o2 = 0;
                    for (int y = 0; y < Cell; y++)
                    {
                        a += gdiOutlineOurs[y * Cell + x];
                        b += theirs[y * Cell + x];
                        u += unhintedOurs[y * Cell + x];
                        o2 += mine[y * Cell + x];
                    }
                    if (a == 0 && b == 0 && u == 0 && o2 == 0) continue;
                    Console.Error.WriteLine($"  x={x,3}  hinted {a,6}   GDI {b,6}"
                        + $"   unhinted {u,6}   ours {o2,6}");
                }
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
            // READ THE INK COLUMN WITH CARE ON B AND Y, and not at all as a statement about
            // fitting. Stage A is rasterizer-neutral BY CONSTRUCTION -- GDI's outline and ours both
            // go through OUR rasterizer, so the rasterizer cancels and what is left is geometry.
            // B and Y are not: they put our fitted outline through our rasterizer and compare it
            // against GDI's OWN GRAY8 bitmap, so a difference there is fitting AND rasterizer
            // together, with no way to say which.
            // <para>It matters because the two disagree in SIGN. B says our fitted geometry carries
            // 0.94/0.85/0.79 of GDI's ink at 11/12/13 -- badly light. Stage D, which reads GDI's
            // FITTED outline out through a MAT2 and runs it through our rasterizer, says the
            // opposite: 1.08/1.07/1.04, ours slightly HEAVY. D isolates the geometry and B does
            // not, so D is the one to believe, and the deficit B reports belongs to GDI's
            // greyscale rasterizer rather than to our fitting. (GRAY8 is normalised correctly here,
            // v * 255 / 64 -- that was checked before concluding it.)</para>
            // <para>What is still missing is a stage that puts ONE outline through both
            // rasterizers. B minus D is the nearest thing to it and it is large.</para>

            // 7 and 8 sit below Segoe UI's gasp gridfit threshold, where GDI is expected NOT to
            // fit -- which stage B can confirm rather than leave assumed.
            foreach (int ppem in new[] { 7, 8, 11, 12, 13, 16, 19 })
            {
                long bPixels = -1, bTotal = -1;
                // R is the stage that was missing: ONE outline through BOTH rasterizers. Every
                // other row here changes the geometry and the rasterizer together or holds the
                // rasterizer fixed, so nothing said how far apart the two RASTERIZERS are -- and
                // B minus D implied a lot. R settles it by giving both sides GDI's own fitted
                // outline: ours draws it, and GDI's GRAY8 bitmap of the same glyph IS GDI drawing
                // it. Whatever differs is the rasterizer alone.
                // <para>THAT PREMISE IS FALSE, and the column profile of a single stem is what
                // showed it. Segoe UI 'l' at 12ppem, GDI's own outline drawn both ways:</para>
                // <code>  x=21  ours 2295  GDI 2295     0
                //         x=22  ours    0  GDI 1143  +1143</code>
                // <para>One column identical to the digit, and then GDI puts half a column of ink
                // where the outline has none. Across sizes the extra is 0.25, 0.50, 0.50, 0.75 of
                // a column at 11, 12, 13, 16 -- QUARTER PIXELS, on the right of every stem ('H'
                // gains on both of its). So GGO_NATIVE's hinted outline is NOT the geometry GDI
                // rasterizes for GGO_GRAY8_BITMAP: the bitmap comes from a hinting pass whose stems
                // are quarter-pixel wider. R therefore compares two GEOMETRIES, not two
                // rasterizers, and its ink ratios (0.87/0.80/0.76) measure that difference.</para>
                // <para>It still earns its place: A says our outline is right, the area arbiter
                // says our rasterizer is exact to 0.999, and R says GDI draws something wider than
                // it will hand over. What it cannot do is isolate a rasterizer.</para>
                // <para>READ R'S TREND WITH CAUTION. Segoe UI gives 0.87, 0.80, 0.76 at 11-13,
                // then 0.72 at 16 and 0.95 at 19 -- a swing between adjacent sizes far too large
                // for a property of a rasterizer, so something size-specific is in it that has not
                // been found yet (19's ourInk is nearly double 16's for a 19% size increase, and
                // stage D's gdi1x column carries the same oddity). The 11-13 numbers are steady
                // and the 7-8 agreement is solid; treat 16 and 19 as unexplained rather than as
                // measurements.</para>
                // U WAS MEANT TO BE R'S CONTROL and CANNOT BE, which is worth keeping rather than
                // deleting. R agrees at 7 and 8 and diverges from 11 up, but those are also the
                // sizes below Segoe UI's gasp threshold, so "unhinted" and "small" are confounded
                // in it. U tried to separate them by running the same both-rasterizers comparison
                // on the UNHINTED outline at every size -- GdiGray(unhinted: true) against our
                // raster of GDI's unhinted outline.
                // <para>GDI IGNORES GGO_UNHINTED FOR GGO_GRAY8_BITMAP. The proof is in the report:
                // U's gdiInk column came back byte-identical to R's at all seven sizes. So U
                // compared our UNHINTED outline against GDI's HINTED bitmap -- geometry and
                // rasterizer mixed again, which is the very thing R exists to avoid, and its
                // numbers (0.76, 0.77, 0.76, 0.90, 0.88) mean nothing. GGO_UNHINTED works for the
                // outline formats, which is why stage A can use it and this cannot.</para>
                foreach (string stage in new[] { "A", "B", "Y", "R" })
                {
                    bool unhinted = stage == "A";
                    // Stage Y fits the outline the way ClearType asks for it -- y only, x left
                    // where the scaled outline puts it. Without this the decomposition has a hole
                    // in it: stage B fits BOTH axes, so everything between B and C could be the
                    // lamps or could be the fitting mode, and there would be no way to tell.
                    TrueTypeFont.SubpixelFitting = stage == "Y";
                    TrueTypeFont.ForceYOnlyFit = stage == "Y";
                    int pixels = 0;
                    long total = 0, ourInk = 0, theirInk = 0, area = 0, perim = 0;
                    // GDI'S TRANSFER CURVE, read off rather than guessed at. Stage R's two cells
                    // hold the SAME outline, so pairing them pixel by pixel gives (our exact area
                    // coverage) -> (what GDI put there). Binning that IS the curve GDI applies on
                    // top of coverage -- the thing the 1.15-1.39 ink ratio is the integral of, and
                    // the last unmeasured term between our text and GDI's.
                    var curveSum = new long[17];
                    var curveN = new long[17];
                    // WHERE the ink is, not just how much. The curve's bottom bin says GDI inks
                    // pixels our coverage leaves empty, and that has two possible causes with
                    // opposite fixes: GDI's glyph is FATTER than the outline it reports, or it is in
                    // a different PLACE. Ink centroids separate them in one line.
                    double mx = 0, my = 0, gx = 0, gy = 0;
                    // WHICH letters, not just how many pixels. An aggregate says a face is wrong at a
                    // size; the names say which glyph's program to step through, which is the only
                    // thing that leads anywhere.
                    var worst = new List<(int Pixels, char Ch)>();
                    foreach (char c in Repertoire)
                    {
                        byte[] mine = stage switch
                        {
                            "R" => RasterizeIntoCell(GdiOutline(c, family, ppem, unhinted: false)),
                            "A" => RasterizeIntoCell(OurOutline(font, c, ppem)),
                            _   => OurGray(font, c, ppem, unhinted: false),
                        };
                        byte[] theirs = stage switch
                        {
                            "A" => RasterizeIntoCell(GdiOutline(c, family, ppem, unhinted: true)),
                            _   => GdiGray(c, family, ppem, unhinted: false),
                        };
                        var (p, t, o, th) = Compare(mine, theirs);
                        pixels += p; total += t; ourInk += o; theirInk += th;
                        if (stage == "R")
                        {
                            area += OutlineArea(GdiOutline(c, family, ppem, unhinted: false),
                                                out long per);
                            perim += per;
                            for (int k = 0; k < mine.Length; k++)
                            {
                                if (mine[k] == 0 && theirs[k] == 0) continue;
                                int bin = mine[k] * 16 / 255;
                                curveSum[bin] += theirs[k];
                                curveN[bin]++;
                                mx += mine[k] * (k % Cell); my += mine[k] * (k / Cell);
                                gx += theirs[k] * (k % Cell); gy += theirs[k] * (k / Cell);
                            }
                        }
                        if (p > 0) worst.Add((p, c));
                    }
                    // A STAGE THAT COINCIDES WITH ANOTHER MUST SAY SO, because two identical rows
                    // under different names read as "the fitting mode makes no difference" whether
                    // that is a result or a dead knob. It was a dead knob: SubpixelFitting alone
                    // never captured plainX at any XHintMode this ships with, so Y fitted exactly
                    // as B did at every size. ForceYOnlyFit fixes that and the two now separate
                    // where there is anything to separate.
                    // <para>They still coincide at 7 and 8, and that IS a result: those sizes are
                    // below Segoe UI's gasp gridfit threshold, the program moves no x, so both
                    // fittings land in the same place. The note says which of the two it is by
                    // saying nothing about causes it cannot check.</para>
                    if (stage == "B") { bPixels = pixels; bTotal = total; }
                    else if (stage == "Y" && pixels == bPixels && total == bTotal)
                        report.AppendLine($"  {ppem,2}  Y y-only     coincides with B exactly -- "
                            + "the fitting moved no x at this size.");

                    string label = stage switch
                    {
                        "A" => "A unhinted",
                        "B" => "B hinted  ",
                        "R" => "R raster  ",
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
                        + (stage == "R" && area > 0
                            ? $"  AREA={area} (ours/area={(double)ourInk / area:0.000}"
                              + $" gdi/area={(double)theirInk / area:0.000}"
                              + $" dilate={(perim == 0 ? 0 : (theirInk - area) / 255.0 / perim):0.0000}px)"
                            : "")
                        + $"  worst:{names}");

                    if (stage == "R")
                    {
                        report.AppendLine($"      centroid @{ppem}: ours "
                            + $"({(ourInk == 0 ? 0 : mx / ourInk):0.000},{(ourInk == 0 ? 0 : my / ourInk):0.000})"
                            + $"  GDI ({(theirInk == 0 ? 0 : gx / theirInk):0.000},{(theirInk == 0 ? 0 : gy / theirInk):0.000})"
                            // 0.000, not "+0.000": in a .NET CUSTOM format string the plus is a
                            // LITERAL, so every negative value printed as "-+0.105".
                            + $"  d=({(ourInk == 0 || theirInk == 0 ? 0 : mx / ourInk - gx / theirInk):0.000},"
                            + $"{(ourInk == 0 || theirInk == 0 ? 0 : my / ourInk - gy / theirInk):0.000})");
                        var curve = new System.Text.StringBuilder($"      curve @{ppem}:");
                        for (int b = 0; b <= 16; b++)
                            curve.Append(curveN[b] == 0 ? "    ." 
                                : $" {b * 255 / 16,3}->{curveSum[b] / curveN[b],3}");
                        report.AppendLine(curve.ToString());
                    }
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
