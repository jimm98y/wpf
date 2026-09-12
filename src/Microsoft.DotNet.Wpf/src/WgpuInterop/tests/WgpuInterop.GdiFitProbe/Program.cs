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

            s_fittedForLearn = fitted;
            // The advance the phase pass divides by, and the hdmx the face ships.
            try {
                var mi = typeof(TrueTypeFont).GetMethod("CompatibleAdvance",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mi is not null)
                {
                    object? r = mi.Invoke(font, new object[] { gid, (float)ppem, ppem });
                    Console.WriteLine($"   COMPATIBLE ADVANCE: {r} px  (= {(float)(r ?? 0f) * 64f} in 64ths)");
                }
            } catch (Exception ex) { Console.WriteLine("   advance probe failed: " + ex.Message); }
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
        // ---- GDI ClearType pipeline learner ---------------------------------------------
        // The filter in fontdrvhost (ulClearTypeFilter_6x1 @ +0x215f8) is NOT a kernel. It
        // rasterizes BI-LEVEL at 6x, packs each pixel's three lamp counts (0..2 lit samples)
        // into a byte, and maps FIVE consecutive lamp counts -- neighbour's right lamp, own
        // three, neighbour's left lamp -- through a 243-entry table into a PALETTE INDEX
        // 0..114. The palette is the enumeration of reachable filtered triples. This learner
        // recovers the palette empirically: compute each pixel's index from the outline,
        // pair it with the triple GDI actually painted, and append the pairs to a file.
        private static void LearnPairs(List<PathFigure> fitted, byte[] px, int W, int H,
                                       string outFile, string tag)
        {
            const int PenX = 8, Baseline = 28;
            var polys = FlattenAll(fitted);
            if (polys.Count == 0) return;
            // GDI hints x in the 6x overscaled space, so every fitted x lands on an exact
            // 1/6-pixel lamp boundary. Our fitted outline is float pixel-space; snap x to the
            // 1/6 grid so the bi-level scan crosses lamps exactly where GDI's does.
            if (Environment.GetEnvironmentVariable("WPF_GDIPIPE_SNAP") == "1")
                foreach (var poly in polys)
                    for (int i = 0; i < poly.Count; i++)
                        poly[i] = new Vector2(MathF.Round(poly[i].X * 6f) / 6f, poly[i].Y);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var poly in polys) foreach (var pt in poly)
            { minX = MathF.Min(minX, pt.X); maxX = MathF.Max(maxX, pt.X);
              minY = MathF.Min(minY, pt.Y); maxY = MathF.Max(maxY, pt.Y); }
            int gy0 = (int) MathF.Floor(minY) - 1, gy1 = (int) MathF.Ceiling(maxY) + 1;
            int gx0 = (int) MathF.Floor(minX) - 2, gx1 = (int) MathF.Ceiling(maxX) + 2;

            var sb = new StringBuilder();
            var spans = new List<(float A, float B)>();
            for (int gy = gy0; gy <= gy1; gy++)
            {
                int row = Baseline + gy;
                if (row < 0 || row >= H) continue;
                float sy = gy + 0.5f;
                Spans(polys, sy, spans);

                // lamp counts for gx0-1 .. gx1+1 so every pixel sees its neighbours
                int n = gx1 - gx0 + 3;
                var lamp = new int[n, 3];
                for (int gx = gx0 - 1; gx <= gx1 + 1; gx++)
                    for (int lampI = 0; lampI < 3; lampI++)
                        for (int half = 0; half < 2; half++)
                        {
                            float sx = gx + (lampI * 2 + half + 0.5f) / 6f;
                            foreach ((float A, float B) sp in spans)
                                if (sx >= sp.A && sx < sp.B) { lamp[gx - gx0 + 1, lampI]++; break; }
                        }

                // Flatten this row's lamps into one linear array indexed by lamp number
                // (3 per pixel), so a 5-lamp window can be taken across pixel boundaries.
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    int i = gx - gx0 + 1;
                    int col = PenX + gx;
                    if (col < 0 || col >= W) continue;
                    int o = (row * W + col) * 4;
                    // DIB is BGRA; leftmost lamp (RED) is byte 2, mid byte 1, right byte 0.
                    int[] outByte = { px[o + 2], px[o + 1], px[o] };
                    for (int L = 0; L < 3; L++)
                    {
                        // the five consecutive lamps centred on this one
                        int[] w = new int[5];
                        for (int t = -2; t <= 2; t++)
                        {
                            int lampPos = L + t;               // -2..4 within the pixel window
                            int pi = i, li = lampPos;
                            while (li < 0) { pi--; li += 3; }
                            while (li > 2) { pi++; li -= 3; }
                            w[t + 2] = (pi >= 0 && pi < lamp.GetLength(0)) ? lamp[pi, li] : 0;
                        }
                        int idx = 81 * w[0] + 27 * w[1] + 9 * w[2] + 3 * w[3] + w[4];
                        sb.Append(tag).Append(' ').Append(idx).Append(' ').Append(outByte[L]).Append((char)10);
                    }
                }
            }
            File.AppendAllText(outFile, sb.ToString());
        }

        private static List<List<Vector2>> FlattenAll(List<PathFigure> figures)
        {
            var polys = new List<List<Vector2>>();
            foreach (PathFigure f in figures)
            {
                var poly = new List<Vector2> { f.Start };
                Vector2 cur = f.Start;
                foreach (PathSegment sg in f.Segments)
                {
                    if (sg is LineSegment ls) { poly.Add(ls.Point); cur = ls.Point; }
                    else if (sg is QuadraticBezierSegment qs)
                    {
                        for (int k = 1; k <= 16; k++)
                        {
                            float t = k / 16f, u = 1 - t;
                            poly.Add(u * u * cur + 2 * u * t * qs.Control + t * t * qs.Point);
                        }
                        cur = qs.Point;
                    }
                    else if (sg is CubicBezierSegment cs)
                    {
                        for (int k = 1; k <= 16; k++)
                        {
                            float t = k / 16f, u = 1 - t;
                            poly.Add(u * u * u * cur + 3 * u * u * t * cs.Control1
                                     + 3 * u * t * t * cs.Control2 + t * t * t * cs.Point);
                        }
                        cur = cs.Point;
                    }
                }
                if (poly.Count > 2) polys.Add(poly);
            }
            return polys;
        }

        private static void Spans(List<List<Vector2>> polys, float sy, List<(float, float)> spans)
        {
            spans.Clear();
            var cross = new List<(float X, int Dir)>();
            foreach (var poly in polys)
                for (int i = 0; i < poly.Count; i++)
                {
                    Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                    if (a.Y == b.Y) continue;
                    float lo = MathF.Min(a.Y, b.Y), hi = MathF.Max(a.Y, b.Y);
                    if (sy < lo || sy >= hi) continue;
                    float t = (sy - a.Y) / (b.Y - a.Y);
                    cross.Add((a.X + t * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                }
            cross.Sort(static (u, v) => u.X.CompareTo(v.X));
            int w = 0;
            for (int i = 0; i < cross.Count - 1; i++)
            {
                w += cross[i].Dir;
                if (w != 0) spans.Add((cross[i].X, cross[i + 1].X));
            }
        }

        private static List<PathFigure>? s_fittedForLearn;

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
            // RGB ONLY. The fourth byte is the ALPHA the compositor blends with -- the average
            // of the three lamps -- so scanning it mixes two different quantities and invents
            // levels that no lamp ever holds. (It cost me a wrong conclusion: with alpha in,
            // the list showed 28 and 198, which are off the ladder three-level lamps through a
            // three-tap box can produce, and looked like a broken quantiser.)
            for (int i = 0; i + 3 < m.Rgba.Length; i += 4)
            { seen.Add(m.Rgba[i]); seen.Add(m.Rgba[i + 1]); seen.Add(m.Rgba[i + 2]); }
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

            // ...and the same lamps put INTO GDI's space, so the two can actually be diffed. For
            // black ink on white paper the shipped curve is a gamma-space blend, alpha =
            // 1 - (1 - c)^(1/g), which leaves the paper-relative pixel at 255 * (1 - c)^(1/g).
            float g = float.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_GAMMA"),
                                     NumberStyles.Float, CultureInfo.InvariantCulture, out float gg)
                ? gg : 1.20f;
            var mapped = new SortedSet<int>();
            foreach (int v in seen)
                mapped.Add((int) MathF.Round(255f * MathF.Pow(1f - v / 255f, 1f / g)));
            Console.WriteLine("     through the curve (gamma " + g.ToString("0.00", CultureInfo.InvariantCulture)
                + "), as pixels: " + string.Join(" ", mapped));
            Console.WriteLine("     GDI's final levels:                    0 58 102 144 182 219 255");

            // OUR LAMP GRID, mapped into GDI's space and labelled with the SAME absolute pixel
            // column as the GDI dump above (which puts the pen at 8), so the two line up and can
            // be subtracted by eye. Without this the lamps could only be compared as a value SET,
            // which hides where they differ.
            Console.WriteLine("     per-column lamp triples (LEFT MID RIGHT), ours, ink rows only:");
            for (int y = 0; y < m.Height; y++)
            {
                var row = new StringBuilder();
                bool ink = false;
                for (int x = 0; x < m.Width; x++)
                {
                    int i = (y * m.Width + x) * 4;
                    if (i + 2 >= m.Rgba.Length) continue;
                    int r = (int) MathF.Round(255f * MathF.Pow(1f - m.Rgba[i] / 255f, 1f / g));
                    int gg2 = (int) MathF.Round(255f * MathF.Pow(1f - m.Rgba[i + 1] / 255f, 1f / g));
                    int b = (int) MathF.Round(255f * MathF.Pow(1f - m.Rgba[i + 2] / 255f, 1f / g));
                    if (r != 255 || gg2 != 255 || b != 255) ink = true;
                    row.Append($" x{8 + m.OriginX + x,2}:{r,3},{gg2,3},{b,3}");
                }
                if (ink) Console.WriteLine($"     row{y,3}{row}");
            }
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

            if (Environment.GetEnvironmentVariable("WPF_GDIPIPE_LEARN") is { Length: > 0 } learnFile
                && s_fittedForLearn is not null)
                LearnPairs(s_fittedForLearn, px, W, H, learnFile,
                           $"{family.Replace(' ', '_')}{(bold ? "B" : "")}{(italic ? "I" : "")}_{ch}_{ppem}");

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
