// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace WgpuInterop.GdiFitProbe
{
    /// <summary>ONE GLYPH, OUR FIT BESIDE GDI'S PIXELS, IN A PROCESS SMALL ENOUGH TO DEBUG.
    /// <para>The parity suite answers "how far apart are we" over thousands of glyphs. This answers
    /// "what exactly did each side do to THIS one", in a process that starts in milliseconds, has
    /// no test host and contains no other glyph -- so a breakpoint in the interpreter stops on the
    /// glyph you asked for, first time, with nothing else on the stack.</para>
    /// <para>It drives the same <see cref="IHintedGlyphFont.TryGetHintedOutline"/> the renderer
    /// does rather than reaching past it, because a probe that fits differently from the shipping
    /// pipeline proves nothing about the shipping pipeline. <c>--check</c> asserts that against a
    /// known-good edge list taken from the parity suite's own solver.</para>
    /// <para>Usage: <c>GdiFitProbe &lt;family&gt; &lt;char&gt; &lt;ppem&gt; [B|I|BI] [--check a,b,c]</c></para></summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: GdiFitProbe <family> <char> <ppem> [B|I|BI] [--check e1,e2,...]");
                Console.Error.WriteLine("   eg: GdiFitProbe \"Segoe UI\" I 11");
                return 2;
            }

            string family = args[0];
            char ch = args[1][0];
            int ppem = int.Parse(args[2], CultureInfo.InvariantCulture);
            string style = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
                ? args[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            // The interpreter's own per-instruction dump, for this glyph and no other. It is an
            // internal switch because nothing outside the assembly has any business setting it --
            // but that is exactly what this probe is, and one glyph's trace is readable where the
            // suite's would be megabytes.
            bool trace = Array.IndexOf(args, "--trace") >= 0;

            int[]? expect = null;
            for (int i = 3; i < args.Length - 1; i++)
                if (args[i] == "--check")
                    expect = Array.ConvertAll(args[i + 1].Split(','), s => int.Parse(s, CultureInfo.InvariantCulture));

            string? file = FontFiles.Find(family, bold, italic);
            if (file is null) { Console.Error.WriteLine($"no file for {family}"); return 3; }
            byte[] bytes = File.ReadAllBytes(file);
            int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
            FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);

            // The SAME state the renderer sets. ClearTypeRendering is what the face is TOLD through
            // GETINFO; SubpixelFitting is whether x is fitted on the lamp grid. The two are separate
            // on purpose and both matter -- fitting subpixel while claiming greyscale fits the glyph
            // for a rasterizer that is not running.
            TrueTypeFont.SubpixelFitting = true;
            TrueTypeFont.ClearTypeRendering = true;

            var font = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
            int gid = font.GlyphIndex(ch);
            if (gid <= 0) { Console.Error.WriteLine($"{family} has no '{ch}'"); return 4; }

            Console.WriteLine($"== {family}{(bold ? " Bold" : "")}{(italic ? " Italic" : "")} '{ch}' @{ppem}");
            Console.WriteLine($"   file  {Path.GetFileName(file)}  gid {gid}  unitsPerEm {font.PixelsPerEm}");

            TrueTypeInterpreter.s_dumpGlyph = trace;
            if (!((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out List<PathFigure> fitted)
                || fitted.Count == 0)
            {
                Console.Error.WriteLine("   not fitted (blank glyph or unmeasurable face)");
                return 5;
            }

            TrueTypeInterpreter.s_dumpGlyph = false;

            int[] edges = Edges(fitted);
            Console.WriteLine("   OUR FITTED EDGES (64ths): " + string.Join(" ", edges));
            Console.WriteLine("   OUR FITTED EDGES (px):    "
                + string.Join(" ", Array.ConvertAll(edges, e => (e / 64.0).ToString("0.000", CultureInfo.InvariantCulture))));

            DumpPoints(fitted);
            DumpOurLamps(fitted);
            DumpGdi(family, ch, ppem, bold, italic);

            if (expect is not null)
            {
                bool same = expect.Length == edges.Length;
                for (int i = 0; same && i < expect.Length; i++) same = expect[i] == edges[i];
                Console.WriteLine(same
                    ? "   CHECK OK: the probe fits exactly as the shipping pipeline does."
                    : "   CHECK FAILED: expected " + string.Join(" ", expect));
                return same ? 0 : 1;
            }
            return 0;
        }

        /// <summary>Every distinct x the fitted outline touches, in 64ths -- the same grouping the
        /// parity suite's edge solver reports, so the two can be compared directly.</summary>
        private static int[] Edges(List<PathFigure> figures)
        {
            var keys = new SortedSet<int>();
            void See(Vector2 p) => keys.Add((int) MathF.Round(p.X * 64f));
            foreach (PathFigure f in figures)
            {
                See(f.Start);
                foreach (PathSegment sg in f.Segments)
                    if (sg is LineSegment ls) See(ls.Point);
                    else if (sg is QuadraticBezierSegment qs) { See(qs.Control); See(qs.Point); }
                    else if (sg is CubicBezierSegment cs) { See(cs.Control1); See(cs.Control2); See(cs.Point); }
            }
            var a = new int[keys.Count];
            keys.CopyTo(a);
            return a;
        }

        /// <summary>OUR lamps for the same glyph, and the set of distinct values in them.
        /// <para>GDI's ClearType output holds exactly SEVEN levels -- 0, 58, 102, 144, 182, 219 and
        /// paper -- because each lamp carries one of {0, 1/2, 1} and a three-tap box of three-valued
        /// lamps can only produce seven.</para>
        /// <para>DO NOT READ THE TWO LADDERS AGAINST EACH OTHER. What RasterizeSubpixel returns is
        /// the RAW FILTERED COVERAGE; the contrast curve is applied afterwards by the renderer
        /// (PathRasterizer.PreFilterLut is null here, so nothing has corrected these). GDI's seven
        /// are FINAL pixels, curve included. The counts are comparable, the values are not, and a
        /// difference between the two lists is not by itself evidence of anything.</para></summary>
        private static void DumpOurLamps(List<PathFigure> figures)
        {
            var moved = new List<PathFigure>(figures);
            PathRasterizer.SubpixelMask m =
                PathRasterizer.RasterizeSubpixel(new PathGeometry(FillRule.NonZero, moved));
            if (m.IsEmpty) { Console.WriteLine("   OUR LAMPS: (empty)"); return; }

            var seen = new SortedSet<int>();
            for (int i = 0; i < m.Rgba.Length; i++) seen.Add(m.Rgba[i]);
            Console.WriteLine($"   OUR LAMPS: {m.Width}x{m.Height} at ({m.OriginX},{m.OriginY}), "
                + $"{seen.Count} distinct lamp values");
            var sb = new StringBuilder("     values: ");
            int n = 0;
            foreach (int v in seen)
            {
                if (n++ == 24) { sb.Append("... "); break; }
                sb.Append(v).Append(' ');
            }
            Console.WriteLine(sb.ToString());
            Console.WriteLine("     (raw coverage, pre-contrast-curve -- NOT the same space as"
                + " GDI's final 0 58 102 144 182 219 255)");
        }

        private static void DumpPoints(List<PathFigure> figures)
        {
            Console.WriteLine("   OUR FITTED POINTS (64ths, contour by contour):");
            int c = 0;
            foreach (PathFigure f in figures)
            {
                var sb = new StringBuilder($"     [{c++}] ");
                void Put(Vector2 p) => sb.Append('(')
                    .Append((int) MathF.Round(p.X * 64f)).Append(',')
                    .Append((int) MathF.Round(p.Y * 64f)).Append(") ");
                Put(f.Start);
                foreach (PathSegment sg in f.Segments)
                    if (sg is LineSegment ls) Put(ls.Point);
                    else if (sg is QuadraticBezierSegment qs) { Put(qs.Control); Put(qs.Point); }
                    else if (sg is CubicBezierSegment cs) { Put(cs.Control1); Put(cs.Control2); Put(cs.Point); }
                Console.WriteLine(sb.ToString());
            }
        }

        // ---- GDI, set up exactly as the parity suite sets it up -------------------------------

        private const int ClearTypeQuality = 5;
        private const int TaBaseline = 24, TaLeft = 0;
        private const int Transparent = 1;

        private static void DumpGdi(string family, char ch, int ppem, bool bold, bool italic)
        {
            const int W = 48, H = 40, PenX = 8, Baseline = 28;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = W,
                biHeight = -H,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            IntPtr dib = CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            { Console.Error.WriteLine("   GDI would not give us a bitmap"); return; }

            var paper = new byte[W * H * 4];
            for (int i = 0; i < paper.Length; i++) paper[i] = 0xFF;
            Marshal.Copy(paper, 0, bits, paper.Length);

            var lf = new LOGFONTW
            {
                lfHeight = -ppem,
                lfWeight = bold ? 700 : 400,
                lfItalic = (byte) (italic ? 1 : 0),
                lfCharSet = 1,
                lfQuality = ClearTypeQuality,
                lfFaceName = family,
            };
            IntPtr font = CreateFontIndirectW(ref lf);
            SelectObject(dc, dib);
            SelectObject(dc, font);
            SetTextColor(dc, 0);
            SetBkMode(dc, Transparent);
            SetTextAlign(dc, TaBaseline | TaLeft);
            ExtTextOutW(dc, PenX, Baseline, 0, IntPtr.Zero, ch.ToString(), 1, IntPtr.Zero);
            GdiFlush();

            var px = new byte[W * H * 4];
            Marshal.Copy(bits, px, 0, px.Length);

            // The three lamps of each pixel, as GDI laid them down. A DIB is BGRA, and on an
            // RGB-striped display ClearType's LEFTMOST lamp is RED -- so the left lamp is byte 2
            // and the right lamp is byte 0, not the other way round. Getting this backwards is the
            // instrument bug the parity suite's own diagonal probe already caught once (it
            // compared GDI's red against our blue and called upright bars a diagonal problem), so
            // the columns below are printed explicitly left to right.
            Console.WriteLine($"   GDI CLEARTYPE PIXELS (pen {PenX}, baseline {Baseline}), '.' = paper:");
            int top = H, bot = -1, left = W, right = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 4;
                    if (px[o] != 0xFF || px[o + 1] != 0xFF || px[o + 2] != 0xFF)
                    { if (y < top) top = y; if (y > bot) bot = y; if (x < left) left = x; if (x > right) right = x; }
                }
            if (bot < 0) { Console.WriteLine("     (no ink)"); DeleteDC(dc); return; }

            for (int y = top; y <= bot; y++)
            {
                var sb = new StringBuilder($"     y{y,2} ");
                for (int x = left; x <= right; x++)
                {
                    int o = (y * W + x) * 4;
                    int v = (px[o] + px[o + 1] + px[o + 2]) / 3;
                    sb.Append(v == 255 ? '.' : v > 200 ? ':' : v > 150 ? '+' : v > 90 ? '*' : v > 40 ? '#' : '@');
                }
                Console.WriteLine(sb.ToString());
            }
            Console.WriteLine($"     columns {left}..{right} (pen at {PenX}), rows {top}..{bot}");
            Console.WriteLine("     per-column lamp triples (LEFT MID RIGHT, i.e. R G B), ink rows only:");
            for (int x = left; x <= right; x++)
            {
                var sb = new StringBuilder($"     x{x,2} ");
                for (int y = top; y <= bot; y++)
                {
                    int o = (y * W + x) * 4;
                    if (px[o] == 0xFF && px[o + 1] == 0xFF && px[o + 2] == 0xFF) continue;
                    sb.Append($"y{y}:{px[o + 2],3},{px[o + 1],3},{px[o],3}  ");
                }
                Console.WriteLine(sb.ToString());
            }
            DeleteDC(dc);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight;
            public short biPlanes; public short biBitCount; public int biCompression;
            public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONTW
        {
            public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
            public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision,
                        lfClipPrecision, lfQuality, lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc,
            ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontIndirectW(ref LOGFONTW lf);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int c);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern int SetTextAlign(IntPtr hdc, int mode);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options,
            IntPtr rect, string str, uint count, IntPtr dx);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
